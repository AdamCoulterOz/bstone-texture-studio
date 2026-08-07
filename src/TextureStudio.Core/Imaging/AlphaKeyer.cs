namespace TextureStudio.Core.Imaging;

/// <summary>Restores transparency on redrawn sprite cells that came back with an opaque
/// background fill.
///
/// Two strategies:
/// - Matte key: when cells were composed with the known matte color (magenta) and the model
///   preserved it, a global soft chroma key — immune to white-on-white art leaks.
/// - Border flood fill: for white-filled sheets (e.g. pasted from the Gemini app), clears only
///   background regions connected to the cell edge, so interior art matching the background
///   color survives.</summary>
public static class AlphaKeyer
{
    /// <summary>The matte color composed behind sprite cells: full magenta, a color that
    /// never occurs in the game's art.</summary>
    public const byte MatteR = 255, MatteG = 0, MatteB = 255;

    /// <summary>Picks the right strategy per cell. Cells that already carry real transparency
    /// are trusted as-is (margin + dust cleanup only): the model sometimes keys the matte
    /// itself and returns true alpha, and transparent pixels decode as black — flood-filling
    /// against that would eat dark cel outlines. Otherwise: matte key when the corners are
    /// matte-colored, else border flood fill. Returns the strategy used, for status
    /// reporting.</summary>
    public static string KeyAuto(RgbaImage img)
    {
        if (HasTransparentCorners(img))
        {
            SuppressFringe(img);
            return "alpha";
        }
        var (r, g, b) = InsetCornerColor(img);
        var isMatte = Math.Abs(r - MatteR) < 70 && g < 110 && Math.Abs(b - MatteB) < 70;
        if (isMatte)
        {
            KeyOutMatte(img);
            return "matte";
        }
        KeyOutBorderConnectedBackground(img);
        return "flood";
    }

    /// <summary>Global soft chroma key against the magenta matte, with despill unmixing:
    /// semi-transparent art drawn over the matte (light glows, smoke) is modeled as
    /// art×α + matte×(1−α); the matte fraction is estimated from magenta dominance
    /// (min(R,B) − G, gated to matte-balanced pixels so genuine purple art survives), the
    /// true art color is recovered, and alpha becomes the art fraction. A second pass
    /// handles WARM glow blends (amber/fire halos painted over the matte): those diverge
    /// R from B, dodging the balance gate and surviving as a pink halo — inside a band
    /// near the keyed background they get a maximal-matte unmix instead.</summary>
    public static void KeyOutMatte(RgbaImage img, int threshold = 110)
    {
        for (var i = 0; i < img.Pixels.Length; i += 4)
        {
            var r = img.Pixels[i];
            var g = img.Pixels[i + 1];
            var b = img.Pixels[i + 2];
            var d = Math.Max(
                Math.Max(Math.Abs(r - MatteR), Math.Abs(g - MatteG)),
                Math.Abs(b - MatteB));
            var half = threshold / 2;
            if (d <= half)
            {
                img.Pixels[i + 3] = 0;
                continue;
            }
            if (d < threshold)
            {
                img.Pixels[i + 3] = (byte)(255 * (d - half) / Math.Max(1, threshold - half));
            }
            // Despill: only for matte-balanced pixels (R≈B) so purple/violet art is safe.
            if (Math.Abs(r - b) > 60)
            {
                continue;
            }
            var matteFraction = Math.Clamp((Math.Min(r, b) - g - 8) / 247.0, 0.0, 1.0);
            if (matteFraction <= 0.03)
            {
                continue;
            }
            var artFraction = 1.0 - matteFraction;
            img.Pixels[i] = (byte)Math.Clamp((r - matteFraction * MatteR) / artFraction, 0, 255);
            img.Pixels[i + 1] = (byte)Math.Clamp(g / artFraction, 0, 255);
            img.Pixels[i + 2] = (byte)Math.Clamp((b - matteFraction * MatteB) / artFraction, 0, 255);
            img.Pixels[i + 3] = Math.Min(img.Pixels[i + 3], (byte)Math.Round(artFraction * 255));
        }
        UnmixWarmGlow(img);
        SuppressFringe(img);
    }

