// Re-slice archived generation sheets against the workspace's group manifests using the
// production Core pipeline (SheetSlicer + AlphaKeyer), writing results into redraws/.
// Usage: TextureStudio.Rekey <workspace-dir> [targetPx]
using System.Text.Json;
using StbImageSharp;
using StbImageWriteSharp;
using TextureStudio.Core.Imaging;
using TextureStudio.Core.Model;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: rekey <workspace-dir> [targetPx]");
    return 1;
}
var ws = args[0];
var targetPx = args.Length > 1 ? int.Parse(args[1]) : 512;
var project = JsonSerializer.Deserialize<Project>(File.ReadAllBytes(Path.Combine(ws, "project.json")))!;

static string Safe(string name) =>
    string.Join("-", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries))
        .Replace(' ', '-');

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

static string FileNameFor(string key)
{
    var tileRef = TileRef.Parse(key);
    var prefix = tileRef.Kind == TileKind.Wall ? "wall" : "sprite";
    return $"{prefix}_{tileRef.Index:D5}.png";
}

var generations = Directory.GetFiles(Path.Combine(ws, "generations"), "*-import.png")
    .Concat(Directory.GetFiles(Path.Combine(ws, "generations"), "*-gen.png"))
    .OrderBy(f => f)
    .ToList();
var processed = 0;
foreach (var file in generations)
{
    var stem = Path.GetFileNameWithoutExtension(file);
    var group = project.Groups.FirstOrDefault(g =>
        g.LastExport is not null && stem.Contains(Safe(g.Name), StringComparison.OrdinalIgnoreCase));
    if (group is null)
    {
        Console.WriteLine($"skip {stem}: no matching group manifest");
        continue;
    }
    Console.WriteLine($"{stem} -> group '{group.Name}' ({group.TileKeys.Count} tiles)");
    var sheet = LoadPng(file);
    var results = SheetSlicer.Slice(sheet, group.LastExport!, targetPx);
    foreach (var result in results)
    {
        var image = result.Image;
        var mode = "-";
        if (TileRef.Parse(result.TileKey).Kind == TileKind.Sprite)
        {
            mode = AlphaKeyer.KeyAuto(image);
            // Parity with the app: refit small objects into the original art's footprint.
            var originalPath = Path.Combine(ws, "originals", FileNameFor(result.TileKey));
            if (File.Exists(originalPath))
            {
                image = SpriteFootprint.Normalize(image, LoadPng(originalPath));
            }
        }
        var alpha0 = 0;
        for (var i = 3; i < image.Pixels.Length; i += 4)
        {
            if (image.Pixels[i] == 0)
            {
                alpha0++;
            }
        }
        var pct = 100.0 * alpha0 / (image.Width * image.Height);
        var baseline = AlphaKeyer.BaselineFraction(image);
        SavePng(image, Path.Combine(ws, "redraws", FileNameFor(result.TileKey)));
        Console.WriteLine($"  {result.TileKey,-12} keyed:{mode,-6} alpha0:{pct,5:F1}% " +
                          $"baseline:{baseline:F3}{(result.UsedFallback ? " (proportional)" : "")}");
        processed++;
    }
}
Console.WriteLine($"done: {processed} tiles rewritten");
return 0;
