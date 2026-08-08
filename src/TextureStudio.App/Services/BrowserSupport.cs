using Microsoft.JSInterop;

namespace TextureStudio.App.Services;

/// <summary>One browser API the studio depends on, and what it is depended on for.</summary>
/// <param name="Probe">Key returned by <c>studioInterop.probeCapabilities</c>.</param>
/// <param name="Name">What to call it in the gate — the spec's own name, so it is
/// searchable when someone goes looking for why their browser lacks it.</param>
/// <param name="Needed">What stops working without it, in the user's terms.</param>
/// <param name="Required">False for capabilities that only cost a convenience.</param>
public sealed record BrowserCapability(string Probe, string Name, string Needed, bool Required);

/// <summary>Checks, once on load, that the browser can actually run the studio.
///
/// Everything here happens client-side, so a missing API is not a degraded feature but a
/// dead end — without the File System Access API there is no workspace, and without a
/// workspace there is nothing to import into. Better to say so up front than to fail at the
/// folder picker with a cancelled-looking no-op.
///
/// WebAssembly is deliberately *not* probed: this runs inside the Blazor app, so its own
/// existence already proves it.</summary>
public sealed class BrowserSupport(IJSRuntime js)
{
    public static readonly IReadOnlyList<BrowserCapability> Capabilities =
    [
        new("fileSystemAccess", "File System Access API",
            "opening a workspace folder to store your project in", Required: true),
        new("fileSystemWrite", "File System Access — writable files",
            "saving redraws, generations and packs back to that folder", Required: true),
        new("offscreenCanvas", "OffscreenCanvas (with convertToBlob)",
            "composing sheets and encoding every PNG", Required: true),
        new("imageBitmap", "createImageBitmap",
            "decoding the game's art and the model's results", Required: true),
        new("indexedDb", "IndexedDB",
            "remembering your workspace between visits", Required: true),
        new("clipboardImage", "Async Clipboard API (images)",
            "copying a composed sheet straight to the clipboard", Required: false),
    ];

    /// <summary>Null until <see cref="CheckAsync"/> has run — the gate stays hidden rather
    /// than flashing before the answer is known.</summary>
    private IReadOnlyList<BrowserCapability>? _missing;

    public bool Checked => _missing is not null;

    /// <summary>Everything absent, required or not.</summary>
    public IReadOnlyList<BrowserCapability> Missing => _missing ?? [];

    /// <summary>The absences that make the studio unusable.</summary>
    public IReadOnlyList<BrowserCapability> MissingRequired =>
        [.. Missing.Where(capability => capability.Required)];

    /// <summary>True once checked and nothing required is absent.</summary>
    public bool IsSupported => Checked && MissingRequired.Count == 0;

    private Task? _check;

    /// <summary>Run the probe once and let every caller await the same result. Both the gate
    /// and the shell need the answer before their first render, and neither can rely on the
    /// other having asked first.</summary>
    public Task EnsureCheckedAsync() => _check ??= CheckAsync();

    private async Task CheckAsync()
    {
        Dictionary<string, bool> probe;
        try
        {
            probe = await js.InvokeAsync<Dictionary<string, bool>>("studioInterop.probeCapabilities")
                    ?? [];
        }
        catch
        {
            // The probe itself failing says nothing about the browser — assume support
            // rather than locking someone out of a working studio over a broken check.
            _missing = [];
            return;
        }
        _missing = [.. Capabilities.Where(c => !probe.GetValueOrDefault(c.Probe))];
    }
}
