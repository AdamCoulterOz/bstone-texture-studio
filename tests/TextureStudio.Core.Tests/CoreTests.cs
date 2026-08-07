using TextureStudio.Core;
using TextureStudio.Core.Formats;
using TextureStudio.Core.Imaging;
using TextureStudio.Core.Model;

namespace TextureStudio.Core.Tests;

public class CoreTests
{
    private static readonly string? VswapPath = new[]
    {
        Environment.GetEnvironmentVariable("BSTONE_VSWAP"),
        "/Users/adam/Code/GitHub/AdamCoulterOz/bstone/data/VSWAP.BS6",
    }.FirstOrDefault(File.Exists);

    [Fact]
    public void Palette_HasExpectedShape()
    {
        var palette = VgaPalette.ToRgba();
        Assert.Equal(256, palette.Length);
        Assert.Equal(0xFF000000u, palette[0]); // black, opaque
    }

    [Fact]
    public void Vswap_ParsesRealAogData()
    {
        if (VswapPath is null)
        {
            return; // No game data on this machine; format coverage comes from the real-data run.
        }
        var vswap = new VswapFile(File.ReadAllBytes(VswapPath));
        Assert.Equal(202, vswap.WallCount);
        Assert.True(vswap.SpriteCount > 300, $"sprite count {vswap.SpriteCount}");
        var palette = VgaPalette.ToRgba();
        var wall = TileDecoders.DecodeWall(vswap.GetWallData(4), palette);
        Assert.Equal(64, wall.Width);
        // Wall 4 is the orange brick texture — assert it is not a flat color.
        Assert.True(Enumerable.Range(0, wall.Pixels.Length / 4)
            .Select(i => wall.Pixels[i * 4])
            .Distinct().Count() > 8);
        // Decode every sprite; malformed posts would throw.
        for (var i = 1; i < vswap.SpriteCount; i++)
        {
            if (!vswap.IsEmptyChunk(vswap.SpriteStart + i))
            {
                var sprite = TileDecoders.DecodeSprite(vswap.GetSpriteData(i), palette);
                Assert.Equal(64, sprite.Width);
            }
        }
    }

    [Fact]
    public void ComposeAndSlice_RoundTripsTiles()
    {
        var tiles = new List<(string, RgbaImage)>();
        for (var t = 0; t < 5; t++)
        {
            var img = new RgbaImage(64, 64);
            img.Fill((byte)(60 + t * 40), (byte)(200 - t * 30), 120);
            tiles.Add(($"wall:{t * 2}", img));
        }
        var (sheet, manifest) = SheetComposer.Compose(
            tiles, seamlessColumns: 3, tilePx: 128, gutterPx: 8, seamless: false);
        Assert.Equal(5, manifest.Cells.Count);
        // 5 tiles auto-lay-out on the smallest square grid: 3x3, square canvas.
        Assert.Equal(3, manifest.Columns);
        Assert.Equal(manifest.CanvasWidth, manifest.CanvasHeight);

        // Simulate an upscaled result at 1.5x and slice back.
        var upscaled = sheet.Resample((int)(sheet.Width * 1.5), (int)(sheet.Height * 1.5));
        var slices = SheetSlicer.Slice(upscaled, manifest, targetPx: 96);
        Assert.Equal(5, slices.Count);
        foreach (var (slice, index) in slices.Select((s, i) => (s, i)))
        {
            Assert.Equal(96, slice.Image.Width);
            // Center pixel keeps the tile's fill color (within resampling tolerance).
            var center = slice.Image.Offset(48, 48);
            Assert.InRange(slice.Image.Pixels[center], 60 + index * 40 - 12, 60 + index * 40 + 12);
        }
    }

    [Fact]
    public void Slicer_SurvivesMarginDrift()
    {
        var tile = new RgbaImage(64, 64);
        tile.Fill(200, 40, 40);
        var (sheet, manifest) = SheetComposer.Compose(
            [("wall:0", tile)], 1, 128, 16, seamless: false);
        // Model-real drift: uniformly upscaled result whose content shifted a few pixels —
        // proportional mapping alone would clip an edge; bbox detection must recover it.
        var scaled = sheet.Resample(sheet.Width * 2, sheet.Height * 2);
        var padded = new RgbaImage(scaled.Width, scaled.Height);
        padded.Fill(32, 32, 32);
        padded.Paste(scaled.Crop(0, 0, scaled.Width - 9, scaled.Height - 5), 9, 5);
        var slices = SheetSlicer.Slice(padded, manifest, 64);
        Assert.False(slices[0].UsedFallback);
        Assert.Equal(200, slices[0].Image.Pixels[slices[0].Image.Offset(32, 32)]);
    }

