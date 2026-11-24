using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace DeviceBattery
{
    /// <summary>
    /// 任务栏条的像素渲染：把每段文字按 ClearType 覆盖率取墨迹，再按颜色/透明度
    /// 混合到一张带逐像素 alpha 的位图上。只借鉴 BatteryHub 的绘制事实，独立实现。
    /// </summary>
    internal sealed class TaskbarStripRenderer
    {
        /// <summary>一段文字的墨迹：裁到实际有像素的范围，a 是 0..255 覆盖率。</summary>
        internal sealed class Mask
        {
            public int Width;
            public int Height;
            public byte[] Alpha;
        }

        internal sealed class Layout
        {
            public int Width;
            public int Height;
            public byte[] Pixels; // 预乘 BGRA
            public List<int[]> Hits = new List<int[]>(); // {from, to, deviceIndex}
        }

        private const int ClearTypeQuality = 5;
        private static readonly Dictionary<string, IntPtr> FontCache =
            new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation,
            int weight, byte italic, byte underline, byte strikeOut, byte charSet, byte outPrecision,
            byte clipPrecision, byte quality, byte pitchAndFamily, string faceName);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetTextFaceW(IntPtr hdc, int count, StringBuilder face);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern bool TextOutW(IntPtr hdc, int x, int y, string text, int length);

        [DllImport("gdi32.dll")]
        private static extern bool GdiFlush();

        /// <summary>
        /// 创建字体并校验实际字体名。缺字体时回退到 fallback，避免 GDI 悄悄替换成别的字体
        /// 导致字形变空白。返回的句柄进程内缓存，不每帧创建。
        /// </summary>
        internal static IntPtr GetFont(int pixelSize, int weight, string preferred, string fallback)
        {
            string key = pixelSize + "|" + weight + "|" + preferred + "|" + fallback;
            IntPtr cached;
            if (FontCache.TryGetValue(key, out cached) && cached != IntPtr.Zero)
            {
                return cached;
            }
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            IntPtr result = IntPtr.Zero;
            string[] faces = new string[] { preferred, fallback };
            for (int i = 0; i < faces.Length; i++)
            {
                IntPtr font = CreateFontW(-pixelSize, 0, 0, 0, weight, 0, 0, 0, 1,
                    0, 0, ClearTypeQuality, 0, faces[i]);
                if (font == IntPtr.Zero) continue;
                IntPtr old = SelectObject(dc, font);
                StringBuilder actual = new StringBuilder(128);
                GetTextFaceW(dc, actual.Capacity, actual);
                SelectObject(dc, old);
                if (string.Equals(actual.ToString(), faces[i], StringComparison.OrdinalIgnoreCase))
                {
                    result = font;
                    break;
                }
                DeleteObject(font);
            }
            if (result == IntPtr.Zero)
            {
                // 两种字体都没有：用系统默认，至少不会画出空白。
                result = CreateFontW(-pixelSize, 0, 0, 0, weight, 0, 0, 0, 1,
                    0, 0, ClearTypeQuality, 0, null);
            }
            DeleteDC(dc);
            FontCache[key] = result;
            return result;
        }

        /// <summary>
        /// 把文字画到黑/白底上取覆盖率。darkOnLight=true 时黑字白底，覆盖率 = 255 - RGB均值；
        /// 否则白字黑底，覆盖率 = RGB均值。这和 BatteryHub 的取法一致，但代码独立实现。
        /// </summary>
        internal static Mask MakeMask(string text, IntPtr font, bool darkOnLight)
        {
            Mask empty = new Mask { Width = 0, Height = 0, Alpha = new byte[0] };
            if (string.IsNullOrEmpty(text) || font == IntPtr.Zero)
            {
                return empty;
            }
            using (Bitmap bitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                IntPtr dc = graphics.GetHdc();
                IntPtr oldFont = SelectObject(dc, font);
                // 用 GDI 自己量，和 TextOut 用同一字体，宽度才对得上。
                Native.SIZE extent = new Native.SIZE(0, 0);
                GetTextExtentPoint32W(dc, text, text.Length, ref extent);
                SelectObject(dc, oldFont);
                graphics.ReleaseHdc(dc);

                int w = extent.CX + 8;
                int h = extent.CY + 8;
                if (w <= 8 || h <= 8)
                {
                    return empty;
                }
                byte[] buffer = new byte[w * h * 4];
                IntPtr bits = GCHandle.Alloc(buffer, GCHandleType.Pinned).AddrOfPinnedObject();
                using (Bitmap section = new Bitmap(w, h, w * 4, PixelFormat.Format32bppArgb, bits))
                {
                    using (Graphics sg = Graphics.FromImage(section))
                    {
                        sg.Clear(darkOnLight ? Color.White : Color.Black);
                        IntPtr sdc = sg.GetHdc();
                        IntPtr oldF = SelectObject(sdc, font);
                        TextOutW(sdc, 4, 4, text, text.Length);
                        GdiFlush();
                        SelectObject(sdc, oldF);
                        sg.ReleaseHdc(sdc);
                    }
                    // 找墨迹边界
                    int x0 = w, y0 = h, x1 = -1, y1 = -1;
                    byte[] coverage = new byte[w * h];
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int p = (y * w + x) * 4;
                            int avg = (buffer[p] + buffer[p + 1] + buffer[p + 2]) / 3;
                            int cov = darkOnLight ? 255 - avg : avg;
                            if (cov > 0)
                            {
                                coverage[y * w + x] = (byte)cov;
                                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                                y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                            }
                        }
                    }
                    if (x1 < 0)
                    {
                        return empty;
                    }
                    Mask mask = new Mask();
                    mask.Width = x1 - x0 + 1;
                    mask.Height = y1 - y0 + 1;
                    mask.Alpha = new byte[mask.Width * mask.Height];
                    for (int y = 0; y < mask.Height; y++)
                    {
                        for (int x = 0; x < mask.Width; x++)
                        {
                            mask.Alpha[y * mask.Width + x] = coverage[(y0 + y) * w + x0 + x];
                        }
                    }
                    return mask;
                }
            }
        }

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetTextExtentPoint32W(IntPtr hdc, string text, int length,
            ref Native.SIZE size);

        /// <summary>
        /// 把若干段拼成一张透明位图。返回预乘 BGRA 像素（自上而下），
        /// 透明像素填 0x01000000 让点击穿透（分层窗口 alpha=0 才穿透）。
        /// </summary>
        internal static Layout Compose(List<TaskbarPiece> pieces, bool light, float scale,
            IntPtr iconFont, IntPtr textFont, int height)
        {
            Layout layout = new Layout();
            if (height < 24) height = 24;
            if (height > 64) height = 64;
            int pad = (int)Math.Round(TaskbarStripStyle.Pad * scale);
            int gapGlyph = (int)Math.Round(TaskbarStripStyle.GapGlyphToNumber * scale);
            int gapBolt = (int)Math.Round(TaskbarStripStyle.GapNumberToBolt * scale);
            int gapDevice = (int)Math.Round(TaskbarStripStyle.GapBetweenDevices * scale);

            List<Mask> masks = new List<Mask>();
            int x = pad;
            for (int i = 0; i < pieces.Count; i++)
            {
                TaskbarPiece piece = pieces[i];
                bool isGlyph = piece.Text.Length == 1 && piece.Text[0] >= '\uE000';
                IntPtr font = isGlyph ? iconFont : textFont;
                Mask mask = MakeMask(piece.Text, font, light);
                masks.Add(mask);
                x += mask.Width + (int)Math.Round(piece.GapAfter * scale);
            }
            layout.Width = x + pad;
            layout.Height = height;
            layout.Pixels = new byte[layout.Width * layout.Height * 4];
            for (int p = 0; p < layout.Pixels.Length; p += 4)
            {
                layout.Pixels[p] = 0; layout.Pixels[p + 1] = 0;
                layout.Pixels[p + 2] = 0; layout.Pixels[p + 3] = 1;
            }

            int cursor = pad;
            int lastDevice = int.MinValue;
            int hitStart = 0;
            for (int i = 0; i < pieces.Count; i++)
            {
                TaskbarPiece piece = pieces[i];
                Mask mask = masks[i];
                if (piece.DeviceIndex >= 0 && piece.DeviceIndex != lastDevice)
                {
                    if (lastDevice >= 0)
                    {
                        layout.Hits.Add(new int[] { hitStart, cursor, lastDevice });
                    }
                    hitStart = cursor;
                    lastDevice = piece.DeviceIndex;
                }
                int alpha = piece.Alpha;
                int y0 = Math.Max(0, (height - mask.Height) / 2);
                for (int yy = 0; yy < mask.Height && y0 + yy < height; yy++)
                {
                    for (int xx = 0; xx < mask.Width && cursor + xx < layout.Width; xx++)
                    {
                        int cov = mask.Alpha[yy * mask.Width + xx] * alpha / 255;
                        if (cov <= 0) continue;
                        int p = ((y0 + yy) * layout.Width + cursor + xx) * 4;
                        // 预乘 BGRA + AC_SRC_ALPHA
                        layout.Pixels[p] = (byte)(piece.Color.B * cov / 255);
                        layout.Pixels[p + 1] = (byte)(piece.Color.G * cov / 255);
                        layout.Pixels[p + 2] = (byte)(piece.Color.R * cov / 255);
                        layout.Pixels[p + 3] = (byte)cov;
                    }
                }
                int after = piece.GapAfter == TaskbarStripStyle.GapGlyphToNumber ? gapGlyph
                    : (piece.GapAfter == TaskbarStripStyle.GapNumberToBolt ? gapBolt
                    : (piece.GapAfter == TaskbarStripStyle.GapBetweenDevices ? gapDevice
                    : (int)Math.Round(piece.GapAfter * scale)));
                cursor += mask.Width + after;
            }
            if (lastDevice >= 0)
            {
                layout.Hits.Add(new int[] { hitStart, layout.Width, lastDevice });
            }
            return layout;
        }

        /// <summary>按命中区间找出 x 对应的设备索引，-1 表示空白区。</summary>
        internal static int HitTest(Layout layout, int x)
        {
            if (layout == null) return -1;
            for (int i = 0; i < layout.Hits.Count; i++)
            {
                int[] hit = layout.Hits[i];
                if (x >= hit[0] && x < hit[1]) return hit[2];
            }
            return -1;
        }
    }
}
