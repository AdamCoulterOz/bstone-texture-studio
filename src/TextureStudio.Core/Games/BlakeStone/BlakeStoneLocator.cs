// The search is ported from bstone's launcher (bstone_game_source.cpp, GPL-2.0-or-later,
// Apogee Entertainment / bstone contributors), including its bounds, its marker files and
// its store labelling.
namespace TextureStudio.Core.Games.BlakeStone;

/// <summary>Finds Blake Stone installations beneath a directory the user granted, including
/// inside the macOS application bundles the storefronts ship.</summary>
public sealed class BlakeStoneLocator : IGameLocator
{
    /// <summary>Deep enough for the deepest layout seen — a storefront bundle wrapping a
    /// DOS-emulator bundle, eight levels from the folder the user can pick — with room to
    /// spare for one holding several games.</summary>
    private const int MaxSearchDepth = 10;

    /// <summary>So granting a whole drive gives up rather than hanging.</summary>
    private const int MaxVisitedDirectories = 4096;

    /// <summary>The file that says a folder holds a game. Matching one is what tells game
    /// folders apart from everything else on the way down.</summary>
    private static readonly string[] MarkerFileNames =
    [
        "AUDIOHED.BS6", // Aliens of Gold.
        "AUDIOHED.BS1", // Aliens of Gold, shareware.
        "AUDIOHED.VSI", // Planet Strike.
    ];

    /// <summary>The art container the studio extracts from, per edition. The extension is a
    /// far better edition test than anything inside the file.</summary>
    private static readonly (string FileName, GameEdition Edition)[] AssetFiles =
    [
        ("VSWAP.BS6", BlakeStoneGame.AliensOfGold),
        ("VSWAP.BS1", BlakeStoneGame.AliensOfGoldShareware),
        ("VSWAP.VSI", BlakeStoneGame.PlanetStrike),
    ];

    public string SearchHint =>
        "Grant the folder a copy was installed under — or a broad one like Applications, " +
        "your home folder or a Steam library. Application bundles are searched inside.";

    public async Task<GameSearchResult> FindAsync(
        IDirectoryTree tree, CancellationToken cancellationToken = default)
    {
        var state = new SearchState();
        await SearchAsync(tree, "", 0, state, cancellationToken);
        return new GameSearchResult(state.Sources, state.VisitedDirectories, state.Exhausted);
    }

    private sealed class SearchState
    {
        public List<GameSource> Sources { get; } = [];
        public int VisitedDirectories { get; set; }
        public bool Exhausted { get; set; }
    }

    private static async Task SearchAsync(
        IDirectoryTree tree, string path, int depth, SearchState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > MaxSearchDepth || state.VisitedDirectories >= MaxVisitedDirectories)
        {
            state.Exhausted = true;
            return;
        }
        state.VisitedDirectories++;
        var entries = await tree.ListAsync(path, cancellationToken);

        if (entries.Files.Any(IsMarkerFileName))
        {
            // A folder holding a game's files is an answer, not a place to search under:
            // what is below it belongs to that game.
            foreach (var (fileName, edition) in AssetFiles)
            {
                var match = entries.Files.FirstOrDefault(
                    f => string.Equals(f, fileName, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    state.Sources.Add(new GameSource(
                        path, match, edition, StoreLabel(path), DisplayPath(tree.RootName, path)));
                }
            }
            return;
        }

        // Deterministic order, so the same folder always yields the same list.
        foreach (var name in entries.Directories.OrderBy(n => n, StringComparer.Ordinal))
        {
            await SearchAsync(
                tree, path.Length == 0 ? name : $"{path}/{name}", depth + 1, state,
                cancellationToken);
        }
    }

    private static bool IsMarkerFileName(string fileName) =>
        MarkerFileNames.Any(marker => string.Equals(fileName, marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>Which store a copy came from, read off the path. bstone can also probe for
    /// GOG's id file a few levels up; here the path is only ever the part below the granted
    /// root, so the folder names are all there is to go on.</summary>
    private static string StoreLabel(string path)
    {
        var haystack = $"/{path.ToUpperInvariant()}/";
        if (haystack.Contains("/STEAMAPPS/"))
        {
            return "Steam";
        }
        if (haystack.Contains("/GOG GAMES/") || haystack.Contains("/GOG GALAXY/") ||
            haystack.Contains("/GOG.COM/"))
        {
            return "GOG";
        }
        return "Folder";
    }

    /// <summary>Everything below an application bundle is that bundle's business, and the
    /// whole chain is far too long to read — so cut there. Paths are shown under the granted
    /// root's name, since that is all the user recognises.</summary>
    private static string DisplayPath(string rootName, string path)
    {
        var index = path.IndexOf(".app/", StringComparison.OrdinalIgnoreCase);
        var shown = index >= 0 ? path[..(index + 4)] : path;
        return shown.Length == 0 ? rootName : $"{rootName}/{shown}";
    }
}
