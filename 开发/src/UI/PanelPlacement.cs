using System;
using System.Drawing;

namespace DeviceBattery
{
    internal static class PanelPlacement
    {
        /// <summary>把面板左上角限制在当前屏幕工作区内，避免记忆位置落到屏幕外。</summary>
        public static Point ClampToWorkArea(Point desired, Size size, Rectangle workArea, int margin)
        {
            if (workArea.Width <= 0 || workArea.Height <= 0)
            {
                return desired;
            }
            if (margin < 0) margin = 0;

            int minX = workArea.Left + margin;
            int minY = workArea.Top + margin;
            int maxX = workArea.Right - Math.Max(1, size.Width) - margin;
            int maxY = workArea.Bottom - Math.Max(1, size.Height) - margin;

            int x = desired.X;
            int y = desired.Y;
            if (maxX < minX) x = minX;
            else x = Math.Max(minX, Math.Min(x, maxX));
            if (maxY < minY) y = minY;
            else y = Math.Max(minY, Math.Min(y, maxY));
            return new Point(x, y);
        }
    }
}
