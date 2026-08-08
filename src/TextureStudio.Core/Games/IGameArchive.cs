using TextureStudio.Core.Imaging;
using TextureStudio.Core.Model;

namespace TextureStudio.Core.Games;

/// <summary>One tile a game exposes: its id, and how the pipeline must treat it.</summary>
/// <param name="Id">Short, opaque and stable — <c>w22</c>, <c>s53</c>. The app carries it
/// around and never parses it; only the game knows what the characters mean.</param>
/// <param name="Kind">Which pipeline treatment this tile needs.</param>
public sealed record GameTile(string Id, TileKind Kind);

/// <summary>An opened game asset container: the studio's read side of the source art.
///
/// <see cref="Tiles"/> is the game's own enumeration, already filtered to the slots that
/// actually hold art. Its order is canonical — the studio sorts frames by position in this
/// list, so "engine order" costs the game nothing to express beyond listing tiles in it.</summary>
public interface IGameArchive
{
    /// <summary>File the archive was opened from — shown in status lines and used for
    /// edition detection.</summary>
    string SourceName { get; }

    /// <summary>Every tile the archive holds, in the game's own order.</summary>
    IReadOnlyList<GameTile> Tiles { get; }

    /// <summary>Decode one tile to RGBA. Cutouts carry alpha; full frames are opaque.</summary>
    RgbaImage Decode(string tileId);
}
