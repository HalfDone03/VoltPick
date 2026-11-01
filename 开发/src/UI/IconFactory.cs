using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

namespace DeviceBattery
{
    internal static class IconFactory
    {
        /// <summary>图标外观参数：色块留白、圆角比例、数字可用高度/宽度；无底块样式另有字高/字重。</summary>
        internal struct IconSpec
        {
            public float Margin;
            public float Radius;
            public float DigitHeight;
            public float DigitWidth;
            /// <summary>无底块时的字体键（见 DigitFont；空表示按样式查表）。</summary>
            public string Font;
        }

        /// <summary>按设置里的样式名取参数；未知值按“柔和”处理。</summary>
        internal static IconSpec SpecFor(string style)
        {
            IconSpec spec = new IconSpec();
            if (string.Equals(style, "classic", StringComparison.OrdinalIgnoreCase))
            {
                // 经典：铺满 16px 槽位的一整块，方一点，数字尽量高。
                spec.Margin = 0f;
                spec.Radius = 0.18f;
                spec.DigitHeight = 0.77f;
                spec.DigitWidth = 0.86f;
            }
            else if (string.Equals(style, "rounded", StringComparison.OrdinalIgnoreCase))
            {
                // 圆润：同样铺满槽位，圆角更大，看着更软。
                spec.Margin = 0f;
                spec.Radius = 0.34f;
                spec.DigitHeight = 0.75f;
                spec.DigitWidth = 0.86f;
            }
            else
            {
                // 柔和：铺满槽位（16px 槽位里再留边，图标就比系统图标小一圈）；
                // 靠圆角和描边和系统图标区分，而不是靠缩小自己。
                spec.Margin = 0f;
                spec.Radius = 0.24f;
                spec.DigitHeight = 0.77f;
                spec.DigitWidth = 0.86f;
            }
            return spec;
        }

        /// <summary>样式 + 字体键：字体键为空或 "auto" 时沿用该样式的默认字体。</summary>
        internal static IconSpec SpecFor(string style, string font)
        {
            IconSpec spec = SpecFor(style);
            if (!string.IsNullOrEmpty(font) && !string.Equals(font, "auto", StringComparison.OrdinalIgnoreCase))
            {
                spec.Font = font;
            }
            return spec;
        }

        public static Icon Build(int size, string text, Color background, bool dim, int digitPercent)
        {
            return Build(size, text, background, dim, digitPercent, false, null);
        }

        public static Icon Build(int size, string text, Color background, bool dim, int digitPercent, bool charging)
        {
            return Build(size, text, background, dim, digitPercent, charging, null);
        }

        /// <summary>
        /// 托盘用的「只有数字」图标：不画底色块，数字直接用图标颜色画在透明背景上，
        /// 并按长方形的比例铺满槽位（宽优先），16px 下比带底块时更宽、更清楚。
        /// </summary>
        public static Icon BuildDigits(int size, string text, Color color, bool dim, int digitPercent,
            bool charging, string style)
        {
            return Render(size, text, color, dim, digitPercent, charging, style, true, null);
        }

        /// <summary>同上，并指定数字字体键（auto 表示跟随样式）。</summary>
        public static Icon BuildDigits(int size, string text, Color color, bool dim, int digitPercent,
            bool charging, string style, string font)
        {
            return Render(size, text, color, dim, digitPercent, charging, style, true, font);
        }

        public static Icon Build(int size, string text, Color background, bool dim, int digitPercent,
            bool charging, string style)
        {
            return Render(size, text, background, dim, digitPercent, charging, style, false, null);
        }

        private static Icon Render(int size, string text, Color color, bool dim, int digitPercent,
            bool charging, string style, bool plain, string font)
        {
            IconSpec spec = plain ? SpecFor(style, font) : SpecFor(style);
            if (size < 8) size = 8;
            // 0 表示「不显示数字」，100 表示最大档；两端都不再被压回 40..90。
            if (digitPercent < 0) digitPercent = 0;
            if (digitPercent > 100) digitPercent = 100;

            if (plain)
            {
                // 无底块时不再「大图缩小」：16px 托盘槽位下将大图重采样，笔画边一定发毛。
                // 改成直接在最终尺寸上让 GDI+ 做网格适配抗锯齿（AntiAliasGridFit），
                // 笔画落在整像素上，看起来才像系统图标那样干净。
                using (Bitmap direct = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                {
                    using (Graphics graphics = Graphics.FromImage(direct))
                    {
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        graphics.PixelOffsetMode = PixelOffsetMode.Half;
                        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        graphics.Clear(Color.Transparent);
                        Draw(graphics, size, text, color, dim, digitPercent,
                            charging && size >= 20, spec, true, style);
                    }

                    return FromBitmap(direct);
                }
            }

            const int factor = 4;
            int big = size * factor;

            using (Bitmap canvas = new Bitmap(big, big, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(canvas))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
                    graphics.Clear(Color.Transparent);
                    Draw(graphics, big, text, color, dim, digitPercent, charging && size >= 20, spec, plain, style);
                }

                using (Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        using (ImageAttributes attributes = new ImageAttributes())
                        {
                            attributes.SetWrapMode(WrapMode.TileFlipXY);
                            graphics.DrawImage(canvas, new Rectangle(0, 0, size, size),
                                0, 0, big, big, GraphicsUnit.Pixel, attributes);
                        }
                    }

                    return FromBitmap(bitmap);
                }
            }
        }

