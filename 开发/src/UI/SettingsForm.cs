using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeviceBattery
{
    /// <summary>
    /// 设置界面（重做版）。
    ///
    /// 设计原则（针对之前几版的失败点重写）：
    /// 1. 容器不自绘圆角、不设 Region：分组就是普通 Panel，背景不透明，
    ///    子控件不会因为“透明背景没有父容器支持”被画成黑块。
    /// 2. 行高交给 WinForms：Label 设 MaximumSize 后开 AutoSize，
    ///    由控件自己按真实绘制逻辑换行，不再手写 TextRenderer 估算高度，
    ///    也就不会再出现文字压行、切掉半行的情况。
    /// 3. 全部控件用系统原生 CheckBox / ComboBox / TextBox / ListView，
    ///    只改颜色不改绘制，主题切换靠颜色，不靠自绘。
    /// </summary>
    internal sealed partial class SettingsForm : Form
    {
        private const int DesignWidth = 600;
        // 高度按最高一页的内容定（设备页 397 + 上下留白），不再固定 620 留一大块空白。
        private const int DesignHeight = 640;

        internal const string AuthorContact = AppInfo.Contact;

        private readonly BatterySnapshot _snapshot;
        private readonly FontBag _fonts = new FontBag();
        // 标签的颜色角色：主题切换时按角色重刷，省得把设置关掉重开才变色。
        private const int RoleText = 0;
        private const int RoleMuted = 1;
        private const int RoleAccent = 2;
        private readonly Dictionary<Control, int> _labelRoles = new Dictionary<Control, int>();
        private readonly List<SettingsSection> _general = new List<SettingsSection>();
        private readonly List<SettingsSection> _display = new List<SettingsSection>();
        private readonly List<SettingsSection> _notify = new List<SettingsSection>();
        private readonly List<SettingsSection> _devices = new List<SettingsSection>();
        private readonly List<SettingsSection> _about = new List<SettingsSection>();

        private DarkTabControl _tabs;
        private Panel _bottom;
        private SurfaceButton _apply;
        private SurfaceButton _ok;
        private SurfaceButton _cancel;

        private CheckBox _autoStart;
        private CheckBox _startWithPanel;
        private CheckBox _history;

        private ComboBox _theme;

        private ComboBox _displayMode;
        private ComboBox _iconDevice;
        private ComboBox _digitSize;
        private ComboBox _iconStyle;
        private ComboBox _iconFont;
        private CheckBox _autoColor;
        private SurfaceButton _colorButton;
        private IconStylePreview _iconPreview;
        private CheckBox _showVoltage;

        private ComboBox _interval;
        private CheckBox _pauseIdle;
        private Label _nextRead;

        private CheckBox _lowWarn;
        private CheckBox _dnd;
        private TextBox _dndStart;
        private TextBox _dndEnd;
        private CheckBox _fullscreen;

        private ListView _deviceList;
        // 设备列表的基准高度：排版时先复原成它再量内容，多出来的页面高度补给列表。
        private int _deviceListBaseHeight;
        private TextBox _deviceName;
        private SurfaceButton _renameButton;
        private ComboBox _deviceThreshold;
        private CheckBox _forceVisible;
        private SurfaceButton _removeButton;

        private Label _versionLabel;
        private Label _authorLabel;
        private Label _githubLabel;
        private Label _contactLabel;
        private SurfaceButton _copyContact;
        private Label _dataPath;
        private SurfaceButton _openFolder;
        private SurfaceButton _reset;

        private Label _status;
        private bool _dirty;
        private bool _loading;
        private bool _layingOut;
        private int _sizedHeight;
        // 用户是否手动拖过窗口；拖过之后不再按页签自动定高。
        private bool _userSized;
        private bool _fitting;
        private string _selectedDeviceId = "";

        public event EventHandler Applied;

        internal bool NeedsImmediateRefresh { get; private set; }

        public SettingsForm(BatterySnapshot snapshot)
        {
            _snapshot = snapshot ?? new BatterySnapshot();
            Text = "VoltPick · 设置";
            // 标题栏左上角那个图标：从 exe 自带的图标资源取，跟资源管理器里看到的一致。
            AppIcon.Apply(this);
            // 设置是独立窗口：有标题栏、可以用鼠标拖边框改大小，面板自己继续留在右下角。
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(DesignWidth, DesignHeight);
            // 上次拖动过的大小存在设置里，下次打开沿用（0 表示还没拖过，仍按内容自适应）。
            if (AppSettings.Current.SettingsWidth > 0 && AppSettings.Current.SettingsHeight > 0)
            {
                ClientSize = new Size(AppSettings.Current.SettingsWidth, AppSettings.Current.SettingsHeight);
                _userSized = true;
            }
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            EnableDoubleBuffer(this);
            Font = _fonts.Take(Theme.FontBody, FontStyle.Regular);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;

            BuildBottomBar();
            BuildTabs();
            BuildGeneralPage();
            BuildDisplayPage();
            BuildNotifyPage();
            BuildDevicePage();
            BuildAboutPage();

            HandleCreated += delegate { ApplyWindowTheme(); };
            // 主题一换就整体重刷：点「应用」后设置窗口自己跟着变色，
            // 以前要按确定关掉再打开才会变。
            Theme.Changed += OnThemeChanged;
            FormClosed += delegate { Theme.Changed -= OnThemeChanged; };
            _tabs.SelectedIndex = 0;
            LoadValues();
            Load += delegate { LayoutPages(); PositionButtons(); };
            Shown += delegate { LayoutPages(); PositionButtons(); };
            // 用户自己拖大/拖小之后就不再按页签内容自动改高度，只重排内容。
            Resize += delegate
            {
                if (_fitting)
                {
                    return;
                }
                _userSized = true;
                LayoutPages();
            };
        }

        /// <summary>关窗时记住用户拖出来的窗口大小。</summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 只有用户真的拖过窗口才记住大小。原来无条件写一遍，
            // 构造里一看到「存过尺寸」就把窗口当成用户定过尺寸，
            // 按页签内容自动定高从第二次打开设置窗口起就永久失效了。
            if (_userSized && WindowState == FormWindowState.Normal &&
                ClientSize.Width > 0 && ClientSize.Height > 0)
            {
                AppSettings.Current.SettingsWidth = ClientSize.Width;
                AppSettings.Current.SettingsHeight = ClientSize.Height;
                AppSettings.Save();
            }
            base.OnFormClosing(e);
        }

        // ---------------- 底部按钮 ----------------

        private void BuildBottomBar()
        {
            _bottom = new Panel();
            _bottom.Dock = DockStyle.Bottom;
            _bottom.Height = UiScale.Px(this, 52);
            _bottom.BackColor = Theme.Background;
            // 底栏和内容之间画一条分隔线：不然按钮像是浮在页面底色上。
            _bottom.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Theme.Divider, 1f))
                {
                    e.Graphics.DrawLine(pen, 0, 0, _bottom.Width, 0);
                }
            };
            Controls.Add(_bottom);

            _status = new Label();
            _status.AutoSize = false;
            _status.BackColor = Theme.Background;
            _status.ForeColor = Theme.Muted;
            _status.Font = _fonts.Take(Theme.FontSmall, FontStyle.Regular);
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Text = "";
            _bottom.Controls.Add(_status);

            _apply = NewButton("应用", false);
            _apply.Click += delegate { ApplySettings(); };
            _bottom.Controls.Add(_apply);

            _ok = NewButton("确定", true);
            _ok.Click += delegate
            {
                ApplySettings();
                Close();
            };
            _bottom.Controls.Add(_ok);

            _cancel = NewButton("取消", false);
            _cancel.Click += delegate { Close(); };
            _bottom.Controls.Add(_cancel);

            _bottom.Resize += delegate { PositionButtons(); };
            // 钩子挂上之前 Panel 已经完成过一次布局，那次 Resize 早就过去了；
            // 不在这里主动摆一次，三个按钮会全部停在 (0, y) 互相压住。
            PositionButtons();
        }

        private SurfaceButton NewButton(string text, bool accent)
        {
            SurfaceButton button = new SurfaceButton();
            button.Text = text;
            button.Accent = accent;
            button.Size = new Size(UiScale.Px(this, 96), UiScale.Px(this, 30));
            return button;
        }

        private void PositionButtons()
        {
            if (_bottom == null || _ok == null)
            {
                return;
            }
            int gap = UiScale.Px(this, 10);
            int right = _bottom.ClientSize.Width - UiScale.Px(this, 16);
            int y = (_bottom.ClientSize.Height - _ok.Height) / 2;
            _ok.Location = new Point(right - _ok.Width, y);
            _apply.Location = new Point(_ok.Left - gap - _apply.Width, y);
            _cancel.Location = new Point(_apply.Left - gap - _cancel.Width, y);
            if (_status != null)
            {
                _status.Height = _ok.Height;
                _status.Location = new Point(UiScale.Px(this, 16), y);
                _status.Width = Math.Max(UiScale.Px(this, 80), _cancel.Left - UiScale.Px(this, 12) - UiScale.Px(this, 16));
            }
        }

        /// <summary>改动只进内存、还没落盘时，左下角提示一句，避免用户以为已经保存。</summary>
        private void MarkDirty()
        {
            _dirty = true;
            if (_status != null)
            {
                _status.Text = "有未应用的改动";
                _status.ForeColor = Theme.Blue;
            }
        }

        private void MarkClean()
        {
            _dirty = false;
            if (_status != null)
            {
                _status.Text = "已保存";
                _status.ForeColor = Theme.Muted;
            }
        }

        // ---------------- 标签页 ----------------

        private void BuildTabs()
        {
            _tabs = new DarkTabControl();
            _tabs.Dock = DockStyle.Fill;
            Controls.Add(_tabs);
            _tabs.BringToFront();
            _tabs.TabPages.Add(NewPage("常规"));
            _tabs.TabPages.Add(NewPage("显示"));
            _tabs.TabPages.Add(NewPage("通知"));
            _tabs.TabPages.Add(NewPage("设备"));
            _tabs.TabPages.Add(NewPage("关于"));

            for (int i = 0; i < _tabs.TabPages.Count; i++)
            {
                _tabs.TabPages[i].ClientSizeChanged += delegate { LayoutPages(); };
                TabPage headerPage = _tabs.TabPages[i];
                headerPage.Paint += delegate(object sender, PaintEventArgs e)
                {
                    PaintPageHeader(e.Graphics, headerPage, UiScale.RawFactor(this));
                };
            }
            // 只在切页时重排 + 重画一次：之前这个事件在循环里被订阅了 7 次，
            // 切一次页要连排 7 遍、窗口高度也跟着改 7 次，画面上就会看到「没渲染完」。
            _tabs.SelectedIndexChanged += OnTabChanged;
            ApplyTabItemSize(UiScale.FitFactor(this, new Size(DesignWidth, DesignHeight)));
        }

        /// <summary>
        /// 页签宽度按最宽的一页文字实测，再留出左右内边距；字号档位或 DPI 变化后要重算。
        /// 固定 76 在中文四字标题下会被裁，所以这里不写死。
        /// </summary>
        private void ApplyTabItemSize(float f)
        {
            if (_tabs == null)
            {
                return;
            }
            int widest = 0;
            for (int i = 0; i < _tabs.TabPages.Count; i++)
            {
                Size measured = TextRenderer.MeasureText(_tabs.TabPages[i].Text, _tabs.Font,
                    new Size(int.MaxValue, 0), TextFormatFlags.NoPadding);
                if (measured.Width > widest)
                {
                    widest = measured.Width;
                }
            }
            int glyph = TextRenderer.MeasureText("国", _tabs.Font, new Size(int.MaxValue, 0),
                TextFormatFlags.NoPadding).Height;
            int width = Math.Max(UiScale.Scale(76, f), widest + UiScale.Scale(26, f));
            int height = Math.Max(UiScale.Scale(34, f), glyph + UiScale.Scale(14, f));
            Size item = new Size(width, height);
            if (_tabs.ItemSize != item)
            {
                _tabs.ItemSize = item;
            }
        }

        /// <summary>切页：先排完再整页重画，避免新页面露出上一页留下的空白。</summary>
        private void OnTabChanged(object sender, EventArgs e)
        {
            if (_tabs == null)
            {
                return;
            }
            TabPage page = _tabs.SelectedTab;
            if (page != null)
            {
                page.SuspendLayout();
            }
            try
            {
                LayoutPages();
            }
            finally
            {
                if (page != null)
                {
                    page.ResumeLayout(true);
                }
            }
            if (page != null)
            {
                page.Invalidate(true);
            }
            _tabs.Invalidate();
            Update();
        }

        /// <summary>给控件开双缓冲（Control.DoubleBuffered 是 protected，这里用反射打开）。</summary>
        private static void EnableDoubleBuffer(Control control)
        {
            if (control == null)
            {
                return;
            }
            PropertyInfo info = typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);
            if (info != null)
            {
                info.SetValue(control, true, null);
            }
        }

        private TabPage NewPage(string text)
        {
            TabPage page = new TabPage(text);
            page.BackColor = Theme.Background;
            page.ForeColor = Theme.Text;
            page.AutoScroll = true;
            EnableDoubleBuffer(page);
            return page;
        }

        private static void AddSections(TabPage page, List<SettingsSection> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                EnableDoubleBuffer(list[i]);
                page.Controls.Add(list[i]);
            }
        }

        // ---------------- 控件工厂 ----------------

        /// <summary>
        /// 输入类控件（下拉框、文本框、复选框）的统一高度：跟着当前字号算，
        /// 字号调大时输入框会一起长高，不会把文字上下切掉。
        /// </summary>
        private int FieldHeight
        {
            get { return _fonts.Take(Theme.FontBody, FontStyle.Regular).Height + UiScale.Px(this, 12); }
        }

        /// <summary>一行里标签的高度：比输入框略矮，居中后和输入框同一条基线。</summary>
        private int LabelHeight
        {
            get { return _fonts.Take(Theme.FontBody, FontStyle.Regular).Height + UiScale.Px(this, 6); }
        }

        private Label NewLabel(string text, int width)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = false;
            label.Width = width;
            label.Height = LabelHeight;
            label.ForeColor = Theme.Text;
            label.BackColor = Theme.Card;
            label.TextAlign = ContentAlignment.MiddleLeft;
            _labelRoles[label] = RoleText;
            return label;
        }

        private Label NewNote(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.MaximumSize = new Size(UiScale.Px(this, 440), 0);
            // 说明文字统一用最小一档（FontSmall）：和字段标签、取值拉开层级。
            label.Font = _fonts.Take(Theme.FontSmall, FontStyle.Regular);
            label.ForeColor = Theme.Muted;
            label.BackColor = Theme.Card;
            _labelRoles[label] = RoleMuted;
            return label;
        }

        /// <summary>路径这类内容用的标签：固定一行，宽度不够用省略号，绝不换行。</summary>
        private Label NewPath()
        {
            Label label = new Label();
            label.AutoSize = false;
            label.AutoEllipsis = true;
            label.Tag = "single";
            // 小一号字：完整路径才放得进一行，不会被省略号截掉尾巴。
            label.Font = _fonts.Take(Theme.FontSmall, FontStyle.Regular);
            label.ForeColor = Theme.Muted;
            label.BackColor = Theme.Card;
            _labelRoles[label] = RoleMuted;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Height = LabelHeight;
            return label;
        }

        /// <summary>可点击的链接标签：单行、下划线、强调色、手型光标。</summary>
        private Label NewLink()
        {
            Label label = NewPath();
            label.Font = _fonts.Take(Theme.FontSmall, FontStyle.Underline);
            label.ForeColor = Theme.Blue;
            _labelRoles[label] = RoleAccent;
            label.Cursor = Cursors.Hand;
            return label;
        }

        /// <summary>用默认浏览器打开链接，失败就静默忽略（没有浏览器时不该弹错误）。</summary>
        private void OpenLink(string url)
        {
            try { System.Diagnostics.Process.Start(url); } catch (Exception) { }
        }

        private CheckBox NewCheck(string text)
        {
            CheckBox box = new CheckBox();
            box.Text = text;
            box.AutoSize = false;
            box.Height = FieldHeight;
            // 原生复选框 + 不透明背景：不走自绘，就不会有透明背景被退化成黑块的问题。
            box.BackColor = Theme.Card;
            box.ForeColor = Theme.Text;
            box.CheckedChanged += delegate { OnFieldChanged(); };
            return box;
        }

        private ComboBox NewCombo(string[] items)
        {
            ComboBox combo = new ComboBox();
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.FlatStyle = FlatStyle.Flat;
            combo.BackColor = Theme.Input;
            combo.ForeColor = Theme.Text;
            combo.Height = FieldHeight;
            combo.IntegralHeight = false;
            combo.MaxDropDownItems = 12;
            combo.Items.AddRange(items);
            combo.SelectedIndexChanged += delegate { OnFieldChanged(); };
            return combo;
        }

        private TextBox NewText(string text)
        {
            TextBox box = new TextBox();
            box.Text = text;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.BackColor = Theme.Input;
            box.ForeColor = Theme.Text;
            box.Height = FieldHeight;
            return box;
        }

        private void OnFieldChanged()
        {
            if (!_loading)
            {
                MarkDirty();
                LayoutPages();
            }
        }

        /// <summary>每页顶部的主标题与一句话说明，让页面有层级而不是直接铺卡片。</summary>
        private static string PageCaption(TabPage page)
        {
            string text = page == null ? "" : page.Text;
            if (text == "常规") return "启动方式、查询节奏与主题";
            if (text == "显示") return "托盘图标与设备面板显示什么";
            if (text == "通知") return "低电量提醒与免打扰时段";
            if (text == "设备") return "已识别的设备与单独设置";
            if (text == "关于") return "版本信息、反馈与支持范围";
            return "";
        }

        /// <summary>页头只留一行说明（设计像素 26）：页签名已经说清这一页叫什么，不再重复画大标题。</summary>
        internal int PageHeaderHeight(float f)
        {
            return UiScale.Scale(26, f);
        }

        /// <summary>在页面顶部画标题与说明；正文从 PageHeaderHeight 之后开始排。</summary>
        private void PaintPageHeader(Graphics g, TabPage page, float f)
        {
            int left = UiScale.Scale(14, f) + UiScale.Scale(16, f);
            int top = UiScale.Scale(10, f);
            Font title = _fonts.Take(Theme.FontTitle, FontStyle.Bold);
            Font caption = _fonts.Take(Theme.FontSmall, FontStyle.Regular);
            int titleH = title.Height;
            TextRenderer.DrawText(g, page.Text, title,
                new Rectangle(left, top, Math.Max(0, page.ClientSize.Width - left * 2), titleH),
                Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, PageCaption(page), caption,
                new Rectangle(left, top + titleH + UiScale.Scale(2, f),
                    Math.Max(0, page.ClientSize.Width - left * 2), caption.Height + UiScale.Scale(2, f)),
                Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
