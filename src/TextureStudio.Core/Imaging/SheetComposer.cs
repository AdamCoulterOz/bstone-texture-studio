using TextureStudio.Core.Model;

namespace TextureStudio.Core.Imaging;

public enum ReservedCorner { None, BottomLeft, BottomRight, TopLeft, TopRight }

public sealed record SheetCell(int CellIndex, string TileKey, int X, int Y, int W, int H, bool Seamless = false);

/// <summary>A planned sheet plus the empty grid slots left over — the app renders the
/// platter directly from this so the preview always matches the composed sheet.</summary>
public sealed record PlannedLayout(SheetManifest Manifest, IReadOnlyList<(int X, int Y)> Ghosts, int Side);

public sealed record SheetManifest(
    int CanvasWidth,
    int CanvasHeight,
    int Columns,
    int Rows,
    int TilePx,
    int GutterPx,
    bool Seamless,
    ReservedCorner Reserved,
    IReadOnlyList<SheetCell> Cells);

public static class SheetComposer
{
    public const byte BgLevel = 32;

    /// <summary>Smallest square cell grid that fits <paramref name="count"/> tiles.</summary>
    public static int SquareSide(int count) => (int)Math.Ceiling(Math.Sqrt(Math.Max(1, count)));

    /// <summary>Compose tiles into an always-square sheet.
    /// Non-seamless: columns are computed (ceil-sqrt) and every cell gets a gutter.
    /// Seamless: the caller's column count fixes how mural rows wrap and rows are butted
    /// edge-to-edge; empty cells pad right/bottom until the cell grid is square.</summary>
    /// <summary>Pure layout math — cell placement without touching pixels. The app composes
    /// the actual bitmap browser-side from this plan; Compose below remains for CLI/tests.</summary>
    public static SheetManifest PlanLayout(
        IReadOnlyList<string> keys,
        int seamlessColumns,
        int tilePx,
        int gutterPx,
        bool seamless)
    {
        if (keys.Count == 0)
        {
            throw new ArgumentException("Need at least one tile.");
        }
        int columns;
        if (seamless)
        {
            var layoutColumns = Math.Max(1, seamlessColumns);
            var layoutRows = (keys.Count + layoutColumns - 1) / layoutColumns;
            columns = Math.Max(layoutColumns, layoutRows); // pad out to square
        }
        else
        {
            columns = SquareSide(keys.Count);
        }
        var side = columns;
        var placeColumns = seamless ? Math.Max(1, seamlessColumns) : columns;

        // Uniform square canvas; seamless rows butt their columns and leave the remaining
        // width as background padding, keeping mural continuity AND a square sheet.
        var size = gutterPx + side * (tilePx + gutterPx);
        var cells = new List<SheetCell>();
        for (var tileIdx = 0; tileIdx < keys.Count; tileIdx++)
        {
            var row = tileIdx / placeColumns;
            var col = tileIdx % placeColumns;
            var x = seamless
                ? gutterPx + col * tilePx
                : gutterPx + col * (tilePx + gutterPx);
            var y = gutterPx + row * (tilePx + gutterPx);
            cells.Add(new SheetCell(row * side + col, keys[tileIdx], x, y, tilePx, tilePx));
        }
        return new SheetManifest(
            size, size, side, side, tilePx, gutterPx, seamless, ReservedCorner.None, cells);
    }

    /// <summary>Run-aware layout: seamless runs (contiguous slices of <paramref name="keys"/>)
    /// are butted edge-to-edge and never wrap across rows; everything else gets normal gutters.
    /// After a run the cursor resumes at the next nominal grid column, so the saved gutters
    /// accumulate as a visible gap at the run's end. The grid stays square and at least as
    /// wide as the longest run.</summary>
    public static PlannedLayout PlanLayoutRuns(
        IReadOnlyList<string> keys,
        int tilePx,
        int gutterPx,
        IReadOnlyList<IReadOnlyList<string>>? seamlessRuns = null)
    {
        var runStart = new Dictionary<string, int>();
        var maxRun = 1;
        foreach (var run in seamlessRuns ?? [])
        {
            if (run.Count < 2)
            {
                continue;
            }
            runStart[run[0]] = run.Count;
            maxRun = Math.Max(maxRun, run.Count);
        }
        var side = Math.Max(SquareSide(Math.Max(1, keys.Count)), maxRun);
        while (true)
        {
            var (cells, ghosts, rowsUsed) = PackRuns(keys, side, tilePx, gutterPx, runStart);
            if (rowsUsed <= side)
            {
                var size = gutterPx + side * (tilePx + gutterPx);
                var manifest = new SheetManifest(size, size, side, side, tilePx, gutterPx,
                    runStart.Count > 0, ReservedCorner.None, cells);
                return new PlannedLayout(manifest, ghosts, side);
            }
            side++;
        }
    }

    private static (List<SheetCell> Cells, List<(int X, int Y)> Ghosts, int RowsUsed) PackRuns(
        IReadOnlyList<string> keys, int side, int tilePx, int gutterPx,
        IReadOnlyDictionary<string, int> runStart)
    {
        int PosX(int col) => gutterPx + col * (tilePx + gutterPx);
        int PosY(int row) => gutterPx + row * (tilePx + gutterPx);
        var cells = new List<SheetCell>();
        var ghosts = new List<(int X, int Y)>();
        int col = 0, row = 0, i = 0;
        while (i < keys.Count)
        {
            var len = runStart.TryGetValue(keys[i], out var l) && i + l <= keys.Count
                ? Math.Min(l, side)
                : 1;
            if (col + len > side)
            {
                for (var c = col; c < side; c++)
                {
                    ghosts.Add((PosX(c), PosY(row)));
                }
                row++;
                col = 0;
            }
            for (var m = 0; m < len; m++)
            {
                cells.Add(new SheetCell(row * side + col + m, keys[i + m],
                    PosX(col) + m * tilePx, PosY(row), tilePx, tilePx, len > 1));
            }
            col += len;
            i += len;
            if (col >= side)
            {
                row++;
                col = 0;
            }
        }
        var rowsUsed = row + (col > 0 ? 1 : 0);
        if (col > 0)
        {
            for (var c = col; c < side; c++)
            {
                ghosts.Add((PosX(c), PosY(row)));
            }
            row++;
        }
        for (; row < side; row++)
        {
            for (var c = 0; c < side; c++)
            {
                ghosts.Add((PosX(c), PosY(row)));
            }
        }
        return (cells, ghosts, rowsUsed);
    }

    public static (RgbaImage Sheet, SheetManifest Manifest) Compose(
        IReadOnlyList<(string Key, RgbaImage Image)> tiles,
        int seamlessColumns,
        int tilePx,
        int gutterPx,
        bool seamless)
    {
        var manifest = PlanLayout(tiles.Select(t => t.Key).ToList(),
            seamlessColumns, tilePx, gutterPx, seamless);
        var sheet = new RgbaImage(manifest.CanvasWidth, manifest.CanvasHeight);
        sheet.Fill(BgLevel, BgLevel, BgLevel);
        foreach (var (cell, (_, image)) in manifest.Cells.Zip(tiles))
        {
            var scaled = tilePx % image.Width == 0
                ? image.ScaleNearest(tilePx / image.Width)
                : image.Resample(tilePx, tilePx);
            sheet.Paste(scaled, cell.X, cell.Y);
        }
        return (sheet, manifest);
    }
}
