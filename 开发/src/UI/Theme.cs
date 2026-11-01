using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DeviceBattery
{
    /// <summary>
    /// 每显示器 DPI 换算。程序是 Per-Monitor V2 感知进程（见 Native.EnableDpiAwareness），
    /// Windows 不会替我们放大坐标；而字体是磅值，会随 DPI 自动变大。
    /// 所以坐标必须按同一比例换算，否则 150%/200% 缩放下文字会对不上卡片、按钮会被裁掉。
    /// 96 DPI（100%）下所有换算都是恒等变换，界面与原来逐像素一致。
    /// </summary>
    internal static class UiScale
    {
        public const int DesignDpi = 96;
        /// <summary>超过 300% 的缩放按 300% 处理，避免算出离谱尺寸。</summary>
        public const int MaxDpi = 288;

        public static float RawFactor(Control control)
        {
            int dpi = DesignDpi;
            if (control != null)
            {
                try { dpi = control.DeviceDpi; } catch (Exception) { dpi = DesignDpi; }
            }
            if (dpi < DesignDpi) dpi = DesignDpi;
            if (dpi > MaxDpi) dpi = MaxDpi;
            return dpi / (float)DesignDpi;
        }

        public static int Scale(int value, float factor)
        {
            if (factor <= 1.0001f)
            {
                return value;
            }
            return (int)Math.Round(value * (double)factor, MidpointRounding.AwayFromZero);
        }

        public static int Px(Control control, int value)
        {
            return Scale(value, RawFactor(control));
        }

        /// <summary>
        /// 窗口整体缩放系数：在 DPI 比例基础上再压一次，保证窗口不会超出目标屏幕的工作区
        /// （例如 1080p + 200% 缩放时，按比例算出来的设置窗口会比屏幕还高）。
        /// </summary>
        public static float FitFactor(Control control, Size designClient)
        {
            float factor = RawFactor(control);
            Rectangle area;
            try
            {
                Screen screen = control == null ? Screen.PrimaryScreen : Screen.FromControl(control);
                area = screen == null ? Screen.PrimaryScreen.WorkingArea : screen.WorkingArea;
            }
            catch (Exception)
            {
                return factor;
            }
            if (area.Width <= 0 || area.Height <= 0)
            {
                return factor;
            }
            // 预留标题栏、边框和任务栏的余量。
            int maxWidth = Math.Max(320, area.Width - 32);
            int maxHeight = Math.Max(240, area.Height - 64);
            if (designClient.Width > 0 && designClient.Width * factor > maxWidth)
            {
                factor = maxWidth / (float)designClient.Width;
            }
            if (designClient.Height > 0 && designClient.Height * factor > maxHeight)
            {
                factor = Math.Min(factor, maxHeight / (float)designClient.Height);
            }
            if (factor < 1f)
            {
                factor = 1f;
            }
            return factor;
        }
    }

    /// <summary>
    /// 一个窗口的 DPI 排版器。首次排版时记下每个控件的设计坐标，
    /// 之后无论 DPI 怎么变、排版调用多少次，都从设计坐标重新换算——
    /// 不会出现“又缩了一遍”导致第二次切 DPI 时界面变成两倍大。
    /// </summary>
    internal sealed class LayoutScale
    {
        private readonly Dictionary<Control, Rectangle> _designBounds =
            new Dictionary<Control, Rectangle>();
        private readonly Dictionary<Control, Size> _designMaxWidth =
            new Dictionary<Control, Size>();

        public void Apply(Form form, Size designClient, Size designMinimum, float factor)
        {
            if (form == null)
            {
                return;
            }
            if (factor < 1f)
            {
                factor = 1f;
            }
            form.SuspendLayout();
            try
            {
                // 先记下设计坐标，再改客户区：锚定控件会随客户区变化先被布局引擎挪一次，
                // 记晚了我就会把"已被挪过的坐标"当成设计值，一缩一放就越走越偏。
                Record(form);
                if (designMinimum.Width > 0 && designMinimum.Height > 0)
                {
                    form.MinimumSize = new Size(UiScale.Scale(designMinimum.Width, factor),
                        UiScale.Scale(designMinimum.Height, factor));
                }
                Size client = new Size(UiScale.Scale(designClient.Width, factor),
                    UiScale.Scale(designClient.Height, factor));
                if (form.ClientSize != client)
                {
                    form.ClientSize = client;
                }
                ScaleChildren(form, factor);
            }
            finally
            {
                form.ResumeLayout(true);
            }
        }

        private void Record(Control parent)
        {
            for (int i = 0; i < parent.Controls.Count; i++)
            {
                Control child = parent.Controls[i];
                if (child == null)
                {
                    continue;
                }
                if (!_designBounds.ContainsKey(child))
                {
                    _designBounds[child] = child.Bounds;
                }
                if (child.HasChildren)
                {
                    Record(child);
                }
            }
        }

        /// <summary>
        /// 把控件当前的位置当成新的“设计坐标”记下来。
        /// 设置页会按行重新排版，但 Apply 每次都把控件恢复成最初记录的设计坐标，
        /// 排版结果就被抹掉了；同步一次之后，后面再缩放也是以排版后的样子为基准。
        /// </summary>
        public void SyncDesign(Control parent, float factor)
        {
            if (parent == null)
            {
                return;
            }
            if (factor < 1f)
            {
                factor = 1f;
            }
            for (int i = 0; i < parent.Controls.Count; i++)
            {
                Control child = parent.Controls[i];
                if (child == null)
                {
                    continue;
                }
                Rectangle bounds = child.Bounds;
                _designBounds[child] = new Rectangle(
                    (int)Math.Round(bounds.X / factor), (int)Math.Round(bounds.Y / factor),
                    (int)Math.Round(bounds.Width / factor), (int)Math.Round(bounds.Height / factor));
                Label label = child as Label;
                if (label != null && !label.MaximumSize.IsEmpty)
                {
                    _designMaxWidth[child] = new Size(
                        (int)Math.Round(label.MaximumSize.Width / factor), label.MaximumSize.Height);
                }
                if (child.HasChildren)
                {
                    SyncDesign(child, factor);
                }
            }
        }

        private void ScaleChildren(Control parent, float factor)
        {
            for (int i = 0; i < parent.Controls.Count; i++)
            {
                Control child = parent.Controls[i];
                if (child == null)
                {
                    continue;
                }
                Rectangle design;
                if (!_designBounds.TryGetValue(child, out design))
                {
                    design = child.Bounds;
                    _designBounds[child] = design;
                }
                try
                {
                    // 锚定控件交给布局引擎（窗口本身会缩放），这里不动，避免两边互相打架。
                    bool anchored = (child.Anchor & (AnchorStyles.Right | AnchorStyles.Bottom)) != 0;
                    if (!anchored && child.Dock == DockStyle.None)
                    {
                        if (child.AutoSize)
                        {
                            child.Location = new Point(UiScale.Scale(design.X, factor),
                                UiScale.Scale(design.Y, factor));
                            Label label = child as Label;
                            if (label != null && !label.MaximumSize.IsEmpty)
                            {
                                Size maximum;
                                if (!_designMaxWidth.TryGetValue(child, out maximum))
                                {
                                    maximum = label.MaximumSize;
                                    _designMaxWidth[child] = maximum;
                                }
                                label.MaximumSize = new Size(UiScale.Scale(maximum.Width, factor),
                                    maximum.Height);
                            }
                        }
                        else
                        {
                            child.SetBounds(UiScale.Scale(design.X, factor), UiScale.Scale(design.Y, factor),
                                UiScale.Scale(design.Width, factor), UiScale.Scale(design.Height, factor),
                                BoundsSpecified.All);
                        }
                    }
                    else if ((child.Dock == DockStyle.Bottom || child.Dock == DockStyle.Top) &&
                             design.Height > 0)
                    {
                        child.Height = UiScale.Scale(design.Height, factor);
                    }
                }
                catch (Exception)
                {
                }
                if (child.HasChildren)
                {
                    ScaleChildren(child, factor);
                }
            }
        }
    }

    /// <summary>
    /// 窗体自己 new 出来的字体（赋给 Control.Font 的那些）：WinForms 不会帮调用方释放 Font，
    /// 每个设置/历史/诊断窗口都 new 一批就会一路漏 GDI 句柄，这里统一登记、窗口销毁时释放。
    /// </summary>
    internal sealed class FontBag : IDisposable
    {
        private readonly List<Font> _fonts = new List<Font>();
        private bool _disposed;

        public Font Take(float size, FontStyle style)
        {
            return Track(Theme.UiFont(size, style));
        }

        /// <summary>登记一个已经创建好的字体（例如等宽字体），窗口销毁时一并释放。</summary>
        public Font Track(Font font)
        {
            if (font == null)
            {
                return null;
            }
            if (!_disposed)
            {
                _fonts.Add(font);
            }
            return font;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            for (int i = 0; i < _fonts.Count; i++)
            {
                try { _fonts[i].Dispose(); } catch (Exception) { }
            }
            _fonts.Clear();
        }
    }

    /// <summary>
    /// 主题模式：跟随系统 / 浅色 / 深色。调色板在运行时切换，面板、设置和图标共用。
    /// </summary>
    internal static class Theme
    {
        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        private static bool _isDark = true;
        private static bool _initialized;
        private static string _mode = "system";
        private static readonly object FontGate = new object();
        private static readonly Dictionary<string, Font> _paintFonts =
            new Dictionary<string, Font>(StringComparer.Ordinal);

        public static Color Background = Color.FromArgb(24, 24, 27);
        public static Color PanelTop = Color.FromArgb(31, 32, 39);
        public static Color PanelBottom = Color.FromArgb(16, 17, 21);
        public static Color Card = Color.FromArgb(39, 39, 42);
        public static Color CardTop = Color.FromArgb(44, 45, 52);
        public static Color CardBottom = Color.FromArgb(35, 36, 42);
        public static Color CardHoverTop = Color.FromArgb(53, 54, 61);
        public static Color CardHoverBottom = Color.FromArgb(45, 46, 52);
        public static Color CardBorder = Color.FromArgb(63, 63, 70);
        public static Color CardHover = Color.FromArgb(48, 48, 52);
        public static Color Border = Color.FromArgb(63, 63, 70);
        public static Color Divider = Color.FromArgb(52, 53, 61);
        public static Color Track = Color.FromArgb(60, 62, 70);
        public static Color HeaderTop = Color.FromArgb(36, 38, 47);
        public static Color HeaderBottom = Color.FromArgb(25, 26, 32);
        public static Color Input = Color.FromArgb(43, 44, 51);
        public static Color List = Color.FromArgb(34, 35, 42);
        public static Color TabHeader = Color.FromArgb(31, 32, 39);
        public static Color TabSelected = Color.FromArgb(47, 49, 58);
        public static Color Button = Color.FromArgb(43, 44, 51);
        public static Color ButtonHover = Color.FromArgb(55, 57, 65);
        public static Color Chip = Color.FromArgb(58, 60, 68);
        public static Color Text = Color.FromArgb(244, 244, 245);
        public static Color Muted = Color.FromArgb(161, 161, 170);
        public static Color Faint = Color.FromArgb(148, 148, 158);
        public static Color Green = Color.FromArgb(34, 197, 94);
        public static Color Yellow = Color.FromArgb(245, 158, 11);
        public static Color Orange = Color.FromArgb(249, 115, 22);
        public static Color Red = Color.FromArgb(248, 113, 113);
        public static Color Blue = Color.FromArgb(96, 165, 250);
        public static Color Purple = Color.FromArgb(167, 139, 250);
        public static Color Charging = Color.FromArgb(56, 189, 248);

        public static event EventHandler Changed;

        static Theme()
        {
            ApplyPalette(true);
            _initialized = true;
        }

        public static bool IsDark
        {
            get { return _isDark; }
        }

        public static string Mode
        {
            get { return _mode; }
        }

        public static string NormalizeMode(string value)
        {
            if (string.Equals(value, "light", StringComparison.OrdinalIgnoreCase))
            {
                return "light";
            }
            if (string.Equals(value, "dark", StringComparison.OrdinalIgnoreCase))
            {
                return "dark";
            }
            return "system";
        }

        /// <summary>应用主题模式；未知值按跟随系统处理。</summary>
        public static void Apply(string mode)
        {
            string normalized = NormalizeMode(mode);
            bool changedMode = !string.Equals(_mode, normalized, StringComparison.Ordinal);
            _mode = normalized;
            bool dark = normalized == "dark" || (normalized == "system" && IsSystemDark());
            if (!_initialized || dark != _isDark)
            {
                _isDark = dark;
                ApplyPalette(dark);
                ResetPaintFonts();
                _initialized = true;
                RaiseChanged();
            }
            else if (changedMode)
            {
                // 模式名称变了但实际明暗没变，也要让设置页刷新选中项。
                RaiseChanged();
            }
        }

        /// <summary>跟随系统模式下检测注册表变化；返回是否真的切换了明暗。</summary>
        public static bool ApplyIfSystemChanged()
        {
            if (!string.Equals(_mode, "system", StringComparison.Ordinal))
            {
                return false;
            }
            bool dark = IsSystemDark();
            if (dark == _isDark)
            {
                return false;
            }
            _isDark = dark;
            ApplyPalette(dark);
            ResetPaintFonts();
            RaiseChanged();
            return true;
        }

        public static bool IsSystemDark()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PersonalizeKey))
                {
                    if (key != null)
                    {
                        object apps = key.GetValue("AppsUseLightTheme");
                        if (apps != null)
                        {
                            return Convert.ToInt32(apps) == 0;
                        }
                        object system = key.GetValue("SystemUsesLightTheme");
                        if (system != null)
                        {
                            return Convert.ToInt32(system) == 0;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            // Windows 10/11 默认是浅色；取不到值时不要强行套深色。
            return false;
        }

        public static Color LevelColor(int percent)
        {
            if (percent <= 10) return Red;
            if (percent <= 25) return Orange;
            if (percent <= 50) return Yellow;
            return Green;
        }

        public static Color AccentColor(int percent)
        {
            if (AppSettings.Current.AutoColor)
            {
                return LevelColor(percent);
            }
            // 用户选择的颜色原样使用。不要因为颜色偏暗就偷偷改回分级色，
            // 否则“固定颜色”保存后会被重置，用户会以为设置没生效。
            return Color.FromArgb(255, Color.FromArgb(AppSettings.Current.CustomColorArgb));
        }

        public static Color StatusColor(DeviceStatus status, int percent)
        {
            switch (status)
            {
                case DeviceStatus.Charging:
                    return AppSettings.Current.AutoColor ? Charging : AccentColor(percent);
                case DeviceStatus.Online: return AccentColor(percent);
                case DeviceStatus.Sleeping: return Faint;
                case DeviceStatus.Stale: return Faint;
                case DeviceStatus.Offline: return Faint;
                case DeviceStatus.Unsupported: return Color.FromArgb(148, 163, 184);
                case DeviceStatus.BluetoothOff: return Color.FromArgb(148, 163, 184);
                case DeviceStatus.Error: return Red;
                default: return Muted;
            }
        }

        /// <summary>图标/按钮上的前景色：按 WCAG 对比度选黑或白，浅色背景不再强行白字。</summary>
        public static Color ContrastText(Color background)
        {
            double luminance = RelativeLuminance(background);
            double white = 1.05 / (luminance + 0.05);
            double black = (luminance + 0.05) / 0.05;
            return black >= white
                ? Color.FromArgb(24, 26, 31)
                : Color.White;
        }

        public static double RelativeLuminance(Color color)
        {
            double r = Linear(color.R);
            double g = Linear(color.G);
            double b = Linear(color.B);
            return 0.2126 * r + 0.7152 * g + 0.0722 * b;
        }

        private static double Linear(int channel)
        {
            double value = channel / 255.0;
            return value <= 0.03928
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        public static Color IconBorder(Color background, bool dim)
        {
            int alpha = dim ? 80 : 120;
            return ContrastText(background) == Color.White
                ? Color.FromArgb(alpha, 255, 255, 255)
                : Color.FromArgb(alpha, 24, 26, 31);
        }

        /// <summary>
        /// 字号阶梯：说明 / 正文 / 标题 / 数值，四档统一。
        /// 以前各界面各写 8、8.25、8.5、9、9.5，同一页里字号不齐才显得乱而丑；
        /// 现在只允许从这四档里取，需要更小时用 FontSmall，不再自造中间值。
        /// </summary>
        // 设置界面统一四级字号：说明用 FontSmall，标签与取值用 FontBody，
        // 分组标题用 FontTitle（Bold），面板上的大号数字用 FontValue。
        public const float FontSmall = 10.5f;
        public const float FontBody = 11.5f;
        public const float FontTitle = 13f;
        public const float FontValue = 13f;
        public static Font UiFont(float size, FontStyle style)
        {
            try
            {
                return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point);
            }
            catch (Exception)
            {
                return new Font("Segoe UI", size, style, GraphicsUnit.Point);
            }
        }

        /// <summary>
        /// 绘制热路径专用的共享字体：同一字号/样式只创建一次。
        /// 调用方**不要** Dispose 返回值，也不要把返回值赋给控件的 Font 属性
        /// （控件会在自身生命周期结束时 Dispone 它，导致缓存失效）。
        /// </summary>
        public static Font PaintFont(float size, FontStyle style)
        {
            string key = ((int)(size * 100f)).ToString(CultureInfo.InvariantCulture) + "|" + (int)style;
            lock (FontGate)
            {
                Font cached;
                if (_paintFonts.TryGetValue(key, out cached))
                {
                    return cached;
                }
                Font created = UiFont(size, style);
                _paintFonts[key] = created;
                return created;
            }
        }

        /// <summary>清空共享绘制字体缓存；主题切换时调用。</summary>
        private static void ResetPaintFonts()
        {
            lock (FontGate)
            {
                foreach (KeyValuePair<string, Font> pair in _paintFonts)
                {
                    try { pair.Value.Dispose(); } catch (Exception) { }
                }
                _paintFonts.Clear();
            }
        }

        private static void RaiseChanged()
        {
            EventHandler handler = Changed;
            if (handler != null)
            {
                try { handler(null, EventArgs.Empty); }
                catch (Exception) { }
            }
        }
        private static void ApplyPalette(bool dark)
        {
            if (dark)
            {
                Background = Color.FromArgb(24, 24, 27);
                PanelTop = Color.FromArgb(31, 32, 39);
                PanelBottom = Color.FromArgb(16, 17, 21);
                Card = Color.FromArgb(39, 39, 42);
                CardTop = Color.FromArgb(44, 45, 52);
                CardBottom = Color.FromArgb(35, 36, 42);
                CardHoverTop = Color.FromArgb(53, 54, 61);
                CardHoverBottom = Color.FromArgb(45, 46, 52);
                CardBorder = Color.FromArgb(63, 63, 70);
                CardHover = Color.FromArgb(48, 48, 52);
                Border = Color.FromArgb(63, 63, 70);
                Divider = Color.FromArgb(52, 53, 61);
                Track = Color.FromArgb(60, 62, 70);
                HeaderTop = Color.FromArgb(36, 38, 47);
                HeaderBottom = Color.FromArgb(25, 26, 32);
                Input = Color.FromArgb(43, 44, 51);
                List = Color.FromArgb(34, 35, 42);
                TabHeader = Color.FromArgb(31, 32, 39);
                TabSelected = Color.FromArgb(47, 49, 58);
                Button = Color.FromArgb(43, 44, 51);
                ButtonHover = Color.FromArgb(55, 57, 65);
                Chip = Color.FromArgb(58, 60, 68);
                Text = Color.FromArgb(244, 244, 245);
                Muted = Color.FromArgb(161, 161, 170);
                Faint = Color.FromArgb(148, 148, 158);
                Green = Color.FromArgb(34, 197, 94);
                Yellow = Color.FromArgb(245, 158, 11);
                Orange = Color.FromArgb(249, 115, 22);
                Red = Color.FromArgb(248, 113, 113);
                Blue = Color.FromArgb(96, 165, 250);
                Purple = Color.FromArgb(167, 139, 250);
                Charging = Color.FromArgb(56, 189, 248);
            }
            else
            {
                Background = Color.FromArgb(245, 247, 250);
                PanelTop = Color.FromArgb(255, 255, 255);
                PanelBottom = Color.FromArgb(238, 242, 247);
                Card = Color.FromArgb(255, 255, 255);
                CardTop = Color.FromArgb(255, 255, 255);
                CardBottom = Color.FromArgb(247, 249, 252);
                CardHoverTop = Color.FromArgb(252, 253, 255);
                CardHoverBottom = Color.FromArgb(242, 246, 252);
                CardBorder = Color.FromArgb(220, 226, 234);
                CardHover = Color.FromArgb(247, 250, 254);
                Border = Color.FromArgb(216, 222, 230);
                Divider = Color.FromArgb(226, 231, 238);
                Track = Color.FromArgb(226, 231, 239);
                HeaderTop = Color.FromArgb(255, 255, 255);
                HeaderBottom = Color.FromArgb(241, 245, 250);
                Input = Color.FromArgb(255, 255, 255);
                List = Color.FromArgb(255, 255, 255);
                TabHeader = Color.FromArgb(248, 250, 253);
                TabSelected = Color.FromArgb(231, 238, 249);
                Button = Color.FromArgb(255, 255, 255);
                ButtonHover = Color.FromArgb(241, 245, 251);
                Chip = Color.FromArgb(238, 242, 248);
                Text = Color.FromArgb(31, 35, 41);
                Muted = Color.FromArgb(95, 102, 115);
                Faint = Color.FromArgb(106, 114, 128);
                Green = Color.FromArgb(21, 128, 61);
                Yellow = Color.FromArgb(180, 95, 6);
                Orange = Color.FromArgb(194, 65, 12);
                Red = Color.FromArgb(220, 38, 38);
                Blue = Color.FromArgb(37, 99, 235);
                Purple = Color.FromArgb(124, 58, 237);
                Charging = Color.FromArgb(2, 132, 199);
            }
        }
    }
}
