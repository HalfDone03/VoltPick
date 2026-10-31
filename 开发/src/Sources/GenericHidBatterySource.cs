using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeviceBattery
{
    internal sealed class GenericHidBatterySource
    {
        public List<HidBatteryDevice> Find()
        {
            return HidEnumerator.FindBatteryDevices();
        }

        /// <summary>用一次已经枚举好的 HID 接口列表查找，避免每轮发现重复扫描全机 HID。</summary>
        public List<HidBatteryDevice> Find(List<HidDeviceInfo> scanned)
        {
            return HidEnumerator.FindBatteryDevices(scanned);
        }

        public BatteryReading Read(HidBatteryDevice device, int timeoutMs)
        {
            BatteryReading reading = new BatteryReading();
            reading.Source = BatterySourceKind.GenericHid;
            reading.Timestamp = DateTime.Now;

            if (device == null || device.Info == null)
            {
                reading.Error = "HID 设备为空";
                return reading;
            }

            IntPtr preparsed = IntPtr.Zero;
            try
            {
                using (HidConnection connection = new HidConnection())
                {
                    if (!connection.OpenRead(device.Info.Path))
                    {
                        reading.Error = "无法打开 HID 电量接口（可能被其它程序占用）";
                        return reading;
                    }
                    reading.DeviceFound = true;

                    if (!Native.HidD_GetPreparsedData(connection.Handle, out preparsed))
                    {
                        reading.Error = "无法读取 HID 报表描述";
                        return reading;
                    }

                    Native.HIDP_CAPS caps = new Native.HIDP_CAPS();
                    caps.Reserved = new ushort[17];
                    if (Native.HidP_GetCaps(preparsed, ref caps) != Native.HIDP_STATUS_SUCCESS)
                    {
                        reading.Error = "无法读取 HID 能力";
                        return reading;
                    }

                    byte[] report;
                    if (device.ReportType == 2)
                    {
                        int length = caps.FeatureReportByteLength;
                        if (length <= 0)
                        {
                            reading.Error = "HID 设备未暴露电量 Feature 报文";
                            return reading;
                        }
                        report = new byte[length];
                        if (device.ReportId != 0)
                        {
                            report[0] = device.ReportId;
                        }
                        if (!connection.GetFeature(report))
                        {
                            reading.Error = "HID 电量 Feature 报文读取失败";
                            return reading;
                        }
                    }
                    else
                    {
                        int length = caps.InputReportByteLength;
                        if (length <= 0)
                        {
                            reading.Error = "HID 设备未暴露电量 Input 报文";
                            return reading;
                        }
                        report = new byte[length];
                        int read;
                        if (!connection.Read(report, Math.Max(200, timeoutMs), out read))
                        {
                            reading.Error = "HID 电量报文读取超时";
                            return reading;
                        }
                        if (read > 0 && read < report.Length)
                        {
                            Array.Resize(ref report, read);
                        }
                    }

                    // 先用描述符里命中的那个字段；读不到再试另一个（
                    // 有的设备两个都声明，但只有一个真的更新）。
                    uint value;
                    ushort primary = device.Usage == 0 ? (ushort)0x0020 : device.Usage;
                    ushort fallback = primary == 0x0020 ? (ushort)0x0024 : (ushort)0x0020;
                    int status = Native.HidP_GetUsageValue(device.ReportType, 0x0006,
                        device.LinkCollection, primary, out value, preparsed, report, (uint)report.Length);
                    if (status != Native.HIDP_STATUS_SUCCESS)
                    {
                        uint retry;
                        int second = Native.HidP_GetUsageValue(device.ReportType, 0x0006,
                            device.LinkCollection, fallback, out retry, preparsed, report, (uint)report.Length);
                        if (second == Native.HIDP_STATUS_SUCCESS)
                        {
                            value = retry;
                            status = second;
                        }
                    }
                    if (status != Native.HIDP_STATUS_SUCCESS)
                    {
                        reading.Error = string.Format(CultureInfo.InvariantCulture,
                            "HID 电量字段解析失败（0x{0:X8}）", status);
                        return reading;
                    }
                    if (value > 100)
                    {
                        reading.Error = "HID 电量值超出 0–100：" + value;
                        return reading;
                    }

                    reading.Percent = (int)value;
                    reading.Charging = null;
                    reading.Confidence = ReadingConfidence.High;
                    reading.Raw = Hex(report);
                    return reading;
                }
            }
            catch (Exception ex)
            {
                reading.Error = "HID 读取异常：" + ex.Message;
                return reading;
            }
            finally
            {
                if (preparsed != IntPtr.Zero)
                {
                    try { Native.HidD_FreePreparsedData(preparsed); } catch (Exception) { }
                }
            }
        }

        private static string Hex(byte[] buffer)
        {
            if (buffer == null || buffer.Length == 0)
            {
                return null;
            }
            StringBuilder builder = new StringBuilder(buffer.Length * 3);
            for (int i = 0; i < buffer.Length && i < 64; i++)
            {
                builder.Append(buffer[i].ToString("X2", CultureInfo.InvariantCulture));
                builder.Append(' ');
            }
            return builder.ToString().Trim();
        }
    }
}
