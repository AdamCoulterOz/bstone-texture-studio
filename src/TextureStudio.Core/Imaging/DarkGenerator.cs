namespace TextureStudio.Core.Imaging;

/// <summary>Universal light-to-dark transform for generating the engine's east/west face
/// variants from redrawn light tiles. Works in linear color so hue doesn't shift.</summary>
public sealed record DarkParams(double Multiply = 0.62, double Gamma = 1.0, double Saturation = 1.0)
{
    public static DarkParams Default { get; } = new();
}

public static class DarkGenerator
{
    public static RgbaImage Apply(RgbaImage source, DarkParams p)
    {
        var result = new RgbaImage(source.Width, source.Height);
        for (var i = 0; i < source.Pixels.Length; i += 4)
        {
            var r = SrgbToLinear(source.Pixels[i]);
            var g = SrgbToLinear(source.Pixels[i + 1]);
            var b = SrgbToLinear(source.Pixels[i + 2]);
            r = p.Multiply * Math.Pow(r, p.Gamma);
            g = p.Multiply * Math.Pow(g, p.Gamma);
            b = p.Multiply * Math.Pow(b, p.Gamma);
            if (Math.Abs(p.Saturation - 1.0) > 1e-9)
            {
                var lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                r = lum + p.Saturation * (r - lum);
                g = lum + p.Saturation * (g - lum);
                b = lum + p.Saturation * (b - lum);
            }
            result.Pixels[i] = LinearToSrgb(r);
            result.Pixels[i + 1] = LinearToSrgb(g);
            result.Pixels[i + 2] = LinearToSrgb(b);
            result.Pixels[i + 3] = source.Pixels[i + 3];
        }
        return result;
    }

    private static double SrgbToLinear(byte v)
    {
        var c = v / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static byte LinearToSrgb(double c)
    {
        c = Math.Clamp(c, 0, 1);
        var s = c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        return (byte)Math.Clamp(Math.Round(s * 255), 0, 255);
    }
}
