using System;
using System.Collections.Generic;
using System.Threading;

namespace DeviceBattery
{
    // ======================================================================
    // 这里每家协议都来自公开的逆向结果（HaloBattery / OpenRazer / HeadsetControl /
    // rivalcfg / steel-mouse 等），移植时保留原实现的校验强度：
    //   * 必须对得上命令、长度、校验和，任何一个不符就当作“没读到”，不猜数字；
    //   * 设备休眠/关机时返回原因，由上层决定是否保留旧读数。
    // ======================================================================

    /// <summary>雷蛇：90 字节 feature 报文，和 Synapse / OpenRazer 用的是同一条。</summary>
    internal sealed class RazerProtocol : VendorProtocol
    {
        private const ushort RazerVendorId = 0x1532;
        private const byte ClassPower = 0x07;
        private const byte CmdBattery = 0x80;
        private const byte CmdCharging = 0x84;
        private const byte StatusOk = 0x02;
        private const byte StatusBusy = 0x01;
        private const byte StatusTimeout = 0x04;
        private const byte StatusUnsupported = 0x05;
        private const int PayloadLength = 90;
        private const int FrameLength = PayloadLength + 1;

        private sealed class RazerModel
        {
            public string Name;
            public byte Transaction;

            public RazerModel(string name, byte transaction)
            {
                Name = name;
                Transaction = transaction;
            }
        }

        private static readonly byte[] TransactionIds = new byte[] { 0x1F, 0x3F, 0xFF, 0x9F, 0x08 };
        private static readonly Dictionary<ushort, RazerModel> Known = BuildKnown();
        private static readonly HashSet<ushort> Keyboards = new HashSet<ushort>
        {
            0x0290, 0x0292, 0x0296, 0x0298, 0x0271, 0x0258, 0x02BA, 0x02B9,
            0x02D5, 0x02D7, 0x025A, 0x025C
        };
        private static readonly HashSet<ushort> Headsets = new HashSet<ushort>
        {
            0x0528, 0x0555, 0x0556, 0x0557
        };
        private static readonly string[] WirelessWords = new string[]
        {
            "hyperspeed", "wireless", "receiver", "dongle", "dock", "blackshark", "barracuda", "nari"
        };

