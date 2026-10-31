using System;
using Microsoft.Win32;

namespace DeviceBattery
{
    /// <summary>
    /// 读取真实的 Windows 版本信息。
    /// Environment.OSVersion 和 GetVersionEx 在未声明支持 Win10 时会伪装成 6.2.9200，
    /// 因此改用注册表读取，才能正确区分 Windows 10 和 Windows 11。
    /// </summary>
    internal static class OsInfo
    {
        private const string VersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

        public static string DisplayName
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(VersionKey))
                    {
                        if (key != null)
                        {
                            object product = key.GetValue("ProductName");
                            object display = key.GetValue("DisplayVersion");
                            object build = key.GetValue("CurrentBuildNumber");
                            object ubr = key.GetValue("UBR");

                            string name = NormalizeProductName(
                                product == null ? null : product.ToString(),
                                build == null ? 0 : ParseInt(build.ToString()));
                            string version = display == null ? "" : display.ToString();
                            string buildText = build == null ? "" : build.ToString();
                            if (ubr != null && buildText.Length > 0)
                            {
                                buildText += "." + ubr;
                            }

                            if (name.Length > 0)
                            {
                                string result = name;
                                if (version.Length > 0) result += " " + version;
                                if (buildText.Length > 0) result += " (build " + buildText + ")";
                                return result;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                }
                // 注册表不可用时退回系统报告值（可能是 6.2 伪装值，但至少有信息）。
                return Environment.OSVersion.VersionString;
            }
        }

        /// <summary>
        /// Windows 11 的 ProductName 仍然是 "Windows 10 ..."，用 build 号纠正。
        /// </summary>
        private static string NormalizeProductName(string productName, int build)
        {
            if (string.IsNullOrEmpty(productName))
            {
                return build >= 22000 ? "Windows 11" : "";
            }
            if (build >= 22000 && productName.IndexOf("Windows 10", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return productName.Replace("Windows 10", "Windows 11");
            }
            return productName;
        }

        private static int ParseInt(string text)
        {
            int value;
            return int.TryParse(text, out value) ? value : 0;
        }

    }
}
