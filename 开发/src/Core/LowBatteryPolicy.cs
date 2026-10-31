using System.Collections.Generic;

namespace DeviceBattery
{
    /// <summary>低电提醒的纯策略，便于测试；不做 IO、不弹窗。</summary>
    internal static class LowBatteryPolicy
    {
        /// <summary>
        /// 返回本次应提醒的档位；-1 表示不提醒。
        /// 自定义阈值优先于全局档位；自定义阈值只提醒一次，直到充电或回升 5% 重置。
        /// </summary>
        public static int ChooseLevel(int percent, int customThreshold, IList<int> globalLevels,
            ICollection<int> warnedLevels)
        {
            if (percent < 0 || percent > 100)
            {
                return -1;
            }
            if (customThreshold > 0)
            {
                if (percent > customThreshold)
                {
                    return -1;
                }
                return warnedLevels != null && warnedLevels.Contains(customThreshold)
                    ? -1 : customThreshold;
            }

            int chosen = -1;
            if (globalLevels == null)
            {
                return chosen;
            }
            // 不依赖配置数组的顺序：取“电量刚跌到或跌破的最低档位”。
            for (int i = 0; i < globalLevels.Count; i++)
            {
                int level = globalLevels[i];
                if (percent <= level &&
                    (chosen < 0 || level < chosen) &&
                    (warnedLevels == null || !warnedLevels.Contains(level)))
                {
                    chosen = level;
                }
            }
            return chosen;
        }

        /// <summary>把本次已经跨过的档位全部标记，避免连续弹窗。</summary>
        public static void MarkWarned(int percent, int customThreshold, IList<int> globalLevels,
            ICollection<int> warnedLevels)
        {
            if (warnedLevels == null)
            {
                return;
            }
            if (customThreshold > 0)
            {
                warnedLevels.Add(customThreshold);
                return;
            }
            if (globalLevels == null)
            {
                return;
            }
            for (int i = 0; i < globalLevels.Count; i++)
            {
                if (percent <= globalLevels[i])
                {
                    warnedLevels.Add(globalLevels[i]);
                }
            }
        }
    }
}