    [Fact]
    public void DarkGenerator_DarkensWithoutHueShift()
    {
        var img = new RgbaImage(2, 1);
        img.Pixels[0] = 200; img.Pixels[1] = 100; img.Pixels[2] = 50; img.Pixels[3] = 255;
        img.Pixels[4] = 10; img.Pixels[5] = 10; img.Pixels[6] = 10; img.Pixels[7] = 128;
        var dark = DarkGenerator.Apply(img, new DarkParams(Multiply: 0.6));
        Assert.True(dark.Pixels[0] < 200 && dark.Pixels[1] < 100 && dark.Pixels[2] < 50);
        // Channel ordering (hue direction) preserved.
        Assert.True(dark.Pixels[0] > dark.Pixels[1] && dark.Pixels[1] > dark.Pixels[2]);
        // Alpha untouched.
        Assert.Equal(128, dark.Pixels[7]);
    }

    [Fact]
    public void Compose_SeamlessPadsToSquareKeepingRowLayout()
    {
        var tiles = Enumerable.Range(0, 6).Select(i =>
        {
            var img = new RgbaImage(64, 64);
            img.Fill((byte)(40 + i * 30), 90, 90);
            return ($"wall:{i}", img);
        }).ToList();
        // 6 murals laid out 3-wide by the user -> 2 rows; padded to a 3x3 square grid.
        var (sheet, manifest) = SheetComposer.Compose(tiles, 3, 128, 16, seamless: true);
        Assert.Equal(3, manifest.Columns);
        Assert.Equal(3, manifest.Rows);
        Assert.Equal(sheet.Width, sheet.Height);
        // Row cells butt horizontally: second cell starts exactly one tile after the first.
        Assert.Equal(manifest.Cells[0].X + 128, manifest.Cells[1].X);
        Assert.Equal(6, manifest.Cells.Count);
    }

    [Fact]
    public void AlphaKeyer_RemovesBorderBackgroundButKeepsInteriorWhites()
    {
        // White background, red square in the middle with one white pixel inside it.
        var img = new RgbaImage(32, 32);
        img.Fill(255, 255, 255);
        for (var y = 8; y < 26; y++)
        {
            for (var x = 8; x < 24; x++)
            {
                var o = img.Offset(x, y);
                img.Pixels[o] = 200; img.Pixels[o + 1] = 30; img.Pixels[o + 2] = 30;
            }
        }
        var interior = img.Offset(16, 16);
        img.Pixels[interior] = 255; img.Pixels[interior + 1] = 255; img.Pixels[interior + 2] = 255;

        AlphaKeyer.KeyOutBorderConnectedBackground(img);

        Assert.Equal(0, img.Pixels[img.Offset(2, 2) + 3]);        // border background cleared
        Assert.Equal(255, img.Pixels[img.Offset(12, 12) + 3]);    // art opaque
        Assert.Equal(255, img.Pixels[interior + 3]);              // interior white preserved
        // Baseline: lowest opaque row is y=25 → (25+1)/32.
        Assert.Equal((25 + 1) / 32.0, AlphaKeyer.BaselineFraction(img)!.Value, 3);
    }

