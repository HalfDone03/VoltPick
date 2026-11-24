using System;
using System.Collections.Generic;
using System.Text;

namespace DeviceBattery
{
    /// <summary>
    /// 一家厂商的无线上报通道。协议实现全部在同目录的 VendorProtocols.cs 里，
    /// 这里只放公共类型和枚举/读取的调度。
    /// </summary>
    internal abstract class VendorProtocol
    {
        public abstract string Id { get; }
        public abstract string Label { get; }

        /// <summary>这个接口是不是本协议要的设备集合之一（只看 VID/PID/用途页）。</summary>
        public abstract bool Accepts(HidDeviceInfo info);

        /// <summary>同一台设备有多个集合时，数字越小的越优先尝试。</summary>
        public virtual int Rank(HidDeviceInfo info)
        {
            return 0;
        }

        /// <summary>
        /// 这个协议两次查询之间的最小间隔（秒）。0 表示完全跟随用户在设置里选的全局间隔。
        /// 有的接收器问得太勤会出问题：雷蛇 BlackShark V3 会被问到重启，MCHOSE/Evision
        /// 键盘接收器要留时间让键盘休眠，所以它们各自设一个下限。
        /// </summary>
        public virtual int MinQuerySeconds
        {
            get { return 0; }
        }

        public abstract string NameFor(HidDeviceInfo info);

        public abstract DeviceCategory CategoryFor(HidDeviceInfo info);

        /// <summary>
        /// 同一个物理设备有时会以有线和接收器两个 PID 出现（华硕就是这样），
        /// 分组键决定它们是否合并成一张卡片。默认按 VID:PID 区分。
        /// </summary>
        public virtual string GroupKey(HidDeviceInfo info)
        {
            return info == null ? "" : info.VendorId.ToString("X4") + ":" + info.ProductId.ToString("X4");
        }

        /// <summary>打开集合、发请求、解析回包。失败时返回带原因的空读数。</summary>
        public abstract BatteryReading Read(HidDeviceInfo info, int timeoutMs);

        /// <summary>
        /// 请求和回包分在不同集合上时用的重载（ZOWIE 发在 FF03、收在 FF04，
        /// Areson 发在 feature 集合、收在 input 集合）。列表按 Rank 排好序，
        /// 第 0 项是首选。默认回落到单集合版本，原有协议一个字都不用改。
        /// </summary>
        public virtual BatteryReading Read(List<HidDeviceInfo> interfaces, int timeoutMs)
        {
            return interfaces == null || interfaces.Count == 0
                ? Failure("设备集合已移除")
                : Read(interfaces[0], timeoutMs);
        }

        protected static BatteryReading Success(int level, bool? charging, string raw)
        {
            BatteryReading reading = new BatteryReading();
            reading.DeviceFound = true;
            reading.Percent = level < 0 ? 0 : (level > 100 ? 100 : level);
            reading.Charging = charging;
            reading.Confidence = ReadingConfidence.High;
            reading.Source = BatterySourceKind.Vendor;
            reading.Raw = raw;
            reading.Timestamp = DateTime.Now;
            return reading;
        }

        protected static BatteryReading Failure(string error)
        {
            BatteryReading reading = new BatteryReading();
            reading.DeviceFound = true;
            reading.Confidence = ReadingConfidence.None;
            reading.Source = BatterySourceKind.Vendor;
            reading.Error = error;
            reading.Timestamp = DateTime.Now;
            return reading;
        }

        /// <summary>铺一条全零报文，把头部字节抄进去。</summary>
        protected static byte[] Frame(int length, params byte[] head)
        {
            byte[] buffer = new byte[length < head.Length ? head.Length : length];
            Array.Copy(head, buffer, head.Length);
            return buffer;
        }

        protected static string Hex(byte[] buffer, int offset, int length)
        {
            if (buffer == null)
            {
                return "";
            }
            if (offset + length > buffer.Length)
            {
                length = buffer.Length - offset;
            }
            if (length <= 0)
            {
                return "";
            }
            StringBuilder text = new StringBuilder(length * 3);
            for (int i = 0; i < length; i++)
            {
                if (i > 0)
                {
                    text.Append(' ');
                }
                text.Append(buffer[offset + i].ToString("X2"));
            }
            return text.ToString();
        }
    }

    /// <summary>发现阶段挑出来的一台厂商设备。</summary>
    internal sealed class VendorDevice
    {
        public VendorProtocol Protocol;
        // Info 始终是 Interfaces[0]（Rank 最小的一个），稳定 ID 一直按它算：
        // 这样新增「多集合读取」不会让老设备换 ID、丢掉重命名和低电阈值。
        public HidDeviceInfo Info;
        // 同一台设备的全部集合。请求在一个集合、回包在另一个集合的协议要用。
        public List<HidDeviceInfo> Interfaces = new List<HidDeviceInfo>();
        public string Name;
        public DeviceCategory Category;

