using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// 把一张方图/照片转成多尺寸 .ico。
//
// 用法（不参与 build.ps1 / test.ps1，改了图标才手动跑一次）：
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:exe ^
//       /codepage:65001 /warn:4 /warnaserror+ /out:开发\tests\MakeIcon.exe ^
//       /r:System.dll /r:System.Drawing.dll 开发\tools\MakeIcon.cs
//   开发\tests\MakeIcon.exe 开发\assets\icon-source.jpg 开发\assets\app.ico [--dump <目录>]
//
// 采样链路（缩略图不糊、不出摩尔纹的关键）：
//   1. 源图居中裁正方形，一次性转成**线性光**浮点（sRGB 直接缩小会把高对比边缘压出锯齿和脏灰）；
//   2. 逐次 2×2 盒式减半，先降到目标尺寸的 2 倍以内（相当于做 mipmap，避免细密纹理走样）；
//   3. 最后一次用 Catmull-Rom 重采样，核宽随缩小倍率放大（面积加权，等价于带预滤波的缩放）；
//   4. 回 sRGB 后按尺寸分档做轻锐化 + 轻微对比/饱和度提升（16～24px 靠锐化才立得住）；
//   5. 四角按 18% 半径做抗锯齿圆角，透明区同时写进 AND 掩码（老式 16/24 位显示不会变黑角）。
// 入口条目写成 BMP 形式的 DIB 而不是 PNG 条目，因为 .NET Framework 的 Icon 对 PNG 条目支持不稳。
internal static class MakeIcon
{
    private static readonly int[] Sizes = new int[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
    private const float CornerRadiusRatio = 0.18f;

    // 注意力图（见 BuildEnergy）：小尺寸裁到视觉重心用。
    private static float[] _energy;
    private static int _gridW;
    private static int _gridH;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 && args.Length != 4)
        {
            Console.WriteLine("usage: MakeIcon <source-image> <out.ico> [--dump <dir>]");
            return 2;
        }

        string dump = null;
        if (args.Length == 4)
        {
            if (args[2] != "--dump")
            {
                Console.WriteLine("unknown option: " + args[2]);
                return 2;
            }
            dump = args[3];
            if (!Directory.Exists(dump))
            {
                Directory.CreateDirectory(dump);
            }
        }

        int mw;
        int mh;
        float[] master;
        using (Bitmap square = LoadSquare(args[0]))
        {
            mw = square.Width;
            mh = square.Height;
            master = ToLinear(square);
        }
        BuildEnergy(master, mw, mh);

        byte[][] blobs = new byte[Sizes.Length][];
        for (int i = 0; i < Sizes.Length; i++)
        {
            using (Bitmap bmp = Render(master, mw, mh, Sizes[i]))
            {
                blobs[i] = ToDib(bmp);
                if (dump != null)
                {
                    bmp.Save(Path.Combine(dump, "icon-" + Sizes[i].ToString() + ".png"), ImageFormat.Png);
                }
            }
        }

