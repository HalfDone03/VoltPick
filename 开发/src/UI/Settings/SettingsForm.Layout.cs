using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeviceBattery
{
    internal sealed partial class SettingsForm : Form
    {

        // ---------------- 排版 ----------------

        private void LayoutPages()
        {
            if (_tabs == null || _layingOut)
            {
                return;
            }
            _layingOut = true;
            try
            {
                float f = UiScale.RawFactor(this);
                // 页签宽度每次重排都按当前字号与 DPI 实测一遍（DarkTabControl 自己在 DPI 变化时会先重置）。
                ApplyTabItemSize(f);
                int side = UiScale.Scale(12, f);
                int gap = UiScale.Scale(10, f);
                int labelColumn = MeasureLabelColumn(f);
                int reserve = UiScale.Scale(16, f);

                // 页面宽度以标签控件为准：TabPage 在没显示过的时候 ClientSize 还是 0，
                // 按它算出来的可用宽度会掉到下限，按钮被压成 48px、文字整个被切掉。
                int pageWidth = _tabs.DisplayRectangle.Width;
                if (pageWidth < UiScale.Scale(200, f))
                {
                    pageWidth = ClientSize.Width - side * 2 - reserve;
                }
                int pageHeight = _tabs.DisplayRectangle.Height;
                int width = pageWidth - side * 2 - reserve;
                if (width < UiScale.Scale(180, f))
                {
                    width = UiScale.Scale(180, f);
                }

                int tallest = 0;
                int currentHeight = 0;
                int selected = _tabs.SelectedIndex < 0 ? 0 : _tabs.SelectedIndex;
                for (int i = 0; i < _tabs.TabPages.Count; i++)
                {
                    TabPage page = _tabs.TabPages[i];
                    int used = LayoutOnePage(page, i, width, f, side, gap, labelColumn, pageHeight);
                    if (i == selected)
                    {
                        currentHeight = used;
                    }
                    if (used > tallest)
                    {
                        tallest = used;
                    }
                    // 页面不再用 AutoScroll：系统滚动条在深色主题下是浅灰的，和卡片对不上。
                    // 内容高度交给 EnsureWindowHeight 撑开窗口，装不下时只往上长、不出现原生滚动条。
                    page.AutoScroll = false;
                }
                // 窗口按「当前页内容」定高：常规页内容少就开个小窗口，
                // 不为了最长页把窗口撑得很大；切到长页时窗口长高，超屏部分页内滚动。
                EnsureWindowHeight(currentHeight > 0 ? currentHeight : tallest, pageHeight, f, side);
                // 窗口高度稳定后，用共享视口统一设置所有页的滚动容器。
                SizeAllScrollHosts(f, side);
                UpdateNextRead();
            }
            finally
            {
                _layingOut = false;
            }
        }

        /// <summary>
        /// 正文放大后内容可能比默认窗口高，按最高的页面把窗口撑到刚好看得下底部那排按钮；
        /// 屏高不够就交给页面自己的滚动条，按钮始终留在窗口里。
        /// </summary>
        private void EnsureWindowHeight(int contentHeight, int pageHeight, float f, int side)
        {
            int chrome = _tabs.DisplayRectangle.Y;
            int bottom = _bottom == null ? UiScale.Scale(52, f) : _bottom.Height;
            // 页面不用原生滚动条，不再预留 HorizontalScrollBarHeight（原来白占 17px）。
            // 窗口不要跟着最长页无限长高（否则关于页能把窗口顶出屏幕）。
            // 上限 720 设计像素：常规页能完整放下（含说明换行），更长页页内滚动。
            int limit = UiScale.Scale(720, f);
            int screenLimit = Screen.FromControl(this).WorkingArea.Height - UiScale.Scale(80, f);
            if (limit > screenLimit) limit = screenLimit;
            MinimumSize = new Size(UiScale.Scale(520, f), UiScale.Scale(330, f));

            // 用户刚拖过窗口：800ms 内不自动改高，避免和拖拽打架。
            if (_userSized && (DateTime.Now - _lastUserResize).TotalMilliseconds < 800)
            {
                return;
            }

            int wanted = chrome + contentHeight + side * 2 + bottom;
            if (wanted > limit)
            {
                wanted = limit;
            }
            int minimum = chrome + UiScale.Scale(220, f) + bottom;
            if (wanted < minimum)
            {
                wanted = minimum;
            }

            // 目标没变就不动窗口，避免 Resize -> LayoutPages 互相抖动。
            if (wanted != _sizedHeight)
            {
                _sizedHeight = wanted;
                if (wanted != ClientSize.Height)
                {
                    _fitting = true;
                    try
                    {
                        ClientSize = new Size(ClientSize.Width, wanted);
                    }
                    finally
                    {
                        _fitting = false;
                    }
                    ScheduleRelayout();
                }
            }
        }

        /// <summary>
        /// 延后一次重排：改完窗口高度后，可视区尺寸要下一轮消息才稳定，
        /// 立刻重排拿到的是旧值（支持页滚动框会停在旧高度）。
        /// </summary>
        private void ScheduleRelayout()
        {
            if (!IsHandleCreated || IsDisposed)
            {
                return;
            }
            try
            {
                BeginInvoke(new MethodInvoker(delegate { LayoutPages(); }));
            }
            catch (InvalidOperationException)
            {
            }
        }

        /// <summary>
        /// 按当前字号实测左侧标签列：字号调大（或换字体）时列宽跟着变，
        /// 标签不会被压窄切掉最后一个字。上限避免超长说明把字段挤没。
        /// </summary>
        private int MeasureLabelColumn(float f)
        {
            int widest = 0;
            int limit = UiScale.Scale(200, f);
            for (int i = 0; i < _tabs.TabPages.Count; i++)
            {
                TabPage page = _tabs.TabPages[i];
                for (int s = 0; s < page.Controls.Count; s++)
                {
                    SettingsSection section = page.Controls[s] as SettingsSection;
                    if (section == null)
                    {
                        continue;
                    }
                    for (int c = 0; c < section.Controls.Count; c++)
                    {
                        Label label = section.Controls[c] as Label;
                        if (label == null || string.Equals(label.Tag as string, "single"))
                        {
                            continue;
                        }
                        // 只量「字段标签」这一列。说明文字（RoleMuted）是整段的自动换行文本，
                        // 拿它去量会把列宽顶到上限，输入框和列表平白少掉一百多像素。
                        int role;
                        if (!_labelRoles.TryGetValue(label, out role) || role != RoleText)
                        {
                            continue;
                        }
                        Size need = TextRenderer.MeasureText(label.Text, label.Font,
                            new Size(int.MaxValue, 0), TextFormatFlags.NoPadding);
                        if (need.Width > widest)
                        {
                            widest = need.Width;
                        }
                    }
                }
            }
            int minimum = UiScale.Scale(96, f);
            if (widest <= 0)
            {
                return minimum;
            }
            return Math.Min(limit, Math.Max(minimum, widest + UiScale.Scale(8, f)));
        }

        /// <summary>
        /// 排一个页面：分组按固有高度铺到内容面板里，再交给本页的滚动容器显示。
        /// 返回内容固有高度，供窗口按当前页定高（受屏幕上限约束）。
        /// </summary>
        private int LayoutOnePage(TabPage page, int pageIndex, int width, float f,
            int side, int gap, int labelColumn, int viewport)
        {
            if (width < UiScale.Scale(180, f))
            {
                width = UiScale.Scale(180, f);
            }
            int top = PageHeaderHeight(f) + UiScale.Scale(8, f);
            List<SettingsSection> sections = SectionsOf(pageIndex);
            int count = sections.Count;
            int[] heights = new int[count];

            // 设备页先把列表复原成基准高度再量，避免上次排版喂大的高度被当成内容高。
            if (pageIndex == 3 && _deviceList != null && _deviceListBaseHeight > 0)
            {
                _deviceList.Height = _deviceListBaseHeight;
            }
            for (int s = 0; s < count; s++)
            {
                heights[s] = sections[s].ApplyLayout(width, f, labelColumn);
            }

            // 设备页：可视区有剩余时把列表拉高填满；上限 520，超出部分照常滚动。
            if (pageIndex == 3 && _deviceList != null && count > 0)
            {
                int used = top;
                for (int s = 0; s < count; s++) used += heights[s] + (s > 0 ? gap : 0);
                int spare = viewport - used - UiScale.Scale(16, f);
                if (spare > UiScale.Scale(16, f))
                {
                    int grown = _deviceList.Height + spare;
                    int cap = UiScale.Scale(520, f);
                    _deviceList.Height = grown > cap ? cap : grown;
                    heights[0] = sections[0].ApplyLayout(width, f, labelColumn);
                }
            }

            int intrinsic = UiScale.Scale(8, f);
            for (int s = 0; s < count; s++)
            {
                intrinsic += heights[s] + (s > 0 ? gap : 0);
            }
            intrinsic += UiScale.Scale(12, f);

            if (_pageContent != null && pageIndex < _pageContent.Length && _pageContent[pageIndex] != null)
            {
                Panel content = _pageContent[pageIndex];
                int y = UiScale.Scale(4, f);
                for (int s = 0; s < count; s++)
                {
                    sections[s].Location = new Point(0, y);   // 相对内容面板，不再加 side
                    y += heights[s] + gap;
                }
                content.SetBounds(0, 0, width, Math.Max(UiScale.Scale(120, f), y));
            }
            // 滚动容器尺寸在窗口高度定下来之后统一设置（见 SizeAllScrollHosts），
            // 这里只排内容，避免用到还没稳定的 TabPage.ClientSize。
            return top + intrinsic + UiScale.Scale(12, f);
        }

        /// <summary>
        /// 窗口高度定下来后，用 TabControl 的共享视口统一设置每一页的滚动容器。
        /// 各页尺寸一致，切页不跳动；内容高于视口时滚动条出现，否则隐藏。
        /// </summary>
        private void SizeAllScrollHosts(float f, int side)
        {
            if (_pageScroll == null) return;
            int top = PageHeaderHeight(f) + UiScale.Scale(8, f);
            Rectangle viewport = _tabs.DisplayRectangle;
            int hostWidth = Math.Max(UiScale.Scale(180, f), viewport.Width - side * 2);
            int hostHeight = Math.Max(UiScale.Scale(140, f), viewport.Height - top - UiScale.Scale(12, f));
            for (int i = 0; i < _pageScroll.Length; i++)
            {
                ScrollHost host = _pageScroll[i];
                if (host == null) continue;
                host.SetBounds(side, top, hostWidth, hostHeight);
                // 内容面板按自己的实际高度（分组排完后已 SetBounds），滚动条据此判断是否需要。
                Panel content = i < _pageContent.Length ? _pageContent[i] : null;
                host.SetContentHeight(content == null ? 0 : content.Height);
                host.Invalidate();
            }
        }

        private List<SettingsSection> SectionsOf(int pageIndex)
        {
            switch (pageIndex)
            {
                case 0: return _general;   // 常规 = 原通用 + 原读取
                case 1: return _display;
                case 2: return _notify;
                case 3: return _devices;
                default: return _about;     // 关于 = 原关于 + 原支持
            }
        }
    }
}
