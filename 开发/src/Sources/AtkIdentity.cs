using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeviceBattery
{
    /// <summary>
    /// ATK 8K NANO 接收器只报告“接收器”名称，真正的鼠标型号需要从
    /// GetMouseCIDMID(0x10) 响应中读取。CID/MID 对应 ATK 的鼠标型号表。
    /// </summary>
    internal sealed class AtkMouseIdentity
    {
        public int Cid;
        public int Mid;
        public string ModelName;
        public string UniqueId;
        public bool ModelKnown;

        /// <summary>
        /// 型号查询偶尔会赶上鼠标休眠。按接收器序列号缓存一次成功结果，
        /// 下次启动即使查询失败也能沿用真实型号和稳定 ID。
        /// </summary>
        public static string CacheKey(HidDeviceInfo info)
        {
            if (info == null)
            {
                return null;
            }
            string serial = NormalizeSerial(info.SerialNumber);
            if (!string.IsNullOrEmpty(serial))
            {
                return "serial:" + serial;
            }
            return string.IsNullOrEmpty(info.ShortId) ? null : "path:" + info.ShortId;
        }

        public string BuildStableId(HidDeviceInfo info)
        {
            string serial = NormalizeSerial(info == null ? null : info.SerialNumber);
            if (string.IsNullOrEmpty(serial))
            {
                serial = info == null ? "unknown" : info.ShortId;
            }
            return string.Format(CultureInfo.InvariantCulture, "atk:{0}-{1}.{2}",
                serial, Cid, Mid);
        }

        public string IdentityText
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture, "CID {0} / MID {1}", Cid, Mid);
            }
        }

        public static string NormalizeSerial(string serial)
        {
            if (string.IsNullOrEmpty(serial))
            {
                return null;
            }
            StringBuilder builder = new StringBuilder(serial.Length);
            for (int i = 0; i < serial.Length && builder.Length < 32; i++)
            {
                char c = serial[i];
                if ((c >= '0' && c <= '9') ||
                    (c >= 'a' && c <= 'z') ||
                    (c >= 'A' && c <= 'Z'))
                {
                    builder.Append(c);
                }
            }
            return builder.ToString();
        }
    }

    internal sealed class AtkIdentityRecord
    {
        public int Cid;
        public int Mid;
        public string ModelName;
        public string UniqueId;
    }

    internal static class AtkProtocol
    {
        public const byte GetBatteryCommand = 0x04;
        public const byte GetMouseCidMidCommand = 0x10;

        /// <summary>
        /// 17 字节 HID 报告：报告号 0x08、命令、状态、地址、有效长度、数据、校验。
        /// 校验算法与 ATK HUB 一致：85 - (8 + 前 15 字节之和) &amp; 0xFF。
        /// </summary>
        public static byte[] CreateRequest(byte command)
        {
            byte[] packet = new byte[17];
            packet[0] = 0x08;
            packet[1] = command;
            int sum = 8;
            for (int i = 1; i <= 15; i++)
            {
                sum += packet[i];
            }
            packet[16] = (byte)(85 - (sum & 255));
            return packet;
        }
    }

    internal static class AtkModelCatalog
    {
        // 取自 ATK HUB 的 Compx 鼠标 CID/MID 表；只收录 ATK A9 家族，
        // 遇到未知型号时保留稳定 ID，同时显示“ATK 鼠标”，不再冒充接收器。
        private static readonly Dictionary<string, string> Models =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "0,0", "ATK A9 PRO MAX" },
                { "1,2", "ATK A9 Ultimate 3.0" },
                { "1,4", "ATK A9 Mini +" },
                { "1,7", "ATK A9 Ultra Max 2.0 +" },
                { "1,9", "ATK A9 Ultimate +" },
                { "1,16", "ATK A9 Mini" },
                { "1,22", "ATK A9 Ultimate" },
                { "1,26", "ATK A9 Air" },
                { "1,27", "ATK A9 Mini" },
                { "1,28", "ATK A9 Mini" },
                { "1,37", "ATK A9 Ultra Max 2.0 Tri-Mode" },
                { "1,38", "ATK A9 Ultra Max 2.0" },
                { "1,75", "ATK A9 Ultimate Silent" },
                { "2,1", "ATK A9 Pro Max 2.0 NK" },
                { "2,2", "ATK A9 Pro" },
                { "2,3", "ATK A9 NK" },
                { "2,4", "ATK A9 Plus NK" },
                { "2,13", "ATK A9 Plus NK" },
                { "2,14", "ATK A9 Pro Max 2.0 NK" },
                { "2,26", "ATK A9 Plus NK" },
                { "2,27", "ATK A9 Pro Max 2.0 NK" },
                { "2,29", "ATK A9 3.0 NK" },
                { "2,32", "ATK A9 SE 3.0 NK" },
                { "2,34", "ATK A9 Plus 3.0 NK" },
                { "2,35", "ATK A9 Pro Max 3.0 NK" },
                { "2,38", "ATK A9 3.0 NK" },
                { "2,39", "ATK A9 SE 3.0 NK" },
                { "2,40", "ATK A9 Plus 3.0 NK" },
                { "2,41", "ATK A9 Pro Max 3.0 NK" },
                { "2,88", "ATK A9 SE NK" },
                { "2,99", "ATK A9" },
                { "2,107", "ATK A9 X" },
                { "2,120", "ATK A9 NB" }
            };

        public static bool TryResolve(int cid, int mid, out string name)
        {
            string key = string.Format(CultureInfo.InvariantCulture, "{0},{1}", cid, mid);
            return Models.TryGetValue(key, out name);
        }

        public static string Resolve(int cid, int mid)
        {
            string name;
            return TryResolve(cid, mid, out name) ? name : "ATK 鼠标";
        }
    }
}
