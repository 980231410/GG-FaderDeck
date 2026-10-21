param(
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$PreviewPath,
    [int]$BackgroundLuminance = 195,
    [int]$BackgroundSaturation = 18
)

# 把一张方形应用图标原图（浅色背景 / 棋盘格）转成带透明通道的多尺寸 .ico。
# 背景从四边泛洪移除，随后按不透明包围盒居中裁成正方形，再导出 16–256 的 PNG 帧。
# 主体逻辑在 C# 里完成，PowerShell 只负责传参。

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$code = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class IconMaker
{
    public static string Build(string sourcePath, string outputPath, string previewPath, int lumLimit, int satLimit)
    {
        using (var source = new Bitmap(sourcePath))
        using (var argb = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(argb))
            {
                g.Clear(Color.Transparent);
                g.DrawImage(source, 0, 0, source.Width, source.Height);
            }

            int width = argb.Width, height = argb.Height;
            var rect = new Rectangle(0, 0, width, height);
            var data = argb.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            var buffer = new byte[stride * height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

            var visited = new bool[width * height];
            var queue = new Queue<int>();

            Func<int, int, bool> isBackground = (x, y) =>
            {
                int i = (y * stride) + (x * 4);
                int b = buffer[i], g2 = buffer[i + 1], r = buffer[i + 2];
                double lum = (0.299 * r) + (0.587 * g2) + (0.114 * b);
                if (lum < lumLimit) return false;
                int max = Math.Max(r, Math.Max(g2, b));
                int min = Math.Min(r, Math.Min(g2, b));
                return (max - min) <= satLimit;
            };

            Action<int, int> push = (x, y) =>
            {
                if (x < 0 || y < 0 || x >= width || y >= height) return;
                int flat = (y * width) + x;
                if (visited[flat]) return;
                if (!isBackground(x, y)) return;
                visited[flat] = true;
                queue.Enqueue(flat);
            };

            for (int x = 0; x < width; x++) { push(x, 0); push(x, height - 1); }
            for (int y = 0; y < height; y++) { push(0, y); push(width - 1, y); }

            while (queue.Count > 0)
            {
                int flat = queue.Dequeue();
                int x = flat % width;
                int y = flat / width;
                push(x - 1, y); push(x + 1, y); push(x, y - 1); push(x, y + 1);
            }

            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (visited[(y * width) + x])
                    {
                        buffer[(y * stride) + (x * 4) + 3] = 0;
                    }
                    else
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            System.Runtime.InteropServices.Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
            argb.UnlockBits(data);

            if (maxX <= minX || maxY <= minY) throw new InvalidOperationException("The artwork became fully transparent.");

            int contentWidth = maxX - minX + 1;
            int contentHeight = maxY - minY + 1;
            int side = Math.Max(contentWidth, contentHeight);
            int canvasSide = (int)Math.Ceiling(side * 1.06);

            using (var canvas = new Bitmap(canvasSide, canvasSide, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.Transparent);
                    g.CompositingMode = CompositingMode.SourceCopy;
                    int offsetX = (canvasSide - contentWidth) / 2;
                    int offsetY = (canvasSide - contentHeight) / 2;
                    g.DrawImage(argb,
                        new Rectangle(offsetX, offsetY, contentWidth, contentHeight),
                        new Rectangle(minX, minY, contentWidth, contentHeight),
                        GraphicsUnit.Pixel);
                }

                int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };
                var frames = new List<byte[]>();
                foreach (int size in sizes) frames.Add(Encode(canvas, size));

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
                using (var file = File.Create(Path.GetFullPath(outputPath)))
                using (var writer = new BinaryWriter(file))
                {
                    writer.Write((ushort)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)sizes.Length);
                    int offset = 6 + (16 * sizes.Length);
                    for (int i = 0; i < sizes.Length; i++)
                    {
                        writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                        writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                        writer.Write((byte)0);
                        writer.Write((byte)0);
                        writer.Write((ushort)1);
                        writer.Write((ushort)32);
                        writer.Write((uint)frames[i].Length);
                        writer.Write((uint)offset);
                        offset += frames[i].Length;
                    }
                    foreach (var frame in frames) writer.Write(frame);
                }

                if (!string.IsNullOrEmpty(previewPath))
                {
                    var preview = Resize(canvas, 512);
                    try { preview.Save(Path.GetFullPath(previewPath), ImageFormat.Png); }
                    finally { preview.Dispose(); }
                }

                return string.Format("{0} (content {1}x{2}, canvas {3})", Path.GetFullPath(outputPath), contentWidth, contentHeight, canvasSide);
            }
        }
    }

    static Bitmap Resize(Bitmap source, int size)
    {
        var output = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(output))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawImage(source, new Rectangle(0, 0, size, size));
        }
        return output;
    }

    static byte[] Encode(Bitmap canvas, int size)
    {
        if (size <= 24)
        {
            using (var small = DrawSmall(size))
            using (var stream = new MemoryStream())
            {
                small.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }

        using (var scaled = Resize(canvas, size))
        using (var stream = new MemoryStream())
        {
            scaled.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
    }

    // Frames at 16/20/24 px are redrawn as vectors; downscaling the artwork turns to mush.
    static Bitmap DrawSmall(int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            float inset = size * 0.03f;
            float side = size - (inset * 2);
            float radius = side * 0.22f;

            using (var path = RoundedRect(inset, inset, side, side, radius))
            using (var fill = new SolidBrush(Color.FromArgb(255, 23, 31, 36)))
            {
                g.FillPath(fill, path);
                using (var pen = new Pen(Color.FromArgb(255, 102, 117, 255), Math.Max(1f, size * 0.055f)))
                {
                    g.DrawPath(pen, path);
                }
            }

            var accents = new[]
            {
                Color.FromArgb(255, 103, 117, 255),
                Color.FromArgb(255, 45, 177, 252),
                Color.FromArgb(255, 2, 221, 188)
            };
            var knobFractions = new[] { 0.37f, 0.55f, 0.66f };
            float railWidth = Math.Max(1f, size * 0.05f);
            float knobWidth = size * 0.21f;
            float knobHeight = size * 0.25f;

            for (int i = 0; i < 3; i++)
            {
                float x = size * (0.29f + (0.21f * i));
                using (var rail = new Pen(Color.FromArgb(235, 210, 219, 230), railWidth))
                {
                    rail.StartCap = LineCap.Round;
                    rail.EndCap = LineCap.Round;
                    g.DrawLine(rail, x, size * 0.20f, x, size * 0.80f);
                }

                float knobTop = (size * knobFractions[i]) - (knobHeight / 2f);
                using (var knobPath = RoundedRect(x - (knobWidth / 2f), knobTop, knobWidth, knobHeight, knobWidth * 0.45f))
                using (var knobBrush = new SolidBrush(accents[i]))
                {
                    g.FillPath(knobBrush, knobPath);
                }
            }
        }
        return bitmap;
    }

    static GraphicsPath RoundedRect(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2f;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
'@

if (-not ('IconMaker' -as [type])) {
    Add-Type -TypeDefinition $code -ReferencedAssemblies System.Drawing
}

[IconMaker]::Build($SourcePath, $OutputPath, $PreviewPath, $BackgroundLuminance, $BackgroundSaturation)
