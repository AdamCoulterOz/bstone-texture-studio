using TextureStudio.Core.Model;

namespace TextureStudio.Core.Games;

/// <summary>A source game the studio can redraw textures for. Everything the pipeline needs
/// that is <i>not</i> shared across games lives behind this interface: how to open the game's
/// asset container, which editions exist, the engine's light/dark convention, where a packed
/// texture belongs inside a mod directory, and how to install the result.
///
/// To add a game: implement this plus <see cref="IGameArchive"/> (and optionally
/// <see cref="IGameMetadata"/>), then add it to <see cref="GameCatalog"/>'s built-in list.
/// Everything downstream — sheets, prompts, slicing, keying, placement, packing — is already
/// game-agnostic and needs no changes.</summary>
public interface IGame
{
    /// <summary>Stable identifier persisted as <see cref="Project.GameId"/>. Never change it
    /// for a shipped game, or existing workspaces stop resolving to it.</summary>
    string Id { get; }

    /// <summary>Display name, e.g. "Blake Stone".</summary>
    string Name { get; }

    /// <summary>One line for the game chooser: what this plugin covers.</summary>
    string Description { get; }

    /// <summary>Releases that share this game's formats. The pack directory and the engine
    /// reference table are both chosen per edition.</summary>
    IReadOnlyList<GameEdition> Editions { get; }

    /// <summary>File-picker accept list for the import control, e.g. ".BS6,.VSI".</summary>
    string ImportAccept { get; }

    /// <summary>What the user has to supply, e.g. "VSWAP.BS6 (Aliens of Gold) or VSWAP.VSI
    /// (Planet Strike)" — shown on the import control and in the empty-workspace prompt.</summary>
    string ImportHint { get; }

    /// <summary>Open the game's asset container. Throws <see cref="InvalidDataException"/>
    /// when the bytes are not this game's format.</summary>
    IGameArchive OpenArchive(byte[] bytes, string fileName);

    /// <summary>Which edition an opened archive is, for workspaces that have not pinned
    /// one explicitly.</summary>
    GameEdition DetectEdition(IGameArchive archive);

    /// <summary>How the pipelines must treat a tile. Derived from the id alone, so it still
    /// answers with no archive open — the CLIs only ever have <c>project.json</c>.</summary>
    TileKind KindOf(string tileId);

    /// <summary>File name for a tile's art inside the workspace (<c>originals/</c>,
    /// <c>redraws/</c>). Stable forever: changing it orphans every file already written.</summary>
    string WorkspaceFileName(string tileId);

    /// <summary>The tile whose (redrawn) art is darkened to produce <paramref name="tileId"/>
    /// under the engine's own convention, or null when there is no default pairing.</summary>
    string? DefaultLightSource(string tileId);

    /// <summary>Role Auto-pair assigns to an unclassified tile; null leaves it alone.</summary>
    PairRole? AutoPairRole(string tileId);

    /// <summary>Rewrite a tile id persisted by an older release, or return it unchanged.
    /// Runs on every load and must be idempotent — a workspace is years of curation keyed
    /// by these strings, so a wrong answer silently detaches art from its metadata.</summary>
    string MigrateTileId(string tileId);

    /// <summary>Category Auto-pair moves newly paired tiles into.</summary>
    string AutoPairCategory { get; }

    /// <summary>What Auto-pair does, in a few words — used in its tooltip and status line
    /// (e.g. "even/odd light-dark defaults").</summary>
    string AutoPairDescription { get; }

    /// <summary>Plan an installable pack: every file it contains, where that file goes, and
    /// which tile's redraw supplies its pixels. Pure — the caller reads the art, applies the
    /// transform and writes the bytes.</summary>
    /// <param name="project">Supplies per-tile roles and light/dark pairing.</param>
    /// <param name="edition">Decides the layout inside the mod directory.</param>
    /// <param name="redrawKeys">Tiles that have redraw art available to pack.</param>
    PackPlan PlanPack(Project project, GameEdition edition, IReadOnlyCollection<string> redrawKeys);

    /// <summary>Where to get the source port and how to point it at a finished pack.</summary>
    InstallGuide InstallGuide { get; }

    /// <summary>Engine reference data (sprite constants, in-game names, actor families), or
    /// null when the game ships none.</summary>
    IGameMetadata? Metadata { get; }

    /// <summary>Finds installed copies under a directory the user granted, so they do not
    /// have to know where a storefront buried the files. Null when the game cannot be
    /// located by scanning and the user must pick the file themselves.</summary>
    IGameLocator? Locator { get; }
}
