using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DeviceBattery
{
    internal sealed class DevicePanel : Form
    {
        private BatterySnapshot _snapshot = new BatterySnapshot();
        /// <summary>上一次喂进来的快照指纹，内容没变就不重绘（秒级刷新时省掉整窗重绘）。</summary>
        private string _snapshotSignature;
        private readonly Timer _outsideTimer;
        private DateTime _shownAt = DateTime.MinValue;
        private bool _refreshing;
        private bool _everActivated;
        private bool _wasMouseDown;
        /// <summary>
        /// 菜单关闭后的这一小段时间里不做外部点击收起：菜单就弹在面板外面，
        /// 用户点完菜单项时光标还停在菜单位置，会被当成“点了面板外面”。
        /// </summary>
        private DateTime _outsideIgnoreUntil = DateTime.MinValue;
        private bool _dragging;
        private bool _dragMoved;
        private bool _suppressClick;
        private Point _dragOffset;
        private string _footerStatus = "";

        private Rectangle _refreshRect;
        private Rectangle _diagnosticsRect;
        private Rectangle _historyRect;
        private Rectangle _unhideRect;
        private Rectangle _bluetoothRect;
        private Rectangle _backRect;
        private Panel _viewHost;
        private Form _embeddedView;
        private string _viewTitle = "";
        private Point _restoreLocation;
        private bool _hasRestore;
        private bool _suppressDeactivateHide;
        private bool _pinned;
        private Timer _releaseSuppressTimer;
        private bool _emptyLinkIsUnhide;
        private bool _cardLinkIsHide;
        private string _cardLinkDeviceId;
        private int _hover = -1;
        /// <summary>
        /// 「显示」弹出的菜单/对话框在面板外面：它们一打开面板就会失焦。
        /// 这段时间必须暂停失焦/外部点击收起，否则用户点完菜单项回来一看，面板早就没了。
        /// </summary>
        private int _menuOpenCount;

        private const int MaxVisibleCards = 4;
        // 设计坐标（96 DPI）。所有实际像素都由 Px() 换算，并且卡片步进统一用 CardStride，
        // 避免 Px(104)+Px(10) 与 Px(114) 在 125%/150% 这类小数缩放下四舍五入到不同值、
        // 逐张卡片累积错位。
        private const int PanelWidthDesign = 380;
        private const int HeaderHeightDesign = 60;
        private const int FooterHeightDesign = 74;
        // 卡片内部按四行排：名称行 / 电量行 / 进度条 / 明细行。112 = 8(下边距) + 16(明细) +
        // 6(进度条) + 8 + 34(电量) + 26(名称) + 14(上边距)，各行不再互相压住。
        private const int CardHeightDesign = 112;
        private const int CardGapDesign = 10;
        // 标题栏 60 + 一张卡片 112 + 页脚 74。
        private const int MinPanelHeightDesign = 246;
        private const int HorizontalPaddingDesign = 14;
        /// <summary>内嵌二级视图顶部返回栏的高度（设计坐标）。</summary>
        private const int TopBarHeightDesign = 44;

        private int _scrollOffset;
        private bool _scrollBarVisible;
        private bool _draggingScroll;
        private int _scrollDragOffset;
        private Rectangle _scrollBarHit;
        private Rectangle _scrollTrackRect;
        private Rectangle _scrollThumbRect;
        private Rectangle _settingsRect;

        public event EventHandler RefreshRequested;
        public event EventHandler SettingsRequested;
        public event EventHandler DiagnosticsRequested;
        public event EventHandler HistoryRequested;
        public event EventHandler OpenBluetoothRequested;
        /// <summary>用户点了面板上的「显示」：要求列出被隐藏的设备并挑一台取消隐藏。</summary>
        public event EventHandler UnhideRequested;
        /// <summary>用户点了强制显示卡片上的「隐藏」：把这台设备恢复成自动隐藏。</summary>
        public event Action<string> HideDeviceRequested;
        public event Action<string> DeviceRefreshRequested;

        /// <summary>
        /// 标记面板外面弹出了菜单/对话框：期间不因失焦或外部点击而收起。
        /// 事件驱动是一次性回调，成对调用即可；用计数是为了菜单里再弹菜单时不提前解锁。
        /// </summary>
        public void SetMenuOpen(bool open)
        {
            if (open)
            {
                _menuOpenCount++;
            }
            else if (_menuOpenCount > 0)
            {
                _menuOpenCount--;
                // 解锁后先静一小段：光标还在菜单原来的位置，别立刻把面板判成被点了外面。
                _outsideIgnoreUntil = DateTime.Now.AddMilliseconds(900);
                _wasMouseDown = IsMouseDown();
            }
        }

        public DevicePanel()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            KeyPreview = true;
            ApplyTheme();
            Font = Theme.UiFont(Theme.FontBody, FontStyle.Regular);
            AccessibleName = "VoltPick面板";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            ClientSize = new Size(Px(PanelWidthDesign), Px(332));

            _outsideTimer = new Timer();
            _outsideTimer.Interval = 250;
            _outsideTimer.Tick += delegate { CheckOutsideClick(); };
        }


        public void ApplyTheme()
        {
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Invalidate();
        }

        /// <summary>设计坐标（96 DPI）换算成当前 DPI 的实际像素。</summary>
        private int Px(int design)
        {
            return UiScale.Px(this, design);
        }

        private int HeaderHeight
        {
            get { return Px(HeaderHeightDesign); }
        }

        private int FooterHeight
        {
            get { return Px(FooterHeightDesign); }
        }

        private int CardHeight
        {
            get { return Px(CardHeightDesign); }
        }

        private int CardGap
        {
            get { return Px(CardGapDesign); }
        }

        /// <summary>相邻卡片的步进（卡片高 + 间距），卡片命中测试和绘制必须用同一个值。</summary>
        private int CardStride
        {
            get { return CardHeight + CardGap; }
        }

        private int CardLeft
        {
            get { return Px(HorizontalPaddingDesign); }
        }

        private int CardWidth
        {
            get { return Width - Px(HorizontalPaddingDesign) * 2; }
        }

        /// <summary>面板内容区（卡片区）的顶边，等于标题栏高度。</summary>
        private int ContentTop
        {
            get { return HeaderHeight; }
        }

        public void SetSnapshot(BatterySnapshot snapshot)
        {
            BatterySnapshot ordered = OrderForDisplay(snapshot ?? new BatterySnapshot());
            // 面板每秒钟被喂一次快照，但读电量是几十秒一次的：内容没变就原样返回，
            // 省掉每秒一次的重排 + 整窗重绘（原来即使数据一模一样也会闪一下）。
            string signature = BuildSignature(ordered);
            if (string.Equals(signature, _snapshotSignature, StringComparison.Ordinal))
            {
                return;
            }
            _snapshotSignature = signature;
            _snapshot = ordered;
            RecalculateSize();
            Invalidate();
        }

        /// <summary>
        /// 只把「画在面板上会变的东西」拼成指纹：设备顺序、名字、电量、电压、充电态、
        /// 状态文字、交通方式、读不到的原因、隐藏数量、蓝牙开关。更新时间也算在内，
        /// 因为明细行会显示「20:46 更新」。
        /// </summary>
        private static string BuildSignature(BatterySnapshot snapshot)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder(256);
            text.Append(snapshot.HiddenDeviceCount).Append('|');
            text.Append(snapshot.BluetoothRadioKnown ? '1' : '0');
            text.Append(snapshot.BluetoothRadioOn ? '1' : '0').Append('|');
            if (snapshot.Devices != null)
            {
                for (int i = 0; i < snapshot.Devices.Count; i++)
                {
                    DeviceState device = snapshot.Devices[i];
                    if (device == null)
                    {
                        text.Append("-|");
                        continue;
                    }
                    text.Append(device.Id).Append('\u0001');
                    text.Append(device.DisplayName).Append('\u0001');
                    text.Append(device.Status).Append('\u0001');
                    text.Append(device.StatusDetail).Append('\u0001');
                    text.Append(device.Transport).Append('\u0001');
                    text.Append(device.UnsupportedReason).Append('\u0001');
                    BatteryReading reading = device.LastReading;
                    if (reading != null)
                    {
                        text.Append(reading.Percent.HasValue ? reading.Percent.Value : -1).Append('\u0001');
                        text.Append(reading.Charging.HasValue ? (reading.Charging.Value ? '1' : '0') : '?');
                        text.Append('\u0001');
                        text.Append(reading.Millivolts.HasValue ? reading.Millivolts.Value : -1);
                        text.Append('\u0001').Append(reading.Timestamp.Ticks);
                    }
                    text.Append('|');
                }
            }
            return text.ToString();
        }

        /// <summary>
        /// 卡片顺序按「能不能马上看到有用信息」排：先有电量且在线，再在线但读不到电量
        /// （用户在设置里强制显示的），最后是已断连的强制显示项；组内按鼠标→耳机→键盘→
        /// 其他，再按名称。只复制一份列表，不改调用方的快照，历史页/诊断页顺序不受影响。
        /// </summary>
        private static BatterySnapshot OrderForDisplay(BatterySnapshot snapshot)
        {
            if (snapshot.Devices == null || snapshot.Devices.Count < 2) return snapshot;
            List<DeviceState> ordered = new List<DeviceState>(snapshot.Devices);
            ordered.Sort(CompareForDisplay);
            BatterySnapshot copy = new BatterySnapshot();
            copy.CapturedAt = snapshot.CapturedAt;
            copy.Devices = ordered;
            copy.HiddenDeviceCount = snapshot.HiddenDeviceCount;
            copy.BluetoothRadioOn = snapshot.BluetoothRadioOn;
            copy.BluetoothRadioKnown = snapshot.BluetoothRadioKnown;
            copy.BluetoothError = snapshot.BluetoothError;
            return copy;
        }

        private static int CompareForDisplay(DeviceState a, DeviceState b)
        {
            int diff = DisplayRank(a) - DisplayRank(b);
            if (diff != 0) return diff;
            diff = CategoryRank(a) - CategoryRank(b);
            if (diff != 0) return diff;
            string left = a == null || a.DisplayName == null ? "" : a.DisplayName;
            string right = b == null || b.DisplayName == null ? "" : b.DisplayName;
            return string.Compare(left, right, StringComparison.CurrentCultureIgnoreCase);
        }

        /// <summary>0=在线且有电量，1=在线但读不到电量，2=已断连（强制显示），3=异常。</summary>
        private static int DisplayRank(DeviceState device)
        {
            if (device == null) return 3;
            if (!device.Connected) return 2;
            return device.HasValue ? 0 : 1;
        }

        private static int CategoryRank(DeviceState device)
        {
            if (device == null) return 4;
            switch (device.Category)
            {
                case DeviceCategory.Mouse: return 0;
                case DeviceCategory.Headset: return 1;
                case DeviceCategory.Keyboard: return 2;
                default: return 3;
            }
        }

        public void SetFooterStatus(string text)
        {
            _footerStatus = text ?? "";
            Invalidate();
        }

        public void SetRefreshing(bool refreshing)
        {
            _refreshing = refreshing;
            Invalidate();
        }

        public void ShowNearCursor()
        {
            // 二级页（诊断/历史）开着时只把面板提到前面：
            // 这时点托盘图标不该按光标重新摆位置，否则刚拖好的面板会被挪走。
            if (EmbeddedViewActive)
            {
                BringToFront();
                return;
            }
            _scrollOffset = 0;
            RecalculateSize();
            Point target;
            if (AppSettings.Current.PanelPositionSaved)
            {
                target = new Point(AppSettings.Current.PanelX, AppSettings.Current.PanelY);
            }
            else
            {
                // 没被用户拖过时，面板固定贴在工作区右下角（任务栏上方），
                // 而不是跟着光标跑：光标往往刚离开托盘，跟着跑会离角落越来越远。
                Screen trayScreen = Screen.PrimaryScreen;
                Native.RECT tray;
                if (Native.TryGetTrayRect(out tray) && tray.Right > tray.Left && tray.Bottom > tray.Top)
                {
                    trayScreen = Screen.FromPoint(new Point((tray.Left + tray.Right) / 2,
                        (tray.Top + tray.Bottom) / 2));
                }
                Rectangle workArea = trayScreen == null ? Screen.PrimaryScreen.WorkingArea : trayScreen.WorkingArea;
                int margin = Px(12);
                target = new Point(workArea.Right - Width - margin, workArea.Bottom - Height - margin);
            }

            Screen screen = Screen.FromPoint(target);
            // 目标屏幕可能和光标所在屏幕不同，按目标屏幕再压一次高度。
            RecalculateSize(screen.WorkingArea.Height - Px(24));
            Location = PanelPlacement.ClampToWorkArea(target, Size, screen.WorkingArea, Px(8));

            _shownAt = DateTime.Now;
            _everActivated = false;
            _wasMouseDown = IsMouseDown();
            if (!Visible)
            {
                Show();
            }
            else
            {
                BringToFront();
            }
            _outsideTimer.Start();
            Log.Info("面板显示：" + Location.X + "," + Location.Y + " " + Width + "x" + Height);
        }

        /// <summary>
        /// 钉住面板：设置这类独立窗口开着的时候，面板不因失焦或点到别处而收起，
        /// 一直留在桌面右下角显示电量。
        /// </summary>
        public void SetPinned(bool pinned)
        {
            _pinned = pinned;
            // 钉住这一刻如果鼠标正按着，不算一次“外部点击”，否则会立刻收起。
            _wasMouseDown = pinned && IsMouseDown();
            if (pinned)
            {
                _shownAt = DateTime.Now;
            }
        }

        public void HidePanel()
        {
            if (Visible)
            {
                Log.Info("面板隐藏（everActivated=" + _everActivated + "）");
            }
            // 面板收起来时内嵌视图也一并收起，否则设置页会被留在一个不可见的面板里。
            if (EmbeddedViewActive)
            {
                CloseEmbeddedView();
            }
            _outsideTimer.Stop();
            Hide();
        }

        /// <summary>面板里是否正展开着一个二级内容（设置 / 历史 / 诊断）。</summary>
        public bool EmbeddedViewActive
        {
            get { return _embeddedView != null && !_embeddedView.IsDisposed; }
        }

        /// <summary>
        /// 把二级页面的内容就地嵌进面板：不再弹出独立窗口，用户始终停在这一层。
        /// 内容窗体由调用方创建并提供，这里只负责挂载和尺寸。
        /// </summary>
        public void ShowEmbeddedView(string title, Form content)
        {
            if (content == null)
            {
                return;
            }
            CloseEmbeddedView();
            _viewTitle = title ?? "";
            _embeddedView = content;
            // 先把内容窗体自己的尺寸记下来：一旦 Dock=Fill 挂进容器，
            // 它会被立刻拉成容器当前大小，之后再读 ClientSize 就是错的（面板会缩成 200x100）。
            Size desired = content.ClientSize;
            content.TopLevel = false;
            content.FormBorderStyle = FormBorderStyle.None;
            content.ShowInTaskbar = false;
            content.Dock = DockStyle.Fill;
            if (_viewHost == null)
            {
                _viewHost = new Panel();
                _viewHost.BackColor = Theme.Background;
                Controls.Add(_viewHost);
            }
            _viewHost.Controls.Add(content);
            _viewHost.Visible = true;
            // 内容窗体自己关掉（点关闭、按 Esc 走 CancelButton 等）时也要把面板还原，
            // 不能指望调用方记得接 FormClosed，否则面板会卡在二级页的尺寸上。
            content.FormClosed += delegate { CloseEmbeddedView(); };
            content.Show();
            int barHeight = Px(TopBarHeightDesign);
            int targetWidth = desired.Width;
            int targetHeight = desired.Height + barHeight;
            // 别让内嵌页超出屏幕：超了就按工作区收一收，内容自己会跟着缩。
            Screen screen = Screen.FromPoint(Cursor.Position);
            Rectangle area = screen.WorkingArea;
            targetWidth = Math.Min(targetWidth, area.Width - Px(16));
            targetHeight = Math.Min(targetHeight, area.Height - Px(16));
            // 记住展开前的位置：收起二级页时原位还原，用户不用再拖一次。
            if (!_hasRestore)
            {
                _restoreLocation = Location;
                _hasRestore = true;
            }
            // 面板平时贴着托盘、以右下角为准，展开时也按右下角当锚点向左上长。
            // 只放大尺寸而不动左上角的话，多出来的部分会顶出屏幕，
            // 下面那次夹取就会把整块面板推到屏幕左上角，关掉后又停在原地。
            int anchorRight = Right;
            int anchorBottom = Bottom;
            ClientSize = new Size(targetWidth, targetHeight);
            Location = new Point(anchorRight - Width, anchorBottom - Height);
            LayoutViewHost();
            // 面板本来贴着小托盘，展开成设置页后会变大，可能顶出屏幕：
            // 按当前屏幕工作区重新夹一次位置，别让内容跑到屏幕外面。
            Screen host = Screen.FromPoint(Location);
            if (host != null)
            {
                Location = PanelPlacement.ClampToWorkArea(Location, Size, host.WorkingArea, Px(8));
            }
            Invalidate();
        }

        /// <summary>收起内嵌视图，恢复到设备列表并按卡片数量重新定尺。</summary>
        public void CloseEmbeddedView()
        {
            Form closing = _embeddedView;
            if (closing == null)
            {
                return;
            }
            _embeddedView = null;
            _viewTitle = "";
            if (_viewHost != null)
            {
                _viewHost.Visible = false;
                try { _viewHost.Controls.Remove(closing); } catch (Exception) { }
            }
            _suppressDeactivateHide = true;
            try
            {
                if (!closing.IsDisposed)
                {
                    closing.Hide();
                    closing.Dispose();
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                // 释放子窗体引发的 Deactivate 有时是异步到达的，等一拍再解除抑制，
                // 不然点「关闭」会把整个面板一起收掉。
                if (_releaseSuppressTimer == null)
                {
                    _releaseSuppressTimer = new Timer();
                    _releaseSuppressTimer.Interval = 400;
                    _releaseSuppressTimer.Tick += delegate
                    {
                        _releaseSuppressTimer.Stop();
                        _suppressDeactivateHide = false;
                    };
                }
                _releaseSuppressTimer.Stop();
                _releaseSuppressTimer.Start();
            }
            RecalculateSize();
            if (_hasRestore)
            {
                // 回到展开前的位置，再夹一次屏幕，面板不会留在左上角。
                _hasRestore = false;
                Screen back = Screen.FromPoint(_restoreLocation);
                Location = PanelPlacement.ClampToWorkArea(_restoreLocation, Size,
                    back.WorkingArea, Px(8));
            }
            LayoutViewHost();
            Invalidate();
        }

        private void LayoutViewHost()
        {
            if (_viewHost == null)
            {
                return;
            }
            int top = Px(TopBarHeightDesign);
            _viewHost.Bounds = new Rectangle(0, top, ClientSize.Width,
                Math.Max(0, ClientSize.Height - top));
        }

        /// <summary>内嵌视图顶部那条返回栏：标题 + 「← 返回」。</summary>
        private void DrawViewBar(Graphics g)
        {
            using (SolidBrush brush = new SolidBrush(Theme.Background))
            {
                g.FillRectangle(brush, new Rectangle(0, 0, Width, Px(TopBarHeightDesign)));
            }
            int textLeft = Px(14);
            _backRect = new Rectangle(Width - Px(14) - Px(62), Px(10), Px(62), Px(24));
            Font font = Theme.PaintFont(Theme.FontValue, FontStyle.Bold);
            {
                TextRenderer.DrawText(g, _viewTitle,
                    font, new Rectangle(textLeft, 0, Math.Max(0, _backRect.Left - textLeft - Px(8)),
                        Px(TopBarHeightDesign)),
                    Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            DrawButton(g, _backRect, "← 返回", _hover == 6, true);
            using (Pen pen = new Pen(Theme.Divider, 1f))
            {
                g.DrawLine(pen, 0, Px(TopBarHeightDesign) - 1, Width, Px(TopBarHeightDesign) - 1);
            }
        }

        private void RecalculateSize()
        {
            // 内嵌视图的尺寸由内容窗体决定，别让卡片数量把它算回去。
            if (EmbeddedViewActive)
            {
                return;
            }
            RecalculateSize(MaxPanelHeight());
        }

        private void RecalculateSize(int maxHeight)
        {
            int count = _snapshot != null ? _snapshot.Devices.Count : 0;
            int natural = HeaderHeight + ContentHeight(count) + FooterHeight;
            int compact = HeaderHeight + ContentHeight(MaxVisibleCards) + FooterHeight;
            int limit = Math.Min(maxHeight, compact);
            int minimum = Px(MinPanelHeightDesign);
            if (limit < minimum) limit = minimum;
            int height = Math.Min(natural, limit);
            if (height < minimum) height = minimum;
            Size before = Size;
            int bottomBefore = Bottom;
            ClientSize = new Size(Px(PanelWidthDesign), height);
            _scrollBarVisible = ContentHeight() > ViewportHeight;
            ClampScroll();
            ApplyRegion();
            // 设备多一张卡面板就长高：底边是贴托盘的那条边，得固定住，
            // 否则新长出来的部分会顶出屏幕下沿，整块面板看起来“往下跑了”。
            if (Size != before)
            {
                KeepOnScreen(bottomBefore);
            }
        }

        /// <summary>
        /// 尺寸变化后把面板拉回屏幕：以底边为锚点（面板贴着托盘），再整体夹进工作区。
        /// 内嵌二级页时由 ShowEmbeddedView / CloseEmbeddedView 自己管位置，这里不插手。
        /// </summary>
        private void KeepOnScreen(int bottomBefore)
        {
            if (!Visible || _hasRestore)
            {
                return;
            }
            Screen screen = Screen.FromPoint(Location);
            if (screen == null)
            {
                return;
            }
            // 底边用调用方在改尺寸之前取的那条，收/放都在同一条边上长。
            Location = PanelPlacement.ClampToWorkArea(
                new Point(Left, bottomBefore - Height), Size, screen.WorkingArea, Px(8));
        }

        private int MaxPanelHeight()
        {
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            int height = area.Height - Px(24);
            return height < Px(MinPanelHeightDesign) ? Px(MinPanelHeightDesign) : height;
        }

        private int ContentHeight()
        {
            int count = _snapshot != null ? _snapshot.Devices.Count : 0;
            return ContentHeight(count);
        }

        private int ContentHeight(int count)
        {
            if (count <= 0) return 0;
            return count * CardHeight + Math.Max(0, count - 1) * CardGap;
        }

        private int ViewportHeight
        {
            get
            {
                int height = Height - ContentTop - FooterHeight;
                return height > 0 ? height : 0;
            }
        }

        private void ClampScroll()
        {
            int max = ContentHeight() - ViewportHeight;
            if (max < 0) max = 0;
            if (_scrollOffset > max) _scrollOffset = max;
            if (_scrollOffset < 0) _scrollOffset = 0;
        }

        private void ScrollBy(int delta)
        {
            if (!_scrollBarVisible) return;
            int max = ContentHeight() - ViewportHeight;
            if (max < 0) max = 0;
            int next = _scrollOffset + delta;
            if (next < 0) next = 0;
            if (next > max) next = max;
            if (next != _scrollOffset)
            {
                _scrollOffset = next;
                Invalidate();
            }
        }

        private void ScrollToThumb(int thumbTop)
        {
            int available = _scrollTrackRect.Height - _scrollThumbRect.Height;
            if (available <= 0) return;
            int max = ContentHeight() - ViewportHeight;
            if (max <= 0) return;
            int offset = (thumbTop - _scrollTrackRect.Top) * max / available;
            if (offset < 0) offset = 0;
            if (offset > max) offset = max;
            if (offset != _scrollOffset)
            {
                _scrollOffset = offset;
                Invalidate();
            }
        }

        private bool _dwmRoundTried;
        private bool _dwmRoundOk;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>
        /// 窗口圆角：Windows 11 用 DWM 的圆角（抗锯齿，边缘干净），
        /// 老系统退回 Region 裁剪（Region 是 1bit 遮罩，圆角会有锯齿台阶）。
        /// </summary>
        private void ApplyRegion()
        {
            if (!_dwmRoundTried)
            {
                _dwmRoundTried = true;
                try
                {
                    int preference = 2; // DWMWCP_ROUND
                    _dwmRoundOk = DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int)) == 0;
                }
                catch (Exception)
                {
                    _dwmRoundOk = false;
                }
                Log.Info("面板圆角：" + (_dwmRoundOk ? "DWM" : "Region"));
            }
            if (_dwmRoundOk)
            {
                if (Region != null)
                {
                    Region old = Region;
                    Region = null;
                    if (old != null) old.Dispose();
                }
                return;
            }
            try
            {
                using (GraphicsPath path = Rounded(ClientRectangle, Px(14)))
                {
                    Region old = Region;
                    Region = new Region(path);
                    if (old != null) old.Dispose();
                }
            }
            catch (Exception)
            {
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyRegion();
        }

        /// <summary>
        /// 拖到另一块缩放比例不同的显示器时，Windows 会发 DPI 变更消息。
        /// 不重新排版的话卡片、按钮还是旧 DPI 的绝对坐标，会错位甚至被裁掉。
        /// </summary>
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            RefreshDpiLayout(e.SuggestedRectangle);
        }

        /// <summary>按当前 DPI 重新计算面板尺寸（显示设置变化、切换显示器后调用）。</summary>
        public void RefreshDpiLayout()
        {
            RefreshDpiLayout(Rectangle.Empty);
        }

        private void RefreshDpiLayout(Rectangle suggested)
        {
            try
            {
                Rectangle area;
                if (suggested.IsEmpty)
                {
                    area = Screen.FromControl(this).WorkingArea;
                }
                else
                {
                    area = Screen.FromRectangle(suggested).WorkingArea;
                }
                RecalculateSize(area.Height - Px(24));
                PerformLayout();
                ApplyRegion();
                Invalidate(true);
            }
            catch (Exception ex)
            {
                Log.Warn("面板按新 DPI 重排失败：" + ex.Message);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            using (GraphicsPath path = Rounded(ClientRectangle, Px(14)))
            {
                using (LinearGradientBrush brush = new LinearGradientBrush(
                    ClientRectangle, Theme.PanelTop, Theme.PanelBottom, LinearGradientMode.Vertical))
                {
                    g.FillPath(brush, path);
                }
                using (Pen pen = new Pen(Theme.Border, 1f))
                {
                    g.DrawPath(pen, path);
                }
            }

            // 内嵌二级内容时只画返回栏，卡片和页脚都让位给内容窗体。
            if (EmbeddedViewActive)
            {
                DrawViewBar(g);
                return;
            }

            DrawHeader(g);
            int top = ContentTop;
            _emptyLinkIsUnhide = false;
            _cardLinkIsHide = false;
            _cardLinkDeviceId = null;
            if (_snapshot == null || _snapshot.Devices.Count == 0)
            {
                DrawEmptyCard(g, new Rectangle(CardLeft, top, CardWidth, CardHeight));
            }
            else
            {
                _bluetoothRect = Rectangle.Empty;
                GraphicsState state = g.Save();
                g.SetClip(new Rectangle(0, top, Width, ViewportHeight));
                for (int i = 0; i < _snapshot.Devices.Count; i++)
                {
                    Rectangle card = new Rectangle(CardLeft, top + i * CardStride - _scrollOffset,
                        CardWidth, CardHeight);
                    if (card.Bottom <= top || card.Top >= top + ViewportHeight) continue;
                    DrawCard(g, card, _snapshot.Devices[i]);
                }
                g.Restore(state);
            }
            DrawFooter(g);
            DrawScrollbar(g);
        }

        private void DrawHeader(Graphics g)
        {
            Font title = Theme.PaintFont(Theme.FontTitle, FontStyle.Bold);
            using (SolidBrush brush = new SolidBrush(Theme.Text))
            {
                g.DrawString("VoltPick", title, brush, Px(16), Px(12));
            }

            int count = _snapshot == null ? 0 : _snapshot.Devices.Count;
            int hidden = _snapshot == null ? 0 : _snapshot.HiddenDeviceCount;
            string subtitle;
            if (count > 0)
            {
                // 卡片超过可视区时要让人知道下面还有，否则会以为就这几台。
                subtitle = count + " 台已连接 · 实时电量";
                if (count > MaxVisibleCards)
                {
                    subtitle = count + " 台已连接 · 滚轮查看全部";
                }
            }
            else if (hidden > 0)
            {
                subtitle = "未检测到可读电量设备 · " + hidden + " 台已记录";
            }
            else
            {
                subtitle = "等待可读取电量的设备连接";
            }
            if (_snapshot != null && _snapshot.BluetoothRadioKnown && !_snapshot.BluetoothRadioOn)
            {
                // 蓝牙关了不该盖掉「有几台设备」这条更要紧的信息。
                subtitle = count > 0
                    ? count + " 台已连接 · 蓝牙已关闭"
                    : "蓝牙已关闭 · 鼠标/键盘仍可读取";
            }
            Font subtitleFont = Theme.PaintFont(Theme.FontSmall, FontStyle.Regular);
            using (SolidBrush brush = new SolidBrush(Theme.Muted))
            {
                // 副标题与标题共用同一条左边缘（原来是 27，标题是 16，看着像缩进错位）。
                g.DrawString(subtitle, subtitleFont, brush, Px(16), Px(34));
            }

            _refreshRect = new Rectangle(Width - Px(76), Px(13), Px(62), Px(26));
            DrawButton(g, _refreshRect, _refreshing ? "刷新中" : "刷新", _hover == 0, false);
            using (Pen divider = new Pen(Theme.Divider, 1f))
            {
                g.DrawLine(divider, Px(14), ContentTop - Px(4), Width - Px(14), ContentTop - Px(4));
            }
        }

        private void DrawCard(Graphics g, Rectangle rect, DeviceState device)
        {
            bool low = device.HasValue && device.PercentOrZero <= 20 &&
                       device.Status != DeviceStatus.Unsupported &&
                       device.Status != DeviceStatus.Offline;
            Color cardTop = _hover >= 10 ? Theme.CardHoverTop : Theme.CardTop;
            Color cardBottom = _hover >= 10 ? Theme.CardHoverBottom : Theme.CardBottom;
            using (GraphicsPath path = Rounded(rect, Px(11)))
            {
                using (LinearGradientBrush brush = new LinearGradientBrush(
                    rect, cardTop, cardBottom, LinearGradientMode.Vertical))
                {
                    g.FillPath(brush, path);
                }
                using (Pen pen = new Pen(low ? Color.FromArgb(170, Theme.Red) : Theme.CardBorder, 1f))
                {
                    g.DrawPath(pen, path);
                }
            }

            Color accent = Theme.StatusColor(device.Status, device.PercentOrZero);
            using (GraphicsPath accentPath = Rounded(new Rectangle(rect.Left + Px(1), rect.Top + Px(14),
                Px(3), rect.Height - Px(28)), Px(2)))
            using (SolidBrush accentBrush = new SolidBrush(Color.FromArgb(210, accent)))
            {
                g.FillPath(accentBrush, accentPath);
            }
            DrawDeviceIcon(g, new Rectangle(rect.Left + Px(16), rect.Top + Px(14), Px(26), Px(26)),
                device.Category, accent);

            // 右上角从外到内是「交通方式胶囊」和「低电」标签，名称的可用宽度跟着它们算，
            // 不再写死一个像素值（写死的话换长名字或四字胶囊就会撞在一起）。
            int rowRight = rect.Right - Px(16);
            string transport = device.TransportText;
            int chipWidth = MeasureChip(g, transport);
            int chipLeft = rowRight - chipWidth;
            int tagWidth = low ? Px(38) : 0;
            int tagLeft = low ? chipLeft - Px(6) - tagWidth : 0;
            int nameRight = (low ? tagLeft : chipLeft) - Px(8);
            int nameLeft = rect.Left + Px(52);
            Rectangle nameRect = new Rectangle(nameLeft, rect.Top + Px(15),
                Math.Max(Px(40), nameRight - nameLeft), Px(24));
            DrawText(g, device.DisplayName, nameRect, Theme.Text, 10.5f, FontStyle.Bold, true);

            if (low)
            {
                DrawTag(g, new Rectangle(tagLeft, rect.Top + Px(18), tagWidth, Px(18)), "低电");
            }
            DrawChip(g, new Rectangle(chipLeft, rect.Top + Px(18), chipWidth, Px(18)), transport);

            int percent = device.PercentOrZero;
            // 「不支持读取」的设备不报数字：哪怕还留着上一次的读数，
            // 旁边已经写着读不到，上面再顶个 0% 会让人以为电量真掉到 0 了。
            bool showPercent = device.HasValue && device.Status != DeviceStatus.Unsupported;
            string big = showPercent ? percent.ToString() : "—";
            Rectangle bigRect = new Rectangle(nameLeft, rect.Top + Px(41), Px(110), Px(34));
            DrawPercent(g, bigRect, big, showPercent, accent, device.Status == DeviceStatus.Charging);

            // 状态文字和电量数字同一行（原来它压在进度条那一行的右边，下面离明细只剩 2px）。
            string status = StatusLine(device);
            int statusLeft = nameLeft + Px(110);
            Rectangle statusRect = new Rectangle(statusLeft, rect.Top + Px(41),
                Math.Max(Px(40), rowRight - statusLeft), Px(34));
            DrawText(g, status, statusRect, accent, 8.5f, FontStyle.Bold, false, ContentAlignment.MiddleRight);

            // 进度条铺满整行：原来只有 172px，右半张卡片空着，像没排完版。
            Rectangle bar = new Rectangle(nameLeft, rect.Top + Px(79),
                Math.Max(Px(40), rowRight - nameLeft), Px(6));
            DrawBar(g, bar, showPercent ? percent : 0, accent,
                device.Status == DeviceStatus.Unsupported || device.Status == DeviceStatus.Offline ||
                device.Status == DeviceStatus.BluetoothOff);

            bool hasLink = device.Transport == TransportKind.Usb24G ||
                           device.Status == DeviceStatus.BluetoothOff ||
                           device.Transport == TransportKind.BluetoothClassic ||
                           device.ForceVisible;
            string detail = DetailLine(device);
            int linkWidth = hasLink ? Px(92) : 0;
            int detailWidth = Math.Max(Px(60),
                rowRight - nameLeft - (hasLink ? linkWidth + Px(6) : 0));
            Rectangle detailRect = new Rectangle(nameLeft, rect.Top + Px(88),
                detailWidth, Px(16));
            DrawText(g, detail, detailRect, Theme.Muted, 8f, FontStyle.Regular, true);

            if (hasLink)
            {
                _bluetoothRect = new Rectangle(rowRight - linkWidth, rect.Top + Px(88),
                    linkWidth, Px(16));
                if (device.ForceVisible)
                {
                    // 解除隐藏的卡片上直接给「隐藏」，不用回设置里取消。
                    _cardLinkIsHide = true;
                    _cardLinkDeviceId = device.Id;
                    DrawLink(g, _bluetoothRect, "隐藏", _hover == 3);
                }
                else
                {
                    // 每张卡片都要重置，否则上一张卡片的「隐藏」会作用到下一张的链接上。
                    _cardLinkIsHide = false;
                    _cardLinkDeviceId = null;
                    DrawLink(g, _bluetoothRect, device.Status == DeviceStatus.BluetoothOff ? "打开蓝牙" : "打开蓝牙设置",
                        _hover == 3);
                }
            }
        }

        private void DrawEmptyCard(Graphics g, Rectangle rect)
        {
            using (GraphicsPath path = Rounded(rect, Px(10)))
            {
                using (SolidBrush brush = new SolidBrush(Theme.Card))
                {
                    g.FillPath(brush, path);
                }
                using (Pen pen = new Pen(Theme.Border, 1f))
                {
                    g.DrawPath(pen, path);
                }
            }

            int hidden = _snapshot == null ? 0 : _snapshot.HiddenDeviceCount;
            // 读不到电量的设备按规则一律不显示，所以面板可能一台都没有：
            // 空态必须把“没检测到可读取电量的设备”讲清楚，而不是留一张空白卡片。
            DrawText(g, "未检测到可读取电量的设备", new Rectangle(rect.Left + Px(16), rect.Top + Px(16),
                rect.Width - Px(32), Px(22)), Theme.Text, 11f, FontStyle.Bold, false);
            // 行距按 18px 设计：三行说明 + 一个链接刚好排满 104px 的卡片，
            // 各行矩形不再相互压住（旧间距里第三行和链接重叠了 2px）。
            if (hidden > 0)
            {
                DrawText(g, hidden + " 台已记录设备当前读不到电量，按规则不显示", new Rectangle(rect.Left + Px(16),
                    rect.Top + Px(46), rect.Width - Px(32), Px(18)), Theme.Muted, 8.5f, FontStyle.Regular, false);
                // 之前这里还有一行「点下面的『显示』挑出来」，指的就是页脚那个按钮；
                // 现在空状态只留这一个链接入口，那行提示就成了误导，删掉。
                _emptyLinkIsUnhide = true;
                _bluetoothRect = new Rectangle(rect.Left + Px(16), rect.Top + Px(70), Px(168), Px(20));
                DrawLink(g, _bluetoothRect, "显示被隐藏的 " + hidden + " 台设备", _hover == 3);
            }
            else
            {
                    _emptyLinkIsUnhide = false;
                DrawText(g, "键盘 / 鼠标：支持标准 HID 电量", new Rectangle(rect.Left + Px(16), rect.Top + Px(46),
                    rect.Width - Px(32), Px(18)), Theme.Muted, 8.5f, FontStyle.Regular, false);
                DrawText(g, "耳机：使用蓝牙连接；2.4G 接收器不提供电量", new Rectangle(rect.Left + Px(16), rect.Top + Px(66),
                    rect.Width - Px(32), Px(18)), Theme.Muted, 8.5f, FontStyle.Regular, false);
                _bluetoothRect = new Rectangle(rect.Left + Px(16), rect.Top + Px(88), Px(92), Px(16));
                DrawLink(g, _bluetoothRect, "打开蓝牙设置", _hover == 3);
            }
        }

        private void DrawFooter(Graphics g)
        {
            // 页脚加高到 74：上一排放按钮，下一排放状态文字，
            // 按钮铺满整行，右边不再空一大片。
            int y = Height - Px(46);
            using (Pen divider = new Pen(Theme.Divider, 1f))
            {
                g.DrawLine(divider, Px(14), y - Px(10), Width - Px(14), y - Px(10));
            }
            // 按钮铺满整行：之前四个按钮都挤在左边 46px 宽，
            // 右边空着一大片，看起来像没排完版。
            int hiddenCount = _snapshot == null ? 0 : _snapshot.HiddenDeviceCount;
            // 空状态里已经有一行「显示被隐藏的 N 台设备」链接，页脚不再重复同一个入口，
            // 只留设置/诊断/历史，空面板的按钮也能宽一点。
            bool hasCards = _snapshot != null && _snapshot.Devices.Count > 0;
            bool showUnhide = hiddenCount > 0 && hasCards;
            int buttonCount = showUnhide ? 4 : 3;
            int gap = Px(6);
            int margin = Px(14);
            int available = Width - margin * 2 - gap * (buttonCount - 1);
            int buttonWidth = Math.Max(Px(46), available / buttonCount);
            int x = margin;
            _settingsRect = new Rectangle(x, y, buttonWidth, Px(24));
            x += buttonWidth + gap;
            _diagnosticsRect = new Rectangle(x, y, buttonWidth, Px(24));
            x += buttonWidth + gap;
            _historyRect = new Rectangle(x, y, buttonWidth, Px(24));
            x += buttonWidth + gap;
            // 有设备被隐藏时，面板上直接给一个「显示」入口。
            _unhideRect = showUnhide
                ? new Rectangle(x, y, buttonWidth, Px(24))
                : Rectangle.Empty;
            DrawButton(g, _settingsRect, "设置", _hover == 1, true);
            DrawButton(g, _diagnosticsRect, "诊断", _hover == 2, true);
            DrawButton(g, _historyRect, "历史", _hover == 4, true);
            if (showUnhide)
            {
                DrawButton(g, _unhideRect, "显示 " + hiddenCount, _hover == 5, true);
            }

            string status = _footerStatus;
            if (string.IsNullOrEmpty(status) && _snapshot != null)
            {
                if (!_snapshot.BluetoothRadioKnown)
                {
                    status = "蓝牙状态未知";
                }
                else
                {
                    status = _snapshot.BluetoothRadioOn ? "蓝牙：开" : "蓝牙：关";
                }
            }
            Rectangle statusRect = new Rectangle(Px(14), Height - Px(20),
                Width - Px(28), Px(16));
            DrawText(g, status, statusRect, Theme.Faint, 8f, FontStyle.Regular, false, ContentAlignment.MiddleCenter);
        }

        private void DrawScrollbar(Graphics g)
        {
            _scrollBarHit = Rectangle.Empty;
            _scrollTrackRect = Rectangle.Empty;
            _scrollThumbRect = Rectangle.Empty;
            if (!_scrollBarVisible) return;

            int top = ContentTop + Px(2);
            int height = ViewportHeight - Px(4);
            if (height < Px(12)) return;
            _scrollBarHit = new Rectangle(Width - Px(18), top - Px(2), Px(18), height + Px(4));
            _scrollTrackRect = new Rectangle(Width - Px(10), top, Px(4), height);

            using (GraphicsPath path = Rounded(_scrollTrackRect, Px(2)))
            using (SolidBrush brush = new SolidBrush(Theme.Divider))
            {
                g.FillPath(brush, path);
            }

            int content = ContentHeight();
            int thumbHeight = (int)Math.Round((double)height * ViewportHeight / Math.Max(1, content));
            if (thumbHeight < Px(24)) thumbHeight = Px(24);
            if (thumbHeight > height) thumbHeight = height;

            int maxOffset = content - ViewportHeight;
            int y = top;
            if (maxOffset > 0)
            {
                y += (int)Math.Round((double)(height - thumbHeight) * _scrollOffset / maxOffset);
            }
            _scrollThumbRect = new Rectangle(_scrollTrackRect.X - Px(1), y,
                _scrollTrackRect.Width + Px(2), thumbHeight);
            using (GraphicsPath path = Rounded(_scrollThumbRect, Px(2)))
            using (SolidBrush brush = new SolidBrush(_draggingScroll ? Theme.Muted : Theme.Faint))
            {
                g.FillPath(brush, path);
            }
        }

        private static string StatusLine(DeviceState device)
        {
            switch (device.Status)
            {
                case DeviceStatus.Charging:
                    return "充电中";
                case DeviceStatus.Online:
                    if (device.HasValue)
                    {
                        return "在线";
                    }
                    // 解除隐藏显示出来的设备本来就读不到电量，说「读取中」会误导。
                    return device.BatterySupported ? "读取中" : "不支持读取";
                case DeviceStatus.Sleeping:
                    return "等待响应";
                case DeviceStatus.Stale:
                    return "数据过期";
                case DeviceStatus.Offline:
                    return "未连接";
                case DeviceStatus.Unsupported:
                    return "不支持读取";
                case DeviceStatus.BluetoothOff:
                    return "蓝牙已关闭";
                case DeviceStatus.Error:
                    return "读取异常";
                default:
                    return "未知";
            }
        }

        private string DetailLine(DeviceState device)
        {
            if (device.Status == DeviceStatus.Unsupported)
            {
                string reason = device.UnsupportedReason;
                if (string.IsNullOrEmpty(reason))
                {
                    return "此连接方式不提供电量";
                }
                // 服务层的原因是要讲清楚「为什么」，句子很长；面板一行只有不到 300px，
                // 直接画会被省略号吃掉后半句（恰恰是结论那半句）。这里换成等义的短句，
                // 完整原因仍然留在诊断页和提示条里。
                if (reason.IndexOf("2.4G", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "2.4G 接收器不提供电量，请用蓝牙";
                }
                if (reason.IndexOf("标准 HID 电量接口", StringComparison.Ordinal) >= 0)
                {
                    return "无标准 HID 电量页（多为有线键盘）";
                }
                if (reason.IndexOf("HFP", StringComparison.Ordinal) >= 0)
                {
                    return "经典蓝牙 HFP 被系统音频占用，请用 LE";
                }
                return reason;
            }
            if (device.Status == DeviceStatus.BluetoothOff)
            {
                return "蓝牙已断开，打开后自动重查";
            }
            if (device.Status == DeviceStatus.Sleeping)
            {
                return string.IsNullOrEmpty(device.StatusDetail)
                    ? "等待设备响应"
                    : device.StatusDetail;
            }
            if (device.Status == DeviceStatus.Offline)
            {
                if (device.HasValue)
                {
                    return "最后读数 " + Stamp(device.LastReading.Timestamp) + " · 保留 24 小时";
                }
                return string.IsNullOrEmpty(device.StatusDetail) ? "未检测到设备" : device.StatusDetail;
            }

            string detail = "";
            bool chargingUnknown = device.HasValue && device.LastReading.Charging == null;
            if (AppSettings.Current.ShowVoltage && device.LastReading != null &&
                device.LastReading.Millivolts.HasValue)
            {
                detail = string.Format("{0:0.00} V", device.LastReading.Millivolts.Value / 1000.0);
            }
            if (chargingUnknown)
            {
                detail = string.IsNullOrEmpty(detail) ? "充电状态未知" : "充电状态未知 · " + detail;
            }

            if (device.LastReading != null &&
                (device.LastReading.LeftPercent.HasValue || device.LastReading.RightPercent.HasValue))
            {
                string lr = string.Format("L {0} · R {1}",
                    device.LastReading.LeftPercent.HasValue
                        ? device.LastReading.LeftPercent.Value + "%" : "—",
                    device.LastReading.RightPercent.HasValue
                        ? device.LastReading.RightPercent.Value + "%" : "—");
                if (device.LastReading.CasePercent.HasValue)
                {
                    lr += string.Format(" · C {0}%", device.LastReading.CasePercent.Value);
                }
                detail = string.IsNullOrEmpty(detail) ? lr : detail + " · " + lr;
            }

            if (HasLargeEarDifference(device.LastReading))
            {
                detail += " · 左右耳电量差异较大";
            }

            string stamp = Stamp(device.LastReading == null ? DateTime.MinValue : device.LastReading.Timestamp);
            detail = string.IsNullOrEmpty(detail) ? stamp + " 更新" : detail + " · " + stamp + " 更新";

            if (device.Status == DeviceStatus.Stale)
            {
                detail += " · 可能已过期";
            }
            else if (device.Status == DeviceStatus.Error && !string.IsNullOrEmpty(device.LastError))
            {
                detail += " · " + device.LastError;
            }
            else if (!string.IsNullOrEmpty(device.LastReading.Error))
            {
                detail += " · " + device.LastReading.Error;
            }
            return detail;
        }

        internal static bool HasLargeEarDifference(BatteryReading reading)
        {
            return reading != null &&
                   reading.LeftPercent.HasValue && reading.RightPercent.HasValue &&
                   Math.Abs(reading.LeftPercent.Value - reading.RightPercent.Value) >= 20;
        }

        private static string Stamp(DateTime timestamp)
        {
            if (timestamp == DateTime.MinValue)
            {
                return "--:--";
            }
            if (timestamp.Date == DateTime.Now.Date)
            {
                return timestamp.ToString("HH:mm");
            }
            return timestamp.ToString("MM-dd HH:mm");
        }

        private void DrawDeviceIcon(Graphics g, Rectangle rect, DeviceCategory category, Color color)
        {
            using (Pen pen = new Pen(color, Math.Max(1.2f, Px(2))))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(50, color)))
            {
                if (category == DeviceCategory.Mouse)
                {
                    using (GraphicsPath body = Rounded(new RectangleF(rect.X + rect.Width * 0.22f, rect.Y + rect.Height * 0.05f,
                        rect.Width * 0.56f, rect.Height * 0.9f), rect.Width * 0.26f))
                    {
                        g.FillPath(brush, body);
                        g.DrawPath(pen, body);
                    }
                    g.DrawLine(pen, rect.X + rect.Width * 0.5f, rect.Y + rect.Height * 0.12f,
                        rect.X + rect.Width * 0.5f, rect.Y + rect.Height * 0.38f);
                }
                else if (category == DeviceCategory.Keyboard)
                {
                    RectangleF body = new RectangleF(rect.X + rect.Width * 0.06f,
                        rect.Y + rect.Height * 0.32f, rect.Width * 0.88f, rect.Height * 0.48f);
                    using (GraphicsPath keyboard = Rounded(body, rect.Width * 0.09f))
                    {
                        g.FillPath(brush, keyboard);
                        g.DrawPath(pen, keyboard);
                    }
                    // 三排键帽的简化线条，缩到托盘面板尺寸仍能辨认。
                    for (int row = 0; row < 3; row++)
                    {
                        float y = body.Top + body.Height * (0.24f + row * 0.24f);
                        for (int col = 0; col < 5; col++)
                        {
                            float x = body.Left + body.Width * (0.12f + col * 0.18f);
                            g.DrawLine(pen, x, y, x + body.Width * 0.10f, y);
                        }
                    }
                }
                else
                {
                    g.DrawArc(pen, rect.X + rect.Width * 0.12f, rect.Y + rect.Height * 0.08f,
                        rect.Width * 0.76f, rect.Height * 0.76f, 190f, 160f);
                    using (GraphicsPath left = Rounded(new RectangleF(rect.X + rect.Width * 0.06f,
                        rect.Y + rect.Height * 0.42f, rect.Width * 0.2f, rect.Height * 0.42f), rect.Width * 0.08f))
                    using (GraphicsPath right = Rounded(new RectangleF(rect.X + rect.Width * 0.74f,
                        rect.Y + rect.Height * 0.42f, rect.Width * 0.2f, rect.Height * 0.42f), rect.Width * 0.08f))
                    {
                        g.FillPath(brush, left);
                        g.DrawPath(pen, left);
                        g.FillPath(brush, right);
                        g.DrawPath(pen, right);
                    }
                }
            }
        }

        private void DrawPercent(Graphics g, Rectangle rect, string text, bool hasValue, Color color, bool charging)
        {
            // 读不到电量时只是一条破折号，用 22pt 画出来又大又空，压成 15pt 更像个占位符。
            Font big = Theme.PaintFont(hasValue ? 22f : 15f, FontStyle.Bold);
            Font small = Theme.PaintFont(Theme.FontSmall, FontStyle.Bold);
            using (SolidBrush brush = new SolidBrush(hasValue ? color : Theme.Faint))
            {
                SizeF size = g.MeasureString(text, big, PointF.Empty, StringFormat.GenericTypographic);
                g.DrawString(text, big, brush, rect.Left, rect.Top, StringFormat.GenericTypographic);
                if (hasValue)
                {
                    g.DrawString("%", small, brush, rect.Left + size.Width - Px(2), rect.Top + Px(16));
                }
                if (charging)
                {
                    DrawSmallBolt(g, rect.Left + size.Width + Px(10), rect.Top + Px(4), color);
                }
            }
        }

        private void DrawSmallBolt(Graphics g, float x, float y, Color color)
        {
            float w = Px(11);
            float h = Px(15);
            PointF[] points = new PointF[]
            {
                new PointF(x + w * 0.55f, y),
                new PointF(x + w * 0.02f, y + h * 0.55f),
                new PointF(x + w * 0.42f, y + h * 0.55f),
                new PointF(x + w * 0.22f, y + h),
                new PointF(x + w * 0.98f, y + h * 0.38f),
                new PointF(x + w * 0.56f, y + h * 0.38f)
            };
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.FillPolygon(brush, points);
            }
        }

        private void DrawBar(Graphics g, Rectangle rect, int percent, Color color, bool muted)
        {
            using (GraphicsPath path = Rounded(rect, rect.Height / 2f))
            using (SolidBrush brush = new SolidBrush(Theme.Track))
            {
                g.FillPath(brush, path);
            }
            if (percent <= 0 || muted)
            {
                return;
            }
            int width = Math.Max(rect.Height, (int)Math.Round(rect.Width * Math.Min(100, percent) / 100.0));
            Rectangle fill = new Rectangle(rect.Left, rect.Top, width, rect.Height);
            using (GraphicsPath path = Rounded(fill, rect.Height / 2f))
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.FillPath(brush, path);
            }
        }

        private void DrawButton(Graphics g, Rectangle rect, string text, bool hover, bool subtle)
        {
            // 浅色主题下 Theme.Button 是纯白，画在近白的面板底上只剩一条淡边，
            // 看着像“一条小条”，这里单独给浅色主题一点灰调。
            Color fill = hover ? Theme.ButtonHover : Theme.Button;
            if (!Theme.IsDark)
            {
                fill = hover ? Color.FromArgb(226, 233, 243) : Color.FromArgb(240, 244, 250);
            }
            using (GraphicsPath path = Rounded(rect, Px(6)))
            {
                using (SolidBrush brush = new SolidBrush(fill))
                {
                    g.FillPath(brush, path);
                }
                if (!subtle)
                {
                    using (Pen pen = new Pen(Theme.Border, 1f))
                    {
                        g.DrawPath(pen, path);
                    }
                }
            }
            DrawText(g, text, rect, subtle ? Theme.Muted : Theme.Text, 8.5f, FontStyle.Bold, false, ContentAlignment.MiddleCenter);
        }

        /// <summary>
        /// 胶囊宽度按文字量出来。固定 58 时「USB」右边空一大块，而「蓝牙 LE」又几乎顶到边，
        /// 两种都难看；这里量出文字宽度再加左右内边距，并夹在 38–86 之间防止极端值。
        /// </summary>
        private int MeasureChip(Graphics g, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Px(38);
            }
            Font font = Theme.PaintFont(7.5f, FontStyle.Bold);
            SizeF size = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
            int width = (int)Math.Ceiling(size.Width) + Px(16);
            if (width < Px(38)) width = Px(38);
            if (width > Px(86)) width = Px(86);
            return width;
        }

        private void DrawChip(Graphics g, Rectangle rect, string text)
        {
            using (GraphicsPath path = Rounded(rect, rect.Height / 2f))
            using (SolidBrush brush = new SolidBrush(Theme.Chip))
            {
                g.FillPath(brush, path);
            }
            DrawText(g, text, rect, Theme.Muted, 7.5f, FontStyle.Bold, false, ContentAlignment.MiddleCenter);
        }

        private void DrawTag(Graphics g, Rectangle rect, string text)
        {
            using (GraphicsPath path = Rounded(rect, rect.Height / 2f))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(45, Theme.Red)))
            {
                g.FillPath(brush, path);
            }
            DrawText(g, text, rect, Theme.Red, 7.5f, FontStyle.Bold, false, ContentAlignment.MiddleCenter);
        }

        private void DrawLink(Graphics g, Rectangle rect, string text, bool hover)
        {
            DrawText(g, text, rect, hover ? Theme.Text : Theme.Blue, 8f, FontStyle.Bold, false, ContentAlignment.MiddleRight);
        }

        /// <summary>
        /// 重绘热路径用的 StringFormat 缓存：按（是否省略号, 对齐方式）复用同一个实例。
        /// 这些是进程内共享的静态对象，不 Dispose，也不允许外部修改。
        /// </summary>
        private static readonly StringFormat[] TextFormats = new StringFormat[6];

        private static StringFormat TextFormat(bool trim, ContentAlignment align)
        {
            int alignment = align == ContentAlignment.MiddleRight ? 1
                : (align == ContentAlignment.MiddleCenter ? 2 : 0);
            int slot = (trim ? 0 : 3) + alignment;
            StringFormat format = TextFormats[slot];
            if (format == null)
            {
                format = new StringFormat(StringFormat.GenericTypographic);
                format.Trimming = trim ? StringTrimming.EllipsisCharacter : StringTrimming.None;
                format.FormatFlags |= StringFormatFlags.NoWrap;
                format.Alignment = alignment == 1 ? StringAlignment.Far
                    : (alignment == 2 ? StringAlignment.Center : StringAlignment.Near);
                format.LineAlignment = StringAlignment.Center;
                TextFormats[slot] = format;
            }
            return format;
        }

        private void DrawText(Graphics g, string text, Rectangle rect, Color color, float size, FontStyle style,
            bool trim, ContentAlignment align)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            Font font = Theme.PaintFont(size, style);
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.DrawString(text, font, brush, rect, TextFormat(trim, align));
            }
        }

        private void DrawText(Graphics g, string text, Rectangle rect, Color color, float size, FontStyle style, bool trim)
        {
            DrawText(g, text, rect, color, size, style, trim, ContentAlignment.MiddleLeft);
        }

        private void CheckOutsideClick()
        {
            if (!Visible)
            {
                _outsideTimer.Stop();
                return;
            }
            if ((DateTime.Now - _shownAt).TotalMilliseconds < 250)
            {
                return;
            }
            if (DateTime.Now < _outsideIgnoreUntil)
            {
                // 刚关掉面板外面的菜单：这一拍鼠标停在菜单位置，不能当外部点击。
                _wasMouseDown = IsMouseDown();
                return;
            }

            bool down = IsMouseDown();
            if (down && !_wasMouseDown && !Bounds.Contains(Cursor.Position) && !EmbeddedViewActive && !_pinned &&
                _menuOpenCount == 0)
            {
                // 内嵌视图开着时不做外部点击收起：设置页里的确认框、选色框都在面板外面，
                // 点它们会被当成“点外面”，面板一收就把设置页连同对话框一起销毁。
                Log.Info("面板因外部点击隐藏，cursor=" + Cursor.Position.X + "," + Cursor.Position.Y);
                HidePanel();
            }
            _wasMouseDown = down;
        }

        private static bool IsMouseDown()
        {
            return (Native.GetAsyncKeyState(0x01) & 0x8000) != 0 ||
                   (Native.GetAsyncKeyState(0x02) & 0x8000) != 0;
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            _everActivated = true;
            // 面板重新拿到焦点，说明收视图那一拍已经过去了。
            _suppressDeactivateHide = false;
            if (_releaseSuppressTimer != null)
            {
                _releaseSuppressTimer.Stop();
            }
            // 窗体刚建句柄时也会收到一次 Activated，那时面板根本没显示；只记真正弹出来的那次。
            if (Visible)
            {
                Log.Info("面板获得焦点");
            }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            // ShowWithoutActivation 显示时可能立刻触发一次 Deactivate；只有用户真正点过面板后才用失焦关闭。
            if (!_everActivated || _suppressDeactivateHide)
            {
                return;
            }
            // 内嵌视图（设置/历史/诊断）开着时不跟着失焦关闭：
            // 那里面弹确认框、选色框都会让面板失焦，一收起就会把设置页连对话框一起销毁。
            // 离开这一页走「← 返回」或 Esc，主动收起面板也照样关。
            if (EmbeddedViewActive || _pinned)
            {
                return;
            }
            // 「显示」菜单开着的时候不收起：菜单本身在面板外面，点它必然让面板失焦，
            // 一收起用户就看不到刚放出来的设备，还以为点了没反应。
            if (_menuOpenCount > 0)
            {
                return;
            }
            HidePanel();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                // 展开着设置/历史/诊断时，Esc 先退回设备列表，再按一次才收起面板。
                if (EmbeddedViewActive)
                {
                    CloseEmbeddedView();
                    return;
                }
                HidePanel();
                return;
            }
            if (_scrollBarVisible)
            {
                if (e.KeyCode == Keys.PageDown) { ScrollBy(ViewportHeight - Px(24)); return; }
                if (e.KeyCode == Keys.PageUp) { ScrollBy(-(ViewportHeight - Px(24))); return; }
                if (e.KeyCode == Keys.Home) { ScrollBy(-ContentHeight()); return; }
                if (e.KeyCode == Keys.End) { ScrollBy(ContentHeight()); return; }
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _suppressClick = false;
            }
            if (e.Button == MouseButtons.Left && _scrollBarVisible && _scrollBarHit.Contains(e.Location))
            {
                if (_scrollThumbRect.Contains(e.Location))
                {
                    _draggingScroll = true;
                    _scrollDragOffset = e.Y - _scrollThumbRect.Top;
                    Capture = true;
                }
                else
                {
                    ScrollBy(e.Y < _scrollThumbRect.Top ? -ViewportHeight : ViewportHeight);
                }
                return;
            }
            if (e.Button == MouseButtons.Left && e.Y < ContentTop - Px(2) && !_refreshRect.Contains(e.Location))
            {
                _dragging = true;
                _dragMoved = false;
                _dragOffset = new Point(Cursor.Position.X - Left, Cursor.Position.Y - Top);
                Capture = true;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_draggingScroll)
            {
                _draggingScroll = false;
                Capture = false;
                _suppressClick = true;
                Invalidate();
                return;
            }
            if (_dragging)
            {
                _dragging = false;
                Capture = false;
                if (_dragMoved)
                {
                    // 手动拖过就以用户放的为准，收起二级页时别再弹回老位置。
                    _hasRestore = false;
                    AppSettings.Current.PanelPositionSaved = true;
                    AppSettings.Current.PanelX = Left;
                    AppSettings.Current.PanelY = Top;
                    try { AppSettings.Save(); } catch (Exception) { }
                    Log.Info("面板位置已记忆：" + Left + "," + Top);
                    _suppressClick = true;
                }
                _dragMoved = false;
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_draggingScroll)
            {
                ScrollToThumb(e.Y - _scrollDragOffset);
                return;
            }
            if (_dragging)
            {
                _dragMoved = true;
                Location = new Point(Cursor.Position.X - _dragOffset.X,
                    Cursor.Position.Y - _dragOffset.Y);
                Cursor = Cursors.SizeAll;
                return;
            }

            if (_scrollBarVisible && _scrollBarHit.Contains(e.Location))
            {
                if (_hover != -1)
                {
                    _hover = -1;
                    Invalidate();
                }
                Cursor = Cursors.Hand;
                return;
            }

            int hover = -1;
            if (EmbeddedViewActive)
            {
                if (!_backRect.IsEmpty && _backRect.Contains(e.Location)) hover = 6;
            }
            else if (_refreshRect.Contains(e.Location)) hover = 0;
            else if (_settingsRect.Contains(e.Location)) hover = 1;
            else if (_diagnosticsRect.Contains(e.Location)) hover = 2;
            else if (_historyRect.Contains(e.Location)) hover = 4;
            else if (!_unhideRect.IsEmpty && _unhideRect.Contains(e.Location)) hover = 5;
            else if (!_bluetoothRect.IsEmpty && _bluetoothRect.Contains(e.Location)) hover = 3;
            else if (CardContains(e.Location)) hover = 10;

            if (hover != _hover)
            {
                _hover = hover;
                if (hover >= 0) Cursor = Cursors.Hand;
                else if (e.Y < ContentTop - Px(2)) Cursor = Cursors.SizeAll;
                else Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_scrollBarVisible)
            {
                ScrollBy(-e.Delta / 120 * Px(72));
                HandledMouseEventArgs handled = e as HandledMouseEventArgs;
                if (handled != null) handled.Handled = true;
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_draggingScroll) return;
            if (_hover != -1)
            {
                _hover = -1;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }
            if (_suppressClick)
            {
                _suppressClick = false;
                return;
            }
            if (_scrollBarVisible && _scrollBarHit.Contains(e.Location))
            {
                return;
            }

            // 内嵌视图展开时，面板自己的按钮都不可见，只留「← 返回」可点。
            if (EmbeddedViewActive)
            {
                if (!_backRect.IsEmpty && _backRect.Contains(e.Location))
                {
                    CloseEmbeddedView();
                }
                return;
            }

            if (_refreshRect.Contains(e.Location))
            {
                Raise(RefreshRequested);
                return;
            }
            if (_settingsRect.Contains(e.Location))
            {
                Raise(SettingsRequested);
                return;
            }
            if (_diagnosticsRect.Contains(e.Location))
            {
                Raise(DiagnosticsRequested);
                return;
            }
            if (_historyRect.Contains(e.Location))
            {
                Raise(HistoryRequested);
                return;
            }
            if (!_unhideRect.IsEmpty && _unhideRect.Contains(e.Location))
            {
                Raise(UnhideRequested);
                return;
            }
            if (!_bluetoothRect.IsEmpty && _bluetoothRect.Contains(e.Location))
            {
                if (_cardLinkIsHide && !string.IsNullOrEmpty(_cardLinkDeviceId))
                {
                    Action<string> hide = HideDeviceRequested;
                    if (hide != null)
                    {
                        try { hide(_cardLinkDeviceId); } catch (Exception) { }
                    }
                    return;
                }
                if (_emptyLinkIsUnhide) Raise(UnhideRequested);
                else Raise(OpenBluetoothRequested);
                return;
            }

            if (_snapshot != null && _snapshot.Devices.Count > 0)
            {
                int offset = e.Y - ContentTop + _scrollOffset;
                int index = offset / CardStride;
                int within = offset % CardStride;
                if (index >= 0 && index < _snapshot.Devices.Count && within >= 0 && within < CardHeight)
                {
                    Action<string> handler = DeviceRefreshRequested;
                    if (handler != null)
                    {
                        try { handler(_snapshot.Devices[index].Id); } catch (Exception) { }
                    }
                }
            }
        }

        private bool CardContains(Point point)
        {
            if (_snapshot == null || _snapshot.Devices.Count == 0)
            {
                return false;
            }
            int top = ContentTop;
            if (point.Y < top || point.Y >= top + ViewportHeight)
            {
                return false;
            }
            int offset = point.Y - top + _scrollOffset;
            int index = offset / CardStride;
            int within = offset % CardStride;
            return index >= 0 && index < _snapshot.Devices.Count && within >= 0 && within < CardHeight;
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null)
            {
                try { handler(this, EventArgs.Empty); } catch (Exception ex) { Log.Error("面板事件异常：" + ex.Message); }
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                parameters.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return parameters;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _outsideTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private static GraphicsPath Rounded(Rectangle bounds, int radius)
        {
            return Rounded(new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), radius);
        }

        private static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            float diameter = Math.Max(1f, radius * 2f);
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
            path.CloseFigure();
            return path;
        }
    }
}
