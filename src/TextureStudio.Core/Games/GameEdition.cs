namespace TextureStudio.Core.Games;

/// <summary>One release of a game: same formats, different content set.</summary>
/// <param name="Id">Persisted as <see cref="Model.Project.EditionId"/> and used as the key
/// into the game's engine reference table. Stable — never repurpose one.</param>
/// <param name="Name">Display name for the edition picker.</param>
/// <param name="AssetDirectory">Folder the source port probes for external textures inside a
/// mod directory. Several editions can share one (both Aliens of Gold releases use
/// <c>aog</c>).</param>
public sealed record GameEdition(string Id, string Name, string AssetDirectory);
