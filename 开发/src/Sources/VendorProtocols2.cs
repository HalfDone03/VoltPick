using System;
using System.Collections.Generic;
using System.Threading;

namespace DeviceBattery
{
    // ======================================================================
    // 国产 2.4G 鼠标的几条常见私有通道。协议同样来自公开逆向结果
    // （HaloBattery 的 wlmouse/lamzu/gwolves/mchose/pulsar 适配器）。
    // 共同原则：命令、长度、校验、取值范围逐项核对，任何一项不符就当没读到。
    // ======================================================================

    /// <summary>
    /// WLmouse / Lamzu / G-Wolves 共用的 0x83 查询：feature 报文发出去，
    /// 回包里找 a1|a2 00 02 02 00 83 &lt;充电&gt; &lt;电量&gt;。
    /// G-Wolves 早期固件用的是 00 02 8F 那一条，作为回退。
    /// </summary>
    internal sealed class Query83Protocol : VendorProtocol
    {
        private static readonly ushort[] VendorIds = new ushort[] { 0x36A7, 0x373E, 0x33E4 };

        private static readonly Dictionary<ushort, Dictionary<ushort, string>> Names =
            new Dictionary<ushort, Dictionary<ushort, string>>
            {
                { 0x36A7, new Dictionary<ushort, string>
                    {
                        { 0xA887, "WLmouse Beast X" },
                        { 0xA868, "WLmouse Beast X Mini Pro" },
                        { 0xA880, "WLmouse Beast X Max" }
                    }
                }
            };

        public override string Id
        {
            get { return "query83"; }
        }

        public override string Label
        {
            get { return "2.4G 私有协议"; }
        }

        public override bool Accepts(HidDeviceInfo info)
        {
            if (info == null)
            {
                return false;
            }
            bool known = false;
            for (int i = 0; i < VendorIds.Length; i++)
            {
                if (VendorIds[i] == info.VendorId)
                {
                    known = true;
                    break;
                }
            }
            if (!known)
            {
                return false;
            }
            // 只认厂商集合：标准鼠标集合不接受 feature 报文。
            return info.UsagePage >= 0xFF00 || info.FeatureReportLength >= 65;
        }

        public override int Rank(HidDeviceInfo info)
        {
            if (info.UsagePage == 0xFFFF)
            {
                return 0;
            }
            if (info.UsagePage >= 0xFF00)
            {
                return 1;
            }
            return 2;
        }

        public override string NameFor(HidDeviceInfo info)
        {
            Dictionary<ushort, string> table;
            string name;
            if (Names.TryGetValue(info.VendorId, out table) && table.TryGetValue(info.ProductId, out name))
            {
                return name;
            }
            if (!string.IsNullOrEmpty(info.ProductName))
            {
                return info.ProductName;
            }
            if (info.VendorId == 0x36A7)
            {
                return "WLmouse 无线鼠标";
            }
            if (info.VendorId == 0x373E)
            {
                return "LAMZU 无线鼠标";
            }
            return "G-Wolves 无线鼠标";
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
                    return Failure("无法打开设备（可能被厂商软件占用）");
                }

                int length = Math.Max(65, (int)info.FeatureReportLength);
                byte[] request = new byte[length];
                request[0] = 0x00;
                request[1] = 0x00;
                request[2] = 0x00;
                request[3] = 0x02;
                request[4] = 0x02;
                request[5] = 0x00;
                request[6] = 0x83;

                if (Native.HidD_SetFeature(connection.Handle, request, request.Length))
                {
                    int deadline = Environment.TickCount + Math.Max(300, timeoutMs);
                    while (Environment.TickCount < deadline)
                    {
                        Thread.Sleep(50);
                        byte[] reply = new byte[length];
                        reply[0] = 0x00;
                        if (!Native.HidD_GetFeature(connection.Handle, reply, reply.Length))
                        {
                            continue;
                        }
                        int level;
                        bool charging;
                        if (ParseFeature(reply, out level, out charging))
                        {
                            return Success(level, charging, Hex(reply, 0, 12));
                        }
                        if (ParseOld(reply, out level, out charging))
                        {
                            return Success(level, charging, Hex(reply, 0, 12));
                        }
                    }
                }

