using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// 把一张方图/照片转成多尺寸 .ico（16/20/24/32/48/64/128/256，全部 32bpp DIB 条目）。
//
// 用法（不参与 build.ps1 / test.ps1，改了图标才手动跑一次）：
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:exe ^
//       /codepage:65001 /out:开发\tests\MakeIcon.exe /r:System.dll /r:System.Drawing.dll ^
//       开发\tools\MakeIcon.cs
//   开发\tests\MakeIcon.exe 开发\assets\icon-source.jpg 开发\assets\app.ico
//
// 从源图居中裁正方形再逐尺寸高质量重采样；条目写成 BMP 形式的 DIB 而不是 PNG 条目，
// 因为 .NET Framework 的 Icon/ExtractAssociatedIcon 对 PNG 条目支持不稳定。
internal static class MakeIcon
{
    private static readonly int[] Sizes = new int[] { 16, 20, 24, 32, 48, 64, 128, 256 };

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.WriteLine("usage: MakeIcon <source-image> <out.ico>");
            return 2;
        }

        using (Image src = Image.FromFile(args[0]))
        {
            int side = Math.Min(src.Width, src.Height);
            int x = (src.Width - side) / 2;
            int y = (src.Height - side) / 2;
            using (Bitmap square = new Bitmap(side, side, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(square))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(src, new Rectangle(0, 0, side, side), new Rectangle(x, y, side, side), GraphicsUnit.Pixel);
                }

                byte[][] blobs = new byte[Sizes.Length][];
                for (int i = 0; i < Sizes.Length; i++)
                {
                    using (Bitmap bmp = Resize(square, Sizes[i]))
                    {
                        blobs[i] = ToDib(bmp);
                    }
                }
                Write(args[1], blobs);
            }
        }

        Console.WriteLine("ok");
        return 0;
    }

    private static Bitmap Resize(Bitmap source, int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            using (ImageAttributes a = new ImageAttributes())
            {
                a.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, a);
            }
        }
        return bmp;
    }

    // 单张图像写成 32bpp BMP 形式的 DIB（BITMAPINFOHEADER + XOR 位图 + AND 掩码），
    // 与项目原 app.ico 的存法一致，避免 .NET Icon 对 PNG 压缩条目的兼容问题。
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
        }
        finally
        {
            bmp.UnlockBits(bd);
        }
        // AND 掩码全 0：照片不透明，透明通道交给 32bpp 的 alpha。
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
