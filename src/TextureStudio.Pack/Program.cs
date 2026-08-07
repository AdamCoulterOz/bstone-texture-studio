// Bundle the workspace's redraws into a bstone external-textures mod directory.
//
// bstone (hardware renderer, "External Textures" ON) probes each wall/sprite id at
// <search path>/aog/wall_<id:08>.png and <search path>/aog/sprite_<id:08>.png before
// falling back to the VSWAP art, so a mod dir of correctly-named PNGs is the whole
// pack format. Sprites keep their alpha; walls are opaque. DerivedDark tiles have no
// file of their own — they are synthesized here by darkening their light source's
// redraw with the project's dark params (AlternateDark darkens its OWN redraw, which
// is drawn in light form by pipeline convention).
//
// Usage: TextureStudio.Pack <workspace-dir> [out-dir] [--game aog|ps]
using System.Text.Json;
using StbImageSharp;
using StbImageWriteSharp;
using TextureStudio.Core.Imaging;
using TextureStudio.Core.Model;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: pack <workspace-dir> [out-dir] [--game aog|ps]");
    return 1;
}
var ws = args[0];
var outDir = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Path.Combine(ws, "pack");
var game = "aog";
for (var i = 1; i < args.Length - 1; i++)
{
    if (args[i] == "--game")
    {
        game = args[i + 1];
    }
}
var project = JsonSerializer.Deserialize<Project>(File.ReadAllBytes(Path.Combine(ws, "project.json")))!;
var assetDir = Path.Combine(outDir, game);
Directory.CreateDirectory(assetDir);

static RgbaImage LoadPng(string path)
{
    var decoded = ImageResult.FromMemory(File.ReadAllBytes(path), StbImageSharp.ColorComponents.RedGreenBlueAlpha);
    return new RgbaImage(decoded.Width, decoded.Height, decoded.Data);
}

static void SavePng(RgbaImage img, string path)
{
    using var stream = File.Create(path);
    new ImageWriter().WritePng(img.Pixels, img.Width, img.Height,
        StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);
}

string RedrawPath(TileRef tileRef) =>
    Path.Combine(ws, "redraws",
        $"{(tileRef.Kind == TileKind.Wall ? "wall" : "sprite")}_{tileRef.Index:D5}.png");

string PackPath(TileRef tileRef) =>
    Path.Combine(assetDir,
        $"{(tileRef.Kind == TileKind.Wall ? "wall" : "sprite")}_{tileRef.Index:D8}.png");

static string? DefaultLightSource(TileRef tileRef) =>
    tileRef.Kind == TileKind.Wall && tileRef.Index % 2 == 1
        ? new TileRef(TileKind.Wall, tileRef.Index - 1).Key
        : null;

// Every key that could produce a pack file: those with a redraw on disk, plus
// DerivedDark keys whose light source has one.
var keys = new SortedSet<string>();
foreach (var file in Directory.GetFiles(Path.Combine(ws, "redraws"), "*.png"))
{
    var stem = Path.GetFileNameWithoutExtension(file).Split('_');
    keys.Add(new TileRef(stem[0] == "wall" ? TileKind.Wall : TileKind.Sprite,
        int.Parse(stem[1])).Key);
}
foreach (var (key, meta) in project.Meta)
{
    if (meta.Role == PairRole.DerivedDark)
    {
        keys.Add(key);
    }
}

var packed = 0;
var darkened = 0;
var skipped = new List<string>();
foreach (var key in keys)
{
    var tileRef = TileRef.Parse(key);
    var meta = project.Meta.GetValueOrDefault(key);
    var ownPath = RedrawPath(tileRef);
    RgbaImage? image = null;
    var wasDark = false;
    if (meta?.Role == PairRole.DerivedDark)
    {
        var sourceKey = meta.LightSourceKey ?? DefaultLightSource(tileRef);
        var sourcePath = sourceKey is null ? null : RedrawPath(TileRef.Parse(sourceKey));
        if (sourcePath is not null && File.Exists(sourcePath))
        {
            image = DarkGenerator.Apply(LoadPng(sourcePath), project.DarkParams);
            wasDark = true;
        }
        else if (File.Exists(ownPath))
        {
            image = LoadPng(ownPath); // direct redraw of a dark tile, unusual but valid
        }
    }
    else if (File.Exists(ownPath))
    {
        image = LoadPng(ownPath);
        if (meta?.Role == PairRole.AlternateDark)
        {
            image = DarkGenerator.Apply(image, project.DarkParams);
            wasDark = true;
        }
    }
    if (image is null)
    {
        skipped.Add(key);
        continue;
    }
    SavePng(image, PackPath(tileRef));
    packed++;
    if (wasDark)
    {
        darkened++;
    }
}

Console.WriteLine($"packed {packed} textures into {assetDir}");
Console.WriteLine($"  {darkened} dark variants synthesized (multiply {project.DarkParams.Multiply:F2}, " +
                  $"gamma {project.DarkParams.Gamma:F2}, saturation {project.DarkParams.Saturation:F2})");
if (skipped.Count > 0)
{
    Console.WriteLine($"  {skipped.Count} keys had no usable art: " +
                      string.Join(" ", skipped.Take(12)) + (skipped.Count > 12 ? " …" : ""));
}
Console.WriteLine($"run:  bstone --mod_dir \"{Path.GetFullPath(outDir)}\"");
Console.WriteLine("then enable: Options → Video → Texturing → External Textures = ON");
return 0;