                // G-Wolves 早期固件：00 02 8F 那一条。
                byte[] old = new byte[length];
                old[0] = 0x00;
                old[1] = 0x00;
                old[2] = 0x02;
                old[3] = 0x8F;
                old[4] = 0x01;
                if (Native.HidD_SetFeature(connection.Handle, old, old.Length))
                {
                    int deadline = Environment.TickCount + Math.Max(300, timeoutMs);
                    while (Environment.TickCount < deadline)
                    {
                        Thread.Sleep(50);
                        byte[] reply = new byte[length];
                        reply[0] = 0x00;
                        if (!Native.HidD_GetFeature(connection.Handle, reply, reply.Length))
                        {
                            continue;
                        }
                        int level;
                        bool charging;
                        if (ParseOld(reply, out level, out charging))
                        {
                            return Success(level, charging, Hex(reply, 0, 12));
                        }
                    }
                }

                return Failure("鼠标没有响应电量请求（可能已关机或休眠）");
            }
            finally
            {
                connection.Dispose();
            }
        }

        /// <summary>新协议回包：a1|a2 00 02 02 00 83 &lt;充电&gt; &lt;电量&gt;。</summary>
        internal static bool ParseFeature(byte[] reply, out int level, out bool charging)
        {
            level = 0;
            charging = false;
            if (reply == null)
            {
                return false;
            }
            for (int i = 5; i < reply.Length - 2; i++)
            {
                if (reply[i] != 0x83 || reply[i - 1] != 0x00 || reply[i - 2] != 0x02)
                {
                    continue;
                }
                if (reply[i - 5] != 0xA1 && reply[i - 5] != 0xA2)
                {
                    continue;
                }
                int battery = reply[i + 2];
                if (battery < 0 || battery > 100)
                {
                    return false;
                }
                level = battery;
                charging = reply[i + 1] != 0;
                return true;
            }
            return false;
        }

        /// <summary>G-Wolves 旧协议回包：a1 02 8f &lt;?&gt; &lt;充电&gt; &lt;电量&gt;。</summary>
        internal static bool ParseOld(byte[] reply, out int level, out bool charging)
        {
            level = 0;
            charging = false;
            if (reply == null)
            {
                return false;
            }
            for (int offset = 0; offset <= 1; offset++)
            {
                if (reply.Length <= offset + 5)
                {
                    continue;
                }
                if (reply[offset] != 0xA1 || reply[offset + 1] != 0x02 || reply[offset + 2] != 0x8F)
                {
                    continue;
                }
                int battery = reply[offset + 5];
                if (battery < 0 || battery > 100)
                {
                    return false;
                }
                level = battery;
                charging = reply[offset + 4] != 0;
                return true;
            }
            return false;
        }
    }

    /// <summary>MCHOSE 0x5253 / 0x3837：0x11 / 0x12 feature 报文，载荷整段取反。</summary>
    internal sealed class MchoseConfigProtocol : VendorProtocol
    {
        private const ushort ConfigPage = 0xFF01;
        private const byte CommandStatus = 0x06;
        private const byte ShortReport = 0x11;
        private const byte LongReport = 0x12;
        private const int ShortLength = 20;
        private const int LongLength = 64;

        public override string Id
        {
            get { return "mchose"; }
        }

        public override string Label
        {
            get { return "MCHOSE 私有协议"; }
        }

        public override bool Accepts(HidDeviceInfo info)
        {
            if (info == null)
            {
                return false;
            }
            if (info.VendorId != 0x5253 && info.VendorId != 0x3837)
            {
                return false;
            }
            return info.UsagePage >= 0xFF00;
        }

        public override int Rank(HidDeviceInfo info)
        {
            if (info.UsagePage == ConfigPage)
            {
                return 0;
            }
            return 1;
        }

        public override string NameFor(HidDeviceInfo info)
        {
            if (!string.IsNullOrEmpty(info.ProductName))
            {
                return info.ProductName;
            }
            return "MCHOSE 无线鼠标";
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
                    return Failure("无法打开设备（可能被 M HUB 占用）");
                }

                byte[] reports = new byte[] { ShortReport, LongReport };
                int[] lengths = new int[] { ShortLength, LongLength };
                for (int channel = 0; channel < reports.Length; channel++)
                {
                    int payloadLength = lengths[channel];
                    // 载荷要再取反一次：0x06 的补码是 0xF9，其余是 0xFF。
                    byte[] request = new byte[payloadLength + 1];
                    request[0] = reports[channel];
                    for (int i = 1; i < request.Length; i++)
                    {
                        request[i] = 0xFF;
                    }
                    request[1] = (byte)(CommandStatus ^ 0xFF);

                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        if (!Native.HidD_SetFeature(connection.Handle, request, request.Length))
                        {
                            break;
                        }
                        Thread.Sleep(120);
                        byte[] reply = new byte[payloadLength + 1];
                        reply[0] = reports[channel];
                        if (!Native.HidD_GetFeature(connection.Handle, reply, reply.Length))
                        {
                            continue;
                        }
                        int level;
                        bool charging;
                        if (ParseStatus(reply, out level, out charging))
                        {
                            return Success(level, charging, Hex(reply, 0, 16));
                        }
                    }
                }

                return Failure("接收器没有给出电量（鼠标可能已休眠）");
            }
            finally
            {
                connection.Dispose();
            }
        }

        /// <summary>回包：报告号、命令补码 0xF9、之后整段取反的载荷。</summary>
        internal static bool ParseStatus(byte[] reply, out int level, out bool charging)
        {
            level = 0;
            charging = false;
            if (reply == null || reply.Length < 13)
            {
                return false;
            }
            if ((reply[0] != ShortReport && reply[0] != LongReport) || reply[1] != (CommandStatus ^ 0xFF))
            {
                return false;
            }
            byte[] payload = new byte[reply.Length - 2];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(reply[i + 2] ^ 0xFF);
            }
            if (payload.Length < 11)
            {
                return false;
            }
            int vendor = payload[0] | (payload[1] << 8);
            if (vendor != 0x5253 && vendor != 0x3837)
            {
                return false;
            }
            int battery = payload[9];
            if (battery < 0 || battery > 100)
            {
                return false;
            }
            level = battery;
            charging = payload[10] != 0;
            return true;
        }
    }

    /// <summary>MCHOSE A5 Pro Max（0x2023）：0x83 feature 报文，回包以 a1 开头。</summary>
    internal sealed class MchoseA5Protocol : VendorProtocol
    {
        private const ushort A5VendorId = 0x2023;
        private const byte A5Page = 0x00;
        private const byte A5Command = 0x83;

        private static readonly Dictionary<ushort, string> Models = new Dictionary<ushort, string>
        {
            { 0xF013, "MCHOSE A5 Pro Max（1K 接收器）" },
            { 0xF015, "MCHOSE A5 Pro Max（4K 接收器）" },
            { 0xF019, "MCHOSE A5 Pro Max（有线）" }
        };

        public override string Id
        {
            get { return "mchose-a5"; }
        }

        public override string Label
        {
            get { return "MCHOSE 私有协议"; }
        }

        public override bool Accepts(HidDeviceInfo info)
        {
            return info != null && info.VendorId == A5VendorId && Models.ContainsKey(info.ProductId);
        }

        public override int Rank(HidDeviceInfo info)
        {
            return info.UsagePage == 0xFFFF ? 0 : 1;
        }

        public override string NameFor(HidDeviceInfo info)
        {
            string name;
            if (Models.TryGetValue(info.ProductId, out name))
            {
                return name;
            }
            return "MCHOSE 无线鼠标";
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
                    return Failure("无法打开设备（可能被 M HUB 占用）");
                }

                int length = Math.Max(65, (int)info.FeatureReportLength);
                byte[] request = new byte[length];
                request[0] = 0x00;
                request[1] = 0x00;
                request[2] = 0x00;
                request[3] = 0x02;
                request[4] = 0x02;
                request[5] = A5Page;
                request[6] = A5Command;

                if (!Native.HidD_SetFeature(connection.Handle, request, request.Length))
                {
                    return Failure("写入 MCHOSE 接收器失败");
                }

                Thread.Sleep(34);
                int deadline = Environment.TickCount + Math.Max(300, timeoutMs);
                while (Environment.TickCount < deadline)
                {
                    byte[] reply = new byte[length];
                    reply[0] = 0x00;
                    if (Native.HidD_GetFeature(connection.Handle, reply, reply.Length))
                    {
                        int level;
                        bool charging;
                        if (ParseA5(reply, out level, out charging))
                        {
                            return Success(level, charging, Hex(reply, 0, 12));
                        }
                    }
                    Thread.Sleep(30);
                }

                return Failure("接收器没有给出电量（鼠标可能已休眠）");
            }
            finally
            {
                connection.Dispose();
            }
        }

        /// <summary>回包：a1（报告号可能在最前）… 页码@+4、命令@+5、充电@+6、电量@+7。</summary>
        internal static bool ParseA5(byte[] reply, out int level, out bool charging)
        {
            level = 0;
            charging = false;
            if (reply == null || reply.Length < 9)
            {
                return false;
            }
            int at = reply[0] == 0xA1 ? 0 : (reply.Length > 1 && reply[1] == 0xA1 ? 1 : -1);
            if (at < 0 || reply.Length < at + 8)
            {
                return false;
            }
            if (reply[at + 4] != A5Page || reply[at + 5] != A5Command)
            {
                return false;
            }
            int battery = reply[at + 7];
            level = battery > 100 ? 100 : battery;
            charging = reply[at + 6] != 0;
            return true;
        }
    }

    /// <summary>MCHOSE G7（0xA8A5:0x2255）：输出报文一次，等 aa 30 开头的输入报文。</summary>
    internal sealed class MchoseG7Protocol : VendorProtocol
    {
        private const ushort G7VendorId = 0xA8A5;
        private const ushort G7ProductId = 0x2255;
        private const byte G7LevelByte = 8;
        private const byte G7ChargingByte = 9;

        public override string Id
        {
            get { return "mchose-g7"; }
        }

        public override string Label
        {
            get { return "MCHOSE 私有协议"; }
        }

        public override bool Accepts(HidDeviceInfo info)
        {
            return info != null && info.VendorId == G7VendorId && info.ProductId == G7ProductId;
        }

        public override int Rank(HidDeviceInfo info)
        {
            return info.UsagePage == 0xFF01 ? 0 : 1;
        }

        public override string NameFor(HidDeviceInfo info)
        {
            return string.IsNullOrEmpty(info.ProductName) ? "MCHOSE G7" : "MCHOSE G7（" + info.ProductName + "）";
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
                    return Failure("无法打开设备（可能被 M HUB 占用）");
                }

                int length = Math.Max(65, (int)info.OutputReportLength);
                byte[] request = new byte[length];
                request[0] = 0x00;
                request[1] = 0x55;
                request[2] = 0x30;
                request[3] = 0xA5;
                request[4] = 0x0B;
                request[5] = 0x2E;
                request[6] = 0x01;
                request[7] = 0x01;
                request[8] = 0x01;

                if (!connection.Write(request))
                {
                    return Failure("写入 MCHOSE 接收器失败");
                }

                int deadline = Environment.TickCount + Math.Max(300, timeoutMs);
                while (Environment.TickCount < deadline)
                {
                    byte[] reply = new byte[info.InputReportLength >= 12 ? info.InputReportLength : 64];
                    int read;
                    if (connection.Read(reply, 60, out read) && read > 0)
                    {
                        int level;
                        bool charging;
                        if (ParseG7(reply, read, out level, out charging))
                        {
                            return Success(level, charging, Hex(reply, 0, 12));
                        }
                    }
                    else
                    {
                        Thread.Sleep(20);
                    }
                }

                return Failure("接收器没有给出电量（鼠标可能已休眠）");
            }
            finally
            {
                connection.Dispose();
            }
        }

        /// <summary>回包以 aa 30 开头，电量在第 8 字节、充电标志第 9 字节。</summary>
        internal static bool ParseG7(byte[] reply, int length, out int level, out bool charging)
        {
            level = 0;
            charging = false;
            if (reply == null || length <= G7ChargingByte)
            {
                return false;
            }
            if (reply[0] != 0xAA || reply[1] != 0x30)
            {
                return false;
            }
            int battery = reply[G7LevelByte];
            if (battery < 0 || battery > 100)
            {
                return false;
            }
            level = battery;
            charging = reply[G7ChargingByte] != 0;
            return true;
        }
    }

    /// <summary>
    /// Pulsar / VXE / Hitscan：17 字节 08 04 帧，校验 = 85 - 前 16 字节之和。
    /// ATK 自己的接收器（0x373B）由专用通道读取，这里不重复认领，避免两张卡片。
    /// </summary>
    internal sealed class PulsarProtocol : VendorProtocol
    {
        private const byte ReportId = 0x08;
        private const byte CommandPower = 0x04;
        private const int FrameLength = 17;
        private const byte ChecksumBase = 0x55;
        private const int LevelIndex = 6;
        private const int PowerIndex = 7;

        private static readonly Dictionary<ushort, Dictionary<ushort, string>> Models =
            new Dictionary<ushort, Dictionary<ushort, string>>
            {
                { 0x3554, new Dictionary<ushort, string>
                    {
                        { 0xF508, "Pulsar X2 V2 Mini（无线）" },
                        { 0xF507, "Pulsar X2 V2 Mini（有线）" },
                        { 0xF58F, "ATK VXE R1 SE+（有线）" },
                        { 0xF58A, "VXE R1 Pro Max（2.4G）" },
                        { 0xF58C, "VXE R1 Pro Max（有线）" }
                    }
                },
                { 0x3770, new Dictionary<ushort, string>
                    {
                        { 0x0200, "Hitscan Hyperlight（2.4G）" },
                        { 0x0100, "Hitscan Hyperlight（有线）" }
                    }
                },
                { 0x3710, new Dictionary<ushort, string>
                    {
                        { 0x5406, "Pulsar X2 CrazyLight Mini（2.4G）" },
                        { 0x3414, "Pulsar X2 CrazyLight Mini（有线）" }
                    }
                }
            };

        public override string Id
        {
            get { return "pulsar"; }
        }

        public override string Label
        {
            get { return "Pulsar/VXE HID"; }
        }

        public override bool Accepts(HidDeviceInfo info)
        {
            if (info == null)
            {
                return false;
            }
            Dictionary<ushort, string> table;
            return Models.TryGetValue(info.VendorId, out table) && table.ContainsKey(info.ProductId);
        }

        public override int Rank(HidDeviceInfo info)
        {
            // OpenMouse 的 ATK/VXE 面板打开的是 0xFF02:0x0002 这个集合。
            if (info.UsagePage == 0xFF02 && info.Usage == 0x0002)
            {
                return 0;
            }
            return info.UsagePage >= 0xFF00 ? 1 : 2;
        }

        public override string NameFor(HidDeviceInfo info)
        {
            Dictionary<ushort, string> table;
            string name;
            if (Models.TryGetValue(info.VendorId, out table) && table.TryGetValue(info.ProductId, out name))
            {
                return name;
            }
            return "无线鼠标";
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
                    return Failure("无法打开设备（可能被厂商软件占用）");
                }

                byte[] request = BuildRequest();
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    if (!connection.Write(request))
                    {
                        return Failure("写入接收器失败");
                    }

                    int deadline = Environment.TickCount + Math.Max(250, timeoutMs);
                    while (Environment.TickCount < deadline)
                    {
                        byte[] reply = new byte[FrameLength];
                        int read;
                        if (!connection.Read(reply, 120, out read) || read < FrameLength)
                        {
                            continue;
                        }
                        int level;
                        bool charging;
                        if (ParsePower(reply, out level, out charging))
                        {
                            return Success(level, charging, Hex(reply, 0, FrameLength));
                        }
                    }
                }

                return Failure("接收器没有给出电量（鼠标可能已关机或休眠）");
            }
            finally
            {
                connection.Dispose();
            }
        }

        /// <summary>17 字节请求帧：报告号 08、命令 04、全零参数、末字节校验。</summary>
        internal static byte[] BuildRequest()
        {
            byte[] frame = new byte[FrameLength];
            frame[0] = ReportId;
            frame[1] = CommandPower;
            frame[FrameLength - 1] = Checksum(frame, FrameLength - 1);
            return frame;
        }

        /// <summary>校验：85 减去前 n 字节之和，取低 8 位。</summary>
        internal static byte Checksum(byte[] frame, int length)
        {
            int sum = 0;
            for (int i = 0; i < length; i++)
            {
                sum += frame[i];
            }
            return (byte)((ChecksumBase - sum) & 0xFF);
        }

        /// <summary>回包：08 04 开头、校验对得上、电量 0..100。</summary>
        internal static bool ParsePower(byte[] reply, out int level, out bool charging)
        {
            level = 0;
            charging = false;
            if (reply == null || reply.Length < FrameLength)
            {
                return false;
            }
            if (reply[0] != ReportId || reply[1] != CommandPower)
            {
                return false;
            }
            if (reply[FrameLength - 1] != Checksum(reply, FrameLength - 1))
            {
                return false;
            }
            int battery = reply[LevelIndex];
            if (battery < 0 || battery > 100)
            {
                return false;
            }
            level = battery;
            charging = reply[PowerIndex] != 0;
            return true;
        }
    }
}
