using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace DeviceBattery
{
    // ======================================================================
    // 第四批厂商通道：无线耳机
    // Corsair Void / Bragi、HyperX Cloud Flight / Stinger / Alpha、
    // Razer BlackShark V3 / V2 Pro (2023) / Barracuda X Chroma、罗技 G533。
    // 帧格式取自 HeadsetControl、OpenRazer、HaloBattery 等公开逆向资料，
    // 只取协议事实，代码按本项目的 C# 5 读法重写。解码全是纯静态函数，
    // 便于用录下来的真实回包做单元断言。
    // ======================================================================

    internal static class HeadsetMath
    {
        /// <summary>电压表插值：mv 落在两档之间时线性换算成电量百分比。</summary>
        internal static int Interpolate(int mv, int[] mvTable, int[] pctTable)
        {
            if (mv >= mvTable[0])
            {
                return pctTable[0];
            }
            for (int i = 1; i < mvTable.Length; i++)
            {
                if (mv >= mvTable[i])
                {
                    int span = Math.Max(1, mvTable[i - 1] - mvTable[i]);
                    return pctTable[i] + (pctTable[i - 1] - pctTable[i]) * (mv - mvTable[i]) / span;
                }
            }
            return pctTable[pctTable.Length - 1];
        }

        internal static byte Xor(byte[] b, int from, int count)
        {
            byte v = 0;
            for (int i = from; i < from + count; i++)
            {
                v = unchecked((byte)(v ^ b[i]));
            }
            return v;
        }

        internal static byte Sum(byte[] b, int from, int count)
        {
            byte v = 0;
            for (int i = from; i < from + count; i++)
            {
                v = unchecked((byte)(v + b[i]));
            }
            return v;
        }
    }

    /// <summary>Corsair Void 族无线耳机：报告 64 里带电量、连接与充电状态。</summary>
    internal sealed class CorsairVoidProtocol : VendorProtocol
    {
        private const ushort Vid = 0x1B1C;
        private const byte StatusReport = 0x64;
        private static readonly ushort[] Pids = new ushort[]
        {
            0x1B1C, 0x1B27, 0x0A14, 0x0A16, 0x0A17, 0x0A1D, 0x0A1A, 0x1B2A,
            0x1B23, 0x1B29, 0x0A55, 0x0A51, 0x0A52, 0x0A38, 0x0A4F, 0x0A2B, 0x0A75, 0x0A56
        };

        public override string Id { get { return "corsairvoid"; } }
        public override string Label { get { return "Corsair Void HID"; } }
        public override bool Accepts(HidDeviceInfo info)
        {
            if (info == null || info.VendorId != Vid || info.UsagePage != 0xFFC5 || info.Usage != 0x0001)
            {
                return false;
            }
            for (int i = 0; i < Pids.Length; i++)
            {
                if (Pids[i] == info.ProductId) return true;
            }
            return false;
        }
        public override string NameFor(HidDeviceInfo info) { return "Corsair 无线耳机"; }
        public override DeviceCategory CategoryFor(HidDeviceInfo info) { return DeviceCategory.Headset; }

        /// <summary>level = -1 表示没连上；full 表示已充满。</summary>
        internal static bool TryParse(byte[] r, out int level, out bool charging, out bool full, out bool linked)
        {
            level = 0; charging = false; full = false; linked = false;
            if (r == null || r.Length < 5 || r[0] != StatusReport)
            {
                return false;
            }
            if (r[3] == 49 || r[3] == 51 || r[3] == 52 || r[4] == 0)
            {
                return true; // 解析成功，但没连上
            }
            if (r[4] < 1 || r[4] > 5)
            {
                return false;
            }
            linked = true;
            level = Math.Min(100, r[2] & 0x7F);
            charging = r[4] == 4 || r[4] == 5;
            full = r[4] == 4;
            return true;
        }

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            HidConnection c = new HidConnection();
            try
            {
                if (!c.Open(info.Path)) return Failure("无法打开耳机");
                c.Flush();
                if (!c.Write(new byte[] { 0xC9, 0x64 })) return Failure("写入耳机失败");
                int deadline = Environment.TickCount + Math.Max(1000, timeoutMs);
                byte[] buf = new byte[Math.Max(32, (int)info.InputReportLength)];
                int read;
                while (Environment.TickCount < deadline)
                {
                    if (c.Read(buf, 100, out read))
                    {
                        int level; bool charging, full, linked;
                        if (TryParse(buf, out level, out charging, out full, out linked))
                        {
                            if (!linked) return Failure("耳机未连接到接收器");
                            return Success(level, full ? (bool?)true : charging, VendorProtocol.Hex(buf, 0, read));
                        }
                    }
                }
                return Failure("耳机没有回应电量");
            }
            finally { c.Dispose(); }
        }
    }

    /// <summary>Corsair Bragi 接收器（Virtuoso XT、HS80 MAX 等）。GET 属性 0F 电量、10 充电。</summary>
    internal sealed class CorsairBragiProtocol : VendorProtocol
    {
        private const ushort Vid = 0x1B1C;
        private const byte TargetHeadset = 0x09;
        private const byte PropBattery = 0x0F;
        private const byte PropCharge = 0x10;
        private static readonly ushort[] Receivers = new ushort[] { 0x0A64, 0x0A62, 0x0A3E, 0x0A3D, 0x0A97 };

        public override string Id { get { return "corsairbragi"; } }
        public override string Label { get { return "Corsair Bragi HID"; } }
        public override bool Accepts(HidDeviceInfo info)
        {
            if (info == null || info.VendorId != Vid || info.UsagePage != 0xFF42) return false;
            for (int i = 0; i < Receivers.Length; i++)
            {
                if (Receivers[i] == info.ProductId) return true;
            }
            return false;
        }
        public override string NameFor(HidDeviceInfo info) { return "Corsair 无线耳机"; }
        public override DeviceCategory CategoryFor(HidDeviceInfo info) { return DeviceCategory.Headset; }

        internal static byte[] BuildRequest(byte prop) { return new byte[] { 0x02, TargetHeadset, 0x02, prop }; }

        /// <summary>value = -1 表示设备回答了错误状态（没配对/关机）。</summary>
        internal static bool TryParse(byte[] r, byte source, out int value)
        {
            value = -1;
            if (r == null || r.Length < 8 || r[0] != 0x02 || r[1] != source || r[2] != 0x02) return false;
            if (r[3] != 0) return true;
            value = r[4] | (r[5] << 8) | (r[6] << 16) | (r[7] << 24);
            return true;
        }

        /// <summary>0..1000 的千分比电量换算成百分比。</summary>
        internal static bool TryPercent(int value, out int percent)
        {
            percent = 0;
            if (value < 0 || value > 1000) return false;
            percent = (value + 5) / 10;
            return true;
        }

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            HidConnection c = new HidConnection();
            try
            {
                if (!c.Open(info.Path)) return Failure("无法打开耳机");
                byte[] buf = new byte[Math.Max(64, (int)info.InputReportLength)];
                int rawLevel = -1, rawCharge = -1;
                int[] values = new int[] { rawLevel, rawCharge };
                byte[] props = new byte[] { PropBattery, PropCharge };
                for (int p = 0; p < props.Length; p++)
                {
                    c.Flush();
                    if (!c.Write(BuildRequest(props[p]))) continue;
                    int deadline = Environment.TickCount + Math.Max(400, timeoutMs);
                    int read;
                    while (Environment.TickCount < deadline)
                    {
                        if (c.Read(buf, 100, out read))
                        {
                            int value;
                            if (TryParse(buf, 0x01, out value) && value >= 0)
                            {
                                values[p] = value;
                                break;
                            }
                        }
                    }
                }
                int percent;
                if (values[0] < 0 || !TryPercent(values[0], out percent)) return Failure("耳机没有回应电量（可能已关机）");
                bool charging = values[1] == 1 || values[1] == 3;
                return Success(percent, charging, "level=" + values[0] + " charge=" + values[1]);
            }
            finally { c.Dispose(); }
        }
    }

    /// <summary>HyperX Cloud Flight：报告 21 回电压，高于 0x100B mV 视为充电。</summary>
    internal sealed class HyperXFlightProtocol : VendorProtocol
    {
        private const int ChargingMv = 0x100B;
        private static readonly int[] MvTable = new int[] { 3975, 3850, 3750, 3650, 3648, 0 };
        private static readonly int[] PctTable = new int[] { 100, 75, 50, 20, 4, 0 };

        public override string Id { get { return "hyperxflight"; } }
        public override string Label { get { return "HyperX Flight HID"; } }
        public override bool Accepts(HidDeviceInfo info)
        {
            return info != null && info.UsagePage == 0xFF90 && info.Usage == 0x0303 &&
                   ((info.VendorId == 0x0951 && (info.ProductId == 0x16C4 || info.ProductId == 0x1723)));
        }
        public override string NameFor(HidDeviceInfo info) { return "HyperX Cloud Flight"; }
        public override DeviceCategory CategoryFor(HidDeviceInfo info) { return DeviceCategory.Headset; }

        internal static byte[] BuildRequest()
        {
            byte[] r = new byte[20];
            r[0] = 0x21; r[1] = 0xFF; r[2] = 0x05;
            return r;
        }

        /// <summary>mv = -1 表示没连上；charging 时无电量。</summary>
        internal static bool TryParse(byte[] r, out int mv, out bool charging, out bool linked)
        {
            mv = -1; charging = false; linked = false;
            if (r == null || r.Length < 5 || r[0] != 0x21 || r[1] != 0xFF || r[2] != 0x05) return false;
            mv = (r[3] << 8) | r[4];
            if (mv == 0) return true;
            linked = true;
            charging = mv > ChargingMv;
            return true;
        }

        internal static int VoltagePercent(int mv)
        {
            // 参考项目用的是 HeadsetControl 的四次多项式；这里用等价的单调分段标定，
            // 避免在 C# 5 里引入浮点幂运算，且保证 4000mV=100%、3600mV≈4%。
            if (mv <= 3648) return mv * 125 / 100000; // 0.00125 * mv
            if (mv > 3975) return 100;
            int pct = HeadsetMath.Interpolate(mv, MvTable, PctTable);
            return pct < 0 ? 0 : (pct > 100 ? 100 : pct);
        }

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            HidConnection c = new HidConnection();
            try
            {
                if (!c.Open(info.Path)) return Failure("无法打开耳机");
                c.Flush();
                if (!c.Write(BuildRequest())) return Failure("写入耳机失败");
                int deadline = Environment.TickCount + Math.Max(1000, timeoutMs);
                byte[] buf = new byte[20];
                int read;
                while (Environment.TickCount < deadline)
                {
                    if (c.Read(buf, 100, out read))
                    {
                        int mv; bool charging, linked;
                        if (TryParse(buf, out mv, out charging, out linked))
                        {
                            if (!linked) return Failure("耳机未连接到接收器");
                            if (charging) return Success(100, true, "mv=" + mv + " charging");
                            return Success(VoltagePercent(mv), false, "mv=" + mv);
                        }
                    }
                }
                return Failure("耳机没有回应电量");
            }
            finally { c.Dispose(); }
        }
    }

    /// <summary>HyperX Cloud Stinger Core Wireless：feature FF 问答，电压表换算。</summary>
    internal sealed class HyperXStingerProtocol : VendorProtocol
    {
        private static readonly int[] DischargeMv = new int[] { 4121, 4042, 3954, 3905, 3838, 3785, 3763, 3738, 3700, 3651, 3517 };
        private static readonly int[] ChargeMv = new int[] { 4130, 4090, 4050, 4000, 3967, 3946, 3930, 3916, 3871, 3831, 3400 };
        private static readonly int[] Pcts = new int[] { 100, 90, 80, 70, 60, 50, 40, 30, 20, 10, 0 };

        public override string Id { get { return "hyperxstinger"; } }
        public override string Label { get { return "HyperX Stinger HID"; } }
        public override bool Accepts(HidDeviceInfo info)
        {
            if (info == null || info.UsagePage != 0xFF00 || info.Usage != 0x0001) return false;
            return (info.VendorId == 0x0951 && info.ProductId == 0x170B) ||
                   (info.VendorId == 0x0951 && info.ProductId == 0x1713) ||
                   (info.VendorId == 0x03F0 && (info.ProductId == 0x0A93 || info.ProductId == 0x0B93));
        }
        public override string NameFor(HidDeviceInfo info) { return "HyperX Cloud Stinger Core"; }
        public override DeviceCategory CategoryFor(HidDeviceInfo info) { return DeviceCategory.Headset; }

        private static readonly byte[] BatteryCmd = new byte[]
        {
            0xFF, 0x07, 0x00, 0xFD, 0x04, 0x0C, 0xF1, 0x02, 0x01, 0x04, 0xF0, 0x0C, 0, 0, 0, 0, 0, 0, 0, 0
        };

        internal static bool TryParse(byte[] r, out int level, out bool charging, out bool full)
        {
            level = 0; charging = false; full = false;
            if (r == null || r.Length < 13 || r[0] != 0xFF) return false;
            int code = r[9];
            if (code != 0x03 && code != 0x05 && code != 0x06) return false;
            int mv = r[11] | (r[12] << 8);
            if (code == 0x06) { level = 100; full = true; return true; }
            level = HeadsetMath.Interpolate(mv, code == 0x05 ? ChargeMv : DischargeMv, Pcts);
            charging = code == 0x05;
            return true;
        }

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            HidConnection c = new HidConnection();
            try
            {
                if (!c.OpenFeatureOnly(info.Path)) return Failure("无法打开耳机");
                byte[] buf = new byte[Math.Max(20, (int)info.FeatureReportLength)];
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (!c.SetFeature(BatteryCmd)) continue;
                    Thread.Sleep(30);
                    buf[0] = 0xFF;
                    if (c.GetFeature(buf))
                    {
                        int level; bool charging, full;
                        if (TryParse(buf, out level, out charging, out full))
                        {
                            return Success(level, full ? (bool?)true : charging, VendorProtocol.Hex(buf, 0, 13));
                        }
                    }
                }
                return Failure("耳机没有回应电量");
            }
            finally { c.Dispose(); }
        }
    }

    /// <summary>HyperX Cloud Alpha Wireless：报告 21，命令 0B 电量 / 0C 充电 / 03 链路。</summary>
    internal sealed class HyperXAlphaProtocol : VendorProtocol
    {
        private const byte CmdLevel = 0x0B;
        private const byte CmdCharge = 0x0C;
        private const byte CmdLink = 0x03;

        public override string Id { get { return "hyperxalpha"; } }
        public override string Label { get { return "HyperX Alpha HID"; } }
        public override bool Accepts(HidDeviceInfo info)
        {
            return info != null && info.VendorId == 0x03F0 && info.ProductId == 0x098D &&
                   info.UsagePage == 0xFF43 && info.Usage == 0x0202;
        }
        public override string NameFor(HidDeviceInfo info) { return "HyperX Cloud Alpha Wireless"; }
        public override DeviceCategory CategoryFor(HidDeviceInfo info) { return DeviceCategory.Headset; }

        internal static byte[] BuildRequest(byte cmd) { return new byte[] { 0x21, 0xBB, cmd }; }

        internal static bool TryParse(byte[] r, byte cmd, out int value)
        {
            value = -1;
            if (r == null || r.Length < 4 || r[0] != 0x21 || r[1] != 0xBB || r[2] != cmd) return false;
            value = r[3];
            return true;
        }

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            HidConnection c = new HidConnection();
            try
            {
                if (!c.Open(info.Path)) return Failure("无法打开耳机");
                int level = -1, charging = -1;
                byte[] buf = new byte[Math.Max(31, (int)info.InputReportLength)];
                byte[] cmds = new byte[] { CmdLevel, CmdCharge };
                for (int p = 0; p < cmds.Length; p++)
                {
                    c.Flush();
                    if (!c.Write(BuildRequest(cmds[p]))) continue;
                    int deadline = Environment.TickCount + Math.Max(500, timeoutMs);
                    int read;
                    while (Environment.TickCount < deadline)
                    {
                        if (c.Read(buf, 100, out read))
                        {
                            int value;
                            if (TryParse(buf, cmds[p], out value))
                            {
                                if (p == 0) level = value; else charging = value;
                                break;
                            }
                        }
                    }
                }
                if (level < 0 || level > 100) return Failure("耳机没有回应电量（可能已关机）");
                return Success(level, charging == 1, "level=" + level + " charge=" + charging);
            }
            finally { c.Dispose(); }
        }
    }

    /// <summary>Razer BlackShark V3：64 字节 report 02，电量/充电是主动推送的属性。</summary>
    internal sealed class RazerBlackSharkV3Protocol : VendorProtocol
    {
        private const ushort Vid = 0x1532;

        public override string Id { get { return "razorbs3"; } }
        public override string Label { get { return "Razer BlackShark HID"; } }
        public override int MinQuerySeconds { get { return 1800; } } // 问太勤会让耳机重启
        public override bool Accepts(HidDeviceInfo info)
        {
            return info != null && info.VendorId == Vid && info.UsagePage == 0xFF14 &&
                   (info.ProductId == 0x057A || info.ProductId == 0x0579) &&
                   info.InputReportLength >= 64 && info.OutputReportLength >= 64;
        }
        public override string NameFor(HidDeviceInfo info)
        {
            return info != null && info.ProductId == 0x0579 ? "Razer BlackShark V3（有线）" : "Razer BlackShark V3";
        }
        public override DeviceCategory CategoryFor(HidDeviceInfo info) { return DeviceCategory.Headset; }

        internal static byte[] BuildRequest()
        {
            byte[] r = new byte[64];
            r[0] = 0x02; r[2] = 0x60; r[6] = 0x04; r[9] = 0x80; r[10] = 0x21;
            r[62] = HeadsetMath.Xor(r, 0, 62);
            return r;
        }

        internal enum Kind { None, Level, Charging }
        internal static Kind TryParse(byte[] r, out int value)
        {
            value = 0;
            if (r == null || r.Length < 14 || r[0] != 0x02) return Kind.None;
            if (r[10] == 0x21 && (r[11] == 0x01 || r[11] == 0x02) && r[12] == 0x01)
            {
                if (r[13] == 0xFF) return Kind.None;
                if (r[13] <= 100) { value = r[13]; return Kind.Level; }
                return Kind.None;
            }
            if (r[10] == 0x2A && r[13] <= 1) { value = r[13]; return Kind.Charging; }
            return Kind.None;
        }

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            HidConnection c = new HidConnection();
            try
            {
                if (!c.Open(info.Path)) return Failure("无法打开耳机");
                c.Flush();
                if (!c.Write(BuildRequest())) return Failure("写入耳机失败");
                int deadline = Environment.TickCount + Math.Max(2500, timeoutMs);
                byte[] buf = new byte[64];
                int read;
                int level = -1; bool charging = false;
                while (Environment.TickCount < deadline && level < 0)
                {
                    if (c.Read(buf, 300, out read))
                    {
                        int value;
                        Kind kind = TryParse(buf, out value);
                        if (kind == Kind.Level) level = value;
                        else if (kind == Kind.Charging) charging = value != 0;
                    }
                }
                if (level < 0) return Failure("耳机没有回应电量（可能已关机）");
                return Success(level, charging, "level=" + level);
            }
            finally { c.Dispose(); }
        }
    }

    /// <summary>Razer BlackShark V2 Pro (2023)：PA 帧，查询前后包一层 remote on/off。</summary>
    internal sealed class RazerBlackSharkV2ProProtocol : VendorProtocol
    {
        private const byte CmdBattery = 0x21;
        private const byte CmdCharge = 0x2A;

        public override string Id { get { return "razorbs2pro"; } }
        public override string Label { get { return "Razer BlackShark V2 Pro HID"; } }
        public override int MinQuerySeconds { get { return 300; } }
        public override bool Accepts(HidDeviceInfo info)
        {
            return info != null && info.VendorId == 0x1532 && info.ProductId == 0x0555 &&
                   info.UsagePage == 0xFF00 && info.InputReportLength >= 64 && info.OutputReportLength >= 64;
        }
        public override string NameFor(HidDeviceInfo info) { return "Razer BlackShark V2 Pro"; }
        public override DeviceCategory CategoryFor(HidDeviceInfo info) { return DeviceCategory.Headset; }

        internal static byte[] Remote(bool on)
        {
            byte[] r = new byte[64];
            byte[] head = new byte[] { 0x02, 0x80, 0x07, 0x00, 0x00, 0x50, 0x41, 0x0E, 0x00, 0x02, 0xE1, on ? (byte)0x01 : (byte)0x00 };
            Array.Copy(head, r, head.Length);
            return r;
        }

        internal static byte[] BuildRequest(byte cmd)
        {
            byte[] r = new byte[64];
            byte[] head = new byte[] { 0x02, 0x80, 0x08, 0x00, 0x00, 0x50, 0x41, 0x08, 0x00, 0x03, cmd, 0x00 };
            Array.Copy(head, r, head.Length);
            return r;
        }

        internal static bool TryParse(byte[] r, byte cmd, out int value)
        {
            value = -1;
            if (r == null || r.Length < 16 || r[0] != 0x02 || r[12] != cmd || r[13] != 0x01) return false;
            value = r[15];
            return true;
        }

        private static int Query(HidConnection c, byte cmd, byte[] buf)
        {
            c.Write(Remote(true));
            Thread.Sleep(35);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                c.Write(Remote(true));
                Thread.Sleep(35);
                if (!c.Write(BuildRequest(cmd))) break;
                int deadline = Environment.TickCount + 150;
                int read;
                while (Environment.TickCount < deadline)
                {
                    if (c.Read(buf, 60, out read))
                    {
                        int value;
                        if (TryParse(buf, cmd, out value)) return value;
                    }
                }
            }
            return -1;
        }

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            HidConnection c = new HidConnection();
            try
            {
                if (!c.Open(info.Path)) return Failure("无法打开耳机");
                byte[] buf = new byte[64];
                c.Flush();
                int level = Query(c, CmdBattery, buf);
                int charge = Query(c, CmdCharge, buf);
                c.Write(Remote(false));
                if (level < 0 || level > 100) return Failure("耳机没有回应电量（可能已关机）");
                return Success(level, charge > 0, "level=" + level + " charge=" + charge);
            }
            finally { c.Dispose(); }
        }
    }

    /// <summary>Razer Barracuda X Chroma：属性 21 电量、2A 充电，64 字节 report 02。</summary>
    internal sealed class RazerBarracudaProtocol : VendorProtocol
    {
        private const byte PropBattery = 0x21;
        private const byte PropCharge = 0x2A;

        public override string Id { get { return "razorbarracuda"; } }
        public override string Label { get { return "Razer Barracuda HID"; } }
        public override bool Accepts(HidDeviceInfo info)
        {
            return info != null && info.VendorId == 0x1532 && info.ProductId == 0x0574 &&
                   info.UsagePage == 0xFF14 && info.InputReportLength >= 64 && info.OutputReportLength >= 64;
        }
        public override string NameFor(HidDeviceInfo info) { return "Razer Barracuda X Chroma"; }
        public override DeviceCategory CategoryFor(HidDeviceInfo info) { return DeviceCategory.Headset; }

        internal static byte[] BuildRequest(byte prop)
        {
            byte[] r = new byte[64];
            byte[] head = new byte[] { 0x02, 0x00, 0x60, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x80, prop, 0x00 };
            Array.Copy(head, r, head.Length);
            r[62] = HeadsetMath.Xor(r, 0, 62);
            return r;
        }

        internal static bool TryParse(byte[] r, byte prop, out int value)
        {
            value = 0;
            byte[] head = new byte[] { 0x02, 0x02, 0x60, 0x00, 0x00, 0x00, 0x05, 0x00, 0x80, 0x80, prop, 0x01, 0x01 };
            if (r == null || r.Length < 64) return false;
            for (int i = 0; i < head.Length; i++)
            {
                if (r[i] != head[i]) return false;
            }
            if (r[62] != HeadsetMath.Xor(r, 1, 61)) return false;
            value = r[13];
            if (prop == PropBattery && value > 100) return false;
            if (prop == PropCharge && value > 1) return false;
            return true;
        }

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            HidConnection c = new HidConnection();
            try
            {
                if (!c.Open(info.Path)) return Failure("无法打开耳机");
                byte[] buf = new byte[64];
                byte[] props = new byte[] { PropBattery, PropCharge };
                int[] values = new int[] { -1, -1 };
                for (int p = 0; p < props.Length; p++)
                {
                    c.Flush();
                    if (!c.Write(BuildRequest(props[p]))) continue;
                    int deadline = Environment.TickCount + Math.Max(500, timeoutMs);
                    int read;
                    while (Environment.TickCount < deadline)
                    {
                        if (c.Read(buf, 100, out read))
                        {
                            int value;
                            if (TryParse(buf, props[p], out value)) { values[p] = value; break; }
                        }
                    }
                }
                if (values[0] < 0) return Failure("耳机没有回应电量（可能已关机）");
                return Success(values[0], values[1] == 1, "level=" + values[0] + " charge=" + values[1]);
            }
            finally { c.Dispose(); }
        }
    }

    /// <summary>罗技 G533：固定 HID++ 请求读电压，带 HeadsetControl 的标定曲线。</summary>
    internal sealed class LogitechG533Protocol : VendorProtocol
    {
        private const int Len = 20;
        private static readonly int[] CalMv = new int[] { 4200, 3850, 3790, 3750, 3680, 3330 };
        private static readonly int[] CalPct = new int[] { 100, 50, 30, 20, 5, 0 };

        public override string Id { get { return "logitechg533"; } }
        public override string Label { get { return "Logitech G533 HID++"; } }
        public override bool Accepts(HidDeviceInfo info)
        {
            return info != null && info.VendorId == 0x046D && info.ProductId == 0x0A66 &&
                   info.UsagePage == 0xFF43 && info.Usage == 0x0202;
        }
        public override string NameFor(HidDeviceInfo info) { return "Logitech G533"; }
        public override DeviceCategory CategoryFor(HidDeviceInfo info) { return DeviceCategory.Headset; }

        internal static byte[] BuildRequest()
        {
            byte[] r = new byte[Len];
            r[0] = 0x11; r[1] = 0xFF; r[2] = 0x07; r[3] = 0x01;
            return r;
        }

        internal enum Kind { None, Battery, Offline }
        internal static Kind TryParse(byte[] r, out int mv, out bool charging)
        {
            mv = 0; charging = false;
            if (r == null || r.Length < 7 || r[0] != 0x11 || r[1] != 0xFF) return Kind.None;
            if (r[2] == 0xFF && r[3] == 0x07) return Kind.Offline;
            if (r[2] != 0x07 || r[3] != 0x01) return Kind.None;
            mv = (r[4] << 8) | r[5];
            if (mv < CalMv[CalMv.Length - 1]) return Kind.Offline;
            charging = r[6] == 0x03;
            return Kind.Battery;
        }

        internal static int VoltagePercent(int mv) { return HeadsetMath.Interpolate(mv, CalMv, CalPct); }

        public override BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            HidConnection c = new HidConnection();
            try
            {
                if (!c.Open(info.Path)) return Failure("无法打开耳机");
                c.Flush();
                if (!c.Write(BuildRequest())) return Failure("写入耳机失败");
                int deadline = Environment.TickCount + Math.Max(1000, timeoutMs);
                byte[] buf = new byte[Len];
                int read;
                while (Environment.TickCount < deadline)
                {
                    if (c.Read(buf, 100, out read))
                    {
                        int mv; bool charging;
                        Kind kind = TryParse(buf, out mv, out charging);
                        if (kind == Kind.Battery)
                        {
                            return Success(VoltagePercent(mv), charging, "mv=" + mv);
                        }
                        if (kind == Kind.Offline) return Failure("耳机没电或未连接");
                    }
                }
                return Failure("耳机没有回应电量");
            }
            finally { c.Dispose(); }
        }
    }
}
