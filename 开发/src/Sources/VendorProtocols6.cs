using System;
using System.Collections.Generic;
using System.Threading;

namespace DeviceBattery
{
    // ======================================================================
    // 第五批厂商通道：2.4G 鼠标
    // AULA 8K、Endgame Gear 4K、ZOWIE DW、Areson / Redragon。
    // 帧格式取自 HaloBattery、vole、zowie-battery-tray、OpenRazer、
    // m913-ctl 等公开逆向资料，只取协议事实，用本项目的 C# 5 读法重写。
    // CompX report 8 的新增型号直接扩展 PulsarProtocol.Models（同一帧格式）。
    // ======================================================================

    /// <summary>
    /// AULA 8K 接收器：FF1C:0092 集合上的 64 字节 report 04。
    /// 每次读是一个小事务：START(01) → 电量(1A) → END(02)，END 无论如何都发。
    /// </summary>
    internal sealed class AulaMouseProtocol : VendorProtocol
    {
        private const ushort Vid = 0x320F;
        private const ushort Pid = 0x22A0;
        private const byte Report = 0x04;
        private const byte CmdStart = 0x01;
        private const byte CmdEnd = 0x02;
        private const byte CmdBattery = 0x1A;

        public override string Id { get { return "aulamouse"; } }
        public override string Label { get { return "AULA 鼠标 HID"; } }
        public override bool Accepts(HidDeviceInfo info)
        {
            return info != null && info.VendorId == Vid && info.ProductId == Pid &&
                   info.UsagePage == 0xFF1C && info.Usage == 0x0092 &&
                   info.InputReportLength >= 64 && info.OutputReportLength >= 64;
        }
        public override string NameFor(HidDeviceInfo info) { return "AULA 无线鼠标"; }
        public override DeviceCategory CategoryFor(HidDeviceInfo info) { return DeviceCategory.Mouse; }

        internal static byte[] BuildRequest(byte cmd)
        {
            byte[] r = new byte[64];
            r[0] = Report;
            r[3] = cmd;
            return r;
        }

        internal enum FrameKind { None, Reply, Battery, Link }
        internal static FrameKind Decode(byte[] r, out int value)
        {
            value = 0;
            if (r == null) return FrameKind.None;
            // 链路推送：04 AA 00 AA 00 00 00 00 SS，SS FF 表示鼠标在线。
            if (r.Length >= 9 && r[0] == Report && r[1] == 0xAA && r[2] == 0 &&
                r[3] == 0xAA && (r[8] == 0 || r[8] == 0xFF))
            {
                value = r[8] == 0xFF ? 1 : 0;
                return FrameKind.Link;
            }
            if (r.Length < 8 || r[0] != Report || r[1] != 0 || r[2] != 0 ||
                r[4] != 0 || r[5] != 0 || r[6] != 0 || r[7] != 0)
            {
                return FrameKind.None;
            }
            if (r[3] != CmdBattery) { value = r[3]; return FrameKind.Reply; }
            if (r.Length < 10) return FrameKind.None;
            if (r[8] < 1 || r[8] > 100 || r[9] > 2) return FrameKind.Battery; // 值为 0：暂无读数
            value = r[8] | ((r[9] != 0) ? 0x100 : 0);
            return FrameKind.Battery;
        }

        private static bool WaitReply(HidConnection c, byte cmd, byte[] buf, int ms, ref bool linkDown)
        {
            int deadline = Environment.TickCount + ms;
            while (Environment.TickCount < deadline)
            {
                int read;
                if (c.Read(buf, Math.Min(100, ms), out read))
                {
                    int value;
                    FrameKind f = Decode(buf, out value);
                    if (f == FrameKind.Link) { linkDown = value == 0; continue; }
                    if (f == FrameKind.Reply && value == cmd) return true;
                    if (f == FrameKind.Battery && cmd == CmdBattery && value != 0)
                    {
                        _lastLevel = value & 0xFF;
                        _lastCharging = (value & 0x100) != 0;
                        return true;
                    }
                }
            }
            return false;
        }

