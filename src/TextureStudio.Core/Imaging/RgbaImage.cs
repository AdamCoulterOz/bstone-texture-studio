namespace TextureStudio.Core.Imaging;

/// <summary>A plain RGBA8888 bitmap. All Core image logic operates on these; the UI layer
/// converts to and from PNG via browser canvas interop.</summary>
public sealed class RgbaImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public RgbaImage(int width, int height, byte[]? pixels = null)
    {
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
        if (Pixels.Length != width * height * 4)
        {
            throw new ArgumentException("Pixel buffer size mismatch.");
        }
    }

    public int Offset(int x, int y) => (y * Width + x) * 4;

    public void Fill(byte r, byte g, byte b, byte a = 255)
    {
        for (var i = 0; i < Pixels.Length; i += 4)
        {
            Pixels[i] = r; Pixels[i + 1] = g; Pixels[i + 2] = b; Pixels[i + 3] = a;
        }
    }

    public void Paste(RgbaImage src, int dx, int dy)
    {
        for (var y = 0; y < src.Height; y++)
        {
            var ty = dy + y;
            if (ty < 0 || ty >= Height)
            {
                continue;
            }
            var srcRow = y * src.Width * 4;
            var dstRow = (ty * Width + dx) * 4;
            var count = Math.Min(src.Width, Width - dx) * 4;
            if (count > 0)
            {
                Array.Copy(src.Pixels, srcRow, Pixels, dstRow, count);
            }
        }
    }

    public RgbaImage Crop(int x, int y, int w, int h)
    {
        var result = new RgbaImage(w, h);
        for (var row = 0; row < h; row++)
        {
            Array.Copy(Pixels, ((y + row) * Width + x) * 4, result.Pixels, row * w * 4, w * 4);
        }
        return result;
    }

    public RgbaImage ScaleNearest(int factor)
    {
        var result = new RgbaImage(Width * factor, Height * factor);
        for (var y = 0; y < result.Height; y++)
        {
            var sy = y / factor;
            for (var x = 0; x < result.Width; x++)
            {
                var so = Offset(x / factor, sy);
                var d = result.Offset(x, y);
                result.Pixels[d] = Pixels[so];
                result.Pixels[d + 1] = Pixels[so + 1];
                result.Pixels[d + 2] = Pixels[so + 2];
                result.Pixels[d + 3] = Pixels[so + 3];
            }
        }
        return result;
    }

    /// <summary>Resample to an arbitrary size: box-average when shrinking, bilinear when growing.
    /// Good enough for tile art; swap for a fancier kernel later if edges look soft.</summary>
    public RgbaImage Resample(int newWidth, int newHeight)
    {
        if (newWidth == Width && newHeight == Height)
        {
            return this;
        }
        var result = new RgbaImage(newWidth, newHeight);
        var xRatio = (double)Width / newWidth;
        var yRatio = (double)Height / newHeight;
        var shrinking = xRatio > 1.0 || yRatio > 1.0;
        for (var y = 0; y < newHeight; y++)
        {
            for (var x = 0; x < newWidth; x++)
            {
                var d = result.Offset(x, y);
                if (shrinking)
                {
                    BoxSample(x * xRatio, y * yRatio, (x + 1) * xRatio, (y + 1) * yRatio, result.Pixels, d);
                }
                else
                {
                    BilinearSample((x + 0.5) * xRatio - 0.5, (y + 0.5) * yRatio - 0.5, result.Pixels, d);
                }
            }
        }
        return result;
    }

    private void BoxSample(double x0, double y0, double x1, double y1, byte[] dst, int d)
    {
        var ix0 = Math.Clamp((int)x0, 0, Width - 1);
        var iy0 = Math.Clamp((int)y0, 0, Height - 1);
        var ix1 = Math.Clamp((int)Math.Ceiling(x1), 1, Width);
        var iy1 = Math.Clamp((int)Math.Ceiling(y1), 1, Height);
        // Scalar accumulators — this runs per OUTPUT pixel, so no allocations allowed here.
        double sr = 0, sg = 0, sb = 0, sa = 0;
        var n = 0;
        for (var y = iy0; y < iy1; y++)
        {
            var o = (y * Width + ix0) * 4;
            for (var x = ix0; x < ix1; x++, o += 4)
            {
                sr += Pixels[o]; sg += Pixels[o + 1]; sb += Pixels[o + 2]; sa += Pixels[o + 3];
                n++;
            }
        }
        if (n == 0)
        {
            n = 1;
        }
        dst[d] = (byte)Math.Clamp(sr / n, 0, 255);
        dst[d + 1] = (byte)Math.Clamp(sg / n, 0, 255);
        dst[d + 2] = (byte)Math.Clamp(sb / n, 0, 255);
        dst[d + 3] = (byte)Math.Clamp(sa / n, 0, 255);
    }

    private void BilinearSample(double sx, double sy, byte[] dst, int d)
    {
        var x0 = Math.Clamp((int)Math.Floor(sx), 0, Width - 1);
        var y0 = Math.Clamp((int)Math.Floor(sy), 0, Height - 1);
        var x1 = Math.Min(x0 + 1, Width - 1);
        var y1 = Math.Min(y0 + 1, Height - 1);
        var fx = Math.Clamp(sx - x0, 0, 1);
        var fy = Math.Clamp(sy - y0, 0, 1);
        for (var c = 0; c < 4; c++)
        {
            var top = Pixels[Offset(x0, y0) + c] * (1 - fx) + Pixels[Offset(x1, y0) + c] * fx;
            var bottom = Pixels[Offset(x0, y1) + c] * (1 - fx) + Pixels[Offset(x1, y1) + c] * fx;
            dst[d + c] = (byte)Math.Clamp(top * (1 - fy) + bottom * fy, 0, 255);
        }
    }
}
