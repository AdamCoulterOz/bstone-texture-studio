using Microsoft.JSInterop;
using TextureStudio.Core.Games;

namespace TextureStudio.App.Services;

/// <summary>A read-only directory the user granted for the game locator to search — typically
/// Applications, a home folder or a Steam library. Deliberately separate from the workspace:
/// that is written to constantly, this must never be.
///
/// The handle is remembered in IndexedDB, so a return visit re-scans without asking again.</summary>
public sealed class ContentSearchService(IJSRuntime js) : IDirectoryTree
{
    /// <summary>Granted root's name, or null when nothing is connected.</summary>
    public string? Name { get; private set; }

    public bool IsOpen => Name is not null;

    string IDirectoryTree.RootName => Name ?? "";

    public async Task<bool> PickAsync()
    {
        Name = await js.InvokeAsync<string?>("studioInterop.pickContentRoot");
        return IsOpen;
    }

    public async Task<bool> HasStoredHandleAsync() =>
        await js.InvokeAsync<bool>("studioInterop.contentHasStored");

    public async Task<bool> RestoreAsync(bool interactive)
    {
        Name = await js.InvokeAsync<string?>("studioInterop.restoreContentRoot", interactive);
        return IsOpen;
    }

    public async Task ForgetAsync()
    {
        Name = null;
        await js.InvokeVoidAsync("studioInterop.contentForget");
    }

    /// <summary>Bytes of a root-relative file, or null when it cannot be read.</summary>
    public async Task<byte[]?> ReadAsync(string path) =>
        await js.InvokeAsync<byte[]?>("studioInterop.contentRead", path);

    async Task<DirectoryEntries> IDirectoryTree.ListAsync(
        string path, CancellationToken cancellationToken)
    {
        var listing = await js.InvokeAsync<Listing>(
            "studioInterop.contentList", cancellationToken, path);
        return new DirectoryEntries(listing.Files, listing.Dirs);
    }

    /// <summary>Shape of one <c>contentList</c> reply.</summary>
    private sealed record Listing(string[] Files, string[] Dirs);
}
