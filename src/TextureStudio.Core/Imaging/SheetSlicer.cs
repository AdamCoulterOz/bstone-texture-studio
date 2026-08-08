using TextureStudio.Core.Model;

namespace TextureStudio.Core.Imaging;

public sealed record SliceResult(string TileKey, RgbaImage Image, bool UsedFallback, double WrapError);

public static class SheetSlicer
{
    /// <summary>Slice an upscaled/redrawn sheet back into per-tile images using the manifest.
    /// Cell rectangles are mapped proportionally, then refined against the detected content
    /// bounding box so gutter pixels never leak into tile edges. Falls back to the proportional
    /// rectangle when detection disagrees with expected geometry by more than 8%.</summary>
    public static List<SliceResult> Slice(RgbaImage sheet, SheetManifest manifest, int targetPx)
    {
        var bg = EstimateBackground(sheet);
        return manifest.Cells.Select(cell => SliceCell(sheet, bg, manifest, cell, targetPx)).ToList();
    }

    /// <summary>Slice a single cell — exposed so callers on a single-threaded UI (WASM) can
    /// chunk the work with yields between cells instead of freezing for the whole sheet.</summary>
    public static SliceResult SliceCell(
        RgbaImage sheet, double bg, SheetManifest manifest, SheetCell cell, int targetPx)
    {
        var sx = (double)sheet.Width / manifest.CanvasWidth;
        var sy = (double)sheet.Height / manifest.CanvasHeight;
        // Old manifests flagged seamless per sheet; run-aware ones flag the member cells.
        var legacySeamless = manifest.Seamless && manifest.Cells.All(c => !c.Seamless);
        var cellSeamless = cell.Seamless || legacySeamless;
        {
            var cx0 = cell.X * sx;
            var cy0 = cell.Y * sy;
            var cw = cell.W * sx;
            var ch = cell.H * sy;
            var padX = Math.Max(2, (cellSeamless ? cell.W * 0.02 : manifest.GutterPx / 2.0) * sx);
            var padY = Math.Max(2, manifest.GutterPx / 2.0 * sy);
            var (bx0, by0, bx1, by1, found) = DetectContentBox(
                sheet, bg,
                (int)(cx0 - padX), (int)(cy0 - padY),
                (int)(cx0 + cw + padX), (int)(cy0 + ch + padY));
            var okW = found && Math.Abs(bx1 - bx0 - cw) < cw * 0.08;
            var okH = found && Math.Abs(by1 - by0 - ch) < ch * 0.08;
            var usedFallback = !(okW && okH);
            if (usedFallback)
            {
                bx0 = (int)Math.Round(cx0);
                by0 = (int)Math.Round(cy0);
                bx1 = (int)Math.Round(cx0 + cw);
                by1 = (int)Math.Round(cy0 + ch);
            }
            // Seamless runs: trust detection vertically but slice horizontally by exact
            // proportion inside the run, because butted neighbors have no gutter to detect.
            if (cellSeamless && !usedFallback)
            {
                bx0 = (int)Math.Round(cx0);
                bx1 = (int)Math.Round(cx0 + cw);
            }
            // Trim the antialiased blend between the cell and the sheet's boundary off
            // detected sprite boxes — those mixed pixels survive keying as a dark
            // hairline on the tile edges. Sprite art is inset, so nothing real is lost.
            if (!usedFallback && cell.Kind == TileKind.Cutout)
            {
                var erode = Math.Clamp((int)Math.Round((bx1 - bx0) * 0.004), 1, 6);
                bx0 += erode;
                bx1 -= erode;
                by0 += erode;
                by1 -= erode;
            }
            var crop = sheet.Crop(Math.Max(0, bx0), Math.Max(0, by0),
                Math.Min(sheet.Width, bx1) - Math.Max(0, bx0),
                Math.Min(sheet.Height, by1) - Math.Max(0, by0));
            var tile = ToSquareTile(crop, cell, targetPx);
            return new SliceResult(cell.TileKey, tile, usedFallback, WrapError(tile));
        }
    }

