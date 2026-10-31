using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace DeviceBattery
{
    internal sealed class BatteryService : IDisposable
    {
        private readonly object _gate = new object();

        /// <summary>蓝牙读的起始超时（毫秒）；读不到时按设备逐次减半，见 <see cref="BleTimeoutFor"/>。</summary>
        private const int BleReadTimeoutMs = 9000;
        private const int MinBleReadTimeoutMs = 2000;
        private readonly Dictionary<string, int> _bleTimeoutMs = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<DeviceState> _devices = new List<DeviceState>();
        private readonly AtkMouseSource _atk = new AtkMouseSource();
        private readonly BluetoothBatterySource _bluetooth = new BluetoothBatterySource();
        private readonly GenericHidBatterySource _genericHid = new GenericHidBatterySource();
        private readonly LogitechHidppSource _hidpp = new LogitechHidppSource();
        // 各品牌的私有 HID 协议（雷蛇、赛睿、WLmouse/Lamzu/G-Wolves、MCHOSE、Pulsar/VXE）。
        private readonly VendorHidBatterySource _vendor = new VendorHidBatterySource();
        private readonly BluetoothWatcher _watcher = new BluetoothWatcher();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);

        private Thread _worker;
        private volatile bool _stopping;
        private readonly ManualResetEvent _workerExited = new ManualResetEvent(false);
        private volatile bool _manualRefresh;
        private volatile bool _discoveryRequested;
        private volatile bool _panelOpen;
        private volatile bool _queryConnectedNow;
        private volatile bool _queryPanelNow;
        private bool _startedByAutostart;
        private DateTime _lastDiscovery = DateTime.MinValue;
        private DateTime _lastPanelRefresh = DateTime.MinValue;
        private bool _bluetoothRadioOn;
        private bool _bluetoothRadioKnown;
        private string _bluetoothError;
        private HidDeviceInfo _atkInfo;
        private List<HidBatteryDevice> _genericHidInfos = new List<HidBatteryDevice>();
        private List<HidppDevice> _hidppInfos = new List<HidppDevice>();
        private List<VendorDevice> _vendorDevices = new List<VendorDevice>();
        private bool _disposed;
        private bool _hasDiscovered;

        public event EventHandler Changed;
        public event EventHandler<LowBatteryEventArgs> LowBattery;

        public bool BluetoothRadioOn
        {
            get { lock (_gate) { return _bluetoothRadioOn; } }
        }

        public bool BluetoothRadioKnown
        {
            get { lock (_gate) { return _bluetoothRadioKnown; } }
        }

        public string BluetoothError
        {
            get { lock (_gate) { return _bluetoothError; } }
        }

        public bool HasDiscovered
        {
            get { lock (_gate) { return _hasDiscovered; } }
        }

        public void Start(bool startedByAutostart)
        {
            _startedByAutostart = startedByAutostart;
            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Name = "BatteryService";
            _worker.Start();

            try
            {
                _watcher.Start(delegate { RequestDiscovery(); });
            }
            catch (Exception ex)
            {
                Log.Warn("蓝牙设备监听启动失败：" + ex.Message);
            }

            // 不要等首个 5 秒轮询周期：启动后立刻发现设备并读取一次。
            // 登录自启例外：醒来信号会让工作线程的「安静期」提前结束，那样 18 秒延迟白等，
            // 等于跟系统登录时的磁盘/服务 IO 抢时间。安静期结束后线程本来就会自己开工。
            if (!startedByAutostart)
            {
                _wake.Set();
            }
        }

        public void Stop()
        {
            _stopping = true;
            _wake.Set();
            bool workerExited = true;
            try
            {
                if (_worker != null)
                {
                    // 单次蓝牙查询预算可达 9 秒。原来只等 3 秒就继续 Dispose，
                    // 工作线程可能还在这几个句柄里读写，等于跨线程关闭正在使用的
                    // HID 句柄。这里等到线程真正退出再释放；等不到就宁可泄漏。
                    workerExited = _workerExited.WaitOne(15000);
                    _worker = null;
                }
            }
            catch (Exception)
            {
            }
            try { _watcher.Dispose(); } catch (Exception) { }
            if (!workerExited)
            {
                Log.Warn("工作线程未在 15 秒内退出，跳过释放 HID 句柄，避免跨线程关闭");
                return;
            }
            try { _atk.Dispose(); } catch (Exception) { }
        }

        public void RequestRefreshAll()
        {
            _manualRefresh = true;
            _wake.Set();
        }

        public void RequestDiscovery()
        {
            _discoveryRequested = true;
            _wake.Set();
        }

        public void SetPanelOpen(bool open)
        {
            _panelOpen = open;
            if (open)
            {
                // 点托盘图标打开面板时读取一次；2 秒内重复点击不重复查询。
                if ((DateTime.Now - _lastPanelRefresh).TotalSeconds >= 2)
                {
                    _lastPanelRefresh = DateTime.Now;
                    lock (_gate)
                    {
                        for (int i = 0; i < _devices.Count; i++)
                        {
                            DeviceState device = _devices[i];
                            if (device.Status != DeviceStatus.Unsupported &&
                                device.Status != DeviceStatus.Offline &&
                                device.Status != DeviceStatus.BluetoothOff)
                            {
                                device.NextDue = DateTime.MinValue;
                            }
                        }
                    }
                    _queryPanelNow = true;
                }
                _wake.Set();
            }
        }

        public void OnPowerResume()
        {
            lock (_gate)
            {
                for (int i = 0; i < _devices.Count; i++)
                {
                    DeviceState device = _devices[i];
                    if (device.LastReading != null)
                    {
                        // 清空时间戳：面板显示 --:--，不会把睡眠前的时间当成“刚刚更新”。
                        device.LastReading.Timestamp = DateTime.MinValue;
                    }
                    if (device.Status == DeviceStatus.Online || device.Status == DeviceStatus.Charging)
                    {
                        device.Status = DeviceStatus.Stale;
                    }
                    device.NextDue = DateTime.MinValue;
                }
            }
            _manualRefresh = true;
            _discoveryRequested = true;
            _wake.Set();
            RaiseChanged();
        }

        public BatterySnapshot GetSnapshot()
        {
            BatterySnapshot snapshot = new BatterySnapshot();
            lock (_gate)
            {
                RefreshStaleStatuses();
                snapshot.BluetoothRadioOn = _bluetoothRadioOn;
                snapshot.BluetoothRadioKnown = _bluetoothRadioKnown;
                snapshot.BluetoothError = _bluetoothError;
                for (int i = 0; i < _devices.Count; i++)
                {
                    snapshot.Devices.Add(_devices[i].Clone());
                }
            }
            // 用户偏好（强制显示 / 改名 / 阈值）随时会变，而缓存里的设备对象只在发现时
            // 套过一次：克隆完再补一遍，否则刚在面板上「显示」的设备下一次读快照又变回隐藏。
            for (int i = 0; i < snapshot.Devices.Count; i++)
            {
                AppSettings.ApplyDevicePreferences(snapshot.Devices[i]);
            }
            return snapshot;
        }

        public DeviceState FindDevice(string id)
        {
            lock (_gate)
            {
                for (int i = 0; i < _devices.Count; i++)
                {
                    if (string.Equals(_devices[i].Id, id, StringComparison.Ordinal))
                    {
                        return _devices[i].Clone();
                    }
                }
            }
            return null;
        }

        private void WorkerLoop()
        {
            try
            {
                if (_startedByAutostart)
                {
                    // 登录自启：先安静 18 秒，别跟系统登录时的磁盘/服务 IO 抢时间。
                    // 这期间蓝牙监听、外部请求都会唤醒 _wake，但只消耗信号不提前结束安静期
                    //（否则开机就立刻扫描，等于没有延迟）。
                    DateTime until = DateTime.Now.AddSeconds(18);
                    while (!_stopping)
                    {
                        TimeSpan left = until - DateTime.Now;
                        if (left.TotalMilliseconds <= 0)
                        {
                            break;
                        }

                        int wait = (int)Math.Min(500d, Math.Max(50d, left.TotalMilliseconds));
                        _wake.WaitOne(wait);
                    }

                    // 安静期结束后立刻开始第一轮，不再额外等一个 5 秒周期。
                    _wake.Set();
                }

                DateTime nextDiscovery = DateTime.MinValue;
                while (!_stopping)
                {
                    try
                    {
                        bool woke = _wake.WaitOne(5000);
                        if (woke)
                        {
                            // 手动刷新/设备事件会唤醒；发现请求单独标记。
                        }

                        if (_discoveryRequested || DateTime.Now >= nextDiscovery)
                        {
                            // 蓝牙监听会连续发多个事件，3 秒内不重复枚举。
                            if (!_discoveryRequested ||
                                (DateTime.Now - _lastDiscovery).TotalSeconds >= 3 ||
                                DateTime.Now >= nextDiscovery)
                            {
                                _discoveryRequested = false;
                                Discover();
                                nextDiscovery = DateTime.Now.AddSeconds(DiscoveryIntervalSeconds());
                            }
                        }

                        if (_manualRefresh)
                        {
                            // 手动刷新优先，并吸收同一次唤醒里的连接/面板读取，避免重复查询。
                            _manualRefresh = false;
                            _queryConnectedNow = false;
                            _queryPanelNow = false;
                            QueryDue(true);
                        }
                        else if (_queryConnectedNow)
                        {
                            // 设备刚接上时立即读一次，即使当前处于息屏/空闲暂停状态。
                            _queryConnectedNow = false;
                            QueryDue(false);
                        }
                        else if (_queryPanelNow)
                        {
                            _queryPanelNow = false;
                            QueryDue(false);
                        }
                        else if (_panelOpen || ShouldQueryNow())
                        {
                            QueryDue(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        // 单次蓝牙/HID/枚举异常不能让整个后台线程永久退出。
                        if (!_stopping)
                        {
                            Log.Error("BatteryService 本轮查询异常，稍后继续：" + ex.Message);
                            Thread.Sleep(1000);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("BatteryService 工作线程异常：" + ex);
            }
            finally
            {
                try { _workerExited.Set(); } catch (ObjectDisposedException) { }
            }
        }

        /// <summary>周期枚举设备只为兜底；连接/断开事件会立即唤醒。间隔随刷新设置放大，最长 15 分钟。</summary>
        internal static int DiscoveryIntervalSeconds()
        {
            int seconds = AppSettings.RefreshMinutes * 15;
            if (seconds < 60) seconds = 60;
            if (seconds > 900) seconds = 900;
            return seconds;
        }

        private bool ShouldQueryNow()
        {
            if (!AppSettings.Current.PauseWhenIdle)
            {
                return true;
            }
            if (!Native.IsDisplayOn())
            {
                return false;
            }
            return Native.GetIdleSeconds() < 240;
        }

        private void Discover()
        {
            HidDeviceInfo atk = null;
            AtkMouseIdentity atkIdentity = null;
            UsbHeadsetInfo usb = null;
            BluetoothDiscoveryResult bluetooth = null;
            List<HidBatteryDevice> genericHid = null;
            List<HidppDevice> hidpp = null;
            List<VendorDevice> vendors = null;
            List<HidDeviceInfo> keyboards = null;

            try
            {
                // 一轮发现只枚举一次 HID 接口：ATK 接收器、通用 HID、键盘三处共用，
                // 避免对同一批接口重复 CreateFile + HidD_GetPreparsedData。
                List<HidDeviceInfo> hidScan = HidEnumerator.FindAll();
                try
                {
                    atk = _atk.Find(hidScan);
                }
                catch (Exception ex)
                {
                    Log.Warn("ATK 接收器检测失败：" + ex.Message);
                }
                if (atk != null)
                {
                    // 接收器产品名只是“Wireless mouse 8k NANO dongle-L”，
                    // 这里再问一次鼠标本体，拿到真实型号和稳定身份。
                    atkIdentity = _atk.Identify(atk);
                    if (atkIdentity == null)
                    {
                        // 鼠标休眠时查询可能失败，但之前的真实型号记录仍在；
                        // 不能退回接收器哈希 ID，否则会生成重复设备。
                        atkIdentity = AppSettings.FindKnownAtkIdentity(atk.SerialNumber);
                    }
                }

                try
                {
                    genericHid = _genericHid.Find(hidScan);
                }
                catch (Exception ex)
                {
                    Log.Warn("通用 HID 鼠标/键盘检测失败：" + ex.Message);
                }

                try
                {
                    hidpp = _hidpp.Find(hidScan);
                }
                catch (Exception ex)
                {
                    Log.Warn("罗技 HID++ 检测失败：" + ex.Message);
                }

                try
                {
                    vendors = _vendor.Find(hidScan);
                }
                catch (Exception ex)
                {
                    Log.Warn("厂商私有协议检测失败：" + ex.Message);
                }

                try
                {
                    keyboards = HidEnumerator.FindKeyboards(hidScan);
                }
                catch (Exception ex)
                {
                    Log.Warn("键盘检测失败：" + ex.Message);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("HID 枚举失败：" + ex.Message);
            }

            try
            {
                usb = UsbHeadsetDetector.Find();
            }
            catch (Exception ex)
            {
                Log.Warn("2.4G 耳机检测失败：" + ex.Message);
            }

            try
            {
                bluetooth = _bluetooth.Discover();
            }
            catch (Exception ex)
            {
                Log.Warn("蓝牙设备检测失败：" + ex.Message);
                bluetooth = new BluetoothDiscoveryResult();
                bluetooth.Error = ex.Message;
            }

            List<DeviceState> candidates = new List<DeviceState>();
            Dictionary<string, DeviceState> oldMap = new Dictionary<string, DeviceState>();
            lock (_gate)
            {
                for (int i = 0; i < _devices.Count; i++)
                {
                    oldMap[_devices[i].Id] = _devices[i];
                }
                _bluetoothRadioKnown = bluetooth != null && bluetooth.RadioKnown;
                _bluetoothRadioOn = bluetooth != null && bluetooth.RadioOn;
                _bluetoothError = bluetooth == null ? "蓝牙状态未知" : bluetooth.Error;
                _atkInfo = atk;
                _genericHidInfos = genericHid ?? new List<HidBatteryDevice>();
                _hidppInfos = hidpp ?? new List<HidppDevice>();
                _vendorDevices = vendors ?? new List<VendorDevice>();
            }

            DeviceState oldAtk = FindAtkDevice(oldMap);
            bool firstDiscovery = !_hasDiscovered;
            bool identityMigrated = false;
            if (atk != null)
            {
                DeviceState mouse = new DeviceState();
                mouse.Id = atkIdentity == null
                    ? "atk:" + atk.ShortId
                    : atkIdentity.BuildStableId(atk);
                mouse.Category = DeviceCategory.Mouse;
                mouse.Model = atkIdentity == null ? "ATK 鼠标" : atkIdentity.ModelName;
                mouse.DisplayName = mouse.Model;
                mouse.Transport = TransportKind.Atk24G;
                mouse.VendorId = atk.VendorId;
                mouse.ProductId = atk.ProductId;
                mouse.HardwareId = atk.InstanceId;
                mouse.Connected = true;
                mouse.BatterySupported = true;
                mouse.Order = 0;
                mouse.SourceDescription = "厂商 HID";
                mouse.Status = DeviceStatus.Online;

                if (atkIdentity != null)
                {
                    // 升级后 ID 由接收器哈希变为“接收器序列号-CID.MID”。
                    // 先迁移本进程里的旧状态，再迁移持久化设置，避免重复记录。
                    if (oldAtk != null && !string.Equals(oldAtk.Id, mouse.Id, StringComparison.Ordinal))
                    {
                        identityMigrated = AppSettings.MigrateDeviceIdentity(oldAtk.Id, mouse.Id);
                        // 旧 ID 已经迁移成新 ID：必须把旧键从 oldMap 移除，否则后面的
                        // “保留本轮没出现的设备”兜底循环会再补一条永远离线的重复记录，
                        // 并且 RememberDevice 会把刚刚删掉的旧记录重新写回设置。
                        oldMap.Remove(oldAtk.Id);
                    }
                    else
                    {
                        identityMigrated = AppSettings.MigrateLegacyAtkDevice(mouse.Id);
                    }
                    if (oldAtk != null && !oldMap.ContainsKey(mouse.Id))
                    {
                        oldMap[mouse.Id] = oldAtk;
                    }
                }
                candidates.Add(Reuse(oldMap, mouse));
            }
            else
            {
                _atk.Dispose();
            }

            if (genericHid != null)
            {
                for (int i = 0; i < genericHid.Count; i++)
                {
                    HidBatteryDevice item = genericHid[i];
                    if (item == null || item.Info == null)
                    {
                        continue;
                    }
                    // 同一个罗技接收器可能既给 HID++ 电量、又给标准电池页：
                    // 同型号只留 HID++ 那张，避免出现两张同名卡片。
                    if (hidpp != null && MatchesHidppDevice(hidpp, item.Info))
                    {
                        continue;
                    }
                    // 同一台设备已经被厂商协议认领（雷蛇/赛睿等）时，
                    // 不再用通用 HID 出一张重复的卡片。
                    if (MatchesVendorDevice(vendors, item.Info))
                    {
                        continue;
                    }
                    DeviceState device = new DeviceState();
                    device.Id = "hid:" + item.Info.ShortId;
                    device.Category = item.Category;
                    string fallback = item.Category == DeviceCategory.Keyboard ? "USB 键盘" : "USB 鼠标";
                    device.Model = string.IsNullOrEmpty(item.Info.ProductName) ? fallback : item.Info.ProductName;
                    device.DisplayName = device.Model;
                    device.Transport = TransportKind.Usb;
                    device.VendorId = item.Info.VendorId;
                    device.ProductId = item.Info.ProductId;
                    device.HardwareId = item.Info.InstanceId;
                    device.Connected = true;
                    device.BatterySupported = true;
                    device.Order = item.Category == DeviceCategory.Keyboard ? 1 : 0;
                    device.SourceDescription = "通用 HID";
                    device.Status = DeviceStatus.Online;
                    candidates.Add(Reuse(oldMap, device));
                }
            }

            if (hidpp != null)
            {
                for (int i = 0; i < hidpp.Count; i++)
                {
                    HidppDevice item = hidpp[i];
                    if (item == null || item.Info == null)
                    {
                        continue;
                    }
                    DeviceState device = new DeviceState();
                    device.Id = "hidpp:" + item.StableId;
                    device.Category = item.Category;
                    device.Model = string.IsNullOrEmpty(item.Name) ? "罗技无线设备" : item.Name;
                    device.DisplayName = device.Model;
                    device.Transport = TransportKind.Usb;
                    device.VendorId = item.Info.VendorId;
                    device.ProductId = item.Info.ProductId;
                    device.HardwareId = item.Info.InstanceId;
                    device.Connected = true;
                    device.BatterySupported = true;
                    device.Order = item.Category == DeviceCategory.Mouse ? 0 :
                        (item.Category == DeviceCategory.Keyboard ? 1 : 2);
                    device.SourceDescription = "罗技 HID++";
                    device.Status = DeviceStatus.Online;
                    candidates.Add(Reuse(oldMap, device));
                }
            }

            if (vendors != null)
            {
                for (int i = 0; i < vendors.Count; i++)
                {
                    VendorDevice item = vendors[i];
                    if (item == null || item.Info == null)
                    {
                        continue;
                    }
                    DeviceState device = new DeviceState();
                    device.Id = "vendor:" + item.StableId;
                    device.Category = item.Category;
                    device.Model = string.IsNullOrEmpty(item.Name) ? "无线设备" : item.Name;
                    device.DisplayName = device.Model;
                    device.Transport = TransportKind.Usb;
                    device.VendorId = item.Info.VendorId;
                    device.ProductId = item.Info.ProductId;
                    device.HardwareId = item.Info.InstanceId;
                    device.Connected = true;
                    device.BatterySupported = true;
                    device.Order = item.Category == DeviceCategory.Mouse ? 0 :
                        (item.Category == DeviceCategory.Keyboard ? 1 : 2);
                    device.SourceDescription = item.Protocol.Label;
                    device.Status = DeviceStatus.Online;
                    candidates.Add(Reuse(oldMap, device));
                }
            }

            if (keyboards != null)
            {
                HashSet<string> genericKeyboards = new HashSet<string>();
                if (genericHid != null)
                {
                    for (int i = 0; i < genericHid.Count; i++)
                    {
                        HidBatteryDevice item = genericHid[i];
                        if (item != null && item.Info != null &&
                            item.Category == DeviceCategory.Keyboard)
                        {
                            genericKeyboards.Add(item.Info.VendorId + ":" + item.Info.ProductId);
                        }
                    }
                }

                if (vendors != null)
                {
                    for (int i = 0; i < vendors.Count; i++)
                    {
                        VendorDevice item = vendors[i];
                        if (item != null && item.Info != null)
                        {
                            genericKeyboards.Add(item.Info.VendorId + ":" + item.Info.ProductId);
                        }
                    }
                }

                for (int i = 0; i < keyboards.Count; i++)
                {
                    HidDeviceInfo info = keyboards[i];
                    if (info == null ||
                        genericKeyboards.Contains(info.VendorId + ":" + info.ProductId))
                    {
                        // 已经有可读电量的通用 HID 键盘，不再放一张“不支持”的卡片。
                        continue;
                    }
                    DeviceState keyboard = new DeviceState();
                    keyboard.Id = "kbd:" + info.ShortId;
                    keyboard.Category = DeviceCategory.Keyboard;
                    keyboard.Model = string.IsNullOrEmpty(info.ProductName) ? "USB 键盘" : info.ProductName;
                    keyboard.DisplayName = keyboard.Model;
                    keyboard.Transport = TransportKind.Usb;
                    keyboard.VendorId = info.VendorId;
                    keyboard.ProductId = info.ProductId;
                    keyboard.HardwareId = info.InstanceId;
                    keyboard.Connected = true;
                    keyboard.BatterySupported = false;
                    keyboard.Order = 1;
                    keyboard.SourceDescription = "键盘检测";
                    keyboard.Status = DeviceStatus.Unsupported;
                    keyboard.UnsupportedReason = "未检测到标准 HID 电量接口（可能是有线键盘或厂商私有协议）";
                    DeviceState state = Reuse(oldMap, keyboard);
                    // 旧记录若曾经可读，不能把现在的“无电量接口”误显示成在线。
                    state.BatterySupported = false;
                    state.Status = DeviceStatus.Unsupported;
                    state.UnsupportedReason = keyboard.UnsupportedReason;
                    candidates.Add(state);
                }
            }

            bool hasBluetoothHeadset = false;
            if (bluetooth != null)
            {
                for (int i = 0; i < bluetooth.Devices.Count; i++)
                {
                    BluetoothDeviceInfo info = bluetooth.Devices[i];
                    DeviceState headset = new DeviceState();
                    headset.Id = info.StableId;
                    headset.Category = info.Category;
                    headset.Model = info.Name;
                    headset.DisplayName = info.Name;
                    headset.Transport = info.Transport;
                    headset.BluetoothAddress = info.Address;
                    headset.BluetoothDeviceId = info.DeviceId;
                    headset.AddressHash = info.AddressHash;
                    headset.Connected = info.Connected;
                    headset.BatterySupported = info.Transport == TransportKind.BluetoothLe;
                    headset.Order = info.Category == DeviceCategory.Mouse ? 0 :
                        (info.Category == DeviceCategory.Keyboard ? 1 : 2);
                    headset.SourceDescription = info.Transport == TransportKind.BluetoothLe ? "蓝牙 GATT" : "经典蓝牙";
                    headset.Status = info.Transport == TransportKind.BluetoothLe
                        ? DeviceStatus.Online
                        : DeviceStatus.Unsupported;
                    if (info.Category == DeviceCategory.Headset &&
                        info.Transport == TransportKind.BluetoothClassic)
                    {
                        headset.UnsupportedReason = "经典蓝牙 HFP 通道由 Windows 音频独占，应用无法在播放/通话时并行读取；请改用蓝牙 LE 或厂商驱动";
                    }
                    DeviceState state = Reuse(oldMap, headset);
                    if (!state.Connected)
                    {
                        // 蓝牙列表里可能仍有已配对但当前断开的设备：只留历史记录，
                        // 不显示、不主动查询，避免把“连接过”误当成“现在已连接”。
                        state.Status = bluetooth.RadioKnown && !bluetooth.RadioOn
                            ? DeviceStatus.BluetoothOff
                            : DeviceStatus.Offline;
                        state.StatusDetail = state.Status == DeviceStatus.BluetoothOff
                            ? "蓝牙已关闭"
                            : "设备未连接";
                        state.NextDue = DateTime.Now.AddMinutes(5);
                    }
                    candidates.Add(state);
                    if (info.Category == DeviceCategory.Headset && state.Connected)
                    {
                        // 只有当前真的连上的蓝牙耳机才算“已有耳机”，否则一个已配对但
                        // 关机的蓝牙耳机记录会把 2.4G 接收器耳机顶掉、让它彻底不显示。
                        hasBluetoothHeadset = true;
                    }
                }
            }

            if (!hasBluetoothHeadset && usb != null)
            {
                DeviceState headset = new DeviceState();
                headset.Id = "usb:3654:1332";
                headset.Category = DeviceCategory.Headset;
                headset.Model = string.IsNullOrEmpty(usb.Name) ? "USB 无线耳机" : usb.Name;
                headset.DisplayName = headset.Model;
                headset.Transport = TransportKind.Usb24G;
                headset.VendorId = usb.VendorId;
                headset.ProductId = usb.ProductId;
                headset.HardwareId = usb.InstanceId;
                headset.Connected = usb.Connected;
                headset.BatterySupported = false;
                headset.Order = 2;
                headset.SourceDescription = "2.4G 接收器";
                headset.Status = DeviceStatus.Unsupported;
                headset.UnsupportedReason = "此连接方式（2.4G 接收器）不提供电量，请改用蓝牙连接";
                candidates.Add(Reuse(oldMap, headset));
            }

            bool hasHeadsetCandidate = false;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Category == DeviceCategory.Headset)
                {
                    hasHeadsetCandidate = true;
                    break;
                }
            }

            // 保留没有出现在本轮发现里的鼠标/耳机，用于面板显示“未连接”和最后已知值。
            foreach (KeyValuePair<string, DeviceState> pair in oldMap)
            {
                DeviceState old = pair.Value;
                bool already = false;
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (string.Equals(candidates[i].Id, old.Id, StringComparison.Ordinal))
                    {
                        already = true;
                        break;
                    }
                }
                if (already)
                {
                    continue;
                }

                if (old.Category == DeviceCategory.Headset && hasHeadsetCandidate)
                {
                    // 耳机已经以另一种连接方式出现：只保留一条主记录。
                    continue;
                }

                old.Connected = false;
                old.Status = old.Transport == TransportKind.BluetoothLe || old.Transport == TransportKind.BluetoothClassic
                    ? (bluetooth != null && bluetooth.RadioKnown && !bluetooth.RadioOn ? DeviceStatus.BluetoothOff : DeviceStatus.Offline)
                    : DeviceStatus.Offline;
                old.StatusDetail = old.Status == DeviceStatus.BluetoothOff ? "蓝牙已关闭" : "设备未连接";

                // 离线设备的最后读数只保留 24 小时，避免把很久以前的值当成当前值。
                if (old.LastSeen != DateTime.MinValue &&
                    (DateTime.Now - old.LastSeen).TotalHours >= 24 &&
                    old.HasValue)
                {
                    old.LastReading = new BatteryReading();
                    old.StatusDetail = "最后读数已超过 24 小时，已清除";
                    old.LastError = old.StatusDetail;
                }

                old.NextDue = DateTime.Now.AddMinutes(5);
                candidates.Add(old);
            }

            bool connectedNow = false;
            for (int i = 0; i < candidates.Count; i++)
            {
                DeviceState candidate = candidates[i];
                DeviceState previous;
                oldMap.TryGetValue(candidate.Id, out previous);
                bool transportChanged = previous != null && previous.Transport != candidate.Transport;
                if (candidate.Connected && candidate.BatterySupported &&
                    (previous == null || !previous.Connected || transportChanged ||
                     previous.Status == DeviceStatus.Offline ||
                     previous.Status == DeviceStatus.BluetoothOff))
                {
                    connectedNow = true;
                    break;
                }
            }
            if (connectedNow)
            {
                _queryConnectedNow = true;
            }

            candidates.Sort(delegate(DeviceState a, DeviceState b)
            {
                int order = a.Order.CompareTo(b.Order);
                if (order != 0) return order;
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
            });

            lock (_gate)
            {
                _devices.Clear();
                _devices.AddRange(candidates);
                for (int i = 0; i < _devices.Count; i++)
                {
                    AppSettings.ApplyDevicePreferences(_devices[i]);
                    AppSettings.RememberDevice(_devices[i]);
                    if (_devices[i].NextDue == DateTime.MinValue)
                    {
                        _devices[i].NextDue = DateTime.Now;
                    }
                }
            }
            if (firstDiscovery || identityMigrated)
            {
                // 首次发现或刚迁移 ID 时立刻落盘，确保设置里的鼠标名称
                // 从一开始就是识别出的型号，而不是旧的接收器名称。
                AppSettings.FlushKnownDevices();
            }
            else
            {
                AppSettings.SaveKnownDevicesIfDirty();
            }

            _lastDiscovery = DateTime.Now;
            lock (_gate)
            {
                _hasDiscovered = true;
            }
            Log.Info(string.Format(CultureInfo.InvariantCulture,
                "设备发现：ATK={0}，罗技={1}，厂商={5}，USB 耳机={2}，蓝牙耳机={3}，蓝牙={4}",
                atk != null, hidpp == null ? 0 : hidpp.Count, usb != null, hasBluetoothHeadset,
                bluetooth == null ? "未知" : (bluetooth.RadioOn ? "开" : "关"),
                vendors == null ? 0 : vendors.Count));
            RaiseChanged();
        }

        /// <summary>通用 HID 命中厂商协议已认领的接口时，只保留厂商那张卡片。</summary>
        private static bool MatchesVendorDevice(List<VendorDevice> vendors, HidDeviceInfo info)
        {
            if (vendors == null || info == null)
            {
                return false;
            }
            for (int i = 0; i < vendors.Count; i++)
            {
                VendorDevice device = vendors[i];
                if (device == null || device.Info == null)
                {
                    continue;
                }
                if (device.Info.VendorId != info.VendorId || device.Info.ProductId != info.ProductId)
                {
                    continue;
                }
                if (string.IsNullOrEmpty(device.Info.ProductName) || string.IsNullOrEmpty(info.ProductName))
                {
                    return true;
                }
                if (string.Equals(device.Info.ProductName, info.ProductName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool MatchesHidppDevice(List<HidppDevice> hidpp, HidDeviceInfo info)
        {
            if (hidpp == null || info == null)
            {
                return false;
            }
            for (int i = 0; i < hidpp.Count; i++)
            {
                HidppDevice device = hidpp[i];
                if (device == null || device.Info == null)
                {
                    continue;
                }
                if (device.Info.VendorId != info.VendorId ||
                    device.Info.ProductId != info.ProductId)
                {
                    continue;
                }
                if (string.IsNullOrEmpty(device.Info.ProductName) ||
                    string.IsNullOrEmpty(info.ProductName))
                {
                    return true;
                }
                if (string.Equals(device.Info.ProductName, info.ProductName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static DeviceState FindAtkDevice(Dictionary<string, DeviceState> oldMap)
        {
            foreach (KeyValuePair<string, DeviceState> pair in oldMap)
            {
                if (pair.Value != null &&
                    pair.Value.Category == DeviceCategory.Mouse &&
                    pair.Key.StartsWith("atk:", StringComparison.Ordinal))
                {
                    return pair.Value;
                }
            }
            return null;
        }

        private static DeviceState Reuse(Dictionary<string, DeviceState> oldMap, DeviceState candidate)
        {
            DeviceState old;
            if (!oldMap.TryGetValue(candidate.Id, out old))
            {
                candidate.LastReading = new BatteryReading();
                candidate.NextDue = DateTime.Now;
                return candidate;
            }

            bool transportChanged = old.Transport != candidate.Transport;
            candidate.LastReading = transportChanged ? new BatteryReading() : old.LastReading;
            candidate.LastSeen = old.LastSeen;
            candidate.LastAttempt = old.LastAttempt;
            candidate.NextDue = transportChanged ? DateTime.Now : old.NextDue;
            candidate.Status = transportChanged ? candidate.Status : old.Status;
            candidate.StatusDetail = old.StatusDetail;
            candidate.LastError = old.LastError;
            candidate.UnsupportedReason = candidate.UnsupportedReason ?? old.UnsupportedReason;
            candidate.QueryCount = old.QueryCount;
            candidate.SuccessCount = old.SuccessCount;
            candidate.FailCount = old.FailCount;

            candidate.WarnedLevels = transportChanged ? new HashSet<int>() : old.WarnedLevels;
            candidate.LastWarnPercent = old.LastWarnPercent;
            candidate.LastWarnTime = old.LastWarnTime;
            candidate.CurrentIntervalSeconds = old.CurrentIntervalSeconds;
            // 设备发现每轮都会重建 DeviceState，待确认的大跳变要跟着走，
            // 否则每次重新枚举都会把“已读到一次的新值”丢掉，永远确认不了。
            candidate.PendingPercent = transportChanged ? -1 : old.PendingPercent;
            candidate.ConfirmCount = transportChanged ? 0 : old.ConfirmCount;

            if (transportChanged)
            {
                candidate.Status = candidate.Category == DeviceCategory.Headset &&
                                   candidate.Transport == TransportKind.Usb24G
                    ? DeviceStatus.Unsupported
                    : DeviceStatus.Online;
            }
            else if (candidate.Transport == TransportKind.BluetoothClassic)
            {
                candidate.Status = DeviceStatus.Unsupported;
            }
            else if (candidate.Transport == TransportKind.Usb24G)
            {
                candidate.Status = DeviceStatus.Unsupported;
            }

            // 设备重新出现：离线/蓝牙关闭的状态要恢复成可查询状态。
            if (old.Status == DeviceStatus.Offline || old.Status == DeviceStatus.BluetoothOff)
            {
                candidate.Status = candidate.BatterySupported ? DeviceStatus.Online : DeviceStatus.Unsupported;
                candidate.NextDue = DateTime.Now;
                candidate.StatusDetail = null;
            }
            // “不支持读取”不再每轮发现都清零重试：只有连接方式变了或设备刚重新连上，
            // 才立即恢复为可查询；否则交给 QueryDue 按 30 分钟退避重试。
            if (old.Status == DeviceStatus.Unsupported && candidate.BatterySupported &&
                (!old.Connected || old.Transport != candidate.Transport))
            {
                candidate.Status = DeviceStatus.Online;
                candidate.NextDue = DateTime.Now;
            }

            return candidate;
        }

        private void QueryDue(bool manual)
        {
            List<DeviceState> due = new List<DeviceState>();
            lock (_gate)
            {
                DateTime now = DateTime.Now;
                for (int i = 0; i < _devices.Count; i++)
                {
                    DeviceState device = _devices[i];
                    if (device.Status == DeviceStatus.Offline ||
                        device.Status == DeviceStatus.BluetoothOff)
                    {
                        if (manual)
                        {
                            due.Add(device);
                        }
                        else if (device.Status == DeviceStatus.Offline && device.Connected &&
                                 device.BatterySupported && device.NextDue <= now)
                        {
                            // 设备其实还在（接收器/蓝牙栈仍报连接），只是之前连续没读到：
                            // 按自己的退避时间继续重试，不要只等下一轮发现，
                            // 否则卡片会消失好几分钟再冒出来。
                            due.Add(device);
                        }
                        continue;
                    }
                    if (device.Status == DeviceStatus.Unsupported)
                    {
                        if (manual)
                        {
                            due.Add(device);
                            continue;
                        }
                        // “暂时读不到”的设备（BatterySupported 仍为 true）按自己的
                        // 退避时间重试一次；能力缺失的设备保持不重试。
                        if (device.BatterySupported && device.NextDue <= now)
                        {
                            due.Add(device);
                        }
                        continue;
                    }
                    if (manual || device.NextDue <= now)
                    {
                        due.Add(device);
                    }
                }
            }

            if (due.Count == 0)
            {
                return;
            }

            // 按数据源分组并行：一个没连上的蓝牙耳机每轮要拖满整个蓝牙超时（最长 9 秒），
            // 以前串行查询会让鼠标/键盘的读数排在它后面，面板要等好几秒才出数字。
            // 同一数据源内部仍然串行——HID 句柄、蓝牙 GATT 会话都不适合并发复用。
            Dictionary<string, List<DeviceState>> groups = new Dictionary<string, List<DeviceState>>(StringComparer.Ordinal);
            List<string> order = new List<string>();
            for (int i = 0; i < due.Count; i++)
            {
                string key = QueryGroupKey(due[i]);
                List<DeviceState> bucket;
                if (!groups.TryGetValue(key, out bucket))
                {
                    bucket = new List<DeviceState>();
                    groups[key] = bucket;
                    order.Add(key);
                }
                bucket.Add(due[i]);
            }

            if (order.Count == 1)
            {
                List<DeviceState> only = groups[order[0]];
                for (int i = 0; i < only.Count; i++)
                {
                    if (_stopping)
                    {
                        return;
                    }
                    QueryOne(only[i]);
                }
                return;
            }

            int pending = order.Count;
            object finished = new object();
            for (int g = 0; g < order.Count; g++)
            {
                List<DeviceState> bucket = groups[order[g]];
                ThreadPool.QueueUserWorkItem(delegate(object state)
                {
                    List<DeviceState> devices = (List<DeviceState>)state;
                    try
                    {
                        for (int i = 0; i < devices.Count; i++)
                        {
                            if (_stopping)
                            {
                                break;
                            }
                            QueryOne(devices[i]);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("并行查询异常：" + ex.Message);
                    }
                    finally
                    {
                        lock (finished)
                        {
                            pending--;
                            Monitor.Pulse(finished);
                        }
                    }
                }, bucket);
            }

            // 等所有分组收工；蓝牙那组最多是自己的超时，不会再拖累别的组。
            lock (finished)
            {
                while (pending > 0)
                {
                    Monitor.Wait(finished, 20000);
                }
            }
        }

        /// <summary>
        /// 并行查询的分组键：同一种读取通道共用一个组（句柄/会话不并发复用），
        /// 不同通道之间互不影响。
        /// </summary>
        internal static string QueryGroupKey(DeviceState device)
        {
            if (device == null)
            {
                return "misc";
            }
            if (device.Transport == TransportKind.Atk24G)
            {
                return "atk";
            }
            if (device.Transport == TransportKind.BluetoothLe || device.Transport == TransportKind.BluetoothClassic)
            {
                return "ble";
            }
            if (!device.BatterySupported)
            {
                return "unsupported";
            }
            string id = device.Id ?? string.Empty;
            if (id.StartsWith("vendor:", StringComparison.Ordinal))
            {
                return "vendor";
            }
            if (id.StartsWith("hidpp:", StringComparison.Ordinal))
            {
                return "hidpp";
            }
            if (device.Transport == TransportKind.Usb)
            {
                return "hid";
            }
            return "misc";
        }

        /// <summary>一次读数失败属于哪一类，决定后续状态和重试节奏。</summary>
        internal enum FailureKind
        {
            /// <summary>设备本身没有可读的电量接口（有线键盘、经典蓝牙 HFP 等）。</summary>
            CapabilityMissing,

            /// <summary>只读到充电盒电量，耳机本体没读到：临时状态，稍后会恢复。</summary>
            CaseOnly,

            /// <summary>打不开设备/接收器：可能只是被占用或刚休眠，先重试再判离线。</summary>
            Disconnected,

            /// <summary>设备在但没响应（休眠、超时）。</summary>
            NoResponse,

            /// <summary>其它读取失败。</summary>
            Transient
        }

        /// <summary>
        /// 把一条失败读数归类。抽成纯函数是为了能直接单测这些关键的字符串判定，
        /// 避免「只读到充电盒」被误判成能力缺失、或者空错误被显示成没有原因的“读取异常”。
        /// </summary>
        internal static FailureKind ClassifyFailure(string error, bool deviceFound)
        {
            if (!deviceFound)
            {
                return FailureKind.Disconnected;
            }
            if (string.IsNullOrEmpty(error))
            {
                return FailureKind.Transient;
            }
            if (error.IndexOf("只读到充电盒", StringComparison.Ordinal) >= 0)
            {
                return FailureKind.CaseOnly;
            }
            if (error.IndexOf("未暴露", StringComparison.Ordinal) >= 0 ||
                error.IndexOf("未检测到", StringComparison.Ordinal) >= 0 ||
                error.IndexOf("HFP", StringComparison.Ordinal) >= 0 ||
                error.IndexOf("不提供", StringComparison.Ordinal) >= 0)
            {
                return FailureKind.CapabilityMissing;
            }
            if (error.IndexOf("没有响应", StringComparison.Ordinal) >= 0 ||
                error.IndexOf("休眠", StringComparison.Ordinal) >= 0 ||
                error.IndexOf("超时", StringComparison.Ordinal) >= 0)
            {
                return FailureKind.NoResponse;
            }
            return FailureKind.Transient;
        }

        /// <summary>
        /// 大跳变（例如 ATK 鼠标在 40% 与 55% 两个设备侧稳定状态之间切换）的处理结果。
        /// </summary>
        internal enum PercentUpdateDecision
        {
            /// <summary>直接采信：第一次读到值，或者变化幅度正常。</summary>
            Accept,

            /// <summary>大幅跳变，先挂起；需要连续两次读到同一个新值才生效。</summary>
            AwaitConfirmation
        }

        /// <summary>与上一次已确认读数相差多少个百分点才算“大跳变”。</summary>
        internal const int PercentJumpThreshold = 10;

        /// <summary>挂着未确认的跳变时，下一次确认读取最多等这么久。</summary>
        internal const int PendingConfirmRetrySeconds = 120;

        /// <summary>
        /// 判断一次新读数能否立刻采信。抽成纯函数便于单测：
        /// 小跳变立即生效（正常掉电不该被延迟），大跳变要连续两次读到同一个新值。
        /// 参数 pendingPercent 用 -1 表示当前没有挂起的值。
        /// </summary>
        internal static PercentUpdateDecision EvaluatePercentUpdate(
            bool hasPrevious, int previousPercent,
            int pendingPercent, int pendingCount,
            int reportedPercent,
            out int nextPendingPercent, out int nextPendingCount)
        {
            nextPendingPercent = -1;
            nextPendingCount = 0;

            if (!hasPrevious)
            {
                // 第一次拿到读数：没有可比对象，直接采信。
                return PercentUpdateDecision.Accept;
            }

            int delta = reportedPercent - previousPercent;
            if (delta < 0)
            {
                delta = -delta;
            }
            if (delta < PercentJumpThreshold)
            {
                // 正常掉电/充电曲线：立即生效，顺手清掉挂起的跳变（回到原值就是最好的反证）。
                return PercentUpdateDecision.Accept;
            }

            if (pendingPercent == reportedPercent && pendingCount >= 1)
            {
                // 连续第二次读到同一个新值：确认这是设备的真实状态，不是抖动。
                return PercentUpdateDecision.Accept;
            }

            nextPendingPercent = reportedPercent;
            nextPendingCount = pendingPercent == reportedPercent ? pendingCount + 1 : 1;
            return PercentUpdateDecision.AwaitConfirmation;
        }

        /// <summary>
        /// 蓝牙读的等待时长：第一轮给足 9 秒（HS 耳机首次建连确实慢），读不到就逐次减半。
        /// 未连接的耳机在系统里往往要拖满整个超时才回“没有响应”，9 秒乘以每轮刷新会把
        /// 面板和鼠标/键盘的读数一起卡住；读到值就立刻恢复成 9 秒，不影响正常设备。
        /// </summary>
        private int BleTimeoutFor(DeviceState device)
        {
            lock (_gate)
            {
                int timeout;
                if (device != null && device.Id != null && _bleTimeoutMs.TryGetValue(device.Id, out timeout))
                {
                    return timeout;
                }
                return BleReadTimeoutMs;
            }
        }

        private void NoteBleResult(DeviceState device, BatteryReading reading, int timeout)
        {
            if (device == null || string.IsNullOrEmpty(device.Id))
            {
                return;
            }
            bool got = reading != null && (reading.Percent.HasValue || reading.LeftPercent.HasValue ||
                                           reading.RightPercent.HasValue || reading.CasePercent.HasValue);
            lock (_gate)
            {
                if (got)
                {
                    _bleTimeoutMs[device.Id] = BleReadTimeoutMs;
                    return;
                }
                int next = Math.Max(MinBleReadTimeoutMs, timeout / 2);
                _bleTimeoutMs[device.Id] = next;
            }
        }
        private void QueryOne(DeviceState device)
        {
            BatteryReading reading;
            try
            {
                if (!device.BatterySupported)
                {
                    // 有线键盘/2.4G 耳机等明确不支持的电量接口：手动刷新时保持
                    // “不支持读取”，不要误走 HID 查询并把它标成离线。
                    reading = BatteryReading.Unknown(device.UnsupportedReason, BatterySourceKind.Unknown);
                    reading.DeviceFound = device.Connected;
                }
                else if (device.Transport == TransportKind.Atk24G)
                {
                    HidDeviceInfo info;
                    lock (_gate) { info = _atkInfo; }
                    reading = _atk.Read(info, 800);
                }
                else if (device.Transport == TransportKind.BluetoothLe ||
                         device.Transport == TransportKind.BluetoothClassic)
                {
                    int bleTimeout = BleTimeoutFor(device);
                    reading = _bluetooth.Read(device, bleTimeout);
                    NoteBleResult(device, reading, bleTimeout);
                }
                else if (device.Id != null && device.Id.StartsWith("vendor:", StringComparison.Ordinal))
                {
                    VendorDevice target = null;
                    lock (_gate)
                    {
                        for (int i = 0; i < _vendorDevices.Count; i++)
                        {
                            VendorDevice candidate = _vendorDevices[i];
                            if (candidate != null &&
                                string.Equals("vendor:" + candidate.StableId, device.Id, StringComparison.Ordinal))
                            {
                                target = candidate;
                                break;
                            }
                        }
                    }
                    reading = target == null
                        ? BatteryReading.Unknown("设备已移除", BatterySourceKind.Vendor)
                        : _vendor.Read(target, 700);
                }
                else if (device.Id != null && device.Id.StartsWith("hidpp:", StringComparison.Ordinal))
                {
                    HidppDevice target = null;
                    lock (_gate)
                    {
                        for (int i = 0; i < _hidppInfos.Count; i++)
                        {
                            HidppDevice candidate = _hidppInfos[i];
                            if (candidate != null &&
                                string.Equals("hidpp:" + candidate.StableId, device.Id, StringComparison.Ordinal))
                            {
                                target = candidate;
                                break;
                            }
                        }
                    }
                    if (target == null)
                    {
                        reading = BatteryReading.Unknown("罗技设备已移除", BatterySourceKind.Vendor);
                    }
                    else
                    {
                        reading = _hidpp.Read(target, 600);
                    }
                }
                else if (device.Transport == TransportKind.Usb)
                {
                    HidBatteryDevice item = null;
                    lock (_gate)
                    {
                        for (int i = 0; i < _genericHidInfos.Count; i++)
                        {
                            HidBatteryDevice candidate = _genericHidInfos[i];
                            if (candidate != null && candidate.Info != null &&
                                string.Equals("hid:" + candidate.Info.ShortId, device.Id, StringComparison.Ordinal))
                            {
                                item = candidate;
                                break;
                            }
                        }
                    }
                    if (item == null)
                    {
                        reading = BatteryReading.Unknown("HID 设备已移除", BatterySourceKind.GenericHid);
                    }
                    else
                    {
                        reading = _genericHid.Read(item, 800);
                    }
                }
                else
                {
                    reading = BatteryReading.Unknown(device.UnsupportedReason, BatterySourceKind.Unknown);
                    reading.DeviceFound = device.Connected;
                }
            }
            catch (Exception ex)
            {
                Log.Error("读取 " + device.DisplayName + " 失败：" + ex);
                reading = BatteryReading.Unknown("读取异常：" + ex.Message, BatterySourceKind.Unknown);
                reading.DeviceFound = true;
            }

            DateTime now = DateTime.Now;
            bool changedStatus = false;
            bool readingConfirmed = false;
            string oldStatus = device.StatusText;
            lock (_gate)
            {
                device.LastAttempt = now;
                device.QueryCount++;

                if (reading.HasPercent)
                {
                    device.SuccessCount++;

                    // 大跳变要先确认：设备侧电量计会在两个稳定状态之间切换
                    // （实测 ATK 鼠标在 40% 与 55% 之间来回，各自能维持数十分钟到数小时）。
                    // 直接采信的话，刚开机读到 55% 就落进历史，之后再掉回 40%，
                    // 历史曲线就成了锯齿。这里挂着等下一次读数验证，小跳变照常立即生效。
                    int lastConfirmed = device.PercentOrZero;
                    bool hasConfirmed = device.HasValue;
                    bool hadPending = device.PendingPercent >= 0;
                    int nextPendingPercent;
                    int nextPendingCount;
                    PercentUpdateDecision decision = EvaluatePercentUpdate(
                        hasConfirmed, lastConfirmed,
                        device.PendingPercent, device.ConfirmCount,
                        reading.Percent.Value,
                        out nextPendingPercent, out nextPendingCount);
                    bool jumpPending = decision == PercentUpdateDecision.AwaitConfirmation;
                    readingConfirmed = !jumpPending;
                    if (jumpPending)
                    {
                        device.PendingPercent = nextPendingPercent;
                        device.ConfirmCount = nextPendingCount;
                    }
                    else
                    {
                        device.PendingPercent = -1;
                        device.ConfirmCount = 0;
                    }

                    if (!jumpPending)
                    {
                        device.LastReading = reading;
                        if (reading.Source == BatterySourceKind.Vendor)
                        {
                            device.SourceDescription = "Airoha 厂商 BLE";
                        }
                    }
                    else
                    {
                        // 读数先不生效：保留上一次已确认的数值，只把“设备还在响应”
                        // 的事实和待确认的新值写进状态说明，界面照旧显示旧值。
                        device.StatusDetail = string.Format(CultureInfo.InvariantCulture,
                            "电量从 {0}% 跳到 {1}%，等待再次确认（{2}/2）",
                            lastConfirmed, reading.Percent.Value, Math.Min(nextPendingCount, 2));
                        Log.Debug("电量大跳变待确认：" + device.DisplayName + " " +
                            lastConfirmed + "% -> " + reading.Percent.Value + "%");
                    }

                    device.LastSeen = now;
                    device.LastError = null;
                    device.BatterySupported = true;
                    device.UnsupportedReason = null;
                    device.Status = reading.Charging.HasValue && reading.Charging.Value
                        ? DeviceStatus.Charging
                        : DeviceStatus.Online;
                    if (!jumpPending)
                    {
                        device.StatusDetail = null;
                    }
                    device.FailCount = 0;

                    // 待确认期间尽快再读一次（最多 2 分钟），确认后回到用户设置的间隔，
                    // 避免“等下一次刷新（可能几小时）”才能把真实状态显示出来。
                    int normalIntervalSeconds = ComputeInterval(device, AppSettings.RefreshMinutes);
                    device.CurrentIntervalSeconds = jumpPending
                        ? Math.Min(normalIntervalSeconds, PendingConfirmRetrySeconds)
                        : normalIntervalSeconds;
                    device.NextDue = now.AddSeconds(device.CurrentIntervalSeconds);

                    if (!jumpPending && hadPending)
                    {
                        // 由“挂起待确认”转为“确认生效”时留一条可见日志，方便事后对照历史曲线。
                        Log.Info("电量大跳变已确认：" + device.DisplayName + " " +
                            device.PercentOrZero + "%");
                    }
                }
                else
                {
                    device.FailCount++;
                    device.LastError = reading.Error;
                    FailureKind kind = ClassifyFailure(reading.Error, reading.DeviceFound);
                    if (kind == FailureKind.CapabilityMissing)
                    {
                        // 设备本身没有可读的电量接口：记住原因，30 分钟后再说。
                        device.Status = DeviceStatus.Unsupported;
                        device.BatterySupported = false;
                        device.UnsupportedReason = reading.Error;
                        device.NextDue = now.AddMinutes(30);
                    }
                    else if (kind == FailureKind.CaseOnly)
                    {
                        // 这是“耳机正在盒里/固件暂时没给左右耳”的临时状态，不是能力缺失：
                        // 保留 BatterySupported，30 分钟后再试，耳机取出后能自动恢复。
                        device.Status = DeviceStatus.Unsupported;
                        device.UnsupportedReason = reading.Error;
                        device.StatusDetail = reading.Error;
                        device.NextDue = now.AddMinutes(30);
                    }
                    else
                    {
                        // 剩下的都是“设备还在、这次没读到”（打不开句柄、休眠超时、其它异常）。
                        // 以前第一次失败就改成“等待响应”，面板和托盘立刻少一张卡片，
                        // 下次读到了又冒出来，看起来就是设备在频繁消失/出现。
                        // 现在：前 TransientFailuresBeforeHidden 次失败保留上一次的可见状态，
                        // 只把原因写进 StatusDetail；连续失败才降级为“等待响应”，
                        // 并按 30 秒 → 5 分钟 的节奏继续重试，读到就立刻恢复在线。
                        // 读失败不再改 Connected：连接与否只由设备发现决定。
                        bool keepVisible = device.FailCount < TransientFailuresBeforeHidden &&
                                           device.Connected && device.HasValue &&
                                           (device.Status == DeviceStatus.Online ||
                                            device.Status == DeviceStatus.Charging);
                        if (!keepVisible)
                        {
                            device.Status = DeviceStatus.Sleeping;
                        }
                        device.StatusDetail = string.IsNullOrEmpty(reading.Error)
                            ? (kind == FailureKind.NoResponse
                                ? "设备没有响应，稍后自动重试"
                                : "读取失败，稍后自动重试")
                            : reading.Error;
                        device.CurrentIntervalSeconds = ComputeRetrySeconds(device.FailCount);
                        device.NextDue = now.AddSeconds(device.CurrentIntervalSeconds);
                    }
                }

                changedStatus = !string.Equals(oldStatus, device.StatusText, StringComparison.Ordinal);
            }

            if (changedStatus)
            {
                Log.Info(string.Format(CultureInfo.InvariantCulture, "{0} 状态：{1} {2}",
                    device.DisplayName, device.StatusText, device.HasValue ? device.PercentOrZero + "%" : "—"));
            }

            if (reading.HasPercent && readingConfirmed)
            {
                BatteryHistory.Record(device, now);
                MaybeWarnLowBattery(device);
            }

            RaiseChanged();
        }

        internal static int ComputeInterval(DeviceState device, int baseMinutes)
        {
            if (device == null || !device.HasValue)
            {
                return ComputeFailureInterval(device == null ? 0 : device.FailCount, baseMinutes);
            }

            // 正常读数严格按用户设置的间隔，不再因低电量/充电/数值不变而额外查询。
            return ClampBaseSeconds(baseMinutes);
        }

        /// <summary>连续失败多少次之前，卡片仍按上一次的成功读数显示（避免一闪一闪）。</summary>
        internal const int TransientFailuresBeforeHidden = 3;

        /// <summary>
        /// “设备还在但这次没读到”的重试节奏：30 秒起步、逐次翻倍、最长 5 分钟。
        /// 与 <see cref="ComputeFailureInterval"/> 的区别是这里按秒级快速恢复：
        /// 单次打不开句柄/休眠超时，下一分钟内就能重新读到，设备不必消失好几分钟。
        /// </summary>
        internal static int ComputeRetrySeconds(int failCount)
        {
            int exponent = Math.Min(4, Math.Max(0, failCount - 1));
            int seconds = 30 * (1 << exponent);
            if (seconds > 300) seconds = 300;
            return seconds;
        }

        internal static int ComputeFailureInterval(int failCount, int baseMinutes)
        {
            int baseSeconds = ClampBaseSeconds(baseMinutes);
            int exponent = Math.Min(4, Math.Max(0, failCount));
            int seconds = 60 * (1 << exponent);
            if (seconds > 900) seconds = 900;
            if (seconds > baseSeconds) seconds = baseSeconds;
            return seconds;
        }

        private static int ClampBaseSeconds(int baseMinutes)
        {
            if (baseMinutes < 1) baseMinutes = 1;
            if (baseMinutes > 1440) baseMinutes = 1440;
            return baseMinutes * 60;
        }

        private void MaybeWarnLowBattery(DeviceState device)
        {
            if (!AppSettings.Current.LowBatteryWarning)
            {
                return;
            }

            bool clearWarn = false;
            bool savedWarn = false;
            bool resetWarn = false;
            int percent = 0;
            int chosen = -1;
            HashSet<int> warnedCopy = null;
            DeviceState eventDevice = null;

            // WarnedLevels / LastWarn* 会和 GetSnapshot → Clone 共享：UI 线程会在锁内枚举
            // 这个 HashSet，所以后台线程的 Clear/Add 必须用同一把锁，否则会抛
            // “Collection was modified”。文件读写和事件分发仍然放在锁外。
            lock (_gate)
            {
                if (!device.HasValue)
                {
                    return;
                }
                if (device.Status == DeviceStatus.Unsupported ||
                    device.Status == DeviceStatus.Offline ||
                    device.Status == DeviceStatus.BluetoothOff ||
                    device.Status == DeviceStatus.Sleeping ||
                    device.Status == DeviceStatus.Stale ||
                    device.Status == DeviceStatus.Error)
                {
                    return;
                }

                percent = device.PercentOrZero;
                bool charging = device.LastReading.Charging.HasValue && device.LastReading.Charging.Value;

                resetWarn = charging ||
                            (device.LastWarnPercent >= 0 && percent >= device.LastWarnPercent + 5);
                if (resetWarn)
                {
                    if (device.WarnedLevels.Count > 0)
                    {
                        device.WarnedLevels.Clear();
                        clearWarn = true;
                    }
                }
                else
                {
                    List<int> levels = AppSettings.Current.WarnLevels;
                    int customThreshold = AppSettings.GetDeviceAlertThreshold(device.Id);
                    chosen = LowBatteryPolicy.ChooseLevel(percent, customThreshold, levels,
                        device.WarnedLevels);
                    if (chosen >= 0)
                    {
                        // 一次跨过多个全局档位时只提醒一次，并把已经跨过的档位都标记掉，避免连续弹窗。
                        LowBatteryPolicy.MarkWarned(percent, customThreshold, levels, device.WarnedLevels);
                        device.LastWarnPercent = percent;
                        device.LastWarnTime = DateTime.Now;
                        warnedCopy = new HashSet<int>(device.WarnedLevels);
                        savedWarn = true;
                        eventDevice = device.Clone();
                    }
                }
            }

            if (clearWarn)
            {
                AppSettings.ClearWarnState(device.Id);
            }
            if (!savedWarn)
            {
                return;
            }

            AppSettings.SaveWarnState(device.Id, warnedCopy, percent);

            LowBatteryEventArgs args = new LowBatteryEventArgs();
            args.Device = eventDevice;
            args.Percent = percent;
            args.Level = chosen;
            EventHandler<LowBatteryEventArgs> handler = LowBattery;
            if (handler != null)
            {
                try { handler(this, args); } catch (Exception ex) { Log.Error("低电提醒事件异常：" + ex.Message); }
            }
        }

        private void RefreshStaleStatuses()
        {
            DateTime now = DateTime.Now;
            for (int i = 0; i < _devices.Count; i++)
            {
                DeviceState device = _devices[i];
                if (!device.HasValue)
                {
                    continue;
                }
                if (device.Status == DeviceStatus.Unsupported ||
                    device.Status == DeviceStatus.Offline ||
                    device.Status == DeviceStatus.BluetoothOff ||
                    device.Status == DeviceStatus.Sleeping ||
                    device.Status == DeviceStatus.Error)
                {
                    // 等待响应的设备保留自己的状态和重试原因，不要在这里降级成“数据过期”，
                    // 否则面板与诊断里看不出到底是没响应还是读数太旧。
                    continue;
                }

                int freshness = Math.Max(device.CurrentIntervalSeconds * 2, device.CurrentIntervalSeconds + 60);
                if (device.LastReading.Timestamp == DateTime.MinValue ||
                    (now - device.LastReading.Timestamp).TotalSeconds > freshness)
                {
                    device.Status = DeviceStatus.Stale;
                }
            }
        }

        private void RaiseChanged()
        {
            EventHandler handler = Changed;
            if (handler != null)
            {
                try { handler(this, EventArgs.Empty); } catch (Exception ex) { Log.Error("Changed 事件异常：" + ex.Message); }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Stop();
            _wake.Close();
            try { _workerExited.Close(); } catch (ObjectDisposedException) { }
        }
    }
}
