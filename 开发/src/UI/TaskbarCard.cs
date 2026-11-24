using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace DeviceBattery
{
    /// <summary>
    /// 任务栏悬浮卡片：显示一台设备的名称、大号电量、进度条、连接状态和最近 24 小时曲线。
    /// 借鉴 BatteryHub 的布局与淡入淡出交互，用 WinForms 自绘实现，默认不显示。
    /// </summary>
    internal sealed class TaskbarCard : Form
    {
        private const int DesignWidth = 320;
        private const int DesignHeight = 232;
        private readonly Timer _fade = new Timer();
        private DeviceState _device;
        private bool _light;
        private float _targetOpacity;
        private float _opacity;

        public TaskbarCard()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            ClientSize = new Size(DesignWidth, DesignHeight);
            _fade.Interval = 16;
            _fade.Tick += delegate
            {
                // ease-out：每帧靠近目标 25%，约 12 帧（200ms）收敛。
                _opacity += (_targetOpacity - _opacity) * 0.25f;
                if (Math.Abs(_targetOpacity - _opacity) < 0.02f)
                {
                    _opacity = _targetOpacity;
                    _fade.Stop();
                    if (_opacity <= 0f) { Hide(); return; }
                }
                try { Opacity = _opacity; } catch (Exception) { }
                Invalidate();
            };
        }

        public void ShowFor(DeviceState device, int anchorX, int anchorY, bool light)
        {
            if (device == null) return;
            _device = device;
            _light = light;
            BackColor = Theme.TaskbarPlate(light);
            ForeColor = Theme.TaskbarForeground(light);
            // 卡片放在设备区间上方，水平方向夹在屏幕工作区内。
            Screen screen = Screen.FromPoint(new Point(anchorX, anchorY));
            int x = Math.Max(screen.WorkingArea.Left + 8,
                Math.Min(anchorX - DesignWidth / 2, screen.WorkingArea.Right - Width - 8));
            int y = anchorY - DesignHeight - 8;
            if (y < screen.WorkingArea.Top + 8)
            {
                y = anchorY + 48; // 上方放不下就放到任务栏上方更远处
            }
            Location = new Point(x, y);
            _targetOpacity = 0.95f;
            if (!Visible)
            {
                _opacity = 0f;
                Opacity = 0f;
                Show();
            }
            _fade.Start();
        }

        public void HideCard()
        {
            if (!Visible) return;
            _targetOpacity = 0f;
            _fade.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            // 圆角卡片：1px 描边 + 实底。
            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Surface.Rounded(this, bounds, 10))
            {
                Color stroke = _light ? Color.FromArgb(210, 210, 210) : Color.FromArgb(70, 70, 70);
                using (SolidBrush fill = new SolidBrush(_light ? Color.FromArgb(251, 251, 251) : Color.FromArgb(43, 43, 43)))
                {
                    g.FillPath(fill, path);
                }
                using (Pen pen = new Pen(stroke, 1f))
                {
                    g.DrawPath(pen, path);
                }
            }

            if (_device == null) return;
            Color fg = _light ? Color.FromArgb(26, 26, 26) : Color.FromArgb(255, 255, 255);
            Color dim = _light ? Color.FromArgb(96, 96, 96) : Color.FromArgb(170, 170, 170);
            Color track = _light ? Color.FromArgb(214, 214, 214) : Color.FromArgb(80, 80, 80);
            bool low = TaskbarStripStyle.IsLow(_device);
            Color accent = low ? Theme.TaskbarLow(_light)
                : (_light ? Color.FromArgb(16, 124, 16) : Color.FromArgb(108, 203, 95));
            Font nameFont = Theme.PaintFont(Theme.FontTitle, FontStyle.Bold);
            Font bigFont = Theme.PaintFont(Theme.FontPanelValue, FontStyle.Bold);
            Font smallFont = Theme.PaintFont(Theme.FontSmall, FontStyle.Regular);
            int pad = UiScale.Px(this, 16);
            int y = pad;

            TextRenderer.DrawText(g, _device.DisplayName, nameFont,
                new Rectangle(pad, y, Width - pad * 2, 24), fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            y += 30;

            string levelText = _device.HasValue ? _device.PercentOrZero + "%" : "?";
            using (SolidBrush brush = new SolidBrush(low ? accent : fg))
            {
                g.DrawString(levelText, bigFont, brush, pad, y);
            }
            y += 40;

            Rectangle bar = new Rectangle(pad, y, Width - pad * 2, UiScale.Px(this, 8));
            using (GraphicsPath trackPath = Surface.Rounded(this, bar, 4))
            using (SolidBrush brush = new SolidBrush(track))
            {
                g.FillPath(brush, trackPath);
            }
            if (_device.HasValue && _device.PercentOrZero > 0)
            {
                int fillWidth = Math.Max(bar.Height,
                    bar.Width * Math.Min(100, _device.PercentOrZero) / 100);
                Rectangle fill = new Rectangle(bar.Left, bar.Top, fillWidth, bar.Height);
                using (GraphicsPath fillPath = Surface.Rounded(this, fill, 4))
                using (SolidBrush brush = new SolidBrush(accent))
                {
                    g.FillPath(brush, fillPath);
                }
            }
            y += 20;

            string state = _device.TransportText;
            if (_device.Status == DeviceStatus.Charging) state += " · 充电中";
            else if (_device.Status == DeviceStatus.Sleeping) state += " · 等待响应";
            else if (_device.Status == DeviceStatus.Stale) state += " · 数据过期";
            TextRenderer.DrawText(g, state, smallFont,
                new Rectangle(pad, y, Width - pad * 2, 18), dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            y += 24;

            // 最近 24 小时曲线：只画真实采样，>2 小时间隔断开，充电段用蓝色。
            DrawSparkline(g, new Rectangle(pad, y, Width - pad * 2, 80), dim);
        }

        private void DrawSparkline(Graphics g, Rectangle rect, Color dim)
        {
            BatteryHistorySeries series = BatteryHistory.GetSeries(_device.Id);
            if (series == null || series.Samples == null || series.Samples.Count < 2)
            {
                TextRenderer.DrawText(g, "暂无历史曲线", Theme.PaintFont(Theme.FontSmall, FontStyle.Regular),
                    rect, dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            long from = BatteryHistory.ToUnixSeconds(DateTime.Now.AddHours(-24));
            List<PointF> points = new List<PointF>();
            for (int i = 0; i < series.Samples.Count; i++)
            {
                BatteryHistorySample s = series.Samples[i];
                if (s.Time < from) continue;
                float x = rect.Left + (float)(s.Time - from) / (24 * 3600f) * rect.Width;
                float y = rect.Bottom - s.Percent / 100f * rect.Height;
                points.Add(new PointF(x, y));
            }
            if (points.Count < 2) return;
            using (Pen pen = new Pen(dim, 1.5f))
            {
                for (int i = 1; i < points.Count; i++)
                {
                    if (points[i].X - points[i - 1].X > rect.Width * 0.1f) continue; // 断点不连线
                    g.DrawLine(pen, points[i - 1], points[i]);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _fade.Dispose();
            base.Dispose(disposing);
        }
    }
}
