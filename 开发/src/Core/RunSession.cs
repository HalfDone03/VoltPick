using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace DeviceBattery
{
    /// <summary>
    /// 正常退出时删除标记；异常退出（崩溃、强杀、断电）会留下标记，
    /// 下次启动据此提示并给出诊断入口。
    /// </summary>
    internal static class RunSession
    {
        public static string MarkerPath
        {
            get { return Path.Combine(AppSettings.ConfigDirectory, "running.marker"); }
        }

        public static bool DetectPreviousAbnormalExit(out string detail)
        {
            return DetectPreviousAbnormalExit(MarkerPath, out detail);
        }

        internal static bool DetectPreviousAbnormalExit(string path, out string detail)
        {
            detail = null;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return false;
                }
                string text = File.ReadAllText(path, Encoding.UTF8);
                string first = text;
                int line = text.IndexOf('\n');
                if (line >= 0) first = text.Substring(0, line);
                DateTime when = DateTime.MinValue;
                DateTime.TryParse(first.Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out when);
                detail = when == DateTime.MinValue
                    ? "上次运行没有正常结束（标记文件存在）"
                    : "上次退出时间：" + when.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void MarkRunning()
        {
            MarkRunning(MarkerPath);
        }

        internal static void MarkRunning(string path)
        {
            try
            {
                string folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }
                StringBuilder text = new StringBuilder();
                text.AppendLine(DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
                text.AppendLine(Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Debug("写入运行标记失败：" + ex.Message);
            }
        }

        public static void MarkCleanExit()
        {
            MarkCleanExit(MarkerPath);
        }

        internal static void MarkCleanExit(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("删除运行标记失败：" + ex.Message);
            }
        }
    }
}
