using TextureStudio.Core.Games.BlakeStone;

namespace TextureStudio.Core.Games;

/// <summary>The installed game plugins. The app registers one as a singleton (so the games'
/// loaded reference tables are shared); the CLIs just construct the built-in set.</summary>
public sealed class GameCatalog
{
    private readonly List<IGame> _games;

    /// <summary>The built-in games, in chooser order. Add new plugins here.</summary>
    public GameCatalog()
        : this(new BlakeStoneGame())
    {
    }

    public GameCatalog(params IGame[] games)
    {
        if (games.Length == 0)
        {
            throw new ArgumentException("A catalog needs at least one game.", nameof(games));
        }
        _games = [.. games];
    }

    public IReadOnlyList<IGame> Games => _games;

    /// <summary>The game a workspace targets. Falls back to the first plugin so a project
    /// naming a game that is no longer installed still opens (with its art intact) instead
    /// of failing to load.</summary>
    public IGame Get(string? gameId) =>
        _games.FirstOrDefault(game => game.Id == gameId) ?? _games[0];

    /// <summary>Resolve a workspace's edition: the pinned one when it still exists,
    /// otherwise detected from the loaded archive, otherwise the game's first edition.</summary>
    public static GameEdition ResolveEdition(IGame game, string? editionId, IGameArchive? archive) =>
        game.Editions.FirstOrDefault(edition => edition.Id == editionId)
        ?? (archive is not null ? game.DetectEdition(archive) : game.Editions[0]);
}