        /// <summary>
        /// 把「数字大小」档位换算成占图标边长的比例。锚点取自设置页的三档：66=较小、80=标准、100=较大；
        /// 40 以下按最小档处理，这样手工改 settings.json 写成 55 之类也不会画出半个数字。
        /// </summary>
        private static float DigitSizeFactor(int digitPercent)
        {
            // 托盘图标只有 16x16，档位之间的差别必须拉到看得见：
            // 较小 0.74、标准 0.90、较大 1.00，两档相差约 1.5~2 像素。
            if (digitPercent <= 40) { return 0.60f; }
            if (digitPercent <= 66) { return 0.60f + (digitPercent - 40) * 0.14f / 26f; }
            if (digitPercent <= 80) { return 0.74f + (digitPercent - 66) * 0.16f / 14f; }
            if (digitPercent >= 100) { return 1.0f; }
            return 0.90f + (digitPercent - 80) * 0.10f / 20f;
        }

        /// <summary>系统里有没有这个字体族；结果缓存，避免每次画图标都枚举字体。</summary>
        private static bool HasFamily(string name)
        {
            lock (_fontCache)
            {
                bool known;
                if (_fontCache.TryGetValue(name, out known))
                {
                    return known;
                }
                bool found = false;
                using (InstalledFontCollection fonts = new InstalledFontCollection())
                {
                    for (int i = 0; i < fonts.Families.Length; i++)
                    {
                        if (string.Equals(fonts.Families[i].Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            found = true;
                            break;
                        }
                    }
                }
                _fontCache[name] = found;
                return found;
            }
        }

        private static readonly Dictionary<string, bool> _fontCache = new Dictionary<string, bool>();

        /// <summary>
        /// 托盘数字的字体键 -> 具体字体族 / 字重。16px 上要够粗才醒目，所以默认给 Black；
        /// 系统里没有这个字体就按下面的顺序退，最后一定落到 Segoe UI，不会画出空白图标。
        /// 键值同时用于 settings.json，改动要和 AppSettings.NormalizeIconFont / 设置页下拉框保持一致。
        /// </summary>
        private static void DigitFont(string key, out string family, out FontStyle weight)
        {
            if (string.IsNullOrEmpty(key) || string.Equals(key, "auto", StringComparison.OrdinalIgnoreCase))
            {
                key = "black";
            }
            key = key.Trim().ToLowerInvariant();

            if (key == "black")
            {
                if (Pick("Segoe UI Black", FontStyle.Regular, out family, out weight)) { return; }
                Pick("Segoe UI", FontStyle.Bold, out family, out weight);
                return;
            }
            if (key == "semibold")
            {
                if (Pick("Segoe UI Semibold", FontStyle.Regular, out family, out weight)) { return; }
                Pick("Segoe UI", FontStyle.Regular, out family, out weight);
                return;
            }
            if (key == "bold")
            {
                Pick("Segoe UI", FontStyle.Bold, out family, out weight);
                return;
            }
            if (key == "yahei")
            {
                if (Pick("Microsoft YaHei UI", FontStyle.Bold, out family, out weight)) { return; }
                if (Pick("Microsoft YaHei", FontStyle.Bold, out family, out weight)) { return; }
                Pick("Segoe UI", FontStyle.Bold, out family, out weight);
                return;
            }
            if (key == "verdana")
            {
                if (Pick("Verdana", FontStyle.Bold, out family, out weight)) { return; }
                Pick("Segoe UI", FontStyle.Bold, out family, out weight);
                return;
            }
            if (key == "tahoma")
            {
                if (Pick("Tahoma", FontStyle.Regular, out family, out weight)) { return; }
                Pick("Segoe UI", FontStyle.Bold, out family, out weight);
                return;
            }
            if (key == "arialblack")
            {
                if (Pick("Arial Black", FontStyle.Regular, out family, out weight)) { return; }
                Pick("Arial", FontStyle.Bold, out family, out weight);
                return;
            }
            if (key == "bahnschrift")
            {
                if (Pick("Bahnschrift SemiBold", FontStyle.Regular, out family, out weight)) { return; }
                if (Pick("Bahnschrift", FontStyle.Bold, out family, out weight)) { return; }
                Pick("Segoe UI", FontStyle.Bold, out family, out weight);
                return;
            }
            if (key == "impact")
            {
                if (Pick("Impact", FontStyle.Regular, out family, out weight)) { return; }
                Pick("Segoe UI", FontStyle.Bold, out family, out weight);
                return;
            }

            // 未知键：当成字体族名试一下，不行就退回默认。
            if (Pick(key, FontStyle.Regular, out family, out weight)) { return; }
            Pick("Segoe UI", FontStyle.Bold, out family, out weight);
        }

        private static bool Pick(string name, FontStyle style, out string family, out FontStyle weight)
        {
            if (HasFamily(name))
            {
                family = name;
                weight = style;
                return true;
            }
            family = "Segoe UI";
            weight = style;
            return false;
        }

        private static void Draw(Graphics graphics, float s, string text, Color color, bool dim,
            int digitPercent, bool bolt, IconSpec spec, bool plain, string style)
        {
            if (plain)
            {
                DrawPlain(graphics, s, text, color, dim, digitPercent, bolt, spec, style);
                return;
            }

            // 任务栏的图标槽位就是 16x16，再往里留白只会让自己的图标比系统图标小一圈；
            // 所以色块铺满整个槽位，只把描边的一半内缩，保证轮廓不被裁掉。
            float stroke = Math.Max(1f, s * 0.019f);
            float margin = spec.Margin > 0f ? Math.Max(0.5f, s * spec.Margin) : stroke * 0.5f;
            RectangleF bounds = new RectangleF(margin, margin, s - margin * 2f, s - margin * 2f);
            float radius = Math.Max(2f, bounds.Width * spec.Radius);
            int alpha = dim ? 115 : 255;

            // 轻微的上亮下暗渐变，让纯色块更有体积感；同时保持外轮廓和数字的高对比。
            Color top = Mix(color, Color.White, dim ? 0.05f : 0.10f);
            Color bottom = Mix(color, Color.Black, dim ? 0.06f : 0.10f);
            using (GraphicsPath path = RoundedRect(bounds, radius))
            using (LinearGradientBrush brush = new LinearGradientBrush(
                bounds, Color.FromArgb(alpha, top), Color.FromArgb(alpha, bottom), 90f))
            {
                graphics.FillPath(brush, path);
            }
            // 任务栏可能是浅色或深色；给图标加一圈对比描边，固定颜色偏黑/偏白时也不会消失。
            using (GraphicsPath path = RoundedRect(bounds, radius))
            using (Pen border = new Pen(Theme.IconBorder(color, dim), stroke))
            {
                graphics.DrawPath(border, path);
            }

            // 「不显示数字」时连数字一起跳过，只留色块；占位符（—、…）照画，表示这款设备暂时读不到。
            if (digitPercent > 0 || IsPlaceholderText(text))
            {
                DrawText(graphics, s, text, color, dim, digitPercent, bolt, spec);
            }

            if (bolt)
            {
                DrawBolt(graphics, s, dim, color);
            }
        }

        /// <summary>
        /// 无底块样式（托盘用）：透明背景上只有数字本身，颜色就是设置里的图标颜色。
        /// 「不显示数字」时留一个小圆点当把手，否则托盘上会是一块抓不到的透明空格。
        /// </summary>
        private static void DrawPlain(Graphics graphics, float s, string text, Color color, bool dim,
            int digitPercent, bool bolt, IconSpec spec, string style)
        {
            if (IsPlaceholderText(text))
            {
                DrawPlaceholderShape(graphics, s, text, color, dim, bolt);
            }
            else if (digitPercent > 0 && !string.IsNullOrEmpty(text))
            {
                DrawPlainText(graphics, s, text, color, dim, digitPercent, bolt, spec, style);
            }
            else if (!bolt)
            {
                DrawPlainDot(graphics, s, color, dim);
            }

            if (bolt)
            {
                DrawBoltShape(graphics, s, color, dim);
            }
        }

        /// <summary>
        /// 无底块时的数字：先按「样式 + 档位」查表拿到字形和基准字号，再在最终像素尺寸上直接
        /// 栅格化——不缩放位图也不缩放字形，只用整数像素把墨迹摆正。表里的字号是在 16px 槽位上
        /// 逐像素量出来的：挑「宽度铺满槽位、笔画全落整像素」的那个字号，所以既保住原来铺满
        /// 槽位的体积感，又不会像缩放那样糊边。
        /// </summary>
        private static void DrawPlainText(Graphics graphics, float s, string text, Color color,
            bool dim, int digitPercent, bool bolt, IconSpec spec, string style)
        {
            int canvas = (int)s;
            // RenderDigits 内部已经在按 2 倍画布超采样后再缩回 layerSize，所以这里给的就是最终
            // 尺寸——再乘 2 会让字形溢出 16px 画布被裁掉一圈。
            // 临时画布比图标大一圈：字号试探时溢出部分不被裁掉，量的才是真实字形。
            int layerSize = canvas + 8;
            float slotScale = s / 16f;
            float maxWidth = s * (bolt ? 0.58f : 1f);

            string digitFamily;
            FontStyle digitWeight;
            int top;
            if (!string.IsNullOrEmpty(spec.Font))
            {
                // 用户在设置里显式选了字体：以它为准，从最大可用字号往下试。
                DigitFont(spec.Font, out digitFamily, out digitWeight);
                top = canvas;
            }
            else
            {
                int baseSize;
                PlainDigitFont(style, digitPercent, text.Length >= 3, canvas, out digitFamily, out digitWeight, out baseSize);
                top = (int)Math.Round(baseSize * slotScale);

            }

            if (top > canvas)
            {
                top = canvas;
            }

            if (top < 6)
            {
                top = 6;
            }

            if (canvas >= 32)
            {
                // 大图标（面板/预览）不需要逐像素挑字号：按基准字号等比放大就行。要是也走下面的
                // 「宽度塞得下」筛选，三个档位会被同一个宽度上限压成同一号字，档位就看不出来了。
                Bitmap first = RenderDigits(text, digitFamily, digitWeight, top, color, dim, layerSize);
                Rectangle firstBox;
                MeasureInk(first, out firstBox);
                if (firstBox.Width > maxWidth && firstBox.Width > 0)
                {
                    int shrunk = (int)Math.Round(top * (maxWidth / firstBox.Width));
                    if (shrunk < 6)
                    {
                        shrunk = 6;
                    }

                    first.Dispose();
                    first = RenderDigits(text, digitFamily, digitWeight, shrunk, color, dim, layerSize);
                    MeasureInk(first, out firstBox);
                }

                using (first)
                {
                    int directX = (int)Math.Round((canvas - firstBox.Width) / 2f) - firstBox.X;
                    int directY = (int)Math.Round((canvas - firstBox.Height) / 2f) - firstBox.Y;
                    graphics.DrawImageUnscaled(first, directX, directY);
                }

                return;
            }

            // 字号从上往下试，取第一个宽度塞得下的：1 位提示下字形宽度随字号跳变，只有这一档
            // 能保证两位数字都完整落在槽位里。
            Bitmap chosen = null;
            Rectangle chosenBox = Rectangle.Empty;
            for (int pixelSize = top; pixelSize >= 6; pixelSize--)
            {
                Bitmap layer = RenderDigits(text, digitFamily, digitWeight, pixelSize, color, dim, layerSize);
                Rectangle box;
                MeasureInk(layer, out box);
                if (box.Width <= 0 || box.Width > maxWidth || box.Height > canvas)
                {
                    // 空盒不能算「塞得下」：字号超过画布时 GDI+ 画成整张空白，
                    // 空盒宽高都是 0，会被误判成最合适的一档，最后贴上去什么都没有。
                    layer.Dispose();
                    continue;
                }

                chosen = layer;
                chosenBox = box;
                break;
            }

            if (chosen == null)
            {
                return;
            }
            using (chosen)
            {
                int offsetX = (int)Math.Round((canvas - chosenBox.Width) / 2f) - chosenBox.X;
                int offsetY = (int)Math.Round((canvas - chosenBox.Height) / 2f) - chosenBox.Y;
                graphics.DrawImageUnscaled(chosen, offsetX, offsetY);
            }
        }

        /// <summary>
        /// 无底块数字的字体表：按样式、字数和档位给出字体与基准字号（都是 16px 槽位实测值）。
        /// 单比特提示下字形宽度会随字号整像素跳变，所以每一档都挑「宽度正好铺满、高度按档递增」
        /// 的那一款，既保住原来的大小，也不会因为缩放出现灰边。
        /// </summary>
        private static void PlainDigitFont(string style, int digitPercent, bool longText, int canvas, out string family,
            out FontStyle weight, out int baseSize)
        {
            int level = digitPercent <= 66 ? 1 : (digitPercent <= 80 ? 2 : 3);
            family = "Tahoma";
            weight = FontStyle.Bold;
            baseSize = 12;

            if (longText)
            {
                // 三位数时三张字形挤在 16px 里：字腔宽的常规体、再各降一档字号才分得开。
                if (string.Equals(style, "classic", StringComparison.OrdinalIgnoreCase)
                    && Pick("Impact", FontStyle.Bold, out family, out weight))
                {
                    baseSize = 12;
                    return;
                }

                if (string.Equals(style, "rounded", StringComparison.OrdinalIgnoreCase)
                    && Pick("Tahoma", FontStyle.Bold, out family, out weight))
                {
                    baseSize = 12;
                    return;
                }

                if (Pick("Segoe UI", FontStyle.Regular, out family, out weight))
                {
                    baseSize = 12;
                    return;
                }

                Pick("Tahoma", FontStyle.Regular, out family, out weight);
                baseSize = 12;
                return;
            }

            if (string.Equals(style, "classic", StringComparison.OrdinalIgnoreCase))
            {
                // 经典：厚实的三档——Verdana Bold 16x9 / Arial Black Bold 16x10 / Impact Bold 16x12。
                if (level == 1 && Pick("Verdana", FontStyle.Bold, out family, out weight))
                {
                    baseSize = 12;
                    return;
                }

                if (level == 2 && Pick("Arial Black", FontStyle.Bold, out family, out weight))
                {
                    baseSize = 13;
                    return;
                }

                if (Pick("Impact", FontStyle.Bold, out family, out weight))
                {
                    baseSize = 15;
                    return;
                }
            }

            if (string.Equals(style, "rounded", StringComparison.OrdinalIgnoreCase))
            {
                // 圆润：Arial，小档粗、大档常规，三档 13x9 / 13x10 / 16x12。
                if (level == 1 && Pick("Arial", FontStyle.Bold, out family, out weight))
                {
                    baseSize = 12;
                    return;
                }

                if (level == 2 && Pick("Arial", FontStyle.Bold, out family, out weight))
                {
                    baseSize = 13;
                    return;
                }

                if (Pick("Arial", FontStyle.Regular, out family, out weight))
                {
                    baseSize = 16;
                    return;
                }
            }

            // 柔和：Segoe UI 常规体，笔画细、字腔大，和另外两档的粗黑感拉开。
            // 但 16/20px 的槽位缩回去后笔芯只剩一成多实心像素，看着发虚；小尺寸换成
            // Semibold 把笔画加厚一档，24px 以上仍保持常规体的轻盈。
            if (canvas <= 20 && Pick("Segoe UI Semibold", FontStyle.Regular, out family, out weight))
            {
                baseSize = level == 1 ? 12 : (level == 2 ? 14 : 16);
                return;
            }

            if (Pick("Segoe UI", FontStyle.Regular, out family, out weight))
            {
                baseSize = level == 1 ? 12 : (level == 2 ? 14 : 17);
                return;
            }

            Pick("Tahoma", FontStyle.Regular, out family, out weight);
            baseSize = level == 1 ? 12 : (level == 2 ? 14 : 16);
        }

        /// <summary>托盘纯数字的超采样倍数：16px 槽位放大到 64px 画、再按面积平均缩回去。</summary>
        private const int DigitSupersample = 4;

        /// <summary>
        /// 画托盘数字：先按 <see cref="DigitSupersample"/> 倍画布用灰度抗锯齿绘制，
        /// 再按面积平均缩回目标尺寸。原来用 SingleBitPerPixelGridFit 直接画在最终尺寸上，
        /// 笔画只落在整像素、没有灰边，字圆和斜线全是硬台阶——放大看就是「毛刺」。
        /// </summary>
        private static Bitmap RenderDigits(string text, string family, FontStyle weight, int pixelSize,
            Color color, bool dim, int layerSize)
        {
            // GDI+ 在字号超过 220 左右时干脆一个像素都不画（实测 220 正常、240 全空），
            // 所以超采样倍数要按最终字号收一收：小图标照旧 4 倍，大图标降到 3/2 倍。
            int factor = DigitSupersample;
            while (factor > 1 && pixelSize * factor > 200)
            {
                factor--;
            }

            int big = layerSize * factor;

            // 字形行高必须落在画布内：一旦超出去，GDI+ 不是裁一刀，而是整张画布一个像素都不画
            // （实测 288px 画布画 240px 字全空、220px 正常）。各字体的行高与字号比例差得很远
            // （Arial Black 就比 Impact 高一大截），所以这里实测一个比例再折算，不能写死系数。
            int fontSize = pixelSize * factor;
            using (Font probe = new Font(family, pixelSize, weight, GraphicsUnit.Pixel))
            {
                float ratio = probe.Height / (float)pixelSize;
                int limit = (int)Math.Floor(big / ratio);
                if (fontSize > limit)
                {
                    fontSize = limit;
                }
            }

            if (fontSize < 6)
            {
                fontSize = 6;
            }
            using (Bitmap canvas = new Bitmap(big, big, PixelFormat.Format32bppArgb))
            {
                using (Graphics paint = Graphics.FromImage(canvas))
                {
                    paint.SmoothingMode = SmoothingMode.AntiAlias;
                    paint.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    // 灰度抗锯齿：托盘图标尺寸固定，ClearType 的彩色子像素边在深色托盘上会发彩
                    paint.TextRenderingHint = TextRenderingHint.AntiAlias;
                    paint.Clear(Color.Transparent);
                    using (Font font = new Font(family, fontSize, weight, GraphicsUnit.Pixel))
                    using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
                    using (SolidBrush brush = new SolidBrush(dim ? Mix(color, Color.Black, 0.45f) : color))
                    {
                        format.Alignment = StringAlignment.Center;
                        format.LineAlignment = StringAlignment.Center;
                        format.FormatFlags |= StringFormatFlags.NoClip;
                        RectangleF box = new RectangleF(0f, 0f, big, big);
                        paint.DrawString(text, font, brush, box, format);
                    }
                }

                return DownscaleAlpha(canvas, layerSize, factor);
            }
        }

        /// <summary>
        /// 整数倍面积平均缩小：按 alpha 预乘后累加，边缘不会因为权重不同而发黑或发白；
        /// 缩小倍数是整数，所以每个目标像素正好吃掉 factor² 个源像素。
        /// 缩完再按「覆盖率 95 分位」反解一次 gamma：细字体缩到 16px 时笔芯只有一百出头的 alpha，
        /// 看着发虚；这一步把笔芯提到接近不透明，同时保留边缘的渐变（不是硬阈值截断）。
        /// </summary>
        private static Bitmap DownscaleAlpha(Bitmap source, int size, int factor)
        {
            Bitmap target = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            BitmapData srcData = source.LockBits(new Rectangle(0, 0, source.Width, source.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            BitmapData dstData = target.LockBits(new Rectangle(0, 0, size, size),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int srcStride = srcData.Stride;
                byte[] srcBytes = new byte[srcStride * source.Height];
                Marshal.Copy(srcData.Scan0, srcBytes, 0, srcBytes.Length);

                int dstStride = dstData.Stride;
                byte[] dstBytes = new byte[dstStride * size];
                int area = factor * factor;
                int[] coverage = new int[size * size];
                int[] colorR = new int[size * size];
                int[] colorG = new int[size * size];
                int[] colorB = new int[size * size];
                int[] histogram = new int[256];
                int ink = 0;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int sumA = 0;
                        int sumB = 0;
                        int sumG = 0;
                        int sumR = 0;
                        for (int sy = 0; sy < factor; sy++)
                        {
                            int row = (y * factor + sy) * srcStride + x * factor * 4;
                            for (int sx = 0; sx < factor; sx++)
                            {
                                int index = row + sx * 4;
                                int alpha = srcBytes[index + 3];
                                sumA += alpha;
                                sumB += srcBytes[index] * alpha;
                                sumG += srcBytes[index + 1] * alpha;
                                sumR += srcBytes[index + 2] * alpha;
                            }
                        }

                        if (sumA == 0)
                        {
                            continue;
                        }

                        int slot = y * size + x;
                        int value = sumA / area;
                        coverage[slot] = value;
                        colorR[slot] = sumR / sumA;
                        colorG[slot] = sumG / sumA;
                        colorB[slot] = sumB / sumA;
                        histogram[value]++;
                        ink++;
                    }
                }

                double gamma = CoverageGamma(histogram, ink);
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int slot = y * size + x;
                        int offset = y * dstStride + x * 4;
                        int value = coverage[slot];
                        if (value == 0)
                        {
                            dstBytes[offset] = 0;
                            dstBytes[offset + 1] = 0;
                            dstBytes[offset + 2] = 0;
                            dstBytes[offset + 3] = 0;
                            continue;
                        }

                        dstBytes[offset] = (byte)colorB[slot];
                        dstBytes[offset + 1] = (byte)colorG[slot];
                        dstBytes[offset + 2] = (byte)colorR[slot];
                        dstBytes[offset + 3] = (byte)Math.Round(255.0 * Math.Pow(value / 255.0, gamma));
                    }
                }

                Marshal.Copy(dstBytes, 0, dstData.Scan0, dstBytes.Length);
            }
            finally
            {
                source.UnlockBits(srcData);
                target.UnlockBits(dstData);
            }

