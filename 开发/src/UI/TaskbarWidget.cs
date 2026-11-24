using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeviceBattery
{
    /// <summary>
    /// 可选的任务栏小组件：在通知区左侧画「类别字形 + 百分比 + 充电/满电闪电」。
    /// 用 UpdateLayeredWindow 提交带逐像素 alpha 的位图，透明处让点击穿透。
    /// 默认关闭；托盘图标照旧保留。布局失败 3 秒自动隐藏，回到普通托盘显示。
    /// 视觉与交互借鉴 BatteryHub，代码独立实现。
    /// </summary>
    internal sealed class TaskbarWidget : Form
    {
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_NOMOVE = 0x0002;
        private const int CheckMs = 1000;

        private readonly Timer _check = new Timer();
        private BatterySnapshot _snapshot;
        private IntPtr _lastTrayHandle = IntPtr.Zero;
        private DateTime _firstFailure = DateTime.MinValue;
        private bool _failed;
        private int _width;
        private int _height;
        private bool _light;
        private TaskbarStripRenderer.Layout _layout;
        private TaskbarCard _card;
        private int _hoverIndex = -1;
        private int _screenX;
        private int _screenY;

        // 分层窗口路径（UpdateLayeredWindow）在本机 WinForms 下能定位、能看到窗口，
        // 但逐像素 alpha 位图没有合成到屏幕（截图/OCR 都看不到字形）。
        // 默认走可靠的 TransparencyKey + OnPaint 路径：背景色键透明、文字照常绘制，
        // 视觉接近且命中测试不受影响。分层路径保留，供以后在别的环境再验证。
        internal bool UseLegacyPaint;

        public TaskbarWidget()
        {
            Text = "";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = false;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            // 分层窗口的透明靠逐像素 alpha 实现，不要设 BackColor=Transparent：
            // WinForms 的 Form 不支持透明背景色，构造时会抛"控件不支持透明的背景色"。
            _width = UiScale.Px(this, 96);
            _height = UiScale.Px(this, 32);
            ClientSize = new Size(_width, _height);
            _check.Interval = CheckMs;
            _check.Tick += delegate { Reposition(); };
            _light = Theme.IsSystemTaskbarDark() == false;
            UseLegacyPaint = true; // 可靠的自绘路径；分层路径待真机环境再启用
            TransparencyKey = Color.FromArgb(1, 2, 3);
            BackColor = TransparencyKey;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // 自绘路径靠 TransparencyKey（WinForms 自己会设 WS_EX_LAYERED），
                // 这里再加一次 WS_EX_LAYERED 会和 TransparencyKey 冲突，让窗口全透明。
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                if (!UseLegacyPaint) cp.ExStyle |= WS_EX_LAYERED;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        public bool Failed { get { return _failed; } }
        internal TaskbarStripRenderer.Layout CurrentLayout { get { return _layout; } }
        internal bool IsLight { get { return _light; } }

        public void Start()
        {
            if (!IsHandleCreated) { CreateHandle(); }
            // 必须作为任务栏的子窗口，否则会被任务栏盖住（分层/工具窗仍在其下）。
            IntPtr taskbar = Native.FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero)
            {
                Native.SetParent(Handle, taskbar);
            }
            Show();
            Reposition();
            _check.Start();
        }

        public void Stop()
        {
            _check.Stop();
            HideCard();
            Hide();
        }

        public void SetSnapshot(BatterySnapshot snapshot)
        {
            _snapshot = snapshot;
            if (_check.Enabled) { Render(); }
        }

        /// <summary>Explorer 重启会广播 TaskbarCreated，收到后立刻重定位。</summary>
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == Native.TaskbarCreatedMessage && m.Result == IntPtr.Zero &&
                _check.Enabled && !_failed)
            {
                Reposition();
            }
        }

        private void Reposition()
        {
            Native.RECT trayRect;
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero || !Native.IsWindow(tray) ||
                !Native.TryGetTrayRect(out trayRect) ||
                trayRect.Right <= trayRect.Left || trayRect.Bottom <= trayRect.Top)
            {
                NoteFailure();
                return;
            }
            if (_lastTrayHandle != tray || trayRect.Bottom - trayRect.Top != _height)
            {
                // 任务栏重建或尺寸变了，重新判定明暗。
                _light = Theme.IsSystemTaskbarDark() == false;
            }
            _lastTrayHandle = tray;
            _firstFailure = DateTime.MinValue;
            _failed = false;
            Render();
        }

        private void Render()
        {
            int scale = (int)Math.Round(UiScale.RawFactor(this) * 100);
            float factor = scale / 100f;
            int barHeight = Math.Max(24, Math.Min(64, Native.GetSystemMetrics(Native.SM_CYSCREEN) > 0
                ? Math.Max(24, Math.Min(64, 40)) : 40));

            List<DeviceState> devices = DeviceVisibility.ConnectedDevices(_snapshot);
            List<TaskbarPiece> pieces = new List<TaskbarPiece>();
            if (devices.Count == 0)
            {
                pieces.Add(TaskbarStripStyle.UnknownDevicePiece(_light));
            }
            else
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    TaskbarStripStyle.AddDevice(pieces, devices[i], i, _light);
                    if (i + 1 < devices.Count && pieces.Count > 0)
                    {
                        pieces[pieces.Count - 1].GapAfter = TaskbarStripStyle.GapBetweenDevices;
                    }
                }
            }

            IntPtr iconFont = TaskbarStripRenderer.GetFont((int)Math.Round(16 * factor), 400,
                "Segoe Fluent Icons", "Segoe MDL2 Assets");
            IntPtr textFont = TaskbarStripRenderer.GetFont((int)Math.Round(13 * factor), 400,
                "Segoe UI Variable Text", "Segoe UI");
            TaskbarStripRenderer.Layout layout = TaskbarStripRenderer.Compose(
                pieces, _light, factor, iconFont, textFont, barHeight);
            _layout = layout;
            // 自绘路径的 GDI+ 字宽比 GDI 墨迹略宽，多留 12px 防止数字被裁掉。
            _width = layout.Width + (UseLegacyPaint ? (int)Math.Round(12 * factor) : 0);
            _height = layout.Height;

            Native.RECT trayRect;
            if (!Native.TryGetTrayRect(out trayRect))
            {
                NoteFailure();
                return;
            }
            // 作为任务栏子窗口时坐标相对任务栏客户区，不是屏幕坐标。
            Native.RECT barRect;
            bool parented = Native.GetWindowRect((Native.FindWindow("Shell_TrayWnd", null)), out barRect);
            int screenX = trayRect.Left - _width - 4 + AppSettings.Current.TaskbarOffsetX;
            int screenY = trayRect.Top + (trayRect.Bottom - trayRect.Top - _height) / 2;
            _screenX = screenX;
            _screenY = screenY;
            int x = parented ? screenX - barRect.Left : screenX;
            int y = parented ? screenY - barRect.Top : screenY;

            if (!UseLegacyPaint)
            {
                // 分层窗口的位置和尺寸都由 UpdateLayeredWindow 的 pptDst/psize 提交，
                // 不要再调 SetWindowPos：两次改位置会互相覆盖，窗口会落到 (0,0)。
                Native.SetWindowPos(Handle, Native.HwndTop, 0, 0, _width, _height,
                    SWP_NOACTIVATE | SWP_NOMOVE);
                CommitLayered(layout, x, y);
            }
            else
            {
                // 自绘路径：ClientSize 由 SetWindowPos 改，随后 OnPaint 按同一组 Piece 绘制。
                Native.SetWindowPos(Handle, Native.HwndTop, x, y, _width, _height,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW);
                Invalidate();
            }
        }

        /// <summary>把预乘 BGRA 位图提交到分层窗口；透明像素让点击穿透。</summary>
        private void CommitLayered(TaskbarStripRenderer.Layout layout, int x, int y)
        {
            IntPtr screenDC = Native.GetDC(IntPtr.Zero);
            IntPtr memoryDC = Native.CreateCompatibleDC(screenDC);
            IntPtr bitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            IntPtr bits = IntPtr.Zero;
            GCHandle pin = GCHandle.Alloc(layout.Pixels, GCHandleType.Pinned);
            try
            {
                // 直接用 CreateDIBSection 建 32bpp 位图：不要把预乘 BGRA 交给 GetHbitmap，
                // 后者会把 alpha=0 当透明色处理，整张图在 AC_SRC_ALPHA 下会变成全透明。
                Native.BITMAPINFOHEADER header = new Native.BITMAPINFOHEADER();
                header.Size = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
                header.Width = layout.Width;
                header.Height = -layout.Height; // 顶向下
                header.Planes = 1;
                header.BitCount = 32;
                header.Compression = 0; // BI_RGB
                bitmap = Native.CreateDIBSection(memoryDC, ref header, 0, out bits, IntPtr.Zero, 0);
                if (bitmap == IntPtr.Zero)
                {
                    Log.Warn("任务栏条 CreateDIBSection 失败：" + Marshal.GetLastWin32Error());
                    return;
                }
                Marshal.Copy(layout.Pixels, 0, bits, layout.Pixels.Length);
                oldBitmap = Native.SelectObject(memoryDC, bitmap);

                Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
                blend.BlendOp = 0;      // AC_SRC_OVER
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = Native.AC_SRC_ALPHA;
                Native.POINT dst = new Native.POINT(x, y);
                Native.POINT src = new Native.POINT(0, 0);
                Native.SIZE size = new Native.SIZE(layout.Width, layout.Height);
                bool updated = Native.UpdateLayeredWindow(Handle, screenDC, ref dst, ref size,
                    memoryDC, ref src, 0, ref blend, Native.ULW_ALPHA);
                if (!updated)
                {
                    Log.Warn("任务栏条分层提交失败：" +
                        Marshal.GetLastWin32Error() + " size=" + layout.Width + "x" + layout.Height +
                        " dst=" + x + "," + y);
                }
            }
            finally
            {
                pin.Free();
                if (oldBitmap != IntPtr.Zero) Native.SelectObject(memoryDC, oldBitmap);
                if (bitmap != IntPtr.Zero) Native.DeleteObject(bitmap);
                Native.DeleteDC(memoryDC);
                Native.ReleaseDC(IntPtr.Zero, screenDC);
            }
        }

        private void NoteFailure()
        {
            if (_firstFailure == DateTime.MinValue) _firstFailure = DateTime.Now;
            if ((DateTime.Now - _firstFailure).TotalSeconds >= 3)
            {
                _failed = true;
                _check.Stop();
                HideCard();
                Hide();
                Log.Warn("任务栏小组件无法定位，已自动回退到托盘图标");
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = TaskbarStripRenderer.HitTest(_layout, e.X);
            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                if (index >= 0) ShowCard(index); else HideCard();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverIndex = -1;
            HideCard();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int index = TaskbarStripRenderer.HitTest(_layout, e.X);
            if (index < 0 || _snapshot == null) return;
            List<DeviceState> devices = DeviceVisibility.ConnectedDevices(_snapshot);
            if (index >= devices.Count) return;
            // 点任务栏条上的某台设备：固定托盘到这台设备，并把面板打开。
            if (TrayApp.SelectDeviceAndShowPanel != null)
            {
                TrayApp.SelectDeviceAndShowPanel(devices[index].Id);
            }
        }

        private void ShowCard(int index)
        {
            if (_snapshot == null) return;
            List<DeviceState> devices = DeviceVisibility.ConnectedDevices(_snapshot);
            if (index >= devices.Count) return;
            if (_card == null || _card.IsDisposed) _card = new TaskbarCard();
            _card.ShowFor(devices[index], _screenX, _screenY - 8, _light);
        }

        private void HideCard()
        {
            if (_card != null && !_card.IsDisposed) _card.HideCard();
        }

        /// <summary>
        /// 可靠绘制路径：用 TransparencyKey 让背景透明，GDI+ 直接画字形/数字/闪电。
        /// 视觉规则和分层路径完全一致（同一套 TaskbarPiece），只是不走逐像素 alpha。
        /// </summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            if (!UseLegacyPaint) return;
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float scale = UiScale.RawFactor(this);
            int pad = (int)Math.Round(TaskbarStripStyle.Pad * scale);
            int x = pad;
            int y = Math.Max(0, (ClientSize.Height - (int)Math.Round(16 * scale)) / 2 - 2);
            Font iconFont = MakeLegacyFont((int)Math.Round(16 * scale), "Segoe Fluent Icons", "Segoe MDL2 Assets");
            Font numberFont = MakeLegacyFont((int)Math.Round(13 * scale), "Segoe UI Variable Text", "Segoe UI");
            Font boltFont = MakeLegacyFont((int)Math.Round(11 * scale), "Segoe Fluent Icons", "Segoe MDL2 Assets");
            List<TaskbarPiece> pieces = BuildPieces();
            for (int i = 0; i < pieces.Count; i++)
            {
                TaskbarPiece piece = pieces[i];
                bool isGlyph = piece.Text.Length == 1 && piece.Text[0] >= '\uE000';
                bool isBolt = isGlyph && piece.Text == TaskbarStripStyle.GlyphBolt;
                Font font = isBolt ? boltFont : (isGlyph ? iconFont : numberFont);
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(piece.Alpha, piece.Color)))
                {
                    g.DrawString(piece.Text, font, brush, x, y);
                }
                // 用 Masonry 的墨迹宽度近似：GDI+ 的 MeasureString 会多算 side bearing，
                // 几段累加后数字会被挤出窗口。字形按固定字号宽，数字按实测但留紧一点。
                int advance = isGlyph
                    ? (int)Math.Round((isBolt ? 11 : 16) * scale)
                    : (int)Math.Round(g.MeasureString(piece.Text, font).Width * 0.85f);
                x += advance + (int)Math.Round(piece.GapAfter * scale);
            }
            iconFont.Dispose();
            numberFont.Dispose();
            boltFont.Dispose();
        }

        private static Font MakeLegacyFont(int size, string preferred, string fallback)
        {
            try { return new Font(preferred, size, FontStyle.Regular, GraphicsUnit.Pixel); }
            catch (Exception)
            {
                try { return new Font(fallback, size, FontStyle.Regular, GraphicsUnit.Pixel); }
                catch (Exception) { return new Font(FontFamily.GenericSansSerif, size); }
            }
        }

        private List<TaskbarPiece> BuildPieces()
        {
            List<DeviceState> devices = DeviceVisibility.ConnectedDevices(_snapshot);
            List<TaskbarPiece> pieces = new List<TaskbarPiece>();
            if (devices.Count == 0)
            {
                pieces.Add(TaskbarStripStyle.UnknownDevicePiece(_light));
            }
            else
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    TaskbarStripStyle.AddDevice(pieces, devices[i], i, _light);
                    if (i + 1 < devices.Count && pieces.Count > 0)
                    {
                        pieces[pieces.Count - 1].GapAfter = TaskbarStripStyle.GapBetweenDevices;
                    }
                }
            }
            return pieces;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _check.Dispose();
                if (_card != null && !_card.IsDisposed) _card.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
