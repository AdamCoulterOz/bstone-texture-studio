using Microsoft.JSInterop;

namespace TextureStudio.App.Services;

/// <summary>A user-picked directory on real disk (File System Access API). The handle is
/// remembered by the browser across sessions; permission may need one click to re-grant.</summary>
public sealed class WorkspaceService(IJSRuntime js)
{
    public string? Name { get; private set; }
    public bool IsOpen => Name is not null;

    /// <summary>True when the connected folder refused a write probe — the handle is
    /// effectively read-only in this browser context.</summary>
    public bool WritesBlocked { get; private set; }

    public async Task<bool> ProbeWriteAsync()
    {
        if (!IsOpen)
        {
            return false;
        }
        WritesBlocked = !await js.InvokeAsync<bool>("studioInterop.wsProbeWrite");
        return !WritesBlocked;
    }

    public async Task<bool> PickAsync()
    {
        Name = await js.InvokeAsync<string?>("studioInterop.pickWorkspace");
        return IsOpen;
    }

    public async Task<bool> HasStoredHandleAsync() =>
        await js.InvokeAsync<bool>("studioInterop.wsHasStored");

    public async Task<bool> RestoreAsync(bool interactive)
    {
        Name = await js.InvokeAsync<string?>("studioInterop.restoreWorkspace", interactive);
        return IsOpen;
    }

    public async Task WriteAsync(string path, byte[] bytes) =>
        await js.InvokeVoidAsync("studioInterop.wsWrite", path, bytes);

    public async Task<byte[]?> ReadAsync(string path) =>
        await js.InvokeAsync<byte[]?>("studioInterop.wsRead", path);

    public async Task<string[]> ListAsync(string path) =>
        await js.InvokeAsync<string[]>("studioInterop.wsList", path);

    /// <summary>Disconnect from the folder. The browser still remembers the handle, so
    /// Reconnect can offer it again; files on disk are untouched.</summary>
    public void Close()
    {
        Name = null;
        WritesBlocked = false;
    }
}
