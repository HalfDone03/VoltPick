using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DeviceBattery
{
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string ValueName = "VoltPick";
        private const string ShortcutName = "VoltPick.lnk";
        private const string FallbackLauncherName = "VoltPick.cmd";

        public const string LaunchArgument = "--autostart-launch";

        public static string LaunchCommand
        {
            get { return "\"" + Application.ExecutablePath + "\" " + LaunchArgument; }
        }

        private static string StartupFolder
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.Startup); }
        }

        private static string ShortcutPath
        {
            get { return Path.Combine(StartupFolder, ShortcutName); }
        }

        private static string FallbackLauncherPath
        {
            get { return Path.Combine(StartupFolder, FallbackLauncherName); }
        }

        public static bool IsEnabled()
        {
            if (File.Exists(ShortcutPath) || File.Exists(FallbackLauncherPath))
            {
                return true;
            }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    string value = key.GetValue(ValueName) as string;
                    if (string.IsNullOrEmpty(value)) return false;
                }
            }
            catch (Exception)
            {
                return false;
            }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ApprovedKey, false))
                {
                    if (key == null) return true;
                    byte[] data = key.GetValue(ValueName) as byte[];
                    if (data == null || data.Length == 0) return true;
                    return (data[0] & 0x01) == 0;
                }
            }
            catch (Exception)
            {
                return true;
            }
        }

        public static bool SetEnabled(bool enabled, out string error)
        {
            error = null;
            if (enabled)
            {
                if (WriteRunKey(true, out error))
                {
                    WriteApproved(true);
                    return true;
                }

                string shortcutError;
                if (CreateShortcut(out shortcutError))
                {
                    error = null;
                    return true;
                }

                error = (error ?? "") + "\n" + shortcutError;
                return false;
            }

            string runError;
            WriteRunKey(false, out runError);
            WriteApproved(false);
            // 启动项有两条落地路径：正常是 .lnk，WScript.Shell 不可用时退化写 .cmd。
            // 关闭自启必须两条都清掉，否则残留的 .cmd 会继续把程序拉起来。
            DeleteStaleEntry(ShortcutPath);
            DeleteStaleEntry(FallbackLauncherPath);

            if (IsEnabled())
            {
                error = "注册表项/快捷方式删除失败，可能被安全软件拦截。";
                return false;
            }
            return true;
        }

        private static bool WriteRunKey(bool enabled, out string error)
        {
            error = null;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, RegistryKeyPermissionCheck.ReadWriteSubTree))
                {
                    if (key == null)
                    {
                        error = "无法打开注册表 Run 项。";
                        return false;
                    }

                    if (enabled)
                    {
                        key.SetValue(ValueName, LaunchCommand, RegistryValueKind.String);
                        string written = key.GetValue(ValueName) as string;
                        if (string.IsNullOrEmpty(written))
                        {
                            error = "写入后读不到注册表值，可能被安全软件回滚。";
                            return false;
                        }
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("写入自启注册表失败：" + ex.Message);
                error = "写入注册表失败：" + ex.Message;
                return false;
            }
        }

        private static void WriteApproved(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(ApprovedKey, RegistryKeyPermissionCheck.ReadWriteSubTree))
                {
                    if (key == null) return;
                    if (enabled)
                    {
                        byte[] data = new byte[12];
                        data[0] = 0x02;
                        key.SetValue(ValueName, data, RegistryValueKind.Binary);
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("写入自启批准状态失败：" + ex.Message);
            }
        }

        private static void DeleteStaleEntry(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Log.Error("删除自启项失败：" + path + "：" + ex.Message);
            }
        }

        private static bool CreateShortcut(out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(StartupFolder);

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    object shell = Activator.CreateInstance(shellType);
                    try
                    {
                        object shortcut = shellType.InvokeMember(
                            "CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                            new object[] { ShortcutPath });

                        Type shortcutType = shortcut.GetType();
                        shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { Application.ExecutablePath });
                        shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { LaunchArgument });
                        shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(Application.ExecutablePath) });
                        shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "VoltPick" });
                        shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                    }
                    finally
                    {
                        if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                        {
                            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
                        }
                    }

                    if (File.Exists(ShortcutPath))
                    {
                        return true;
                    }
                    error = "启动文件夹快捷方式没有创建成功。";
                    return false;
                }

                string cmdPath = FallbackLauncherPath;
                File.WriteAllText(cmdPath,
                    "@echo off\r\nstart \"\" " + LaunchCommand + "\r\n",
                    System.Text.Encoding.Default);
                if (File.Exists(cmdPath))
                {
                    return true;
                }
                error = "无法在启动文件夹创建启动器。";
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("创建自启快捷方式失败：" + ex.Message);
                error = "创建启动项失败：" + ex.Message;
                return false;
            }
        }

        public static void RepairPathIfNeeded()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;
                    string value = key.GetValue(ValueName) as string;
                    if (string.IsNullOrEmpty(value)) return;

                    string expected = LaunchCommand;
                    if (string.Equals(value, expected, StringComparison.OrdinalIgnoreCase)) return;

                    string existingPath = ExtractPath(value);
                    if (string.IsNullOrEmpty(existingPath)) return;

                    bool pointsToSelf;
                    try
                    {
                        pointsToSelf = string.Equals(
                            Path.GetFullPath(existingPath),
                            Application.ExecutablePath,
                            StringComparison.OrdinalIgnoreCase);
                    }
                    catch (Exception)
                    {
                        pointsToSelf = false;
                    }

                    if (!pointsToSelf && File.Exists(existingPath))
                    {
                        return;
                    }

                    key.SetValue(ValueName, expected, RegistryValueKind.String);
                    Log.Info("已修正自启项路径：" + existingPath + " -> " + expected);
                }
            }
            catch (Exception ex)
            {
                Log.Error("修正自启项失败：" + ex.Message);
            }
        }

        private static string ExtractPath(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return null;
            string text = commandLine.Trim();
            if (text.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = text.IndexOf('"', 1);
                if (end > 1)
                {
                    return text.Substring(1, end - 1);
                }
                return null;
            }

            int space = text.IndexOf(' ');
            return space > 0 ? text.Substring(0, space) : text;
        }

        /// <summary>
        /// 解析 --autostart 的开关值。旧实现是「只要不是 off 就当开启」，
        /// 于是 --autostart offf / --autostart no / 打错一个字母都会把自启打开。
        /// 现在只认明确的开关词，其余一律拒绝，交给调用方报错。
        /// </summary>
        public static bool TryParseMode(string mode, out bool enable)
        {
            enable = false;
            if (string.IsNullOrEmpty(mode))
            {
                return false;
            }
            string text = mode.Trim();
            if (string.Equals(text, "on", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "enable", StringComparison.OrdinalIgnoreCase))
            {
                enable = true;
                return true;
            }
            if (string.Equals(text, "off", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "disable", StringComparison.OrdinalIgnoreCase))
            {
                enable = false;
                return true;
            }
            return false;
        }

        public static int RunFromCommandLine(string mode)
        {
            bool enable;
            if (!TryParseMode(mode, out enable))
            {
                string bad = "FAIL unknown | --autostart 只接受 on/off（或 true/false、1/0、enable/disable）：" +
                             (mode == null ? "" : mode.Replace("\n", " "));
                WriteResult(bad);
                Console.WriteLine(bad);
                return 2;
            }

            string error;
            bool ok = SetEnabled(enable, out error);
            string line = (ok ? "OK " : "FAIL ") + (enable ? "on" : "off");
            if (!ok && !string.IsNullOrEmpty(error))
            {
                line = line + " | " + error.Replace("\n", " ");
            }

            WriteResult(line);
            Console.WriteLine(line);
            return ok ? 0 : 1;
        }

        private static void WriteResult(string line)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "autostart-result.txt"),
                    line,
                    System.Text.Encoding.UTF8);
            }
            catch (Exception)
            {
            }
        }
    }
}
