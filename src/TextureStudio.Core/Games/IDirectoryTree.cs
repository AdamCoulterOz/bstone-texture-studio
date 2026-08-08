namespace TextureStudio.Core.Games;

/// <summary>The slice of a real directory tree a locator needs. Implemented by the app over
/// the File System Access API, so Core never touches a filesystem itself.
///
/// Paths are root-relative and forward-slashed; <c>""</c> is the root.</summary>
public interface IDirectoryTree
{
    /// <summary>Name of the granted root, for display.</summary>
    string RootName { get; }

    /// <summary>Entries directly inside <paramref name="path"/>. Must return
    /// <see cref="DirectoryEntries.Empty"/> rather than throwing for a directory the browser
    /// refuses to read — a locator walks whatever it is given and an unreadable branch is a
    /// normal outcome, not an error.</summary>
    Task<DirectoryEntries> ListAsync(string path, CancellationToken cancellationToken);
}

/// <summary>One directory's immediate children, split by kind. Names only, not paths.</summary>
public sealed record DirectoryEntries(
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Directories)
{
    public static readonly DirectoryEntries Empty = new([], []);
}
