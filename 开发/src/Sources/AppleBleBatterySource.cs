using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Devices.Bluetooth.Advertisement;

namespace DeviceBattery
{
    /// <summary>
    /// 苹果 AirPods / Beats 的电量不在 Windows 的电池服务里，但在 BLE 邻近配对广播里明文播着
    /// （厂商数据公司 ID 0x004C，类型 0x07）。这里只被动听广播，一个字节都不发。
    /// 广播地址会轮换，所以按型号匹配已配对设备，最近最强的那条胜出。
    /// </summary>
    internal sealed class AppleBleBatterySource : IDisposable
    {
        private BluetoothLEAdvertisementWatcher _watcher;
        private readonly object _gate = new object();
        private readonly Dictionary<int, Advert> _latest = new Dictionary<int, Advert>();
        private const int KeepSeconds = 300;
        private const int MaxAddresses = 64;

        internal sealed class Advert
        {
            public int Model;
            public int Left = -1;
            public int Right = -1;
            public int Case = -1;
            public bool LeftCharging;
            public bool RightCharging;
            public bool CaseCharging;
            public int Rssi = -127;
            public DateTime At;
        }

        private static bool IsSingleBatteryModel(int model)
        {
            return model == 0x200A || model == 0x201F || model == 0x202D || model == 0x2003 ||
                   model == 0x2005 || model == 0x2006 || model == 0x2009 || model == 0x200C ||
                   model == 0x200D || model == 0x2010 || model == 0x2017 || model == 0x2025;
        }

        private static int Level(int nibble)
        {
            return nibble <= 10 ? nibble * 10 : -1;
        }

        /// <summary>解析 0x07 邻近配对 TLV。d 从类型字节开始。</summary>
        internal static bool TryParseProximity(byte[] d, out Advert advert)
        {
            advert = null;
            if (d == null || d.Length < 27 || d[0] != 0x07 || d[1] != 0x19 || d[2] != 0x01) return false;
            int model = d[3] | (d[4] << 8);
            if ((model & 0xFF00) != 0x2000) return false;
            bool single = IsSingleBatteryModel(model);
            bool leftBroadcasts = (d[5] & 0x20) != 0;
            int cur = d[6] & 0x0F;
            int other = d[6] >> 4;
            int chg = d[7] >> 4;
            Advert a = new Advert();
            a.Model = model;
            a.At = DateTime.UtcNow;
            if (single)
            {
                int pct = Level(cur);
                bool charging = (chg & 0x01) != 0;
                if (cur == 0 && other >= 1 && other <= 10)
                {
                    pct = Level(other);
                    charging = (chg & 0x02) != 0;
                }
                if (pct < 0) return false;
                a.Left = pct;
                a.LeftCharging = charging;
                advert = a;
                return true;
            }
            a.Left = Level(leftBroadcasts ? cur : other);
            a.Right = Level(leftBroadcasts ? other : cur);
            a.LeftCharging = a.Left >= 0 && (chg & (leftBroadcasts ? 0x01 : 0x02)) != 0;
            a.RightCharging = a.Right >= 0 && (chg & (leftBroadcasts ? 0x02 : 0x01)) != 0;
            a.Case = Level(d[7] & 0x0F);
            a.CaseCharging = a.Case >= 0 && (chg & 0x04) != 0;
            if (a.Left < 0 && a.Right < 0 && a.Case < 0) return false;
            advert = a;
            return true;
        }

        /// <summary>苹果 Continuity 数据可能前面还有别的 TLV，要走到类型 0x07 为止。</summary>
        internal static bool TryParseManufacturerData(byte[] data, out Advert advert)
        {
            advert = null;
            if (data == null) return false;
            int i = 0;
            while (i + 1 < data.Length)
            {
                int type = data[i];
                int len = data[i + 1];
                if (i + 2 + len > data.Length) break;
                if (type == 0x07)
                {
                    byte[] tlv = new byte[2 + len];
                    Array.Copy(data, i, tlv, 0, tlv.Length);
                    return TryParseProximity(tlv, out advert);
                }
                i += 2 + len;
            }
            return false;
        }

        /// <summary>开始被动监听。只调一次，重复调用安全。</summary>
        public bool Start()
        {
            try
            {
                if (_watcher != null) return true;
                _watcher = new BluetoothLEAdvertisementWatcher();
                _watcher.ScanningMode = BluetoothLEScanningMode.Passive;
                _watcher.Received += OnReceived;
                _watcher.Start();
                return true;
            }
            catch (Exception ex)
            {
                // 蓝牙关闭或没有 BLE 适配器时这里必然失败，是预期情况，不写 WARN 刷屏。
                Log.Debug("苹果耳机广播监听未启动：" + ex.Message);
                _watcher = null;
                return false;
            }
        }

        private void OnReceived(BluetoothLEAdvertisementWatcher sender,
            BluetoothLEAdvertisementReceivedEventArgs args)
        {
            try
            {
                if (args.Advertisement == null) return;
                var manufacturerData = args.Advertisement.GetManufacturerDataByCompanyId(0x004C);
                if (manufacturerData == null) return;
                for (int i = 0; i < manufacturerData.Count; i++)
                {
                    var entry = manufacturerData[i];
                    if (entry == null || entry.Data == null) continue;
                    byte[] bytes = ToArray(entry.Data);
                    Advert advert;
                    if (!TryParseManufacturerData(bytes, out advert)) continue;
                    advert.Rssi = args.RawSignalStrengthInDBm;
                    long address = (long)args.BluetoothAddress;
                    lock (_gate)
                    {
                        Advert previous;
                        // 同一型号可能有多只耳朵/多台设备广播，取信号最强的那条。
                        if (!_latest.TryGetValue(advert.Model, out previous) ||
                            advert.Rssi >= previous.Rssi)
                        {
                            _latest[advert.Model] = advert;
                        }
                        // 地址轮换：按时间淘汰老广播，并限制总数。
                        DateTime cutoff = DateTime.UtcNow.AddSeconds(-KeepSeconds);
                        List<int> stale = new List<int>();
                        foreach (KeyValuePair<int, Advert> pair in _latest)
                        {
                            if (pair.Value.At < cutoff) stale.Add(pair.Key);
                        }
                        for (int s = 0; s < stale.Count; s++) _latest.Remove(stale[s]);
                        while (_latest.Count > MaxAddresses)
                        {
                            int oldest = 0;
                            DateTime oldestAt = DateTime.MaxValue;
                            foreach (KeyValuePair<int, Advert> pair in _latest)
                            {
                                if (pair.Value.At < oldestAt) { oldestAt = pair.Value.At; oldest = pair.Key; }
                            }
                            _latest.Remove(oldest);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 广播是尽力而为的，任何一次解析异常都不该影响托盘进程。
            }
        }

        private static byte[] ToArray(Windows.Storage.Streams.IBuffer buffer)
        {
            using (Windows.Storage.Streams.DataReader reader =
                Windows.Storage.Streams.DataReader.FromBuffer(buffer))
            {
                byte[] bytes = new byte[buffer.Length];
                reader.ReadBytes(bytes);
                return bytes;
            }
        }

        /// <summary>取某个型号（已配对设备 PID）最近最强的广播。</summary>
        public Advert Get(int model)
        {
            lock (_gate)
            {
                Advert advert;
                return _latest.TryGetValue(model, out advert) ? advert : null;
            }
        }

        public void Dispose()
        {
            if (_watcher == null) return;
            try
            {
                _watcher.Received -= OnReceived;
                if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                {
                    _watcher.Stop();
                }
            }
            catch (Exception) { }
            _watcher = null;
        }
    }
}