        Write(args[1], blobs);
        Console.WriteLine("ok " + Sizes.Length + " sizes " + blobs.Length + " entries");
        return 0;
    }

    private static Bitmap LoadSquare(string path)
    {
        using (Image src = Image.FromFile(path))
        {
            int side = Math.Min(src.Width, src.Height);
            int x = (src.Width - side) / 2;
            int y = (src.Height - side) / 2;
            Bitmap square = new Bitmap(side, side, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(square))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                // 1:1 裁切，禁止任何插值（这条路径不重采样，只搬像素）。
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(src, new Rectangle(0, 0, side, side), new Rectangle(x, y, side, side), GraphicsUnit.Pixel);
            }
            return square;
        }
    }

    // sRGB 8bit → 线性光浮点（只留 RGB，照片全不透明）。
    private static float[] ToLinear(Bitmap bmp)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        float[] data = new float[w * h * 3];
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[bd.Stride];
            for (int y = 0; y < h; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), row, 0, row.Length);
                for (int x = 0; x < w; x++)
                {
                    int p = x * 4;
                    int d = (y * w + x) * 3;
                    data[d] = SrgbToLinear(row[p + 2] / 255f);
                    data[d + 1] = SrgbToLinear(row[p + 1] / 255f);
                    data[d + 2] = SrgbToLinear(row[p] / 255f);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(bd);
        }
        return data;
    }

    private static float SrgbToLinear(float c)
    {
        return c <= 0.04045f ? c / 12.92f : (float)Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static float LinearToSrgb(float c)
    {
        if (c <= 0f)
        {
            return 0f;
        }
        return c <= 0.0031308f ? c * 12.92f : 1.055f * (float)Math.Pow(c, 1.0 / 2.4) - 0.055f;
    }

    private static Bitmap Render(float[] master, int mw, int mh, int size)
    {
        // 小尺寸先按注意力区放大裁切：16px 里塞下整张半身像只会糊成一团，
        // 裁到视觉重心（脸 + 剑）才认得出是个人。
        int x0;
        int y0;
        int cw;
        int ch;
        BestWindow(mw, mh, ZoomFor(size), out x0, out y0, out cw, out ch);

        int w;
        int h;
        float[] img = Crop(master, mw, mh, x0, y0, cw, ch, out w, out h);
        while (w > size * 2 && h > size * 2)
        {
            int nw;
            int nh;
            img = Halve(img, w, h, out nw, out nh);
            w = nw;
            h = nh;
        }

        float[] small = (w == size && h == size) ? img : Resample(img, w, h, size, size);
        return ToBitmap(small, size);
    }

    // 尺寸越小裁得越紧：整幅缩到 16px 只剩噪点，聚焦到脸上才有形。
    private static float ZoomFor(int size)
    {
        if (size >= 48)
        {
            return 1f;
        }
        if (size == 40)
        {
            return 0.94f;
        }
        if (size == 32)
        {
            return 0.88f;
        }
        if (size == 24)
        {
            return 0.78f;
        }
        if (size == 20)
        {
            return 0.72f;
        }
        return 0.66f;
    }

    // 在线性光图上建 64×64 的邻域梯度图，再转成积分图，用来快速找"细节最密"的窗口。
    private static void BuildEnergy(float[] linear, int w, int h)
    {
        _gridW = Math.Min(64, w);
        _gridH = Math.Min(64, h);
        float[] cell = new float[_gridW * _gridH];
        int[] count = new int[_gridW * _gridH];
        for (int y = 0; y < h; y++)
        {
            int gy = y * _gridH / h;
            for (int x = 0; x < w; x++)
            {
                int gx = x * _gridW / w;
                int i = (y * w + x) * 3;
                float luma = 0.2126f * linear[i] + 0.7152f * linear[i + 1] + 0.0722f * linear[i + 2];
                cell[gy * _gridW + gx] += luma;
                count[gy * _gridW + gx]++;
            }
        }
        for (int i = 0; i < cell.Length; i++)
        {
            cell[i] = count[i] == 0 ? 0f : cell[i] / count[i];
        }

        int stride = _gridW + 1;
        _energy = new float[stride * (_gridH + 1)];
        for (int y = 0; y < _gridH; y++)
        {
            for (int x = 0; x < _gridW; x++)
            {
                float c = cell[y * _gridW + x];
                float e = 0f;
                if (x > 0)
                {
                    e += Math.Abs(c - cell[y * _gridW + x - 1]);
                }
                if (y > 0)
                {
                    e += Math.Abs(c - cell[(y - 1) * _gridW + x]);
                }
                _energy[(y + 1) * stride + (x + 1)] = e
                    + _energy[y * stride + (x + 1)]
                    + _energy[(y + 1) * stride + x]
                    - _energy[y * stride + x];
            }
        }
    }

    private static float WindowEnergy(int gx, int gy, int gw, int gh)
    {
        int stride = _gridW + 1;
        return _energy[(gy + gh) * stride + gx + gw]
             - _energy[gy * stride + gx + gw]
             - _energy[(gy + gh) * stride + gx]
             + _energy[gy * stride + gx];
    }

    private static void BestWindow(int mw, int mh, float zoom, out int x0, out int y0, out int cw, out int ch)
    {
        int gw = Math.Max(1, (int)Math.Round(_gridW * zoom));
        int gh = Math.Max(1, (int)Math.Round(_gridH * zoom));
        if (gw > _gridW)
        {
            gw = _gridW;
        }
        if (gh > _gridH)
        {
            gh = _gridH;
        }

        float cx = _gridW * 0.5f;
        float cy = _gridH * 0.5f;
        float best = -1f;
        int bx = 0;
        int by = 0;
        for (int y = 0; y + gh <= _gridH; y++)
        {
            for (int x = 0; x + gw <= _gridW; x++)
            {
                float score = WindowEnergy(x, y, gw, gh);
                // 能量相当时偏向画面中心，避免裁到角落的高光。
                float dx = (x + gw * 0.5f) - cx;
                float dy = (y + gh * 0.5f) - cy;
                score /= 1f + (dx * dx + dy * dy) / (_gridW * _gridW * 0.35f);
                if (score > best)
                {
                    best = score;
                    bx = x;
                    by = y;
                }
            }
        }

        x0 = bx * mw / _gridW;
        y0 = by * mh / _gridH;
        cw = gw * mw / _gridW;
        ch = gh * mh / _gridH;
        if (cw < 2)
        {
            cw = 2;
        }
        if (ch < 2)
        {
            ch = 2;
        }
        if (x0 + cw > mw)
        {
            x0 = mw - cw;
        }
        if (y0 + ch > mh)
        {
            y0 = mh - ch;
        }
        if (x0 < 0)
        {
            x0 = 0;
        }
        if (y0 < 0)
        {
            y0 = 0;
        }
    }

    private static float[] Crop(float[] src, int sw, int sh, int x0, int y0, int cw, int ch, out int w, out int h)
    {
        w = cw;
        h = ch;
        float[] dst = new float[cw * ch * 3];
        for (int y = 0; y < ch; y++)
        {
            for (int x = 0; x < cw; x++)
            {
                int s = ((y0 + y) * sw + (x0 + x)) * 3;
                int d = (y * cw + x) * 3;
                dst[d] = src[s];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2];
            }
        }
        return dst;
    }

    // 2×2 盒式减半：面积平均，先杀掉细纹理和 JPEG 噪点，再谈重采样。
    private static float[] Halve(float[] src, int w, int h, out int nw, out int nh)
    {
        nw = w / 2;
        nh = h / 2;
        float[] dst = new float[nw * nh * 3];
        for (int y = 0; y < nh; y++)
        {
            for (int x = 0; x < nw; x++)
            {
                int a = ((y * 2) * w + x * 2) * 3;
                int b = a + 3;
                int c = a + w * 3;
                int d = c + 3;
                int o = (y * nw + x) * 3;
                dst[o] = (src[a] + src[b] + src[c] + src[d]) * 0.25f;
                dst[o + 1] = (src[a + 1] + src[b + 1] + src[c + 1] + src[d + 1]) * 0.25f;
                dst[o + 2] = (src[a + 2] + src[b + 2] + src[c + 2] + src[d + 2]) * 0.25f;
            }
        }
        return dst;
    }

    private static float[] Resample(float[] src, int sw, int sh, int dw, int dh)
    {
        float[] tmp = new float[sh * dw * 3];
        ResampleAxis(src, sw, sh, tmp, dw, true);
        float[] dst = new float[dw * dh * 3];
        ResampleAxis(tmp, dw, sh, dst, dh, false);
        return dst;
    }

    // 分离式 Catmull-Rom，核宽按缩小倍率放大（缩小 3 倍时用 3 倍宽的核采样，等于带低通的缩放）。
    private static void ResampleAxis(float[] src, int w, int h, float[] dst, int outLen, bool horizontal)
    {
        int srcLen = horizontal ? w : h;
        float scale = (float)srcLen / outLen;
        float widen = Math.Max(1f, scale);
        int outer = horizontal ? h : w;

        for (int i = 0; i < outLen; i++)
        {
            float center = (i + 0.5f) * scale;
            int lo = (int)Math.Floor(center - 2f * widen);
            int hi = (int)Math.Ceiling(center + 2f * widen);
            if (lo < 0)
            {
                lo = 0;
            }
            if (hi > srcLen - 1)
            {
                hi = srcLen - 1;
            }

            float[] weight = new float[hi - lo + 1];
            float sum = 0f;
            for (int s = lo; s <= hi; s++)
            {
                float v = Cubic((s + 0.5f - center) / widen);
                weight[s - lo] = v;
                sum += v;
            }
            if (sum == 0f)
            {
                weight[0] = 1f;
                sum = 1f;
            }

            for (int o = 0; o < outer; o++)
            {
                float r = 0f;
                float g = 0f;
                float b = 0f;
                for (int s = lo; s <= hi; s++)
                {
                    float k = weight[s - lo] / sum;
                    int si = horizontal ? (o * w + s) * 3 : (s * w + o) * 3;
                    r += src[si] * k;
                    g += src[si + 1] * k;
                    b += src[si + 2] * k;
                }
                int di = horizontal ? (o * outLen + i) * 3 : (i * w + o) * 3;
                dst[di] = r;
                dst[di + 1] = g;
                dst[di + 2] = b;
            }
        }
    }

    private static float Cubic(float x)
    {
        float a = Math.Abs(x);
        if (a < 1f)
        {
            return 1.5f * a * a * a - 2.5f * a * a + 1f;
        }
        if (a < 2f)
        {
            return -0.5f * a * a * a + 2.5f * a * a - 4f * a + 2f;
        }
        return 0f;
    }

    private static Bitmap ToBitmap(float[] rgb, int size)
    {
        // 小图标在缩小时会丢失对比与饱和度，按尺寸补一点点，视觉上才「清楚」。
        float contrast = size <= 24 ? 1.14f : (size <= 48 ? 1.08f : 1.04f);
        float saturation = size <= 24 ? 1.18f : (size <= 48 ? 1.12f : 1.06f);
        // 32～48 档锐化别太重，过头会在轮廓上留下白边（深色背景看得很明显）。
        float sharpen = size <= 24 ? 0.45f : (size <= 48 ? 0.11f : (size <= 96 ? 0.13f : 0.09f));

        byte[] px = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            float r = LinearToSrgb(rgb[i * 3]);
            float g = LinearToSrgb(rgb[i * 3 + 1]);
            float b = LinearToSrgb(rgb[i * 3 + 2]);
            float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            r = luma + (r - luma) * saturation;
            g = luma + (g - luma) * saturation;
            b = luma + (b - luma) * saturation;
            r = 0.5f + (r - 0.5f) * contrast;
            g = 0.5f + (g - 0.5f) * contrast;
            b = 0.5f + (b - 0.5f) * contrast;
            px[i * 4] = ToByte(r);
            px[i * 4 + 1] = ToByte(g);
            px[i * 4 + 2] = ToByte(b);
            px[i * 4 + 3] = 255;
        }

        // 极小尺寸再压一道像素级抖动：照片缩到 32px 以下，头发和背景的细纹会碎成一圈毛刺，
        // 把整体往平滑版拉一部分回来，轮廓才显得圆润。
        float denoise = size <= 16 ? 0.45f : (size <= 20 ? 0.35f : (size <= 24 ? 0.28f : (size <= 32 ? 0.25f : (size <= 40 ? 0.20f : (size <= 48 ? 0.17f : 0f)))));
        if (denoise > 0f)
        {
            BlendDenoised(px, size, denoise);
        }

        if (sharpen > 0f)
        {
            // 小尺寸先做一道保边平滑再锐化：直接锐化只会把缩图残留的噪点一起放大，
            // 看起来就是一圈"毛刺"。双边滤波只抹平同色区域内的抖动，边界留着。
            Bilateral(px, size, size <= 24 ? 0.16f : (size <= 48 ? 0.11f : (size <= 96 ? 0.07f : 0.05f)));
            UnsharpMask(px, size, sharpen);
        }

        ApplyRoundedCorners(px, size);

        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[bd.Stride];
            for (int y = 0; y < size; y++)
            {
                Array.Copy(px, y * size * 4, row, 0, size * 4);
                System.Runtime.InteropServices.Marshal.Copy(row, 0, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), row.Length);
            }
        }
        finally
        {
            bmp.UnlockBits(bd);
        }
        return bmp;
    }

    private static byte ToByte(float v)
    {
        int n = (int)Math.Round(v * 255f);
        if (n < 0)
        {
            return 0;
        }
        return n > 255 ? (byte)255 : (byte)n;
    }

    // 把像素整体往"加重平滑版"拉一部分，用来压极小尺寸上的像素级抖动。
    private static void BlendDenoised(byte[] px, int size, float factor)
    {
        byte[] smooth = new byte[px.Length];
        Array.Copy(px, smooth, px.Length);
        Bilateral(smooth, size, 0.3f);
        Bilateral(smooth, size, 0.3f);
        for (int i = 0; i < size * size; i++)
        {
            int p = i * 4;
            px[p] = Clamp(px[p] + (smooth[p] - px[p]) * factor);
            px[p + 1] = Clamp(px[p + 1] + (smooth[p + 1] - px[p + 1]) * factor);
            px[p + 2] = Clamp(px[p + 2] + (smooth[p + 2] - px[p + 2]) * factor);
        }
    }

    // 3×3 双边滤波：空间权重固定，颜色差越大权重越小（sigmaRange 按 0～1 归一，内部乘 255）。
    private static void Bilateral(byte[] px, int size, float sigmaRange)
    {
        if (sigmaRange <= 0f || size < 4)
        {
            return;
        }

        byte[] src = new byte[px.Length];
        Array.Copy(px, src, px.Length);
        float sigma = sigmaRange * 255f;
        float inv = -1f / (2f * sigma * sigma);
        float[] spatial = new float[] { 0.55f, 1f, 0.55f };

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int o = (y * size + x) * 4;
                float sr = src[o];
                float sg = src[o + 1];
                float sb = src[o + 2];
                float wr = 0f;
                float wg = 0f;
                float wb = 0f;
                float sum = 0f;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = y + dy;
                    if (yy < 0 || yy >= size)
                    {
                        continue;
                    }
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx;
                        if (xx < 0 || xx >= size)
                        {
                            continue;
                        }
                        int p = (yy * size + xx) * 4;
                        float dr = src[p] - sr;
                        float dg = src[p + 1] - sg;
                        float db = src[p + 2] - sb;
                        float d2 = dr * dr + dg * dg + db * db;
                        float w = spatial[dx + 1] * spatial[dy + 1] * (float)Math.Exp(d2 * inv);
                        wr += w * src[p];
                        wg += w * src[p + 1];
                        wb += w * src[p + 2];
                        sum += w;
                    }
                }
                if (sum <= 0f)
                {
                    continue;
                }
                px[o] = Clamp(wr / sum);
                px[o + 1] = Clamp(wg / sum);
                px[o + 2] = Clamp(wb / sum);
            }
        }
    }

    // 5×5 二项式模糊做反锐化掩膜：掩膜半径比 3×3 均值大，找回来的是"边缘"而不是"像素噪点"，
    // 所以不会把小图上的噪点一起放大成毛刺，也不会像拉普拉斯锐化那样在轮廓外描白边。
    private static void UnsharpMask(byte[] px, int size, float amount)
    {
        byte[] src = new byte[px.Length];
        Array.Copy(px, src, px.Length);
        float[] kernel = new float[] { 1f / 16f, 4f / 16f, 6f / 16f, 4f / 16f, 1f / 16f };
        float[] blur = new float[size * size * 3];
        float[] tmp = new float[size * size * 3];

        // 先横后竖，两趟分离卷积。
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float r = 0f;
                float g = 0f;
                float b = 0f;
                for (int k = -2; k <= 2; k++)
                {
                    int xx = x + k;
                    if (xx < 0)
                    {
                        xx = 0;
                    }
                    if (xx > size - 1)
                    {
                        xx = size - 1;
                    }
                    float w = kernel[k + 2];
                    int p = (y * size + xx) * 4;
                    r += src[p] * w;
                    g += src[p + 1] * w;
                    b += src[p + 2] * w;
                }
                int o = (y * size + x) * 3;
                tmp[o] = r;
                tmp[o + 1] = g;
                tmp[o + 2] = b;
            }
        }
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float r = 0f;
                float g = 0f;
                float b = 0f;
                for (int k = -2; k <= 2; k++)
                {
                    int yy = y + k;
                    if (yy < 0)
                    {
                        yy = 0;
                    }
                    if (yy > size - 1)
                    {
                        yy = size - 1;
                    }
                    float w = kernel[k + 2];
                    int p = (yy * size + x) * 3;
                    r += tmp[p] * w;
                    g += tmp[p + 1] * w;
                    b += tmp[p + 2] * w;
                }
                int o = (y * size + x) * 3;
                blur[o] = r;
                blur[o + 1] = g;
                blur[o + 2] = b;
            }
        }

        for (int i = 0; i < size * size; i++)
        {
            int p = i * 4;
            int o = i * 3;
            px[p] = Clamp(src[p] + amount * (src[p] - blur[o]));
            px[p + 1] = Clamp(src[p + 1] + amount * (src[p + 1] - blur[o + 1]));
            px[p + 2] = Clamp(src[p + 2] + amount * (src[p + 2] - blur[o + 2]));
        }
    }

    private static byte Clamp(float v)
    {
        int n = (int)Math.Round(v);
        if (n < 0)
        {
            return 0;
        }
        return n > 255 ? (byte)255 : (byte)n;
    }

    // 4×4 超采样算圆角覆盖度，边缘才有过渡像素，不会出现阶梯毛刺。
    private static void ApplyRoundedCorners(byte[] px, int size)
    {
        float radius = Math.Max(1.5f, size * CornerRadiusRatio);
        const int Steps = 4;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int inside = 0;
                for (int sy = 0; sy < Steps; sy++)
                {
                    for (int sx = 0; sx < Steps; sx++)
                    {
                        float px0 = x + (sx + 0.5f) / Steps;
                        float py0 = y + (sy + 0.5f) / Steps;
                        if (InsideRounded(px0, py0, size, radius))
                        {
                            inside++;
                        }
                    }
                }
                px[(y * size + x) * 4 + 3] = (byte)(inside * 255 / (Steps * Steps));
            }
        }
    }

    private static bool InsideRounded(float x, float y, int size, float radius)
    {
        float cx = Math.Min(Math.Max(x, radius), size - radius);
        float cy = Math.Min(Math.Max(y, radius), size - radius);
        float dx = x - cx;
        float dy = y - cy;
        return dx * dx + dy * dy <= radius * radius;
    }

    private static byte[] ToDib(Bitmap bmp)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        int stride = w * 4;
        int maskStride = ((w + 31) / 32) * 4;
        int size = 40 + stride * h + maskStride * h;
        byte[] data = new byte[size];

        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            WriteInt(data, 0, 40);
            WriteInt(data, 4, w);
            WriteInt(data, 8, h * 2);
            WriteShort(data, 12, 1);
            WriteShort(data, 14, 32);
            WriteInt(data, 20, stride * h);
            for (int row = 0; row < h; row++)
            {
                IntPtr p = new IntPtr(bd.Scan0.ToInt64() + (long)(h - 1 - row) * bd.Stride);
                System.Runtime.InteropServices.Marshal.Copy(p, data, 40 + row * stride, stride);
            }
            // AND 掩码：alpha 低于一半的像素记为透明，老式 16/24 位显示下圆角不会发黑。
            int maskOffset = 40 + stride * h;
            for (int row = 0; row < h; row++)
            {
                for (int col = 0; col < w; col++)
                {
                    if (data[40 + row * stride + col * 4 + 3] < 128)
                    {
                        int bit = maskOffset + row * maskStride + (col >> 3);
                        data[bit] |= (byte)(0x80 >> (col & 7));
                    }
                }
            }
        }
        finally
        {
            bmp.UnlockBits(bd);
        }
        return data;
    }

    private static void Write(string path, byte[][] blobs)
    {
        using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)blobs.Length);
            int offset = 6 + 16 * blobs.Length;
            for (int i = 0; i < blobs.Length; i++)
            {
                int size = Sizes[i];
                bw.Write((byte)(size >= 256 ? 0 : size));
                bw.Write((byte)(size >= 256 ? 0 : size));
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write(blobs[i].Length);
                bw.Write(offset);
                offset += blobs[i].Length;
            }
            for (int i = 0; i < blobs.Length; i++)
            {
                bw.Write(blobs[i]);
            }
        }
    }

    private static void WriteInt(byte[] buffer, int offset, int value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, buffer, offset, 4);
    }

    private static void WriteShort(byte[] buffer, int offset, short value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, buffer, offset, 2);
    }
}