    /// <summary>Warm-glow rescue: within a band near the keyed background, pixels that are
    /// warm (|R−B| large, B−G high — magenta pushed B up and G down) get a maximal-matte
    /// unmix (τ = min(min(R,B), 255−G)/255), turning painted glow halos into genuine
    /// translucent glow. Interior art never qualifies: it is either balanced, not
    /// magenta-bright, or outside the band.</summary>
    private static void UnmixWarmGlow(RgbaImage img)
    {
        var w = img.Width;
        var h = img.Height;
        var band = Math.Max(8, (int)(Math.Min(w, h) * 0.09));
        var dist = new int[w * h];
        Array.Fill(dist, int.MaxValue);
        var queue = new Queue<int>();
        for (var i = 0; i < w * h; i++)
        {
            if (img.Pixels[i * 4 + 3] == 0)
            {
                dist[i] = 0;
                queue.Enqueue(i);
            }
        }
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            if (dist[i] >= band)
            {
                continue;
            }
            var y = i / w;
            var x = i % w;
            foreach (var j in stackalloc[] { i - 1, i + 1, i - w, i + w })
            {
                if (j < 0 || j >= w * h || (j == i - 1 && x == 0) || (j == i + 1 && x == w - 1))
                {
                    continue;
                }
                if (dist[j] > dist[i] + 1)
                {
                    dist[j] = dist[i] + 1;
                    queue.Enqueue(j);
                }
            }
        }
        for (var i = 0; i < w * h; i++)
        {
            var o = i * 4;
            if (img.Pixels[o + 3] == 0 || dist[i] > band)
            {
                continue;
            }
            var r = img.Pixels[o];
            var g = img.Pixels[o + 1];
            var b = img.Pixels[o + 2];
            // Warm = R-dominant blends only; blue-violet art (B >> R) must never qualify.
            if (r - b <= 60 || b - g < 35 || Math.Min(r, b) < 120)
            {
                continue;
            }
            var tau = 0.97 * Math.Min(Math.Min(r, b), 255 - g) / 255.0;
            if (tau < 0.12)
            {
                continue;
            }
            var art = 1.0 - tau;
            img.Pixels[o] = (byte)Math.Clamp((r - tau * MatteR) / art, 0, 255);
            img.Pixels[o + 1] = (byte)Math.Clamp(g / art, 0, 255);
            img.Pixels[o + 2] = (byte)Math.Clamp((b - tau * MatteB) / art, 0, 255);
            img.Pixels[o + 3] = Math.Min(img.Pixels[o + 3], (byte)Math.Round(art * 255));
        }
    }

    /// <summary>Fringe suppression: cell-border slivers can survive slicing, and heavily-matte
    /// edge blends despill to near-transparent pink dust. Sprite art never touches the
    /// cell edge, and sub-16% alpha carries no visible art — clear both.</summary>
    private static void SuppressFringe(RgbaImage img)
    {
        var margin = Math.Max(2, Math.Min(img.Width, img.Height) / 100);
        for (var y = 0; y < img.Height; y++)
        {
            for (var x = 0; x < img.Width; x++)
            {
                var edge = x < margin || y < margin ||
                           x >= img.Width - margin || y >= img.Height - margin;
                var o = img.Offset(x, y);
                if (edge || img.Pixels[o + 3] < 40)
                {
                    img.Pixels[o + 3] = 0;
                }
            }
        }
    }

    /// <summary>True when the inset corner blocks are already (almost entirely) transparent —
    /// the model returned a pre-keyed cell with a real alpha channel.</summary>
    private static bool HasTransparentCorners(RgbaImage img)
    {
        var transparent = 0;
        var total = 0;
        foreach (var (cx, cy, block) in InsetCornerBlocks(img))
        {
            for (var y = cy; y < cy + block; y++)
            {
                for (var x = cx; x < cx + block; x++)
                {
                    total++;
                    if (img.Pixels[img.Offset(x, y) + 3] < 16)
                    {
                        transparent++;
                    }
                }
            }
        }
        return transparent >= total * 9 / 10;
    }

    /// <summary>Flood-fills from the cell edge inward, clearing pixels within
    /// <paramref name="tolerance"/> (max per-channel difference) of the background color to
    /// alpha 0, then anti-aliases the silhouette edge.
    ///
    /// The background color is estimated from small blocks inset from the corners — never from
    /// the outermost ring, which may contain slivers of sheet gutter from imprecise slicing.
    /// That outermost margin is cleared unconditionally (sprite art never reaches the cell
    /// edge) so gutter slivers can't poison the fill.</summary>
    public static void KeyOutBorderConnectedBackground(RgbaImage img, int tolerance = 45)
    {
        var w = img.Width;
        var h = img.Height;
        var margin = Math.Max(2, Math.Min(w, h) / 100);
        var (bgR, bgG, bgB) = InsetCornerColor(img);
        var cleared = new bool[w * h];
        var queue = new Queue<int>();

        // Unconditionally clear the outer margin (gutter slivers + slice edge noise).
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (x < margin || y < margin || x >= w - margin || y >= h - margin)
                {
                    cleared[y * w + x] = true;
                }
            }
        }

        void TryEnqueue(int x, int y)
        {
            var index = y * w + x;
            if (cleared[index])
            {
                return;
            }
            var o = index * 4;
            if (Math.Abs(img.Pixels[o] - bgR) <= tolerance &&
                Math.Abs(img.Pixels[o + 1] - bgG) <= tolerance &&
                Math.Abs(img.Pixels[o + 2] - bgB) <= tolerance)
            {
                cleared[index] = true;
                queue.Enqueue(index);
            }
        }

        // Seed from the ring just inside the cleared margin.
        for (var x = margin; x < w - margin; x++)
        {
            TryEnqueue(x, margin);
            TryEnqueue(x, h - margin - 1);
        }
        for (var y = margin; y < h - margin; y++)
        {
            TryEnqueue(margin, y);
            TryEnqueue(w - margin - 1, y);
        }
        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            var x = index % w;
            var y = index / w;
            if (x > 0) TryEnqueue(x - 1, y);
            if (x < w - 1) TryEnqueue(x + 1, y);
            if (y > 0) TryEnqueue(x, y - 1);
            if (y < h - 1) TryEnqueue(x, y + 1);
        }

        for (var i = 0; i < cleared.Length; i++)
        {
            if (cleared[i])
            {
                img.Pixels[i * 4 + 3] = 0;
            }
        }
        AntiAliasSilhouette(img, cleared);
    }

    /// <summary>Softens the opaque/transparent boundary: pixels on the silhouette edge get
    /// alpha equal to the 3x3 neighborhood's opacity mean.</summary>
    private static void AntiAliasSilhouette(RgbaImage img, bool[] cleared)
    {
        var w = img.Width;
        var h = img.Height;
        var edgeAlpha = new Dictionary<int, byte>();
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var index = y * w + x;
                if (cleared[index])
                {
                    continue;
                }
                var opaque = 0;
                var total = 0;
                var touchesBackground = false;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx;
                        var ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                        {
                            continue;
                        }
                        total++;
                        if (cleared[ny * w + nx])
                        {
                            touchesBackground = true;
                        }
                        else
                        {
                            opaque++;
                        }
                    }
                }
                if (touchesBackground)
                {
                    edgeAlpha[index] = (byte)(255 * opaque / Math.Max(1, total));
                }
            }
        }
        foreach (var (index, alpha) in edgeAlpha)
        {
            img.Pixels[index * 4 + 3] = Math.Min(img.Pixels[index * 4 + 3], alpha);
        }
    }

    /// <summary>Fraction of the image height where the art's lowest opaque row sits
    /// (0 = top, 1 = bottom); null when the image is fully transparent.</summary>
    public static double? BaselineFraction(RgbaImage img)
    {
        for (var y = img.Height - 1; y >= 0; y--)
        {
            for (var x = 0; x < img.Width; x++)
            {
                if (img.Pixels[img.Offset(x, y) + 3] > 128)
                {
                    return (y + 1) / (double)img.Height;
                }
            }
        }
        return null;
    }

    /// <summary>Median color of four small blocks inset from the corners — a robust estimate
    /// of the cell background that ignores slice-edge noise.</summary>
    private static (byte R, byte G, byte B) InsetCornerColor(RgbaImage img)
    {
        var rs = new List<byte>();
        var gs = new List<byte>();
        var bs = new List<byte>();
        foreach (var (cx, cy, block) in InsetCornerBlocks(img))
        {
            for (var y = cy; y < cy + block; y++)
            {
                for (var x = cx; x < cx + block; x++)
                {
                    var o = img.Offset(x, y);
                    rs.Add(img.Pixels[o]);
                    gs.Add(img.Pixels[o + 1]);
                    bs.Add(img.Pixels[o + 2]);
                }
            }
        }
        static byte Median(List<byte> values)
        {
            values.Sort();
            return values[values.Count / 2];
        }
        return (Median(rs), Median(gs), Median(bs));
    }

    private static IEnumerable<(int X, int Y, int Block)> InsetCornerBlocks(RgbaImage img)
    {
        var margin = Math.Max(2, Math.Min(img.Width, img.Height) / 100);
        var inset = margin + 2;
        var block = Math.Max(2, Math.Min(8, img.Width / 16));
        yield return (inset, inset, block);
        yield return (img.Width - inset - block, inset, block);
        yield return (inset, img.Height - inset - block, block);
        yield return (img.Width - inset - block, img.Height - inset - block, block);
    }
}
