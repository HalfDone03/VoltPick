using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DeviceBattery
{
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly HiddenHost _host;
        private readonly BatteryService _service;
        private readonly DevicePanel _panel;
        private readonly NotifyIcon _tray;
        private readonly ContextMenuStrip _menu;
        // 「显示」弹出的临时菜单不能就地 Dispose：ToolStripManager 的 ModalMenuFilter
        // 在关闭下拉之后还会回访该下拉，而且关闭动作本身可能被推迟到后续消息。同一条
        // 鼠标消息里释放它，下一次按下就会抛 ObjectDisposedException 并结束进程。
        // 所以只先 Close()（解除活动下拉跟踪），只有真正的 Dispose 交给 DisposeRetiredMenus()。
        private ContextMenuStrip _unhideMenu;
        private readonly List<ContextMenuStrip> _retiredMenus = new List<ContextMenuStrip>();

        // 托盘进程通常不是前台进程：这时弹出菜单会被系统当成“点到了别的窗口”，
        // 第一次点击就把菜单关掉，用户看到的就是「点显示没反应」。
        // 弹出之前先把面板窗口提为前台，菜单才能真正吃到点击。
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _traySourceItem;
        private readonly ToolStripMenuItem _autoStartItem;
        private readonly List<NotifyIcon> _separate = new List<NotifyIcon>();
        private readonly Timer _uiTimer;
        private readonly Timer _deviceTimer;
        private readonly Timer _startupTimer;

        private HistoryForm _historyForm;
        private SettingsForm _settingsForm;
        private BatterySnapshot _snapshot = new BatterySnapshot();
        private int _iconSize;
        private bool _renderPending;
        private DateTime _refreshUntil = DateTime.MinValue;
        private string _abnormalExit;
        private bool _openDiagnosticsOnBalloon;

        public TrayApp()
        {
            AppSettings.Load();
            Theme.Apply(AppSettings.Current.ThemeMode);
            string previousExit;
            if (RunSession.DetectPreviousAbnormalExit(out previousExit))
            {
                _abnormalExit = previousExit;
            }
            RunSession.MarkRunning();
            _iconSize = ResolveIconSize();
            _host = new HiddenHost();
            _service = new BatteryService();
            _panel = new DevicePanel();

            _menu = new ContextMenuStrip();
            _menu.ShowImageMargin = false;
            _statusItem = new ToolStripMenuItem("正在检测设备…");
            _statusItem.Enabled = false;
            _menu.Items.Add(_statusItem);
            _menu.Items.Add(new ToolStripSeparator());

            // 菜单只留最常用的几项：状态栏显示、设置、自启动、退出。
            // 刷新/历史/诊断/使用说明都在面板和设置里，不必再占一层菜单。
            _traySourceItem = new ToolStripMenuItem("状态栏显示");
            _menu.Items.Add(_traySourceItem);

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem settingsItem = new ToolStripMenuItem("设置…");
            settingsItem.Click += delegate { ShowSettings(); };
            _menu.Items.Add(settingsItem);

            _menu.Items.Add(new ToolStripSeparator());

            _autoStartItem = new ToolStripMenuItem("自启动：关");
            _autoStartItem.Click += delegate { ToggleAutoStart(); };
            _menu.Items.Add(_autoStartItem);

            _menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate { ExitApp(); };
            _menu.Items.Add(exitItem);
            _menu.Opening += delegate
            {
                UpdateMenu();
                RebuildTraySourceMenu();
            };
            ThemeMenu.Apply(_menu);

            _tray = new NotifyIcon();
            _tray.ContextMenuStrip = _menu;
            _tray.Text = "VoltPick：正在检测…";
            _tray.Icon = IconFactory.BuildDigits(_iconSize, "…", Theme.Faint, false,
                AppSettings.Current.DigitPercent, false, AppSettings.Current.IconStyle,
                AppSettings.Current.IconFont);
            _tray.Visible = true;
            _tray.MouseClick += OnTrayMouseClick;
            _tray.BalloonTipClicked += OnBalloonTipClicked;

            _panel.RefreshRequested += delegate { RequestRefresh(); };
            // 打开设置不隐藏面板：面板要一直留在桌面右下角显示电量。
            _panel.SettingsRequested += delegate { ShowSettings(); };
            _panel.DiagnosticsRequested += delegate { HidePanel(); ShowDiagnostics(); };
            _panel.HistoryRequested += delegate { HidePanel(); ShowHistory(); };
            _panel.OpenBluetoothRequested += delegate { OpenBluetoothSettings(); };
            _panel.UnhideRequested += delegate { ShowUnhideMenu(); };
            _panel.HideDeviceRequested += delegate(string id) { HideDevice(id); };
            _panel.DeviceRefreshRequested += delegate { RequestRefresh(); };
            _panel.VisibleChanged += delegate
            {
                if (!_panel.Visible)
                {
                    _service.SetPanelOpen(false);
                }
            };

            _service.Changed += OnServiceChanged;
            _service.LowBattery += OnLowBattery;
            _host.DeviceChanged += OnDeviceChanged;

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionEnding += OnSessionEnding;
            try { SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged; } catch (Exception) { }

            _uiTimer = new Timer();
            _uiTimer.Interval = 5000;
            _uiTimer.Tick += delegate { RefreshUi(); };
            _uiTimer.Start();

            _deviceTimer = new Timer();
            _deviceTimer.Interval = 2000;
            _deviceTimer.Tick += delegate
            {
                _deviceTimer.Stop();
                _service.RequestDiscovery();
            };

            _startupTimer = new Timer();
            _startupTimer.Interval = 2500;
            _startupTimer.Tick += delegate { _startupTimer.Stop(); OnStartupTick(); };
            _startupTimer.Start();

            AutoStart.RepairPathIfNeeded();
            SingleInstance.ShowRequested = delegate
            {
                try
                {
                    _host.BeginInvoke(new Action(delegate
                    {
                        if (_panel.Visible)
                        {
                            HidePanel();
                        }
                        else
                        {
                            ShowPanel();
                        }
                    }));
                }
                catch (Exception)
                {
                }
            };

            _service.Start(IsStartedByAutostart());
            RefreshUi();
        }

        private void OnServiceChanged(object sender, EventArgs e)
        {
            if (_renderPending)
            {
                return;
            }
            _renderPending = true;
            try
            {
                _host.BeginInvoke(new Action(delegate
                {
                    _renderPending = false;
                    RefreshUi();
                }));
            }
            catch (Exception)
            {
                _renderPending = false;
            }
        }

        private void OnLowBattery(object sender, LowBatteryEventArgs e)
        {
            try
            {
                _host.BeginInvoke(new Action<LowBatteryEventArgs>(ShowLowBattery), e);
            }
            catch (Exception)
            {
            }
        }

        private void RefreshUi()
        {
            _snapshot = _service.GetSnapshot();
            if (AppSettings.Current.DisplayMode == "separate")
            {
                RenderSeparate();
            }
            else
            {
                DisposeSeparateIcons();
                _tray.Visible = true;
                RenderAggregate();
            }
            EnsureTrayIconVisible();

            if (_menu.Visible)
            {
                UpdateMenu();
            }
            if (_panel.Visible)
            {
                _panel.SetSnapshot(DeviceVisibility.ConnectedSnapshot(_snapshot));
                _panel.SetFooterStatus(BuildFooterStatus());
            }
            if (_refreshUntil != DateTime.MinValue && DateTime.Now >= _refreshUntil)
            {
                _refreshUntil = DateTime.MinValue;
                _panel.SetRefreshing(false);
            }
            DisposeRetiredMenus();
            SetUiInterval(_panel.Visible || _refreshUntil != DateTime.MinValue ? 1000 : 15000);
        }

        /// <summary>
        /// 兜底：任何渲染分支出问题时（例如设备数量正好在切换“分开显示 / 合并显示”的过程中变化），
        /// 托盘上至少要留一个图标，不能让用户彻底找不到程序。
        /// </summary>
        private void EnsureTrayIconVisible()
        {
            if (_tray.Visible)
            {
                return;
            }
            for (int i = 0; i < _separate.Count; i++)
            {
                if (_separate[i] != null && _separate[i].Visible)
                {
                    return;
                }
            }
            Log.Warn("托盘图标全部隐藏，回退到合并图标");
            _tray.Visible = true;
            RenderAggregate();
        }

        private void SetUiInterval(int milliseconds)
        {
            if (_uiTimer.Interval != milliseconds)
            {
                _uiTimer.Interval = milliseconds;
            }
        }

        private void RenderAggregate()
        {
            DeviceState primary = ChooseAggregateDevice();
            string text = "—";
            Color color = Theme.Faint;
            bool dim = true;
            bool charging = false;
            string tooltip = BuildAggregateTooltip();

            if (primary != null && primary.HasValue)
            {
                text = primary.PercentOrZero.ToString();
                color = Theme.StatusColor(primary.Status, primary.PercentOrZero);
                dim = primary.Status == DeviceStatus.Stale ||
                      primary.Status == DeviceStatus.Sleeping ||
                      primary.Status == DeviceStatus.Offline ||
                      primary.Status == DeviceStatus.Error;
                charging = primary.Status == DeviceStatus.Charging;
            }
            else if (!_service.HasDiscovered)
            {
                text = "…";
                tooltip = "VoltPick：正在检测…";
            }
            else if (_snapshot.Devices.Count == 0)
            {
                tooltip = "VoltPick：未检测到可读取电量的设备";
            }
            else
            {
                text = "—";
                color = Theme.Faint;
                dim = true;
                // 读不到电量的设备按规则不显示；这里必须说清“为什么没有”，不能让托盘留空。
                if (HiddenDeviceCount() > 0)
                {
                    tooltip = "VoltPick：未检测到可读取电量的设备 · " + HiddenDeviceCount() + " 台已记录设备当前无法读取";
                }
                else
                {
                    tooltip = "VoltPick：未检测到可读取电量的设备";
                }
            }

            SetIconFor(_tray, text, color, dim, charging);
            _tray.Text = Clip(tooltip, 63);
        }

        private void RenderSeparate()
        {
            List<DeviceState> enabled = DeviceVisibility.ConnectedDevices(_snapshot);
            if (enabled.Count == 0)
            {
                DisposeSeparateIcons();
                _tray.Visible = true;
                RenderAggregate();
                return;
            }

            _tray.Visible = false;
            int count = Math.Min(4, enabled.Count);
            EnsureSeparate(count);
            for (int i = 0; i < _separate.Count; i++)
            {
                NotifyIcon icon = _separate[i];
                DeviceState device = enabled[i];
                icon.Visible = true;
                string text = device.HasValue ? device.PercentOrZero.ToString() : "—";
                Color color = device.HasValue ? Theme.StatusColor(device.Status, device.PercentOrZero) : Theme.Faint;
                bool dim = !device.HasValue ||
                           device.Status == DeviceStatus.Stale ||
                           device.Status == DeviceStatus.Sleeping ||
                           device.Status == DeviceStatus.Offline ||
                           device.Status == DeviceStatus.Unsupported;
                bool charging = device.Status == DeviceStatus.Charging;
                SetIconFor(icon, text, color, dim, charging);
                icon.Text = Clip(device.DisplayName + " " + (device.HasValue ? device.PercentOrZero + "%" : device.StatusText), 63);
            }
        }

        private void EnsureSeparate(int count)
        {
            while (_separate.Count < count)
            {
                NotifyIcon icon = new NotifyIcon();
                icon.ContextMenuStrip = _menu;
                icon.Text = "VoltPick";
                icon.Icon = IconFactory.BuildDigits(_iconSize, "…", Theme.Faint, false,
                    AppSettings.Current.DigitPercent, false, AppSettings.Current.IconStyle,
                    AppSettings.Current.IconFont);
                icon.Visible = true;
                icon.MouseClick += OnTrayMouseClick;
                icon.BalloonTipClicked += OnBalloonTipClicked;
                _separate.Add(icon);
            }
            while (_separate.Count > count)
            {
                NotifyIcon icon = _separate[_separate.Count - 1];
                _separate.RemoveAt(_separate.Count - 1);
                try
                {
                    icon.Visible = false;
                    icon.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        private void DisposeSeparateIcons()
        {
            for (int i = 0; i < _separate.Count; i++)
            {
                try
                {
                    _separate[i].Visible = false;
                    _separate[i].Dispose();
                }
                catch (Exception)
                {
                }
            }
            _separate.Clear();
        }

        private DeviceState ChooseAggregateDevice()
        {
            return TrayDeviceSelector.Choose(_snapshot, AppSettings.Current.TrayDeviceId,
                AppSettings.Current.IconDevice);
        }

        private int HiddenDeviceCount()
        {
            int hidden = 0;
            if (_snapshot != null)
            {
                for (int i = 0; i < _snapshot.Devices.Count; i++)
                {
                    if (!DeviceVisibility.IsDisplayable(_snapshot.Devices[i]))
                    {
                        hidden++;
                    }
                }
            }
            return hidden;
        }

        /// <summary>面板上点「显示」：把当前被隐藏（连着但读不到电量）的设备列出来，挑一台就取消隐藏。</summary>
        private void ShowUnhideMenu()
        {
            List<DeviceState> hidden = new List<DeviceState>();
            if (_snapshot != null)
            {
                for (int i = 0; i < _snapshot.Devices.Count; i++)
                {
                    DeviceState device = _snapshot.Devices[i];
                    if (device != null && !string.IsNullOrEmpty(device.Id) &&
                        !DeviceVisibility.IsDisplayable(device))
                    {
                        hidden.Add(device);
                    }
                }
            }
            if (hidden.Count == 0)
            {
                // 面板底部写着「显示 N」，可全量快照里却挑不出隐藏设备：两次快照不同步。
                Log.Info("显示菜单：全量快照里没有隐藏设备，这次点击被忽略");
                return;
            }
            Log.Info("显示菜单：弹出 " + hidden.Count + " 台隐藏设备");

            RetireUnhideMenu();
            ContextMenuStrip menu = new ContextMenuStrip();
            _unhideMenu = menu;
            menu.ShowImageMargin = false;
            // 菜单在面板外面：告诉面板“先别收”，否则点开的瞬间面板就消失了。
            _panel.SetMenuOpen(true);
            menu.Closed += delegate { _panel.SetMenuOpen(false); };
            // 先给一条「全部显示」：用户想一次看全所有设备时不用一台台点。
            ToolStripMenuItem all = new ToolStripMenuItem("显示全部（" + hidden.Count + " 台）");
            List<string> allIds = new List<string>();
            for (int k = 0; k < hidden.Count; k++)
            {
                allIds.Add(hidden[k].Id);
            }
            all.Click += delegate
            {
                Log.Info("显示菜单点击：全部 " + allIds.Count + " 台");
                SetDevicesForceVisible(allIds, true);
                RefreshPanelAfterUnhide();
            };
            menu.Items.Add(all);
            menu.Items.Add(new ToolStripSeparator());
            for (int i = 0; i < hidden.Count; i++)
            {
                DeviceState device = hidden[i];
                string reason = string.IsNullOrEmpty(device.UnsupportedReason)
                    ? device.StatusText : device.UnsupportedReason;
                string text = device.DisplayName + " · " + reason;
                if (text.Length > 60)
                {
                    text = text.Substring(0, 59) + "…";
                }
                string id = device.Id;
                ToolStripMenuItem item = new ToolStripMenuItem(text);
                item.Click += delegate
                {
                    Log.Info("显示菜单点击：" + id);
                    SetDeviceForceVisible(id, true);
                    RefreshPanelAfterUnhide();
                };
                menu.Items.Add(item);
            }
            // 不在这里也不在 Closed 里释放：见 RetireUnhideMenu() 的说明。
            try
            {
                if (_panel != null && _panel.IsHandleCreated)
                {
                    SetForegroundWindow(_panel.Handle);
                }
            }
            catch (Exception)
            {
            }
            menu.Show(Cursor.Position);
            Log.Info("显示菜单：已弹出，位置 " + Cursor.Position.X + "," + Cursor.Position.Y);
        }

        /// <summary>
        /// 让上一份「显示」菜单退休：先 Close() 解除 ToolStripManager 的活动下拉跟踪，
        /// 真正的 Dispose 推迟到 DisposeRetiredMenus()，绝不在鼠标消息处理中途释放。
        /// </summary>
        private void RetireUnhideMenu()
        {
            ContextMenuStrip previous = _unhideMenu;
            if (previous == null)
            {
                return;
            }
            _unhideMenu = null;
            if (!previous.IsDisposed)
            {
                try
                {
                    if (previous.Visible)
                    {
                        previous.Close();
                    }
                }
                catch (Exception)
                {
                }
            }
            _retiredMenus.Add(previous);
        }

        /// <summary>
        /// 释放已退休的「显示」菜单。只在 UI 计时器回调里跑：那一刻消息循环已经转过很多圈，
        /// 下拉的关闭动作一定完成；仍然可见的那一份留到下一轮。
        /// </summary>
        private void DisposeRetiredMenus()
        {
            for (int i = _retiredMenus.Count - 1; i >= 0; i--)
            {
                ContextMenuStrip menu = _retiredMenus[i];
                if (menu == null)
                {
                    _retiredMenus.RemoveAt(i);
                    continue;
                }
                if (menu.Visible)
                {
                    continue;
                }
                _retiredMenus.RemoveAt(i);
                try { menu.Dispose(); } catch (Exception) { }
            }
        }

        /// <summary>
        /// 菜单点完把面板重新亮出来：SetMenuOpen 期间面板不会自己收起，
        /// 但菜单一关焦点就没了，这里补一次显示，用户才能马上看到刚放出来的设备。
        /// </summary>
        private void RefreshPanelAfterUnhide()
        {
            try
            {
                if (!_panel.IsHandleCreated)
                {
                    _panel.SetMenuOpen(false);
                    return;
                }
                _panel.BeginInvoke(new Action(delegate
                {
                    _panel.SetMenuOpen(false);
                    // 菜单开着的时候面板一直留着，所以这里只要重画一次：
                    // 不走 ShowPanel()，避免面板重新贴边闪一下又被判成“点了外面”。
                    if (!_panel.Visible)
                    {
                        ShowPanel();
                        return;
                    }
                    _snapshot = _service.GetSnapshot();
                    // 服务里缓存的设备还带着旧的 ForceVisible：不重新套一遍偏好，
                    // 刚点完“显示”的设备在这一步会被判成隐藏，面板反而少一台。
                    ApplyPreferencesToSnapshot(_snapshot);
                    _panel.SetSnapshot(DeviceVisibility.ConnectedSnapshot(_snapshot));
                    _panel.SetFooterStatus(BuildFooterStatus());
                    Log.Info("解除隐藏后刷新面板：" + DescribePanelDevices(_snapshot));
                    RefreshUi();
                }));
            }
            catch (Exception)
            {
            }
        }

        /// <summary>给刚拿到的快照重新套一遍用户偏好（强制显示 / 改名 / 阈值）。</summary>
        private static void ApplyPreferencesToSnapshot(BatterySnapshot snapshot)
        {
            if (snapshot == null || snapshot.Devices == null)
            {
                return;
            }
            for (int i = 0; i < snapshot.Devices.Count; i++)
            {
                DeviceState device = snapshot.Devices[i];
                if (device != null && !string.IsNullOrEmpty(device.Id))
                {
                    AppSettings.ApplyDevicePreferences(device);
                }
            }
        }

        /// <summary>取消隐藏：写进设置并立刻刷新面板，设备马上出现在列表里。</summary>
        private void SetDeviceForceVisible(string id, bool forceVisible)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }
            SetDevicesForceVisible(new string[] { id }, forceVisible);
        }

        /// <summary>
        /// 批量取消隐藏：只写一次设置文件、只刷一次界面。
        /// 「显示全部」用它，避免 N 台设备触发 N 次 Save + RefreshUi。
        /// </summary>
        private void SetDevicesForceVisible(IEnumerable<string> ids, bool forceVisible)
        {
            if (ids == null)
            {
                return;
            }
            List<string> changed = new List<string>();
            foreach (string id in ids)
            {
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }
                AppSettings.SetDeviceForceVisible(id, forceVisible);
                changed.Add(id);
            }
            if (changed.Count == 0)
            {
                return;
            }
            AppSettings.Save();
            Log.Info("显示设置已保存：" + changed.Count + " 台 → " + (forceVisible ? "显示" : "隐藏") +
                     "（" + string.Join("、", changed.ToArray()) + "）");
            if (_snapshot != null)
            {
                for (int i = 0; i < _snapshot.Devices.Count; i++)
                {
                    DeviceState device = _snapshot.Devices[i];
                    if (device == null || string.IsNullOrEmpty(device.Id))
                    {
                        continue;
                    }
                    if (changed.Contains(device.Id))
                    {
                        AppSettings.ApplyDevicePreferences(device);
                    }
                }
            }
            RefreshUi();
        }

        /// <summary>卡片上的「隐藏」：把这台设备恢复成自动隐藏。</summary>
        private void HideDevice(string id)
        {
            SetDeviceForceVisible(id, false);
        }

        /// <summary>
        /// 托盘 tooltip 只有 63 字符（NotifyIcon.Text 硬限制），
        /// 所以按“最需要关注”排序：低电量优先，其次充电中，最后才按名称。
        /// 剩余的设备用数量概括，避免关键信息被截掉。
        /// </summary>
        private string BuildAggregateTooltip()
        {
            List<DeviceState> connected = DeviceVisibility.ConnectedDevices(_snapshot);
            if (connected.Count == 0)
            {
                int hiddenEmpty = HiddenDeviceCount();
                if (!_service.HasDiscovered)
                {
                    return "VoltPick：正在检测…";
                }
                return hiddenEmpty > 0
                    ? "VoltPick：未检测到可读取电量的设备 · " + hiddenEmpty + " 台已记录"
                    : "VoltPick：未检测到可读取电量的设备";
            }

            List<DeviceState> sorted = new List<DeviceState>(connected);
            sorted.Sort(CompareTooltipPriority);

            // 第一台设备优先完整显示：它是当前最需要看的。
            StringBuilder builder = new StringBuilder();
            DeviceState first = sorted[0];
            builder.Append(first.DisplayName);
            builder.Append(" ");
            builder.Append(DescribeForTooltip(first));

            int shown = 1;
            // 在剩余预算内尽量再放进 1 台，剩余用计数概括。
            for (int i = 1; i < sorted.Count; i++)
            {
                DeviceState device = sorted[i];
                string piece = " · " + device.DisplayName + " " + DescribeForTooltip(device);
                int remaining = sorted.Count - shown - 1;
                string tail = remaining > 0 ? " · 另 " + remaining + " 台" : "";
                if (builder.Length + piece.Length + tail.Length > 63)
                {
                    break;
                }
                builder.Append(piece);
                shown++;
            }

            int left = sorted.Count - shown;
            if (left > 0)
            {
                string tail = " · 另 " + left + " 台";
                if (builder.Length + tail.Length <= 63)
                {
                    builder.Append(tail);
                }
            }
            return builder.ToString();
        }

        /// <summary>低电量优先，其次异常状态，最后按类别固定顺序。</summary>
        internal static int CompareTooltipPriority(DeviceState a, DeviceState b)
        {
            bool aValue = a != null && a.HasValue;
            bool bValue = b != null && b.HasValue;
            if (aValue != bValue) return aValue ? -1 : 1;
            if (aValue && bValue)
            {
                int pa = a.PercentOrZero;
                int pb = b.PercentOrZero;
                if (pa != pb) return pa.CompareTo(pb);
            }
            int ca = (int)(a == null ? DeviceCategory.Unknown : a.Category);
            int cb = (int)(b == null ? DeviceCategory.Unknown : b.Category);
            return ca.CompareTo(cb);
        }

        /// <summary>单个设备在 tooltip 里的简短状态，尽量省字符。</summary>
        internal static string DescribeForTooltip(DeviceState device)
        {
            if (device == null)
            {
                return "未知";
            }
            if (!device.HasValue)
            {
                return device.StatusText;
            }
            string text = device.PercentOrZero + "%";
            if (device.Status == DeviceStatus.Charging) return text + " 充电";
            if (device.Status == DeviceStatus.Sleeping) return text + " 休眠";
            if (device.Status == DeviceStatus.Stale) return text + " 过期";
            return text;
        }

        private static DeviceState FindDevice(BatterySnapshot snapshot, string id)
        {
            if (snapshot == null || string.IsNullOrEmpty(id))
            {
                return null;
            }
            for (int i = 0; i < snapshot.Devices.Count; i++)
            {
                if (string.Equals(snapshot.Devices[i].Id, id, StringComparison.Ordinal))
                {
                    return snapshot.Devices[i];
                }
            }
            return null;
        }

        private string BuildFooterStatus()
        {
            string bluetooth;
            if (!_snapshot.BluetoothRadioKnown)
            {
                bluetooth = "蓝牙状态未知";
            }
            else
            {
                bluetooth = _snapshot.BluetoothRadioOn ? "蓝牙：开" : "蓝牙：关";
            }
            int hidden = HiddenDeviceCount();
            if (hidden <= 0)
            {
                return bluetooth;
            }
            return bluetooth + " · " + hidden + " 台已隐藏";
        }

        private void SetIconFor(NotifyIcon target, string text, Color color, bool dim, bool charging)
        {
            string key = _iconSize + "|" + text + "|" + color.ToArgb() + "|" + dim + "|" +
                         charging + "|" + AppSettings.Current.DigitPercent + "|" + AppSettings.Current.IconStyle +
                         "|" + AppSettings.Current.IconFont;
            if (string.Equals(target.Tag as string, key, StringComparison.Ordinal))
            {
                return;
            }

            Icon icon = IconFactory.BuildDigits(_iconSize, text, color, dim, AppSettings.Current.DigitPercent,
                charging, AppSettings.Current.IconStyle, AppSettings.Current.IconFont);
            Icon previous = target.Icon;
            target.Icon = icon;
            target.Tag = key;
            if (previous != null)
            {
                try { previous.Dispose(); } catch (Exception) { }
            }
        }

        private void UpdateMenu()
        {
            _autoStartItem.Text = AutoStart.IsEnabled() ? "自启动：开" : "自启动：关";
            List<DeviceState> connected = DeviceVisibility.ConnectedDevices(_snapshot);
            int hiddenCount = HiddenDeviceCount();
            if (connected.Count == 0)
            {
                if (!_service.HasDiscovered)
                {
                    _statusItem.Text = "正在检测设备…";
                }
                else if (hiddenCount > 0)
                {
                    _statusItem.Text = "未检测到可读取电量的设备 · " + hiddenCount + " 台已记录（设置 → 设备）";
                }
                else
                {
                    _statusItem.Text = "未检测到可读取电量的设备";
                }
            }
            else
            {
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < connected.Count; i++)
                {
                    DeviceState device = connected[i];
                    if (i > 0) builder.Append("   ");
                    builder.Append(device.DisplayName);
                    builder.Append(" ");
                    builder.Append(device.HasValue ? device.PercentOrZero + "%" : device.StatusText);
                }
                if (hiddenCount > 0)
                {
                    builder.Append("   （另 ");
                    builder.Append(hiddenCount);
                    builder.Append(" 台已隐藏）");
                }
                _statusItem.Text = builder.ToString();
            }
            _traySourceItem.Text = "状态栏显示：" + TraySourceSummary();
        }

        private string TraySourceSummary()
        {
            string id = AppSettings.Current.TrayDeviceId;
            if (!string.IsNullOrEmpty(id))
            {
                DeviceState selected = FindDevice(_snapshot, id);
                if (selected != null && DeviceVisibility.IsDisplayable(selected))
                {
                    return selected.DisplayName;
                }
                return "所选设备不可显示（自动回退）";
            }
            if (AppSettings.Current.IconDevice == "mouse") return "固定鼠标";
            if (AppSettings.Current.IconDevice == "keyboard") return "固定键盘";
            if (AppSettings.Current.IconDevice == "headset") return "固定耳机";
            return "自动（最低电量）";
        }

        private void RebuildTraySourceMenu()
        {
            _traySourceItem.DropDownItems.Clear();
            ToolStripMenuItem auto = new ToolStripMenuItem("自动（最低电量）");
            auto.Checked = string.IsNullOrEmpty(AppSettings.Current.TrayDeviceId) &&
                           AppSettings.Current.IconDevice == "auto";
            auto.Click += delegate { SetTraySource("", "auto"); };
            _traySourceItem.DropDownItems.Add(auto);

            ToolStripMenuItem mouse = new ToolStripMenuItem("固定鼠标");
            mouse.Checked = string.IsNullOrEmpty(AppSettings.Current.TrayDeviceId) &&
                            AppSettings.Current.IconDevice == "mouse";
            mouse.Click += delegate { SetTraySource("", "mouse"); };
            _traySourceItem.DropDownItems.Add(mouse);

            ToolStripMenuItem keyboard = new ToolStripMenuItem("固定键盘");
            keyboard.Checked = string.IsNullOrEmpty(AppSettings.Current.TrayDeviceId) &&
                               AppSettings.Current.IconDevice == "keyboard";
            keyboard.Click += delegate { SetTraySource("", "keyboard"); };
            _traySourceItem.DropDownItems.Add(keyboard);

            ToolStripMenuItem headset = new ToolStripMenuItem("固定耳机");
            headset.Checked = string.IsNullOrEmpty(AppSettings.Current.TrayDeviceId) &&
                              AppSettings.Current.IconDevice == "headset";
            headset.Click += delegate { SetTraySource("", "headset"); };
            _traySourceItem.DropDownItems.Add(headset);

            List<DeviceState> connected = DeviceVisibility.ConnectedDevices(_snapshot);
            if (connected.Count > 0)
            {
                _traySourceItem.DropDownItems.Add(new ToolStripSeparator());
                for (int i = 0; i < connected.Count; i++)
                {
                    DeviceState device = connected[i];
                    ToolStripMenuItem item = new ToolStripMenuItem(
                        device.DisplayName + (device.HasValue ? "  " + device.PercentOrZero + "%" : ""));
                    item.Checked = string.Equals(AppSettings.Current.TrayDeviceId, device.Id,
                        StringComparison.Ordinal);
                    string id = device.Id;
                    item.Click += delegate { SetTraySource(id, "auto"); };
                    _traySourceItem.DropDownItems.Add(item);
                }
            }

            ThemeMenu.Apply(_menu);
        }

        private void SetTraySource(string deviceId, string category)
        {
            AppSettings.Current.TrayDeviceId = deviceId ?? "";
            AppSettings.Current.IconDevice = string.IsNullOrEmpty(category) ? "auto" : category;
            try { AppSettings.Save(); } catch (Exception) { }
            RefreshUi();
        }

        private void OnTrayMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (_panel.Visible)
                {
                    HidePanel();
                }
                else
                {
                    ShowPanel();
                }
            }
        }

        private void OnBalloonTipClicked(object sender, EventArgs e)
        {
            if (_openDiagnosticsOnBalloon)
            {
                _openDiagnosticsOnBalloon = false;
                ShowDiagnostics();
                return;
            }
            ShowPanel();
        }

        private void ShowPanel()
        {
            _snapshot = _service.GetSnapshot();
            _panel.SetSnapshot(DeviceVisibility.ConnectedSnapshot(_snapshot));
            Log.Info("面板设备：" + DescribePanelDevices(_snapshot));
            _panel.SetFooterStatus(BuildFooterStatus());
            _panel.ShowNearCursor();
            SetUiInterval(1000);
            _service.SetPanelOpen(true);
        }

        /// <summary>
        /// 面板打开时记一行诊断：每台设备是否可见、有没有电量。
        /// 「面板只显示 3 台」这类反馈只能靠这行日志分清是过滤规则还是抓取没完成。
        /// </summary>
        private static string DescribePanelDevices(BatterySnapshot snapshot)
        {
            if (snapshot == null || snapshot.Devices.Count == 0)
            {
                return "0 台";
            }
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            for (int i = 0; i < snapshot.Devices.Count; i++)
            {
                DeviceState device = snapshot.Devices[i];
                if (device == null)
                {
                    continue;
                }
                if (builder.Length > 0)
                {
                    builder.Append("；");
                }
                builder.Append(device.DisplayName);
                builder.Append(DeviceVisibility.IsDisplayable(device) ? " 显示" : " 隐藏");
                builder.Append(device.HasValue ? " 有电量" : " 无电量");
            }
            return builder.ToString();
        }

        private void HidePanel()
        {
            _panel.HidePanel();
            _service.SetPanelOpen(false);
        }

        private void RequestRefresh()
        {
            _service.RequestRefreshAll();
            _panel.SetRefreshing(true);
            _refreshUntil = DateTime.Now.AddSeconds(3);
            SetUiInterval(1000);
        }

        private void ShowLowBattery(LowBatteryEventArgs e)
        {
            if (AppSettings.IsInDoNotDisturb())
            {
                return;
            }
            if (AppSettings.Current.SuppressFullscreen && Native.IsFullscreenAppActive())
            {
                return;
            }

            NotifyIcon icon = GetBalloonIcon();
            if (icon == null)
            {
                return;
            }
            string text = string.Format("{0} 剩余 {1}%。{2}", e.Device.DisplayName, e.Percent,
                e.Level <= 10 ? "很快就没电了，建议立刻充电。" : "记得找时间充电。");
            try
            {
                icon.ShowBalloonTip(8000, "设备电量低", text,
                    e.Level <= 10 ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Log.Warn("低电通知失败：" + ex.Message);
            }
        }

        private NotifyIcon GetBalloonIcon()
        {
            if (_tray.Visible)
            {
                return _tray;
            }
            for (int i = 0; i < _separate.Count; i++)
            {
                if (_separate[i].Visible)
                {
                    return _separate[i];
                }
            }
            return _tray;
        }

        private void ShowSettings()
        {
            // 设置是独立窗口，不再借着面板展开：
            // 面板要继续留在桌面右下角显示电量，不能跟着设置一起被拉大、挪位置。
            if (_settingsForm != null && !_settingsForm.IsDisposed)
            {
                if (_settingsForm.WindowState == FormWindowState.Minimized)
                {
                    _settingsForm.WindowState = FormWindowState.Normal;
                }
                _settingsForm.Activate();
                return;
            }
            // 面板继续留在桌面右下角：没显示就按记忆的位置显示出来，并钉住不让它被收走。
            _panel.SetSnapshot(DeviceVisibility.ConnectedSnapshot(_service.GetSnapshot()));
            _panel.SetFooterStatus(BuildFooterStatus());
            _panel.ShowNearCursor();
            _panel.SetPinned(true);
            SetUiInterval(1000);
            _service.SetPanelOpen(true);

            SettingsForm form = new SettingsForm(_service.GetSnapshot());
            form.Applied += delegate
            {
                ApplySettingsChanged(false);
                if (form.NeedsImmediateRefresh)
                {
                    _service.RequestRefreshAll();
                }
            };
            form.FormClosed += delegate
            {
                _settingsForm = null;
                _panel.SetPinned(false);
                // 面板可能开着，重新取一次快照，让卡片上的数值/名称跟上新设置。
                try { _panel.SetSnapshot(DeviceVisibility.ConnectedSnapshot(_service.GetSnapshot())); } catch (Exception) { }
            };
            _settingsForm = form;
            form.Show();
        }

        private void ApplySettingsChanged(bool refreshNow)
        {
            Theme.Apply(AppSettings.Current.ThemeMode);
            ThemeMenu.Apply(_menu);
            _panel.ApplyTheme();
            _iconSize = ResolveIconSize();
            _tray.Tag = null;
            for (int i = 0; i < _separate.Count; i++)
            {
                _separate[i].Tag = null;
            }
            RefreshUi();
            if (refreshNow)
            {
                _service.RequestRefreshAll();
            }
        }

        private void ShowDiagnostics()
        {
            if (!_panel.Visible)
            {
                _panel.ShowNearCursor();
            }
            if (_panel.EmbeddedViewActive)
            {
                _panel.CloseEmbeddedView();
            }
            BatterySnapshot snapshot = _service.GetSnapshot();
            DiagnosticsForm form = new DiagnosticsForm(snapshot);
            form.FormClosed += delegate { _panel.CloseEmbeddedView(); };
            _panel.ShowEmbeddedView("诊断", form);
        }

        private void ShowHistory()
        {
            if (_historyForm != null && !_historyForm.IsDisposed)
            {
                if (_historyForm.WindowState == FormWindowState.Minimized)
                {
                    _historyForm.WindowState = FormWindowState.Normal;
                }
                _historyForm.Activate();
                return;
            }
            if (!_panel.Visible)
            {
                _panel.ShowNearCursor();
            }
            if (_panel.EmbeddedViewActive)
            {
                _panel.CloseEmbeddedView();
            }
            DeviceState selected = ChooseAggregateDevice();
            HistoryForm form = new HistoryForm(selected == null ? null : selected.Id);
            form.FormClosed += delegate
            {
                _historyForm = null;
                _panel.CloseEmbeddedView();
            };
            _historyForm = form;
            _panel.ShowEmbeddedView("历史", form);
        }

        private void ShowHelp()
        {
            // 说明文档只有 README.md 一份；文件缺失时用内置文本兜底。
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "README.md");
            if (File.Exists(path))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn("打开使用说明失败：" + ex.Message);
                }
            }

            MessageBox.Show(
                "左键托盘图标：打开或关闭设备面板。\n" +
                "右键托盘图标：刷新、设置、诊断、自启动、退出。\n\n" +
                "程序读取设备自己上报的电量：鼠标和键盘优先用标准 HID 电量接口，蓝牙设备用标准电池服务，部分游戏外设的厂商私有协议也能读取。\n" +
                "读不到电量的设备会说明原因（例如 2.4G 接收器不传输电量、有线键盘没有电量接口），不会显示猜测值。\n\n" +
                "如果任务栏上没有图标，请在 Windows 11 的“任务栏设置 → 其他系统托盘图标”中打开“VoltPick”。",
                "VoltPick", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OpenBluetoothSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开蓝牙设置，请手动打开 Windows 设置 → 蓝牙和设备。\n\n" + ex.Message,
                    "VoltPick", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ToggleAutoStart()
        {
            bool enable = !AutoStart.IsEnabled();
            string error;
            if (!AutoStart.SetEnabled(enable, out error))
            {
                MessageBox.Show("设置开机自启动失败。\n\n" + (error ?? ""),
                    "VoltPick", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            UpdateMenu();
        }

        private void OnStartupTick()
        {
            bool abnormalShown = false;
            if (!string.IsNullOrEmpty(_abnormalExit))
            {
                Log.Warn("检测到上次异常退出：" + _abnormalExit);
                ShowBalloon("上次程序未正常退出",
                    _abnormalExit + "。点击这个气泡可以查看诊断信息。",
                    ToolTipIcon.Warning);
                _openDiagnosticsOnBalloon = true;
                _abnormalExit = null;
                abnormalShown = true;
            }

            if (!abnormalShown && !AppSettings.Current.FirstRunHintShown)
            {
                ShowBalloon("VoltPick已启动",
                    "如果任务栏里没有图标，打开“任务栏设置 → 其他系统托盘图标”，把「VoltPick」打开即可。",
                    ToolTipIcon.Info);
                AppSettings.Current.FirstRunHintShown = true;
                AppSettings.Save();
            }

            if (Program.ShowSettingsOnStart)
            {
                Program.ShowSettingsOnStart = false;
                ShowSettings();
            }
            else if (Program.ShowPanelOnStart || AppSettings.Current.StartWithPanel)
            {
                Program.ShowPanelOnStart = false;
                ShowPanel();
            }

        }

        /// <summary>
        /// 按当前设置重新注册热键。silent 为 true 时不弹提示（启动阶段使用）。
        /// 返回是否注册成功，供设置页判断组合键是否被占用。
        /// </summary>
        private void ShowBalloon(string title, string text, ToolTipIcon icon)
        {
            try
            {
                NotifyIcon target = GetBalloonIcon();
                if (target != null)
                {
                    target.ShowBalloonTip(8000, title, text, icon);
                }
            }
            catch (Exception)
            {
            }
        }

        private void OnDeviceChanged(object sender, EventArgs e)
        {
            if (_deviceTimer.Enabled)
            {
                _deviceTimer.Stop();
            }
            _deviceTimer.Start();
        }

        /// <summary>
        /// SystemEvents 的回调跑在系统事件线程上，不是 UI 线程；而 _uiTimer、_tray、_panel
        /// 全是 UI 线程对象。在这里统一切回 UI 线程再动，否则 Stop()/Start() 与托盘图标重绘
        /// 会抛 InvalidOperationException，或者把 GDI 句柄踩坏（表现为图标变黑/消失）。
        /// 句柄还没建好时不排队（排进去也没人处理），直接就地执行，外层都有 try/catch。
        /// </summary>
        private void RunOnUiThread(Action action)
        {
            try
            {
                if (_panel != null && _panel.IsHandleCreated && _panel.InvokeRequired)
                {
                    _panel.BeginInvoke(action);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("切回 UI 线程失败：" + ex.Message);
                return;
            }
            action();
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Suspend && e.Mode != PowerModes.Resume)
            {
                return;
            }
            bool suspend = e.Mode == PowerModes.Suspend;
            RunOnUiThread(delegate
            {
                try
                {
                    if (suspend)
                    {
                        _uiTimer.Stop();
                        return;
                    }
                    _uiTimer.Interval = 1000;
                    _uiTimer.Start();
                    _service.OnPowerResume();
                    RefreshUi();
                }
                catch (Exception ex)
                {
                    Log.Warn("电源状态变化处理失败：" + ex.Message);
                }
            });
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            // Windows 注销/关机是正常结束，不应当成异常退出。
            RunSession.MarkCleanExit();
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            // 改缩放 / 换屏时 SystemEvents 在自己的线程上回调，图标尺寸和面板坐标必须回 UI 线程再算。
            RunOnUiThread(delegate
            {
                try
                {
                    _iconSize = ResolveIconSize();
                    _tray.Tag = null;
                    for (int i = 0; i < _separate.Count; i++)
                    {
                        _separate[i].Tag = null;
                    }
                    // 换屏/改缩放后，面板的绝对坐标要按新 DPI 重算，否则会停在旧尺寸上被裁切。
                    _panel.RefreshDpiLayout();
                    RefreshUi();
                }
                catch (Exception ex)
                {
                    Log.Warn("显示设置变化处理失败：" + ex.Message);
                }
            });
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            try
            {
                // SystemEvents 可能从系统事件线程触发，主题和图标必须回到 UI 线程再改。
                if (_panel.IsHandleCreated && _panel.InvokeRequired)
                {
                    _panel.BeginInvoke(new Action<object, UserPreferenceChangedEventArgs>(
                        OnUserPreferenceChanged), sender, e);
                    return;
                }
                if (!Theme.ApplyIfSystemChanged())
                {
                    return;
                }
                ThemeMenu.Apply(_menu);
                _panel.ApplyTheme();
                _tray.Tag = null;
                for (int i = 0; i < _separate.Count; i++)
                {
                    _separate[i].Tag = null;
                }
                RefreshUi();
            }
            catch (Exception)
            {
            }
        }

        private static bool IsStartedByAutostart()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 1; i < args.Length; i++)
                {
                    if (string.Equals(args[i], AutoStart.LaunchArgument, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        private static int ResolveIconSize()
        {
            int width = SystemInformation.SmallIconSize.Width;
            if (width < 12) width = 16;
            if (width > 64) width = 64;
            return width;
        }

        private static string Clip(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.Length <= max) return value;
            return value.Substring(0, max);
        }

        private void ExitApp()
        {
            try
            {
                _service.Changed -= OnServiceChanged;
                _service.LowBattery -= OnLowBattery;
                _service.Dispose();
            }
            catch (Exception)
            {
            }
            try { AppSettings.FlushKnownDevices(); } catch (Exception) { }

            try
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                SystemEvents.SessionEnding -= OnSessionEnding;
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            }
            catch (Exception)
            {
            }

            _uiTimer.Stop();
            _uiTimer.Dispose();
            _deviceTimer.Stop();
            _deviceTimer.Dispose();
            _startupTimer.Stop();
            _startupTimer.Dispose();
            _host.DeviceChanged -= OnDeviceChanged;

            DisposeSeparateIcons();
            if (_settingsForm != null && !_settingsForm.IsDisposed)
            {
                try { _settingsForm.Close(); } catch (Exception) { }
                _settingsForm = null;
            }
            _tray.Visible = false;
            _tray.Dispose();
            if (_historyForm != null && !_historyForm.IsDisposed)
            {
                try { _historyForm.Close(); } catch (Exception) { }
                _historyForm = null;
            }
            // 退出时只关不释放：进程马上就要结束，而正在关闭中的下拉一释放就会再撞
            // ModalMenuFilter 的延迟关闭（ObjectDisposedException），没必要冒这个险。
            if (_unhideMenu != null)
            {
                try
                {
                    if (_unhideMenu.Visible)
                    {
                        _unhideMenu.Close();
                    }
                }
                catch (Exception)
                {
                }
                _unhideMenu = null;
            }
            _retiredMenus.Clear();
            _panel.HidePanel();
            _panel.Dispose();
            _host.Close();
            _host.Dispose();
            SingleInstance.Stop();
            RunSession.MarkCleanExit();
            ExitThread();
        }
    }
}