        private static Dictionary<ushort, RazerModel> BuildKnown()
        {
            Dictionary<ushort, RazerModel> map = new Dictionary<ushort, RazerModel>();
            Add(map, 0x0528, "Razer BlackShark V2 Pro (2020)", 0x3F);
            Add(map, 0x0555, "Razer BlackShark V2 Pro (2023)", 0x3F);
            Add(map, 0x0556, "Razer BlackShark V2 Pro (2023)", 0x3F);
            Add(map, 0x0557, "Razer BlackShark V2 Pro (2023)", 0x3F);
            Add(map, 0x00A4, "Razer Mouse Dock Pro", 0x1F);
            Add(map, 0x0290, "Razer DeathStalker V2 Pro", 0x9F);
            Add(map, 0x0292, "Razer DeathStalker V2 Pro", 0x1F);
            Add(map, 0x0296, "Razer DeathStalker V2 Pro TKL", 0x9F);
            Add(map, 0x0298, "Razer DeathStalker V2 Pro TKL", 0x1F);
            Add(map, 0x0271, "Razer BlackWidow V3 Mini HyperSpeed", 0x9F);
            Add(map, 0x0258, "Razer BlackWidow V3 Mini HyperSpeed", 0x1F);
            Add(map, 0x02BA, "Razer BlackWidow V4 Mini HyperSpeed", 0x9F);
            Add(map, 0x02B9, "Razer BlackWidow V4 Mini HyperSpeed", 0x1F);
            Add(map, 0x02D5, "Razer BlackWidow V4 Tenkeyless HyperSpeed", 0x9F);
            Add(map, 0x02D7, "Razer BlackWidow V4 Tenkeyless HyperSpeed", 0x1F);
            Add(map, 0x025A, "Razer BlackWidow V3 Pro", 0x3F);
            Add(map, 0x025C, "Razer BlackWidow V3 Pro", 0x9F);
            Add(map, 0x001F, "Razer Naga Epic", 0xFF);
            Add(map, 0x0024, "Razer Mamba (2012)", 0xFF);
            Add(map, 0x0025, "Razer Mamba (2012)", 0xFF);
            Add(map, 0x0032, "Razer Ouroboros", 0xFF);
            Add(map, 0x003E, "Razer Naga Epic Chroma", 0xFF);
            Add(map, 0x003F, "Razer Naga Epic Chroma", 0xFF);
            Add(map, 0x0044, "Razer Mamba", 0xFF);
            Add(map, 0x0045, "Razer Mamba", 0xFF);
            Add(map, 0x0059, "Razer Lancehead", 0x3F);
            Add(map, 0x005A, "Razer Lancehead", 0x3F);
            Add(map, 0x0062, "Razer Atheris", 0x1F);
            Add(map, 0x006F, "Razer Lancehead Wireless", 0x1F);
            Add(map, 0x0070, "Razer Lancehead Wireless", 0x1F);
            Add(map, 0x0072, "Razer Mamba Wireless", 0x3F);
            Add(map, 0x0073, "Razer Mamba Wireless", 0x3F);
            Add(map, 0x0077, "Razer Pro Click", 0x1F);
            Add(map, 0x0080, "Razer Pro Click", 0x1F);
            Add(map, 0x007A, "Razer Viper Ultimate", 0xFF);
            Add(map, 0x007B, "Razer Viper Ultimate", 0xFF);
            Add(map, 0x007C, "Razer DeathAdder V2 Pro", 0x3F);
            Add(map, 0x007D, "Razer DeathAdder V2 Pro", 0x3F);
            Add(map, 0x0083, "Razer Basilisk X HyperSpeed", 0xFF);
            Add(map, 0x0086, "Razer Basilisk Ultimate", 0x1F);
            Add(map, 0x0088, "Razer Basilisk Ultimate", 0x1F);
            Add(map, 0x008F, "Razer Naga Pro", 0x1F);
            Add(map, 0x0090, "Razer Naga Pro", 0x1F);
            Add(map, 0x0094, "Razer Orochi V2", 0x1F);
            Add(map, 0x009A, "Razer Pro Click Mini", 0x1F);
            Add(map, 0x009C, "Razer DeathAdder V2 X HyperSpeed", 0x1F);
            Add(map, 0x009E, "Razer Viper Mini SE", 0x1F);
            Add(map, 0x009F, "Razer Viper Mini SE", 0x1F);
            Add(map, 0x00A5, "Razer Viper V2 Pro", 0x1F);
            Add(map, 0x00A6, "Razer Viper V2 Pro", 0x1F);
            Add(map, 0x00A7, "Razer Naga V2 Pro", 0x1F);
            Add(map, 0x00A8, "Razer Naga V2 Pro", 0x1F);
            Add(map, 0x00AA, "Razer Basilisk V3 Pro", 0x1F);
            Add(map, 0x00AB, "Razer Basilisk V3 Pro", 0x1F);
            Add(map, 0x00AF, "Razer Cobra Pro", 0x1F);
            Add(map, 0x00B0, "Razer Cobra Pro", 0x1F);
            Add(map, 0x00B3, "Razer HyperPolling Dongle", 0x1F);
            Add(map, 0x00B4, "Razer Naga V2 HyperSpeed", 0x1F);
            Add(map, 0x00B6, "Razer DeathAdder V3 Pro", 0x1F);
            Add(map, 0x00B7, "Razer DeathAdder V3 Pro", 0x1F);
            Add(map, 0x00B8, "Razer Viper V3 HyperSpeed", 0x1F);
            Add(map, 0x00B9, "Razer Basilisk V3 X HyperSpeed", 0x1F);
            Add(map, 0x00BE, "Razer DeathAdder V4 Pro", 0x1F);
            Add(map, 0x00BF, "Razer DeathAdder V4 Pro", 0x1F);
            Add(map, 0x00C0, "Razer Viper V3 Pro", 0x1F);
            Add(map, 0x00C1, "Razer Viper V3 Pro", 0x1F);
            Add(map, 0x00C2, "Razer DeathAdder V3 Pro", 0x1F);
            Add(map, 0x00C3, "Razer DeathAdder V3 Pro", 0x1F);
            Add(map, 0x00C4, "Razer DeathAdder V3 HyperSpeed", 0x1F);
            Add(map, 0x00C5, "Razer DeathAdder V3 HyperSpeed", 0x1F);
            Add(map, 0x00C7, "Razer Pro Click V2 Vertical Edition", 0x1F);
            Add(map, 0x00C8, "Razer Pro Click V2 Vertical Edition", 0x1F);
            Add(map, 0x00CC, "Razer Basilisk V3 Pro 35K", 0x1F);
            Add(map, 0x00CD, "Razer Basilisk V3 Pro 35K", 0x1F);
            Add(map, 0x00D0, "Razer Pro Click V2", 0x1F);
            Add(map, 0x00D1, "Razer Pro Click V2", 0x1F);
            Add(map, 0x00D3, "Razer Basilisk Mobile", 0x1F);
            Add(map, 0x00D4, "Razer Basilisk Mobile", 0x1F);
            return map;
        }

