using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace DeviceBattery
{
    internal sealed class BluetoothDeviceInfo
    {
        public string DeviceId;
        public string Name;
        public ulong Address;
        public bool Connected;
        public TransportKind Transport = TransportKind.BluetoothLe;
        public DeviceCategory Category = DeviceCategory.Headset;
        public bool AudioDevice;
        public string AddressHash;

        public string StableId
        {
            get
            {
                if (Address != 0)
                {
                    return "bt:" + Address.ToString("X12", CultureInfo.InvariantCulture);
                }
                return "bt:" + Log.HashAddress(0);
            }
        }
    }

    internal sealed class BluetoothDiscoveryResult
    {
        public bool RadioKnown;
        public bool RadioOn;
        public string Error;
        public List<BluetoothDeviceInfo> Devices = new List<BluetoothDeviceInfo>();
    }

    internal sealed class BluetoothBatterySource
    {
        private readonly AirohaBatterySource _airoha = new AirohaBatterySource();
        private readonly AirohaCapabilityCache _airohaCache = new AirohaCapabilityCache();

        private static readonly Guid BatteryServiceUuid = new Guid("0000180F-0000-1000-8000-00805F9B34FB");
        private static readonly Guid BatteryLevelUuid = new Guid("00002A19-0000-1000-8000-00805F9B34FB");
        private static readonly Guid UserDescriptionUuid = new Guid("00002901-0000-1000-8000-00805F9B34FB");
        private static readonly Guid HidServiceUuid = new Guid("00001812-0000-1000-8000-00805F9B34FB");

        private static readonly string[] HeadsetKeywords = new string[]
        {
            "zg2", "zhu", "玄武", "竹林鸟", "headset", "headphone", "earbud", "earphone",
            "airpods", "buds", "freebuds", "wh-", "wf-", "ch-", "soundcore", "jbl",
            "bose", "sony", "edifier", "漫步者", "耳机", "耳麦", "arctis", "hyperx",
            "steelseries", "g435", "g733", "razer"
        };

        private static readonly string[] NotHeadsetKeywords = new string[]
        {
            "mouse", "keyboard", "鼠标", "键盘", "trackpad", "触摸板"
        };

        private static readonly string[] MouseKeywords = new string[]
        {
            "mouse", "鼠标", "mx master", "mx anywhere", "g304", "g502", "g pro",
            "viper", "deathadder", "basilisk", "orochi", "m720", "m590", "lift"
        };

        private static readonly string[] KeyboardKeywords = new string[]
        {
            "keyboard", "键盘", "kbd", "keychron", "wooting", "nuphy", "mx keys",
            "k380", "k580", "k850", "60he", "68he", "75he", "win60", "win68",
            "aula", "狼蛛", "akko", "ducky", "leopold", "hhkb", "realforce",
            "filco", "varmilo", "iqunix", "royal kludge"
        };

        public BluetoothDiscoveryResult Discover()
        {
            BluetoothDiscoveryResult result = new BluetoothDiscoveryResult();
            try
            {
                BluetoothAdapter adapter = Wait(BluetoothAdapter.GetDefaultAsync(), 3500, "获取蓝牙适配器");
                if (adapter == null)
                {
                    result.RadioKnown = true;
                    result.RadioOn = false;
                    result.Error = "未找到蓝牙适配器";
                    return result;
                }

                try
                {
                    Radio radio = Wait(adapter.GetRadioAsync(), 2500, "读取蓝牙无线电状态");
                    result.RadioKnown = true;
                    result.RadioOn = radio != null && radio.State == RadioState.On;
                    if (radio != null && radio.State != RadioState.On)
                    {
                        result.Error = "蓝牙已关闭";
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    // 某些系统上取不到 Radio 状态，但设备枚举仍可用，继续尝试。
                    Log.Warn("读取蓝牙无线电状态失败：" + ShortError(ex));
                    result.RadioKnown = true;
                    result.RadioOn = true;
                }

                Dictionary<ulong, BluetoothDeviceInfo> map = new Dictionary<ulong, BluetoothDeviceInfo>();

                try
                {
                    string classicSelector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
                    DeviceInformationCollection classic = Wait(DeviceInformation.FindAllAsync(classicSelector), 4500, "枚举经典蓝牙设备");
                    foreach (DeviceInformation info in classic)
                    {
                        try
                        {
                            BluetoothDevice device = Wait(BluetoothDevice.FromIdAsync(info.Id), 2500, "打开经典蓝牙设备");
                            if (device == null)
                            {
                                continue;
                            }
                            try
                            {
                                string name = FirstNonEmpty(device.Name, info.Name);
                                bool audio = IsAudioClass(device);
                                if (!audio && !LooksLikeHeadset(name))
                                {
                                    continue;
                                }

                                BluetoothDeviceInfo candidate = new BluetoothDeviceInfo();
                                candidate.DeviceId = info.Id;
                                candidate.Name = string.IsNullOrEmpty(name) ? "蓝牙耳机" : name;
                                candidate.Address = device.BluetoothAddress;
                                candidate.Connected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
                                candidate.Transport = TransportKind.BluetoothClassic;
                                candidate.AudioDevice = audio;
                                candidate.AddressHash = Log.HashAddress(candidate.Address);
                                AddCandidate(map, candidate, false);
                            }
                            finally
                            {
                                device.Dispose();
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Debug("经典蓝牙设备枚举跳过：" + ShortError(ex));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("经典蓝牙枚举失败：" + ShortError(ex));
                }

                try
                {
                    // 枚举全部 BLE 记录，既保留历史记录，也能发现刚配对/刚连接的设备；
                    // 是否显示必须看 ConnectionStatus，不能把“Windows 有这条记录”当成已连接。
                    string bleSelector = BluetoothLEDevice.GetDeviceSelector();
                    DeviceInformationCollection ble = Wait(DeviceInformation.FindAllAsync(bleSelector), 4500, "枚举蓝牙 LE 设备");
                    foreach (DeviceInformation info in ble)
                    {
                        try
                        {
                            string name = info.Name;
                            bool headset = LooksLikeHeadset(name);
                            bool keyboard = !headset && LooksLikeKeyboard(name);
                            bool mouse = !headset && !keyboard && LooksLikeMouse(name);
                            if (!headset && !keyboard && !mouse)
                            {
                                continue;
                            }

                            BluetoothLEDevice device = Wait(BluetoothLEDevice.FromIdAsync(info.Id), 2500, "打开蓝牙 LE 设备");
                            if (device == null)
                            {
                                continue;
                            }
                            try
                            {
                                BluetoothDeviceInfo candidate = new BluetoothDeviceInfo();
                                candidate.DeviceId = info.Id;
                                candidate.Name = string.IsNullOrEmpty(name)
                                    ? (keyboard ? "蓝牙键盘" : (mouse ? "蓝牙鼠标" : "蓝牙耳机"))
                                    : name;
                                candidate.Address = device.BluetoothAddress;
                                // BLE GATT 可以按需连接，但“已断连”的设备不应继续出现在托盘/面板。
                                candidate.Connected =
                                    device.ConnectionStatus == BluetoothConnectionStatus.Connected;
                                candidate.Transport = TransportKind.BluetoothLe;
                                candidate.Category = keyboard ? DeviceCategory.Keyboard :
                                    (mouse ? DeviceCategory.Mouse : DeviceCategory.Headset);
                                candidate.AudioDevice = headset;
                                candidate.AddressHash = Log.HashAddress(candidate.Address);
                                AddCandidate(map, candidate, true);
                            }
                            finally
                            {
                                device.Dispose();
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Debug("蓝牙 LE 设备枚举跳过：" + ShortError(ex));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("蓝牙 LE 枚举失败：" + ShortError(ex));
                }

                // 有些键盘的蓝牙名称只有型号（例如 K380、Keychron K3），名称里没有
                // “keyboard”。通过 HID over GATT 服务（0x1812）反查已连接的 BLE HID
                // 设备，可覆盖这类品牌/型号，而不需要维护 VID/PID 白名单。
                try
                {
                    string hidSelector = GattDeviceService.GetDeviceSelectorFromUuid(HidServiceUuid);
                    DeviceInformationCollection hidDevices = Wait(
                        DeviceInformation.FindAllAsync(hidSelector), 3500, "枚举蓝牙 HID 设备");
                    foreach (DeviceInformation info in hidDevices)
                    {
                        try
                        {
                            BluetoothLEDevice device = Wait(
                                BluetoothLEDevice.FromIdAsync(info.Id), 2000, "打开蓝牙 HID 设备");
                            if (device == null)
                            {
                                continue;
                            }
                            try
                            {
                                if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                                {
                                    continue;
                                }
                                string name = FirstNonEmpty(device.Name, info.Name);
                                if (LooksLikeHeadset(name))
                                {
                                    continue;
                                }
                                bool mouse = LooksLikeMouse(name);
                                BluetoothDeviceInfo candidate = new BluetoothDeviceInfo();
                                candidate.DeviceId = info.Id;
                                candidate.Name = string.IsNullOrEmpty(name)
                                    ? (mouse ? "蓝牙鼠标" : "蓝牙键盘")
                                    : name;
                                candidate.Address = device.BluetoothAddress;
                                candidate.Connected = true;
                                candidate.Transport = TransportKind.BluetoothLe;
                                candidate.Category = mouse ? DeviceCategory.Mouse : DeviceCategory.Keyboard;
                                candidate.AddressHash = Log.HashAddress(candidate.Address);
                                AddCandidate(map, candidate, true);
                            }
                            finally
                            {
                                device.Dispose();
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Debug("蓝牙 HID 设备枚举跳过：" + ShortError(ex));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("蓝牙 HID 枚举失败：" + ShortError(ex));
                }

                foreach (KeyValuePair<ulong, BluetoothDeviceInfo> pair in map)
                {
                    result.Devices.Add(pair.Value);
                }
                result.Devices.Sort(delegate(BluetoothDeviceInfo a, BluetoothDeviceInfo b)
                {
                    return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                });
            }
            catch (Exception ex)
            {
                result.Error = ShortError(ex);
                Log.Warn("蓝牙设备发现失败：" + result.Error);
            }
            return result;
        }

        public BatteryReading Read(DeviceState device, int timeoutMs)
        {
            BatteryReading reading = new BatteryReading();
            reading.Source = BatterySourceKind.BleGatt;
            reading.Timestamp = DateTime.Now;
            bool airohaAttempted = false;

            if (device == null)
            {
                reading.Error = "设备为空";
                return reading;
            }

            if (device.Transport == TransportKind.BluetoothClassic)
            {
                reading.DeviceFound = true;
                reading.Error = "经典蓝牙（HFP）电量暂不可读，请改用蓝牙 LE 或厂商驱动";
                return reading;
            }

            BluetoothLEDevice ble = null;
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            try
            {
                // 优先按地址重新打开，避免复用枚举阶段的旧 GATT 会话导致 180F 读取超时。
                if (device.BluetoothAddress != 0)
                {
                    ble = Wait(BluetoothLEDevice.FromBluetoothAddressAsync(device.BluetoothAddress),
                        Remaining(deadline, timeoutMs), "按地址连接蓝牙设备");
                }
                if (ble == null && !string.IsNullOrEmpty(device.BluetoothDeviceId))
                {
                    ble = Wait(BluetoothLEDevice.FromIdAsync(device.BluetoothDeviceId),
                        Remaining(deadline, timeoutMs), "连接蓝牙设备");
                }
                if (ble == null)
                {
                    reading.Error = "蓝牙设备当前不可用";
                    return reading;
                }

                reading.DeviceFound = true;

                // REDMI/Airoha 耳机的厂商服务能直接给左右耳；优先它，避免标准 180F 在
                // 部分 Windows 蓝牙栈上等待 5 秒以上。
                if (device.Category == DeviceCategory.Headset)
                {
                    airohaAttempted = true;
                    TryAiroha(ble, device, deadline, timeoutMs, reading);
                    if (reading.HasPercent || reading.LeftPercent.HasValue ||
                        reading.RightPercent.HasValue || reading.CasePercent.HasValue)
                    {
                        return reading;
                    }
                }

                GattDeviceServicesResult services = Wait(
                    ble.GetGattServicesForUuidAsync(BatteryServiceUuid, BluetoothCacheMode.Uncached),
                    Remaining(deadline, timeoutMs),
                    "读取电量服务");
                if (services == null || services.Status != GattCommunicationStatus.Success ||
                    services.Services == null || services.Services.Count == 0)
                {
                    if (!airohaAttempted)
                    {
                        airohaAttempted = true;
                        TryAiroha(ble, device, deadline, timeoutMs, reading);
                    }
                    if (reading.HasPercent || reading.LeftPercent.HasValue ||
                        reading.RightPercent.HasValue || reading.CasePercent.HasValue)
                    {
                        return reading;
                    }
                    reading.Error = services == null || services.Status != GattCommunicationStatus.Success
                        ? "无法读取电量服务：" + (services == null ? "无响应" : services.Status.ToString())
                        : "设备未暴露标准电量服务（0x180F）";
                    return reading;
                }

                List<int> values = new List<int>();
                List<string> labels = new List<string>();
                foreach (GattDeviceService service in services.Services)
                {
                    try
                    {
                        GattCharacteristicsResult characteristics = Wait(
                            service.GetCharacteristicsForUuidAsync(BatteryLevelUuid, BluetoothCacheMode.Uncached),
                            Remaining(deadline, timeoutMs),
                            "读取电量特征");
                        if (characteristics == null ||
                            characteristics.Status != GattCommunicationStatus.Success ||
                            characteristics.Characteristics == null)
                        {
                            continue;
                        }

                        foreach (GattCharacteristic characteristic in characteristics.Characteristics)
                        {
                            try
                            {
                                GattReadResult value = Wait(
                                    characteristic.ReadValueAsync(BluetoothCacheMode.Uncached),
                                    Remaining(deadline, timeoutMs),
                                    "读取电量值");
                                if (value == null || value.Status != GattCommunicationStatus.Success ||
                                    value.Value == null || value.Value.Length < 1)
                                {
                                    continue;
                                }

                                DataReader reader = DataReader.FromBuffer(value.Value);
                                byte level = reader.ReadByte();
                                if (level <= 100)
                                {
                                    // 描述只用来区分左右耳/充电盒，读不到不该拖累这次读数。
                                    // 先单独取（失败记 null）再和电量一起入表，保证 values 与 labels
                                    // 下标始终对齐：老写法是 values.Add 之后直接 labels.Add(ReadUserDescription(...))，
                                    // 描述一旦超时抛出，labels 就比 values 少一项，后面 labels[0] 会错位甚至越界。
                                    string label = null;
                                    try
                                    {
                                        label = ReadUserDescription(characteristic, deadline);
                                    }
                                    catch (Exception ex)
                                    {
                                        Log.Debug("读取电量特征描述失败：" + ShortError(ex));
                                    }
                                    values.Add(level);
                                    labels.Add(label);
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Debug("读取电量特征失败：" + ShortError(ex));
                            }
                        }
                    }
                    finally
                    {
                        service.Dispose();
                    }
                }

                if (values.Count == 0)
                {
                    if (!airohaAttempted)
                    {
                        airohaAttempted = true;
                        TryAiroha(ble, device, deadline, timeoutMs, reading);
                    }
                    if (reading.HasPercent || reading.LeftPercent.HasValue ||
                        reading.RightPercent.HasValue || reading.CasePercent.HasValue)
                    {
                        return reading;
                    }
                    reading.Error = "电量特征存在，但没有读到有效值";
                    return reading;
                }

                reading.Percent = values[0];
                reading.Confidence = ReadingConfidence.High;
                reading.Source = BatterySourceKind.BleGatt;
                reading.Charging = null;
                reading.Raw = string.Join(",", values.ConvertAll<string>(delegate(int v) { return v.ToString(CultureInfo.InvariantCulture); }).ToArray());
                reading.Timestamp = DateTime.Now;

                if (values.Count == 1)
                {
                    // 只有一个电量值时，不猜测左右耳。
                    if (LooksLikeCase(labels[0]))
                    {
                        reading.CasePercent = values[0];
                        reading.Percent = null;
                        reading.Error = "只读到充电盒电量，未读到耳机本机电量";
                    }
                }
                else
                {
                    reading.LeftPercent = values[0];
                    reading.RightPercent = values[1];
                    reading.Percent = Math.Min(values[0], values[1]);
                    if (values.Count >= 3)
                    {
                        reading.CasePercent = values[2];
                    }

                    // 如果描述符明确标了左右，按描述符纠正顺序。
                    int leftIndex = IndexOfLabel(labels, "left", "左");
                    int rightIndex = IndexOfLabel(labels, "right", "右");
                    if (leftIndex >= 0 && rightIndex >= 0 && leftIndex != rightIndex &&
                        leftIndex < values.Count && rightIndex < values.Count)
                    {
                        reading.LeftPercent = values[leftIndex];
                        reading.RightPercent = values[rightIndex];
                        reading.Percent = Math.Min(reading.LeftPercent.Value, reading.RightPercent.Value);
                    }
                }

                // 标准 GATT 只有一个总值时，再尝试 Airoha 厂商服务拿左右耳/充电盒。
                if (!airohaAttempted &&
                    (!reading.LeftPercent.HasValue || !reading.RightPercent.HasValue))
                {
                    airohaAttempted = true;
                    TryAiroha(ble, device, deadline, timeoutMs, reading);
                }
                return reading;
            }
            catch (TimeoutException)
            {
                reading.Error = "蓝牙读取超时（设备可能被其它连接占用）";
                return reading;
            }
            catch (Exception ex)
            {
                reading.Error = "蓝牙读取失败：" + ShortError(ex);
                return reading;
            }
            finally
            {
                if (ble != null)
                {
                    try { ble.Dispose(); } catch (Exception) { }
                }
            }
        }

        private bool TryAiroha(BluetoothLEDevice ble, DeviceState device, DateTime deadline, int timeoutMs,
            BatteryReading reading)
        {
            if (ble == null || device == null || reading == null || device.Category != DeviceCategory.Headset)
            {
                return false;
            }
            int remaining = Remaining(deadline, timeoutMs);
            if (remaining < 300)
            {
                return false;
            }
            // 厂商服务探测单独限时，避免服务发现卡住时把整次 BLE 查询耗光，
            // 导致本来可用的标准 0x180F 也来不及读取。5 秒覆盖
            // “服务发现 + 订阅通知 + 左右耳两次 selector”的常见冷启动。
            remaining = Math.Min(remaining, 5000);

            string cacheKey = !string.IsNullOrEmpty(device.Id)
                ? device.Id
                : "bt:" + device.BluetoothAddress.ToString("X12", CultureInfo.InvariantCulture);
            if (!_airohaCache.ShouldTry(cacheKey, DateTime.Now))
            {
                Log.Debug("Airoha 能力缓存：暂时跳过 " + cacheKey);
                return false;
            }

            bool airohaMissing;
            bool success = _airoha.TryRead(ble, remaining, reading, out airohaMissing);
            AirohaReadOutcome outcome = success
                ? AirohaReadOutcome.Success
                : (airohaMissing ? AirohaReadOutcome.NotSupported : AirohaReadOutcome.Failed);
            _airohaCache.Record(cacheKey, outcome, DateTime.Now);

            if (success)
            {
                reading.DeviceFound = true;
                Log.Debug("Airoha 厂商电量读取成功：" + reading.Raw);
                return true;
            }
            Log.Debug("Airoha 厂商电量读取跳过/失败：" + outcome + " " + cacheKey);
            return false;
        }

        private static string ReadUserDescription(GattCharacteristic characteristic, DateTime deadline)
        {
            try
            {
                GattDescriptorsResult result = Wait(
                    characteristic.GetDescriptorsForUuidAsync(UserDescriptionUuid, BluetoothCacheMode.Uncached),
                    Remaining(deadline, 1500),
                    "读取电量描述");
                if (result == null || result.Status != GattCommunicationStatus.Success ||
                    result.Descriptors == null || result.Descriptors.Count == 0)
                {
                    return null;
                }
                GattDescriptor descriptor = result.Descriptors[0];
                GattReadResult value = Wait(descriptor.ReadValueAsync(BluetoothCacheMode.Uncached),
                    Remaining(deadline, 1500), "读取电量描述值");
                if (value == null || value.Status != GattCommunicationStatus.Success ||
                    value.Value == null || value.Value.Length == 0)
                {
                    return null;
                }
                DataReader reader = DataReader.FromBuffer(value.Value);
                byte[] bytes = new byte[value.Value.Length];
                reader.ReadBytes(bytes);
                return Encoding.UTF8.GetString(bytes).Trim('\0', ' ');
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int IndexOfLabel(List<string> labels, string english, string chinese)
        {
            for (int i = 0; i < labels.Count; i++)
            {
                string label = labels[i];
                if (string.IsNullOrEmpty(label))
                {
                    continue;
                }
                if (label.IndexOf(english, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    label.IndexOf(chinese, StringComparison.Ordinal) >= 0)
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool LooksLikeCase(string label)
        {
            if (string.IsNullOrEmpty(label))
            {
                return false;
            }
            return label.IndexOf("case", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   label.IndexOf("盒", StringComparison.Ordinal) >= 0;
        }

        private static void AddCandidate(Dictionary<ulong, BluetoothDeviceInfo> map, BluetoothDeviceInfo candidate, bool prefer)
        {
            if (candidate.Address == 0)
            {
                return;
            }
            BluetoothDeviceInfo existing;
            if (!map.TryGetValue(candidate.Address, out existing))
            {
                map[candidate.Address] = candidate;
                return;
            }
            // 经典音频链路已连接、BLE GATT 暂时断开时，不要用一条“未连接”的 BLE 记录
            // 覆盖已连接的经典记录；反过来 BLE 真正连上时仍优先 BLE（可读电量）。
            if (prefer && existing.Transport != TransportKind.BluetoothLe &&
                (candidate.Connected || !existing.Connected))
            {
                map[candidate.Address] = candidate;
            }
        }

        private static bool IsAudioClass(BluetoothDevice device)
        {
            try
            {
                // BluetoothMajorClass.AudioVideo = 4
                return Convert.ToInt32(device.ClassOfDevice.MajorClass, CultureInfo.InvariantCulture) == 4;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool LooksLikeHeadset(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            string lower = name.ToLowerInvariant();
            for (int i = 0; i < NotHeadsetKeywords.Length; i++)
            {
                if (lower.IndexOf(NotHeadsetKeywords[i], StringComparison.Ordinal) >= 0)
                {
                    return false;
                }
            }
            for (int i = 0; i < HeadsetKeywords.Length; i++)
            {
                if (lower.IndexOf(HeadsetKeywords[i], StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool LooksLikeKeyboard(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            string lower = name.ToLowerInvariant();
            for (int i = 0; i < KeyboardKeywords.Length; i++)
            {
                if (lower.IndexOf(KeyboardKeywords[i], StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool LooksLikeMouse(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            string lower = name.ToLowerInvariant();
            for (int i = 0; i < MouseKeywords.Length; i++)
            {
                if (lower.IndexOf(MouseKeywords[i], StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static string FirstNonEmpty(string first, string second)
        {
            return string.IsNullOrEmpty(first) ? second : first;
        }

        private static string ShortError(Exception ex)
        {
            if (ex == null)
            {
                return "未知错误";
            }
            if (ex is AggregateException && ex.InnerException != null)
            {
                return ex.InnerException.GetType().Name + ": " + ex.InnerException.Message;
            }
            return ex.GetType().Name + ": " + ex.Message;
        }

        private static int Remaining(DateTime deadline, int max)
        {
            int milliseconds = (int)(deadline - DateTime.Now).TotalMilliseconds;
            if (milliseconds <= 0)
            {
                throw new TimeoutException("蓝牙读取总超时");
            }
            return Math.Min(milliseconds, max);
        }

        private static T Wait<T>(IAsyncOperation<T> operation, int timeoutMs, string what)
        {
            if (operation == null)
            {
                throw new InvalidOperationException(what + "：操作为空");
            }
            Task<T> task = operation.AsTask();
            if (!task.Wait(timeoutMs))
            {
                // 超时只是 Wait 放弃等待，WinRT 操作本身还在后台跑，仍持有 GATT 服务/特征的引用，
                // 而调用方的 finally 已经把服务 Dispose 了。不 Cancel 就会攒下孤儿操作，
                // 表现为蓝牙读得越来越慢、偶发 GattCommunicationStatus 异常。
                try
                {
                    operation.Cancel();
                }
                catch (Exception ex)
                {
                    Log.Debug("取消蓝牙操作失败（" + what + "）：" + ShortError(ex));
                }
                throw new TimeoutException(what + " 超时");
            }
            return task.Result;
        }
    }

    internal sealed class BluetoothWatcher : IDisposable
    {
        private DeviceWatcher _classic;
        private DeviceWatcher _ble;
        private Action _onChanged;
        private int _lastSignal;

        public void Start(Action onChanged)
        {
            _onChanged = onChanged;
            try
            {
                _classic = DeviceInformation.CreateWatcher(BluetoothDevice.GetDeviceSelector());
                Hook(_classic);
                _classic.Start();
            }
            catch (Exception ex)
            {
                Log.Warn("启动经典蓝牙监听失败：" + ex.Message);
            }

            try
            {
                _ble = DeviceInformation.CreateWatcher(BluetoothLEDevice.GetDeviceSelector());
                Hook(_ble);
                _ble.Start();
            }
            catch (Exception ex)
            {
                Log.Warn("启动蓝牙 LE 监听失败：" + ex.Message);
            }
        }

        private void Hook(DeviceWatcher watcher)
        {
            watcher.Added += delegate(DeviceWatcher sender, DeviceInformation info) { Signal(); };
            watcher.Updated += delegate(DeviceWatcher sender, DeviceInformationUpdate update) { Signal(); };
            watcher.Removed += delegate(DeviceWatcher sender, DeviceInformationUpdate update) { Signal(); };
            watcher.EnumerationCompleted += delegate(DeviceWatcher sender, object args) { Signal(); };
        }

        private void Signal()
        {
            int now = Environment.TickCount;
            if (unchecked(now - _lastSignal) < 800)
            {
                return;
            }
            _lastSignal = now;
            Action handler = _onChanged;
            if (handler != null)
            {
                try { handler(); } catch (Exception) { }
            }
        }

        public void Dispose()
        {
            try { if (_classic != null) _classic.Stop(); } catch (Exception) { }
            try { if (_ble != null) _ble.Stop(); } catch (Exception) { }
            _classic = null;
            _ble = null;
        }
    }
}
