namespace TextureStudio.Core.Games;

/// <summary>Read-only engine reference data for a game's tiles — sprite constants, the
/// engine's own names, actor families. Shown in Properties and used to seed the item layer;
/// never written into the user's curated metadata.
///
/// The table itself ships as a static asset so Core stays free of HTTP: the app fetches
/// <see cref="AssetPath"/> once and hands the bytes to <see cref="Load"/>.</summary>
public interface IGameMetadata
{
    /// <summary>App-relative path of the JSON table in wwwroot.</summary>
    string AssetPath { get; }

    /// <summary>False until <see cref="Load"/> has run; lookups return null before then.</summary>
    bool IsLoaded { get; }

    /// <summary>Parse the fetched table. Called once per session. A malformed table must
    /// leave the provider loaded-but-empty rather than throw, so lookups degrade to null and
    /// the studio stays usable without reference data.</summary>
    void Load(byte[] json);

    /// <summary>Engine reference for one tile, or null when unknown.</summary>
    CanonicalTile? Lookup(GameEdition edition, string tileId);

    /// <summary>Grouping key that collects one actor's frames into a single item during the
    /// item-layer build; null means the tile stands alone.</summary>
    string? ActorFamily(GameEdition edition, string tileId);
}

/// <summary>Engine-derived facts about a single tile — all optional except the constant.</summary>
/// <param name="Constant">The engine's own symbol for the tile, e.g. "SPR_MUTHUM1_W2_7".</param>
/// <param name="EngineName">Name the game's own tables give it, e.g. "Water Puddle".</param>
/// <param name="InGameLabel">Text the game shows the player, when it has one.</param>
/// <param name="TypeLabel">Humanized behavior, e.g. "blocking object", "pickup: health".</param>
/// <param name="FrameLabel">Humanized animation slot, e.g. "walk 2 · rotation 7/8".</param>
public sealed record CanonicalTile(
    string Constant,
    string? EngineName,
    string? InGameLabel,
    string? TypeLabel,
    string? FrameLabel);
