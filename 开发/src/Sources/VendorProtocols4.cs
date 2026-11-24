using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeviceBattery
{
    // ======================================================================
    // 第三批厂商通道：2.4G 键盘
    // Evision（VGN / VXE / ATK）、AULA / MCHOSE "BY" 接收器、
    // MCHOSE GLW（3837:3033）、SONiX（AULA / AJAZZ 键盘）。
    //
    // 帧格式取自公开逆向资料（HaloBattery、Linux hid-pulsar、HeadsetControl、
    // 各厂商 HUB 的网页驱动），只取协议事实，代码按本项目的读法重写。
    // 每个协议都把解码做成纯静态函数，便于用录下来的帧做单元测试。
    // ======================================================================

    /// <summary>
    /// Evision 方案键盘：VGN S99 / V98 PRO / N75 PRO / VXE75 / V87、ATK V75X，
    /// 以及用同一接收器的 "Wireless Gaming Keyboard"。
    /// 请求 04 SL SH 1A VV 00…（32 字节，SL/SH 是序号），回包 04 SL SH 1A .. 00 00 .. PP ST。
    /// </summary>
    internal sealed class EvisionProtocol : VendorProtocol
    {
        private const ushort Vid = 0x320F;
        private const ushort ReceiverPid = 0x5088;
        private const ushort CablePid = 0x5055;
        private const ushort UsagePage = 0xFF1C;
        private const ushort Usage = 0x0092;
        private const byte Report = 0x04;
        private const byte CmdBattery = 0x1A;
        private const int FrameLength = 32;

        public override string Id { get { return "evision"; } }
        public override string Label { get { return "Evision 键盘 HID"; } }

        // 接收器不能一直被唤醒：最多每 10 分钟问一次，线缆也跟着这个下限，保守但安全。
        public override int MinQuerySeconds { get { return 600; } }

        public override bool Accepts(HidDeviceInfo info)
        {
            return info != null && info.VendorId == Vid &&
                   (info.ProductId == ReceiverPid || info.ProductId == CablePid) &&
                   info.UsagePage == UsagePage && info.Usage == Usage &&
                   info.OutputReportLength >= 10 && info.InputReportLength >= 10;
        }

        public override string NameFor(HidDeviceInfo info)
        {
            string name = ModelFor(info == null ? "" : info.ProductName);
            return name ?? "Evision 无线键盘";
        }

        public override DeviceCategory CategoryFor(HidDeviceInfo info) { return DeviceCategory.Keyboard; }

        /// <summary>产品名 -&gt; (显示名, 是否用 VGN 变体请求)。未知型号返回 null。</summary>
        internal static bool TryModel(string product, out string name, out bool vgn)
        {
            name = null;
            vgn = false;
            if (string.IsNullOrEmpty(product))
            {
                return false;
            }
            string p = product.Trim();
            if (string.Equals(p, "Wireless Gaming Keyboard", StringComparison.OrdinalIgnoreCase))
            {
                name = "Wireless Gaming Keyboard";
                return true;
            }
            string[] vgnModels = new string[]
            {
                "VGN S99", "VGN V98pro V2", "VGN V98 PRO", "VGN V98pro", "VGN N75 PRO",
                "VGN VXE75 V2", "VGN VXE75", "VXE V87", "VXE V87 RGB"
            };
            string[] normalModels = new string[] { "ATK V75X" };
            for (int i = 0; i < vgnModels.Length; i++)
            {
                if (string.Equals(p, vgnModels[i], StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p, vgnModels[i] + " 2.4G Dongle", StringComparison.OrdinalIgnoreCase))
                {
                    name = vgnModels[i];
                    vgn = true;
                    return true;
                }
            }
            for (int i = 0; i < normalModels.Length; i++)
            {
                if (string.Equals(p, normalModels[i], StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p, normalModels[i] + " 2.4G Dongle", StringComparison.OrdinalIgnoreCase))
                {
                    name = normalModels[i];
                    return true;
                }
            }
            return false;
        }

        private static string ModelFor(string product)
        {
            string name;
            bool vgn;
            return TryModel(product, out name, out vgn) ? name : null;
        }

        internal static byte[] BuildRequest(int seq, bool vgn)
        {
            byte[] frame = new byte[FrameLength];
            frame[0] = Report;
            frame[1] = (byte)(seq & 0xFF);
            frame[2] = (byte)((seq >> 8) & 0xFF);
            frame[3] = CmdBattery;
            frame[4] = vgn ? (byte)0x06 : (byte)0x00;
            return frame;
        }

        /// <summary>解析回包。返回 true 表示拿到电量，level/charging 有意义。</summary>
        internal static bool TryParse(byte[] r, int seq, out int level, out bool charging)
        {
            level = 0;
            charging = false;
            if (r == null || r.Length < 10)
            {
                return false;
            }
            if (r[0] != Report || r[1] != (byte)(seq & 0xFF) || r[2] != (byte)((seq >> 8) & 0xFF) ||
                r[3] != CmdBattery || r[5] != 0 || r[6] != 0)
            {
                return false;
            }
            if (r[8] > 100 || r[9] > 2)
            {
                return false;
            }
            level = r[8];
            charging = r[9] != 0;
            return true;
        }

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            if (info == null)
            {
                return Failure("设备已移除");
            }
            string name;
            bool vgn;
            if (!TryModel(info.ProductName, out name, out vgn))
            {
                return Failure("未知 Evision 键盘型号：" + (info.ProductName ?? ""));
            }
            HidConnection connection = new HidConnection();
            try
            {
                if (!connection.Open(info.Path))
                {
                    return Failure("无法打开键盘（可能被厂商软件占用）");
                }
                int seq = (Environment.TickCount & 0xFFFF) | 1;
                byte[] request = BuildRequest(seq, vgn);
                connection.Flush();
                if (!connection.Write(request))
                {
                    return Failure("写入键盘失败");
                }
                int deadline = Environment.TickCount + Math.Max(800, timeoutMs);
                byte[] buffer = new byte[FrameLength];
                int read;
                while (Environment.TickCount < deadline)
                {
                    if (connection.Read(buffer, 150, out read) && read >= 10)
                    {
                        int level;
                        bool charging;
                        if (TryParse(buffer, seq, out level, out charging))
                        {
                            return Success(level, charging, VendorProtocol.Hex(buffer, 0, read));
                        }
                    }
                }
                return Failure("键盘没有回应电量（可能已休眠或关机）");
            }
            finally
            {
                connection.Dispose();
            }
        }
    }

    /// <summary>
    /// AULA / MCHOSE 键盘共用的 "BY" 接收器（3554:FA09 / 3554:FB40 / 41E4:2001）。
    /// 请求 13 4A PK 00…SS（20 字节，PK 0/1 交替），回包 13 CC .. LL ST .. SS。
    /// </summary>
    internal sealed class AulaKeyboardProtocol : VendorProtocol
    {
        private const int FrameLength = 20;
        private const byte Report = 0x13;
        private const byte CmdBattery = 0x4A;

        public override string Id { get { return "aulakbd"; } }
        public override string Label { get { return "AULA 键盘 HID"; } }

        public override int MinQuerySeconds { get { return 600; } }

        public override bool Accepts(HidDeviceInfo info)
        {
            if (info == null)
            {
                return false;
            }
            bool receiver = (info.VendorId == 0x3554 && (info.ProductId == 0xFA09 || info.ProductId == 0xFB40)) ||
                            (info.VendorId == 0x41E4 && info.ProductId == 0x2001);
            return receiver && (info.UsagePage == 0xFF02 || info.UsagePage == 0xFF04) &&
                   info.Usage == 0x0002 && info.InputReportLength >= FrameLength &&
                   info.OutputReportLength >= FrameLength;
        }

        public override string NameFor(HidDeviceInfo info)
        {
            string name = info == null ? "" : (info.ProductName ?? "");
            return string.IsNullOrEmpty(name) ? "AULA 无线键盘" : name;
        }

        public override DeviceCategory CategoryFor(HidDeviceInfo info) { return DeviceCategory.Keyboard; }

        private static byte Sum(byte[] b, int count)
        {
            byte sum = 0;
            for (int i = 0; i < count; i++)
            {
                sum = unchecked((byte)(sum + b[i]));
            }
            return sum;
        }

        internal static byte[] BuildRequest(int packets)
        {
            byte[] frame = new byte[FrameLength];
            frame[0] = Report;
            frame[1] = CmdBattery;
            frame[2] = (byte)(packets & 0xFF);
            frame[FrameLength - 1] = Sum(frame, FrameLength - 1);
            return frame;
        }

        internal static bool TryParse(byte[] r, out int level, out bool charging)
        {
            level = 0;
            charging = false;
            if (r == null || r.Length < FrameLength)
            {
                return false;
            }
            if (r[0] != Report || (r[1] & 0x7F) != CmdBattery)
            {
                return false;
            }
            // 全零载荷是请求的回显，不是电量回包。
            bool echo = true;
            for (int i = 4; i < FrameLength - 1; i++)
            {
                if (r[i] != 0)
                {
                    echo = false;
                    break;
                }
            }
            if (echo)
            {
                return false;
            }
            bool checksum = r[FrameLength - 1] == Sum(r, FrameLength - 1);
            // MCHOSE 单包帧把校验位留 0，用 byte2==1 / byte3==0 识别。
            bool mchose = r[FrameLength - 1] == 0 && r[2] == 1 && r[3] == 0;
            if (!checksum && !mchose)
            {
                return false;
            }
            if (r[5] < 1 || r[5] > 100)
            {
                return false;
            }
            level = r[5];
            charging = (r[6] >> 4) == 1;
            return true;
        }

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            if (info == null)
            {
                return Failure("设备已移除");
            }
            HidConnection connection = new HidConnection();
            try
            {
                if (!connection.Open(info.Path))
                {
                    return Failure("无法打开键盘（可能被厂商软件占用）");
                }
                int deadline = Environment.TickCount + Math.Max(1500, timeoutMs);
                int packets = 0;
                byte[] buffer = new byte[FrameLength];
                int read;
                while (Environment.TickCount < deadline)
                {
                    byte[] request = BuildRequest(packets & 1);
                    packets++;
                    connection.Flush();
                    if (!connection.Write(request))
                    {
                        return Failure("写入键盘失败");
                    }
                    int askDeadline = Environment.TickCount + 1300;
                    while (Environment.TickCount < askDeadline)
                    {
                        if (connection.Read(buffer, 100, out read) && read >= FrameLength)
                        {
                            int level;
                            bool charging;
                            if (TryParse(buffer, out level, out charging))
                            {
                                return Success(level, charging, VendorProtocol.Hex(buffer, 0, read));
                            }
                        }
                    }
                }
                return Failure("键盘没有回应电量（可能已休眠或关机）");
            }
            finally
            {
                connection.Dispose();
            }
        }
    }

    /// <summary>
    /// MCHOSE 机械键盘共用的 3837:3033 接收器。GLW 帧走 65 字节无编号集合，
    /// 请求功能配置读（命令 05、长度 56），回包 payload[32] 低 7 位是电量、最高位是充电。
    /// </summary>
    internal sealed class MchoseGlwProtocol : VendorProtocol
    {
        private const ushort Vid = 0x3837;
        private const ushort Pid = 0x3033;
        private const byte FlagRequest = 0x55;
        private const byte FlagReply = 0xAA;
        private const byte CmdFuncConfig = 0x05;
        private const int Payload = 8;
        private const int BatteryIndex = 32;
        private const int ReadSize = 56;

        public override string Id { get { return "mchoseglw"; } }
        public override string Label { get { return "MCHOSE 键盘 GLW"; } }

        public override int MinQuerySeconds { get { return 600; } }

        public override bool Accepts(HidDeviceInfo info)
        {
            if (info == null || info.VendorId != Vid || info.ProductId != Pid)
            {
                return false;
            }
            if (info.InputReportLength != 65 || info.OutputReportLength != 65)
            {
                return false;
            }
            return (info.UsagePage == 0x0001 && info.Usage == 0x0000) ||
                   (info.UsagePage == 0xFF31 && info.Usage == 0x0074);
        }

        public override string NameFor(HidDeviceInfo info)
        {
            string name = info == null ? "" : (info.ProductName ?? "").Trim();
            // "MCHOSE G75 V2 2.4G" -> "MCHOSE G75 V2"
            if (name.EndsWith(" 2.4G", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - 5).Trim();
            }
            return string.IsNullOrEmpty(name) ? "MCHOSE 键盘" : name;
        }

        public override DeviceCategory CategoryFor(HidDeviceInfo info) { return DeviceCategory.Keyboard; }

        internal static byte[] BuildRequest()
        {
            // 校验 = 长度 + 偏移(0)，读命令没有 payload。
            return new byte[] { 0x00, FlagRequest, CmdFuncConfig, 0x00,
                ReadSize, ReadSize, 0x00, 0x00, 0x00 };
        }

        /// <summary>r 是原始输入报告：无编号集合的首字节可能是 0（报告 ID），这里跳过。</summary>
        internal static bool TryParse(byte[] r, out int level, out bool charging)
        {
            level = 0;
            charging = false;
            if (r == null)
            {
                return false;
            }
            int start = r.Length > 8 && r[0] == 0 ? 1 : 0;
            int need = start + Payload + BatteryIndex + 1;
            if (r.Length < need)
            {
                return false;
            }
            if (r[start] != FlagReply || r[start + 1] != CmdFuncConfig ||
                r[start + 5] != 0 || r[start + 6] != 0)
            {
                return false;
            }
            int size = r[start + 4];
            if (size < BatteryIndex + 1 || start + Payload + size > r.Length)
            {
                return false;
            }
            byte sum = 0;
            for (int i = 4; i < 8; i++)
            {
                sum = unchecked((byte)(sum + r[start + i]));
            }
            for (int i = 0; i < size; i++)
            {
                sum = unchecked((byte)(sum + r[start + Payload + i]));
            }
            if (sum != r[start + 3])
            {
                return false;
            }
            byte b = r[start + Payload + BatteryIndex];
            level = b & 0x7F;
            charging = (b & 0x80) != 0;
            return level <= 100;
        }

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            if (info == null)
            {
                return Failure("设备已移除");
            }
            HidConnection connection = new HidConnection();
            try
            {
                if (!connection.Open(info.Path))
                {
                    return Failure("无法打开键盘（可能被厂商软件占用）");
                }
                byte[] request = BuildRequest();
                byte[] buffer = new byte[Math.Max(65, (int)info.InputReportLength)];
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (attempt > 0)
                    {
                        System.Threading.Thread.Sleep(150);
                    }
                    connection.Flush();
                    if (!connection.Write(request))
                    {
                        continue;
                    }
                    int deadline = Environment.TickCount + Math.Max(500, timeoutMs);
                    int read;
                    while (Environment.TickCount < deadline)
                    {
                        if (connection.Read(buffer, 100, out read))
                        {
                            int level;
                            bool charging;
                            if (TryParse(buffer, out level, out charging))
                            {
                                return Success(level, charging, VendorProtocol.Hex(buffer, 0, read));
                            }
                        }
                    }
                }
                return Failure("键盘没有回应电量（可能已休眠或关机）");
            }
            finally
            {
                connection.Dispose();
            }
        }
    }

    /// <summary>
    /// SONiX 方案键盘：AULA F108 Pro、AJAZZ AK820 MAX / AK980 V2 等。
    /// 只在 USB 接口 3 的 FF60:0061 集合上应答，请求 20 01 00 …SS（33 字节）。
    /// </summary>
    internal sealed class SonixKeyboardProtocol : VendorProtocol
    {
        private const int Interface = 3;
        private const ushort UsagePage = 0xFF60;
        private const ushort Usage = 0x0061;
        private const int FrameLength = 33;

        public override string Id { get { return "sonixkbd"; } }
        public override string Label { get { return "SONiX 键盘 HID"; } }

        public override bool Accepts(HidDeviceInfo info)
        {
            if (info == null || info.InterfaceNumber != Interface ||
                info.UsagePage != UsagePage || info.Usage != Usage)
            {
                return false;
            }
            bool receiver = (info.VendorId == 0x05AC && info.ProductId == 0x024F) ||
                            (info.VendorId == 0x0C45 && (info.ProductId == 0xFEFE || info.ProductId == 0xFDFD));
            return receiver && info.InputReportLength >= FrameLength && info.OutputReportLength >= FrameLength;
        }

        public override string NameFor(HidDeviceInfo info)
        {
            string name = info == null ? "" : (info.ProductName ?? "").Trim();
            if (string.IsNullOrEmpty(name) || name.EndsWith("dongle", StringComparison.OrdinalIgnoreCase))
            {
                return "无线键盘";
            }
            if (name.EndsWith(" 2.4G", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - 5).Trim();
            }
            return name;
        }

        public override DeviceCategory CategoryFor(HidDeviceInfo info) { return DeviceCategory.Keyboard; }

        internal static byte[] BuildRequest()
        {
            byte[] frame = new byte[FrameLength];
            frame[1] = 0x20;
            frame[2] = 0x01;
            byte sum = 0;
            for (int i = 1; i < FrameLength - 1; i++)
            {
                sum = unchecked((byte)(sum + frame[i]));
            }
            frame[FrameLength - 1] = sum;
            return frame;
        }

        /// <summary>回包 20 01 00 LL（无编号集合的首字节 0 会被跳过）。0 表示键盘关机/不在 2.4G 模式。</summary>
        internal static bool TryParse(byte[] r, out int level)
        {
            level = 0;
            if (r == null)
            {
                return false;
            }
            int start = r.Length > 4 && r[0] == 0 ? 1 : 0;
            if (r.Length < start + 4)
            {
                return false;
            }
            if (r[start] != 0x20 || r[start + 1] != 0x01 || r[start + 2] != 0x00)
            {
                return false;
            }
            level = r[start + 3];
            return true;
        }

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            if (info == null)
            {
                return Failure("设备已移除");
            }
            HidConnection connection = new HidConnection();
            try
            {
                if (!connection.Open(info.Path))
                {
                    return Failure("无法打开键盘（可能被厂商软件占用）");
                }
                // 第一次请求可能因为无线链路刚唤醒而没有回应，再试一次。
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    byte[] request = BuildRequest();
                    connection.Flush();
                    if (!connection.Write(request))
                    {
                        return Failure("写入键盘失败");
                    }
                    int deadline = Environment.TickCount + Math.Max(500, timeoutMs);
                    byte[] buffer = new byte[FrameLength];
                    int read;
                    while (Environment.TickCount < deadline)
                    {
                        if (connection.Read(buffer, 100, out read))
                        {
                            int level;
                            if (TryParse(buffer, out level))
                            {
                                if (level < 1 || level > 100)
                                {
                                    return Failure("键盘已关机或不在 2.4G 模式");
                                }
                                return Success(level, null, VendorProtocol.Hex(buffer, 0, read));
                            }
                        }
                    }
                }
                return Failure("键盘没有回应电量（可能已休眠或关机）");
            }
            finally
            {
                connection.Dispose();
            }
        }
    }
}