        private static void Add(Dictionary<ushort, RazerModel> map, ushort pid, string name, byte transaction)
        {
            map[pid] = new RazerModel(name, transaction);
        }

        public override string Id
        {
            get { return "razer"; }
        }

        public override string Label
        {
            get { return "雷蛇 HID"; }
        }

        public override bool Accepts(HidDeviceInfo info)
        {
            if (info == null || info.VendorId != RazerVendorId)
            {
                return false;
            }
            if (Known.ContainsKey(info.ProductId))
            {
                return true;
            }
            string name = info.ProductName == null ? "" : info.ProductName.ToLowerInvariant();
            for (int i = 0; i < WirelessWords.Length; i++)
            {
                if (name.IndexOf(WirelessWords[i], StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        public override int Rank(HidDeviceInfo info)
        {
            // 命令走厂商集合（0xFF00）或鼠标/键盘集合（0x0001），其余最后再试。
            if (info.UsagePage == 0xFF00)
            {
                return 0;
            }
            if (info.UsagePage == 0x0001)
            {
                return 1;
            }
            return 2;
        }

        public override string NameFor(HidDeviceInfo info)
        {
            RazerModel model;
            if (Known.TryGetValue(info.ProductId, out model))
            {
                return model.Name;
            }
            return string.IsNullOrEmpty(info.ProductName)
                ? "雷蛇无线设备 0x" + info.ProductId.ToString("X4")
                : info.ProductName;
        }

        public override DeviceCategory CategoryFor(HidDeviceInfo info)
        {
            if (Keyboards.Contains(info.ProductId))
            {
                return DeviceCategory.Keyboard;
            }
            if (Headsets.Contains(info.ProductId))
            {
                return DeviceCategory.Headset;
            }
            string name = (NameFor(info) ?? "").ToLowerInvariant();
            if (name.IndexOf("blackshark", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("barracuda", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("nari", StringComparison.Ordinal) >= 0)
            {
                return DeviceCategory.Headset;
            }
            if (name.IndexOf("keyboard", StringComparison.Ordinal) >= 0)
            {
                return DeviceCategory.Keyboard;
            }
            return DeviceCategory.Mouse;
        }

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            RazerModel model;
            byte preferred = Known.TryGetValue(info.ProductId, out model) ? model.Transaction : (byte)0x1F;

            HidConnection connection = new HidConnection();
            try
            {
                if (!connection.Open(info.Path))
                {
                    return Failure("无法打开雷蛇设备（可能被雷蛇软件独占）");
                }

                int value;
                int status = 0;
                // 报文长度按集合自己声明的来：HidD_SetFeature / HidD_GetFeature 要求缓冲区
                // 不小于报告长度，压着小了驱动直接失败。
                int featureLength = Math.Max(FrameLength, (int)info.FeatureReportLength);
                List<byte> transactions = new List<byte>();
                transactions.Add(preferred);
                for (int i = 0; i < TransactionIds.Length; i++)
                {
                    if (TransactionIds[i] != preferred)
                    {
                        transactions.Add(TransactionIds[i]);
                    }
                }

                for (int i = 0; i < transactions.Count; i++)
                {
                    status = Query(connection, transactions[i], ClassPower, CmdBattery, timeoutMs, featureLength, out value);
                    if (status == StatusOk)
                    {
                        int level = (int)Math.Round(value * 100.0 / 255.0);
                        int chargeValue;
                        int chargeStatus = Query(connection, transactions[i], ClassPower, CmdCharging,
                            Math.Min(timeoutMs, 600), featureLength, out chargeValue);
                        bool? charging = chargeStatus == StatusOk ? (bool?)(chargeValue != 0) : null;
                        return Success(level, charging, "tid=" + transactions[i].ToString("X2") + " raw=" + value);
                    }
                    if (status == StatusTimeout || status == StatusUnsupported)
                    {
                        break;
                    }
                }

                return Failure(ReasonFor(status));
            }
            finally
            {
                connection.Dispose();
            }
        }

        private static string ReasonFor(int status)
        {
            if (status == StatusTimeout)
            {
                return "接收器在，但设备没有响应（已关机或休眠）";
            }
            if (status == StatusUnsupported)
            {
                return "这台设备不支持电量查询";
            }
            if (status == 0x03)
            {
                return "设备拒绝了电量查询";
            }
            return "没有收到电量应答（可能被雷蛇软件占用）";
        }

        private static int Query(HidConnection connection, byte transaction, byte commandClass, byte commandId,
            int timeoutMs, int featureLength, out int value)
        {
            value = -1;
            byte[] request = BuildRequest(transaction, commandClass, commandId);
            byte[] frame = new byte[featureLength];
            Array.Copy(request, 0, frame, 1, request.Length);

            if (!Native.HidD_SetFeature(connection.Handle, frame, frame.Length))
            {
                return -1;
            }

            int deadline = Environment.TickCount + Math.Max(200, timeoutMs);
            Thread.Sleep(60);
            int lastStatus = -1;
            while (Environment.TickCount < deadline)
            {
                byte[] reply = new byte[frame.Length];
                reply[0] = 0x00;
                if (!Native.HidD_GetFeature(connection.Handle, reply, reply.Length))
                {
                    return -1;
                }

                // Windows 会把报告号放在第 0 字节，扣掉它才是协议里的 90 字节。
                int offset = reply.Length >= PayloadLength + 1 ? 1 : 0;
                if (reply.Length - offset < PayloadLength)
                {
                    return -1;
                }

                lastStatus = reply[offset];
                bool forUs = reply[offset + 6] == commandClass && reply[offset + 7] == commandId;
                if (!forUs)
                {
                    // 同一集合上别的程序（灯效）的应答：只有“成功”状态才可能是它的，
                    // 04/05 是设备给出的最终答复，可以直接返回。
                    if (lastStatus != StatusOk)
                    {
                        return lastStatus;
                    }
                    Thread.Sleep(60);
                    continue;
                }

                if (lastStatus == StatusOk)
                {
                    value = reply[offset + 9];
                    return StatusOk;
                }
                if (lastStatus == StatusBusy)
                {
                    Thread.Sleep(60);
                    continue;
                }
                return lastStatus;
            }

            return lastStatus == StatusOk ? StatusBusy : lastStatus;
        }

        /// <summary>协议里的 90 字节请求：事务号、长度、命令类/命令号，最后是异或校验。</summary>
        internal static byte[] BuildRequest(byte transaction, byte commandClass, byte commandId)
        {
            byte[] message = new byte[PayloadLength];
            message[1] = transaction;
            message[5] = 0x02;
            message[6] = commandClass;
            message[7] = commandId;
            byte crc = 0;
            for (int i = 2; i < 88; i++)
            {
                crc ^= message[i];
            }
            message[88] = crc;
            return message;
        }
    }

    /// <summary>赛睿：耳机走 b0 交换，鼠标走 aa / d2 交换，都在 0xFFC0 配置集合上。</summary>
    internal sealed class SteelSeriesProtocol : VendorProtocol
    {
        private const ushort SteelSeriesVendorId = 0x1038;
        private const ushort ConfigUsagePage = 0xFFC0;
        private const byte ReportHeadsets = 0xB0;
        private const byte ReportNovaPro = 0x06;

        internal enum Kind
        {
            Nova7,
            Nova7Discrete,
            Nova5,
            Arctis7Plus,
            GameBuds,
            NovaPro,
            Rival3,
            Aerox3
        }

        private sealed class Model
        {
            public string Name;
            public Kind Kind;
            public DeviceCategory Category;

            public Model(string name, Kind kind, DeviceCategory category)
            {
                Name = name;
                Kind = kind;
                Category = category;
            }
        }

        private static readonly Dictionary<ushort, Model> Known = BuildKnown();

        private static Dictionary<ushort, Model> BuildKnown()
        {
            Dictionary<ushort, Model> map = new Dictionary<ushort, Model>();
            Add(map, 0x22A1, "Arctis Nova 7", Kind.Nova7);
            Add(map, 0x2202, "Arctis Nova 7", Kind.Nova7Discrete);
            Add(map, 0x227E, "Arctis Nova 7 Gen 2", Kind.Nova7);
            Add(map, 0x2206, "Arctis Nova 7x", Kind.Nova7Discrete);
            Add(map, 0x2258, "Arctis Nova 7x", Kind.Nova7);
            Add(map, 0x229E, "Arctis Nova 7x", Kind.Nova7);
            Add(map, 0x22AD, "Arctis Nova 7x", Kind.Nova7);
            Add(map, 0x22A4, "Arctis Nova 7X", Kind.Nova7Discrete);
            Add(map, 0x22A5, "Arctis Nova 7X", Kind.Nova7);
            Add(map, 0x223A, "Arctis Nova 7 Diablo IV", Kind.Nova7Discrete);
            Add(map, 0x22A9, "Arctis Nova 7 Diablo IV", Kind.Nova7);
            Add(map, 0x227A, "Arctis Nova 7 WoW Edition", Kind.Nova7Discrete);
            Add(map, 0x2232, "Arctis Nova 5", Kind.Nova5);
            Add(map, 0x2253, "Arctis Nova 5X", Kind.Nova5);
            Add(map, 0x220A, "Arctis Nova 7P", Kind.Nova7Discrete);
            Add(map, 0x22A7, "Arctis Nova 7P", Kind.Nova7);
            Add(map, 0x2298, "Arctis Nova 7P", Kind.Nova7);
            Add(map, 0x2269, "Arctis Nova 3P Wireless", Kind.Nova5);
            Add(map, 0x226D, "Arctis Nova 3X Wireless", Kind.Nova5);
            Add(map, 0x220E, "Arctis 7+", Kind.Arctis7Plus);
            Add(map, 0x2212, "Arctis 7+ PS5", Kind.Arctis7Plus);
            Add(map, 0x2216, "Arctis 7+ Xbox", Kind.Arctis7Plus);
            Add(map, 0x2236, "Arctis 7+ Destiny", Kind.Arctis7Plus);
            Add(map, 0x230A, "Arctis GameBuds", Kind.GameBuds);
            Add(map, 0x2317, "Arctis GameBuds X", Kind.GameBuds);
            Add(map, 0x12E0, "Arctis Nova Pro Wireless", Kind.NovaPro);
            Add(map, 0x12E5, "Arctis Nova Pro Wireless X", Kind.NovaPro);
            Add(map, 0x1830, "SteelSeries Rival 3 Wireless", Kind.Rival3);
            Add(map, 0x1872, "SteelSeries Rival 3 Wireless Gen 2", Kind.Rival3);
            Add(map, 0x1838, "SteelSeries Aerox 3 Wireless", Kind.Aerox3);
            Add(map, 0x1878, "SteelSeries Aerox 3 Wireless CS2", Kind.Aerox3);
            Add(map, 0x1852, "SteelSeries Aerox 5 Wireless", Kind.Aerox3);
            Add(map, 0x185C, "SteelSeries Aerox 5 Wireless Destiny 2", Kind.Aerox3);
            Add(map, 0x1860, "SteelSeries Aerox 5 Wireless Diablo IV", Kind.Aerox3);
            Add(map, 0x1858, "SteelSeries Aerox 9 Wireless", Kind.Aerox3);
            Add(map, 0x1874, "SteelSeries Aerox 9 Wireless WoW", Kind.Aerox3);
            return map;
        }

        private static void Add(Dictionary<ushort, Model> map, ushort pid, string name, Kind kind)
        {
            DeviceCategory category = DeviceCategory.Headset;
            if (kind == Kind.Rival3 || kind == Kind.Aerox3)
            {
                category = DeviceCategory.Mouse;
            }
            map[pid] = new Model(name, kind, category);
        }

        public override string Id
        {
            get { return "steelseries"; }
        }

        public override string Label
        {
            get { return "赛睿 HID"; }
        }

        public override bool Accepts(HidDeviceInfo info)
        {
            return info != null && info.VendorId == SteelSeriesVendorId && Known.ContainsKey(info.ProductId);
        }

        public override int Rank(HidDeviceInfo info)
        {
            return info.UsagePage == ConfigUsagePage ? 0 : 1;
        }

        public override string NameFor(HidDeviceInfo info)
        {
            Model model;
            if (Known.TryGetValue(info.ProductId, out model))
            {
                return model.Name;
            }
            return string.IsNullOrEmpty(info.ProductName) ? "赛睿无线设备" : info.ProductName;
        }

        public override DeviceCategory CategoryFor(HidDeviceInfo info)
        {
            Model model;
            if (Known.TryGetValue(info.ProductId, out model))
            {
                return model.Category;
            }
            return DeviceCategory.Headset;
        }

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            Model model;
            if (!Known.TryGetValue(info.ProductId, out model))
            {
                return Failure("未知的赛睿设备");
            }

            HidConnection connection = new HidConnection();
            try
            {
                if (!connection.Open(info.Path))
                {
                    return Failure("无法打开赛睿设备（可能被赛睿 GG 独占）");
                }

                if (model.Kind == Kind.Rival3 || model.Kind == Kind.Aerox3)
                {
                    return ReadMouse(connection, info, model, timeoutMs);
                }
                return ReadHeadset(connection, info, model, timeoutMs);
            }
            finally
            {
                connection.Dispose();
            }
        }

        private BatteryReading ReadHeadset(HidConnection connection, HidDeviceInfo info, Model model, int timeoutMs)
        {
            byte reportId = model.Kind == Kind.NovaPro ? ReportNovaPro : (byte)0x00;
            int length = info.OutputReportLength >= 3 ? info.OutputReportLength : 64;
            byte[] request = new byte[length];
            request[0] = reportId;
            request[1] = ReportHeadsets;

            if (!connection.Write(request))
            {
                return Failure("写入赛睿接收器失败");
            }

            byte[] reply = WaitReport(connection, info, ReportHeadsets, timeoutMs);
            if (reply == null)
            {
                return Failure("赛睿接收器没有应答（耳机可能已关机）");
            }

            int level;
            bool charging;
            bool online;
            if (!Parse(model.Kind, reply, out level, out charging, out online))
            {
                return Failure("赛睿电量应答格式不符（原始：" + Hex(reply, 0, 8) + "）");
            }
            if (!online)
            {
                return Failure("耳机已关机或不在范围内");
            }
            return Success(level, charging, Hex(reply, 0, 8));
        }

        private BatteryReading ReadMouse(HidConnection connection, HidDeviceInfo info, Model model, int timeoutMs)
        {
            bool aerox = model.Kind == Kind.Aerox3;
            int length = info.OutputReportLength >= 3 ? info.OutputReportLength : 64;
            byte[] request = new byte[length];
            request[0] = 0x00;
            request[1] = aerox ? (byte)0xD2 : (byte)0xAA;
            if (!aerox)
            {
                request[2] = 0x01;
            }

            string lastError = "鼠标没有应答（可能已关机或休眠）";
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (!connection.Write(request))
                {
                    return Failure("写入赛睿鼠标失败");
                }

                int deadline = Environment.TickCount + Math.Max(150, timeoutMs);
                while (Environment.TickCount < deadline)
                {
                    byte[] reply = new byte[info.InputReportLength >= 3 ? info.InputReportLength : 64];
                    int read;
                    if (!connection.Read(reply, Math.Max(80, timeoutMs / 2), out read) || read <= 0)
                    {
                        continue;
                    }

                    int level;
                    bool charging;
                    bool online;
                    if (aerox)
                    {
                        online = ParseAerox3(reply, read, out level, out charging);
                    }
                    else
                    {
                        online = ParseRival3(reply, read, out level, out charging);
                    }

                    if (online)
                    {
                        return Success(level, charging, Hex(reply, 0, 8));
                    }
                }
            }
            return Failure(lastError);
        }

        private static byte[] WaitReport(HidConnection connection, HidDeviceInfo info, byte reportId, int timeoutMs)
        {
            int deadline = Environment.TickCount + Math.Max(300, timeoutMs);
            byte[] first = null;
            while (Environment.TickCount < deadline)
            {
                byte[] reply = new byte[info.InputReportLength >= 3 ? info.InputReportLength : 64];
                int read;
                if (!connection.Read(reply, 120, out read) || read <= 0)
                {
                    continue;
                }
                if (reply[0] == reportId)
                {
                    return reply;
                }
                if (first == null)
                {
                    first = reply;
                }
            }
            // 和 HeadsetControl 一样：没有 b0 报文时把收到的第一条交给解析器判断。
            return first;
        }

        /// <summary>按型号解析 b0 应答。多种布局共用同一条报表，所以必须严格校验。</summary>
        internal static bool Parse(Kind kind, byte[] r, out int level, out bool charging, out bool online)
        {
            level = 0;
            charging = false;
            online = false;
            if (r == null)
            {
                return false;
            }

            switch (kind)
            {
                case Kind.Nova7:
                    if (r.Length < 4 || r[0] != 0xB0 || r[1] != 0x03 || r[3] == 0x00)
                    {
                        return false;
                    }
                    level = Math.Min((int)r[2], 100);
                    charging = r[3] == 0x01 || r[3] == 0x02;
                    online = true;
                    return true;

                case Kind.Nova7Discrete:
                    if (!Parse(Kind.Nova7, r, out level, out charging, out online))
                    {
                        return false;
                    }
                    level = Math.Min(level, 4) * 25;
                    return true;

                case Kind.Nova5:
                    if (r.Length < 5 || r[0] != 0xB0 || r[1] == 0x02)
                    {
                        return false;
                    }
                    level = Math.Min((int)r[3], 100);
                    charging = r[4] == 0x01;
                    online = true;
                    return true;

                case Kind.Arctis7Plus:
                    if (r.Length < 4 || r[0] != 0xB0 || r[1] == 0x01)
                    {
                        return false;
                    }
                    level = Math.Min((int)r[2], 4) * 25;
                    charging = r[3] == 0x01;
                    online = true;
                    return true;

                case Kind.GameBuds:
                {
                    if (r.Length < 7 || r[0] != 0xB0)
                    {
                        return false;
                    }
                    int best = 101;
                    for (int i = 0; i < 2; i++)
                    {
                        if (r[3 + i] == 0x03 && r[5 + i] < best)
                        {
                            best = Math.Min((int)r[5 + i], 100);
                        }
                    }
                    if (best > 100)
                    {
                        return false;
                    }
                    level = best;
                    charging = false;
                    online = true;
                    return true;
                }

                case Kind.NovaPro:
                {
                    // 9 档粗电量（0..8），状态字节 01 关机 / 02 充电 / 08 使用中。
                    if (r.Length < 16)
                    {
                        return false;
                    }
                    int code = r[6];
                    int state = r[15];
                    if (code > 8 || (state != 0x01 && state != 0x02 && state != 0x08))
                    {
                        return false;
                    }
                    level = code * 100 / 8;
                    charging = state == 0x02;
                    online = state != 0x01;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Rival 3：有 aa 回显时按回显读，没有时按 rivalcfg 的布局读。</summary>
        internal static bool ParseRival3(byte[] r, int length, out int level, out bool charging)
        {
            level = 0;
            charging = false;
            if (r == null || length <= 0)
            {
                return false;
            }
            int m = (r[0] == 0x00 && length > 1) ? 1 : 0;
            if (length >= m + 4 && r[m] == 0xAA)
            {
                int value = r[m + 1];
                if (value < 0 || value > 100)
                {
                    return false;
                }
                level = value;
                charging = r[m + 3] != 0;
                return true;
            }
            if (length >= m + 3)
            {
                int value = r[m];
                int flag = r[m + 2];
                if (value >= 0 && value <= 100 && (flag == 0 || flag == 1))
                {
                    level = value;
                    charging = flag == 1;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Aerox 3/5/9：d2 回显、充电标志在最高位、电平是 1..21 档或直接百分比。</summary>
        internal static bool ParseAerox3(byte[] r, int length, out int level, out bool charging)
        {
            level = 0;
            charging = false;
            if (r == null || length <= 0)
            {
                return false;
            }
            int m = (r[0] == 0x00 && length > 1) ? 1 : 0;
            if (length < m + 2 || r[m] != 0xD2)
            {
                return false;
            }
            int raw = r[m + 1];
            int value = raw & 0x7F;
            if (value == 0)
            {
                return false;
            }
            level = value > 21 ? Math.Min(value, 100) : (value - 1) * 5;
            charging = (raw & 0x80) != 0;
            return true;
        }
    }
}