    [Fact]
    public void AlphaKeyer_SurvivesGutterSliverOnCropEdge()
    {
        // White cell with a red square, but the crop caught a 2px dark gutter sliver on the
        // left edge — the failure that left backgrounds opaque on real imports.
        var img = new RgbaImage(64, 64);
        img.Fill(253, 253, 253);
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 2; x++)
            {
                var o = img.Offset(x, y);
                img.Pixels[o] = 32; img.Pixels[o + 1] = 32; img.Pixels[o + 2] = 32;
            }
        }
        for (var y = 20; y < 50; y++)
        {
            for (var x = 20; x < 44; x++)
            {
                var o = img.Offset(x, y);
                img.Pixels[o] = 200; img.Pixels[o + 1] = 30; img.Pixels[o + 2] = 30;
            }
        }
        AlphaKeyer.KeyOutBorderConnectedBackground(img);
        Assert.Equal(0, img.Pixels[img.Offset(10, 10) + 3]);   // white background cleared
        Assert.Equal(0, img.Pixels[img.Offset(0, 30) + 3]);    // gutter sliver cleared
        Assert.Equal(255, img.Pixels[img.Offset(30, 30) + 3]); // art intact
    }

    [Fact]
    public void AlphaKeyer_MatteKeyClearsMagentaOnly()
    {
        var img = new RgbaImage(32, 32);
        img.Fill(AlphaKeyer.MatteR, AlphaKeyer.MatteG, AlphaKeyer.MatteB);
        // A red-tie-colored block and a white block must both survive a matte key.
        for (var x = 4; x < 12; x++)
        {
            var o = img.Offset(x, 16);
            img.Pixels[o] = 200; img.Pixels[o + 1] = 30; img.Pixels[o + 2] = 30;
            var o2 = img.Offset(x, 20);
            img.Pixels[o2] = 255; img.Pixels[o2 + 1] = 255; img.Pixels[o2 + 2] = 255;
        }
        Assert.Equal("matte", AlphaKeyer.KeyAuto(img));
        Assert.Equal(0, img.Pixels[img.Offset(2, 2) + 3]);
        Assert.Equal(255, img.Pixels[img.Offset(6, 16) + 3]);
        Assert.Equal(255, img.Pixels[img.Offset(6, 20) + 3]);
    }

    [Fact]
    public void AlphaKeyer_UnmixesGlowSpillButKeepsPurpleArt()
    {
        var img = new RgbaImage(16, 16);
        img.Fill(AlphaKeyer.MatteR, AlphaKeyer.MatteG, AlphaKeyer.MatteB);
        // A 50/50 blend of white glow and matte: (255,128,255).
        var glow = img.Offset(4, 4);
        img.Pixels[glow] = 255; img.Pixels[glow + 1] = 128; img.Pixels[glow + 2] = 255;
        // Genuine purple art (R and B strongly unbalanced).
        var purple = img.Offset(8, 8);
        img.Pixels[purple] = 128; img.Pixels[purple + 1] = 0; img.Pixels[purple + 2] = 255;

        AlphaKeyer.KeyOutMatte(img);

        // Glow pixel: recovered to near-white at roughly half alpha.
        Assert.InRange(img.Pixels[glow], 240, 255);
        Assert.InRange(img.Pixels[glow + 1], 240, 255);
        Assert.InRange(img.Pixels[glow + 3], 110, 145);
        // Purple art untouched and fully opaque.
        Assert.Equal(128, img.Pixels[purple]);
        Assert.Equal(255, img.Pixels[purple + 3]);
    }

    [Fact]
    public void AlphaKeyer_TrustsCellsThatArriveAlreadyTransparent()
    {
        // The model sometimes keys the matte itself and returns true alpha. Transparent
        // pixels decode as black, so the flood path would eat dark cel outlines — KeyAuto
        // must leave pre-keyed cells alone (margin/dust cleanup aside).
        var img = new RgbaImage(64, 64); // all (0,0,0,0)
        for (var y = 20; y < 40; y++)
        {
            for (var x = 20; x < 40; x++)
            {
                var o = img.Offset(x, y);
                var outline = x < 24 || x >= 36 || y < 24 || y >= 36;
                img.Pixels[o] = outline ? (byte)30 : (byte)255;
                img.Pixels[o + 1] = outline ? (byte)30 : (byte)255;
                img.Pixels[o + 2] = outline ? (byte)30 : (byte)255;
                img.Pixels[o + 3] = 255;
            }
        }
        Assert.Equal("alpha", AlphaKeyer.KeyAuto(img));
        Assert.Equal(255, img.Pixels[img.Offset(21, 30) + 3]); // dark outline survives
        Assert.Equal(255, img.Pixels[img.Offset(30, 30) + 3]); // interior survives
        Assert.Equal(0, img.Pixels[img.Offset(10, 10) + 3]);   // background stays clear
    }

    [Fact]
    public void AlphaKeyer_UnmixesWarmGlowHaloNearBackground()
    {
        var img = new RgbaImage(64, 64);
        img.Fill(AlphaKeyer.MatteR, AlphaKeyer.MatteG, AlphaKeyer.MatteB);
        // An amber glowing blob (a dome) in the middle of the matte.
        for (var y = 24; y < 40; y++)
        {
            for (var x = 24; x < 40; x++)
            {
                var o = img.Offset(x, y);
                img.Pixels[o] = 255; img.Pixels[o + 1] = 170; img.Pixels[o + 2] = 60;
            }
        }
        // The halo case: a warm glow+matte blend that dodges the balance gate
        // (|R−B| = 88) and sits just under the opacity threshold (d = 109) — the old
        // keyer left this as a near-opaque pink ring.
        var halo = img.Offset(22, 30);
        img.Pixels[halo] = 251; img.Pixels[halo + 1] = 109; img.Pixels[halo + 2] = 163;

        AlphaKeyer.KeyOutMatte(img);

        Assert.InRange(img.Pixels[halo + 3], 60, 160); // translucent, not opaque
        Assert.True(img.Pixels[halo + 2] < 100);       // magenta's blue purged
        Assert.True(img.Pixels[halo + 1] > 150);       // warm glow color recovered
        Assert.Equal(255, img.Pixels[img.Offset(30, 30) + 3]); // blob interior untouched
    }

    [Fact]
    public void SpriteFootprint_RotationBakeMatchesCssClockwise()
    {
        // Marker block at the left-middle; rotate(90deg) CSS-clockwise about the placed
        // rect's center must move it to the top-middle.
        var img = new RgbaImage(64, 64);
        for (var y = 30; y <= 34; y++)
        {
            for (var x = 8; x <= 12; x++)
            {
                var o = img.Offset(x, y);
                img.Pixels[o] = 200; img.Pixels[o + 3] = 255;
            }
        }
        var rotated = SpriteFootprint.ApplyPlacement(img, new SpritePlacement(1, 0, 0, 90));
        var bounds = SpriteFootprint.OpaqueBounds(rotated, alphaThreshold: 128)!.Value;
        var centerX = bounds.X + bounds.W / 2.0;
        var centerY = bounds.Y + bounds.H / 2.0;
        Assert.InRange(centerX, 30, 34); // now horizontally centered
        Assert.InRange(centerY, 8, 12);  // at the top — clockwise
        Assert.Equal(0, rotated.Pixels[rotated.Offset(10, 32) + 3]); // old spot cleared
    }

    [Fact]
    public void SpriteFootprint_PlacementTransformMatchesNormalize()
    {
        // Small original blob near the tile floor.
        var original = new RgbaImage(64, 64);
        for (var y = 40; y <= 50; y++)
        {
            for (var x = 20; x <= 30; x++)
            {
                original.Pixels[original.Offset(x, y) + 3] = 255;
            }
        }
        // Redraw enlarged to fill most of its cell.
        var redraw = new RgbaImage(512, 512);
        for (var y = 50; y <= 450; y++)
        {
            for (var x = 50; x <= 450; x++)
            {
                var o = redraw.Offset(x, y);
                redraw.Pixels[o] = 200; redraw.Pixels[o + 3] = 255;
            }
        }
        var baked = SpriteFootprint.Normalize(redraw, original);
        var placement = SpriteFootprint.ComputePlacement(redraw, original);
        var transformed = SpriteFootprint.ApplyPlacement(redraw, placement);

        Assert.False(placement.IsIdentity);
        var b1 = SpriteFootprint.OpaqueBounds(baked)!.Value;
        var b2 = SpriteFootprint.OpaqueBounds(transformed)!.Value;
        Assert.InRange(b2.X, b1.X - 2, b1.X + 2);
        Assert.InRange(b2.Y, b1.Y - 2, b1.Y + 2);
        Assert.InRange(b2.W, b1.W - 3, b1.W + 3);
        Assert.InRange(b2.H, b1.H - 3, b1.H + 3);
    }

    [Fact]
    public void ItemMigration_GroupsByNameThenFamilyThenSingleton()
    {
        var keys = new[] { "sprite:1", "sprite:2", "sprite:3", "sprite:4", "sprite:5" };
        var meta = new Dictionary<string, TileMeta>
        {
            ["sprite:1"] = new() { Name = "Guard", Category = "Enemies" },
            ["sprite:2"] = new() { Name = "Guard", Category = "Enemies" },
            // 3 and 4 unnamed but same engine family; 5 unnamed standalone
        };
        var items = ItemMigration.Build(keys, meta,
            k => k is "sprite:3" or "sprite:4" ? "OOZE" : null);

        Assert.Equal(3, items.Count);
        var guard = items.Single(i => i.Name == "Guard");
        Assert.Equal(new[] { "sprite:1", "sprite:2" }, guard.TileKeys);
        Assert.True(guard.IsAnimation);
        var ooze = items.Single(i => i.TileKeys.Contains("sprite:3"));
        Assert.Equal(2, ooze.TileKeys.Count);
        Assert.Equal("", ooze.Name);
        var single = items.Single(i => i.TileKeys.Contains("sprite:5"));
        Assert.False(single.IsAnimation);
    }

    [Fact]
    public void SpriteFootprint_SignificantBoundsIgnoresSpecks()
    {
        var img = new RgbaImage(128, 128);
        // Real art: solid 40x30 block.
        for (var y = 60; y < 90; y++)
        {
            for (var x = 44; x < 84; x++)
            {
                img.Pixels[img.Offset(x, y) + 3] = 255;
            }
        }
        // A stray keying speck near a corner must not inflate the box.
        img.Pixels[img.Offset(2, 2) + 3] = 255;
        var box = SpriteFootprint.SignificantBounds(img)!.Value;
        Assert.Equal((44, 60, 40, 30), box);
    }

    [Fact]
    public void SpriteFootprint_RefitsEnlargedArtIntoOriginalBox()
    {
        // Original: a small 12x10 object sitting on the floor of a 64-tile, centered-ish.
        var original = new RgbaImage(64, 64);
        for (var y = 50; y < 60; y++)
        {
            for (var x = 26; x < 38; x++)
            {
                var o = original.Offset(x, y);
                original.Pixels[o] = 200; original.Pixels[o + 3] = 255;
            }
        }
        // Redraw at 256px: the model filled almost the whole cell.
        var redraw = new RgbaImage(256, 256);
        for (var y = 8; y < 248; y++)
        {
            for (var x = 8; x < 248; x++)
            {
                var o = redraw.Offset(x, y);
                redraw.Pixels[o + 1] = 180; redraw.Pixels[o + 3] = 255;
            }
        }
        var normalized = SpriteFootprint.Normalize(redraw, original);
        var box = SpriteFootprint.OpaqueBounds(normalized)!.Value;
        // Art shrank to the original's fractional footprint (12/64 → 48px at 256).
        Assert.InRange(box.W, 40, 52);
        Assert.InRange(box.H, 34, 44);
        // Bottom edge matches the original floor line (60/64 → 240 at 256).
        Assert.InRange(box.Y + box.H, 234, 244);

        // Near-full-tile art passes through untouched.
        var actor = new RgbaImage(64, 64);
        for (var i = 3; i < actor.Pixels.Length; i += 4)
        {
            actor.Pixels[i] = 255;
        }
        Assert.Same(redraw, SpriteFootprint.Normalize(redraw, actor));
    }

    [Fact]
    public void TileRef_RoundTripsKeys()
    {
        Assert.Equal("wall:12", new TileRef(TileKind.Wall, 12).Key);
        Assert.Equal(new TileRef(TileKind.Sprite, 7), TileRef.Parse("sprite:7"));
    }

    [Fact]
    public void PlanLayoutRuns_ButtsRunsWithoutWrappingAndGhostFillsTheRest()
    {
        var keys = new[] { "a", "b", "c", "d", "e" };
        var runs = new List<List<string>> { new() { "b", "c", "d" } };
        var planned = SheetComposer.PlanLayoutRuns(keys, 256, 16, runs);
        var m = planned.Manifest;

        Assert.Equal(3, planned.Side);
        Assert.Equal(16 + 3 * (256 + 16), m.CanvasWidth);
        Assert.Equal(5, m.Cells.Count);

        // 'a' sits alone in row 0; the 3-run can't follow it there, so the row's
        // remaining slots become ghosts and the run starts fresh on row 1.
        var a = m.Cells.Single(c => c.TileKey == "a");
        Assert.Equal((16, 16, false), (a.X, a.Y, a.Seamless));
        var b = m.Cells.Single(c => c.TileKey == "b");
        var c3 = m.Cells.Single(c => c.TileKey == "c");
        var d = m.Cells.Single(c => c.TileKey == "d");
        Assert.All(new[] { b, c3, d }, cell => Assert.True(cell.Seamless));
        Assert.Equal(b.Y, c3.Y);
        Assert.Equal(b.X + b.W, c3.X); // butted — no gutter between run members
        Assert.Equal(c3.X + c3.W, d.X);
        var e = m.Cells.Single(c => c.TileKey == "e");
        Assert.True(e.Y > b.Y);
        Assert.False(e.Seamless);

        // 2 ghosts after 'a' + 2 after 'e' complete the 3×3 grid.
        Assert.Equal(4, planned.Ghosts.Count);
    }

    [Fact]
    public void PlanLayoutRuns_GridGrowsToFitTheWidestRun()
    {
        var keys = new[] { "a", "b", "c", "d" };
        var runs = new List<List<string>> { new() { "a", "b", "c", "d" } };
        var planned = SheetComposer.PlanLayoutRuns(keys, 256, 16, runs);
        Assert.Equal(4, planned.Side); // ceil-sqrt would say 2; the run forces 4
        var xs = planned.Manifest.Cells.Select(c => c.X).ToList();
        Assert.Equal(new[] { 16, 272, 528, 784 }, xs);
        Assert.All(planned.Manifest.Cells, c => Assert.Equal(16, c.Y));
    }
}
