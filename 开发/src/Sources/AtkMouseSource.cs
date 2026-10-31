using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeviceBattery
{
    /// <summary>
    /// ATK / Compx 8K NANO 接收器协议：
    /// 先用 08 10 ... 3D 读取鼠标 CID/MID，再用 08 04 00 ... 49 读取电量；
    /// 电量响应 [6] 百分比、[7] 充电、[8..9] 电芯电压（大端 mV）。
    /// </summary>
    internal sealed class AtkMouseSource : IDisposable
    {
        private const int ReportLength = 17;

        private readonly HidConnection _connection = new HidConnection();
        private string _path;
        private AtkMouseIdentity _identity;
        private string _identityKey;

        public string Name
        {
            get { return "ATK HID"; }
        }

        public HidDeviceInfo Find()
        {
            return HidEnumerator.FindAtkMouse();
        }

        /// <summary>用一次已经枚举好的 HID 接口列表查找，避免每轮发现重复扫描全机 HID。</summary>
        public HidDeviceInfo Find(List<HidDeviceInfo> scanned)
        {
            return HidEnumerator.FindAtkMouse(scanned);
        }

        /// <summary>
        /// 读取接收器后面真正连接的鼠标型号。该命令是只读查询，不会改动鼠标配置。
        /// 失败时返回上一次成功识别的结果，避免已连接的鼠标名称反复变化。
        /// </summary>
        public AtkMouseIdentity Identify(HidDeviceInfo info)
        {
            if (info == null)
            {
                return null;
            }

            string key = (info.Path ?? string.Empty) + "|" + (info.SerialNumber ?? string.Empty);
            bool hasCached = _identity != null && string.Equals(_identityKey, key, StringComparison.Ordinal);
            if (!EnsureOpen(info))
            {
                return UseCachedIdentity(info, key, hasCached);
            }

            byte[] packet = AtkProtocol.CreateRequest(AtkProtocol.GetMouseCidMidCommand);
            if (!_connection.Write(packet))
            {
                CloseConnection();
                return UseCachedIdentity(info, key, hasCached);
            }

            byte[] response = new byte[64];
            int read;
            if (!_connection.Read(response, 800, out read) ||
                read < 8 ||
                response[0] != 0x08 ||
                response[1] != AtkProtocol.GetMouseCidMidCommand ||
                response[2] != 0)
            {
                CloseConnection();
                return UseCachedIdentity(info, key, hasCached);
            }

            int cid = response[6];
            int mid = response[7];
            string model;
            bool known = AtkModelCatalog.TryResolve(cid, mid, out model);
            if (!known)
            {
                model = "ATK 鼠标";
            }

            AtkMouseIdentity identity = new AtkMouseIdentity();
            identity.Cid = cid;
            identity.Mid = mid;
            identity.ModelName = model;
            identity.ModelKnown = known;
            identity.UniqueId = string.Format("{0}.{1}.{2}.{3}",
                response[12].ToString("X2"), response[13].ToString("X2"),
                response[14].ToString("X2"), response[15].ToString("X2"));
            _identity = identity;
            _identityKey = key;
            AppSettings.RememberAtkIdentity(identity, info);
            Log.Info("ATK 鼠标识别：" + identity.IdentityText + "，型号 " + identity.ModelName);
            return identity;
        }

        private AtkMouseIdentity UseCachedIdentity(HidDeviceInfo info, string key, bool hasCached)
        {
            if (hasCached)
            {
                return _identity;
            }
            AtkMouseIdentity saved = AppSettings.FindAtkIdentity(info);
            if (saved != null)
            {
                _identity = saved;
                _identityKey = key;
            }
            return saved;
        }

        private bool EnsureOpen(HidDeviceInfo info)
        {
            if (info == null)
            {
                return false;
            }
            if (_connection.IsOpen && string.Equals(_path, info.Path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!_connection.Open(info.Path))
            {
                return false;
            }
            _path = info.Path;
            return true;
        }

        private void CloseConnection()
        {
            _connection.Close();
            _path = null;
        }

        /// <summary>
        /// 发请求前把输入队列里残留/推送的帧读掉。超时给得很短（每次 20ms，
        /// 最多 4 次），最坏情况只多花 80ms，不会挤占正常读取的 800ms 预算。
        /// 读失败就说明队列空了，直接返回。
        /// </summary>
        private void DrainPendingFrames()
        {
            const int DrainTimeoutMs = 20;
            const int DrainLimit = 4;
            for (int i = 0; i < DrainLimit; i++)
            {
                byte[] stale = new byte[ReportLength];
                int read;
                if (!_connection.Read(stale, DrainTimeoutMs, out read))
                {
                    return;
                }
                Log.Debug("ATK 丢弃输入队列残留帧：" + BitConverter.ToString(stale, 0, read));
            }
        }

        public BatteryReading Read(HidDeviceInfo info, int timeoutMs)
        {
            BatteryReading reading = new BatteryReading();
            reading.Source = BatterySourceKind.AtkHid;
            reading.Timestamp = DateTime.Now;

            if (info == null)
            {
                reading.DeviceFound = false;
                reading.Error = "未检测到无线接收器";
                return reading;
            }

            reading.DeviceFound = true;

            if (!EnsureOpen(info))
            {
                reading.DeviceFound = false;
                reading.Error = "无法打开无线接收器（可能被其它程序占用）";
                return reading;
            }

            byte[] packet = AtkProtocol.CreateRequest(AtkProtocol.GetBatteryCommand);

            // 这个接收器会在没有任何请求时主动推送 08/04 电量帧（实测：静置时
            // 也收到过一帧完整电量帧）。不先排空输入队列，读到的可能是推送的
            // 旧帧而不是本次请求的响应。排空只用很短超时，不加长正常读取预算。
            DrainPendingFrames();

            string lastError = "鼠标没有响应";
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!_connection.Write(packet))
                {
                    CloseConnection();
                    reading.DeviceFound = false;
                    reading.Error = "写入无线接收器失败";
                    return reading;
                }

                byte[] response = new byte[ReportLength];
                int read;
                if (!_connection.Read(response, timeoutMs, out read))
                {
                    lastError = "鼠标没有响应（可能休眠中）";
                    continue;
                }

                // 逐字段校验：报告号、命令、状态字节、载荷长度、校验和、取值范围。
                // 任何一项不符都不当成读数，只把原因原样留给用户看，不猜数字。
                AtkBatteryFrame frame = AtkFrameParser.ParseBattery(response, read);
                if (!frame.Valid)
                {
                    lastError = frame.Error;
                    continue;
                }

                reading.Percent = frame.Percent;
                reading.Charging = frame.Charging;
                reading.Millivolts = frame.Millivolts;
                reading.Confidence = ReadingConfidence.High;
                reading.Raw = BitConverter.ToString(response, 0, read);
                reading.Error = null;
                reading.Timestamp = DateTime.Now;
                return reading;
            }

            // 超时后释放句柄，避免长期占用设备；下一次查询会重新打开。
            CloseConnection();
            reading.Error = lastError;
            return reading;
        }

        public void Dispose()
        {
            _connection.Dispose();
        }
    }

    /// <summary>
    /// 一帧 0x04 电量响应的解析结果。校验不通过时 Valid=false，Error 里是要
    /// 显示给用户的原因文本（不填任何猜测出来的数字）。
    /// </summary>
    internal sealed class AtkBatteryFrame
    {
        public bool Valid;
        public int Percent;
        public bool Charging;
        public int Millivolts;
        public string Error;

        public static AtkBatteryFrame Reject(string error)
        {
            AtkBatteryFrame frame = new AtkBatteryFrame();
            frame.Valid = false;
            frame.Error = error;
            return frame;
        }
    }

    /// <summary>
    /// ATK 8K 接收器的 17 字节电量帧解析器。抽成纯函数（不碰句柄、不碰日志），
    /// 方便在 TestHarness 里用真实帧字节做回归断言。
    ///
    /// 帧布局（实测稳定帧 08-04-00-00-00-02-28-00-0F-57-00-00-00-00-00-00-B9）：
    ///   [0] 0x08 报告号        [1] 0x04 电量命令
    ///   [2] 0x00 状态字节      [3] 地址      [4] 保留
    ///   [5] 0x02 载荷长度
    ///   [6] 百分比（0x28=40，0x37=55）
    ///   [7] 充电标志
    ///   [8..9] 电芯电压，大端 mV
    ///   [10..15] 保留
    ///   [16] 校验 = 85 - (前 16 字节之和) &amp; 0xFF
    ///
    /// 40 与 55 是同一个字节 [6] 的两个取值（设备侧电量计的两个稳定状态），
    /// 不是不同字节、也不是不同帧布局，所以这里不需要“二选一”，只要把 [6]
    /// 原样读出来，并把电压一并交给界面，让用户用电压这个物理量交叉判断。
    /// </summary>
    internal static class AtkFrameParser
    {
        public const int FrameLength = 17;
        public const byte ReportId = 0x08;
        public const byte BatteryCommand = 0x04;
        public const byte ExpectedStatus = 0x00;
        public const byte ExpectedPayloadLength = 0x02;
        public const int MinPercent = 0;
        public const int MaxPercent = 100;
        public const int MinMillivolts = 2500;
        public const int MaxMillivolts = 5000;

        /// <summary>协议校验：85 - (前 16 字节之和) &amp; 0xFF。与 AtkProtocol.CreateRequest 同一算法。</summary>
        public static byte Checksum(byte[] frame, int length)
        {
            if (frame == null || length <= 0)
            {
                return 0;
            }
            int sum = 0;
            for (int i = 0; i < length - 1 && i < frame.Length; i++)
            {
                sum += frame[i];
            }
            return (byte)(85 - (sum & 255));
        }

        /// <summary>校验和是否与帧内 [16] 一致。拿它单独断言真实帧（0xB9 / 0xB5）。</summary>
        public static bool HasValidChecksum(byte[] frame, int length)
        {
            if (frame == null || length < FrameLength || frame.Length < FrameLength)
            {
                return false;
            }
            return frame[FrameLength - 1] == Checksum(frame, FrameLength);
        }

        /// <summary>解析一帧电量响应。length 是实际读到的字节数。</summary>
        public static AtkBatteryFrame ParseBattery(byte[] frame, int length)
        {
            if (frame == null || length < FrameLength || frame.Length < FrameLength)
            {
                return AtkBatteryFrame.Reject("响应长度不足（期望 " +
                    FrameLength.ToString(CultureInfo.InvariantCulture) + " 字节）");
            }
            if (frame[0] != ReportId)
            {
                return AtkBatteryFrame.Reject("响应报告号异常：0x" +
                    frame[0].ToString("X2", CultureInfo.InvariantCulture));
            }
            if (frame[1] != BatteryCommand)
            {
                return AtkBatteryFrame.Reject("响应命令异常：0x" +
                    frame[1].ToString("X2", CultureInfo.InvariantCulture));
            }
            if (frame[2] != ExpectedStatus)
            {
                return AtkBatteryFrame.Reject("响应状态异常：0x" +
                    frame[2].ToString("X2", CultureInfo.InvariantCulture));
            }
            if (frame[5] != ExpectedPayloadLength)
            {
                return AtkBatteryFrame.Reject("响应载荷长度异常：0x" +
                    frame[5].ToString("X2", CultureInfo.InvariantCulture));
            }
            byte expected = Checksum(frame, FrameLength);
            if (frame[FrameLength - 1] != expected)
            {
                return AtkBatteryFrame.Reject("响应校验和错误：帧内 0x" +
                    frame[FrameLength - 1].ToString("X2", CultureInfo.InvariantCulture) +
                    "，应为 0x" + expected.ToString("X2", CultureInfo.InvariantCulture));
            }

            int percent = frame[6];
            if (percent < MinPercent || percent > MaxPercent)
            {
                return AtkBatteryFrame.Reject("电量值 " + percent.ToString(CultureInfo.InvariantCulture) + "% 超出 0–100");
            }

            int millivolts = (frame[8] << 8) | frame[9];
            if (millivolts < MinMillivolts || millivolts > MaxMillivolts)
            {
                return AtkBatteryFrame.Reject("电压 " + millivolts.ToString(CultureInfo.InvariantCulture) + " mV 异常");
            }

            AtkBatteryFrame result = new AtkBatteryFrame();
            result.Valid = true;
            result.Percent = percent;
            result.Charging = frame[7] != 0;
            result.Millivolts = millivolts;
            result.Error = null;
            return result;
        }
    }
}
