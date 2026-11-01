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
                for (int i = 0; i < _tabs.TabPages.Count; i++)
                {
                    TabPage page = _tabs.TabPages[i];
                    int used = LayoutOnePage(page, i, width, f, side, gap, labelColumn, pageHeight);
                    if (used > tallest)
                    {
                        tallest = used;
                    }
                    // 页面不再用 AutoScroll：系统滚动条在深色主题下是浅灰的，和卡片对不上。
                    // 内容高度交给 EnsureWindowHeight 撑开窗口，装不下时只往上长、不出现原生滚动条。
                    page.AutoScroll = false;
                }
                // 正文放大后个别页面会比默认窗口高，按最高的页面把窗口撑一下，
                // 免得下面那排「取消 / 应用 / 确定」被窗口下沿切掉。
                // 窗口高度跟着当前页走：换页时按那一页的内容重新定高，页尾不留大块空白。
                EnsureWindowHeight(tallest, pageHeight, f, side);

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
            int extra = SystemInformation.HorizontalScrollBarHeight;
            int limit = Screen.FromControl(this).WorkingArea.Height - UiScale.Scale(120, f);
            // 无论是否自动定高，都允许用户把窗口拖小到内容需要滚动为止，而不锁死成内容尺寸。
            MinimumSize = new Size(UiScale.Scale(520, f), UiScale.Scale(330, f));
            if (_userSized)
            {
                // 用户拖过窗口：只在内容放不下时往上长，绝不自动缩回去。
                int growTo = chrome + contentHeight + side * 2 + bottom + extra;
                if (growTo > limit)
                {
                    growTo = limit;
                }
                if (growTo > ClientSize.Height)
                {
                    _fitting = true;
                    try
                    {
                        ClientSize = new Size(ClientSize.Width, growTo);
                    }
                    finally
                    {
                        _fitting = false;
                    }
                    // 窗口刚被我们自己改高时 Resize 里会直接 return（_fitting），
                    // 支持页的滚动框因此还按旧的可视高度算。这里补一次延迟重排，
                    // 让它按新的窗口高度重新铺满。
                    ScheduleRelayout();
                }
                return;
            }

            // 按最高的一页定窗口高度：比默认高就撑开、比默认矮就收一下，页尾不留大块空白。
            int wanted = chrome + contentHeight + side * 2 + bottom + extra;
            if (wanted > limit)
            {
                wanted = limit;
            }
            int floor = chrome + Math.Min(contentHeight, pageHeight) + side * 2 + bottom + extra;
            if (wanted < floor)
            {
                wanted = floor;
            }

            // 只在目标高度稳定变化时才动窗口，避免和 Resize -> LayoutPages 互相抖动。
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

            // 最小尺寸固定成一个小下限：窗口跟着内容变高变矮，同时允许用户拖小到内容滚动。
            MinimumSize = new Size(UiScale.Scale(520, f), UiScale.Scale(330, f));
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

        private int LayoutOnePage(TabPage page, int pageIndex, int width, float f,
            int side, int gap, int labelColumn, int viewport)
        {
            if (width < UiScale.Scale(180, f))
            {
                width = UiScale.Scale(180, f);
            }

            // 正文从页头下面开始排（页头是 TabPage 自己画出来的标题 + 一句话说明）。
            int top = PageHeaderHeight(f) + UiScale.Scale(8, f);
            List<SettingsSection> sections = SectionsOf(pageIndex);
            int count = sections.Count;
            int[] heights = new int[count];
            int contentHeight = top;
            // 设备页先把列表复原成基准高度再量，否则上一次排版喂大的高度会被当成内容高，
            // 一轮一轮往上追涨。
            if (pageIndex == 3 && _deviceList != null && _deviceListBaseHeight > 0)
            {
                if (_deviceList.Height != _deviceListBaseHeight)
                {
                    _deviceList.Height = _deviceListBaseHeight;
                }
            }
            for (int s = 0; s < count; s++)
            {
                heights[s] = sections[s].ApplyLayout(width, f, labelColumn);
                contentHeight += heights[s] + (s > 0 ? gap : 0);
            }

            // 按基准高度先算出「内容本身」的高度，窗口高度只认这个值；
            // 后面为了撑满页面而把列表拉高，不会反过来把窗口撑高。
            int intrinsic = top + UiScale.Scale(12, f);
            for (int s = 0; s < count; s++)
            {
                intrinsic += heights[s] + (s > 0 ? gap : 0);
            }

            // 设备页：列表只有一条表头（没记录）或条数很少时，页面下半截会空着。
            // 把剩余高度补给列表，卡片撑满页面；列表有上限，条目多了照旧滚动。
            if (pageIndex == 3 && _deviceList != null && count > 0)
            {
                int spare = viewport - contentHeight - UiScale.Scale(16, f);
                if (spare > UiScale.Scale(16, f))
                {
                    int cap = UiScale.Scale(520, f);
                    int grown = _deviceList.Height + spare;
                    _deviceList.Height = grown > cap ? cap : grown;
                    heights[0] = sections[0].ApplyLayout(width, f, labelColumn);
                    contentHeight = top;
                    for (int s = 0; s < count; s++)
                    {
                        contentHeight += heights[s] + (s > 0 ? gap : 0);
                    }
                }
            }

            // 内容比可视区矮的时候，以前多出来的空间全堆在页面最下面，看起来像“没排完”。
            // 现在：五个页签共用同一个窗口高度，短页多出来的空间先均分到卡片间距
            // （每段最多 48px），剩下的再整体下沉一点，页尾不留一大片空白。
            int extra = 0;
            int shift = 0;
            int bottomPad = UiScale.Scale(12, f);
            int leftover = viewport - contentHeight - bottomPad;
            if (count > 1 && leftover > 0)
            {
                extra = Math.Min(UiScale.Scale(48, f), leftover / count);
                int rest = leftover - extra * count;
                if (rest > 0)
                {
                    shift = Math.Min(UiScale.Scale(12, f), rest / 2);
                }
            }
            else if (count == 1 && leftover > UiScale.Scale(60, f))
            {
                shift = Math.Min(UiScale.Scale(32, f), leftover / 3);
            }

            int y = top + shift;
            for (int s = 0; s < count; s++)
            {
                SettingsSection section = sections[s];
                section.Location = new Point(side, y);
                y += heights[s] + gap + extra;
            }

            // 返回「内容本身」的高度（不含为填满可视区加的空白、也不含补给列表的高度），
            // 窗口高度按它算，否则铺开空白会把窗口越撑越高，形成循环。
            return intrinsic;
        }

        private List<SettingsSection> SectionsOf(int pageIndex)
        {
            switch (pageIndex)
            {
                case 0: return _general;
                case 1: return _display;
                case 2: return _notify;
                case 3: return _devices;
                default: return _about;
            }
        }
    }
}
