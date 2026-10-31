using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DeviceBattery
{
    /// <summary>
    /// 设置页里的一个白底分组：一条标题 + 若干行控件。
    ///
    /// 这里刻意**不做任何自绘容器的花样**：
    /// 背景就是普通 Panel 的 BackColor（不透明），只额外画一条底部分隔线和标题文字。
    /// 之前那版把圆角卡片做成自绘 Panel + Region 裁剪，子控件（复选框/标签）的
    /// 透明背景没有父容器支持，被 WinForms 退化成黑色，整行变成黑条；
    /// 这一版所有控件都待在不透明背景上，不会再出现那种情况。
    ///
    /// 行高也不再自己用 TextRenderer 估算，而是给 Label 设 MaximumSize + AutoSize，
    /// 让 WinForms 用真正的绘制逻辑算高度——算出来多少就画多少，不会再压字或切字。
    /// </summary>
    internal sealed class SettingsSection : Panel
    {
        private readonly List<Control[]> _rows = new List<Control[]>();
        private readonly string _title;
        private Font _titleFont;
        /// <summary>
        /// 上一次排版用的缩放系数。绘制时必须和 ApplyLayout 用同一个系数，
        /// 否则标题小竖条和控件左边缘会差 1–2px（窗口整体缩放和 DPI 缩放不是一回事）。
        /// </summary>
        private float _factor = 1f;

        public SettingsSection(string title)
        {
            _title = title ?? "";
            BackColor = Theme.Card;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }


        /// <summary>加入一行：一个或多个控件横排（第一个通常是标签）。</summary>
        public void AddRow(params Control[] row)
        {
            if (row == null || row.Length == 0)
            {
                return;
            }
            _rows.Add(row);
            for (int i = 0; i < row.Length; i++)
            {
                if (row[i] != null && !Controls.Contains(row[i]))
                {
                    Controls.Add(row[i]);
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            EnsureTitleFont();
            if (_titleFont == null)
            {
                _titleFont = Theme.UiFont(Theme.FontTitle, FontStyle.Bold);
            }
            // 标题不再贴着卡片上沿：上边留一点气口，标题行独占一条，
            // 下面再画分隔线（原来标题顶在 0 像素处，卡片看着像没留边）。
            // 分组改成真正的卡片：圆角底 + 1px 描边，标题前加一条强调色小竖条。
            // 背景在 OnPaintBackground 里先铺成页面底色，圆角外的四个角才会是页面背景，
            // 而不是方方正正的卡片色（子控件仍继承不透明的 Theme.Card，不会被画成黑块）。
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle card = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath path = Surface.Rounded(this, card, 10))
            {
                using (SolidBrush fill = new SolidBrush(Theme.Card))
                {
                    g.FillPath(fill, path);
                }
                using (Pen border = new Pen(Theme.CardBorder, 1f))
                {
                    g.DrawPath(border, path);
                }
            }
            // 必须和 ApplyLayout 里的 padX 用同一个值（14）：标题小竖条和下面的控件
            // 共用同一条左边缘，之前一个 16 一个 14，标题看着比输入框多缩进 2px。
            int padX = UiScale.Scale(14, _factor);
            int titleTop = UiScale.Px(this, 11);
            // 标题高度按字体实测：写死 20px 时 13pt 粗体的下伸部（g、p 之类）会被裁掉一截。
            int titleH = _titleFont.Height + UiScale.Px(this, 2);
            int barW = UiScale.Px(this, 3);
            int barH = UiScale.Px(this, 14);
            using (SolidBrush accent = new SolidBrush(Theme.Blue))
            {
                g.FillRectangle(accent, padX, titleTop + (titleH - barH) / 2, barW, barH);
            }
            TextRenderer.DrawText(g, _title, _titleFont,
                new Rectangle(padX + barW + UiScale.Px(this, 8), titleTop,
                    Math.Max(0, Width - padX * 2 - barW), titleH),
                Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            // 给下拉框补一圈描边：FlatStyle.Flat 的 ComboBox 在深色卡片上几乎没有边框，
            // 看着像一段悬空文字。这里在控件外侧补 1px，颜色和卡片分隔线一致。
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            for (int i = 0; i < Controls.Count; i++)
            {
                ComboBox combo = Controls[i] as ComboBox;
                if (combo == null || !combo.Visible)
                {
                    continue;
                }
                using (Pen frame = new Pen(Theme.Divider, 1f))
                {
                    g.DrawRectangle(frame, combo.Left - 1, combo.Top - 1,
                        combo.Width + 1, combo.Height + 1);
                }
            }
        }

        private void EnsureTitleFont()
        {
            if (_titleFont == null)
            {
                _titleFont = Theme.UiFont(Theme.FontTitle, FontStyle.Bold);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 卡片外面的圆角要用页面底色，所以这里不能铺卡片色。
            e.Graphics.Clear(Theme.Background);
        }

        /// <summary>
        /// 排版：返回分组应有的高度。width 是可用总宽（含内边距），
        /// labelColumn 是左侧标签列的宽度（0 表示单列铺满）。
        /// </summary>
        public int ApplyLayout(int width, float factor, int labelColumn)
        {
            EnsureTitleFont();
            // 记下来给 OnPaint 用：排版和绘制必须是同一个系数，
            // 否则标题小竖条与控件左边缘差 1–2px（窗口整体缩放和 DPI 缩放不是一回事）。
            _factor = factor <= 0.1f ? 1f : factor;
            int padX = UiScale.Scale(14, _factor);
            // 标题区 = 上留白 + 标题实际字高 + 与正文的间距，保证粗体字底不被压住。
            int titleH = _titleFont.Height + UiScale.Scale(22, factor);
            int gapY = UiScale.Scale(8, factor);
            int gapX = UiScale.Scale(12, factor);
            int bottom = UiScale.Scale(12, factor);
            int inner = Math.Max(UiScale.Scale(120, factor), width - padX * 2);
            // 分组的宽度必须自己设：Panel 默认只有 200px 宽，不设的话
            // 白底只会铺到 200px，右边整片是页面底色，文字也被裁在边界上。
            Width = width;
            int y = titleH;

            for (int r = 0; r < _rows.Count; r++)
            {
                Control[] row = _rows[r];
                // 只有一个开关的行走字段列：和上面的下拉框同一个左边界。
                // 否则这些复选框贴在卡片最左边，跟下拉框错开一整列，看着很散。
                bool soloCheck = labelColumn > 0 && row.Length == 1
                    && (row[0] is CheckBox || row[0] is RadioButton);
                // 只有勾选框的行统一贴左边距缩进一格：之前按本卡标签列宽缩进，
                // 于是「全屏」这类没有标签的卡片会比别的卡片多缩进一截。
                int x = soloCheck ? padX + gapX : padX;
                int rowHeight = 0;
                int[] widths = new int[row.Length];
                int[] heights = new int[row.Length];

                // 统计这一行：按钮保持自己的宽度，剩下的空间才给输入框/下拉框。
                // 以前是把整行剩余宽度发给每一个字段，两个按钮各拿满宽，
                // 加起来越过右边被裁掉，只露出半个边框。
                int buttonWidthSum = 0;
                int flexCount = 0;
                int minField = UiScale.Scale(48, factor);
                // 这一行有没有左侧标签：没有标签的行（整行列表、单独说明）不该再空出标签列，
                // 否则列表平白少 90px，第三列「状态」就要靠横向滚动条才看得到。
                bool hasLabel = false;
                for (int i = 0; i < row.Length; i++)
                {
                    Label probeLabel = row[i] as Label;
                    if (probeLabel != null && !string.Equals(probeLabel.Tag as string, "single"))
                    {
                        hasLabel = true;
                        break;
                    }
                }
                for (int i = 0; i < row.Length; i++)
                {
                    Control item = row[i];
                    if (item == null || item is Label)
                    {
                        continue;
                    }
                    if (item is SurfaceButton)
                    {
                        buttonWidthSum += Math.Max(minField, item.Width);
                    }
                    else
                    {
                        flexCount++;
                    }
                }
                int gaps = Math.Max(0, row.Length - 1) * gapX;
                if (soloCheck)
                {
                    // 缩进占掉一格间距，宽度要跟着减，右边才不会顶出去。
                    gaps += gapX;
                }
                int fieldSpace = inner - (hasLabel && labelColumn > 0 ? labelColumn : 0) - gaps;
                int flexSpace = fieldSpace - buttonWidthSum;
                if (flexCount > 0 && flexSpace < minField * flexCount)
                {
                    flexSpace = minField * flexCount;
                }
                int perFlex = flexCount > 0 ? flexSpace / flexCount : 0;

                // 先算这一行每个控件的目标宽度
                for (int i = 0; i < row.Length; i++)
                {
                    Control c = row[i];
                    if (c == null)
                    {
                        continue;
                    }
                    if (c is Label)
                    {
                        widths[i] = row.Length == 1 ? inner : labelColumn;
                    }
                    else if (c is SurfaceButton)
                    {
                        // 按钮固定用自身宽度：既不会被拉成一大条，也不会被压窄切字。
                        widths[i] = Math.Min(c.Width, Math.Max(minField, fieldSpace));
                    }
                    else if (c is ComboBox)
                    {
                        // 下拉框不铺满整行：拉成一条长条看着空，右边留一点更像表单。
                        widths[i] = Math.Min(perFlex, UiScale.Scale(280, factor));
                    }
                    else
                    {
                        widths[i] = perFlex;
                    }

                    // 让 WinForms 自己算换行后的高度：先关掉 AutoSize 设上限，再打开
                    if (c is Label || c is CheckBox || c is RadioButton)
                    {
                        c.Height = 1;
                    }
                }

                for (int i = 0; i < row.Length; i++)
                {
                    Control c = row[i];
                    if (c == null)
                    {
                        continue;
                    }
                    if (c is Label)
                    {
                        Label label = (Label)c;
                        if (string.Equals(label.Tag as string, "single"))
                        {
                            // 路径这类内容固定单行：不换行，宽度不够就用省略号。
                            label.AutoSize = false;
                            label.AutoEllipsis = true;
                            label.MaximumSize = Size.Empty;
                            label.Width = widths[i];
                            label.Height = TextRenderer.MeasureText("国Ag", label.Font).Height;
                            heights[i] = label.Height;
                        }
                        else
                        {
                            label.AutoSize = false;
                            label.MaximumSize = new Size(widths[i], 0);
                            label.AutoSize = true;
                            heights[i] = label.Height;
                        }
                    }
                    else if (c is CheckBox)
                    {
                        CheckBox box = (CheckBox)c;
                        box.AutoSize = false;
                        box.Width = widths[i];
                        box.Height = UiScale.Scale(24, factor);
                        heights[i] = box.Height;
                    }
                    else
                    {
                        c.Width = widths[i];
                        Panel host = c as Panel;
                        if (host != null)
                        {
                            // 容器（比如「时段」里的两个输入框）高度按里面控件实际高度撑开，
                            // 否则字体放大后输入框下沿会被卡片裁掉一条。
                            int need = host.Height;
                            for (int k = 0; k < host.Controls.Count; k++)
                            {
                                Control part = host.Controls[k];
                                TextBox edit = part as TextBox;
                                if (edit != null)
                                {
                                    need = Math.Max(need, edit.PreferredHeight);
                                    continue;
                                }
                                Label text = part as Label;
                                if (text != null)
                                {
                                    need = Math.Max(need, text.Font.Height + UiScale.Scale(8, factor));
                                }
                            }
                            for (int k = 0; k < host.Controls.Count; k++)
                            {
                                Control part = host.Controls[k];
                                if (part is Label)
                                {
                                    part.Height = need;
                                    part.Top = 0;
                                }
                            }
                            if (need > host.Height)
                            {
                                host.Height = need;
                            }
                        }
                        heights[i] = c.Height;
                    }
                    if (heights[i] > rowHeight)
                    {
                        rowHeight = heights[i];
                    }
                }

                // 再摆位置：同一行的控件垂直居中
                for (int i = 0; i < row.Length; i++)
                {
                    Control c = row[i];
                    if (c == null)
                    {
                        continue;
                    }
                    int top = y + (rowHeight - heights[i]) / 2;
                    c.Location = new Point(x, top);
                    x += widths[i] + gapX;
                }

                y += rowHeight + gapY;
            }

            Height = y - gapY + bottom;
            Invalidate();
            return Height;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _titleFont != null)
            {
                _titleFont.Dispose();
                _titleFont = null;
            }
            base.Dispose(disposing);
        }
    }
}
