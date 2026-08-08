// Bundle the workspace's redraws into the source port's external-textures mod directory.
//
// The workspace's game plugin decides what the pack contains (IGame.PlanPack) and how the
// result is installed (IGame.InstallGuide); everything here is game-agnostic. Sprites
// keep their alpha; walls are opaque. DerivedDark tiles have no file of their own — they are
// synthesized here by darkening their light source's redraw with the project's dark params
// (AlternateDark darkens its OWN redraw, which is drawn in light form by pipeline convention).
//
// Usage: TextureStudio.Pack <workspace-dir> [out-dir] [--edition <id>]
using System.Text.Json;
using StbImageSharp;
using StbImageWriteSharp;
using TextureStudio.Core.Games;
using TextureStudio.Core.Imaging;
using TextureStudio.Core.Model;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: pack <workspace-dir> [out-dir] [--edition <id>]");
    return 1;
}
var ws = args[0];
var outDir = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Path.Combine(ws, "pack");
string? editionArg = null;
for (var i = 1; i < args.Length - 1; i++)
{
    if (args[i] == "--edition")
    {
        editionArg = args[i + 1];
    }
}
var project = JsonSerializer.Deserialize<Project>(File.ReadAllBytes(Path.Combine(ws, "project.json")))!;
var game = new GameCatalog().Get(project.GameId);
var edition = game.Editions.FirstOrDefault(e =>
                  e.Id == editionArg || e.AssetDirectory == editionArg)
              ?? GameCatalog.ResolveEdition(game, project.EditionId, archive: null);
Console.WriteLine($"game: {game.Name} — {edition.Name}");

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

string RedrawPath(string tileId) =>
    Path.Combine(ws, "redraws", game.WorkspaceFileName(tileId));

// The redraws on disk are the art available to pack. Only the game can name them, so the
// mapping is built by asking it for each id it knows rather than parsing the file names.
var redrawFiles = Directory.GetFiles(Path.Combine(ws, "redraws"), "*.png")
    .Select(Path.GetFileName)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
var redrawKeys = project.Meta.Keys
    .Concat(project.Items.SelectMany(i => i.TileKeys))
    .Concat(project.Groups.SelectMany(g => g.TileKeys))
    .Concat(project.TileVersions.Keys)
    .Distinct(StringComparer.Ordinal)
    .Where(id => redrawFiles.Contains(game.WorkspaceFileName(id)))
    .ToList();
var plan = game.PlanPack(project, edition, redrawKeys);

var packed = 0;
foreach (var entry in plan.Entries)
{
    var image = LoadPng(RedrawPath(entry.SourceTileKey));
    if (entry.Transform == PackTransform.Darken)
    {
        image = DarkGenerator.Apply(image, project.DarkParams);
    }
    var packPath = Path.Combine(outDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(packPath)!);
    SavePng(image, packPath);
    packed++;
}

Console.WriteLine($"packed {packed} textures into {Path.Combine(outDir, edition.AssetDirectory)}");
Console.WriteLine($"  {plan.TransformedCount} dark variants synthesized " +
                  $"(multiply {project.DarkParams.Multiply:F2}, " +
                  $"gamma {project.DarkParams.Gamma:F2}, saturation {project.DarkParams.Saturation:F2})");
if (plan.SkippedTileKeys.Count > 0)
{
    Console.WriteLine($"  {plan.SkippedTileKeys.Count} keys had no usable art: " +
                      string.Join(" ", plan.SkippedTileKeys.Take(12)) +
                      (plan.SkippedTileKeys.Count > 12 ? " …" : ""));
}
var guide = game.InstallGuide;
Console.WriteLine($"install: point {guide.PortName} ({guide.PortUrl}) at " +
                  $"\"{Path.GetFullPath(outDir)}\" — {guide.PackInstructionsUrl}");
if (guide.ExtraStep is not null)
{
    Console.WriteLine($"then: {guide.ExtraStep}" +
                      (guide.ExtraStepUrl is null ? "" : $" — {guide.ExtraStepUrl}"));
}
return 0;
