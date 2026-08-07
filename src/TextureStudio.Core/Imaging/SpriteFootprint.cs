namespace TextureStudio.Core.Imaging;

/// <summary>Uniform transform for a keyed cell image: scale about the top-left, translate
/// by cell-fraction offsets, then rotate about the placed rect's center (CSS rotate
/// semantics, positive = clockwise degrees). Identity = (1, 0, 0, 0).</summary>
public readonly record struct SpritePlacement(
    double Scale, double OffsetX, double OffsetY, double Rotation = 0)
{
    public static readonly SpritePlacement Identity = new(1, 0, 0);

    public bool IsIdentity =>
        Math.Abs(Scale - 1) < 1e-9 && Math.Abs(OffsetX) < 1e-9 && Math.Abs(OffsetY) < 1e-9 &&
        Math.Abs(Rotation) < 1e-9;
}

/// <summary>Restores a redrawn sprite's in-world footprint: models love enlarging small
/// pickups to fill their cell, but billboard scale is fixed by the engine — so the keyed
/// redraw is re-fitted into the ORIGINAL art's fractional bounding box (aspect preserved,
/// anchored bottom-center for floor contact).</summary>
public static class SpriteFootprint
{
    /// <summary>Bounding box of pixels with alpha above the threshold; null when empty.</summary>
    public static (int X, int Y, int W, int H)? OpaqueBounds(RgbaImage img, byte alphaThreshold = 32)
    {
        int minX = img.Width, minY = img.Height, maxX = -1, maxY = -1;
        for (var y = 0; y < img.Height; y++)
        {
            for (var x = 0; x < img.Width; x++)
            {
                if (img.Pixels[img.Offset(x, y) + 3] > alphaThreshold)
                {
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }
        }
        return maxX < 0 ? null : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>Bounds computed from rows/columns holding at least <paramref name="minCount"/>
    /// solidly-opaque pixels — immune to stray keying specks that would otherwise inflate the
    /// measured box to the whole tile.</summary>
    public static (int X, int Y, int W, int H)? SignificantBounds(
        RgbaImage img, byte alphaThreshold = 128, int minCount = 2)
    {
        var rowCounts = new int[img.Height];
        var colCounts = new int[img.Width];
        for (var y = 0; y < img.Height; y++)
        {
            for (var x = 0; x < img.Width; x++)
            {
                if (img.Pixels[img.Offset(x, y) + 3] > alphaThreshold)
                {
                    rowCounts[y]++;
                    colCounts[x]++;
                }
            }
        }
        int minX = -1, maxX = -1, minY = -1, maxY = -1;
        for (var y = 0; y < img.Height; y++)
        {
            if (rowCounts[y] >= minCount)
            {
                if (minY < 0)
                {
                    minY = y;
                }
                maxY = y;
            }
        }
        for (var x = 0; x < img.Width; x++)
        {
            if (colCounts[x] >= minCount)
            {
                if (minX < 0)
                {
                    minX = x;
                }
                maxX = x;
            }
        }
        return maxX < 0 || maxY < 0 ? null : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>The same refit expressed as a transform on the whole cell image (uniform
    /// scale about the top-left, then translate by cell-fraction offsets), so a placement
    /// UI can preview and hand-tune it before baking. Identity when Normalize would pass
    /// the image through untouched.</summary>
    public static SpritePlacement ComputePlacement(RgbaImage redraw, RgbaImage original)
    {
        var originalBox = OpaqueBounds(original);
        var redrawBox = SignificantBounds(redraw) ?? OpaqueBounds(redraw);
        if (originalBox is not { } ob || redrawBox is not { } rb)
        {
            return SpritePlacement.Identity;
        }
        var originalFraction = Math.Max(
            ob.W / (double)original.Width, ob.H / (double)original.Height);
        if (originalFraction > 0.85)
        {
            return SpritePlacement.Identity;
        }
        var targetX = ob.X * (double)redraw.Width / original.Width;
        var targetY = ob.Y * (double)redraw.Height / original.Height;
        var targetW = Math.Max(1.0, ob.W * (double)redraw.Width / original.Width);
        var targetH = Math.Max(1.0, ob.H * (double)redraw.Height / original.Height);
        var fit = Math.Min(targetW / rb.W, targetH / rb.H);
        var x = targetX + (targetW - rb.W * fit) / 2;   // centered horizontally in the box
        var y = targetY + (targetH - rb.H * fit);       // resting on the box floor
        return new SpritePlacement(fit,
            (x - rb.X * fit) / redraw.Width,
            (y - rb.Y * fit) / redraw.Height);
    }

    /// <summary>Transform that aspect-fits the image's significant art into an explicit
    /// target box (same pixel space), anchored bottom-center — used to align a new take
    /// with the bounds of an already-approved redraw. Identity when either box is empty.</summary>
    public static SpritePlacement PlacementIntoBox(
        RgbaImage redraw, (int X, int Y, int W, int H) targetBox)
    {
        var redrawBox = SignificantBounds(redraw) ?? OpaqueBounds(redraw);
        if (redrawBox is not { } rb || targetBox.W < 1 || targetBox.H < 1)
        {
            return SpritePlacement.Identity;
        }
        var fit = Math.Min(targetBox.W / (double)rb.W, targetBox.H / (double)rb.H);
        var x = targetBox.X + (targetBox.W - rb.W * fit) / 2;
        var y = targetBox.Y + (targetBox.H - rb.H * fit);
        return new SpritePlacement(fit,
            (x - rb.X * fit) / redraw.Width,
            (y - rb.Y * fit) / redraw.Height);
    }

    /// <summary>Bakes a placement: the image is uniformly resampled and drawn (clipped) onto
    /// a transparent canvas of the original size at the placement's offsets; a non-zero
    /// rotation spins the placed rect about its own center (matching the CSS preview).</summary>
    public static RgbaImage ApplyPlacement(RgbaImage redraw, SpritePlacement placement)
    {
        if (placement.IsIdentity)
        {
            return redraw;
        }
        var w = redraw.Width;
        var h = redraw.Height;
        if (Math.Abs(placement.Rotation) < 1e-9)
        {
            var scaledW = Math.Max(1, (int)Math.Round(w * placement.Scale));
            var scaledH = Math.Max(1, (int)Math.Round(h * placement.Scale));
            var scaled = redraw.Resample(scaledW, scaledH);
            var canvas = new RgbaImage(w, h); // fully transparent
            var dx = (int)Math.Round(placement.OffsetX * w);
            var dy = (int)Math.Round(placement.OffsetY * h);
            for (var sy = 0; sy < scaledH; sy++)
            {
                var ty = dy + sy;
                if (ty < 0 || ty >= h)
                {
                    continue;
                }
                var sx0 = Math.Max(0, -dx);
                var count = Math.Min(scaledW, w - dx) - sx0;
                if (count > 0)
                {
                    Array.Copy(scaled.Pixels, (sy * scaledW + sx0) * 4,
                        canvas.Pixels, (ty * w + dx + sx0) * 4, count * 4);
                }
            }
            return canvas;
        }
        // Rotated path: inverse-map every destination pixel through rotate-about-placed-
        // center then unscale/untranslate, sampling the source bilinearly.
        var result = new RgbaImage(w, h);
        var x0 = placement.OffsetX * w;
        var y0 = placement.OffsetY * h;
        var cx = x0 + w * placement.Scale / 2;
        var cy = y0 + h * placement.Scale / 2;
        var rad = placement.Rotation * Math.PI / 180.0;
        var cos = Math.Cos(-rad);
        var sin = Math.Sin(-rad);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var relX = x + 0.5 - cx;
                var relY = y + 0.5 - cy;
                var ux = cx + relX * cos - relY * sin;
                var uy = cy + relX * sin + relY * cos;
                var srcX = (ux - x0) / placement.Scale - 0.5;
                var srcY = (uy - y0) / placement.Scale - 0.5;
                if (srcX < -0.5 || srcY < -0.5 || srcX > w - 0.5 || srcY > h - 0.5)
                {
                    continue;
                }
                var ix = (int)Math.Floor(srcX);
                var iy = (int)Math.Floor(srcY);
                var fx = srcX - ix;
                var fy = srcY - iy;
                var d = result.Offset(x, y);
                for (var c = 0; c < 4; c++)
                {
                    double Sample(int sx, int sy) =>
                        sx < 0 || sy < 0 || sx >= w || sy >= h
                            ? 0
                            : redraw.Pixels[redraw.Offset(sx, sy) + c];
                    var top = Sample(ix, iy) * (1 - fx) + Sample(ix + 1, iy) * fx;
                    var bottom = Sample(ix, iy + 1) * (1 - fx) + Sample(ix + 1, iy + 1) * fx;
                    result.Pixels[d + c] = (byte)Math.Clamp(top * (1 - fy) + bottom * fy, 0, 255);
                }
            }
        }
        return result;
    }

    /// <summary>When the original art occupies well under the full tile (small objects), the
    /// redraw's opaque art is cropped, aspect-fit into the original's fractional box, and
    /// anchored bottom-center within it. Large art (actors) passes through untouched.</summary>
    public static RgbaImage Normalize(RgbaImage redraw, RgbaImage original)
    {
        var originalBox = OpaqueBounds(original);
        var redrawBox = SignificantBounds(redraw) ?? OpaqueBounds(redraw);
        if (originalBox is not { } ob || redrawBox is not { } rb)
        {
            return redraw;
        }
        var originalFraction = Math.Max(
            ob.W / (double)original.Width, ob.H / (double)original.Height);
        if (originalFraction > 0.85)
        {
            return redraw; // near-full-tile art: leave the model's framing alone
        }
        // Original box mapped into redraw pixel space.
        var targetX = (int)Math.Round(ob.X * (double)redraw.Width / original.Width);
        var targetY = (int)Math.Round(ob.Y * (double)redraw.Height / original.Height);
        var targetW = Math.Max(1, (int)Math.Round(ob.W * (double)redraw.Width / original.Width));
        var targetH = Math.Max(1, (int)Math.Round(ob.H * (double)redraw.Height / original.Height));

        var crop = redraw.Crop(rb.X, rb.Y, rb.W, rb.H);
        var fit = Math.Min(targetW / (double)rb.W, targetH / (double)rb.H);
        var newW = Math.Max(1, (int)Math.Round(rb.W * fit));
        var newH = Math.Max(1, (int)Math.Round(rb.H * fit));
        var scaled = crop.Resample(newW, newH);

        var canvas = new RgbaImage(redraw.Width, redraw.Height); // fully transparent
        var x = targetX + (targetW - newW) / 2;      // centered horizontally in the box
        var y = targetY + (targetH - newH);          // resting on the box floor
        canvas.Paste(scaled, Math.Max(0, x), Math.Max(0, y));
        return canvas;
    }
}