        public string StableId
        {
            get { return Protocol.Id + "|" + (Info == null ? "?" : Info.ShortId); }
        }
    }

    /// <summary>
    /// 厂商通道的统一入口：发现时按 VID/PID/用途页认领接口，读取时交给对应协议。
    /// 认领过的接口不会再走通用 HID 和罗技 HID++，避免一台设备出两张卡片。
    /// </summary>
    internal sealed class VendorHidBatterySource
    {
        private static readonly VendorProtocol[] All = new VendorProtocol[]
        {
            new RazerProtocol(),
            new SteelSeriesProtocol(),
            new Query83Protocol(),
            new MchoseConfigProtocol(),
            new MchoseA5Protocol(),
            new MchoseG7Protocol(),
            new PulsarProtocol(),
            new HyperXProtocol(),
            new AsusProtocol(),
            new JblProtocol(),
            // 新增协议一律追加在末尾，不改前面 10 个的认领顺序：
            // 万一新旧协议认领了同一个接口，先注册的仍然优先，老设备行为不变。
            new EvisionProtocol(),
            new AulaKeyboardProtocol(),
            new MchoseGlwProtocol(),
            new SonixKeyboardProtocol(),
            new CorsairVoidProtocol(),
            new CorsairBragiProtocol(),
            new HyperXFlightProtocol(),
            new HyperXStingerProtocol(),
            new HyperXAlphaProtocol(),
            new RazerBlackSharkV3Protocol(),
            new RazerBlackSharkV2ProProtocol(),
            new RazerBarracudaProtocol(),
            new LogitechG533Protocol(),
            new AulaMouseProtocol(),
            new Endgame4KProtocol(),
            new ZowieProtocol(),
            new AresonProtocol()
        };

        public List<VendorDevice> Find(List<HidDeviceInfo> interfaces)
        {
            List<VendorDevice> found = new List<VendorDevice>();
            if (interfaces == null || interfaces.Count == 0)
            {
                return found;
            }

            // 同一台设备的多个集合归到一组：先按协议认领，再取排序最靠前的那个接口。
            Dictionary<string, List<HidDeviceInfo>> groups = new Dictionary<string, List<HidDeviceInfo>>();
            for (int i = 0; i < interfaces.Count; i++)
            {
                HidDeviceInfo info = interfaces[i];
                if (info == null)
                {
                    continue;
                }
                for (int p = 0; p < All.Length; p++)
                {
                    if (!All[p].Accepts(info))
                    {
                        continue;
                    }
                    string key = All[p].Id + "|" + All[p].GroupKey(info);
                    List<HidDeviceInfo> list;
                    if (!groups.TryGetValue(key, out list))
                    {
                        list = new List<HidDeviceInfo>();
                        groups[key] = list;
                    }
                    list.Add(info);
                    break;
                }
            }

            foreach (KeyValuePair<string, List<HidDeviceInfo>> pair in groups)
            {
                string[] parts = pair.Key.Split('|');
                VendorProtocol protocol = FindProtocol(parts[0]);
                if (protocol == null || pair.Value.Count == 0)
                {
                    continue;
                }

                pair.Value.Sort(delegate(HidDeviceInfo a, HidDeviceInfo b)
                {
                    return protocol.Rank(a).CompareTo(protocol.Rank(b));
                });

                VendorDevice device = new VendorDevice();
                device.Protocol = protocol;
                device.Info = pair.Value[0];
                device.Interfaces = pair.Value;
                device.Name = protocol.NameFor(pair.Value[0]);
                device.Category = protocol.CategoryFor(pair.Value[0]);
                found.Add(device);
            }

            return found;
        }

        public BatteryReading Read(VendorDevice device, int timeoutMs)
        {
            if (device == null || device.Info == null)
            {
                return VendorProtocolFailure("设备已移除");
            }
            try
            {
                return device.Protocol.Read(device.Interfaces, timeoutMs);
            }
            catch (Exception ex)
            {
                return VendorProtocolFailure(device.Protocol.Label + " 读取失败：" + ex.Message);
            }
        }

        private static BatteryReading VendorProtocolFailure(string error)
        {
            BatteryReading reading = new BatteryReading();
            reading.DeviceFound = false;
            reading.Source = BatterySourceKind.Vendor;
            reading.Confidence = ReadingConfidence.None;
            reading.Error = error;
            reading.Timestamp = DateTime.Now;
            return reading;
        }

        private static VendorProtocol FindProtocol(string id)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (All[i].Id == id)
                {
                    return All[i];
                }
            }
            return null;
        }
    }
}
