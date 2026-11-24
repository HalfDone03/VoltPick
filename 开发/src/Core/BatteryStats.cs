using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeviceBattery
{
    /// <summary>一段连续放电或充电区间：从首个读数到最后一个读数。</summary>
    internal sealed class BatterySession
    {
        public long Start;
        public long End;
        public int From;
        public int To;
        public bool Completed;

        public long Seconds { get { return Math.Max(0, End - Start); } }
        public double RatePerHour
        {
            get
            {
                double hours = Seconds / 3600.0;
                return hours <= 0 ? 0 : (To - From) / hours;
            }
        }
    }

    /// <summary>
    /// 由真实历史采样算出的电量统计：充放电区间、平均速率、续航与剩余时间。
    /// 数据不够时对应字段是 null，界面显示「数据不足」，绝不拿 0 或猜测值糊弄。
    /// </summary>
    internal sealed class BatteryStats
    {
        public const int MinDrop = 5;
        public const long MinDischargeSeconds = 30 * 60;
        public const int MinRise = 5;
        public const long MinChargeSeconds = 5 * 60;
        public const long MinTotalSeconds = 3600;

        public List<BatterySession> Discharges = new List<BatterySession>();
        public List<BatterySession> Charges = new List<BatterySession>();
        public double? DischargeRatePerHour;
        public double? ChargeRatePerHour;
        public double? BatteryLifeHours;
        public double? RemainingHours;
        public double? FullChargeHours;
        public double? ToFullHours;
        public double? AverageChargeSessionHours;
        public long? LastCharged;
        public int Readings;
        public long MonitoredSeconds;
        public long? FirstReading;

        private static BatterySession MakeSession(List<BatteryHistorySample> run, bool charging)
        {
            if (run.Count == 0) return null;
            BatteryHistorySample first = run[0];
            BatteryHistorySample last = run[run.Count - 1];
            int to = charging ? first.Percent : last.Percent;
            long end = last.Time;
            if (charging)
            {
                for (int i = 0; i < run.Count; i++)
                {
                    if (run[i].Percent > to) to = run[i].Percent;
                }
                for (int i = 0; i < run.Count; i++)
                {
                    if (run[i].Percent == to) { end = run[i].Time; break; }
                }
            }
            BatterySession s = new BatterySession();
            s.Start = first.Time;
            s.End = end;
            s.From = first.Percent;
            s.To = to;
            s.Completed = charging && (to >= 100 || ReachedFull(run));
            return s;
        }

        private static bool ReachedFull(List<BatteryHistorySample> run)
        {
            for (int i = 0; i < run.Count; i++)
            {
                if (run[i].Charging == 1 && run[i].Percent >= 100) return true;
            }
            return false;
        }

        /// <summary>
        /// samples 按时间从旧到新。currentPercent/currentCharging 是当前设备读数（可能为 null）。
        /// </summary>
        internal static BatteryStats Compute(IList<BatteryHistorySample> samples,
            int? currentPercent, bool? currentCharging)
        {
            BatteryStats st = new BatteryStats();
            if (samples == null || samples.Count == 0) return st;
            st.Readings = samples.Count;
            st.FirstReading = samples[0].Time;

            // 把样本切成「同一状态、且没有监测断点」的连续段。
            int i = 0;
            while (i < samples.Count)
            {
                int charging = samples[i].Charging;
                int j = i + 1;
                while (j < samples.Count &&
                       !BatteryHistory.IsGap(samples[j - 1], samples[j]) &&
                       samples[j].Charging == charging)
                {
                    j++;
                }
                List<BatteryHistorySample> run = new List<BatteryHistorySample>();
                for (int k = i; k < j; k++) run.Add(samples[k]);
                // 放电段要把切换到充电时的第一个读数也算进来（那一刻仍是电池电量）。
                if (charging == 0 && j < samples.Count && !BatteryHistory.IsGap(samples[j - 1], samples[j]))
                {
                    BatteryHistorySample edge = new BatteryHistorySample();
                    edge.Time = samples[j].Time;
                    edge.Percent = samples[j].Percent;
                    edge.Charging = 0;
                    run.Add(edge);
                }
                if (run.Count > 1 && charging != 2)
                {
                    BatterySession s = MakeSession(run, charging == 1);
                    if (s != null)
                    {
                        if (charging == 1) st.Charges.Add(s);
                        else st.Discharges.Add(s);
                    }
                }
                i = j;
            }

            // 监测总时长：把每段连续监测的时间加起来，断点期间不算。
            long? segmentStart = null;
            BatteryHistorySample previous = null;
            for (int k = 0; k < samples.Count; k++)
            {
                BatteryHistorySample sample = samples[k];
                if (previous == null || BatteryHistory.IsGap(previous, sample))
                {
                    if (segmentStart.HasValue && previous != null)
                    {
                        st.MonitoredSeconds += Math.Max(0, previous.Time - segmentStart.Value);
                    }
                    segmentStart = sample.Time;
                }
                previous = sample;
            }
            if (segmentStart.HasValue && previous != null)
            {
                st.MonitoredSeconds += Math.Max(0, previous.Time - segmentStart.Value);
            }

            // 放电统计：只统计掉了至少 5 个点且持续至少 30 分钟的段。
            long drop = 0, dischargeSeconds = 0;
            for (int k = 0; k < st.Discharges.Count; k++)
            {
                BatterySession s = st.Discharges[k];
                if (s.From >= s.To + MinDrop && s.Seconds >= MinDischargeSeconds)
                {
                    drop += s.From - s.To;
                    dischargeSeconds += s.Seconds;
                }
            }
            if (dischargeSeconds >= MinTotalSeconds && drop >= MinDrop)
            {
                double rate = drop / (dischargeSeconds / 3600.0);
                st.DischargeRatePerHour = rate;
                st.BatteryLifeHours = 100.0 / rate;
                if (currentPercent.HasValue && currentCharging.HasValue && !currentCharging.Value &&
                    currentPercent.Value >= 0)
                {
                    st.RemainingHours = currentPercent.Value / rate;
                }
            }

            // 充电统计：涨了至少 5 个点且持续至少 5 分钟的段。
            long rise = 0, chargeSeconds = 0;
            int countedCharges = 0;
            for (int k = 0; k < st.Charges.Count; k++)
            {
                BatterySession s = st.Charges[k];
                if (s.To >= s.From + MinRise && s.Seconds >= MinChargeSeconds)
                {
                    rise += s.To - s.From;
                    chargeSeconds += s.Seconds;
                    countedCharges++;
                }
            }
            if (countedCharges > 0 && rise >= MinRise && chargeSeconds > 0)
            {
                double rate = rise / (chargeSeconds / 3600.0);
                st.ChargeRatePerHour = rate;
                st.FullChargeHours = 100.0 / rate;
                st.AverageChargeSessionHours = chargeSeconds / 3600.0 / countedCharges;
                if (currentPercent.HasValue && currentCharging.HasValue && currentCharging.Value &&
                    currentPercent.Value >= 0 && currentPercent.Value < 100)
                {
                    st.ToFullHours = (100 - currentPercent.Value) / rate;
                }
            }
            for (int k = 0; k < st.Charges.Count; k++)
            {
                BatterySession s = st.Charges[k];
                if (s.To > s.From || s.Completed)
                {
                    if (!st.LastCharged.HasValue || s.End > st.LastCharged.Value) st.LastCharged = s.End;
                }
            }
            return st;
        }

        /// <summary>"3 h 20 min" / "45 min" / "2 d 4 h"，按数据精度取整。</summary>
        internal static string FormatHours(double hours)
        {
            long minutes = (long)Math.Round(hours * 60.0);
            if (minutes < 1) return "不到 1 分钟";
            if (minutes < 60) return minutes.ToString(CultureInfo.CurrentCulture) + " 分钟";
            if (minutes < 48 * 60)
            {
                long h = minutes / 60;
                long m = minutes % 60;
                if (h >= 10 || m == 0) return ((minutes + 30) / 60).ToString(CultureInfo.CurrentCulture) + " 小时";
                return h + " 小时 " + m + " 分钟";
            }
            long totalHours = (minutes + 30) / 60;
            if (totalHours % 24 == 0) return (totalHours / 24).ToString(CultureInfo.CurrentCulture) + " 天";
            return (totalHours / 24).ToString(CultureInfo.CurrentCulture) + " 天 " +
                   (totalHours % 24).ToString(CultureInfo.CurrentCulture) + " 小时";
        }

        internal static string FormatRate(double rate)
        {
            return (rate >= 10 ? rate.ToString("0", CultureInfo.CurrentCulture)
                               : rate.ToString("0.0", CultureInfo.CurrentCulture)) + " %/小时";
        }
    }
}
