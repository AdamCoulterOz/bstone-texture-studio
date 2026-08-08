using TextureStudio.Core.Model;

namespace TextureStudio.Core.Games.BlakeStone;

/// <summary>The one place that knows what a Blake Stone tile id means.
///
/// Ids are <c>w&lt;n&gt;</c> for a wall chunk and <c>s&lt;n&gt;</c> for a sprite chunk, where
/// n is the index the engine addresses that chunk by. Everything outside this plugin treats
/// them as opaque strings; the mapping back to VSWAP chunks, workspace files and pack file
/// names lives here.</summary>
internal static class BlakeStoneTiles
{
    private const char WallPrefix = 'w';
    private const char SpritePrefix = 's';

    public static string WallId(int index) => $"{WallPrefix}{index}";

    public static string SpriteId(int index) => $"{SpritePrefix}{index}";

    /// <summary>Walls fill their cell; sprites are objects with transparency around them.</summary>
    public static TileKind KindOf(string tileId) =>
        IsWall(tileId) ? TileKind.Full : TileKind.Cutout;

    public static bool IsWall(string tileId) =>
        tileId.Length > 1 && tileId[0] == WallPrefix;

    /// <summary>Chunk index, or -1 for an id this game did not mint.</summary>
    public static int IndexOf(string tileId) =>
        tileId.Length > 1 && int.TryParse(tileId.AsSpan(1), out var index) ? index : -1;

    /// <summary>Workspace file name. Still the pre-plugin spelling
    /// (<c>wall_00012.png</c>) on purpose: every existing workspace already has thousands of
    /// files under these names, and an id scheme is not worth orphaning them for.</summary>
    public static string WorkspaceFileName(string tileId) =>
        $"{Prefix(tileId)}_{IndexOf(tileId):D5}.png";

    /// <summary>Path inside the mod directory, which the engine probes by id.</summary>
    public static string PackFileName(string tileId) =>
        $"{Prefix(tileId)}_{IndexOf(tileId):D8}.png";

    private static string Prefix(string tileId) => IsWall(tileId) ? "wall" : "sprite";

    /// <summary>Pre-plugin ids were <c>wall:12</c> / <c>sprite:107</c>. Rewriting them is
    /// idempotent — anything not in that exact shape is already current and passes through.</summary>
    public static string Migrate(string tileId)
    {
        var separator = tileId.IndexOf(':');
        if (separator < 0)
        {
            return tileId;
        }
        var kind = tileId[..separator];
        var rest = tileId[(separator + 1)..];
        if (!int.TryParse(rest, out var index))
        {
            return tileId;
        }
        return kind switch
        {
            "wall" => WallId(index),
            "sprite" => SpriteId(index),
            _ => tileId,
        };
    }
}
