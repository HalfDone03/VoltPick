using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace DeviceBattery
{
    // ======================================================================
    // 第二批厂商通道：HyperX Cloud II 无线、华硕 ROG/TUF 无线鼠标、JBL Quantum 910。
    // 协议同样来自公开逆向结果（HeadsetControl 的 hyperx/corsair 设备、G-Helper 的
    // 华硕鼠标、plugato 的 JBL 监视器），这里只取协议事实，代码按本项目的读法重写。
    // ======================================================================

    /// <summary>
    /// HyperX Cloud II 无线：接收器的厂商集合 ff90:0303，请求 52 字节
    /// <c>06 ff bb &lt;命令&gt;</c>，回报前四字节回显，电量在 [7]、充电标志在 [4]。
    /// </summary>
    internal sealed class HyperXProtocol : VendorProtocol
    {
        private const ushort HyperXVendorId = 0x03F0;
        private const ushort UsagePageWanted = 0xFF90;
        private const ushort UsageWanted = 0x0303;
        private const byte ReportId = 0x06;
        private const byte CommandLevel = 0x02;
        private const byte CommandCharging = 0x03;
        private const int LevelIndex = 7;
        private const int ChargingIndex = 4;
        private const int RequestLength = 52;
        private const int ReplyLength = 20;

        private static readonly Dictionary<ushort, string> Models = new Dictionary<ushort, string>
        {
            { 0x0696, "HyperX Cloud II Wireless" },
            { 0x018B, "HyperX Cloud II Wireless" }
        };

        public override string Id
        {
            get { return "hyperx"; }
        }

        public override string Label
        {
            get { return "HyperX HID"; }
        }

        public override bool Accepts(HidDeviceInfo info)
        {
            return info != null
                && info.VendorId == HyperXVendorId
                && Models.ContainsKey(info.ProductId)
                && info.UsagePage == UsagePageWanted
                && info.Usage == UsageWanted;
        }

        public override int Rank(HidDeviceInfo info)
        {
            return 0;
        }

        public override string NameFor(HidDeviceInfo info)
        {
            string name;
            if (info != null && Models.TryGetValue(info.ProductId, out name))
            {
                return name;
            }
            return "HyperX 无线耳机";
        }

        public override string GroupKey(HidDeviceInfo info)
        {
            // 有线与接收器是两个 PID，但只有一只耳机。
            return NameFor(info);
        }

        public override DeviceCategory CategoryFor(HidDeviceInfo info)
        {
            return DeviceCategory.Headset;
        }

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            HidConnection connection = new HidConnection();
            try
            {
                if (!connection.Open(info.Path))
                {
                    return Failure("无法打开设备（可能被 NGENUITY 占用）");
                }

                int level;
                byte[] first;
                if (!QueryLevel(connection, info, timeoutMs, out level, out first))
                {
                    return Failure("耳机没有回应电量请求（可能已关机或休眠）");
                }

                bool charging = false;
                byte[] chargeReply;
                int chargeState;
                if (QueryCharging(connection, info, timeoutMs, out chargeState, out chargeReply))
                {
                    charging = chargeState == 1;
                }

                return Success(level, charging, Hex(first, 0, 10));
            }
            finally
            {
                connection.Dispose();
            }
        }

        private static bool QueryLevel(HidConnection connection, HidDeviceInfo info, int timeoutMs,
            out int level, out byte[] reply)
        {
            level = 0;
            reply = null;
            int length = Math.Max(RequestLength, (int)info.OutputReportLength);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (!Write(connection, length, CommandLevel))
                {
                    return false;
                }
                Thread.Sleep(100);
                byte[] buffer = new byte[Math.Max(ReplyLength, (int)info.InputReportLength)];
                int read;
                if (!connection.Read(buffer, Math.Max(300, Math.Min(timeoutMs, 1000)), out read) || read < 8)
                {
                    continue;
                }
                if (!Echo(buffer, CommandLevel))
                {
                    continue;
                }
                int value = buffer[LevelIndex];
                if (value < 0 || value > 100)
                {
                    continue;
                }
                level = value;
                reply = buffer;
                return true;
            }
            return false;
        }

        private static bool QueryCharging(HidConnection connection, HidDeviceInfo info, int timeoutMs,
            out int state, out byte[] reply)
        {
            state = -1;
            reply = null;
            int length = Math.Max(RequestLength, (int)info.OutputReportLength);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!Write(connection, length, CommandCharging))
                {
                    return false;
                }
                Thread.Sleep(100);
                byte[] buffer = new byte[Math.Max(ReplyLength, (int)info.InputReportLength)];
                int read;
                if (!connection.Read(buffer, Math.Max(300, Math.Min(timeoutMs, 1000)), out read) || read < 5)
                {
                    continue;
                }
                if (!Echo(buffer, CommandCharging))
                {
                    continue;
                }
                state = buffer[ChargingIndex];
                reply = buffer;
                return true;
            }
            return false;
        }

        private static bool Write(HidConnection connection, int length, byte command)
        {
            byte[] request = new byte[length];
            request[0] = ReportId;
            request[1] = 0xFF;
            request[2] = 0xBB;
            request[3] = command;
            return connection.Write(request);
        }

        /// <summary>回报必须回显 06 ff bb &lt;命令&gt;，否则不是这次问答的答案。</summary>
        internal static bool Echo(byte[] reply, byte command)
        {
            return reply != null
                && reply.Length > 3
                && reply[0] == ReportId
                && reply[1] == 0xFF
                && reply[2] == 0xBB
                && reply[3] == command;
        }

        /// <summary>电量 0..100，超出范围当作没读到。</summary>
        internal static bool TryParseLevel(byte[] reply, out int level)
        {
            level = 0;
            if (!Echo(reply, CommandLevel) || reply.Length <= LevelIndex)
            {
                return false;
            }
            int value = reply[LevelIndex];
            if (value < 0 || value > 100)
            {
                return false;
            }
            level = value;
            return true;
        }
    }

    /// <summary>
    /// 华硕 ROG / TUF 无线鼠标（接收器或有线）：厂商集合上发 65 字节
    /// <c>00 12 07</c>，回报回显 <c>12 07</c>，电量在 +4、充电标志在 +9。
    /// 老型号的电量字节是 0..4 档，按 ×25 折算。
    /// </summary>
    internal sealed class AsusProtocol : VendorProtocol
    {
        private const ushort AsusVendorId = 0x0B05;
        private const int PacketLength = 65;
        private const byte LevelByte = 4;
        private const byte ChargingByte = 9;
        private const int StepScale = 25;
        private const int ReadAttempts = 8;
        private const int WriteAttempts = 3;

        private static readonly Dictionary<ushort, string> PercentModels = new Dictionary<ushort, string>
        {
            { 0x1A72, "ROG Gladius III Aimpoint" },
            { 0x1A70, "ROG Gladius III Aimpoint" },
            { 0x1B0C, "ROG Gladius III EVA 2" },
            { 0x1B0A, "ROG Gladius III EVA 2" },
            { 0x197F, "ROG Gladius III Wireless" },
            { 0x197D, "ROG Gladius III Wireless" },
            { 0x1A1A, "ROG Chakram X" },
            { 0x1A18, "ROG Chakram X" },
            { 0x1A94, "ROG Harpe Ace Aim Lab Edition" },
            { 0x1A92, "ROG Harpe Ace Aim Lab Edition" },
            { 0x1A68, "ROG Keris Wireless Aimpoint" },
            { 0x1A66, "ROG Keris Wireless Aimpoint" },
            { 0x1979, "ROG Spatha X" },
            { 0x1977, "ROG Spatha X" },
            { 0x19F4, "TUF Gaming M4 Wireless" },
            { 0x1A8D, "TX Gaming Mouse" },
            { 0x1AF5, "TX Gaming Mouse Mini" },
            { 0x1AF3, "TX Gaming Mouse Mini" },
            { 0x1C57, "TUF Gaming Mini Miku Edition" },
            { 0x1C56, "TUF Gaming Mini Miku Edition" }
        };

        private static readonly Dictionary<ushort, string> StepModels = new Dictionary<ushort, string>
        {
            { 0x18E5, "ROG Chakram" },
            { 0x18E3, "ROG Chakram" },
            { 0x1960, "ROG Keris Wireless" },
            { 0x195E, "ROG Keris Wireless" },
            { 0x1A59, "ROG Keris EVA Edition" },
            { 0x1A57, "ROG Keris EVA Edition" },
            { 0x1949, "ROG Strix Impact II Wireless" },
            { 0x1947, "ROG Strix Impact II Wireless" }
        };

        public override string Id
        {
            get { return "asus"; }
        }

        public override string Label
        {
            get { return "ASUS ROG HID"; }
        }

        public override bool Accepts(HidDeviceInfo info)
        {
            if (info == null || info.VendorId != AsusVendorId || info.UsagePage < 0xFF00)
            {
                return false;
            }
            return PercentModels.ContainsKey(info.ProductId) || StepModels.ContainsKey(info.ProductId);
        }

        public override int Rank(HidDeviceInfo info)
        {
            // 原实现在 interface 0 的厂商集合上发命令；这里没有接口号可用，
            // 只能优先挑报告长度最像 65 字节的那个集合。
            return (int)info.FeatureReportLength >= PacketLength ? 0 : 1;
        }

        public override string NameFor(HidDeviceInfo info)
        {
            string name;
            if (info != null && PercentModels.TryGetValue(info.ProductId, out name))
            {
                return name;
            }
            if (info != null && StepModels.TryGetValue(info.ProductId, out name))
            {
                return name;
            }
            return "ROG 无线鼠标";
        }

        public override string GroupKey(HidDeviceInfo info)
        {
            // 有线与接收器是同一只鼠标，合并成一张卡片。
            return NameFor(info);
        }

        public override DeviceCategory CategoryFor(HidDeviceInfo info)
        {
            return DeviceCategory.Mouse;
        }

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            HidConnection connection = new HidConnection();
            try
            {
                if (!connection.Open(info.Path))
                {
                    return Failure("无法打开设备（可能被 Armoury Crate 占用）");
                }

                bool stepScale = StepModels.ContainsKey(info.ProductId);
                // 发命令前先把队列里残留的按键/配置报文丢掉，和 G-Helper 一样。
                byte[] drain = new byte[PacketLength];
                int drained;
                for (int i = 0; i < 4; i++)
                {
                    if (!connection.Read(drain, 20, out drained) || drained <= 0)
                    {
                        break;
                    }
                }

                int length = Math.Max(PacketLength, (int)info.OutputReportLength);
                byte[] request = new byte[length];
                request[0] = 0x00;
                request[1] = 0x12;
                request[2] = 0x07;

                for (int attempt = 0; attempt < WriteAttempts; attempt++)
                {
                    if (!connection.Write(request))
                    {
                        return Failure("写入鼠标失败");
                    }

                    for (int read = 0; read < ReadAttempts; read++)
                    {
                        byte[] reply = new byte[Math.Max(PacketLength, (int)info.InputReportLength)];
                        int got;
                        if (!connection.Read(reply, 300, out got) || got <= 0)
                        {
                            break;      // 超时：把请求再发一次
                        }
                        if (IsError(reply))
                        {
                            return Failure("鼠标不认这条电量命令");
                        }
                        if (IsAllZero(reply))
                        {
                            return Failure("鼠标已关机或休眠");
                        }
                        int offset = EchoOffset(reply);
                        if (offset < 0)
                        {
                            continue;   // 按键或配置事件，跳过
                        }
                        int raw = reply[offset + LevelByte];
                        bool charging = reply[offset + ChargingByte] != 0;
                        if (raw == 0 && !charging)
                        {
                            return Failure("鼠标处于待机，未给出电量");
                        }
                        if (stepScale)
                        {
                            if (raw > 4)
                            {
                                return Failure("电量档位超出范围");
                            }
                            int steps = raw * StepScale;
                            string note = "约 " + steps.ToString(CultureInfo.InvariantCulture) + "%"
                                + (charging ? "，充电中" : "");
                            return Success(steps, charging, note);
                        }
                        if (raw > 100)
                        {
                            return Failure("电量超出 100%，不予采信");
                        }
                        return Success(raw, charging, Hex(reply, 0, 12));
                    }
                }

                return Failure("没有收到回显 12 07 的报文");
            }
            finally
            {
                connection.Dispose();
            }
        }

        /// <summary>回显位置：0（无报告号）或 1（报告号在最前），-1 表示不是电量回包。</summary>
        internal static int EchoOffset(byte[] reply)
        {
            if (reply == null || reply.Length < 3)
            {
                return -1;
            }
            if (reply[0] == 0x12 && reply[1] == 0x07)
            {
                return 0;
            }
            if (reply[0] == 0x00 && reply[1] == 0x12 && reply[2] == 0x07)
            {
                return 1;
            }
            return -1;
        }

        /// <summary>鼠标不认识这条命令时的回包：ff aa（报告号可能在最前）。</summary>
        internal static bool IsError(byte[] reply)
        {
            if (reply == null || reply.Length < 3)
            {
                return false;
            }
            if (reply[0] == 0xFF && reply[1] == 0xAA)
            {
                return true;
            }
            return reply[0] == 0x00 && reply[1] == 0xFF && reply[2] == 0xAA;
        }

        internal static bool IsAllZero(byte[] reply)
        {
            if (reply == null)
            {
                return true;
            }
            for (int i = 0; i < reply.Length; i++)
            {
                if (reply[i] != 0)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>按型号量纲解析：百分比型号直接取值，档位型号 ×25。</summary>
        internal static bool TryParse(byte[] reply, bool stepScale, out int level, out bool charging)
        {
            level = 0;
            charging = false;
            int offset = EchoOffset(reply);
            if (offset < 0 || reply.Length < offset + ChargingByte + 1)
            {
                return false;
            }
            int raw = reply[offset + LevelByte];
            bool chargingFlag = reply[offset + ChargingByte] != 0;
            if (raw == 0 && !chargingFlag)
            {
                return false;
            }
            if (stepScale)
            {
                if (raw > 4)
                {
                    return false;
                }
                level = raw * StepScale;
                charging = chargingFlag;
                return true;
            }
            if (raw > 100)
            {
                return false;
            }
            level = raw;
            charging = chargingFlag;
            return true;
        }
    }

    /// <summary>
    /// JBL Quantum 910 无线：耳机自己往接收器的厂商集合 ff13:0001 推报文，
    /// 报告号 0x08 的第 1 字节就是电量，没有请求可发，也没有充电标志。
    /// 原实现常驻线程一直监听；这里在每次查询时短时间监听，读到就返回。
    /// </summary>
    internal sealed class JblProtocol : VendorProtocol
    {
        private const ushort JblVendorId = 0x0ECB;
        private const ushort Quantum910 = 0x2088;
        private const ushort UsagePageWanted = 0xFF13;
        private const byte ReportIdBattery = 0x08;
        private const byte ReportIdMute = 0x2F;
        private const byte ReportIdPower = 0x09;
        private const int LevelIndex = 1;
        private const int ListenMs = 1200;
        private const int SliceMs = 250;

        public override string Id
        {
            get { return "jbl"; }
        }

        public override string Label
        {
            get { return "JBL HID"; }
        }

        public override bool Accepts(HidDeviceInfo info)
        {
            return info != null
                && info.VendorId == JblVendorId
                && info.ProductId == Quantum910
                && info.UsagePage == UsagePageWanted;
        }

        public override int Rank(HidDeviceInfo info)
        {
            return 0;
        }

        public override string NameFor(HidDeviceInfo info)
        {
            return "JBL Quantum 910 Wireless";
        }

        public override DeviceCategory CategoryFor(HidDeviceInfo info)
        {
            return DeviceCategory.Headset;
        }

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            HidConnection connection = new HidConnection();
            try
            {
                if (!connection.OpenRead(info.Path))
                {
                    return Failure("无法打开设备（可能被 QuantumENGINE 占用）");
                }

                int budget = Math.Max(ListenMs, Math.Min(timeoutMs, ListenMs));
                int deadline = Environment.TickCount + budget;
                while (Environment.TickCount < deadline)
                {
                    byte[] buffer = new byte[Math.Max(64, (int)info.InputReportLength)];
                    int read;
                    if (!connection.Read(buffer, SliceMs, out read) || read <= 0)
                    {
                        continue;
                    }
                    if (buffer[0] == ReportIdMute || buffer[0] == ReportIdPower)
                    {
                        continue;   // 静音状态、开关机状态，都不是电量
                    }
                    if (buffer[0] != ReportIdBattery)
                    {
                        continue;
                    }
                    int level = buffer[LevelIndex];
                    if (level < 0 || level > 100)
                    {
                        continue;
                    }
                    // 这一帧不带充电标志，充电状态留空而不是猜。
                    return Success(level, null, Hex(buffer, 0, 8));
                }

                return Failure("耳机这次没有播报电量（JBL 只在状态变化时推送）");
            }
            finally
            {
                connection.Dispose();
            }
        }

        /// <summary>电量帧：报告号 0x08，第 1 字节 = 电量。</summary>
        internal static bool TryParseLevel(byte[] reply, out int level)
        {
            level = 0;
            if (reply == null || reply.Length <= LevelIndex || reply[0] != ReportIdBattery)
            {
                return false;
            }
            int value = reply[LevelIndex];
            if (value < 0 || value > 100)
            {
                return false;
            }
            level = value;
            return true;
        }
    }
}
