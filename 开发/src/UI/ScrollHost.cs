using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DeviceBattery
{
    /// <summary>
    /// 自带暗色滚动条的容器：WinForms 的 AutoScroll 在 .NET Framework 下只能用系统
    /// 浅色滚动条（SetWindowTheme 对 Panel 的非客户区不生效），深色界面里会露出一条
    /// 白条。这里改成自己画：内容控件放进来，滚动条悬浮在右侧，鼠标滚轮/拖动/点击轨道
    /// 都能用，颜色走 Theme，深浅色主题一致。
    /// </summary>
    internal sealed class ScrollHost : Panel
    {
        private Control _content;
        private int _contentHeight;
        private int _offset;
        private bool _dragging;
        private int _dragStartY;
        private int _dragStartOffset;
        private bool _hoverBar;

        public ScrollHost()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            TabStop = false;
            BackColor = Theme.Background;
            MouseEnter += delegate { Focus(); };
        }

        /// <summary>内容控件：宽高由调用方设置，位置由滚动条控制。</summary>
        public void Attach(Control content)
        {
            _content = content;
            if (content != null)
            {
                Controls.Add(content);
                content.Location = new Point(0, -_offset);
            }
        }

        /// <summary>内容总高度，设置后自动夹取滚动位置并重画滚动条。</summary>
        public void SetContentHeight(int height)
        {
            _contentHeight = Math.Max(0, height);
            ApplyOffset(_offset);
        }

        public int ContentHeight
        {
            get { return _contentHeight; }
        }

        /// <summary>可见内容宽度（滚动条出现时要扣掉条宽）。</summary>
        public int ViewWidth
        {
            get { return Math.Max(0, ClientSize.Width - (NeedsBar ? BarWidth : 0)); }
        }

        private int BarWidth
        {
            get { return UiScale.Px(this, 9); }
        }

        private bool NeedsBar
        {
            get { return _contentHeight > ClientSize.Height; }
        }

        private int MaxOffset
        {
            get { return Math.Max(0, _contentHeight - ClientSize.Height); }
        }

        private void ApplyOffset(int value)
        {
            int max = MaxOffset;
            _offset = value < 0 ? 0 : (value > max ? max : value);
            if (_content != null)
            {
                _content.Top = -_offset;
                _content.Width = ViewWidth;
            }
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyOffset(_offset);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollByWheel(e.Delta);
        }

        /// <summary>按滚轮方向滚动一格；内容里的子控件收到滚轮时也转给它。</summary>
        public void ScrollByWheel(int delta)
        {
            if (MaxOffset <= 0)
            {
                return;
            }
            ApplyOffset(_offset - (delta > 0 ? 1 : -1) * UiScale.Px(this, 48));
        }

        private Rectangle TrackBounds
        {
            get
            {
                int inset = UiScale.Px(this, 3);
                return new Rectangle(ClientSize.Width - BarWidth, inset, BarWidth,
                    Math.Max(0, ClientSize.Height - inset * 2));
            }
        }

        private Rectangle ThumbBounds
        {
            get
            {
                Rectangle track = TrackBounds;
                if (track.Height <= 0 || _contentHeight <= 0)
                {
                    return Rectangle.Empty;
                }
                int minThumb = UiScale.Px(this, 26);
                int height = (int)Math.Round(track.Height * (double)ClientSize.Height / _contentHeight);
                if (height < minThumb)
                {
                    height = minThumb;
                }
                if (height > track.Height)
                {
                    height = track.Height;
                }
                int max = MaxOffset;
                int travel = track.Height - height;
                int top = track.Top + (max <= 0 ? 0 : (int)Math.Round(travel * (double)_offset / max));
                return new Rectangle(track.X, top, track.Width, height);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using (SolidBrush brush = new SolidBrush(Theme.Background))
            {
                g.FillRectangle(brush, ClientRectangle);
            }
            if (!NeedsBar)
            {
                return;
            }
            Rectangle track = TrackBounds;
            Rectangle thumb = ThumbBounds;
            if (track.Height <= 0 || thumb.Height <= 0)
            {
                return;
            }
            Color trackColor = Theme.IsDark ? Color.FromArgb(40, 63, 63, 70) : Color.FromArgb(28, 0, 0, 0);
            Color thumbColor = _dragging || _hoverBar
                ? (Theme.IsDark ? Color.FromArgb(190, 161, 161, 170) : Color.FromArgb(150, 100, 116, 139))
                : (Theme.IsDark ? Color.FromArgb(130, 161, 161, 170) : Color.FromArgb(95, 100, 116, 139));
            using (GraphicsPath path = Surface.Rounded(this, new RectangleF(track.X, track.Y, track.Width, track.Height), BarWidth / 2))
            using (SolidBrush brush = new SolidBrush(trackColor))
            {
                g.FillPath(brush, path);
            }
            using (GraphicsPath path = Surface.Rounded(this, new RectangleF(thumb.X, thumb.Y, thumb.Width, thumb.Height), BarWidth / 2))
            using (SolidBrush brush = new SolidBrush(thumbColor))
            {
                g.FillPath(brush, path);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!NeedsBar || e.Button != MouseButtons.Left)
            {
                return;
            }
            Rectangle thumb = ThumbBounds;
            if (thumb.Contains(e.Location))
            {
                _dragging = true;
                _dragStartY = e.Y;
                _dragStartOffset = _offset;
                Capture = true;
            }
            else if (TrackBounds.Contains(e.Location))
            {
                // 点击轨道：上下翻一屏。
                bool up = e.Y < thumb.Top;
                ApplyOffset(_offset + (up ? -1 : 1) * Math.Max(UiScale.Px(this, 40), ClientSize.Height - UiScale.Px(this, 40)));
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hover = NeedsBar && TrackBounds.Contains(e.Location);
            if (hover != _hoverBar)
            {
                _hoverBar = hover;
                Invalidate();
            }
            if (!_dragging)
            {
                return;
            }
            Rectangle track = TrackBounds;
            Rectangle thumb = ThumbBounds;
            int travel = track.Height - thumb.Height;
            if (travel <= 0 || MaxOffset <= 0)
            {
                return;
            }
            int delta = e.Y - _dragStartY;
            ApplyOffset(_dragStartOffset + (int)Math.Round(delta * (double)MaxOffset / travel));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragging)
            {
                _dragging = false;
                Capture = false;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverBar && !_dragging)
            {
                _hoverBar = false;
                Invalidate();
            }
        }
    }
}