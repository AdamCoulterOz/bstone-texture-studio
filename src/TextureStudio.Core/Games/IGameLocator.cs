namespace TextureStudio.Core.Games;

/// <summary>Finds a game's content under a directory the user granted.
///
/// This exists because the files are rarely where a person would point: a storefront buries
/// them inside an application bundle — four levels down for one, eight for another — and on
/// macOS a bundle cannot even be opened from a folder picker. So the user grants whatever is
/// broad enough (one game, a folder of games, or an Applications folder) and the locator
/// searches down from it.
///
/// Ported from bstone's launcher (<c>bstone_game_source.cpp</c>). Its other half — reading the
/// Windows registry, Steam's library manifests and GOG's Galaxy database to find installs
/// without asking — cannot come with it: a browser has no ambient filesystem, and every byte
/// has to come from a handle the user granted.</summary>
public interface IGameLocator
{
    /// <summary>One line for the folder-picking control: where this game's files usually
    /// live, and what is worth granting.</summary>
    string SearchHint { get; }

    /// <summary>Every copy of the game found at or below the tree's root, in walk order.</summary>
    Task<GameSearchResult> FindAsync(IDirectoryTree tree, CancellationToken cancellationToken = default);
}

/// <summary>One copy of a game found by a locator.</summary>
/// <param name="DirectoryPath">Root-relative directory holding the files; "" is the root.</param>
/// <param name="AssetFileName">The file the studio extracts art from, e.g. "VSWAP.BS6".</param>
/// <param name="Edition">Which release this copy is, decided by the locator.</param>
/// <param name="StoreLabel">Where the copy came from — "Steam", "GOG" or "Folder" — so two
/// copies of the same edition are tellable apart.</param>
/// <param name="DisplayPath">A path short enough to read: cut at the application bundle.</param>
public sealed record GameSource(
    string DirectoryPath,
    string AssetFileName,
    GameEdition Edition,
    string StoreLabel,
    string DisplayPath)
{
    /// <summary>Root-relative path of the asset file, ready for
    /// <see cref="IDirectoryTree"/>'s reader.</summary>
    public string AssetPath =>
        DirectoryPath.Length == 0 ? AssetFileName : $"{DirectoryPath}/{AssetFileName}";
}

/// <summary>Outcome of one search.</summary>
/// <param name="Sources">Copies found, in walk order.</param>
/// <param name="DirectoriesVisited">How many directories the walk listed.</param>
/// <param name="Exhausted">True when a depth or directory cap stopped the walk before it
/// finished — the results may be incomplete, and a narrower root would do better.</param>
public sealed record GameSearchResult(
    IReadOnlyList<GameSource> Sources,
    int DirectoriesVisited,
    bool Exhausted)
{
    public static readonly GameSearchResult None = new([], 0, false);
}
