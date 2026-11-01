using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DeviceBattery
{
    /// <summary>
    /// 设置页里的托盘图标预览：把当前「样式 + 字体 + 数字大小 + 充电标记」按
    /// 16 / 24 / 32 / 48 四个真实像素尺寸并排画出来。
    ///
    /// 以前改这几个下拉框只能选完再回头看托盘，来回切窗口才能比较，
    /// 三个样式名字（柔和/经典/圆润）也看不出到底差在哪。
    ///
    /// 这里**直接调 IconFactory 的正式接口**，画出来的每一档就是托盘要用的那张位图，
    /// 不存在「预览一套、实际另一套」的偏差；尺寸也是按当前缩放系数实算的，
    /// 125%/150% DPI 下预览里的小图就是托盘真实大小。
    ///
    /// 底色固定画一条深色槽位条：托盘不论系统深浅色都是深底浅字，
    /// 跟着设置页换成白底反而看不出真实观感。
    /// </summary>
    internal sealed class IconStylePreview : Control
    {
        private static readonly int[] DesignSizes = new int[] { 16, 24, 32, 48 };

        private readonly Icon[] _icons = new Icon[DesignSizes.Length];
        private readonly int[] _pixels = new int[DesignSizes.Length];
        private float _factor = 1f;
        private string _style = "soft";
        private string _font = "auto";
        private int _digitPercent = 80;
        private bool _charging;
        private bool _dirty = true;

        public IconStylePreview()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Height = UiScale.Scale(DesignHeight, 1f);
        }

        /// <summary>
        /// 整块预览的基准高度（未缩放）：槽位条 64 + 标注间距 4 + 标注行 16 + 余量 4。
        /// 原来给 74，标注正好被控件下沿切掉一半，看着像发虚。
        /// </summary>
        private const int DesignHeight = 88;

        /// <summary>换样式/字体/数字大小时调用，下次绘制前重建位图。</summary>
        public void SetState(string style, string font, int digitPercent, bool charging)
        {
            _style = string.IsNullOrEmpty(style) ? "soft" : style;
            _font = string.IsNullOrEmpty(font) ? "auto" : font;
            _digitPercent = digitPercent;
            _charging = charging;
            _dirty = true;
            DropIcons();
            Invalidate();
        }

        /// <summary>
        /// 跟着设置页的缩放系数走：图标按缩放后的真实像素重建，
        /// 高度也一起缩放，排版时才能拿到正确高度。
        /// </summary>
        public void ApplyScale(float factor)
        {
            if (factor <= 0.1f)
            {
                factor = 1f;
            }
            if (Math.Abs(factor - _factor) > 0.001f)
            {
                _factor = factor;
                _dirty = true;
                DropIcons();
            }
            Height = UiScale.Scale(DesignHeight, _factor);
        }

        private void DropIcons()
        {
            for (int i = 0; i < _icons.Length; i++)
            {
                if (_icons[i] != null)
                {
                    try { _icons[i].Dispose(); } catch (Exception) { }
                    _icons[i] = null;
                }
            }
        }

        private void EnsureIcons()
        {
            if (!_dirty)
            {
                return;
            }
            _dirty = false;
            for (int i = 0; i < DesignSizes.Length; i++)
            {
                int px = UiScale.Scale(DesignSizes[i], _factor);
                if (px < 8)
                {
                    px = 8;
                }
                if (_icons[i] != null && _pixels[i] == px)
                {
                    continue;
                }
                if (_icons[i] != null)
                {
                    try { _icons[i].Dispose(); } catch (Exception) { }
                    _icons[i] = null;
                }
                try
                {
                    // 用真实接口画，保证「预览里什么样、托盘里就什么样」。
                    _icons[i] = IconFactory.BuildDigits(px, _digitPercent > 0 ? "88" : "", Theme.StatusColor(
                        _charging ? DeviceStatus.Charging : DeviceStatus.Online, 88),
                        false, _digitPercent, _charging, _style, _font);
                    _pixels[i] = px;
                }
                catch (Exception)
                {
                    _icons[i] = null;
                    _pixels[i] = px;
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            EnsureIcons();

            int padX = UiScale.Scale(14, _factor);
            int padY = UiScale.Scale(8, _factor);
            int gap = UiScale.Scale(12, _factor);
            int captionGap = UiScale.Scale(4, _factor);
            int slot = UiScale.Scale(48, _factor);
            int stripH = slot + padY * 2;
            int width = Math.Max(1, Width - 1);

            Rectangle strip = new Rectangle(0, 0, width, stripH);
            using (GraphicsPath path = Surface.Rounded(this, strip, UiScale.Scale(8, _factor)))
            {
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(32, 32, 36)))
                {
                    g.FillPath(fill, path);
                }
                using (Pen border = new Pen(Theme.CardBorder, 1f))
                {
                    g.DrawPath(border, path);
                }
            }

            Font caption = Theme.PaintFont(Math.Max(7.5f, 8f * _factor), FontStyle.Regular);
            int x = padX;
            for (int i = 0; i < DesignSizes.Length; i++)
            {
                Icon icon = _icons[i];
                int px = icon == null ? UiScale.Scale(DesignSizes[i], _factor) : _pixels[i];
                if (px < 8)
                {
                    px = 8;
                }
                int top = (stripH - px) / 2;
                if (icon != null)
                {
                    g.DrawIconUnstretched(icon, new Rectangle(x, top, px, px));
                }
                string label = DesignSizes[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
                Size labelSize = TextRenderer.MeasureText(label, caption);
                TextRenderer.DrawText(g, label, caption,
                    new Rectangle(x + (px - labelSize.Width) / 2, stripH + captionGap,
                        labelSize.Width + 2, labelSize.Height),
                    Theme.Text, TextFormatFlags.NoPadding);
                x += px + gap;
            }

            // 右侧补一行说明，避免用户以为这是「放大的示意图」。
            string hint = _digitPercent > 0 ? "实际大小" : "不显示数字";
            Size hintSize = TextRenderer.MeasureText(hint, caption);
            int hintX = width - padX - hintSize.Width;
            if (hintX > x)
            {
                TextRenderer.DrawText(g, hint, caption,
                    new Rectangle(hintX, stripH + captionGap,
                        hintSize.Width + 2, hintSize.Height),
                    Theme.Muted, TextFormatFlags.NoPadding);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DropIcons();
            }
            base.Dispose(disposing);
        }
    }
}
