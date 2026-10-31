using System;
using System.Collections.Generic;
using System.Management;

namespace DeviceBattery
{
    internal sealed class UsbHeadsetInfo
    {
        public string Name;
        public string InstanceId;
        public string PnpDeviceId;
        public ushort VendorId;
        public ushort ProductId;
        public bool Connected;
    }

    internal static class UsbHeadsetDetector
    {
        // 一个已知的 2.4G 耳机接收器。它只暴露音频和 HID 按键，不提供电量；
        // 保留 VID/PID 是为了给这类设备明确显示“2.4G 不支持读取”，而不是当成未知设备。
        private const string KnownVidPid = "VID_3654&PID_1332";

        private static readonly object CacheGate = new object();
        private static UsbHeadsetInfo _cached;
        private static DateTime _cachedAt = DateTime.MinValue;

        public static UsbHeadsetInfo Find()
        {
            // WMI 查询本身不便宜，而蓝牙事件会把发现间隔压到 3 秒一次；
            // 20 秒缓存足够避免重复查询，也不影响插拔后的最终识别。
            lock (CacheGate)
            {
                if (_cachedAt != DateTime.MinValue &&
                    (DateTime.Now - _cachedAt).TotalSeconds < 20)
                {
                    return _cached;
                }
            }

            UsbHeadsetInfo result = Query();

            lock (CacheGate)
            {
                _cached = result;
                _cachedAt = DateTime.Now;
            }
            return result;
        }

        private static UsbHeadsetInfo Query()
        {
            UsbHeadsetInfo fallback = null;
            try
            {
                // 只查目标 VID/PID，别把整机几百个 PnP 实体全拉回来。
                string query = "SELECT Name,PNPDeviceID,Status FROM Win32_PnPEntity WHERE PNPDeviceID LIKE '%" +
                               KnownVidPid + "%'";
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(query))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementObject item in items)
                    {
                        try
                        {
                            string pnp = Convert.ToString(item["PNPDeviceID"]);
                            if (string.IsNullOrEmpty(pnp) ||
                                pnp.IndexOf(KnownVidPid, StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                continue;
                            }

                            string name = Convert.ToString(item["Name"]);
                            string status = Convert.ToString(item["Status"]);
                            UsbHeadsetInfo info = new UsbHeadsetInfo();
                            info.Name = string.IsNullOrEmpty(name) ? "USB 无线耳机" : name;
                            info.PnpDeviceId = pnp;
                            info.InstanceId = pnp;
                            info.VendorId = 0x3654;
                            info.ProductId = 0x1332;
                            info.Connected = string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase);

                            // 优先选音频接口那条记录，它的名字最像产品名。
                            bool looksLikeAudio = info.Name.IndexOf("ZhuAudio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                  info.Name.IndexOf("ZG2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                  info.Name.IndexOf("耳机", StringComparison.OrdinalIgnoreCase) >= 0;
                            if (looksLikeAudio)
                            {
                                return info;
                            }
                            if (fallback == null &&
                                info.Name.IndexOf("Composite", StringComparison.OrdinalIgnoreCase) < 0 &&
                                info.Name.IndexOf("Input Device", StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                fallback = info;
                            }
                        }
                        finally
                        {
                            item.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("USB 耳机检测失败：" + ex.Message);
            }
            return fallback;
        }
    }
}
