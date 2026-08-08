using TextureStudio.Core.Imaging;
using TextureStudio.Core.Model;

namespace TextureStudio.Core.Games;

/// <summary>An opened game asset container — the studio's read side of the source art.
/// Indices are dense per kind (0..<see cref="WallCount"/>-1 and 0..<see cref="SpriteCount"/>-1);
/// unused slots are reported by <see cref="IsEmpty"/> rather than skipped, because the engine
/// addresses tiles by index and the pack file names have to match.</summary>
public interface IGameArchive
{
    /// <summary>File the archive was opened from — shown in status lines and used for
    /// edition detection.</summary>
    string SourceName { get; }

    int WallCount { get; }

    int SpriteCount { get; }

    /// <summary>True for index slots the game leaves unused.</summary>
    bool IsEmpty(TileRef tile);

    /// <summary>Decode one tile to RGBA. Sprites carry alpha; walls are opaque.</summary>
    RgbaImage Decode(TileRef tile);
}