    /// <summary>Bring a sliced crop to the square target size. Walls remap exactly (they
    /// must fill the square). Sprite crops from non-square model output are fitted
    /// aspect-true and centered on a magenta matte canvas instead — squishing character
    /// art to square distorts it, and the matte padding keys away downstream.</summary>
    private static RgbaImage ToSquareTile(RgbaImage crop, SheetCell cell, int targetPx)
    {
        var isCutout = cell.Kind == TileKind.Cutout;
        var aspectSkew = Math.Abs(crop.Width - crop.Height) / (double)Math.Max(crop.Width, crop.Height);
        if (!isCutout || aspectSkew < 0.02)
        {
            return crop.Resample(targetPx, targetPx);
        }
        var scale = (double)targetPx / Math.Max(crop.Width, crop.Height);
        var w = Math.Max(1, (int)Math.Round(crop.Width * scale));
        var h = Math.Max(1, (int)Math.Round(crop.Height * scale));
        var scaled = crop.Resample(w, h);
        var canvas = new RgbaImage(targetPx, targetPx);
        // Match the crop's background: pre-keyed (transparent-corner) sheets pad
        // transparent; opaque sheets pad magenta so the keyer strips it as matte.
        if (crop.Pixels[3] != 0 || crop.Pixels[(crop.Width - 1) * 4 + 3] != 0)
        {
            canvas.Fill(255, 0, 255);
        }
        canvas.Paste(scaled, (targetPx - w) / 2, (targetPx - h) / 2);
        return canvas;
    }

    /// <summary>Mean abs RGB difference between the left and right edge columns — a proxy for
    /// how visibly a repeating wall texture seams against itself in-game.</summary>
    public static double WrapError(RgbaImage img)
    {
        double sum = 0;
        for (var y = 0; y < img.Height; y++)
        {
            var l = img.Offset(0, y);
            var r = img.Offset(img.Width - 1, y);
            for (var c = 0; c < 3; c++)
            {
                sum += Math.Abs(img.Pixels[l + c] - img.Pixels[r + c]);
            }
        }
        return sum / (img.Height * 3);
    }

    public static double EstimateBackground(RgbaImage img)
    {
        var samples = new List<double>();
        var n = Math.Min(8, Math.Min(img.Width, img.Height));
        foreach (var (cx, cy) in new[] { (0, 0), (img.Width - n, 0), (0, img.Height - n), (img.Width - n, img.Height - n) })
        {
            for (var y = cy; y < cy + n; y++)
            {
                for (var x = cx; x < cx + n; x++)
                {
                    var o = img.Offset(x, y);
                    samples.Add((img.Pixels[o] + img.Pixels[o + 1] + img.Pixels[o + 2]) / 3.0);
                }
            }
        }
        samples.Sort();
        return samples[samples.Count / 2];
    }

    private static (int X0, int Y0, int X1, int Y1, bool Found) DetectContentBox(
        RgbaImage img, double bg, int wx0, int wy0, int wx1, int wy1)
    {
        wx0 = Math.Max(0, wx0);
        wy0 = Math.Max(0, wy0);
        wx1 = Math.Min(img.Width, wx1);
        wy1 = Math.Min(img.Height, wy1);
        var w = wx1 - wx0;
        var h = wy1 - wy0;
        if (w <= 0 || h <= 0)
        {
            return (0, 0, 0, 0, false);
        }
        var rowFrac = new double[h];
        var colFrac = new double[w];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var o = img.Offset(wx0 + x, wy0 + y);
                var lum = (img.Pixels[o] + img.Pixels[o + 1] + img.Pixels[o + 2]) / 3.0;
                if (Math.Abs(lum - bg) > 18)
                {
                    rowFrac[y]++;
                    colFrac[x]++;
                }
            }
        }
        int y0 = -1, y1 = -1, x0 = -1, x1 = -1;
        for (var y = 0; y < h; y++)
        {
            if (rowFrac[y] / w > 0.05)
            {
                if (y0 < 0)
                {
                    y0 = y;
                }
                y1 = y + 1;
            }
        }
        for (var x = 0; x < w; x++)
        {
            if (colFrac[x] / h > 0.05)
            {
                if (x0 < 0)
                {
                    x0 = x;
                }
                x1 = x + 1;
            }
        }
        if (y0 < 0 || x0 < 0)
        {
            return (0, 0, 0, 0, false);
        }
        return (wx0 + x0, wy0 + y0, wx0 + x1, wy0 + y1, true);
    }
}
