using TextureStudio.Core.Model;

namespace TextureStudio.Core.Games.BlakeStone;

/// <summary>Blake Stone: Aliens of Gold / Planet Strike, packed for the BStone source port.
/// The engine probes <c>&lt;search path&gt;/&lt;edition dir&gt;/{wall|sprite}_&lt;id:08&gt;.png</c>
/// with its hardware renderer and "External Textures" on, so a mod directory of
/// correctly-named PNGs is the whole pack format — no engine changes.</summary>
public sealed class BlakeStoneGame : IGame
{
    /// <summary>Persisted in project.json. Must match <see cref="Project.GameId"/>'s default,
    /// which is what pre-plugin workspaces deserialize to (asserted by a test).</summary>
    public const string GameId = "blake-stone";

    /// <summary>Aliens of Gold's shareware release stops well short of the full game's sprite
    /// set, which is how the two are told apart — they share a file name.</summary>
    private const int SharewareSpriteCeiling = 560;

    public static readonly GameEdition AliensOfGold = new("aog_full", "Aliens of Gold", "aog");

    public static readonly GameEdition AliensOfGoldShareware =
        new("aog_sw", "Aliens of Gold (shareware)", "aog");

    public static readonly GameEdition PlanetStrike = new("ps", "Planet Strike", "ps");

    public string Id => GameId;

    public string Name => "Blake Stone";

    public string Description =>
        "Aliens of Gold and Planet Strike — wall and sprite art from VSWAP, packed as " +
        "external textures for the BStone source port.";

    public IReadOnlyList<GameEdition> Editions { get; } =
        [AliensOfGold, AliensOfGoldShareware, PlanetStrike];

    public string ImportAccept => ".BS6,.BS1,.VSI,.bs6,.bs1,.vsi";

    public string ImportHint =>
        "VSWAP.BS6 (Aliens of Gold), VSWAP.BS1 (shareware) or VSWAP.VSI (Planet Strike)";

    public IGameArchive OpenArchive(byte[] bytes, string fileName) =>
        new VswapArchive(bytes, fileName);

    /// <summary>The file extension names the release outright (BS6 = six episodes, BS1 = the
    /// one-episode shareware, VSI = Planet Strike), so it decides whenever it is one we know.
    /// Sprite count only breaks the tie for a renamed file.</summary>
    public GameEdition DetectEdition(IGameArchive archive) =>
        Path.GetExtension(archive.SourceName).ToUpperInvariant() switch
        {
            ".VSI" => PlanetStrike,
            ".BS1" => AliensOfGoldShareware,
            ".BS6" => AliensOfGold,
            _ => archive is VswapArchive vswap && vswap.RawSpriteCount <= SharewareSpriteCeiling
                ? AliensOfGoldShareware
                : AliensOfGold,
        };

    public TileKind KindOf(string tileId) => BlakeStoneTiles.KindOf(tileId);

    public string WorkspaceFileName(string tileId) => BlakeStoneTiles.WorkspaceFileName(tileId);

    public string MigrateTileId(string tileId) => BlakeStoneTiles.Migrate(tileId);

    /// <summary>Wall chunks alternate light (even) / dark (odd), so an odd wall's art is
    /// derived from its even sibling unless the user points it somewhere else. Sprites carry
    /// no such convention.</summary>
    public string? DefaultLightSource(string tileId) =>
        BlakeStoneTiles.IsWall(tileId) && BlakeStoneTiles.IndexOf(tileId) % 2 == 1
            ? BlakeStoneTiles.WallId(BlakeStoneTiles.IndexOf(tileId) - 1)
            : null;

    public PairRole? AutoPairRole(string tileId) =>
        !BlakeStoneTiles.IsWall(tileId) ? null
        : BlakeStoneTiles.IndexOf(tileId) % 2 == 0 ? PairRole.Light
        : PairRole.DerivedDark;

    public string AutoPairCategory => "Surfaces";

    public string AutoPairDescription => "even/odd light-dark defaults";

    /// <summary>The engine probes this path inside any of its search paths before falling
    /// back to the VSWAP art, so correctly-named PNGs are the whole pack format.</summary>
    private static string PackPath(GameEdition edition, string tileId) =>
        $"{edition.AssetDirectory}/{BlakeStoneTiles.PackFileName(tileId)}";

    public PackPlan PlanPack(
        Project project, GameEdition edition, IReadOnlyCollection<string> redrawKeys)
    {
        var available = new HashSet<string>(redrawKeys);
        // Every key that could produce a file: those with redraw art, plus the derived darks,
        // which have none of their own but are synthesized from their light source's.
        var candidates = new SortedSet<string>(available, StringComparer.Ordinal);
        foreach (var (key, meta) in project.Meta)
        {
            if (meta.Role == PairRole.DerivedDark)
            {
                candidates.Add(key);
            }
        }

        var entries = new List<PackEntry>();
        var skipped = new List<string>();
        foreach (var key in candidates)
        {
            var meta = project.Meta.GetValueOrDefault(key);
            string? source = null;
            var transform = PackTransform.None;

            if (meta?.Role == PairRole.DerivedDark)
            {
                var lightKey = meta.LightSourceKey ?? DefaultLightSource(key);
                if (lightKey is not null && available.Contains(lightKey))
                {
                    source = lightKey;
                    transform = PackTransform.Darken;
                }
                else if (available.Contains(key))
                {
                    source = key; // a direct redraw of a dark tile: unusual, but valid
                }
            }
            else if (available.Contains(key))
            {
                source = key;
                // An alternate dark is drawn light like everything else, so it darkens itself.
                transform = meta?.Role == PairRole.AlternateDark
                    ? PackTransform.Darken
                    : PackTransform.None;
            }

            if (source is null)
            {
                skipped.Add(key);
            }
            else
            {
                entries.Add(new PackEntry(PackPath(edition, key), source, transform));
            }
        }
        return new PackPlan(entries, skipped);
    }

    // Anchors are the published README's section slugs, checked against the rendered page.
    // They are numbered, so a new section upstream shifts them — a stale one lands the reader
    // at the top of the README rather than breaking, but it is worth re-checking on release.
    public InstallGuide InstallGuide { get; } = new(
        "BStone",
        "https://github.com/bibendovsky/bstone/releases/latest",
        "https://github.com/bibendovsky/bstone#43---addons",
        "enable Options → Video → Texturing → External Textures",
        "https://github.com/bibendovsky/bstone#8---external-textures");

    public IGameMetadata? Metadata { get; } = new BlakeStoneMetadata();

    public IGameLocator? Locator { get; } = new BlakeStoneLocator();
}
