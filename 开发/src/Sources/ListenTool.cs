using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace DeviceBattery
{
    /// <summary>
    /// 设备报文探测工具（--listen VID:PID）。列出某个设备的全部 HID 集合，只读地取
    /// 输入/特征报告，再听一小段时间记录设备主动推来的报告。整个过程一个字节都不写，
    /// 所以不会改鼠标 DPI、不会切换键盘固件、也不会碰配对状态；拿到的文本可以直接贴
    /// 到反馈里，用来给新设备加协议。
    /// </summary>
    internal static class ListenTool
    {
        private const int MaxFrames = 40;

        /// <summary>"1D57:FA60" -&gt; (0x1D57, 0xFA60)。大小写不敏感，忽略首尾空白。</summary>
        internal static bool TryParseVidPid(string text, out int vid, out int pid)
        {
            vid = 0;
            pid = 0;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            string[] parts = text.Trim().Split(':');
            if (parts.Length != 2)
            {
                return false;
            }
            return int.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid) &&
                   int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid) &&
                   vid >= 0 && vid <= 0xFFFF && pid >= 0 && pid <= 0xFFFF;
        }

        internal static string Hex4(int value)
        {
            return value.ToString("X4", CultureInfo.InvariantCulture);
        }

        private static string Hex(byte[] buffer, int length)
        {
            if (buffer == null || length <= 0)
            {
                return "";
            }
            if (length > buffer.Length)
            {
                length = buffer.Length;
            }
            int shown = Math.Min(64, length);
            StringBuilder text = new StringBuilder(shown * 3 + 16);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0)
                {
                    text.Append(' ');
                }
                text.Append(buffer[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            if (length > shown)
            {
                text.Append(" ... (").Append(length.ToString(CultureInfo.InvariantCulture)).Append(" bytes)");
            }
            return text.ToString();
        }

        private static string Label(HidDeviceInfo info)
        {
            return "if" + info.InterfaceNumber.ToString(CultureInfo.InvariantCulture) + " " +
                info.UsagePage.ToString("X4", CultureInfo.InvariantCulture) + ":" +
                info.Usage.ToString("X4", CultureInfo.InvariantCulture) + " in/out/feature " +
                info.InputReportLength.ToString(CultureInfo.InvariantCulture) + "/" +
                info.OutputReportLength.ToString(CultureInfo.InvariantCulture) + "/" +
                info.FeatureReportLength.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 生成探测报告。milliseconds 是监听设备主动推报告的时长（毫秒）。
        /// </summary>
        internal static string Run(int vid, int pid, int milliseconds)
        {
            if (milliseconds < 100)
            {
                milliseconds = 100;
            }
            StringBuilder outText = new StringBuilder();
            outText.Append(AppInfo.DisplayName).Append(" listen ")
                .Append(Hex4(vid)).Append(':').Append(Hex4(pid)).AppendLine();

            List<HidDeviceInfo> all = HidEnumerator.FindAll();
            List<HidDeviceInfo> found = new List<HidDeviceInfo>();
            for (int i = 0; i < all.Count; i++)
            {
                HidDeviceInfo info = all[i];
                if (info != null && info.VendorId == vid && info.ProductId == pid)
                {
                    found.Add(info);
                }
            }
            if (found.Count == 0)
            {
                outText.AppendLine("没找到这个设备的 HID 集合，接收器插上了吗？");
                return outText.ToString();
            }
            outText.Append("产品名：").AppendLine(found[0].ProductName ?? "");

            // 每个集合开一个后台线程收主动推送的报告。报告带集合序号、相对时间与字节。
            List<Thread> threads = new List<Thread>();
            object gate = new object();
            List<string> pushed = new List<string>();
            DateTime start = DateTime.UtcNow;

            for (int i = 0; i < found.Count; i++)
            {
                HidDeviceInfo info = found[i];
                outText.Append("[").Append(i.ToString(CultureInfo.InvariantCulture)).Append("] ")
                    .AppendLine(Label(info));

                // 控制管道上的被动读取：按报告 ID 0..8 各取一次输入/特征报告。
                // 无报告编号的集合对所有 ID 回同一份数据，这里去重只留一条。
                HidConnection poll = new HidConnection();
                if (info.FeatureReportLength > 0)
                {
                    if (!poll.OpenFeatureOnly(info.Path))
                    {
                        outText.AppendLine("  无法打开（可能被厂商软件占用），跳过此集合的被动读取");
                    }
                    else
                    {
                        byte[] lastFeature = null;
                        for (byte id = 0; id <= 8; id++)
                        {
                            byte[] buffer = new byte[info.FeatureReportLength];
                            buffer[0] = id;
                            if (poll.GetFeature(buffer) && !SamePayload(buffer, lastFeature))
                            {
                                outText.Append("  get feature #")
                                    .Append(id.ToString(CultureInfo.InvariantCulture))
                                    .Append(": ").AppendLine(Hex(buffer, buffer.Length));
                                lastFeature = buffer;
                            }
                        }
                    }
                    poll.Dispose();
                }
                if (info.InputReportLength > 0)
                {
                    HidConnection inputPoll = new HidConnection();
                    if (inputPoll.OpenRead(info.Path))
                    {
                        byte[] lastInput = null;
                        for (byte id = 0; id <= 8; id++)
                        {
                            byte[] buffer = new byte[info.InputReportLength];
                            buffer[0] = id;
                            if (inputPoll.GetInputReport(buffer) && !SamePayload(buffer, lastInput))
                            {
                                outText.Append("  get input   #")
                                    .Append(id.ToString(CultureInfo.InvariantCulture))
                                    .Append(": ").AppendLine(Hex(buffer, buffer.Length));
                                lastInput = buffer;
                            }
                        }
                    }
                    inputPoll.Dispose();

                    // 主动推送的报告单独开线程听，避免阻塞被动读取。
                    int index = i;
                    Thread thread = new Thread(delegate()
                    {
                        HidConnection reader = new HidConnection();
                        try
                        {
                            if (!reader.OpenRead(info.Path))
                            {
                                return;
                            }
                            int count = 0;
                            DateTime until = DateTime.UtcNow.AddMilliseconds(milliseconds);
                            while (count < MaxFrames && DateTime.UtcNow < until)
                            {
                                int remaining = (int)Math.Max(1, (until - DateTime.UtcNow).TotalMilliseconds);
                                byte[] buffer = new byte[Math.Max(1, (int)info.InputReportLength)];
                                int bytesRead;
                                if (reader.Read(buffer, Math.Min(remaining, 200), out bytesRead) && bytesRead > 0)
                                {
                                    long ms = (long)(DateTime.UtcNow - start).TotalMilliseconds;
                                    string line = "  [" + index.ToString(CultureInfo.InvariantCulture) + "] " +
                                        ms.ToString(CultureInfo.InvariantCulture) + " ms  " + Hex(buffer, bytesRead);
                                    lock (gate)
                                    {
                                        pushed.Add(line);
                                    }
                                    count++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            lock (gate)
                            {
                                pushed.Add("  [" + index.ToString(CultureInfo.InvariantCulture) + "] 读取异常：" + ex.Message);
                            }
                        }
                        finally
                        {
                            reader.Dispose();
                        }
                    });
                    thread.IsBackground = true;
                    threads.Add(thread);
                }
            }

            for (int i = 0; i < threads.Count; i++)
            {
                threads[i].Start();
            }
            for (int i = 0; i < threads.Count; i++)
            {
                threads[i].Join(milliseconds + 2000);
            }

            outText.AppendLine();
            outText.Append("监听 ").Append((milliseconds / 1000).ToString(CultureInfo.InvariantCulture))
                .AppendLine(" 秒期间收到的输入报告（集合, 毫秒, 字节）：");
            if (pushed.Count == 0)
            {
                outText.AppendLine("  无");
            }
            else
            {
                for (int i = 0; i < pushed.Count; i++)
                {
                    outText.AppendLine(pushed[i]);
                }
            }
            return outText.ToString();
        }

        /// <summary>报告 ID 之后的内容是否相同（无报告编号的集合对所有 ID 回同一份数据）。</summary>
        private static bool SamePayload(byte[] current, byte[] previous)
        {
            if (previous == null || current == null || current.Length < 2 || previous.Length < 2)
            {
                return false;
            }
            if (current.Length != previous.Length)
            {
                return false;
            }
            for (int i = 1; i < current.Length; i++)
            {
                if (current[i] != previous[i])
                {
                    return false;
                }
            }
            return true;
        }
    }
}
