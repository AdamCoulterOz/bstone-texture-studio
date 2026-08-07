using TextureStudio.Core.Imaging;

namespace TextureStudio.Core.Generation;

public sealed record RevisionRegion(int X, int Y, int Width, int Height);

/// <summary>Region-scoped revision support: annotate regions for the model to see, then
/// composite its output back into the original strictly inside those regions — a hard
/// constraint the model itself never guarantees.</summary>
public static class RevisionTools
{
    /// <summary>Up to four distinguishable outline colors, indexed by revision block.</summary>
    public static readonly string[] ColorNames = ["red", "blue", "green", "orange"];

    private static readonly (byte R, byte G, byte B)[] Palette =
    [
        (255, 0, 0), (0, 90, 255), (0, 200, 0), (255, 140, 0),
    ];

    /// <summary>Returns a copy with a colored outline drawn around each region (color chosen
    /// per entry), used as the model-facing annotated input.</summary>
    public static RgbaImage Annotate(
        RgbaImage source, IReadOnlyList<(RevisionRegion Region, int ColorIndex)> regions)
    {
        var copy = new RgbaImage(source.Width, source.Height, (byte[])source.Pixels.Clone());
        foreach (var (region, colorIndex) in regions)
        {
            DrawRect(copy, region, Math.Max(2, source.Width / 300),
                Palette[Math.Clamp(colorIndex, 0, Palette.Length - 1)]);
        }
        return copy;
    }

    /// <summary>Composites <paramref name="revised"/> (resampled to the original's size if
    /// needed) into <paramref name="original"/>, only inside the regions, with a small feather
    /// at region borders so edits blend instead of seaming.</summary>
    public static RgbaImage CompositeRegions(
        RgbaImage original, RgbaImage revised, IReadOnlyList<RevisionRegion> regions, int featherPx = 4)
    {
        var fitted = revised.Width == original.Width && revised.Height == original.Height
            ? revised
            : revised.Resample(original.Width, original.Height);
        var result = new RgbaImage(original.Width, original.Height, (byte[])original.Pixels.Clone());
        foreach (var r in regions)
        {
            var x0 = Math.Max(0, r.X);
            var y0 = Math.Max(0, r.Y);
            var x1 = Math.Min(original.Width, r.X + r.Width);
            var y1 = Math.Min(original.Height, r.Y + r.Height);
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    var edge = Math.Min(
                        Math.Min(x - x0, x1 - 1 - x),
                        Math.Min(y - y0, y1 - 1 - y));
                    var alpha = featherPx > 0 ? Math.Min(1.0, (edge + 1) / (double)featherPx) : 1.0;
                    var o = result.Offset(x, y);
                    for (var c = 0; c < 4; c++)
                    {
                        result.Pixels[o + c] = (byte)Math.Round(
                            result.Pixels[o + c] * (1 - alpha) + fitted.Pixels[o + c] * alpha);
                    }
                }
            }
        }
        return result;
    }

    private static void DrawRect(RgbaImage img, RevisionRegion r, int thickness, (byte R, byte G, byte B) color)
    {
        for (var t = 0; t < thickness; t++)
        {
            var x0 = Math.Max(0, r.X - t);
            var y0 = Math.Max(0, r.Y - t);
            var x1 = Math.Min(img.Width - 1, r.X + r.Width - 1 + t);
            var y1 = Math.Min(img.Height - 1, r.Y + r.Height - 1 + t);
            for (var x = x0; x <= x1; x++)
            {
                SetPixel(img, x, y0, color);
                SetPixel(img, x, y1, color);
            }
            for (var y = y0; y <= y1; y++)
            {
                SetPixel(img, x0, y, color);
                SetPixel(img, x1, y, color);
            }
        }
    }

    private static void SetPixel(RgbaImage img, int x, int y, (byte R, byte G, byte B) color)
    {
        var o = img.Offset(x, y);
        img.Pixels[o] = color.R;
        img.Pixels[o + 1] = color.G;
        img.Pixels[o + 2] = color.B;
        img.Pixels[o + 3] = 255;
    }
}
