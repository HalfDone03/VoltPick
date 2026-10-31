using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DeviceBattery
{
    internal static class Log
    {
        private const long MaxBytes = 1024 * 1024;

        /// <summary>
        /// 相同内容的最小重复间隔：同一个错误/同一行状态在 10 分钟内反复出现时只记一条，
        /// 之后每 10 分钟补一条“仍在重复”，避免一次设备异常就把 1MB 日志刷满。
        /// </summary>
        internal const int DuplicateSuppressSeconds = 600;

        private static readonly object Gate = new object();
        private static bool _cleaned;
        private static string _lastKey;
        private static DateTime _lastKeyTime = DateTime.MinValue;
        private static int _duplicateCount;

        // 冒号/短横线分隔的 MAC，例如 AA:BB:CC:DD:EE:FF、aa-bb-cc-dd-ee-ff。
        private static readonly Regex MacPattern = new Regex(
            @"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b",
            RegexOptions.Compiled);

        // 无分隔符的 12 位十六进制 MAC（蓝牙缓存键、DeviceId 里的形式），
        // 要求至少含一个 A–F 字母，避免把纯数字的接收器序列号也当 MAC 抹掉。
        private static readonly Regex BareMacPattern = new Regex(
            @"\b(?=[0-9A-Fa-f]{12}\b)(?=[0-9A-Fa-f]*[A-Fa-f])[0-9A-Fa-f]{12}\b",
            RegexOptions.Compiled);

        /// <summary>
        /// 仅供本地测试注入日志目录：测试会故意写坏配置来验证回退逻辑，
        /// 那些 ERROR 不该落到用户真实日志里（否则「设置读取失败」会污染反馈内容）。
        /// 正常运行时保持 null。
        /// </summary>
        internal static string DirectoryOverride { get; set; }

        public static string DirectoryPath
        {
            get
            {
                string folder = DirectoryOverride;
                if (string.IsNullOrEmpty(folder))
                {
                    folder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "VoltPick",
                        "logs");
                }
                try
                {
                    Directory.CreateDirectory(folder);
                    return folder;
                }
                catch (Exception)
                {
                    return AppDomain.CurrentDomain.BaseDirectory;
                }
            }
        }

        public static string FilePath
        {
            get { return Path.Combine(DirectoryPath, "tray.log"); }
        }

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Warn(string message)
        {
            Write("WARN", message);
        }

        public static void Error(string message)
        {
            Write("ERROR", message);
        }

        public static void Debug(string message)
        {
            if (AppSettings.Current != null && AppSettings.Current.DetailedDiagnostics)
            {
                Write("DEBUG", message);
            }
        }

        public static string HashAddress(ulong address)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(address.ToString("X12", CultureInfo.InvariantCulture));
                using (SHA1 sha = SHA1.Create())
                {
                    byte[] hash = sha.ComputeHash(bytes);
                    StringBuilder builder = new StringBuilder(10);
                    for (int i = 0; i < 4; i++)
                    {
                        builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                    }
                    return builder.ToString();
                }
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }
            try
            {
                string result = MacPattern.Replace(text, "[MAC]");
                return BareMacPattern.Replace(result, "[MAC]");
            }
            catch (Exception)
            {
                return text;
            }
        }

        /// <summary>
        /// 仅供本地测试：清掉重复抑制状态，让测试里的日志断言互不影响。
        /// 正常运行时不需要调用。
        /// </summary>
        internal static void ResetForTests()
        {
            lock (Gate)
            {
                _lastKey = null;
                _lastKeyTime = DateTime.MinValue;
                _duplicateCount = 0;
            }
        }

        public static bool Clear(out string error)
        {
            error = null;
            lock (Gate)
            {
                try
                {
                    string folder = DirectoryPath;
                    string[] files = Directory.GetFiles(folder, "tray.log*");
                    for (int i = 0; i < files.Length; i++)
                    {
                        File.Delete(files[i]);
                    }
                    _cleaned = true;
                    // 刚清空日志后，同样的内容应该能重新记下来。
                    _lastKey = null;
                    _lastKeyTime = DateTime.MinValue;
                    _duplicateCount = 0;
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    if (!_cleaned)
                    {
                        _cleaned = true;
                        CleanupOldFiles();
                    }

                    string path = FilePath;
                    string folder = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(folder))
                    {
                        Directory.CreateDirectory(folder);
                    }

                    DateTime now = DateTime.Now;
                    string text = Redact(message);
                    string key = level + "\u0001" + text;
                    if (string.Equals(_lastKey, key, StringComparison.Ordinal))
                    {
                        // 与上一条完全相同：先静默计数，最多每 DuplicateSuppressSeconds 补一条心跳，
                        // 既不会刷屏，也还能看出问题持续了多久。
                        _duplicateCount++;
                        if ((now - _lastKeyTime).TotalSeconds < DuplicateSuppressSeconds)
                        {
                            return;
                        }
                        Append(path, now, "INFO",
                            "（上一行相同内容已重复 " + _duplicateCount + " 次，仍在重复）");
                        _duplicateCount = 0;
                        _lastKeyTime = now;
                        return;
                    }

                    if (_duplicateCount > 0)
                    {
                        Append(path, now, "INFO",
                            "（上一行相同内容重复 " + _duplicateCount + " 次，已省略）");
                        _duplicateCount = 0;
                    }
                    _lastKey = key;
                    _lastKeyTime = now;
                    Append(path, now, level, text);
                }
            }
            catch (Exception)
            {
                // 日志失败不能影响主程序。
            }
        }

        private static void Append(string path, DateTime now, string level, string message)
        {
            FileInfo info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes)
            {
                Rotate(path);
            }
            string line = now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + "  " + level + "  " + message + Environment.NewLine;
            File.AppendAllText(path, line, new UTF8Encoding(false));
        }

        private static void Rotate(string path)
        {
            try
            {
                string three = path + ".3";
                string two = path + ".2";
                string one = path + ".1";
                if (File.Exists(three)) File.Delete(three);
                if (File.Exists(two)) File.Move(two, three);
                if (File.Exists(one)) File.Move(one, two);
                File.Move(path, one);
            }
            catch (Exception)
            {
            }
        }

        private static void CleanupOldFiles()
        {
            try
            {
                DateTime cutoff = DateTime.Now.AddDays(-7);
                string folder = DirectoryPath;
                if (!Directory.Exists(folder))
                {
                    return;
                }
                string[] files = Directory.GetFiles(folder, "tray.log*");
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        if (File.GetLastWriteTime(files[i]) < cutoff)
                        {
                            File.Delete(files[i]);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