            return target;
        }

        /// <summary>
        /// 由覆盖率直方图反解 gamma，让 95 分位的覆盖率正好落到接近不透明的 0.96：
        /// 粗体笔画本来就有大片全满像素（p95≈255），gamma 自动接近 1、等于不改；
        /// 细体只有一百出头的覆盖率，gamma 会降到 0.5 附近，笔芯被提实而边缘仍是渐变。
        /// </summary>
        private static double CoverageGamma(int[] histogram, int ink)
        {
            if (ink == 0)
            {
                return 1.0;
            }

            int target = (int)Math.Ceiling(ink * 0.95);
            int seen = 0;
            int level = 255;
            for (int value = 255; value >= 1; value--)
            {
                seen += histogram[value];
                if (seen >= target)
                {
                    level = value;
                    break;
                }
            }

            if (level >= 245)
            {
                return 1.0;
            }

            double gamma = Math.Log(0.96) / Math.Log(level / 255.0);
            // 下限决定「最细的字体在 16px 上有多实」：0.34 时 soft 档的笔芯能压到接近全不透明，
            // 只留边缘一两个灰阶；再往上抬（0.5）16px 会整体发虚，看着像蒙了一层雾。
            if (gamma < 0.34)
            {
                return 0.34;
            }

            return gamma > 1.0 ? 1.0 : gamma;
        }
        /// <summary>量墨迹包围盒，并返回「硬像素占比」（alpha≥250 占全部非透明像素的比例）。</summary>
        private static float MeasureInk(Bitmap layer, out Rectangle box)
        {
            int minX = int.MaxValue;
            int minY = int.MaxValue;
            int maxX = -1;
            int maxY = -1;
            int ink = 0;
            int hard = 0;
            for (int y = 0; y < layer.Height; y++)
            {
                for (int x = 0; x < layer.Width; x++)
                {
                    int alpha = layer.GetPixel(x, y).A;
                    if (alpha == 0)
                    {
                        continue;
                    }

                    ink++;
                    if (alpha >= 250)
                    {
                        hard++;
                    }

                    if (alpha <= 24)
                    {
                        continue;
                    }

                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < 0 || ink == 0)
            {
                box = Rectangle.Empty;
                return -1f;
            }

            box = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
            return (float)hard / ink;
        }

        private static void DrawPlainDot(Graphics graphics, float s, Color color, bool dim)
        {
            float size = Math.Max(2f, s * 0.34f);
            RectangleF bounds = new RectangleF((s - size) / 2f, (s - size) / 2f, size, size);
            using (GraphicsPath path = RoundedRect(bounds, size * 0.5f))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(dim ? 140 : 255, color)))
            {
                graphics.FillPath(brush, path);
            }
        }

        private static void DrawText(Graphics graphics, float s, string text, Color background,
            bool dim, int digitPercent, bool bolt, IconSpec spec)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            if (IsPlaceholderText(text))
            {
                DrawPlaceholder(graphics, s, text, background, dim, bolt);
                return;
            }

            float fontSize = s * (digitPercent / 100f);
            float minimum = s * 0.28f;
            if (fontSize < minimum)
            {
                fontSize = minimum;
            }

            using (Font font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
            using (GraphicsPath textPath = new GraphicsPath())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.FormatFlags |= StringFormatFlags.NoClip;
                textPath.AddString(text, font.FontFamily, (int)font.Style, font.Size,
                    PointF.Empty, format);

                RectangleF ink = textPath.GetBounds();
                if (ink.Width <= 0f || ink.Height <= 0f)
                {
                    return;
                }

                // 按实际字形外框缩放并居中，而不是按行高居中：数字会更饱满、上下左右更对称。
                // 三档「数字大小」按 40/66/80/100 四个锚点分段放大：较小约 72%、标准约 88%、较大 100%。
                // 之前用 (digitPercent-40)/50 直接线性，三档只差 2~3 个百分点，16px 托盘下完全看不出来。
                float sizeFactor = DigitSizeFactor(digitPercent);
                // 宽高都乘以档位系数：小档是等比的小数字，大档吃满整个槽位。
                float maxWidth = s * (bolt ? 0.50f : spec.DigitWidth) * sizeFactor;
                float maxHeight = s * (bolt ? 0.62f : spec.DigitHeight) * sizeFactor;

                // 先按“整体等比”定一个安全尺寸，再允许最多 1.25 倍横向压缩换取高度。
                // 这样 8、88、100 的字高彼此接近，不会出现个位数巨大、三位数缩成一条。
                float uniform = Math.Min(maxWidth / ink.Width, maxHeight / ink.Height);
                if (uniform > 1.35f)
                {
                    uniform = 1.35f;
                }
                float heightScale = maxHeight / ink.Height;
                if (heightScale > 1.35f)
                {
                    heightScale = 1.35f;
                }
                float squeeze = uniform > 0.01f ? heightScale / uniform : 1f;
                if (squeeze > 1.30f)
                {
                    squeeze = 1.30f;
                }
                float scaleY = Math.Min(heightScale, uniform * squeeze);
                float scaleX = Math.Min(scaleY, maxWidth / ink.Width);

                float centerX = s * (bolt ? 0.34f : 0.5f);
                float centerY = s * 0.5f;
                using (Matrix transform = new Matrix())
                {
                    // Matrix 的 Translate/Scale 是后乘；顺序写成“目标中心 → 缩放 → 移到原点”，
                    // 最终才是 target + scale * (point - inkCenter)。
                    transform.Translate(centerX, centerY);
                    transform.Scale(scaleX, scaleY);
                    transform.Translate(-(ink.X + ink.Width * 0.5f), -(ink.Y + ink.Height * 0.5f));
                    textPath.Transform(transform);
                }

                Color foreground = Theme.ContrastText(background);
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(dim ? 175 : 255, foreground)))
                {
                    graphics.FillPath(brush, textPath);
                }
            }
        }

        private static bool IsPlaceholderText(string text)
        {
            return string.Equals(text, "—", StringComparison.Ordinal) ||
                   string.Equals(text, "-", StringComparison.Ordinal) ||
                   string.Equals(text, "…", StringComparison.Ordinal) ||
                   string.Equals(text, "...", StringComparison.Ordinal);
        }

        /// <summary>
        /// 空态图标（当前没有可读电量的设备）不要走字形缩放：把“—”的墨迹拉到 82% 宽后
        /// 会变成一根很粗的横条，看着像电量条；“…”则会变成三个巨大的圆点。
        /// 这里固定线宽、居中画一个小横条或三个小圆点，16px 下也能一眼认出是“无数据”。
        /// </summary>
        private static void DrawPlaceholder(Graphics graphics, float s, string text, Color background,
            bool dim, bool bolt)
        {
            DrawPlaceholderShape(graphics, s, text, Theme.ContrastText(background), dim, bolt);
        }

        private static void DrawPlaceholderShape(Graphics graphics, float s, string text, Color foreground,
            bool dim, bool bolt)
        {
            float centerX = s * (bolt ? 0.34f : 0.5f);
            float centerY = s * 0.5f;
            float thickness = Math.Max(1.2f, s * 0.08f);
            int alpha = dim ? 175 : 255;
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(alpha, foreground)))
            {
                if (string.Equals(text, "—", StringComparison.Ordinal) ||
                    string.Equals(text, "-", StringComparison.Ordinal))
                {
                    float width = s * 0.42f;
                    RectangleF bar = new RectangleF(centerX - width / 2f, centerY - thickness / 2f,
                        width, thickness);
                    using (GraphicsPath path = RoundedRect(bar, thickness / 2f))
                    {
                        graphics.FillPath(brush, path);
                    }
                    return;
                }

                float dot = Math.Max(1.5f, s * 0.10f);
                float step = dot * 1.9f;
                for (int i = -1; i <= 1; i++)
                {
                    float x = centerX + i * step - dot / 2f;
                    graphics.FillEllipse(brush, x, centerY - dot / 2f, dot, dot);
                }
            }
        }

        private static Color Mix(Color color, Color target, float amount)
        {
            if (amount < 0f) amount = 0f;
            if (amount > 1f) amount = 1f;
            return Color.FromArgb(
                (int)Math.Round(color.R + (target.R - color.R) * amount),
                (int)Math.Round(color.G + (target.G - color.G) * amount),
                (int)Math.Round(color.B + (target.B - color.B) * amount));
        }

        private static void DrawBolt(Graphics graphics, float s, bool dim, Color background)
        {
            DrawBoltShape(graphics, s, Theme.ContrastText(background), dim);
        }

        private static void DrawBoltShape(Graphics graphics, float s, Color foreground, bool dim)
        {
            float x = s * 0.66f;
            float y = s * 0.02f;
            float w = s * 0.30f;
            float h = s * 0.40f;
            PointF[] points = new PointF[]
            {
                new PointF(x + w * 0.56f, y),
                new PointF(x + w * 0.06f, y + h * 0.55f),
                new PointF(x + w * 0.44f, y + h * 0.55f),
                new PointF(x + w * 0.26f, y + h),
                new PointF(x + w * 0.96f, y + h * 0.38f),
                new PointF(x + w * 0.54f, y + h * 0.38f)
            };

            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(dim ? 110 : 190, 0, 0, 0)))
            {
                graphics.FillPolygon(shadow, points);
            }
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(dim ? 190 : 255, foreground)))
            using (Pen outline = new Pen(Color.FromArgb(dim ? 120 : 220, 24, 24, 27), Math.Max(1f, s * 0.035f)))
            {
                graphics.FillPolygon(fill, points);
                graphics.DrawPolygon(outline, points);
            }
        }

        private static Icon FromBitmap(Bitmap bitmap)
        {
            int size = bitmap.Width;
            byte[] ico = BuildIco(bitmap);

            using (MemoryStream stream = new MemoryStream(ico))
            using (Icon raw = new Icon(stream, new Size(size, size)))
            {
                return (Icon)raw.Clone();
            }
        }

        private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
        {
            float diameter = radius * 2f;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        private static byte[] BuildIco(Bitmap bitmap)
        {
            int width = bitmap.Width;
            int height = bitmap.Height;

            BitmapData data = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            byte[] pixels = new byte[width * height * 4];
            try
            {
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), pixels, y * width * 4, width * 4);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            int maskStride = ((width + 31) / 32) * 4;
            int xorSize = width * height * 4;
            int maskSize = maskStride * height;
            int imageSize = 40 + xorSize + maskSize;

            byte[] ico = new byte[6 + 16 + imageSize];
            WriteUInt16(ico, 0, 0);
            WriteUInt16(ico, 2, 1);
            WriteUInt16(ico, 4, 1);

            ico[6] = (byte)(width >= 256 ? 0 : width);
            ico[7] = (byte)(height >= 256 ? 0 : height);
            ico[8] = 0;
            ico[9] = 0;
            WriteUInt16(ico, 10, 1);
            WriteUInt16(ico, 12, 32);
            WriteUInt32(ico, 14, (uint)imageSize);
            WriteUInt32(ico, 18, 22);

            int header = 22;
            WriteUInt32(ico, header + 0, 40);
            WriteInt32(ico, header + 4, width);
            WriteInt32(ico, header + 8, height * 2);
            WriteUInt16(ico, header + 12, 1);
            WriteUInt16(ico, header + 14, 32);
            WriteUInt32(ico, header + 16, 0);
            WriteUInt32(ico, header + 20, (uint)(xorSize + maskSize));

            int xorOffset = header + 40;
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(pixels, y * width * 4, ico, xorOffset + (height - 1 - y) * width * 4, width * 4);
            }

            return ico;
        }

        private static void WriteUInt16(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            WriteUInt32(buffer, offset, (uint)value);
        }
    }
}
