using System;
using System.Collections.Generic;

namespace DeviceBattery
{
    internal enum AirohaReadOutcome
    {
        Success,
        NotSupported,
        Failed
    }

    /// <summary>
    /// 记录每台耳机对 Airoha 厂商服务的支持情况，避免每次刷新都重复探测。
    /// 只缓存本次运行；不跨重启，避免固件升级后长期使用错误结论。
    /// </summary>
    internal sealed class AirohaCapabilityCache
    {
        private sealed class Entry
        {
            public int Failures;
            public DateTime RetryAfter = DateTime.MinValue;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();

        public bool ShouldTry(string key, DateTime now)
        {
            if (string.IsNullOrEmpty(key))
            {
                return true;
            }
            lock (_gate)
            {
                Entry entry;
                if (!_entries.TryGetValue(key, out entry))
                {
                    return true;
                }
                return entry.RetryAfter <= now;
            }
        }

        public void Record(string key, AirohaReadOutcome outcome, DateTime now)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }
            lock (_gate)
            {
                Entry entry;
                if (!_entries.TryGetValue(key, out entry))
                {
                    entry = new Entry();
                    _entries[key] = entry;
                }

                if (outcome == AirohaReadOutcome.Success)
                {
                    entry.Failures = 0;
                    entry.RetryAfter = DateTime.MinValue;
                }
                else if (outcome == AirohaReadOutcome.NotSupported)
                {
                    // 没有该厂商服务的设备，30 分钟内不再重复探测。
                    entry.Failures = 0;
                    entry.RetryAfter = now.AddMinutes(30);
                }
                else
                {
                    // 服务存在但本次读取失败，可能是耳机刚唤醒或链路忙。
                    entry.Failures++;
                    if (entry.Failures >= 2)
                    {
                        entry.Failures = 0;
                        entry.RetryAfter = now.AddMinutes(5);
                    }
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
            }
        }
    }
}
