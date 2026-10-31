using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DeviceBattery
{
    /// <summary>
    /// 二级页面（设置 / 历史 / 诊断）共用的绘制件。
    /// 面板是一级页面，全部自绘；二级页面原先用标准 WinForms 控件，
    /// 和面板放在一起像两套软件。这里把面板的视觉语言抽出来复用：
    /// 圆角卡片、渐变填充、强调色、悬停反馈、统一字号与内边距。
    /// 所有尺寸都按设计坐标（96 DPI）写，再用 UiScale 换算成实际像素。
    /// </summary>
    internal static class Surface
    {
        public const int CardRadius = 10;
        public const int ButtonRadius = 6;


        public static int Px(Control control, int value)
        {
            return UiScale.Px(control, value);
        }

        /// <summary>圆角矩形路径。半径按控件 DPI 缩放，避免高 DPI 下圆角显得过尖。</summary>
        public static GraphicsPath Rounded(Control control, Rectangle bounds, int radiusDesign)
        {
            int radius = Math.Max(1, Px(control, radiusDesign));
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            if (diameter > bounds.Width)
            {
                diameter = bounds.Width;
            }
            if (diameter > bounds.Height)
            {
                diameter = bounds.Height;
            }
            if (diameter <= 1)
            {
                path.AddRectangle(bounds);
                return path;
            }
            Rectangle arc = new Rectangle(bounds.Left, bounds.Top, diameter, diameter);
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>圆角矩形路径（浮点矩形，用于把描边压到像素中心线上）。</summary>
        public static GraphicsPath Rounded(Control control, RectangleF bounds, int radiusDesign)
        {
            int radius = Math.Max(1, Px(control, radiusDesign));
            GraphicsPath path = new GraphicsPath();
            float diameter = radius * 2f;
            if (diameter > bounds.Width)
            {
                diameter = bounds.Width;
            }
            if (diameter > bounds.Height)
            {
                diameter = bounds.Height;
            }
            if (diameter <= 1f)
            {
                path.AddRectangle(bounds);
                return path;
            }
            RectangleF arc = new RectangleF(bounds.Left, bounds.Top, diameter, diameter);
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>画一张分组卡片：顶部略亮的竖向渐变 + 1px 描边。</summary>
        public static void Card(Graphics g, Control control, Rectangle bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }
            using (GraphicsPath path = Rounded(control, bounds, CardRadius))
            {
                using (LinearGradientBrush brush = new LinearGradientBrush(
                    new Rectangle(bounds.X, bounds.Y, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height)),
                    Theme.CardTop, Theme.CardBottom, LinearGradientMode.Vertical))
                {
                    g.FillPath(brush, path);
                }
                using (Pen pen = new Pen(Theme.CardBorder, 1f))
                {
                    g.DrawPath(pen, path);
                }
            }
        }

        /// <summary>卡片内的分组标题（粗体）+ 可选说明（淡色，第二行）。</summary>
        public static void SectionHeader(Graphics g, Control control, Rectangle bounds, string title,
            string subtitle)
        {
            int titleHeight = Px(control, 20);
            // 共享缓存字体，不能 Dispose，也不能重名。
            Font titleFont = Theme.PaintFont(Theme.FontTitle, FontStyle.Bold);
            TextRenderer.DrawText(g, title, titleFont,
                new Rectangle(bounds.X, bounds.Y, bounds.Width, titleHeight),
                Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (string.IsNullOrEmpty(subtitle))
            {
                return;
            }
            Font subtitleFont = Theme.PaintFont(Theme.FontSmall, FontStyle.Regular);
            TextRenderer.DrawText(g, subtitle, subtitleFont,
                new Rectangle(bounds.X, bounds.Y + titleHeight, bounds.Width, Px(control, 18)),
                Theme.Faint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        /// <summary>分组之间的细分隔线。</summary>
        public static void Divider(Graphics g, Control control, int left, int right, int y)
        {
            using (Pen pen = new Pen(Theme.Divider, 1f))
            {
                g.DrawLine(pen, left, y, right, y);
            }
        }

    }

    /// <summary>面板风格的圆角按钮：渐变填充、悬停变亮、键盘焦点环。</summary>
    internal sealed class SurfaceButton : Button
    {
        private bool _hover;
        private bool _pressed;
        private bool _accent;
        private Color _swatch = Color.Empty;
        // 只有键盘把焦点移进来才画焦点环。鼠标点过之后 ShowFocusCues 仍可能为 true，
        // 那样按钮上会留一个虚线方框，看起来像渲染坏了。
        private bool _keyboardFocus;

        public SurfaceButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = Color.Transparent;
            ForeColor = Theme.Text;
        }

        /// <summary>主操作按钮用强调色填充。</summary>
        public bool Accent
        {
            get { return _accent; }
            set
            {
                if (_accent == value)
                {
                    return;
                }
                _accent = value;
                Invalidate();
            }
        }

        /// <summary>
        /// 让按钮直接显示一块颜色（选色按钮用）。设为 Color.Empty 恢复普通按钮。
        /// 自绘按钮不吃 BackColor，所以颜色预览必须走这个属性。
        /// </summary>
        public Color Swatch
        {
            get { return _swatch; }
            set
            {
                if (_swatch == value)
                {
                    return;
                }
                _swatch = value;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            _pressed = false;
            Invalidate();
        }

        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e);
            // 鼠标按下时 OnEnter 也在同一个消息里发生，此时 MouseButtons 能区分「点进来」和「Tab 进来」。
            _keyboardFocus = (Control.MouseButtons == MouseButtons.None);
            Invalidate();
        }

        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            _keyboardFocus = false;
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            bool tab = e.KeyCode == Keys.Tab || e.KeyCode == Keys.Up || e.KeyCode == Keys.Down ||
                       e.KeyCode == Keys.Left || e.KeyCode == Keys.Right;
            if (!_keyboardFocus && tab)
            {
                _keyboardFocus = true;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _keyboardFocus = false;
                _pressed = true;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _pressed = false;
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 整数坐标加半像素偏移：1px 描边落在像素中心线上，
            // 不会左右各渗半个像素，边缘就不会有毛边感。
            g.PixelOffsetMode = PixelOffsetMode.Half;
            // 自绘按钮的透明底色不会自动擦除，圆角外面会留下上一帧的脏像素；
            // 先用父容器底色铺满，四个角才是干净的。
            Color parentColor = Parent != null ? Parent.BackColor : Theme.Card;
            using (SolidBrush backBrush = new SolidBrush(parentColor))
            {
                g.FillRectangle(backBrush, ClientRectangle);
            }

            Color fillTop;
            Color fillBottom;
            Color border;
            Color text;
            if (_accent)
            {
                Color baseColor = Theme.IsDark ? Theme.Blue : Color.FromArgb(37, 99, 235);
                fillTop = _pressed ? Darken(baseColor, 0.16f) : (_hover ? Lighten(baseColor, 0.08f) : Lighten(baseColor, 0.04f));
                fillBottom = _pressed ? Darken(baseColor, 0.24f) : (_hover ? baseColor : Darken(baseColor, 0.10f));
                border = Darken(baseColor, 0.22f);
                // 蓝底上用白字，避免浅色主题下蓝底配深字看不清。
                text = Color.White;
            }
            else
            {
                // 浅色主题下 Theme.Button 本身就是纯白，再往上“提亮”只会得到白底白框，
                // 按钮会整个隐形。浅色用「白底 + 明确描边」这种标准按钮外观，
                // 深色才用提亮的渐变。
                Color baseFill = Theme.IsDark ? Theme.Button : Color.White;
                Color hoverFill = Theme.IsDark ? Theme.ButtonHover : Color.FromArgb(243, 246, 251);
                Color pressFill = Theme.IsDark ? Darken(Theme.Button, 0.10f) : Color.FromArgb(228, 233, 241);
                fillTop = _pressed ? pressFill : (_hover ? hoverFill : (Theme.IsDark ? Lighten(baseFill, 0.20f) : baseFill));
                fillBottom = _pressed ? pressFill : (_hover ? hoverFill : (Theme.IsDark ? Lighten(baseFill, 0.04f) : baseFill));
                border = Theme.IsDark ? Lighten(Theme.Border, 0.22f) : Color.FromArgb(178, 189, 205);
                if (_hover)
                {
                    border = Theme.IsDark ? Lighten(Theme.Border, 0.30f) : Color.FromArgb(140, 154, 176);
                }
                text = Theme.Text;
            }
            if (!Enabled)
            {
                fillTop = Theme.Card;
                fillBottom = Theme.Card;
                border = Theme.Divider;
                text = Theme.Faint;
            }
            else if (_swatch != Color.Empty)
            {
                // 选色按钮：整块铺所选颜色，文字取对比色。
                fillTop = _swatch;
                fillBottom = _swatch;
                border = Theme.ContrastText(_swatch);
                text = Theme.ContrastText(_swatch);
            }

            // 键盘焦点只加粗描边、换成强调色，不再画虚线方框：虚线在圆角按钮上
            // 会看成一个套在里面的方框，用户会当成渲染 bug。
            bool showFocus = Focused && _keyboardFocus && Enabled;
            if (showFocus)
            {
                border = Theme.IsDark ? Theme.Blue : Color.FromArgb(37, 99, 235);
            }
            float borderWidth = showFocus ? Math.Max(1.5f, Surface.Px(this, 2)) : 1f;

            // 描边压在像素中心线上（0.5 偏移）：1px 边沿完整落在一个像素里，
            // 不会和底色各掺一半而显得发虚、有毛边。
            RectangleF shape = new RectangleF(0.5f, 0.5f, Math.Max(1f, Width - 1f), Math.Max(1f, Height - 1f));

            using (GraphicsPath path = Surface.Rounded(this, shape, Surface.ButtonRadius))
            {
                using (LinearGradientBrush brush = new LinearGradientBrush(
                    new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)),
                    fillTop, fillBottom, LinearGradientMode.Vertical))
                {
                    g.FillPath(brush, path);
                }
                using (Pen pen = new Pen(border, borderWidth))
                {
                    g.DrawPath(pen, path);
                }
            }

            Rectangle textRect = new Rectangle(Surface.Px(this, 4), 0,
                Math.Max(0, Width - Surface.Px(this, 8)), Height);
            // 主按钮加粗突出重点，普通按钮用常规字重，整页看起来不会一片粗体。
            Font font = Theme.PaintFont(Theme.FontBody, _accent ? FontStyle.Bold : FontStyle.Regular);
            TextRenderer.DrawText(g, Text, font, textRect, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private static Color Lighten(Color color, float amount)
        {
            return Color.FromArgb(color.A,
                Math.Min(255, (int)(color.R + (255 - color.R) * amount)),
                Math.Min(255, (int)(color.G + (255 - color.G) * amount)),
                Math.Min(255, (int)(color.B + (255 - color.B) * amount)));
        }

        private static Color Darken(Color color, float amount)
        {
            return Color.FromArgb(color.A,
                (int)(color.R * (1f - amount)),
                (int)(color.G * (1f - amount)),
                (int)(color.B * (1f - amount)));
        }
    }
}
