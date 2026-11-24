using System;
using System.Drawing;

namespace DeviceBattery
{
    /// <summary>
    /// 任务栏显示的一段内容：字形 / 数字 / 闪电，各自带颜色与透明度。
    /// 规则集中在这里做纯函数，方便断言；渲染只管把 Piece 画到分层窗口上。
    /// 只借鉴 BatteryHub 的视觉事实（字形码点、颜色、状态语义），不复制其源码。
    /// </summary>
    internal sealed class TaskbarPiece
    {
        public string Text;
        public Color Color;
        public int Alpha = 255;
        /// <summary>这段属于哪台设备，-1 表示与设备无关的占位（如无设备字形）。</summary>
        public int DeviceIndex = -1;
        /// <summary>这段之后留多少设计像素（96dpi）。</summary>
        public int GapAfter;
    }

    internal static class TaskbarStripStyle
    {
        // Segoe Fluent Icons / MDL2 的码点，属于字形标识，不是源码。
        internal const string GlyphMouse = "\uE962";
        internal const string GlyphHeadset = "\uE7F6";
        internal const string GlyphKeyboard = "\uE765";
        internal const string GlyphGamepad = "\uE7FC";
        internal const string GlyphOther = "\uE83F";
        internal const string GlyphBolt = "\uE945";
        internal const string GlyphBatteryUnknown = "\uE996";
        internal const string GlyphRefresh = "\uE72C";

        // 设计坐标下的间距（96dpi）。
        internal const int Pad = 8;
        internal const int GapGlyphToNumber = 5;
        internal const int GapNumberToBolt = 2;
        internal const int GapBetweenDevices = 14;

        internal static string GlyphFor(DeviceCategory category)
        {
            switch (category)
            {
                case DeviceCategory.Mouse: return GlyphMouse;
                case DeviceCategory.Headset: return GlyphHeadset;
                case DeviceCategory.Keyboard: return GlyphKeyboard;
                case DeviceCategory.Gamepad: return GlyphGamepad;
                default: return GlyphOther;
            }
        }

        /// <summary>没有单独设置时用的默认低电阈值（与参考项目一致）。</summary>
        internal const int DefaultLowThreshold = 15;

        /// <summary>
        /// 设备是否处于低电：有读数、未充电、且电量不高于这台设备自己的阈值。
        /// 设置里没给这台设备设过阈值时用默认 15%。
        /// </summary>
        internal static bool IsLow(DeviceState device)
        {
            if (device == null || !device.HasValue || device.Status == DeviceStatus.Charging)
            {
                return false;
            }
            int threshold = AppSettings.GetDeviceAlertThreshold(device.Id);
            if (threshold <= 0)
            {
                threshold = DefaultLowThreshold;
            }
            return device.PercentOrZero <= threshold;
        }

        /// <summary>设备是否休眠/过期（任务栏上整体变淡）。</summary>
        internal static bool IsAsleep(DeviceState device)
        {
            return device != null &&
                   (device.Status == DeviceStatus.Sleeping || device.Status == DeviceStatus.Stale);
        }

        /// <summary>充电标记：未充满是绿闪电，充满是蓝闪电；其它状态没有标记。</summary>
        internal static bool TryBolt(DeviceState device, bool light, out string glyph, out Color color)
        {
            glyph = null;
            color = Color.Transparent;
            if (device == null || device.Status != DeviceStatus.Charging)
            {
                return false;
            }
            glyph = GlyphBolt;
            color = device.HasValue && device.PercentOrZero >= 100
                ? Theme.TaskbarFull(light)
                : Theme.TaskbarCharging(light);
            return true;
        }

        /// <summary>把一台设备变成 2~3 段 Piece（字形 + 数字 + 可选闪电）。</summary>
        internal static void AddDevice(System.Collections.Generic.List<TaskbarPiece> pieces,
            DeviceState device, int index, bool light)
        {
            bool asleep = IsAsleep(device);
            int alpha = asleep ? 120 : 255;
            Color foreground = Theme.TaskbarForeground(light);
            bool low = IsLow(device);

            pieces.Add(new TaskbarPiece
            {
                Text = GlyphFor(device == null ? DeviceCategory.Unknown : device.Category),
                Color = foreground,
                Alpha = alpha,
                DeviceIndex = index,
                GapAfter = GapGlyphToNumber
            });

            string text = device != null && device.HasValue
                ? device.PercentOrZero.ToString() + "%"
                : "?";
            pieces.Add(new TaskbarPiece
            {
                Text = text,
                Color = low ? Theme.TaskbarLow(light) : foreground,
                Alpha = alpha,
                DeviceIndex = index,
                GapAfter = 0
            });

            string boltGlyph;
            Color boltColor;
            if (TryBolt(device, light, out boltGlyph, out boltColor))
            {
                pieces[pieces.Count - 1].GapAfter = GapNumberToBolt;
                pieces.Add(new TaskbarPiece
                {
                    Text = boltGlyph,
                    Color = boltColor,
                    Alpha = alpha,
                    DeviceIndex = index,
                    GapAfter = 0
                });
            }
        }

        /// <summary>
        /// 托盘数字图标用的状态色：低电红、充电绿、充满蓝、休眠/过期变淡。
        /// 用户在设置里关掉「按电量自动配色」并选了固定色时，原样用固定色——
        /// 显式设置优先于状态语义，不能偷偷改回去。
        /// </summary>
        internal static Color TrayIconColor(DeviceState device)
        {
            if (device == null || !device.HasValue)
            {
                return Theme.Faint;
            }
            if (!AppSettings.Current.AutoColor)
            {
                return Color.FromArgb(AppSettings.Current.CustomColorArgb);
            }
            if (device.Status == DeviceStatus.Sleeping || device.Status == DeviceStatus.Stale ||
                device.Status == DeviceStatus.Offline || device.Status == DeviceStatus.Error)
            {
                return Theme.Faint;
            }
            if (device.Status == DeviceStatus.Charging)
            {
                return device.PercentOrZero >= 100 ? Theme.TaskbarFull(Theme.IsSystemTaskbarDark())
                    : Theme.TaskbarCharging(Theme.IsSystemTaskbarDark());
            }
            if (IsLow(device))
            {
                return Theme.TaskbarLow(Theme.IsSystemTaskbarDark());
            }
            return Theme.LevelColor(device.PercentOrZero);
        }

        /// <summary>无设备时也给一个淡色电池字形，保证任务栏条有可点的目标。</summary>
        internal static TaskbarPiece UnknownDevicePiece(bool light)
        {
            return new TaskbarPiece
            {
                Text = GlyphBatteryUnknown,
                Color = Theme.TaskbarForeground(light),
                Alpha = 110,
                DeviceIndex = -1,
                GapAfter = 0
            };
        }
    }
}
