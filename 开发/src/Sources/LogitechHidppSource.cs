using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeviceBattery
{
    /// <summary>一个可读电量的罗技设备：接收器接口 + 接收器内部的设备序号。</summary>
    internal sealed class HidppDevice
    {
        public HidDeviceInfo Info;
        public byte DeviceIndex;
        public byte FeatureIndex;
        public string Name;
        public DeviceCategory Category = DeviceCategory.Mouse;

        public string StableId
        {
            get
            {
                string shortId = Info == null ? "unknown" : Info.ShortId;
                return shortId + ":" + DeviceIndex.ToString("X2", CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>
    /// 罗技 HID++ 2.0 电量通道（Feature 0x1000 BATTERY_STATUS）。
    /// 覆盖 Lightspeed/Unifying/Bolt 接收器后面的无线鼠标、键盘、耳机：
    /// 先通过 IRoot(0x0000) 查到 Feature 0x1000 的序号，再向该序号要电量。
    /// </summary>
    internal sealed class LogitechHidppSource
    {
        public const ushort LogitechVendorId = 0x046D;

        public const ushort FeatureDeviceName = 0x0005;
        public const ushort FeatureBatteryStatus = 0x1000;

        private const byte ReportIdShort = 0x10;
        private const byte ReportIdLong = 0x11;
        private const int RequestTimeoutMs = 90;

        // 接收器后面最多 6 台设备；首轮探测 0xFF 与 1~3，够用又不会把发现拖慢。
        private static readonly byte[] ProbeIndices = new byte[] { 0xFF, 0x01, 0x02, 0x03 };

        // 已确认过的接口只复查已知序号，避免每轮发现都全量试探。
        private readonly Dictionary<string, List<byte>> _knownIndices = new Dictionary<string, List<byte>>();
        private readonly object _gate = new object();

        public List<HidppDevice> Find(List<HidDeviceInfo> all)
        {
            List<HidppDevice> result = new List<HidppDevice>();
            if (all == null)
            {
                return result;
            }
            for (int i = 0; i < all.Count; i++)
            {
                HidDeviceInfo info = all[i];
                if (info == null || info.VendorId != LogitechVendorId)
                {
                    continue;
                }
                if (!IsHidppInterface(info))
                {
                    continue;
                }
                try
                {
                    ProbeInterface(info, result);
                }
                catch (Exception ex)
                {
                    Log.Warn("罗技 HID++ 探测失败（" + (info.ProductName ?? "未知设备") + "）：" + ex.Message);
                }
            }
            return result;
        }

        /// <summary>HID++ 帧只能从厂商自定义集合走：罗技固定用 0xFF00 / 0xFF43 使用页。</summary>
        private static bool IsHidppInterface(HidDeviceInfo info)
        {
            return info.UsagePage == 0xFF00 || info.UsagePage == 0xFF43;
        }

        private void ProbeInterface(HidDeviceInfo info, List<HidppDevice> result)
        {
            using (HidConnection connection = new HidConnection())
            {
                if (!connection.Open(info.Path))
                {
                    // 被 G HUB / Options+ 独占时打不开，交给下一轮重试。
                    return;
                }

                byte[] indices = ResolveIndices(info);
                for (int i = 0; i < indices.Length; i++)
                {
                    byte index = indices[i];
                    byte featureIndex = QueryFeatureIndex(connection, index, FeatureBatteryStatus);
                    if (featureIndex == 0)
                    {
                        continue;
                    }
                    RememberIndex(info, index);
                    HidppDevice device = new HidppDevice();
                    device.Info = info;
                    device.DeviceIndex = index;
                    device.FeatureIndex = featureIndex;
                    device.Name = QueryDeviceName(connection, index);
                    device.Category = GuessCategory(device.Name);
                    result.Add(device);
                }
            }
        }

        private byte[] ResolveIndices(HidDeviceInfo info)
        {
            string key = info.ShortId ?? info.Path;
            lock (_gate)
            {
                List<byte> known;
                if (key != null && _knownIndices.TryGetValue(key, out known) && known.Count > 0)
                {
                    return known.ToArray();
                }
            }
            return ProbeIndices;
        }

        private void RememberIndex(HidDeviceInfo info, byte index)
        {
            if (info == null)
            {
                return;
            }
            string key = info.ShortId ?? info.Path;
            if (key == null)
            {
                return;
            }
            lock (_gate)
            {
                List<byte> known;
                if (!_knownIndices.TryGetValue(key, out known))
                {
                    known = new List<byte>();
                    _knownIndices[key] = known;
                }
                if (!known.Contains(index))
                {
                    known.Add(index);
                }
            }
        }

        private byte QueryFeatureIndex(HidConnection connection, byte deviceIndex, ushort featureId)
        {
            byte[] request = BuildGetFeature(deviceIndex, featureId);
            byte[] response = Exchange(connection, request);
            if (response == null)
            {
                return 0;
            }
            if (!IsResponseFor(response, deviceIndex, 0x00))
            {
                return 0;
            }
            if (response.Length < 5)
            {
                return 0;
            }
            byte index = response[4];
            return index;
        }

        private string QueryDeviceName(HidConnection connection, byte deviceIndex)
        {
            byte featureIndex = QueryFeatureIndex(connection, deviceIndex, FeatureDeviceName);
            if (featureIndex == 0)
            {
                return null;
            }
            byte[] request = BuildRequest(deviceIndex, featureIndex, 0x00, 0x00, new byte[0], true);
            byte[] response = Exchange(connection, request);
            if (response == null || !IsResponseFor(response, deviceIndex, featureIndex))
            {
                return null;
            }
            StringBuilder builder = new StringBuilder(16);
            for (int i = 4; i < response.Length && builder.Length < 32; i++)
            {
                byte value = response[i];
                if (value == 0)
                {
                    break;
                }
                if (value >= 0x20 && value < 0x7F)
                {
                    builder.Append((char)value);
                }
            }
            string name = builder.ToString().Trim();
            return name.Length == 0 ? null : name;
        }

        public BatteryReading Read(HidppDevice device, int timeoutMs)
        {
            BatteryReading reading = new BatteryReading();
            reading.Source = BatterySourceKind.Vendor;
            reading.Timestamp = DateTime.Now;

            if (device == null || device.Info == null)
            {
                reading.Error = "罗技设备为空";
                return reading;
            }

            int timeout = timeoutMs > 0 ? Math.Min(timeoutMs, 600) : 300;
            try
            {
                using (HidConnection connection = new HidConnection())
                {
                    if (!connection.Open(device.Info.Path))
                    {
                        reading.Error = "无法打开罗技 HID++ 接口（可能被 G HUB 占用）";
                        return reading;
                    }
                    reading.DeviceFound = true;

                    byte[] request = BuildRequest(device.DeviceIndex, device.FeatureIndex, 0x00, 0x00, new byte[0], false);
                    byte[] response = Exchange(connection, request, timeout);
                    if (response == null)
                    {
                        reading.Error = "罗技 HID++ 电量查询无响应";
                        return reading;
                    }
                    if (!IsResponseFor(response, device.DeviceIndex, device.FeatureIndex))
                    {
                        reading.Error = "罗技 HID++ 响应与请求不匹配";
                        return reading;
                    }

                    int level = ParseLevel(response);
                    if (level < 0)
                    {
                        reading.Error = "罗技 HID++ 电量字段无效";
                        return reading;
                    }

                    reading.Percent = level;
                    reading.Charging = null;
                    reading.Confidence = ReadingConfidence.High;
                    reading.Raw = Hex(response);
                    return reading;
                }
            }
            catch (Exception ex)
            {
                reading.Error = "罗技 HID++ 读取异常：" + ex.Message;
                return reading;
            }
        }

        private static byte[] Exchange(HidConnection connection, byte[] request)
        {
            return Exchange(connection, request, RequestTimeoutMs);
        }

        private static byte[] Exchange(HidConnection connection, byte[] request, int timeoutMs)
        {
            if (!connection.Write(request))
            {
                return null;
            }
            int expected = request.Length > 8 ? 20 : 7;
            byte[] buffer = new byte[expected];
            int read;
            if (!connection.Read(buffer, timeoutMs, out read))
            {
                return null;
            }
            if (read > 0 && read < buffer.Length)
            {
                Array.Resize(ref buffer, read);
            }
            return buffer;
        }

        internal static bool IsResponseFor(byte[] response, byte deviceIndex, byte featureIndex)
        {
            if (response == null || response.Length < 5)
            {
                return false;
            }
            if (response[0] != ReportIdShort && response[0] != ReportIdLong)
            {
                return false;
            }
            if (response[1] != deviceIndex)
            {
                return false;
            }
            // featureIndex 为 0 时只校验设备序号（IRoot 的应答本身就带序号 0）。
            return featureIndex == 0x00 || response[2] == featureIndex;
        }

        /// <summary>0x1000 功能 0 的第一个参数字节就是剩余电量百分比。</summary>
        internal static int ParseLevel(byte[] response)
        {
            if (response == null || response.Length < 5)
            {
                return -1;
            }
            int level = response[4];
            if (level < 0 || level > 100)
            {
                return -1;
            }
            return level;
        }

        internal static byte[] BuildGetFeature(byte deviceIndex, ushort featureId)
        {
            byte[] parameters = new byte[]
            {
                (byte)(featureId >> 8),
                (byte)(featureId & 0xFF)
            };
            return BuildRequest(deviceIndex, 0x00, 0x00, 0x00, parameters, false);
        }

        /// <summary>
        /// 组装 HID++ 2.0 请求帧：短报文 7 字节（report 0x10），长报文 20 字节（report 0x11）。
        /// 帧结构：报文号 / 设备序号 / 功能序号 / (功能编号&lt;&lt;4 | 软件号) / 参数…
        /// </summary>
        internal static byte[] BuildRequest(byte deviceIndex, byte featureIndex, byte function,
            byte softwareId, byte[] parameters, bool longReport)
        {
            int length = longReport ? 20 : 7;
            byte[] frame = new byte[length];
            frame[0] = longReport ? ReportIdLong : ReportIdShort;
            frame[1] = deviceIndex;
            frame[2] = featureIndex;
            frame[3] = (byte)((function << 4) | (softwareId & 0x0F));
            if (parameters != null)
            {
                for (int i = 0; i < parameters.Length && 4 + i < length; i++)
                {
                    frame[4 + i] = parameters[i];
                }
            }
            return frame;
        }

        internal static DeviceCategory GuessCategory(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return DeviceCategory.Mouse;
            }
            string lower = name.ToLowerInvariant();
            if (lower.IndexOf("keyboard", StringComparison.Ordinal) >= 0 ||
                lower.IndexOf("kbd", StringComparison.Ordinal) >= 0 ||
                lower.IndexOf("keys", StringComparison.Ordinal) >= 0)
            {
                return DeviceCategory.Keyboard;
            }
            if (lower.IndexOf("headset", StringComparison.Ordinal) >= 0 ||
                lower.IndexOf("headphone", StringComparison.Ordinal) >= 0 ||
                lower.IndexOf("earbud", StringComparison.Ordinal) >= 0 ||
                lower.IndexOf("earphone", StringComparison.Ordinal) >= 0)
            {
                return DeviceCategory.Headset;
            }
            return DeviceCategory.Mouse;
        }

        private static string Hex(byte[] buffer)
        {
            if (buffer == null || buffer.Length == 0)
            {
                return null;
            }
            StringBuilder builder = new StringBuilder(buffer.Length * 3);
            for (int i = 0; i < buffer.Length && i < 32; i++)
            {
                builder.Append(buffer[i].ToString("X2", CultureInfo.InvariantCulture));
                builder.Append(' ');
            }
            return builder.ToString().Trim();
        }
    }
}
