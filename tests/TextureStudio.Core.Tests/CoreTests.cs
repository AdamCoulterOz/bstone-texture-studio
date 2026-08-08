using TextureStudio.Core.Games;
using TextureStudio.Core.Games.BlakeStone;
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
        var palette = BlakeStonePalette.ToRgba();
        Assert.Equal(256, palette.Length);
        Assert.Equal(0xFF000000u, palette[0]); // black, opaque
    }

    [Fact]
    public void BlakeStoneArchive_ParsesRealAogData()
    {
        if (VswapPath is null)
        {
            return; // No game data on this machine; format coverage comes from the real-data run.
        }
        var game = new BlakeStoneGame();
        var archive = game.OpenArchive(File.ReadAllBytes(VswapPath), "VSWAP.BS6");
        var walls = archive.Tiles.Where(t => t.Kind == TileKind.Full).ToList();
        var cutouts = archive.Tiles.Where(t => t.Kind == TileKind.Cutout).ToList();
        Assert.Equal(202, walls.Count);
        Assert.True(cutouts.Count > 300, $"cutout count {cutouts.Count}");
        Assert.Equal(BlakeStoneGame.AliensOfGold, game.DetectEdition(archive));
        // Ids are the game's own short form, and walls come first in engine order.
        Assert.Equal("w0", archive.Tiles[0].Id);
        Assert.StartsWith("s", cutouts[0].Id);
        var wall = archive.Decode("w4");
        Assert.Equal(64, wall.Width);
        // Wall 4 is the orange brick texture — assert it is not a flat color.
        Assert.True(Enumerable.Range(0, wall.Pixels.Length / 4)
            .Select(i => wall.Pixels[i * 4])
            .Distinct().Count() > 8);
        // Decode every cutout; malformed posts would throw.
        foreach (var tile in cutouts)
        {
            Assert.Equal(64, archive.Decode(tile.Id).Width);
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
    public void ItemLayerBuilder_GroupsByNameThenFamilyThenSingleton()
    {
        var keys = new[] { "sprite:1", "sprite:2", "sprite:3", "sprite:4", "sprite:5" };
        var meta = new Dictionary<string, TileMeta>
        {
            ["sprite:1"] = new() { Name = "Guard", Category = "Enemies" },
            ["sprite:2"] = new() { Name = "Guard", Category = "Enemies" },
            // 3 and 4 unnamed but same engine family; 5 unnamed standalone
        };
        var items = ItemLayerBuilder.Build(keys, meta,
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
    public void BlakeStone_MapsItsOwnTileIds()
    {
        var game = new BlakeStoneGame();
        // Kind is the only thing the pipelines may read off a tile.
        Assert.Equal(TileKind.Full, game.KindOf("w12"));
        Assert.Equal(TileKind.Cutout, game.KindOf("s107"));
        // Workspace file names keep the pre-plugin spelling so existing folders still match.
        Assert.Equal("wall_00012.png", game.WorkspaceFileName("w12"));
        Assert.Equal("sprite_00107.png", game.WorkspaceFileName("s107"));
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

    // ---- Game plugin layer ----

    [Fact]
    public void NewProject_TargetsTheDefaultGame()
    {
        // Pre-plugin project.json files carry no GameId, so the default is what they get.
        Assert.Equal(BlakeStoneGame.GameId, new Project().GameId);
        Assert.Equal(BlakeStoneGame.GameId, new GameCatalog().Get(null).Id);
    }

    [Fact]
    public void Catalog_FallsBackWhenAGameIsNotInstalled()
    {
        var catalog = new GameCatalog();
        Assert.Equal(BlakeStoneGame.GameId, catalog.Get("wolfenstein-3d").Id);
    }

    [Fact]
    public void BlakeStone_PairsOddWallsToTheirEvenSibling()
    {
        var game = new BlakeStoneGame();
        Assert.Equal("w12", game.DefaultLightSource("w13"));
        Assert.Null(game.DefaultLightSource("w12"));
        Assert.Null(game.DefaultLightSource("s13"));
        Assert.Equal(PairRole.Light, game.AutoPairRole("w12"));
        Assert.Equal(PairRole.DerivedDark, game.AutoPairRole("w13"));
        Assert.Null(game.AutoPairRole("s12"));
    }

    [Fact]
    public void BlakeStone_PacksIntoTheEditionsAssetDirectory()
    {
        var game = new BlakeStoneGame();
        var project = new Project();

        var aog = game.PlanPack(project, BlakeStoneGame.AliensOfGold, ["w12"]);
        Assert.Equal("aog/wall_00000012.png", Assert.Single(aog.Entries).Path);
        var ps = game.PlanPack(project, BlakeStoneGame.PlanetStrike, ["s107"]);
        Assert.Equal("ps/sprite_00000107.png", Assert.Single(ps.Entries).Path);
        // Both Aliens of Gold releases share one mod-dir folder.
        Assert.Equal(BlakeStoneGame.AliensOfGold.AssetDirectory,
            BlakeStoneGame.AliensOfGoldShareware.AssetDirectory);
    }

    [Fact]
    public void PlanPack_SynthesizesDarkVariantsAndReportsWhatItCouldNotMake()
    {
        var game = new BlakeStoneGame();
        var project = new Project
        {
            Meta =
            {
                // A light wall, its derived dark sibling (no art of its own)…
                ["w12"] = new TileMeta { Role = PairRole.Light },
                ["w13"] = new TileMeta { Role = PairRole.DerivedDark },
                // …an alternate dark, which is drawn light and darkens itself…
                ["w20"] = new TileMeta { Role = PairRole.AlternateDark },
                // …and a derived dark whose light source was never redrawn.
                ["w41"] = new TileMeta { Role = PairRole.DerivedDark },
            },
        };

        var plan = game.PlanPack(project, BlakeStoneGame.AliensOfGold,
            ["w12", "w20", "s7"]);

        var byPath = plan.Entries.ToDictionary(e => e.Path);
        Assert.Equal(4, plan.Entries.Count);
        // The light wall and the plain sprite pack as drawn.
        Assert.Equal(("w12", PackTransform.None),
            (byPath["aog/wall_00000012.png"].SourceTileKey, byPath["aog/wall_00000012.png"].Transform));
        Assert.Equal(("s7", PackTransform.None),
            (byPath["aog/sprite_00000007.png"].SourceTileKey, byPath["aog/sprite_00000007.png"].Transform));
        // The derived dark has no art of its own — it is darkened from its light sibling.
        Assert.Equal(("w12", PackTransform.Darken),
            (byPath["aog/wall_00000013.png"].SourceTileKey, byPath["aog/wall_00000013.png"].Transform));
        // The alternate dark darkens itself.
        Assert.Equal(("w20", PackTransform.Darken),
            (byPath["aog/wall_00000020.png"].SourceTileKey, byPath["aog/wall_00000020.png"].Transform));
        Assert.Equal(2, plan.TransformedCount);
        // The one with no usable art is reported rather than silently dropped.
        Assert.Equal("w41", Assert.Single(plan.SkippedTileKeys));
    }

    [Fact]
    public void PlanPack_HasNothingToDoWithoutRedraws()
    {
        var plan = new BlakeStoneGame().PlanPack(new Project(), BlakeStoneGame.AliensOfGold, []);
        Assert.Empty(plan.Entries);
        Assert.Equal(0, plan.TransformedCount);
    }

    [Fact]
    public void ResolveEdition_PrefersThePinnedOneThenDetection()
    {
        var game = new BlakeStoneGame();
        var archive = new FakeArchive("VSWAP.BS6", spriteCount: 900);
        Assert.Equal(BlakeStoneGame.PlanetStrike,
            GameCatalog.ResolveEdition(game, "ps", archive));
        // An edition the game no longer has falls through to detection.
        Assert.Equal(BlakeStoneGame.AliensOfGold,
            GameCatalog.ResolveEdition(game, "aog_beta", archive));
        Assert.Equal(BlakeStoneGame.AliensOfGoldShareware,
            GameCatalog.ResolveEdition(game, "", new FakeArchive("VSWAP.BS1", spriteCount: 400)));
        Assert.Equal(BlakeStoneGame.PlanetStrike,
            GameCatalog.ResolveEdition(game, "", new FakeArchive("VSWAP.VSI", spriteCount: 900)));
    }

    [Fact]
    public void BlakeStoneMetadata_ReadsTheShippedTableShape()
    {
        var metadata = new BlakeStoneMetadata();
        Assert.Null(metadata.Lookup(BlakeStoneGame.AliensOfGold, "s1"));
        metadata.Load(System.Text.Encoding.UTF8.GetBytes(
            """
            {"aog_full":{
              "1":{"c":"SPR_STAT_0","n":"Water Puddle","t":"bo_water_puddle"},
              "2":{"c":"SPR_MUTHUM1_W2_7","u":"Mutant Human"},
              "3":{"c":"SPR_STAT_2","t":"block"}}}
            """));
        Assert.True(metadata.IsLoaded);

        var puddle = metadata.Lookup(BlakeStoneGame.AliensOfGold, "s1");
        Assert.Equal("Water Puddle", puddle!.EngineName);
        Assert.Equal("pickup: water puddle", puddle.TypeLabel);
        // Statics stand alone rather than joining an actor family.
        Assert.Null(metadata.ActorFamily(BlakeStoneGame.AliensOfGold, "s1"));

        var mutant = metadata.Lookup(BlakeStoneGame.AliensOfGold, "s2");
        Assert.Equal("Mutant Human", mutant!.InGameLabel);
        Assert.Equal("walk 2 · rotation 7/8", mutant.FrameLabel);
        Assert.Equal("MUTHUM1",
            metadata.ActorFamily(BlakeStoneGame.AliensOfGold, "s2"));

        Assert.Equal("blocking object",
            metadata.Lookup(BlakeStoneGame.AliensOfGold, "s3")!.TypeLabel);
        // Walls, unknown indices and other editions have no reference data.
        Assert.Null(metadata.Lookup(BlakeStoneGame.AliensOfGold, "w1"));
        Assert.Null(metadata.Lookup(BlakeStoneGame.AliensOfGold, "s99"));
        Assert.Null(metadata.Lookup(BlakeStoneGame.PlanetStrike, "s1"));
    }

    [Fact]
    public void BlakeStoneMetadata_SurvivesAMalformedTable()
    {
        var metadata = new BlakeStoneMetadata();
        metadata.Load(System.Text.Encoding.UTF8.GetBytes("not json"));
        Assert.True(metadata.IsLoaded);
        Assert.Null(metadata.Lookup(BlakeStoneGame.AliensOfGold, "s1"));
    }

    private sealed class FakeArchive(string sourceName, int spriteCount) : IGameArchive
    {
        public string SourceName => sourceName;

        public IReadOnlyList<GameTile> Tiles { get; } =
            [.. Enumerable.Range(0, spriteCount).Select(i => new GameTile($"s{i}", TileKind.Cutout))];

        public RgbaImage Decode(string tileId) => new(64, 64);
    }

    // ---- Locator ----

    [Fact]
    public async Task Locator_FindsGamesBuriedInApplicationBundles()
    {
        // The layout that motivates the search: a storefront bundle wrapping a DOS-emulator
        // bundle, the files eight levels below the folder a user can actually pick.
        var tree = new FakeTree("Applications",
            "Blake Stone - Aliens of Gold.app/Contents/Resources/game/Blake Stone Aliens of " +
            "Gold.app/Contents/Resources/Blake Stone Aliens of Gold.boxer/C Blake Stone Aliens " +
            "of Gold.harddisk/AUDIOHED.BS6",
            "Blake Stone - Aliens of Gold.app/Contents/Resources/game/Blake Stone Aliens of " +
            "Gold.app/Contents/Resources/Blake Stone Aliens of Gold.boxer/C Blake Stone Aliens " +
            "of Gold.harddisk/VSWAP.BS6");

        var result = await new BlakeStoneLocator().FindAsync(tree);

        var source = Assert.Single(result.Sources);
        Assert.Equal(BlakeStoneGame.AliensOfGold, source.Edition);
        Assert.Equal("VSWAP.BS6", source.AssetFileName);
        Assert.EndsWith("harddisk/VSWAP.BS6", source.AssetPath);
        Assert.False(result.Exhausted);
        // The display path stops at the outermost bundle rather than reciting the chain.
        Assert.Equal("Applications/Blake Stone - Aliens of Gold.app", source.DisplayPath);
    }

    [Fact]
    public async Task Locator_TellsEditionsApartByExtensionAndLabelsTheStore()
    {
        var tree = new FakeTree("search-root",
            "steamapps/common/Blake Stone - Aliens of Gold/AUDIOHED.BS6",
            "steamapps/common/Blake Stone - Aliens of Gold/VSWAP.BS6",
            "steamapps/common/Blake Stone - Planet Strike/AUDIOHED.VSI",
            "steamapps/common/Blake Stone - Planet Strike/VSWAP.VSI",
            "GOG Games/Blake Stone Shareware/audiohed.bs1",
            "GOG Games/Blake Stone Shareware/vswap.bs1");

        var result = await new BlakeStoneLocator().FindAsync(tree);

        Assert.Equal(3, result.Sources.Count);
        Assert.Equal(
            [BlakeStoneGame.AliensOfGoldShareware, BlakeStoneGame.AliensOfGold, BlakeStoneGame.PlanetStrike],
            result.Sources.Select(s => s.Edition));
        // Walk order is alphabetical per level, so GOG (G) precedes steamapps (s).
        Assert.Equal(["GOG", "Steam", "Steam"], result.Sources.Select(s => s.StoreLabel));
        // Lower-case names on disk still match.
        Assert.Equal("vswap.bs1", result.Sources[0].AssetFileName);
    }

    [Fact]
    public async Task Locator_StopsAtAGameFolderRatherThanSearchingUnderIt()
    {
        // What is below a game folder belongs to that game — a mod directory holding another
        // copy's files must not be reported as a second install.
        var tree = new FakeTree("root",
            "game/AUDIOHED.BS6",
            "game/VSWAP.BS6",
            "game/mods/other/AUDIOHED.VSI",
            "game/mods/other/VSWAP.VSI");

        var result = await new BlakeStoneLocator().FindAsync(tree);

        var source = Assert.Single(result.Sources);
        Assert.Equal("game", source.DirectoryPath);
        // Nothing below the game folder was even listed.
        Assert.DoesNotContain(tree.Listed, p => p.StartsWith("game/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Locator_IgnoresAGameFolderWithNoArtToExtract()
    {
        // The marker proves it is a game folder, but without the art container there is
        // nothing for the studio to import — and the walk still must not descend.
        var tree = new FakeTree("root", "game/AUDIOHED.BS6", "game/MAPHEAD.BS6");

        var result = await new BlakeStoneLocator().FindAsync(tree);

        Assert.Empty(result.Sources);
        Assert.False(result.Exhausted);
    }

    [Fact]
    public async Task Locator_GivesUpOnATreeTooDeepRatherThanRunningAway()
    {
        // 12 levels — past the depth cap, so the game at the bottom is never reached and the
        // caller is told the answer is incomplete.
        var deep = string.Join("/", Enumerable.Range(0, 12).Select(i => $"d{i}"));
        var tree = new FakeTree("root", $"{deep}/AUDIOHED.BS6", $"{deep}/VSWAP.BS6");

        var result = await new BlakeStoneLocator().FindAsync(tree);

        Assert.Empty(result.Sources);
        Assert.True(result.Exhausted);
    }

    [Fact]
    public async Task Locator_SurvivesAnUnreadableBranch()
    {
        var tree = new FakeTree("root", "denied/x", "ok/AUDIOHED.BS6", "ok/VSWAP.BS6")
        {
            Unreadable = { "denied" },
        };

        var result = await new BlakeStoneLocator().FindAsync(tree);

        Assert.Equal("ok", Assert.Single(result.Sources).DirectoryPath);
    }

    /// <summary>An in-memory <see cref="IDirectoryTree"/> built from full file paths.</summary>
    private sealed class FakeTree(string rootName, params string[] filePaths) : IDirectoryTree
    {
        public string RootName => rootName;

        /// <summary>Directories that answer as if the browser refused to read them.</summary>
        public HashSet<string> Unreadable { get; } = [];

        /// <summary>Every path the walk actually listed, for asserting what it skipped.</summary>
        public List<string> Listed { get; } = [];

        public Task<DirectoryEntries> ListAsync(string path, CancellationToken cancellationToken)
        {
            Listed.Add(path);
            if (Unreadable.Contains(path))
            {
                return Task.FromResult(DirectoryEntries.Empty);
            }
            var prefix = path.Length == 0 ? "" : path + "/";
            var files = new List<string>();
            var directories = new HashSet<string>();
            foreach (var filePath in filePaths)
            {
                if (!filePath.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }
                var rest = filePath[prefix.Length..];
                var slash = rest.IndexOf('/');
                if (slash < 0)
                {
                    files.Add(rest);
                }
                else
                {
                    directories.Add(rest[..slash]);
                }
            }
            return Task.FromResult(new DirectoryEntries(files, [.. directories]));
        }
    }
}
