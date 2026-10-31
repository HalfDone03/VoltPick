using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DeviceBattery
{
    /// <summary>
    /// 原生 TabControl 在深色主题下会留下白色边框；这里完全自绘标签栏和内容背景。
    /// </summary>
    internal sealed class DarkTabControl : TabControl
    {
        public DarkTabControl()
        {
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            DrawMode = TabDrawMode.OwnerDrawFixed;
            SizeMode = TabSizeMode.Fixed;
            ItemSize = new Size(76, 34);
            Font = Theme.UiFont(Theme.FontBody, FontStyle.Regular);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTabItemSize();
        }

        /// <summary>
        /// 按当前 DPI 缩放标签尺寸。显示器切换或缩放比例变化时也要重新计算，
        /// 否则标签会停留在旧尺寸上。
        /// </summary>
        private void ApplyTabItemSize()
        {
            int dpi = 96;
            try { dpi = DeviceDpi; } catch (Exception) { }
            double factor = dpi / 96.0;
            if (factor < 1.0) factor = 1.0;
            Size size = new Size((int)Math.Round(76 * factor), (int)Math.Round(34 * factor));
            if (ItemSize != size)
            {
                ItemSize = size;
            }
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ApplyTabItemSize();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Background);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.Clear(Theme.Background);

            int headerBottom = DisplayRectangle.Top;
            using (SolidBrush header = new SolidBrush(Theme.TabHeader))
            {
                g.FillRectangle(header, 0, 0, Width, headerBottom);
            }
            using (SolidBrush content = new SolidBrush(Theme.Background))
            {
                g.FillRectangle(content, DisplayRectangle);
            }
            using (Pen border = new Pen(Theme.Divider, 1f))
            {
                g.DrawRectangle(border, DisplayRectangle.Left, DisplayRectangle.Top,
                    DisplayRectangle.Width - 1, DisplayRectangle.Height - 1);
            }

            for (int i = 0; i < TabPages.Count; i++)
            {
                Rectangle rect = GetTabRect(i);
                bool selected = i == SelectedIndex;
                Rectangle pill = new Rectangle(rect.Left + 4, rect.Top + 3,
                    rect.Width - 8, rect.Height - 6);
                if (selected)
                {
                    using (GraphicsPath path = Rounded(pill, 7))
                    using (SolidBrush brush = new SolidBrush(Theme.TabSelected))
                    {
                        g.FillPath(brush, path);
                    }
                    using (SolidBrush accent = new SolidBrush(Theme.Blue))
                    {
                        g.FillRectangle(accent, pill.Left + 12, pill.Bottom - 3, pill.Width - 24, 2);
                    }
                }
                TextRenderer.DrawText(g, TabPages[i].Text, Font, pill,
                    selected ? Theme.Text : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        private static GraphicsPath Rounded(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            if (d > rect.Height) d = rect.Height;
            path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