        // 事务中的中间状态：只在这次 Read 调用期间有效，不需要持久化。
        [ThreadStatic] private static int _lastLevel;
        [ThreadStatic] private static bool _lastCharging;

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            if (info == null) return Failure("设备已移除");
            HidConnection c = new HidConnection();
            try
            {
                if (!c.Open(info.Path)) return Failure("无法打开鼠标");
                _lastLevel = 0;
                c.Flush();
                bool linkDown = false;
                bool answered = false;
                if (c.Write(BuildRequest(CmdStart)) && WaitReply(c, CmdStart, new byte[64], 450, ref linkDown))
                {
                    answered = true;
                    if (c.Write(BuildRequest(CmdBattery)))
                    {
                        WaitReply(c, CmdBattery, new byte[64], 350, ref linkDown);
                    }
                }
                // END 必须发出去，无论前面是否成功，避免事务留在接收器里。
                if (c.Write(BuildRequest(CmdEnd))) WaitReply(c, CmdEnd, new byte[64], 200, ref linkDown);
                if (!answered || linkDown) return Failure("鼠标已关机或不在接收器上");
                if (_lastLevel < 1 || _lastLevel > 100) return Failure("接收器还没有读到电量");
                return Success(_lastLevel, _lastCharging, "level=" + _lastLevel);
            }
            finally { c.Dispose(); }
        }
    }

    /// <summary>
    /// Endgame Gear 4K 鼠标：FF01:0002 集合上的 feature A1（64 字节）。
    /// SetFeature 发命令、等一下、GetFeature 读回；状态 03 表示忙要重读，08/11 表示休眠。
    /// </summary>
    internal sealed class Endgame4KProtocol : VendorProtocol
    {
        private const byte Report = 0xA1;
        private const byte CmdBattery = 0xB4;
        private const byte CmdInfo = 0x0E;
        private const byte CmdProbe = 0x0F;
        private const byte StatusOk = 0x01;
        private const byte StatusBusy = 0x03;
        private static readonly Dictionary<ushort, string> Models = new Dictionary<ushort, string>
        {
            { 0x1972, "Endgame Gear OP1w 4k" },
            { 0x1984, "Endgame Gear OP1w 4k v2" },
            { 0x1968, "Endgame Gear XM2w 4k" },
            { 0x1982, "Endgame Gear XM2w 4k v2" }
        };

        public override string Id { get { return "endgame4k"; } }
        public override string Label { get { return "Endgame Gear 4K HID"; } }
        public override bool Accepts(HidDeviceInfo info)
        {
            return info != null && info.VendorId == 0x3367 && info.ProductId == 0x1970 &&
                   info.UsagePage == 0xFF01 && info.Usage == 0x0002 && info.FeatureReportLength >= 64;
        }
        public override string NameFor(HidDeviceInfo info) { return "Endgame Gear 4K 鼠标"; }
        public override DeviceCategory CategoryFor(HidDeviceInfo info) { return DeviceCategory.Mouse; }

        internal static byte[] BuildRequest(byte cmd)
        {
            byte[] r = new byte[64];
            r[0] = Report; r[1] = cmd; r[2] = 0x00;
            return r;
        }

        internal enum Battery { None, Level, Asleep }
        internal static Battery ParseBattery(byte[] r, out int level)
        {
            level = 0;
            if (r == null || r.Length < 20 || r[0] != Report) return Battery.None;
            if (r[1] == StatusOk && r[16] <= 100) { level = r[16]; return Battery.Level; }
            if (r[1] == 0x08 || r[1] == 0x11) return Battery.Asleep;
            return Battery.None;
        }

        internal static bool ParseInfo(byte[] r, out int mousePid)
        {
            mousePid = 0;
            if (r == null || r.Length < 20 || r[0] != Report || r[1] != StatusOk ||
                r[16] != 0x67 || r[17] != 0x33) return false;
            mousePid = r[18] | (r[19] << 8);
            return true;
        }

        private static byte[] Command(HidConnection c, byte cmd, byte[] buf)
        {
            if (!c.SetFeature(BuildRequest(cmd))) return null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Thread.Sleep(150 + attempt * 100);
                buf[0] = Report;
                if (!c.GetFeature(buf)) return null;
                if (buf.Length < 20 || buf[0] != Report) return null;
                if (buf[1] != StatusBusy) return buf;
            }
            return buf;
        }

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            if (info == null) return Failure("设备已移除");
            HidConnection c = new HidConnection();
            try
            {
                if (!c.OpenFeatureOnly(info.Path)) return Failure("无法打开鼠标");
                byte[] buf = new byte[64];
                byte[] reply = Command(c, CmdBattery, buf);
                int level;
                Battery b = ParseBattery(reply, out level);
                if (b == Battery.None)
                {
                    // 失败后探一次接收器和鼠标，再重试；probe 只在失败后发，
                    // 每次都发会让鼠标一直醒着。
                    Command(c, CmdProbe, buf);
                    reply = Command(c, CmdBattery, buf);
                    b = ParseBattery(reply, out level);
                }
                if (b == Battery.Asleep) return Failure("鼠标已休眠");
                if (b != Battery.Level) return Failure("鼠标没有回应电量");
                int pid;
                string name = "Endgame Gear 4K 鼠标";
                reply = Command(c, CmdInfo, buf);
                if (ParseInfo(reply, out pid))
                {
                    string model;
                    if (Models.TryGetValue((ushort)pid, out model)) name = model;
                }
                return Success(level, null, name + " level=" + level);
            }
            finally { c.Dispose(); }
        }
    }

    /// <summary>
    /// ZOWIE DW 系列：发请求在 FF03 集合（report 08），回电压在 FF04 集合（report 09）。
    /// 只有电压，需要用户开启「按电压估算」才换算成档位电量。
    /// </summary>
    internal sealed class ZowieProtocol : VendorProtocol
    {
        private const ushort Vid = 0x04A5;
        private const byte RequestReport = 0x08;
        private const byte ReplyReport = 0x09;

        public override string Id { get { return "zowie"; } }
        public override string Label { get { return "ZOWIE HID（电压）"; } }
        public override bool Accepts(HidDeviceInfo info)
        {
            return info != null && info.VendorId == Vid &&
                   (info.ProductId == 0x800A || info.ProductId == 0x8008) &&
                   info.InputReportLength >= 16 && info.OutputReportLength >= 16;
        }
        public override string NameFor(HidDeviceInfo info) { return "ZOWIE 无线鼠标"; }
        public override DeviceCategory CategoryFor(HidDeviceInfo info) { return DeviceCategory.Mouse; }

        internal static byte[] BuildRequest()
        {
            byte[] r = new byte[16];
            r[0] = RequestReport; r[1] = 0xB4; r[2] = 0x15;
            return r;
        }

        /// <summary>回包电压（mV）；0 表示鼠标没连上。 -1 表示不是回包。</summary>
        internal static int ParseVoltage(byte[] r)
        {
            if (r == null || r.Length < 5 || r[0] != ReplyReport || r[1] == 0xE6 || r[4] != 0xC8) return -1;
            return (r[1] * 256 + r[2]) * 10;
        }

        /// <summary>按鼠标自己的 LED 档位阈值换算（10 / 25 / 50%），仅用于用户开启电压估算时。</summary>
        private static readonly int[] VoltageTable = new int[] { 4200, 4000, 3890, 3760, 3680, 3500 };
        private static readonly int[] PercentTable = new int[] { 100, 75, 50, 25, 10, 0 };

        internal static int VoltagePercent(int mv)
        {
            // 按鼠标自己的 LED 阈值标定（来自 zowie-battery-tray 的实测），
            // 只用于用户显式开启「按电压估算」的场合。
            if (mv <= 0) return 0;
            return HeadsetMath.Interpolate(mv, VoltageTable, PercentTable);
        }

        public override BatteryReading Read(List<HidDeviceInfo> interfaces, int timeoutMs)
        {
            if (interfaces == null || interfaces.Count == 0) return Failure("设备已移除");
            HidDeviceInfo output = null, input = null;
            for (int i = 0; i < interfaces.Count; i++)
            {
                HidDeviceInfo info = interfaces[i];
                if (info == null) continue;
                if (info.UsagePage == 0xFF03) output = info;
                if (info.UsagePage == 0xFF04) input = info;
            }
            if (output == null || input == null) return Failure("没找到 ZOWIE 的请求/回报集合");
            HidConnection writer = new HidConnection();
            HidConnection reader = new HidConnection();
            try
            {
                if (!writer.Open(output.Path) || !reader.OpenRead(input.Path)) return Failure("无法打开鼠标");
                reader.Flush();
                if (!writer.Write(BuildRequest())) return Failure("写入鼠标失败");
                int deadline = Environment.TickCount + Math.Max(1500, timeoutMs);
                byte[] buf = new byte[16];
                int read;
                while (Environment.TickCount < deadline)
                {
                    if (reader.Read(buf, 150, out read))
                    {
                        int mv = ParseVoltage(buf);
                        if (mv == 0) return Failure("鼠标未连接");
                        if (mv > 0)
                        {
                            if (!AppSettings.Current.AllowVoltageEstimate)
                            {
                                BatteryReading unknown = Failure("只回报电压 " + mv +
                                    " mV；如需按标定档位显示，请到 设置→显示 开启「按电压估算」");
                                unknown.Millivolts = mv;
                                return unknown;
                            }
                            BatteryReading result = Success(VoltagePercent(mv), null, "mv=" + mv);
                            result.Millivolts = mv;
                            // 电压换算是标定档位，不是设备直接上报：置信度降级，卡片会标注。
                            result.Confidence = ReadingConfidence.Low;
                            return result;
                        }
                    }
                }
                return Failure("鼠标没有回应电压");
            }
            finally { writer.Dispose(); reader.Dispose(); }
        }

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            List<HidDeviceInfo> list = new List<HidDeviceInfo>();
            if (info != null) list.Add(info);
            return Read(list, timeoutMs);
        }
    }

    /// <summary>
    /// Areson / Redragon 接收器（25A7:FA07 / FA08）：请求走 FF02:0002 的 feature 集合，
    /// 回包走 FF01:0000 的 input 集合，17 字节，整帧和为 0x55。
    /// </summary>
    internal sealed class AresonProtocol : VendorProtocol
    {
        private const int Len = 17;
        public override string Id { get { return "areson"; } }
        public override string Label { get { return "Areson HID"; } }
        public override bool Accepts(HidDeviceInfo info)
        {
            return info != null && info.VendorId == 0x25A7 &&
                   (info.ProductId == 0xFA07 || info.ProductId == 0xFA08);
        }
        public override string NameFor(HidDeviceInfo info) { return "Redragon 无线鼠标"; }
        public override DeviceCategory CategoryFor(HidDeviceInfo info) { return DeviceCategory.Mouse; }

        internal static byte[] BuildRequest()
        {
            byte[] r = new byte[Len];
            r[0] = 0x08; r[1] = 0x04;
            int sum = 0;
            for (int i = 0; i < Len - 1; i++) sum += r[i];
            r[Len - 1] = (byte)((0x55 - sum) & 0xFF);
            return r;
        }

        private static int Sum(byte[] b)
        {
            int sum = 0;
            for (int i = 0; i < Len; i++) sum += b[i];
            return sum & 0xFF;
        }

        internal static bool TryParse(byte[] r, out int level, out bool charging)
        {
            level = 0; charging = false;
            if (r == null || r.Length < Len || Sum(r) != 0x55 ||
                r[0] != 0x09 || r[1] != 0x04 || r[2] != 0x00 || r[6] > 10) return false;
            level = r[6] * 10;
            charging = r[7] != 0;
            return true;
        }

        public override BatteryReading Read(List<HidDeviceInfo> interfaces, int timeoutMs)
        {
            if (interfaces == null || interfaces.Count == 0) return Failure("设备已移除");
            HidDeviceInfo feature = null, input = null;
            for (int i = 0; i < interfaces.Count; i++)
            {
                HidDeviceInfo info = interfaces[i];
                if (info == null) continue;
                if (info.UsagePage >= 0xFF00 && info.FeatureReportLength >= Len) feature = info;
                if (info.UsagePage == 0xFF01 || info.UsagePage < 0xFF00) input = info;
            }
            if (feature == null) feature = interfaces[0];
            if (input == null) input = interfaces[0];
            HidConnection writer = new HidConnection();
            HidConnection reader = new HidConnection();
            try
            {
                if (!writer.OpenFeatureOnly(feature.Path) || !reader.OpenRead(input.Path)) return Failure("无法打开鼠标");
                reader.Flush();
                byte[] request = BuildRequest();
                if (!writer.SetFeature(request)) return Failure("写入鼠标失败");
                int deadline = Environment.TickCount + Math.Max(1800, timeoutMs);
                byte[] buf = new byte[Len];
                int read;
                while (Environment.TickCount < deadline)
                {
                    if (reader.Read(buf, 150, out read))
                    {
                        int level; bool charging;
                        if (TryParse(buf, out level, out charging))
                            return Success(level, charging, VendorProtocol.Hex(buf, 0, read));
                    }
                }
                return Failure("鼠标没有回应电量（可能已关机）");
            }
            finally { writer.Dispose(); reader.Dispose(); }
        }

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            List<HidDeviceInfo> list = new List<HidDeviceInfo>();
            if (info != null) list.Add(info);
            return Read(list, timeoutMs);
        }
    }
}
