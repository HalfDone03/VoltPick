using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeviceBattery
{
    internal sealed partial class SettingsForm : Form
    {

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Theme.Changed -= OnThemeChanged;
            }
            base.Dispose(disposing);
            if (disposing)
            {
                // 每次打开设置都会新建一批字体，不释放就一路泄漏 GDI 句柄。
                _fonts.Dispose();
            }
        }

        private void ApplyWindowTheme()
        {
            if (!IsHandleCreated)
            {
                return;
            }
            int dark = Theme.IsDark ? 1 : 0;
            try { DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); } catch (Exception) { }
            try { DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int)); } catch (Exception) { }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        /// <summary>把 9:05 / 22：30 这样的输入规范成 09:05 / 22:30；非法返回 false。</summary>
        internal static bool TryNormalizeDnd(string text, out string normalized)
        {
            normalized = null;
            if (text == null)
            {
                return false;
            }
            string value = text.Trim().Replace('：', ':');
            if (value.Length == 0)
            {
                return false;
            }
            string[] parts = value.Split(':');
            if (parts.Length != 2)
            {
                return false;
            }
            int hour;
            int minute;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hour))
            {
                return false;
            }
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minute))
            {
                return false;
            }
            if (hour < 0 || hour > 23 || minute < 0 || minute > 59)
            {
                return false;
            }
            normalized = hour.ToString("00", CultureInfo.InvariantCulture)
                + ":" + minute.ToString("00", CultureInfo.InvariantCulture);
            return true;
        }
    }
}
