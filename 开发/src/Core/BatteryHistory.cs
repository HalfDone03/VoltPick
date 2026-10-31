using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace DeviceBattery
{
    internal sealed class BatteryHistorySample
    {
        // Unix 秒；比 DateTime 的 JSON 格式更稳定，也便于画图。
        public long Time;
        public int Percent;
        public int Charging; // 0=未充电，1=充电，2=未知
    }

    internal sealed class BatteryHistorySeries
    {
        public string Id;
        public string Name;
        public string Category;
        public List<BatteryHistorySample> Samples = new List<BatteryHistorySample>();

        public BatteryHistorySeries Clone()
        {
            BatteryHistorySeries copy = new BatteryHistorySeries();
            copy.Id = Id;
            copy.Name = Name;
            copy.Category = Category;
            if (Samples != null)
            {
                for (int i = 0; i < Samples.Count; i++)
                {
                    BatteryHistorySample sample = Samples[i];
                    if (sample == null)
                    {
                        continue;
                    }
                    BatteryHistorySample item = new BatteryHistorySample();
                    item.Time = sample.Time;
                    item.Percent = sample.Percent;
                    item.Charging = sample.Charging;
                    copy.Samples.Add(item);
                }
            }
            return copy;
        }
    }

    internal sealed class BatteryHistoryData
    {
        public int Version = 1;
        public List<BatteryHistorySeries> Series = new List<BatteryHistorySeries>();
    }

    /// <summary>一次历史采样决策的结果。</summary>
    internal enum HistorySampleDecision
    {
        /// <summary>可以落一个采样点。</summary>
        Record = 0,

        /// <summary>与上一点同值同充电状态，且还没到续点时间：这一轮丢弃。</summary>
        SkipDuplicate = 1
    }

    /// <summary>
    /// 历史采样去重闸门。只决定“这一次读数要不要落成历史点”，不修改读数、不插值补点，
    /// 所以曲线上的每个点都对应一次真实读数。规则：
    /// 1. 与上一个点同值同充电状态：超过 <see cref="SameValueKeepAliveSeconds"/> 才补一个点，
    ///    避免每轮刷新都写一个同值点（7 天就会顶到 1200 上限），曲线也不会被同值点灌满；
    /// 2. 数值变化或充电状态变化：立即落点。跳变是否可信由上游（BatteryService）在
    ///    查询层用“连续两次一致”确认，这里只接收已确认值，不再重复判定；
    /// 3. 系统时间回拨：不写倒序点，避免图表折返。
    /// </summary>
    internal sealed class HistorySampleGate
    {
        /// <summary>同值续点间隔：数值没变时最多每 30 分钟补一个点。</summary>
        public const int SameValueKeepAliveSeconds = 1800;

        private bool _hasLast;
        private int _lastPercent;
        private int _lastCharging;
        private long _lastSeconds;

        /// <summary>用已有的最后一个采样点恢复状态（重新载入历史文件时用）。</summary>
        public void Seed(int percent, int charging, long seconds)
        {
            _hasLast = true;
            _lastPercent = percent;
            _lastCharging = charging;
            _lastSeconds = seconds;
        }

        public HistorySampleDecision Evaluate(int percent, int charging, long seconds)
        {
            if (!_hasLast)
            {
                Commit(percent, charging, seconds);
                return HistorySampleDecision.Record;
            }
            if (seconds < _lastSeconds)
            {
                // 系统时间回拨：不写倒序样本，也不改状态，等时间正常后继续。
                return HistorySampleDecision.SkipDuplicate;
            }
            if (percent != _lastPercent || charging != _lastCharging)
            {
                Commit(percent, charging, seconds);
                return HistorySampleDecision.Record;
            }
            if (seconds - _lastSeconds >= SameValueKeepAliveSeconds)
            {
                // 同值续点：保证长时间不变的曲线仍然有时间跨度，但不会每轮都写。
                Commit(percent, charging, seconds);
                return HistorySampleDecision.Record;
            }
            return HistorySampleDecision.SkipDuplicate;
        }

        private void Commit(int percent, int charging, long seconds)
        {
            _hasLast = true;
            _lastPercent = percent;
            _lastCharging = charging;
            _lastSeconds = seconds;
        }
    }

    /// <summary>
    /// 本地电量历史。只保留最近 7 天，每台设备最多 1200 个采样点；
    /// 数据与设置分开存放，避免 settings.json 随历史增长。
    /// </summary>
    internal static class BatteryHistory
    {
        public const int RetentionDays = 7;
        public const int MaxSamplesPerDevice = 1200;
        private const int MaxGapSeconds = 2 * 60 * 60;

        private static readonly object Gate = new object();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private static readonly Dictionary<string, HistorySampleGate> Gates =
            new Dictionary<string, HistorySampleGate>(StringComparer.Ordinal);
        private static BatteryHistoryData _data;
        private static string _loadedPath;

        public static bool Enabled
        {
            get { return AppSettings.Current == null || AppSettings.Current.HistoryEnabled; }
        }

        public static string FilePath
        {
            get { return Path.Combine(AppSettings.ConfigDirectory, "battery-history.json"); }
        }

        public static void ResetForTests()
        {
            lock (Gate)
            {
                _data = null;
                _loadedPath = null;
                Gates.Clear();
            }
        }

        public static void Record(DeviceState device, DateTime now)
        {
            if (device == null || device.LastReading == null || !device.LastReading.Percent.HasValue)
            {
                return;
            }
            if (!Enabled || string.IsNullOrEmpty(device.Id))
            {
                return;
            }

            int percent = device.LastReading.Percent.Value;
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            int charging = device.LastReading.Charging.HasValue
                ? (device.LastReading.Charging.Value ? 1 : 0)
                : (device.Status == DeviceStatus.Charging ? 1 : 2);
            long seconds = ToUnixSeconds(now);

            lock (Gate)
            {
                BatteryHistoryData data = EnsureLoaded();
                BatteryHistorySeries series = FindSeries(data, device.Id);
                if (series == null)
                {
                    series = new BatteryHistorySeries();
                    series.Id = device.Id;
                    data.Series.Add(series);
                }
                series.Name = device.DisplayName;
                series.Category = device.CategoryText;

                HistorySampleGate gate = GateFor(series, device.Id);
                if (gate.Evaluate(percent, charging, seconds) != HistorySampleDecision.Record)
                {
                    // 同值重复读数：这一轮不落点。既不写文件，也不插值补点。
                    TrimEmpty(data);
                    return;
                }

                BatteryHistorySample sample = new BatteryHistorySample();
                sample.Time = seconds;
                sample.Percent = percent;
                sample.Charging = charging;
                series.Samples.Add(sample);

                PruneAll(data, seconds);
                SaveLocked(data);
            }
        }

        /// <summary>取（必要时新建）某台设备的采样闸门，并用已落盘的最后一个点初始化。</summary>
        private static HistorySampleGate GateFor(BatteryHistorySeries series, string id)
        {
            HistorySampleGate gate;
            if (Gates.TryGetValue(id, out gate) && gate != null)
            {
                return gate;
            }
            gate = new HistorySampleGate();
            if (series != null && series.Samples != null && series.Samples.Count > 0)
            {
                BatteryHistorySample last = series.Samples[series.Samples.Count - 1];
                if (last != null)
                {
                    gate.Seed(last.Percent, last.Charging, last.Time);
                }
            }
            Gates[id] = gate;
            return gate;
        }

        public static List<BatteryHistorySeries> GetSeries()
        {
            lock (Gate)
            {
                BatteryHistoryData data = EnsureLoaded();
                List<BatteryHistorySeries> result = new List<BatteryHistorySeries>();
                for (int i = 0; i < data.Series.Count; i++)
                {
                    BatteryHistorySeries series = data.Series[i];
                    if (series == null || series.Samples == null || series.Samples.Count == 0)
                    {
                        continue;
                    }
                    result.Add(series.Clone());
                }
                result.Sort(delegate(BatteryHistorySeries a, BatteryHistorySeries b)
                {
                    return string.Compare(a.Name ?? "", b.Name ?? "", StringComparison.CurrentCultureIgnoreCase);
                });
                return result;
            }
        }

        public static BatteryHistorySeries GetSeries(string id)
        {
            lock (Gate)
            {
                BatteryHistoryData data = EnsureLoaded();
                BatteryHistorySeries series = FindSeries(data, id);
                return series == null ? null : series.Clone();
            }
        }

        /// <summary>
        /// 删掉一台设备的全部采样（设置页「移除记录」时用）。其余设备的曲线不受影响；
        /// 该设备的采样闸门也一并复位，免得下次记录时沿用旧基线。
        /// </summary>
        public static bool ClearSeries(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }
            lock (Gate)
            {
                BatteryHistoryData data = EnsureLoaded();
                int index = -1;
                for (int i = 0; i < data.Series.Count; i++)
                {
                    BatteryHistorySeries series = data.Series[i];
                    if (series != null && string.Equals(series.Id, id, StringComparison.Ordinal))
                    {
                        index = i;
                        break;
                    }
                }
                if (index < 0)
                {
                    return false;
                }
                data.Series.RemoveAt(index);
                Gates.Remove(id);
                SaveLocked(data);
                return true;
            }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                _data = new BatteryHistoryData();
                _loadedPath = FilePath;
                Gates.Clear();
                string[] files = new string[]
                {
                    _loadedPath,
                    _loadedPath + ".bak",
                    _loadedPath + ".tmp"
                };
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        if (File.Exists(files[i]))
                        {
                            File.Delete(files[i]);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("清空电量历史失败：" + ex.Message);
                    }
                }
            }
        }

        public static int CountSamples()
        {
            lock (Gate)
            {
                BatteryHistoryData data = EnsureLoaded();
                int count = 0;
                for (int i = 0; i < data.Series.Count; i++)
                {
                    if (data.Series[i] != null && data.Series[i].Samples != null)
                    {
                        count += data.Series[i].Samples.Count;
                    }
                }
                return count;
            }
        }

        public static DateTime FromUnixSeconds(long seconds)
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return epoch.AddSeconds(seconds).ToLocalTime();
        }

        private static long ToUnixSeconds(DateTime value)
        {
            DateTime utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(utc - epoch).TotalSeconds;
        }

        private static BatteryHistoryData EnsureLoaded()
        {
            string path = FilePath;
            if (_data != null && string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return _data;
            }

            _loadedPath = path;
            _data = new BatteryHistoryData();
            try
            {
                if (!File.Exists(path))
                {
                    return _data;
                }
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrEmpty(text))
                {
                    return _data;
                }
                BatteryHistoryData loaded = Json.Deserialize<BatteryHistoryData>(text);
                if (loaded == null || loaded.Series == null)
                {
                    return _data;
                }
                for (int i = 0; i < loaded.Series.Count; i++)
                {
                    BatteryHistorySeries series = loaded.Series[i];
                    if (series == null || string.IsNullOrEmpty(series.Id))
                    {
                        continue;
                    }
                    if (series.Samples == null)
                    {
                        series.Samples = new List<BatteryHistorySample>();
                    }
                    _data.Series.Add(series);
                }
            }
            catch (Exception ex)
            {
                Log.Error("读取电量历史失败：" + ex.Message);
                _data = new BatteryHistoryData();
            }
            return _data;
        }

        private static BatteryHistorySeries FindSeries(BatteryHistoryData data, string id)
        {
            if (data == null || data.Series == null || string.IsNullOrEmpty(id))
            {
                return null;
            }
            for (int i = 0; i < data.Series.Count; i++)
            {
                BatteryHistorySeries series = data.Series[i];
                if (series != null && string.Equals(series.Id, id, StringComparison.Ordinal))
                {
                    return series;
                }
            }
            return null;
        }

        /// <summary>
        /// 按本次采样的时间裁剪所有设备序列：7 天以外、超过 1200 点的旧样本都丢掉，
        /// 并且移除已经空掉的序列。以前只裁剪当前设备，长时间没读到的设备会一直留着过期数据。
        /// </summary>
        private static void PruneAll(BatteryHistoryData data, long nowSeconds)
        {
            if (data == null || data.Series == null)
            {
                return;
            }
            for (int i = 0; i < data.Series.Count; i++)
            {
                BatteryHistorySeries series = data.Series[i];
                if (series == null || series.Samples == null)
                {
                    continue;
                }
                Trim(series, nowSeconds);
            }
            TrimEmpty(data);
        }

        private static void Trim(BatteryHistorySeries series, long nowSeconds)
        {
            long cutoff = nowSeconds - RetentionDays * 24L * 60L * 60L;
            int first = 0;
            while (first < series.Samples.Count && series.Samples[first].Time < cutoff)
            {
                first++;
            }
            if (first > 0)
            {
                series.Samples.RemoveRange(0, first);
            }
            int excess = series.Samples.Count - MaxSamplesPerDevice;
            if (excess > 0)
            {
                series.Samples.RemoveRange(0, excess);
            }
        }

        private static void TrimEmpty(BatteryHistoryData data)
        {
            for (int i = data.Series.Count - 1; i >= 0; i--)
            {
                BatteryHistorySeries series = data.Series[i];
                if (series == null || series.Samples == null || series.Samples.Count == 0)
                {
                    data.Series.RemoveAt(i);
                }
            }
        }

        private static void SaveLocked(BatteryHistoryData data)
        {
            try
            {
                string path = FilePath;
                string folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }
                string text = Json.Serialize(data);
                string temp = path + ".tmp";
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    string backup = path + ".bak";
                    try
                    {
                        File.Replace(temp, path, backup, true);
                    }
                    catch (Exception)
                    {
                        File.Copy(temp, path, true);
                        File.Delete(temp);
                    }
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            catch (Exception ex)
            {
                Log.Error("保存电量历史失败：" + ex.Message);
            }
        }

        // 历史图表使用：把相邻样本间隔超过 2 小时视为断点。
        public static bool IsGap(BatteryHistorySample previous, BatteryHistorySample current)
        {
            return previous == null || current == null ||
                   current.Time - previous.Time > MaxGapSeconds;
        }

        public static string FormatTime(DateTime value)
        {
            return value.ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
        }
    }
}
