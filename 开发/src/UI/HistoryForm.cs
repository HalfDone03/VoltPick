using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeviceBattery
{
    internal sealed class HistoryForm : Form
    {
        // 设计坐标（96 DPI）。实际像素由 _scale 换算，150%/200% 缩放下整体等比放大，
        // 窗口缩放时再按当前客户区重新摆放，避免底部按钮被裁掉。
        private const int DesignWidth = 680;
        private const int DesignHeight = 486;
        private const int DesignMinWidth = 560;
        private const int DesignMinHeight = 400;

        private readonly ComboBox _devices;
        private readonly ComboBox _range;
        private readonly HistoryChart _chart;
        private readonly Label _deviceLabel;
        private readonly Label _rangeLabel;
        private readonly Label _summary;
        private readonly Label _note;
        private readonly Button _clear;
        private readonly Button _refresh;
        private readonly Button _close;
        private readonly Timer _timer;
        private readonly FontBag _fonts = new FontBag();
        private List<BatteryHistorySeries> _series = new List<BatteryHistorySeries>();
        private string _selectedId;
        private float _scale = 1f;

        public HistoryForm(string initialDeviceId)
        {
            _selectedId = initialDeviceId;
            Text = "VoltPick · 电量历史";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(DesignWidth, DesignHeight);
            MinimumSize = new Size(DesignMinWidth, DesignMinHeight);
            ShowInTaskbar = false;
            // 坐标由 LayoutControls 按 DPI 显式换算，关掉 WinForms 自动缩放以免双重放大。
            AutoScaleMode = AutoScaleMode.None;
            Font = _fonts.Take(Theme.FontBody, FontStyle.Regular);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;

            _deviceLabel = MakeLabel("设备");
            Controls.Add(_deviceLabel);

            _devices = new ComboBox();
            _devices.DropDownStyle = ComboBoxStyle.DropDownList;
            _devices.FlatStyle = FlatStyle.Flat;
            _devices.BackColor = Theme.Input;
            _devices.ForeColor = Theme.Text;
            _devices.SelectedIndexChanged += delegate { UpdateChart(); };
            Controls.Add(_devices);

            _rangeLabel = MakeLabel("范围");
            Controls.Add(_rangeLabel);

            _range = new ComboBox();
            _range.DropDownStyle = ComboBoxStyle.DropDownList;
            _range.FlatStyle = FlatStyle.Flat;
            _range.BackColor = Theme.Input;
            _range.ForeColor = Theme.Text;
            _range.Items.Add("最近 24 小时");
            _range.Items.Add("最近 7 天");
            _range.SelectedIndex = 0;
            _range.SelectedIndexChanged += delegate { UpdateChart(); };
            Controls.Add(_range);

            _refresh = MakeButton("刷新");
            _refresh.Click += delegate { RefreshData(); };
            Controls.Add(_refresh);

            _clear = MakeButton("清空");
            _clear.Click += delegate { ClearHistory(); };
            Controls.Add(_clear);

            _chart = new HistoryChart();
            Controls.Add(_chart);

            _summary = new Label();
            _summary.ForeColor = Theme.Text;
            _summary.Font = _fonts.Take(Theme.FontTitle, FontStyle.Bold);
            _summary.Text = "";
            Controls.Add(_summary);

            _note = new Label();
            _note.ForeColor = Theme.Faint;
            _note.Font = _fonts.Take(Theme.FontSmall, FontStyle.Regular);
            _note.Text = "";
            Controls.Add(_note);

            _close = MakeButton("关闭");
            SurfaceButton closeAccent = _close as SurfaceButton;
            if (closeAccent != null)
            {
                closeAccent.Accent = true;
            }
            _close.Click += delegate { Close(); };
            Controls.Add(_close);
            AcceptButton = _close;
            CancelButton = _close;

            _timer = new Timer();
            _timer.Interval = 30000;
            _timer.Tick += delegate
            {
                if (Visible && !_devices.DroppedDown)
                {
                    RefreshData();
                }
            };

            Load += delegate
            {
                ApplyDpiLayout();
                RefreshData();
                _timer.Start();
            };
            FormClosed += delegate
            {
                Theme.Changed -= OnThemeChanged;
                _timer.Stop();
                _timer.Dispose();
            };
            Theme.Changed += OnThemeChanged;
            HandleCreated += delegate { ApplyWindowTheme(); };
        }

        private int Px(int value)
        {
            return UiScale.Scale(value, _scale);
        }

        /// <summary>按当前 DPI 计算窗口尺寸与最小尺寸；之后每次 Resize 只重排子控件。</summary>
        internal void ApplyDpiLayout()
        {
            ApplyDpiLayout(UiScale.FitFactor(this, new Size(DesignWidth, DesignHeight)));
        }

        /// <summary>按指定缩放系数排版；重复调用结果一致（每次都从设计坐标算）。</summary>
        internal void ApplyDpiLayout(float factor)
        {
            if (factor < 1f)
            {
                factor = 1f;
            }
            _scale = factor;
            SuspendLayout();
            try
            {
                MinimumSize = new Size(Px(DesignMinWidth), Px(DesignMinHeight));
                ClientSize = new Size(Px(DesignWidth), Px(DesignHeight));
            }
            finally
            {
                ResumeLayout(true);
            }
            LayoutControls();
        }

        /// <summary>
        /// 摆控件。所有位置都从当前客户区算出来，所以窗口拉大拉小（以及不同 DPI）都不会
        /// 让“关闭”按钮或说明文字跑到窗口外面。
        /// </summary>
        private void LayoutControls()
        {
            if (_chart == null || _devices == null)
            {
                return;
            }
            SuspendLayout();
            try
            {
                int rowTop = Px(12);
                int rowHeight = Px(28);

                _deviceLabel.Location = new Point(Px(16), Px(16));
                _deviceLabel.Size = new Size(Px(42), Px(20));

                // 右侧按钮从窗口右边缘往里排：窗口能被拉到最小宽度 560，
                // 原来把「刷新/清空」钉死在 526/596，缩窄后就被裁到窗口外面点不到了。
                int rightEdge = ClientSize.Width - Px(14);
                int clearWidth = Px(62);
                int refreshWidth = Px(62);
                int clearLeft = rightEdge - clearWidth;
                int refreshLeft = clearLeft - Px(6) - refreshWidth;

                int rangeWidth = Px(142);
                int rangeLeft = refreshLeft - Px(8) - rangeWidth;
                int rangeLabelLeft = rangeLeft - Px(6) - Px(42);
                int devicesLeft = Px(58);
                int devicesWidth = Math.Max(Px(90), rangeLabelLeft - Px(12) - devicesLeft);

                _devices.Location = new Point(devicesLeft, rowTop);
                _devices.Size = new Size(devicesWidth, rowHeight);

                _rangeLabel.Location = new Point(rangeLabelLeft, Px(16));
                _rangeLabel.Size = new Size(Px(42), Px(20));
                _range.Location = new Point(rangeLeft, rowTop);
                _range.Size = new Size(rangeWidth, rowHeight);

                _refresh.Location = new Point(refreshLeft, rowTop);
                _refresh.Size = new Size(refreshWidth, rowHeight);
                _clear.Location = new Point(clearLeft, rowTop);
                _clear.Size = new Size(clearWidth, rowHeight);

                int chartTop = Px(50);
                int chartWidth = Math.Max(Px(200), ClientSize.Width - Px(28));
                int chartHeight = Math.Max(Px(120), ClientSize.Height - chartTop - Px(106));
                _chart.Location = new Point(Px(16), chartTop);
                _chart.Size = new Size(chartWidth, chartHeight);

                _summary.Location = new Point(Px(16), ClientSize.Height - Px(96));
                _summary.Size = new Size(Math.Max(Px(120), ClientSize.Width - Px(32)), Px(24));
                _note.Location = new Point(Px(16), ClientSize.Height - Px(68));
                _note.Size = new Size(Math.Max(Px(120), ClientSize.Width - Px(32)), Px(22));

                _close.Location = new Point(ClientSize.Width - Px(22) - Px(64),
                    ClientSize.Height - Px(38));
                _close.Size = new Size(Px(64), Px(28));
            }
            finally
            {
                ResumeLayout(true);
                _chart.Invalidate();
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutControls();
            Invalidate();
        }

        /// <summary>
        /// 顶部工具条和底部说明各压一张卡片：面板是圆角卡片堆叠，
        /// 二级页面只在纯色背景上摆控件，两者放在一起会像两套软件。
        /// </summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            Rectangle toolbar = new Rectangle(Px(8), Px(6),
                Math.Max(0, ClientSize.Width - Px(16)), Px(40));
            if (toolbar.Width > 0 && toolbar.Height > 0)
            {
                Surface.Card(g, this, toolbar);
            }
            Rectangle footer = new Rectangle(Px(8), ClientSize.Height - Px(102),
                Math.Max(0, ClientSize.Width - Px(16)), Px(68));
            if (footer.Width > 0 && footer.Height > 0)
            {
                Surface.Card(g, this, footer);
            }
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            ApplyDpiLayout();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _fonts.Dispose();
            }
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            if (IsDisposed || Disposing)
            {
                return;
            }
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            _devices.BackColor = Theme.Input;
            _devices.ForeColor = Theme.Text;
            _range.BackColor = Theme.Input;
            _range.ForeColor = Theme.Text;
            _summary.ForeColor = Theme.Text;
            _note.ForeColor = Theme.Faint;
            _chart.BackColor = Theme.Background;
            _chart.ForeColor = Theme.Text;
            for (int i = 0; i < Controls.Count; i++)
            {
                Button button = Controls[i] as Button;
                if (button == null)
                {
                    continue;
                }
                button.BackColor = Theme.Button;
                button.ForeColor = Theme.Text;
                button.FlatAppearance.BorderColor = Theme.Border;
            }
            ApplyWindowTheme();
            _chart.Invalidate();
            Invalidate(true);
        }

        private Label MakeLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.ForeColor = Theme.Muted;
            label.BackColor = Color.Transparent;
            return label;
        }

        private Button MakeButton(string text)
        {
            // 用面板同款圆角按钮，二级页面和一级页面看起来才是一套软件。
            SurfaceButton button = new SurfaceButton();
            button.Text = text;
            button.Cursor = Cursors.Hand;
            return button;
        }

        private void ApplyWindowTheme()
        {
            if (!IsHandleCreated)
            {
                return;
            }
            int dark = Theme.IsDark ? 1 : 0;
            try { DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); } catch (Exception) { }
            try { DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int)); } catch (Exception) { }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private void RefreshData()
        {
            string selected = _selectedId;
            if (_devices.SelectedItem != null)
            {
                HistoryChoice current = _devices.SelectedItem as HistoryChoice;
                if (current != null && !string.IsNullOrEmpty(current.Series.Id))
                {
                    selected = current.Series.Id;
                }
            }

            _series = BatteryHistory.GetSeries();
            _devices.BeginUpdate();
            _devices.Items.Clear();
            for (int i = 0; i < _series.Count; i++)
            {
                BatteryHistorySeries series = _series[i];
                string text = series.Name;
                if (!string.IsNullOrEmpty(series.Category))
                {
                    text += " · " + series.Category;
                }
                _devices.Items.Add(new HistoryChoice(text, series));
            }
            _devices.EndUpdate();

            if (_devices.Items.Count == 0)
            {
                _devices.Enabled = false;
                _chart.Series = null;
                _summary.Text = "还没有历史数据";
                _note.Text = BatteryHistory.Enabled
                    ? "连接设备并成功读取一次电量后，这里会自动开始记录。"
                    : "历史记录已关闭；把 settings.json 里的 historyEnabled 改成 true 并重启即可开启。";
                _clear.Enabled = false;
                _chart.Invalidate();
                return;
            }

            _devices.Enabled = true;
            _clear.Enabled = true;
            int index = 0;
            if (!string.IsNullOrEmpty(selected))
            {
                for (int i = 0; i < _devices.Items.Count; i++)
                {
                    HistoryChoice choice = _devices.Items[i] as HistoryChoice;
                    if (choice != null && string.Equals(choice.Series.Id, selected, StringComparison.Ordinal))
                    {
                        index = i;
                        break;
                    }
                }
            }
            _devices.SelectedIndex = index;
            UpdateChart();
        }

        private void UpdateChart()
        {
            HistoryChoice choice = _devices.SelectedItem as HistoryChoice;
            if (choice == null)
            {
                _chart.Series = null;
                _summary.Text = "还没有历史数据";
                _chart.Invalidate();
                return;
            }
            DateTime from = DateTime.Now.AddHours(_range.SelectedIndex == 0 ? -24 : -24 * 7);
            DateTime to = DateTime.Now;
            _selectedId = choice.Series.Id;
            _chart.From = from;
            _chart.To = to;
            _chart.Series = choice.Series;
            _chart.Invalidate();

            List<BatteryHistorySample> samples = FilteredSamples(choice.Series, from, to);
            if (samples.Count == 0)
            {
                _summary.Text = "所选范围内没有采样";
                _note.Text = "历史只保留最近 7 天；关闭“记录电量历史”后不会再新增采样。";
                return;
            }

            int min = 100;
            int max = 0;
            long sum = 0;
            int jumps = 0;
            DateTime lastCharge = DateTime.MinValue;
            for (int i = 0; i < samples.Count; i++)
            {
                BatteryHistorySample sample = samples[i];
                if (sample.Percent < min) min = sample.Percent;
                if (sample.Percent > max) max = sample.Percent;
                sum += sample.Percent;
                if (sample.Charging == 1)
                {
                    DateTime time = BatteryHistory.FromUnixSeconds(sample.Time);
                    if (time > lastCharge) lastCharge = time;
                }
                if (i > 0 && HistoryChart.IsReadingJump(samples[i - 1], sample))
                {
                    jumps++;
                }
            }
            int average = (int)Math.Round((double)sum / samples.Count);
            string chargeText = lastCharge == DateTime.MinValue
                ? "范围内未见充电"
                : "最近充电 " + BatteryHistory.FormatTime(lastCharge);
            _summary.Text = string.Format(CultureInfo.CurrentCulture,
                "最低 {0}% · 最高 {1}% · 平均 {2}% · {3} 个采样{4}", min, max, average, samples.Count,
                jumps > 0 ? " · " + jumps + " 处读数跳变" : "");
            _note.Text = chargeText + " · " + BatteryHistory.FilePath;
        }

        private static List<BatteryHistorySample> FilteredSamples(BatteryHistorySeries series, DateTime from, DateTime to)
        {
            List<BatteryHistorySample> result = new List<BatteryHistorySample>();
            if (series == null || series.Samples == null)
            {
                return result;
            }
            long start = ToUnixSeconds(from);
            long end = ToUnixSeconds(to);
            for (int i = 0; i < series.Samples.Count; i++)
            {
                BatteryHistorySample sample = series.Samples[i];
                if (sample != null && sample.Time >= start && sample.Time <= end)
                {
                    result.Add(sample);
                }
            }
            return result;
        }

        private static long ToUnixSeconds(DateTime value)
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            return (long)(utc - epoch).TotalSeconds;
        }

        internal static string BuildClearConfirmation(int devices, int samples)
        {
            return string.Format(CultureInfo.CurrentCulture,
                "清空 {0} 台设备的 {1} 条电量历史？\n\n" +
                "会同时删除 battery-history.json 及其 .bak、.tmp 备份；" +
                "设备记录、重命名和低电提醒状态不受影响。此操作不可撤销。",
                devices, samples);
        }

        private void ClearHistory()
        {
            int samples = BatteryHistory.CountSamples();
            if (samples == 0)
            {
                MessageBox.Show(this, "目前没有可清空的电量历史。", "VoltPick",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int devices = 0;
            List<BatteryHistorySeries> all = BatteryHistory.GetSeries();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null && all[i].Samples != null && all[i].Samples.Count > 0)
                {
                    devices++;
                }
            }
            string message = BuildClearConfirmation(devices, samples);
            if (MessageBox.Show(this, message, "清空电量历史",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }
            BatteryHistory.Clear();
            _selectedId = null;
            RefreshData();
        }

        private sealed class HistoryChoice
        {
            private readonly string _text;
            public readonly BatteryHistorySeries Series;

            public HistoryChoice(string text, BatteryHistorySeries series)
            {
                _text = text;
                Series = series;
            }

            public override string ToString()
            {
                return _text;
            }
        }
    }

    internal sealed class HistoryChart : Control
    {
        /// <summary>
        /// 相邻两次采样之间变化 ≥ 12 个百分点、且间隔 ≤ 5 分钟，认为不是真实充放电曲线，
        /// 而是设备上报在跳变（例如 ATK 鼠标在 40%/55% 之间来回跳）。这种情况曲线要断开，
        /// 不能画成一条假的陡坡。
        /// </summary>
        internal const int JumpPercentThreshold = 12;
        internal const int JumpMaxSeconds = 300;

        private BatteryHistorySeries _series;
        private DateTime _from = DateTime.Now.AddHours(-24);
        private DateTime _to = DateTime.Now;
        private int _hoverIndex = -1;
        private DateTime _hoverTime = DateTime.MinValue;
        private int _hoverPercent;
        private bool _hoverCharging;
        private int _hoverJump;

        public HistoryChart()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
        }

        private int Px(int value)
        {
            return UiScale.Px(this, value);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            RefreshHoverAt(e.Location);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverIndex != -1)
            {
                _hoverIndex = -1;
                _hoverJump = 0;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        /// <summary>按鼠标位置更新高亮；命中返回 true。</summary>
        private bool RefreshHoverAt(Point location)
        {
            int index;
            DateTime time;
            int percent;
            bool charging;
            if (!TryHitTest(location, out index, out time, out percent, out charging))
            {
                if (_hoverIndex != -1)
                {
                    _hoverIndex = -1;
                    _hoverJump = 0;
                    Cursor = Cursors.Default;
                    Invalidate();
                }
                return false;
            }
            Cursor = Cursors.Cross;
            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                _hoverTime = time;
                _hoverPercent = percent;
                _hoverCharging = charging;
                _hoverJump = JumpDelta(index);
                Invalidate();
            }
            return true;
        }

        /// <summary>
        /// 数据或时间范围变了，旧的高亮坐标就失效了。鼠标还停在图上时立刻按当前位置重新命中，
        /// 否则提示框会一直显示上一份数据里的数值。
        /// </summary>
        private void ResetHover()
        {
            _hoverIndex = -1;
            _hoverJump = 0;
            if (!IsHandleCreated || IsDisposed)
            {
                return;
            }
            try
            {
                Point cursor = PointToClient(Cursor.Position);
                if (ClientRectangle.Contains(cursor))
                {
                    RefreshHoverAt(cursor);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>该采样相对前一个采样是否处在一次读数跳变里；返回差值（带正负）。</summary>
        private int JumpDelta(int index)
        {
            if (_series == null || _series.Samples == null)
            {
                return 0;
            }
            if (index <= 0 || index >= _series.Samples.Count)
            {
                return 0;
            }
            BatteryHistorySample previous = _series.Samples[index - 1];
            BatteryHistorySample current = _series.Samples[index];
            if (!IsReadingJump(previous, current))
            {
                return 0;
            }
            return current.Percent - previous.Percent;
        }

        /// <summary>读数跳变判定：值变化大、时间间隔短，超出真实充放电的物理可能。</summary>
        internal static bool IsReadingJump(BatteryHistorySample previous, BatteryHistorySample current)
        {
            if (previous == null || current == null)
            {
                return false;
            }
            int delta = current.Percent - previous.Percent;
            if (delta < 0)
            {
                delta = -delta;
            }
            long seconds = current.Time - previous.Time;
            return delta >= JumpPercentThreshold && seconds >= 0 && seconds <= JumpMaxSeconds;
        }

        /// <summary>
        /// 把鼠标位置映射到最近的采样点。只在绘图区内生效，且忽略与当前时间范围不符的采样。
        /// 采样按时间升序排列，先用二分找到可见区间的起点，鼠标移动时不必扫全表。
        /// </summary>
        internal bool TryHitTest(Point location, out int index, out DateTime time,
            out int percent, out bool charging)
        {
            index = -1;
            time = DateTime.MinValue;
            percent = 0;
            charging = false;
            if (_series == null || _series.Samples == null || _series.Samples.Count == 0)
            {
                return false;
            }
            Rectangle plot = PlotRectangle();
            if (plot.Width < 40 || plot.Height < 40 || !plot.Contains(location))
            {
                return false;
            }

            long start = ToUnixSeconds(_from);
            long end = ToUnixSeconds(_to);
            if (end <= start)
            {
                return false;
            }

            List<BatteryHistorySample> samples = _series.Samples;
            int low = 0;
            int high = samples.Count - 1;
            int first = samples.Count;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                BatteryHistorySample probe = samples[middle];
                if (probe == null || probe.Time < start)
                {
                    low = middle + 1;
                }
                else
                {
                    first = middle;
                    high = middle - 1;
                }
            }

            int best = -1;
            double bestDistance = double.MaxValue;
            for (int i = first; i < samples.Count; i++)
            {
                BatteryHistorySample sample = samples[i];
                if (sample == null)
                {
                    continue;
                }
                if (sample.Time > end)
                {
                    break;
                }
                double x = plot.Left + (double)(sample.Time - start) / (end - start) * plot.Width;
                double distance = Math.Abs(x - location.X);
                // 读数跳变会让两个采样落在几乎同一个横坐标上。距离打平时取更晚的那个，
                // 这样提示框读到的是“最新一次读数”，不会在 40/55 之间来回闪。
                if (distance < bestDistance - 0.5 ||
                    (Math.Abs(distance - bestDistance) <= 0.5 && best >= 0))
                {
                    bestDistance = distance;
                    best = i;
                }
            }
            if (best < 0)
            {
                return false;
            }

            BatteryHistorySample hit = samples[best];
            index = best;
            time = BatteryHistory.FromUnixSeconds(hit.Time);
            percent = Clamp(hit.Percent);
            charging = hit.Charging == 1;
            return true;
        }

        private static int Clamp(int percent)
        {
            if (percent < 0) return 0;
            if (percent > 100) return 100;
            return percent;
        }

        private Rectangle PlotRectangle()
        {
            return new Rectangle(Px(48), Px(16), Math.Max(1, Width - Px(66)), Math.Max(1, Height - Px(52)));
        }

        public BatteryHistorySeries Series
        {
            get { return _series; }
            set
            {
                _series = value;
                ResetHover();
            }
        }

        public DateTime From
        {
            get { return _from; }
            set
            {
                _from = value;
                ResetHover();
            }
        }

        public DateTime To
        {
            get { return _to; }
            set
            {
                _to = value;
                ResetHover();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Rectangle plot = PlotRectangle();
            if (plot.Width < 40 || plot.Height < 40)
            {
                return;
            }

            using (SolidBrush background = new SolidBrush(Theme.List))
            using (Pen border = new Pen(Theme.Divider, 1f))
            using (GraphicsPath plotPath = Surface.Rounded(this, plot, Surface.CardRadius))
            {
                g.FillPath(background, plotPath);
                g.DrawPath(border, plotPath);
            }

            Font labelFont = Theme.PaintFont(Theme.FontSmall, FontStyle.Regular);
            using (Pen grid = new Pen(Theme.Divider, 1f))
            {
                for (int i = 0; i <= 4; i++)
                {
                    int value = 100 - i * 25;
                    int y = plot.Top + (int)Math.Round((double)plot.Height * i / 4);
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    string text = value.ToString(CultureInfo.InvariantCulture) + "%";
                    SizeF size = g.MeasureString(text, labelFont);
                    using (SolidBrush brush = new SolidBrush(Theme.Faint))
                    {
                        g.DrawString(text, labelFont, brush, plot.Left - size.Width - 6, y - size.Height / 2);
                    }
                }
            }

            if (_series == null)
            {
                // 窗口左上角已经写着「还没有历史数据」和怎么才会有数据，
                // 图里再叠一句「暂无采样」是重复信息，只看网格就行。
                return;
            }

            if (_series.Samples == null || _series.Samples.Count == 0)
            {
                DrawCentered(g, plot, "暂无采样");
                return;
            }

            long start = ToUnixSeconds(_from);
            long end = ToUnixSeconds(_to);
            if (end <= start) end = start + 1;

            List<BatteryHistorySample> visible = new List<BatteryHistorySample>();
            for (int i = 0; i < _series.Samples.Count; i++)
            {
                BatteryHistorySample sample = _series.Samples[i];
                if (sample == null || sample.Time < start || sample.Time > end)
                {
                    continue;
                }
                visible.Add(sample);
            }
            if (visible.Count == 0)
            {
                DrawCentered(g, plot, "范围内暂无采样");
                return;
            }

            Color accent = Theme.AccentColor(Clamp(visible[visible.Count - 1].Percent));
            List<List<PointF>> segments;
            List<PointF[]> jumpEdges;
            BuildGeometry(visible, plot, start, end, out segments, out jumpEdges);
            for (int i = 0; i < segments.Count; i++)
            {
                DrawSegment(g, plot, segments[i], accent);
            }

            // 跳变处画虚线 + 两端空心圆：明确表示“这里数值不连续”，同时保留两个真实读数。
            for (int i = 0; i < jumpEdges.Count; i++)
            {
                DrawJumpEdge(g, jumpEdges[i][0], jumpEdges[i][1], accent);
            }

            using (SolidBrush brush = new SolidBrush(Theme.Faint))
            {
                string left = _rangeIsWeek()
                    ? _from.ToString("MM-dd", CultureInfo.CurrentCulture)
                    : _from.ToString("HH:mm", CultureInfo.CurrentCulture);
                string right = _rangeIsWeek()
                    ? _to.ToString("MM-dd", CultureInfo.CurrentCulture)
                    : _to.ToString("HH:mm", CultureInfo.CurrentCulture);
                g.DrawString(left, labelFont, brush, plot.Left, plot.Bottom + 6);
                SizeF rightSize = g.MeasureString(right, labelFont);
                g.DrawString(right, labelFont, brush, plot.Right - rightSize.Width, plot.Bottom + 6);
                string middle = _rangeIsWeek()
                    ? _from.AddDays(3.5).ToString("MM-dd", CultureInfo.CurrentCulture)
                    : _from.AddHours(12).ToString("HH:mm", CultureInfo.CurrentCulture);
                SizeF middleSize = g.MeasureString(middle, labelFont);
                g.DrawString(middle, labelFont, brush, plot.Left + plot.Width / 2 - middleSize.Width / 2,
                    plot.Bottom + 6);
            }

            DrawHover(g, plot, start, end);
        }

        /// <summary>
        /// 把可见采样切成若干折线段：时间断档（超过 2 小时没采样）和读数跳变（几秒内跳 12% 以上）
        /// 都要断开，不能连成一条假曲线。跳变处额外给出“上一段末点 → 新段首点”的连线，用于画虚线提示。
        /// </summary>
        internal static void BuildGeometry(List<BatteryHistorySample> visible, Rectangle plot,
            long start, long end, out List<List<PointF>> segments, out List<PointF[]> jumpEdges)
        {
            segments = new List<List<PointF>>();
            jumpEdges = new List<PointF[]>();
            if (visible == null || visible.Count == 0 || end <= start)
            {
                return;
            }
            List<PointF> segment = new List<PointF>();
            BatteryHistorySample previous = null;
            PointF lastPoint = PointF.Empty;
            for (int i = 0; i < visible.Count; i++)
            {
                BatteryHistorySample sample = visible[i];
                float x = plot.Left + (float)((double)(sample.Time - start) / (end - start) * plot.Width);
                float y = plot.Bottom - (float)Clamp(sample.Percent) / 100f * plot.Height;
                PointF point = new PointF(x, y);
                if (previous != null)
                {
                    bool jump = IsReadingJump(previous, sample);
                    bool gap = BatteryHistory.IsGap(previous, sample);
                    if (jump || gap)
                    {
                        if (segment.Count > 0)
                        {
                            segments.Add(segment);
                        }
                        segment = new List<PointF>();
                        if (jump)
                        {
                            jumpEdges.Add(new PointF[] { lastPoint, point });
                        }
                    }
                }
                segment.Add(point);
                lastPoint = point;
                previous = sample;
            }
            if (segment.Count > 0)
            {
                segments.Add(segment);
            }
        }

        private void DrawJumpEdge(Graphics g, PointF from, PointF to, Color accent)        {
            using (Pen dashed = new Pen(Color.FromArgb(130, Theme.Faint), Math.Max(1f, Px(1))))
            {
                dashed.DashStyle = DashStyle.Dash;
                g.DrawLine(dashed, from, to);
            }
            using (SolidBrush hole = new SolidBrush(Theme.List))
            using (Pen ring = new Pen(Color.FromArgb(200, accent), Math.Max(1f, Px(1))))
            {
                float radius = Math.Max(2.5f, Px(3));
                g.FillEllipse(hole, from.X - radius, from.Y - radius, radius * 2, radius * 2);
                g.DrawEllipse(ring, from.X - radius, from.Y - radius, radius * 2, radius * 2);
                g.FillEllipse(hole, to.X - radius, to.Y - radius, radius * 2, radius * 2);
                g.DrawEllipse(ring, to.X - radius, to.Y - radius, radius * 2, radius * 2);
            }
        }

        private void DrawHover(Graphics g, Rectangle plot, long start, long end)
        {
            if (_hoverIndex < 0 || end <= start)
            {
                return;
            }
            float x = plot.Left + (float)((double)(ToUnixSeconds(_hoverTime) - start) /
                (end - start) * plot.Width);
            if (x < plot.Left || x > plot.Right)
            {
                return;
            }
            float y = plot.Bottom - (float)_hoverPercent / 100f * plot.Height;

            using (Pen guide = new Pen(Color.FromArgb(110, Theme.Muted), 1f))
            {
                guide.DashStyle = DashStyle.Dot;
                g.DrawLine(guide, x, plot.Top, x, plot.Bottom);
            }

            Color accent = Theme.AccentColor(_hoverPercent);
            using (SolidBrush dot = new SolidBrush(accent))
            {
                g.FillEllipse(dot, x - 3.5f, y - 3.5f, 7f, 7f);
            }
            using (Pen ring = new Pen(Color.FromArgb(180, Theme.Background), 1.5f))
            {
                g.DrawEllipse(ring, x - 3.5f, y - 3.5f, 7f, 7f);
            }

            // 打卡到秒：40/55 这种跳变往往在几秒内发生，只显示“时:分”两个读数会看起来一样。
            string timeText = _rangeIsWeek()
                ? _hoverTime.ToString("MM-dd HH:mm", CultureInfo.CurrentCulture)
                : _hoverTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            string text = timeText + "   " + _hoverPercent + "%" +
                          (_hoverCharging ? "  充电中" : "") +
                          (_hoverJump > 0 ? "   ↑ 读数跳变" : (_hoverJump < 0 ? "   ↓ 读数跳变" : ""));

            Font font = Theme.PaintFont(Theme.FontSmall, FontStyle.Regular);
            SizeF size = g.MeasureString(text, font);
            float boxWidth = size.Width + 14f;
            float boxHeight = size.Height + 8f;
            float boxLeft = x + 12f;
            if (boxLeft + boxWidth > plot.Right)
            {
                boxLeft = x - 12f - boxWidth;
            }
            if (boxLeft < plot.Left)
            {
                boxLeft = plot.Left + 2f;
            }
            float boxTop = y - boxHeight - 10f;
            if (boxTop < plot.Top)
            {
                boxTop = y + 12f;
            }
            if (boxTop + boxHeight > plot.Bottom)
            {
                boxTop = plot.Bottom - boxHeight - 2f;
            }
            RectangleF box = new RectangleF(boxLeft, boxTop, boxWidth, boxHeight);

            using (GraphicsPath path = new GraphicsPath())
            {
                float radius = 4f;
                path.AddArc(box.Left, box.Top, radius * 2, radius * 2, 180f, 90f);
                path.AddArc(box.Right - radius * 2, box.Top, radius * 2, radius * 2, 270f, 90f);
                path.AddArc(box.Right - radius * 2, box.Bottom - radius * 2, radius * 2, radius * 2, 0f, 90f);
                path.AddArc(box.Left, box.Bottom - radius * 2, radius * 2, radius * 2, 90f, 90f);
                path.CloseFigure();
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(245, Theme.Input)))
                {
                    g.FillPath(fill, path);
                }
                using (Pen border = new Pen(Theme.Border, 1f))
                {
                    g.DrawPath(border, path);
                }
            }
            using (SolidBrush brush = new SolidBrush(Theme.Text))
            {
                g.DrawString(text, font, brush, box.Left + 7f, box.Top + 4f);
            }
        }

        private bool _rangeIsWeek()
        {
            return (_to - _from).TotalDays > 2;
        }

        private static void DrawCentered(Graphics g, Rectangle plot, string text)
        {
            Font font = Theme.PaintFont(Theme.FontSmall, FontStyle.Regular);
            using (SolidBrush brush = new SolidBrush(Theme.Faint))
            {
                SizeF size = g.MeasureString(text, font);
                g.DrawString(text, font, brush, plot.Left + (plot.Width - size.Width) / 2,
                    plot.Top + (plot.Height - size.Height) / 2);
            }
        }

        private static void DrawSegment(Graphics g, Rectangle plot, List<PointF> points, Color accent)
        {
            if (points == null || points.Count == 0)
            {
                return;
            }
            if (points.Count == 1)
            {
                using (SolidBrush dot = new SolidBrush(accent))
                {
                    g.FillEllipse(dot, points[0].X - 2.5f, points[0].Y - 2.5f, 5f, 5f);
                }
                return;
            }

            using (GraphicsPath fill = new GraphicsPath())
            {
                fill.AddLines(points.ToArray());
                fill.AddLine(points[points.Count - 1].X, plot.Bottom, points[0].X, plot.Bottom);
                fill.CloseFigure();
                using (LinearGradientBrush brush = new LinearGradientBrush(
                    new Rectangle(plot.Left, plot.Top, Math.Max(1, plot.Width), Math.Max(1, plot.Height)),
                    Color.FromArgb(70, accent), Color.FromArgb(4, accent), LinearGradientMode.Vertical))
                {
                    g.FillPath(brush, fill);
                }
            }
            using (Pen line = new Pen(accent, 2f))
            {
                line.LineJoin = LineJoin.Round;
                g.DrawLines(line, points.ToArray());
            }
            using (SolidBrush dot = new SolidBrush(accent))
            {
                g.FillEllipse(dot, points[points.Count - 1].X - 2.5f, points[points.Count - 1].Y - 2.5f, 5f, 5f);
            }
        }

        private static long ToUnixSeconds(DateTime value)
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            return (long)(utc - epoch).TotalSeconds;
        }
    }
}
