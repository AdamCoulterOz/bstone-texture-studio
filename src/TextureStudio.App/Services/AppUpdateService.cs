using Microsoft.JSInterop;

namespace TextureStudio.App.Services;

/// <summary>Watches for a newly published build and lets the user take it.
///
/// The studio is installable, so a copy can keep running for weeks on a version the author
/// has long since replaced. A service worker parks the new build in "waiting" until every tab
/// on the old one closes — which for an installed app may be never — so the update has to be
/// offered explicitly rather than arriving on a reload.
///
/// Deliberately not automatic: an update reloads the page, and the studio holds unsaved
/// in-flight work (a placement session, a queued generation). Interrupting that to install a
/// version the user did not ask for would be worse than running slightly behind.</summary>
public sealed class AppUpdateService(IJSRuntime js) : IDisposable
{
    private DotNetObjectReference<AppUpdateService>? _self;

    /// <summary>True once a new build is installed and waiting to take over. There is no way to
    /// wave it away: the offer is one small pill in the title bar, and a dismissed update is
    /// indistinguishable from one that was never found — which is the failure this whole flow
    /// already spent two deploys stuck in.</summary>
    public bool UpdateAvailable { get; private set; }

    public event Action? OnChange;

    /// <summary>Register the worker and start watching. Safe to call on an unsupported
    /// browser — it simply never reports an update.</summary>
    public async Task StartAsync()
    {
        if (_self is not null)
        {
            return;
        }
        _self = DotNetObjectReference.Create(this);
        try
        {
            await js.InvokeAsync<bool>("studioInterop.registerServiceWorker", _self);
        }
        catch
        {
            // No service worker: the app still runs, it just cannot offer updates.
        }
    }

    [JSInvokable]
    public void OnUpdateAvailable()
    {
        UpdateAvailable = true;
        OnChange?.Invoke();
    }

    /// <summary>Take the update. The page reloads once the new worker is in control, so
    /// nothing after this call runs.</summary>
    public async Task ApplyAsync() => await js.InvokeVoidAsync("studioInterop.applyUpdate");

    public void Dispose() => _self?.Dispose();
}
