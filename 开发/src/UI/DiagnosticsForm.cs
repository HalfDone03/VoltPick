using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DeviceBattery
{
    internal sealed class DiagnosticsForm : Form
    {
        private const int DesignWidth = 660;
        private const int DesignHeight = 500;
        private const int DesignMinWidth = 520;
        private const int DesignMinHeight = 360;

        private readonly BatterySnapshot _snapshot;
        // 正文不再是只读文本框，而是一块自绘面板：分节标题 + 左标签 + 右值。
        // 复制/导出仍走 BuildText()，保证贴给开发者的是纯文本。
        private Panel _content;
        private ScrollHost _scrollView;
        private readonly FontBag _fonts = new FontBag();
        private readonly LayoutScale _layout = new LayoutScale();
        private Panel _bottomBar;
        private Button _closeButton;

        public DiagnosticsForm(BatterySnapshot snapshot)
        {
            _snapshot = snapshot ?? new BatterySnapshot();
            Text = "VoltPick · 诊断信息";
            AppIcon.Apply(this);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(DesignWidth, DesignHeight);
            MinimumSize = new Size(DesignMinWidth, DesignMinHeight);
            ShowInTaskbar = false;
            // 布局全部是 96 DPI 设计坐标，由 UiScale 统一换算成当前 DPI 的像素；
            // 关掉 WinForms 自己的缩放，避免和显式缩放叠加成两倍。
            AutoScaleMode = AutoScaleMode.None;
            Font = _fonts.Take(Theme.FontBody, FontStyle.Regular);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;

            // 正文放进一张卡片里，和面板的卡片语言保持一致：
            // 外面留 16px 边距，顶部一条标题 + 说明，正文用比卡片略深的底色。
            Panel body = new Panel();
            body.Dock = DockStyle.Fill;
            body.BackColor = Theme.Background;
            body.Padding = new Padding(UiScale.Px(this, 16), UiScale.Px(this, 52),
                UiScale.Px(this, 16), UiScale.Px(this, 10));
            body.Paint += delegate(object sender, PaintEventArgs e) { PaintBody(e.Graphics, body); };
            Controls.Add(body);

            _content = new Panel();
            _content.Dock = DockStyle.None;
            _content.BackColor = Theme.List;
            _content.Paint += delegate(object sender, PaintEventArgs e) { RenderContent(e.Graphics, _content.ClientSize.Width); };
            _scrollView = new ScrollHost();
            _scrollView.Dock = DockStyle.Fill;
            _scrollView.Attach(_content);
            _content.MouseWheel += delegate(object sender, MouseEventArgs e) { _scrollView.ScrollByWheel(e.Delta); };
            body.Controls.Add(_scrollView);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 46;
            bottom.BackColor = Theme.Background;
            Controls.Add(bottom);

            SurfaceButton copy = new SurfaceButton();
            copy.Text = "复制诊断文本";
            copy.Location = new Point(16, 9);
            copy.Size = new Size(120, 28);
            copy.Click += delegate
            {
                try
                {
                    Clipboard.SetText(BuildText());
                    MessageBox.Show(this, "诊断信息已复制到剪贴板。", "VoltPick",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "复制失败：" + ex.Message, "VoltPick",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            bottom.Controls.Add(copy);

            SurfaceButton export = new SurfaceButton();
            export.Text = "导出…";
            export.Location = new Point(144, 9);
            export.Size = new Size(86, 28);
            export.Click += delegate { Export(); };
            bottom.Controls.Add(export);

            SurfaceButton openLog = new SurfaceButton();
            openLog.Text = "打开日志目录";
            openLog.Location = new Point(238, 9);
            openLog.Size = new Size(110, 28);
            openLog.Click += delegate
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "\"" + Log.DirectoryPath + "\""); }
                catch (Exception ex) { Log.Error("打开日志目录失败：" + ex.Message); }
            };
            bottom.Controls.Add(openLog);

            SurfaceButton clearLog = new SurfaceButton();
            clearLog.Text = "清空日志";
            clearLog.Location = new Point(356, 9);
            clearLog.Size = new Size(86, 28);
            clearLog.Click += delegate
            {
                if (MessageBox.Show(this, "清空所有日志文件？此操作不可撤销。", "VoltPick",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return;
                }
                string error;
                if (Log.Clear(out error))
                {
                    MessageBox.Show(this, "日志已清空。", "VoltPick",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(this, "清空失败：" + (error ?? "未知错误"), "VoltPick",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            bottom.Controls.Add(clearLog);

            SurfaceButton close = new SurfaceButton();
            close.Text = "关闭";
            close.Size = new Size(86, 28);
            // 主操作用强调色，和面板里的主按钮一致。
            close.Accent = true;
            // 窗口可以被缩到 520 宽（DPI 缩放下更窄），所以「关闭」不是绝对定位钉死在 562，
            // 而是每次排版时按底栏当前宽度从右边算；否则一缩窄按钮就被裁掉，只能靠 Esc 关闭。
            // 诊断页现在是嵌在面板里的非模态视图，光设 DialogResult 不会关窗，
            // 必须自己调 Close()，否则点「关闭」毫无反应。
            close.Click += delegate { Close(); };
            _closeButton = close;
            _bottomBar = bottom;
            bottom.Resize += delegate { LayoutBottomButtons(); PositionCloseButton(); };
            bottom.Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;
            HandleCreated += delegate { ApplyWindowTheme(); };
            Load += delegate
            {
                ApplyDpiLayout();
                PositionCloseButton();
            };
        }

        /// <summary>画标题条和正文卡片：标题用面板的分组标题样式，正文卡片包住文本框。</summary>
        private void PaintBody(Graphics g, Panel body)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            Padding padding = body.Padding;
            Surface.SectionHeader(g, this,
                new Rectangle(padding.Left, UiScale.Px(this, 10),
                    Math.Max(0, body.ClientSize.Width - padding.Horizontal), UiScale.Px(this, 38)),
                "诊断信息",
                "复制给开发者时，这段文本包含版本、设备状态、设置和最近日志。");
            Rectangle card = new Rectangle(padding.Left, padding.Top,
                Math.Max(0, body.ClientSize.Width - padding.Horizontal),
                Math.Max(0, body.ClientSize.Height - padding.Top - padding.Bottom));
            Surface.Card(g, this, card);
        }

        /// <summary>
        /// 底栏按钮按文字实测宽度排版：原来写死 110 宽，「打开日志目录」在 125% 缩放下被截成
        /// 「打开日志目…」，用户根本认不出这个按钮干什么。改成量一遍文字再加内边距，随 DPI 稳定。
        /// </summary>
        private void LayoutBottomButtons()
        {
            if (_bottomBar == null || _closeButton == null)
            {
                return;
            }
            int gap = UiScale.Px(this, 8);
            int pad = UiScale.Px(this, 18);
            int height = UiScale.Px(this, 28);
            int x = UiScale.Px(this, 16);
            for (int i = 0; i < _bottomBar.Controls.Count; i++)
            {
                SurfaceButton button = _bottomBar.Controls[i] as SurfaceButton;
                if (button == null || button == _closeButton)
                {
                    continue;
                }
                int textWidth = TextRenderer.MeasureText(button.Text, button.Font).Width;
                button.Size = new Size(Math.Max(UiScale.Px(this, 64), textWidth + pad), height);
                button.Location = new Point(x, Math.Max(0, (_bottomBar.ClientSize.Height - height) / 2));
                x = button.Right + gap;
            }
        }

        /// <summary>
        /// 把「关闭」放在底栏右侧：底栏宽度来自布局（会随窗口/DPI 变化），
        /// 因此这里每次重排都重新算一次，也顺便保证按钮完整落在底栏内。
        /// </summary>
        private void PositionCloseButton()
        {
            if (_bottomBar == null || _closeButton == null)
            {
                return;
            }
            int right = UiScale.Px(this, 12);
            int left = _bottomBar.ClientSize.Width - right - _closeButton.Width;
            // 「关闭」要排在左边那排按钮之后：这里按底栏里实际存在的按钮算，
            // 不再写死 352 这种跟着旧版绝对坐标走的魔法数字。
            int leftmostRight = 0;
            for (int i = 0; i < _bottomBar.Controls.Count; i++)
            {
                Control control = _bottomBar.Controls[i];
                if (control == null || control == _closeButton)
                {
                    continue;
                }
                if (control.Right > leftmostRight)
                {
                    leftmostRight = control.Right;
                }
            }
            int minimum = leftmostRight + UiScale.Px(this, 8);
            if (left < minimum)
            {
                // 底栏实在放不下时，牺牲底栏高度换取可见性：不让按钮跑到窗口外面。
                left = Math.Max(0, _bottomBar.ClientSize.Width - _closeButton.Width - UiScale.Px(this, 2));
            }
            int top = Math.Max(0, (_bottomBar.ClientSize.Height - _closeButton.Height) / 2);
            if (_closeButton.Left != left || _closeButton.Top != top)
            {
                _closeButton.Location = new Point(left, top);
            }
        }

        /// <summary>
        /// 按当前 DPI 重排：底部按钮和边栏高度都按 96 DPI 设计值等比放大，
        /// 否则 150%/200% 缩放下 9pt 文字会撑出 28px 高的按钮，“关闭”也会被窗口裁掉。
        /// 重复调用结果一致（LayoutScale 记住设计坐标）。
        /// </summary>
        internal void ApplyDpiLayout()
        {
            ApplyDpiLayout(UiScale.FitFactor(this, new Size(DesignWidth, DesignHeight)));
        }

        internal void ApplyDpiLayout(float factor)
        {
            _layout.Apply(this, new Size(DesignWidth, DesignHeight),
                new Size(DesignMinWidth, DesignMinHeight), factor);
            LayoutBottomButtons();
            PositionCloseButton();
            SizeDiagnosticContent();
        }

        /// <summary>
        /// 诊断正文是自绘面板：滚动仍由外层 ScrollHost 负责，但正文按换行后的真实高度撑开，
        /// 外层才知道能滚多远（以前这活儿是文本框自己算的，改自绘后得自己量）。
        /// </summary>
        private void SizeDiagnosticContent()
        {
            if (_scrollView == null || _content == null || !_scrollView.IsHandleCreated)
            {
                return;
            }
            int width = Math.Max(UiScale.Px(this, 200), _scrollView.ViewWidth);
            int contentHeight = RenderContent(null, width);
            _content.Left = 0;
            _content.Width = width;
            _content.Height = contentHeight;
            _scrollView.SetContentHeight(contentHeight);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            ApplyDpiLayout();
            ApplyWindowTheme();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            SizeDiagnosticContent();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _fonts.Dispose();
            }
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

        /// <summary>诊断正文的一行：分节标题（可空）+ 左标签（可空）+ 右值。</summary>
        private sealed class DiagRow
        {
            public string Title;
            public string Label;
            public string Value;
            // 说明文字用弱化色，错误行用警示色：同一屏里要能一眼分出主次。
            public bool Muted;
            public bool Warn;
        }

        /// <summary>
        /// 把诊断数据摊成一串行：分节标题挂在该节第一行上，绘制时先画标题再画行。
        /// 与 BuildText 读同一份快照，保证界面看到的和复制出去的是同一批事实。
        /// </summary>
        private List<DiagRow> BuildRows()
        {
            List<DiagRow> rows = new List<DiagRow>();

            rows.Add(new DiagRow { Title = "运行环境", Label = "时间", Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) });
            rows.Add(new DiagRow { Label = "系统", Value = OsInfo.DisplayName + "  " + (Environment.Is64BitOperatingSystem ? "x64" : "x86") });
            rows.Add(new DiagRow { Label = "运行时", Value = ".NET Framework " + Environment.Version });
            bool lowIntegrity = Native.IsLowIntegrity();
            rows.Add(new DiagRow
            {
                Label = "完整性",
                Value = lowIntegrity ? "Low（托盘可能被系统拦截）" : "Medium / 正常",
                Warn = lowIntegrity
            });

            rows.Add(new DiagRow { Title = "文件位置", Label = "配置", Value = AppSettings.SettingsPath });
            rows.Add(new DiagRow
            {
                Label = "历史",
                Value = BatteryHistory.FilePath + "（" + (AppSettings.Current.HistoryEnabled ? "开启" : "关闭") +
                        "，" + BatteryHistory.CountSamples() + " 个采样）"
            });
            rows.Add(new DiagRow { Label = "日志", Value = Log.FilePath });

            rows.Add(new DiagRow
            {
                Title = "行为",
                Label = "面板位置",
                Value = AppSettings.Current.PanelPositionSaved
                    ? AppSettings.Current.PanelX + "," + AppSettings.Current.PanelY + "（已记忆）"
                    : "自动（跟随托盘）"
            });
            int refreshMinutes = AppSettings.RefreshMinutes;
            rows.Add(new DiagRow
            {
                Label = "刷新",
                Value = (refreshMinutes >= 60 && refreshMinutes % 60 == 0
                        ? (refreshMinutes / 60) + " 小时"
                        : refreshMinutes + " 分钟") +
                        "，息屏暂停=" + (AppSettings.Current.PauseWhenIdle ? "开" : "关")
            });
            rows.Add(new DiagRow
            {
                Label = "蓝牙",
                Value = (_snapshot.BluetoothRadioKnown
                        ? (_snapshot.BluetoothRadioOn ? "开" : "关")
                        : "未知") +
                        (string.IsNullOrEmpty(_snapshot.BluetoothError) ? "" : "（" + _snapshot.BluetoothError + "）"),
                Warn = _snapshot.BluetoothRadioKnown && !_snapshot.BluetoothRadioOn
            });
            rows.Add(new DiagRow { Label = "自启动", Value = AutoStart.IsEnabled() ? "开" : "关" });

            if (_snapshot.Devices.Count == 0)
            {
                rows.Add(new DiagRow { Title = "设备", Label = "—", Value = "未发现设备", Muted = true });
            }
            for (int i = 0; i < _snapshot.Devices.Count; i++)
            {
                DeviceState device = _snapshot.Devices[i];
                string title = "设备 " + (i + 1) + " · " + device.DisplayName;
                if (i == 0)
                {
                    title = "设备 · 共 " + _snapshot.Devices.Count + " 台";
                }
                rows.Add(new DiagRow { Title = title, Label = "名称", Value = device.DisplayName + "（" + device.CategoryText + "）" });
                rows.Add(new DiagRow
                {
                    Label = "状态",
                    Value = device.StatusText + (string.IsNullOrEmpty(device.StatusDetail) ? "" : " · " + device.StatusDetail)
                });
                rows.Add(new DiagRow
                {
                    Label = "连接",
                    Value = device.TransportText + "，电量支持=" + (device.BatterySupported ? "是" : "否")
                });
                rows.Add(new DiagRow { Label = "ID", Value = device.Id });
                rows.Add(new DiagRow { Label = "硬件", Value = Log.Redact(device.HardwareId) });
                if (device.VendorId != 0 || device.ProductId != 0)
                {
                    rows.Add(new DiagRow
                    {
                        Label = "VID/PID",
                        Value = string.Format(CultureInfo.InvariantCulture, "{0:X4}/{1:X4}", device.VendorId, device.ProductId)
                    });
                }
                if (device.BluetoothAddress != 0)
                {
                    rows.Add(new DiagRow { Label = "蓝牙地址", Value = device.AddressHash });
                }
                rows.Add(new DiagRow
                {
                    Label = "来源",
                    Value = device.SourceText + "，查询 " + device.QueryCount + " 次，成功 " + device.SuccessCount + " 次"
                });
                if (device.HasValue)
                {
                    BatteryReading reading = device.LastReading;
                    rows.Add(new DiagRow
                    {
                        Label = "最后读数",
                        Value = reading.Percent.Value + "%" +
                                (reading.Charging.HasValue ? (reading.Charging.Value ? "，充电中" : "，未充电") : "，充电状态未知") +
                                (reading.Millivolts.HasValue ? "，" + reading.Millivolts.Value + " mV" : "") +
                                "，置信度=" + reading.Confidence
                    });
                    if (reading.LeftPercent.HasValue || reading.RightPercent.HasValue)
                    {
                        rows.Add(new DiagRow
                        {
                            Label = "左右耳",
                            Value = "L=" + (reading.LeftPercent.HasValue
                                    ? reading.LeftPercent.Value.ToString(CultureInfo.InvariantCulture) : "?") +
                                    "%，R=" + (reading.RightPercent.HasValue
                                    ? reading.RightPercent.Value.ToString(CultureInfo.InvariantCulture) : "?") + "%"
                        });
                    }
                    rows.Add(new DiagRow
                    {
                        Label = "更新时间",
                        Value = reading.Timestamp == DateTime.MinValue
                            ? "--:--"
                            : reading.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    });
                    if (AppSettings.Current.DetailedDiagnostics && !string.IsNullOrEmpty(reading.Raw))
                    {
                        rows.Add(new DiagRow { Label = "原始报文", Value = Log.Redact(reading.Raw) });
                    }
                }
                if (!string.IsNullOrEmpty(device.UnsupportedReason))
                {
                    rows.Add(new DiagRow { Label = "不支持原因", Value = device.UnsupportedReason, Warn = true });
                }
                if (!string.IsNullOrEmpty(device.LastError))
                {
                    rows.Add(new DiagRow { Label = "最后错误", Value = device.LastError, Warn = true });
                }
                rows.Add(new DiagRow
                {
                    Label = "下次查询",
                    Value = device.NextDue == DateTime.MinValue
                        ? "立即"
                        : device.NextDue.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                });
            }

            rows.Add(new DiagRow { Title = "说明", Value = "鼠标、键盘优先读取标准 HID 电量接口；蓝牙设备读取标准电池服务。", Muted = true });
            rows.Add(new DiagRow { Value = "部分游戏外设使用厂商私有协议，程序在识别到该协议时一并支持。", Muted = true });
            rows.Add(new DiagRow { Value = "蓝牙耳机优先读取厂商私有服务以获取左右耳分别的电量，不支持时回退到标准电池服务。", Muted = true });
            rows.Add(new DiagRow { Value = "2.4G 接收器通常不传输电量，此时显示“不支持读取”，不会显示 0%。", Muted = true });
            rows.Add(new DiagRow { Value = "经典蓝牙音频通道被系统占用时，程序不会抢占音频，只做明确的降级说明。", Muted = true });
            rows.Add(new DiagRow { Value = "所有读数均来自设备上报；程序不做插值、不做猜测。", Muted = true });
            return rows;
        }

        /// <summary>
        /// 排版并（可选）绘制诊断正文，返回内容总高度。
        /// g 为 null 时只量高度不画，用于给 ScrollHost 报内容尺寸，避免量一次画一次两套算法对不上。
        /// </summary>
        private int RenderContent(Graphics g, int width)
        {
            int padX = UiScale.Px(this, 14);
            int inner = Math.Max(UiScale.Px(this, 160), width - padX * 2);
            Font titleFont = Theme.PaintFont(11.5f, FontStyle.Bold);
            Font labelFont = Theme.PaintFont(Theme.FontSmall, FontStyle.Regular);
            Font valueFont = Theme.PaintFont(Theme.FontBody, FontStyle.Regular);
            List<DiagRow> rows = BuildRows();

            // 标签列按最长标签量一次，避免每行各让一截、右值左边缘参差不齐。
            int labelWidth = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (string.IsNullOrEmpty(rows[i].Label))
                {
                    continue;
                }
                int measured = TextRenderer.MeasureText(rows[i].Label, labelFont).Width;
                if (measured > labelWidth)
                {
                    labelWidth = measured;
                }
            }
            labelWidth = Math.Min(Math.Max(labelWidth, UiScale.Px(this, 44)), UiScale.Px(this, 108));
            int valueLeft = padX + labelWidth + UiScale.Px(this, 12);
            int valueWidth = Math.Max(UiScale.Px(this, 80), width - padX - valueLeft);

            int titleHeight = UiScale.Px(this, 24);
            int titleGap = UiScale.Px(this, 14);
            int rowGap = UiScale.Px(this, 7);
            int y = UiScale.Px(this, 8);

            for (int i = 0; i < rows.Count; i++)
            {
                DiagRow row = rows[i];
                if (!string.IsNullOrEmpty(row.Title))
                {
                    if (i > 0)
                    {
                        y += titleGap;
                    }
                    if (g != null)
                    {
                        // 和设置页一致：左侧一根短蓝竖条 + 粗体标题，右侧一条细分割线。
                        Rectangle bar = new Rectangle(padX, y + (titleHeight - UiScale.Px(this, 13)) / 2,
                            UiScale.Px(this, 3), UiScale.Px(this, 13));
                        using (SolidBrush brush = new SolidBrush(Theme.Blue))
                        {
                            g.FillRectangle(brush, bar);
                        }
                        int textLeft = padX + UiScale.Px(this, 10);
                        int textWidth = TextRenderer.MeasureText(g, row.Title, titleFont).Width + UiScale.Px(this, 6);
                        TextRenderer.DrawText(g, row.Title, titleFont,
                            new Rectangle(textLeft, y, textWidth, titleHeight), Theme.Text,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                        int lineLeft = textLeft + textWidth;
                        int lineRight = width - padX;
                        if (lineRight > lineLeft)
                        {
                            using (Pen pen = new Pen(Theme.Divider))
                            {
                                g.DrawLine(pen, lineLeft, y + titleHeight / 2, lineRight, y + titleHeight / 2);
                            }
                        }
                    }
                    y += titleHeight;
                    // 标题和数据行可以是同一行对象，画完标题继续往下画这一行本身。
                }

                int valueHeight = Math.Max(UiScale.Px(this, 18),
                    TextRenderer.MeasureText(row.Value, valueFont,
                        new Size(valueWidth, int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height);
                if (g != null)
                {
                    if (!string.IsNullOrEmpty(row.Label))
                    {
                        TextRenderer.DrawText(g, row.Label, labelFont,
                            new Rectangle(padX, y, labelWidth, valueHeight), Theme.Muted,
                            TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
                    }
                    Color color = row.Warn ? Theme.Orange : (row.Muted ? Theme.Muted : Theme.Text);
                    TextRenderer.DrawText(g, row.Value, valueFont,
                        new Rectangle(valueLeft, y, valueWidth, valueHeight), color,
                        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak |
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                }
                y += valueHeight + rowGap;
                if (i == rows.Count - 1)
                {
                    break;
                }
            }
            return y + UiScale.Px(this, 10);
        }

        private string BuildText()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine(AppInfo.DisplayName);
            builder.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            builder.AppendLine("系统：" + OsInfo.DisplayName + "  " +
                               (Environment.Is64BitOperatingSystem ? "x64" : "x86"));
            builder.AppendLine("运行时：.NET Framework " + Environment.Version);
            builder.AppendLine("进程完整性：" + (Native.IsLowIntegrity() ? "Low（托盘可能被系统拦截）" : "Medium/正常"));
            builder.AppendLine("配置：" + AppSettings.SettingsPath);
            builder.AppendLine("历史：" + BatteryHistory.FilePath +
                               "（" + (AppSettings.Current.HistoryEnabled ? "开启" : "关闭") +
                               "，" + BatteryHistory.CountSamples() + " 个采样）");
            builder.AppendLine("日志：" + Log.FilePath);
            builder.AppendLine("面板位置：" + (AppSettings.Current.PanelPositionSaved
                ? AppSettings.Current.PanelX + "," + AppSettings.Current.PanelY + "（已记忆）"
                : "自动（跟随托盘）"));
            int refreshMinutes = AppSettings.RefreshMinutes;
            builder.AppendLine("刷新：" + (refreshMinutes >= 60 && refreshMinutes % 60 == 0
                    ? (refreshMinutes / 60) + " 小时"
                    : refreshMinutes + " 分钟") +
                               "，息屏暂停=" + (AppSettings.Current.PauseWhenIdle ? "开" : "关"));
            builder.AppendLine("蓝牙：" + (_snapshot.BluetoothRadioKnown
                ? (_snapshot.BluetoothRadioOn ? "开" : "关")
                : "未知") +
                (string.IsNullOrEmpty(_snapshot.BluetoothError) ? "" : "（" + _snapshot.BluetoothError + "）"));
            builder.AppendLine("自启动：" + (AutoStart.IsEnabled() ? "开" : "关"));
            builder.AppendLine();
            builder.AppendLine("设备：");

            if (_snapshot.Devices.Count == 0)
            {
                builder.AppendLine("  （未发现设备）");
            }
            for (int i = 0; i < _snapshot.Devices.Count; i++)
            {
                DeviceState device = _snapshot.Devices[i];
                builder.AppendLine("  [" + (i + 1) + "] " + device.DisplayName + "  (" + device.CategoryText + ")");
                builder.AppendLine("      状态：" + device.StatusText +
                                   (string.IsNullOrEmpty(device.StatusDetail) ? "" : " · " + device.StatusDetail));
                builder.AppendLine("      连接：" + device.TransportText +
                                   "，电量支持=" + (device.BatterySupported ? "是" : "否"));
                builder.AppendLine("      ID：" + device.Id +
                                   "，硬件：" + Log.Redact(device.HardwareId));
                if (device.VendorId != 0 || device.ProductId != 0)
                {
                    builder.AppendLine(string.Format("      VID/PID：{0:X4}/{1:X4}",
                        device.VendorId, device.ProductId));
                }
                if (device.BluetoothAddress != 0)
                {
                    builder.AppendLine("      蓝牙地址哈希：" + device.AddressHash);
                }
                builder.AppendLine("      来源：" + device.SourceText +
                                   "，查询 " + device.QueryCount + " 次，成功 " + device.SuccessCount + " 次");
                if (device.HasValue)
                {
                    BatteryReading reading = device.LastReading;
                    builder.AppendLine("      最后读数：" + reading.Percent.Value + "%" +
                                       (reading.Charging.HasValue ? (reading.Charging.Value ? "，充电中" : "，未充电") : "，充电状态未知") +
                                       (reading.Millivolts.HasValue ? "，" + reading.Millivolts.Value + " mV" : "") +
                                       "，置信度=" + reading.Confidence);
                    if (reading.LeftPercent.HasValue || reading.RightPercent.HasValue)
                    {
                        builder.AppendLine("      左右耳：L=" +
                            (reading.LeftPercent.HasValue ? reading.LeftPercent.Value.ToString(CultureInfo.InvariantCulture) : "?") +
                            "%，R=" +
                            (reading.RightPercent.HasValue ? reading.RightPercent.Value.ToString(CultureInfo.InvariantCulture) : "?") + "%");
                    }
                    builder.AppendLine("      更新时间：" + (reading.Timestamp == DateTime.MinValue
                        ? "--:--"
                        : reading.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
                    if (AppSettings.Current.DetailedDiagnostics && !string.IsNullOrEmpty(reading.Raw))
                    {
                        builder.AppendLine("      原始报文：" + Log.Redact(reading.Raw));
                    }
                }
                if (!string.IsNullOrEmpty(device.UnsupportedReason))
                {
                    builder.AppendLine("      不支持原因：" + device.UnsupportedReason);
                }
                if (!string.IsNullOrEmpty(device.LastError))
                {
                    builder.AppendLine("      最后错误：" + device.LastError);
                }
                builder.AppendLine("      下次查询：" + (device.NextDue == DateTime.MinValue
                    ? "立即"
                    : device.NextDue.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
                builder.AppendLine();
            }

            builder.AppendLine("说明：");
            builder.AppendLine("  * 鼠标、键盘优先读取标准 HID 电量接口；蓝牙设备读取标准电池服务。");
            builder.AppendLine("  * 部分游戏外设使用厂商私有协议，程序在识别到该协议时一并支持。");
            builder.AppendLine("  * 蓝牙耳机优先读取厂商私有服务以获取左右耳分别的电量，不支持时回退到标准电池服务。");
            builder.AppendLine("  * 2.4G 接收器通常不传输电量，此时显示“不支持读取”，不会显示 0%。");
            builder.AppendLine("  * 经典蓝牙音频通道被系统占用时，程序不会抢占音频，只做明确的降级说明。");
            builder.AppendLine("  * 所有读数均来自设备上报；程序不做插值、不做猜测。");
            return builder.ToString();
        }

        private void Export()
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "导出诊断信息";
                dialog.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                dialog.FileName = "device-battery-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
                try
                {
                    File.WriteAllText(dialog.FileName, BuildText(), new UTF8Encoding(false));
                    MessageBox.Show(this, "已导出到：\n" + dialog.FileName, "VoltPick",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "导出失败：" + ex.Message, "VoltPick",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }
    }
}
