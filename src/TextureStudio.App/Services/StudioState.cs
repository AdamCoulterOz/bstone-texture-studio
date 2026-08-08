using System.Text.Json;
using TextureStudio.Core.Games;
using TextureStudio.Core.Generation;
using TextureStudio.Core.Imaging;
using TextureStudio.Core.Model;

namespace TextureStudio.App.Services;

/// <summary>Read-only engine-derived reference data for a tile — shown in Properties, never
/// written into the user's own metadata fields. The game plugin supplies everything except
/// <see cref="ArtBounds"/>, which is measured from the decoded original.</summary>
public sealed record CanonicalDisplay(
    string Constant, string? EngineName, string? InGameLabel, string? TypeLabel,
    string? FrameLabel, string? ArtBounds);

/// <summary>All app state and workflow operations. Panes subscribe to OnChange.</summary>
public sealed class StudioState(
    ImageCodec codec, GeminiImageClient gemini, OpenAiImageClient openai,
    WorkspaceService workspace, ContentSearchService content, GameCatalog catalog,
    HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly Dictionary<string, RgbaImage> _originals = [];
    private readonly Dictionary<string, RgbaImage> _redraws = [];
    private readonly Dictionary<string, RgbaImage> _derivedDarks = [];
    private readonly Dictionary<string, string> _urlCache = [];
    private readonly HashSet<string> _urlPending = [];
    private byte[]? _sourceBytes;
    private CancellationTokenSource? _autoSaveCts;

    /// <summary>Legacy single style reference — migrated into StyleRefs on load.</summary>
    public const string StyleReferenceFileName = "style-reference.png";

    public WorkspaceService Workspace => workspace;

    public event Action? OnChange;

    /// <summary>The imported game data, or null before an import. Doubles as the "is there
    /// anything to work on" check throughout the app.</summary>
    public IGameArchive? Archive { get; private set; }

    /// <summary>Every installed game plugin, in chooser order.</summary>
    public IReadOnlyList<IGame> Games => catalog.Games;

    /// <summary>The game this workspace targets. Falls back to the first plugin when the
    /// project names one that is no longer installed, so the workspace still opens.</summary>
    public IGame Game => catalog.Get(Project.GameId);

    /// <summary>The workspace's edition: pinned when the user chose one, otherwise detected
    /// from the imported archive.</summary>
    public GameEdition Edition => GameCatalog.ResolveEdition(Game, Project.EditionId, Archive);

    /// <summary>True while the edition is being inferred rather than pinned.</summary>
    public bool EditionIsAuto => Game.Editions.All(e => e.Id != Project.EditionId);

    /// <summary>Switching game reinterprets every tile index, so it is only offered on a
    /// workspace with nothing imported and nothing curated yet.</summary>
    public bool CanChangeGame => Archive is null && Project.Items.Count == 0;

    public void SetGame(string gameId)
    {
        if (Project.GameId == gameId || !CanChangeGame)
        {
            return;
        }
        Project.GameId = gameId;
        Project.EditionId = "";
        SetStatus($"Game set to {Game.Name}. Import {Game.ImportHint} to begin.", "Workspace");
    }

    /// <summary>Pin an edition, or pass null/empty to go back to detecting it. Only the
    /// engine reference table and the pack directory are edition-dependent — the decoded
    /// art is not, so nothing has to be re-read.</summary>
    public void SetEdition(string? editionId)
    {
        Project.EditionId = Game.Editions.Any(e => e.Id == editionId) ? editionId! : "";
        SetStatus($"Edition: {Edition.Name}{(EditionIsAuto ? " (detected)" : "")}.", "Workspace");
    }

    // ---- Packing ----

    /// <summary>Packing needs somewhere to write and something to write — the workspace
    /// folder and at least one applied redraw.</summary>
    public bool CanPack => workspace.IsOpen && !workspace.WritesBlocked && _redraws.Count > 0;

    /// <summary>Where a pack is written, relative to the workspace.</summary>
    public const string PackFolderName = "pack";

    /// <summary>Build the game's pack into <c>&lt;workspace&gt;/pack</c>. The game decides what
    /// the pack contains and where each file goes; this only reads the art, applies the
    /// transform each entry asks for, and writes the bytes.
    ///
    /// Existing files are overwritten in place, but stale ones are not removed — the folder is
    /// additive, so deleting it first is the way to get a clean pack.</summary>
    public async Task PackAsync()
    {
        if (!CanPack)
        {
            Fail(workspace.IsOpen
                ? "Nothing to pack yet — apply some redraws first."
                : "Open a workspace first — the pack is written into it.");
            return;
        }
        var plan = Game.PlanPack(Project, Edition, _redraws.Keys.ToList());
        if (plan.Entries.Count == 0)
        {
            Fail($"Nothing to pack for {Game.Name} — apply some redraws first.");
            return;
        }
        Busy = true;
        SetStatus($"Packing {plan.Entries.Count} textures…", "Progress");
        var written = 0;
        try
        {
            foreach (var entry in plan.Entries)
            {
                if (!_redraws.TryGetValue(entry.SourceTileKey, out var art))
                {
                    continue; // planned from a redraw that has since been dropped
                }
                if (entry.Transform == PackTransform.Darken)
                {
                    art = DarkGenerator.Apply(art, Project.DarkParams);
                }
                // Each encode is a browser round trip, which yields on its own — so the UI
                // stays responsive without an explicit delay; only the status is throttled.
                await workspace.WriteAsync(
                    $"{PackFolderName}/{entry.Path}", await codec.EncodePngAsync(art));
                if (++written % 25 == 0)
                {
                    SetStatus($"Packing… {written}/{plan.Entries.Count}", "Progress");
                }
            }
            var skipped = plan.SkippedTileKeys.Count;
            SetStatus($"Packed {written} textures into '{workspace.Name}/{PackFolderName}' " +
                      $"({plan.TransformedCount} dark variants synthesized" +
                      $"{(skipped > 0 ? $", {skipped} tiles had no art" : "")}). " +
                      $"Point {Game.InstallGuide.PortName} at that folder.");
        }
        catch (Exception ex)
        {
            await workspace.ProbeWriteAsync();
            SetStatus($"⚠ Packing stopped after {written} textures: {FirstLine(ex.Message)}");
        }
        finally
        {
            Busy = false;
            Notify();
        }
    }

    // ---- Locating installed game content ----

    /// <summary>The read-only folder granted for the locator to search.</summary>
    public ContentSearchService ContentSearch => content;

    /// <summary>Copies of the game found by the last search; null before one has run.</summary>
    public GameSearchResult? FoundSources { get; private set; }

    public bool Searching { get; private set; }

    /// <summary>Grant a folder to search, then scan it. Needs a user gesture for the picker.</summary>
    public async Task PickAndSearchAsync()
    {
        if (await content.PickAsync())
        {
            await SearchForGameDataAsync();
        }
    }

    /// <summary>Re-scan the granted folder — silently on load, or on the user's request.</summary>
    public async Task SearchForGameDataAsync()
    {
        if (Game.Locator is not { } locator || !content.IsOpen || Searching)
        {
            return;
        }
        Searching = true;
        SetStatus($"Searching '{content.Name}' for {Game.Name} game data…", "Progress");
        try
        {
            FoundSources = await locator.FindAsync(content);
            var found = FoundSources.Sources.Count;
            SetStatus(found == 0
                ? $"No {Game.Name} game data under '{content.Name}'" +
                  (FoundSources.Exhausted
                      ? " — the search hit its limit, so try a folder closer to the game."
                      : $" ({FoundSources.DirectoriesVisited} folders searched).")
                : $"Found {found} cop{(found == 1 ? "y" : "ies")} of {Game.Name} under " +
                  $"'{content.Name}'.");
        }
        catch (Exception ex)
        {
            FoundSources = null;
            SetStatus($"⚠ Search of '{content.Name}' failed: {FirstLine(ex.Message)}");
        }
        finally
        {
            Searching = false;
            Notify();
        }
    }

    /// <summary>Stop searching that folder and forget the grant.</summary>
    public async Task ForgetContentRootAsync()
    {
        var name = content.Name;
        await content.ForgetAsync();
        FoundSources = null;
        SetStatus($"Stopped searching '{name}'.", "Workspace");
    }

    /// <summary>Import a located copy: read its art container and run the normal import.
    /// The edition is left to detection, which reads the same file name the locator did — a
    /// stale manual pin that disagrees with the file is dropped rather than mislabelling the
    /// import.</summary>
    public async Task ImportFoundSourceAsync(GameSource source)
    {
        if (!workspace.IsOpen)
        {
            Fail("Open a workspace first — everything you do is stored there.");
            return;
        }
        try
        {
            Busy = true;
            SetStatus($"Reading {source.AssetFileName} from {source.DisplayPath}…", "Progress");
            var bytes = await content.ReadAsync(source.AssetPath);
            if (bytes is null or { Length: 0 })
            {
                Fail($"Could not read {source.AssetPath} — re-grant the search folder.");
                return;
            }
            if (!EditionIsAuto && Project.EditionId != source.Edition.Id)
            {
                Project.EditionId = "";
                SetStatus($"Edition un-pinned — {source.AssetFileName} is " +
                          $"{source.Edition.Name}.", "Workspace");
            }
            LoadGameData(bytes, source.AssetFileName);
        }
        catch (Exception ex)
        {
            Fail($"{Game.Name} game data load failed: {FirstLine(ex.Message)}");
        }
        finally
        {
            Busy = false;
            Notify();
        }
    }

    public Project Project { get; private set; } = new();
    /// <summary>Every tile the archive holds, in the game's own order. One list: what a
    /// tile *is* belongs to the game, and the app only ever needs its kind.</summary>
    public List<string> TileKeys { get; } = [];

    /// <summary>Position of each tile in the game's enumeration — "engine order" for
    /// sorting an item's frames, without the app knowing what an id means.</summary>
    private readonly Dictionary<string, int> _tileOrder = [];

    /// <summary>Workspace file name → tile id, so redraws on disk can be matched back.</summary>
    private readonly Dictionary<string, string> _fileToTile = [];

    /// <summary>Sort key for a tile; unknown tiles sort last rather than throwing.</summary>
    public int TileOrder(string tileId) =>
        _tileOrder.TryGetValue(tileId, out var order) ? order : int.MaxValue;
    public List<string> Selection { get; } = [];
    public TileGroup? ActiveGroup { get; set; }
    public bool ShowRedraw { get; set; }
    public string CategoryFilter { get; set; } = "";
    /// <summary>Proxy to the persisted project setting (kept for existing call sites).</summary>
    public int SliceTargetPx
    {
        get => Project.Generation.SliceTargetPx;
        set => Project.Generation.SliceTargetPx = value;
    }

    public const string ApiKeyFileName = "gemini-api-key.txt";

    private string _apiKey = "";

    /// <summary>Persisted to the workspace in its own file (never in project.json, so the
    /// project stays shareable).</summary>
    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (_apiKey == value)
            {
                return;
            }
            _apiKey = value;
            _ = PersistApiKeyAsync();
        }
    }

    private async Task PersistApiKeyAsync()
    {
        if (!workspace.IsOpen || workspace.WritesBlocked)
        {
            return;
        }
        try
        {
            await workspace.WriteAsync(ApiKeyFileName, System.Text.Encoding.UTF8.GetBytes(_apiKey));
        }
        catch (Exception ex)
        {
            SetStatus($"⚠ Could not persist API key: {FirstLine(ex.Message)}");
        }
    }

    public const string OpenAiKeyFileName = "openai-api-key.txt";

    private string _openAiApiKey = "";

    /// <summary>OpenAI key, persisted like the Gemini key: its own workspace file,
    /// never in project.json.</summary>
    public string OpenAiApiKey
    {
        get => _openAiApiKey;
        set
        {
            if (_openAiApiKey == value)
            {
                return;
            }
            _openAiApiKey = value;
            _ = PersistOpenAiKeyAsync();
        }
    }

    private async Task PersistOpenAiKeyAsync()
    {
        if (!workspace.IsOpen || workspace.WritesBlocked)
        {
            return;
        }
        try
        {
            await workspace.WriteAsync(OpenAiKeyFileName, System.Text.Encoding.UTF8.GetBytes(_openAiApiKey));
        }
        catch (Exception ex)
        {
            SetStatus($"⚠ Could not persist OpenAI API key: {FirstLine(ex.Message)}");
        }
    }

    /// <summary>True when the configured model runs on the OpenAI provider.</summary>
    public bool UsesOpenAi => OpenAiImageClient.IsOpenAiModel(Project.Generation.ModelId);

    /// <summary>The configured provider's key (for gating generate/revise).</summary>
    public string ActiveProviderKey => UsesOpenAi ? OpenAiApiKey : ApiKey;
    public string Status { get; private set; } =
        "Open a workspace folder, choose its game, then import the game's data. " +
        "Without a workspace, work is in-memory only.";
    public bool Busy { get; private set; }

    // Background generation/import jobs. Each opened job lives in its own main-area tab,
    // fully decoupled from the group grid (the always-present first tab); ActiveJob is the
    // job tab currently fronted, null = the grid tab.
    public List<GenerationJob> Jobs { get; } = [];
    public List<GenerationJob> OpenTabs { get; } = [];
    public GenerationJob? ActiveJob { get; set; }

    public void SelectGridTab()
    {
        ActiveJob = null;
        Notify();
    }

    public void SelectJob(GenerationJob job) => _ = SelectJobAsync(job);

    public async Task SelectJobAsync(GenerationJob job)
    {
        if (!OpenTabs.Contains(job))
        {
            OpenTabs.Add(job);
        }
        ActiveJob = job;
        job.NotificationDismissed = true;
        Notify();
        // Jobs restored from history reload their archived sheet on first open.
        if (job.RawPng is null && job.SheetFile is not null &&
            job.State == JobState.AwaitingReview)
        {
            if (await workspace.ReadAsync(job.SheetFile) is { Length: > 0 } png)
            {
                await AttachSheetPngAsync(job, png);
                Notify();
            }
            else
            {
                SetStatus($"⚠ Archived sheet not found: {job.SheetFile}");
            }
        }
        if (job.State == JobState.Placing && job.Placements is null &&
            job.PendingPlacements is { Count: > 0 })
        {
            await HydratePlacementsAsync(job);
        }
    }

    /// <summary>Rebuild the Adjust &amp; Apply session from its persisted snapshot — keyed
    /// PNGs from jobs/&lt;id&gt;/ plus saved transforms; no re-slicing.</summary>
    private async Task HydratePlacementsAsync(GenerationJob job)
    {
        var records = job.PendingPlacements!;
        var restored = new List<SlicePlacement>();
        var done = 0;
        foreach (var record in records)
        {
            SetStatus($"Restoring placements… {++done}/{records.Count}");
            Notify();
            var png = await workspace.ReadAsync(record.File);
            if (png is null)
            {
                continue;
            }
            var keyed = await codec.DecodePngAsync(png);
            var placement = new SlicePlacement
            {
                TileKey = record.TileKey,
                Keyed = keyed,
                IsSprite = record.IsSprite,
                KeyMode = record.KeyMode,
                UsedFallback = record.UsedFallback,
                WrapError = record.WrapError,
                Auto = new SpritePlacement(record.AutoScale, record.AutoOffsetX,
                    record.AutoOffsetY, record.AutoRotation),
                AnchorX = record.AnchorX,
                AnchorY = record.AnchorY,
                BoundsX = record.BoundsX,
                BoundsY = record.BoundsY,
                BoundsW = record.BoundsW,
                BoundsH = record.BoundsH,
                Included = record.Included,
                Scale = record.Scale,
                OffsetX = record.OffsetX,
                OffsetY = record.OffsetY,
                Rotation = record.Rotation,
            };
            placement.PreviewUrl = await codec.ToDataUrlAsync(keyed);
            restored.Add(placement);
            await Task.Delay(1);
        }
        job.Placements = restored;
        job.PendingPlacements = null;
        SetStatus($"Placement session restored — {restored.Count} tiles, no re-slice needed.");
        Notify();
    }

    /// <summary>Placement edits only need a debounced save (transforms snapshot into job
    /// history) — no global re-render.</summary>
    public void SchedulePlacementSave() => ScheduleAutoSave();

    /// <summary>Write each keyed tile PNG under jobs/&lt;id&gt;/ so the Adjust &amp; Apply
    /// session can be rebuilt after a refresh without re-slicing.</summary>
    private async Task PersistPlacementArtAsync(GenerationJob job)
    {
        if (!workspace.IsOpen || workspace.WritesBlocked || job.Placements is null)
        {
            return;
        }
        try
        {
            // Let the placement grid render and become interactive first — encoding N
            // full-size PNGs on the single WASM thread right away made the whole step
            // feel frozen. The art never changes during tuning, so late is fine.
            await Task.Delay(3000);
            foreach (var placement in job.Placements)
            {
                if (job.State != JobState.Placing)
                {
                    return; // applied or discarded while we waited
                }
                await workspace.WriteAsync(PlacementFile(job, placement.TileKey),
                    await codec.EncodePngAsync(placement.Keyed));
                await Task.Delay(30);
            }
            ScheduleAutoSave(); // transform snapshot lands alongside the art
        }
        catch (Exception ex)
        {
            SetStatus($"⚠ Could not save placement session: {FirstLine(ex.Message)}");
        }
    }

    /// <summary>Next per-group job ordinal (max existing + 1, counting history too).</summary>
    private int NextJobNumber(TileGroup group) =>
        Jobs.Where(j => j.Group == group).Select(j => j.Number)
            .Concat(Project.JobHistory.Where(r => r.GroupName == group.Name).Select(r => r.Number))
            .DefaultIfEmpty(0).Max() + 1;

    /// <summary>Mirror the live jobs rail into the project (newest first, capped at 50) —
    /// runs on every save, so dismissed jobs drop out of history too.</summary>
    private void SyncJobHistory()
    {
        Project.JobHistory = Jobs.Take(50).Select(j => new JobRecord
        {
            Id = j.Id,
            Kind = j.Kind,
            GroupName = j.Group.Name,
            Created = j.Created,
            State = j.State.ToString(),
            Error = j.Error,
            VariantLabel = j.VariantLabel,
            SheetFile = j.SheetFile,
            Manifest = j.Manifest,
            Prompts = j.Prompts.ToList(),
            Number = j.Number,
            Placements = j.State == JobState.Placing && j.Placements is not null
                ? j.Placements.Select(p => new PlacementRecord
                {
                    TileKey = p.TileKey,
                    File = PlacementFile(j, p.TileKey),
                    IsSprite = p.IsSprite,
                    KeyMode = p.KeyMode,
                    UsedFallback = p.UsedFallback,
                    WrapError = p.WrapError,
                    Included = p.Included,
                    Scale = p.Scale,
                    OffsetX = p.OffsetX,
                    OffsetY = p.OffsetY,
                    Rotation = p.Rotation,
                    AnchorX = p.AnchorX,
                    AnchorY = p.AnchorY,
                    BoundsX = p.BoundsX,
                    BoundsY = p.BoundsY,
                    BoundsW = p.BoundsW,
                    BoundsH = p.BoundsH,
                    AutoScale = p.Auto.Scale,
                    AutoOffsetX = p.Auto.OffsetX,
                    AutoOffsetY = p.Auto.OffsetY,
                    AutoRotation = p.Auto.Rotation,
                }).ToList()
                : (j.PendingPlacements ?? []),
        }).ToList();
        // What's open right now, so a reload lands back here. User preferences that live
        // on Ui (like the notification filter) must carry over — this rebuild replaces
        // the whole object.
        Project.Ui = new UiState
        {
            ActiveGroupName = ActiveGroup?.Name,
            OpenTabJobIds = OpenTabs.Select(j => j.Id).ToList(),
            ActiveTabJobId = ActiveJob?.Id,
            ItemsCategory = CategoryFilter,
            ShowRedraw = ShowRedraw,
            HiddenNotifications = Project.Ui.HiddenNotifications,
        };
    }

    private static string PlacementFile(GenerationJob job, string tileKey) =>
        $"jobs/{job.Id}/{tileKey.Replace(':', '_')}.png";

    /// <summary>Rebuild the jobs rail from persisted history. In-flight states become
    /// Interrupted; a Placing job whose sheet is archived returns to review.</summary>
    private void RestoreJobHistory()
    {
        Jobs.Clear();
        OpenTabs.Clear();
        ActiveJob = null;
        foreach (var record in Project.JobHistory.Take(50))
        {
            var group = Project.Groups.FirstOrDefault(g => g.Name == record.GroupName);
            if (group is null)
            {
                continue; // the group was deleted since — nothing to reopen against
            }
            if (!Enum.TryParse<JobState>(record.State, out var state))
            {
                state = JobState.Interrupted;
            }
            state = state switch
            {
                JobState.Queued or JobState.Running => JobState.Interrupted,
                // A persisted placement snapshot resumes Adjust & Apply directly; without
                // one, fall back to review (sheet archived) or interrupted.
                JobState.Placing when record.Placements.Count > 0 => JobState.Placing,
                JobState.Placing => record.SheetFile is null
                    ? JobState.Interrupted
                    : JobState.AwaitingReview,
                _ => state,
            };
            var job = new GenerationJob
            {
                Id = record.Id,
                Kind = record.Kind,
                Group = group,
                Manifest = record.Manifest ?? group.LastExport ?? PlanGroup(group).Manifest,
                Created = record.Created,
                State = state,
                VariantLabel = record.VariantLabel,
                Error = record.Error,
                SheetFile = record.SheetFile,
                NotificationDismissed = true, // history never re-toasts
                Number = record.Number,
            };
            if (state == JobState.Placing && record.Placements.Count > 0)
            {
                job.PendingPlacements = record.Placements;
            }
            job.Prompts.AddRange(record.Prompts);
            Jobs.Add(job);
        }
        RestoreUiState();
    }

    /// <summary>Reopen what was open last session: group, job tabs, items filters.</summary>
    private void RestoreUiState()
    {
        var ui = Project.Ui;
        if (ui.ActiveGroupName is { Length: > 0 } groupName &&
            Project.Groups.FirstOrDefault(g => g.Name == groupName) is { } group)
        {
            ActiveGroup = group;
        }
        ShowRedraw = ui.ShowRedraw;
        CategoryFilter = ui.ItemsCategory ?? "";
        OpenTabs.Clear();
        foreach (var id in ui.OpenTabJobIds)
        {
            if (Jobs.FirstOrDefault(j => j.Id == id) is { } tabJob)
            {
                OpenTabs.Add(tabJob);
            }
        }
        ActiveJob = ui.ActiveTabJobId is { Length: > 0 } activeId
            ? OpenTabs.FirstOrDefault(j => j.Id == activeId)
            : null;
        if (ActiveJob is { } active)
        {
            _ = SelectJobAsync(active); // lazy-loads its sheet / placement snapshot
        }
    }

    /// <summary>Cancel a queued or running job. During the arming countdown this stops the
    /// run before the API call ever fires (no credits burned); mid-flight it aborts the
    /// HTTP call.</summary>
    public void CancelJob(GenerationJob job)
    {
        if (job.State is not (JobState.Queued or JobState.Running))
        {
            return;
        }
        job.CancelRequested = true;
        job.Cts.Cancel();
        Notify();
    }

    /// <summary>The 5s arming window: the toast counts down and cancel is free. Throws
    /// OperationCanceledException if cancelled before it elapses.</summary>
    private async Task ArmCountdownAsync(GenerationJob job)
    {
        for (var second = 5; second > 0; second--)
        {
            job.Countdown = second;
            Notify();
            await Task.Delay(1000, job.Cts.Token);
        }
        job.Countdown = 0;
        job.Cts.Token.ThrowIfCancellationRequested();
    }

    /// <summary>Cancelled toasts read "cancelled" for 2s, then close themselves.</summary>
    private async Task AutoCloseCancelToastAsync(GenerationJob job)
    {
        await Task.Delay(2000);
        job.NotificationDismissed = true;
        Notify();
    }

    public void CloseJobTab(GenerationJob job)
    {
        OpenTabs.Remove(job);
        if (ActiveJob == job)
        {
            ActiveJob = null;
        }
        Notify();
    }

    public void DismissJob(GenerationJob job)
    {
        Jobs.Remove(job);
        OpenTabs.Remove(job);
        job.Sheet = null;
        job.RawPng = null;
        job.PreviewUrl = null;
        if (ActiveJob == job)
        {
            ActiveJob = null;
        }
        Notify();
    }

    public void Notify()
    {
        OnChange?.Invoke();
        ScheduleAutoSave();
    }

    public void Fail(string message) => SetStatus(message);

    /// <summary>Current notification banner (top of the main pane); persists until
    /// dismissed or replaced by the next message.</summary>
    public string? Banner { get; private set; }

    public void DismissBanner()
    {
        Banner = null;
        Notify();
    }

    /// <summary>Notification kinds the banner filter can hide (Application drawer).</summary>
    public static readonly string[] NotificationKinds =
        ["Errors", "Jobs", "Progress", "Workspace", "Info"];

    public void ToggleNotificationKind(string kind)
    {
        if (!Project.Ui.HiddenNotifications.Remove(kind))
        {
            Project.Ui.HiddenNotifications.Add(kind);
        }
        Notify();
    }

    /// <summary>Best-effort message classification so the banner filter works without
    /// tagging every call site.</summary>
    private static string ClassifyStatus(string message)
    {
        if (message.StartsWith('⚠') || message.Contains("failed") || message.Contains("Could not"))
        {
            return "Errors";
        }
        if (message.Contains("Slicing…") || message.Contains("Restoring placements") ||
            message.Contains("Composing"))
        {
            return "Progress";
        }
        if (message.Contains("workspace") || message.Contains("Workspace") ||
            message.Contains("game data"))
        {
            return "Workspace";
        }
        if (message.Contains("generation") || message.Contains("Generation") ||
            message.Contains("revision") || message.Contains("Revision") ||
            message.Contains("ready for review") || message.Contains("cancelled") ||
            message.Contains("Sliced") || message.Contains("Position ") ||
            message.Contains("Received "))
        {
            return "Jobs";
        }
        return "Info";
    }

    private void SetStatus(string message, string? kind = null)
    {
        Status = message;
        if (!Project.Ui.HiddenNotifications.Contains(kind ?? ClassifyStatus(message)))
        {
            Banner = message;
        }
        Notify();
    }

    /// <summary>Import the workspace game's asset container: enumerate its non-empty tiles,
    /// then decode and mirror them into the workspace in the background.</summary>
    public void LoadGameData(byte[] bytes, string fileName)
    {
        Archive = Game.OpenArchive(bytes, fileName);
        _sourceBytes = bytes;
        Project.SourceFileName = fileName;
        TileKeys.Clear();
        _originals.Clear();
        _urlCache.Clear();
        _artBoxCache.Clear();
        _tileOrder.Clear();
        _fileToTile.Clear();
        foreach (var tile in Archive.Tiles)
        {
            _tileOrder[tile.Id] = TileKeys.Count;
            _fileToTile[Game.WorkspaceFileName(tile.Id)] = tile.Id;
            TileKeys.Add(tile.Id);
        }
        SetStatus($"{Game.Name} — {Edition.Name} game data from {fileName}: " +
                  $"{TileKeys.Count} tiles.");
        _ = DecodeAllAndPersistAsync(fileName);
    }

    /// <summary>Where a tile's art lives inside the workspace — the game decides, so the
    /// names survive an id-scheme change.</summary>
    public string FileNameFor(string key) => Game.WorkspaceFileName(key);

    /// <summary>Decode every tile up front; when a workspace is open, persist the decoded
    /// originals (and the source archive itself) so the folder is a self-contained mirror.</summary>
    private async Task DecodeAllAndPersistAsync(string fileName)
    {
        var allKeys = TileKeys.ToList();
        var persist = workspace.IsOpen && !workspace.WritesBlocked;
        if (persist)
        {
            try
            {
                await workspace.WriteAsync($"source/{fileName}", _sourceBytes!);
                // Skip re-writing originals when the workspace already holds this set.
                var existing = await workspace.ListAsync("originals");
                persist = existing.Length != allKeys.Count;
            }
            catch (Exception ex)
            {
                persist = false;
                await workspace.ProbeWriteAsync();
                SetStatus($"⚠ Workspace write failed ({FirstLine(ex.Message)}) — decoding " +
                          "in memory only. Close and re-open the workspace.");
            }
        }
        var done = 0;
        foreach (var key in allKeys)
        {
            RgbaImage image;
            try
            {
                image = GetOriginal(key);
            }
            catch (Exception ex)
            {
                SetStatus($"Decode failed for {key}: {ex.Message}");
                continue;
            }
            if (persist)
            {
                try
                {
                    await workspace.WriteAsync($"originals/{FileNameFor(key)}", await codec.EncodePngAsync(image));
                }
                catch (Exception ex)
                {
                    persist = false;
                    await workspace.ProbeWriteAsync();
                    SetStatus($"⚠ Workspace write failed at {key} ({FirstLine(ex.Message)}) — " +
                              "continuing decode in memory only.");
                }
            }
            if (++done % 50 == 0)
            {
                SetStatus($"Decoding tiles… {done}/{allKeys.Count}" + (persist ? " (writing workspace)" : ""));
            }
        }
        SetStatus($"{Project.SourceFileName}: {TileKeys.Count} tiles decoded" +
                  (workspace.IsOpen ? $" — workspace '{workspace.Name}' up to date." : "."));
        await EnsureItemsMigratedAsync();
    }

    public RgbaImage GetOriginal(string key)
    {
        if (_originals.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var image = Archive!.Decode(key);
        _originals[key] = image;
        return image;
    }

    public bool HasRedraw(string key) => _redraws.ContainsKey(key);

    public RgbaImage? GetRedraw(string key) => _redraws.GetValueOrDefault(key);

    public void SetRedraw(string key, RgbaImage image, bool persist = true)
    {
        _redraws[key] = image;
        _derivedDarks.Clear();
        InvalidateUrl(key, redraw: true);
        // Dark tiles sourcing this art render a derived image; refresh them too.
        foreach (var (otherKey, meta) in Project.Meta)
        {
            if (meta.LightSourceKey == key ||
                (meta.Role == PairRole.DerivedDark && meta.LightSourceKey is null &&
                 DefaultLightSource(otherKey) == key))
            {
                InvalidateUrl(otherKey, redraw: true);
            }
        }
        if (persist && workspace.IsOpen)
        {
            _ = PersistRedrawAsync(key, image);
        }
    }

    private async Task PersistRedrawAsync(string key, RgbaImage image)
    {
        try
        {
            await workspace.WriteAsync($"redraws/{FileNameFor(key)}", await codec.EncodePngAsync(image));
        }
        catch (Exception ex)
        {
            SetStatus($"Workspace write failed for {key}: {ex.Message}");
        }
    }

    public TileMeta GetMeta(string key)
    {
        if (!Project.Meta.TryGetValue(key, out var meta))
        {
            meta = new TileMeta();
            Project.Meta[key] = meta;
        }
        return meta;
    }

    /// <summary>Display image honoring the A/B toggle.
    /// DerivedDark: darkened copy of its light source's redraw.
    /// AlternateDark: its own redraw (or the specified light source's) IS light-form art —
    /// the display is always the darkened version, never the raw redraw.</summary>
    public RgbaImage GetDisplay(string key)
    {
        if (!ShowRedraw)
        {
            return GetOriginal(key);
        }
        var meta = Project.Meta.GetValueOrDefault(key);
        if (meta is { Role: PairRole.AlternateDark })
        {
            // All redraws are drawn light; an alternate dark is simply its own redraw darkened.
            return _redraws.TryGetValue(key, out var ownArt)
                ? CachedDark(key, ownArt)
                : GetOriginal(key);
        }
        if (_redraws.TryGetValue(key, out var redraw))
        {
            return redraw;
        }
        if (meta is { Role: PairRole.DerivedDark })
        {
            var sourceKey = meta.LightSourceKey ?? DefaultLightSource(key);
            if (sourceKey is not null && _redraws.TryGetValue(sourceKey, out var lightRedraw))
            {
                return CachedDark(key, lightRedraw);
            }
        }
        return GetOriginal(key);
    }

    private RgbaImage CachedDark(string key, RgbaImage lightArt)
    {
        if (!_derivedDarks.TryGetValue(key, out var dark))
        {
            dark = DarkGenerator.Apply(lightArt, Project.DarkParams);
            _derivedDarks[key] = dark;
        }
        return dark;
    }

    // ---- Engine reference metadata (read-only; the game plugin owns it) ----

    private Task? _metadataTask;

    /// <summary>Fetch the active game's reference table once. The table is a static asset so
    /// Core stays HTTP-free — see <see cref="IGameMetadata"/>.</summary>
    private Task EnsureMetadataAsync() => _metadataTask ??= LoadMetadataAsync();

    private async Task LoadMetadataAsync()
    {
        // Games are singletons, so a table already fetched for this game stays good across
        // workspace switches; only a switch to a different game needs a fetch.
        if (Game.Metadata is not { IsLoaded: false } metadata)
        {
            return;
        }
        try
        {
            metadata.Load(await http.GetByteArrayAsync(metadata.AssetPath));
        }
        catch (Exception ex)
        {
            metadata.Load([]); // degrade to no reference data rather than retry on every tile
            SetStatus($"⚠ {Game.Name} reference data unavailable ({FirstLine(ex.Message)}) — " +
                      "engine names and frame labels will be blank.", "Errors");
        }
        Notify();
    }

    /// <summary>Engine reference data for a tile; null when the game ships none, the tile is
    /// unknown, or the table is still loading (a load is kicked off on first call).</summary>
    public CanonicalDisplay? GetCanonical(string key)
    {
        if (Archive is null || Game.Metadata is not { } metadata)
        {
            return null;
        }
        if (!metadata.IsLoaded)
        {
            _ = EnsureMetadataAsync();
            return null;
        }
        return metadata.Lookup(Edition, key) is { } tile
            ? new CanonicalDisplay(tile.Constant, tile.EngineName, tile.InGameLabel,
                tile.TypeLabel, tile.FrameLabel, ArtBounds(key))
            : null;
    }

    private readonly Dictionary<string, (int X, int Y, int W, int H)?> _artBoxCache = [];

    /// <summary>Opaque-art bounding box of the original tile, in its own pixel space.</summary>
    private (int X, int Y, int W, int H)? GetArtBox(string key)
    {
        if (_artBoxCache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var box = SpriteFootprint.OpaqueBounds(GetOriginal(key), alphaThreshold: 0);
        _artBoxCache[key] = box;
        return box;
    }

    /// <summary>Opaque-art bounding box of the original sprite, e.g. "34×46 @ (15,18)".</summary>
    private string? ArtBounds(string key) =>
        GetArtBox(key) is { } box ? $"{box.W}×{box.H} @ ({box.X},{box.Y})" : "empty";

    // ---- Item layer (Category → Item → Frame) ----

    public List<TileItem> SelectedItems { get; } = [];

    public TileItem? SelectedItem => SelectedItems.Count == 1 ? SelectedItems[0] : null;

    /// <summary>Frame edit mode (toggled by [Edit] in the Properties header): shows the ×
    /// remove control on frame rows. Resets whenever the selection changes.</summary>
    public bool FrameEditMode { get; set; }

    /// <summary>Plain click: select (or deselect when already the sole selection).
    /// Additive click (cmd/ctrl): toggle membership for multi-item operations.</summary>
    public void SelectItem(TileItem? item, bool additive = false)
    {
        if (item is null)
        {
            SelectedItems.Clear();
        }
        else if (additive)
        {
            if (!SelectedItems.Remove(item))
            {
                SelectedItems.Add(item);
            }
        }
        else if (SelectedItems.Count == 1 && SelectedItems[0] == item)
        {
            SelectedItems.Clear();
        }
        else
        {
            SelectedItems.Clear();
            SelectedItems.Add(item);
        }
        Selection.Clear();
        Selection.AddRange(SelectedItems.SelectMany(i => i.TileKeys));
        FrameEditMode = false;
        Notify();
    }

    /// <summary>Add a run of items (shift-click range or select-all) to the selection,
    /// preserving order and skipping ones already selected.</summary>
    public void SelectItemRange(IEnumerable<TileItem> items)
    {
        foreach (var item in items)
        {
            if (!SelectedItems.Contains(item))
            {
                SelectedItems.Add(item);
            }
        }
        Selection.Clear();
        Selection.AddRange(SelectedItems.SelectMany(i => i.TileKeys));
        FrameEditMode = false;
        Notify();
    }

    /// <summary>Rename all selected items; items ending up with the same name (and kind)
    /// combine into one item whose frames re-sort into engine order. Also merges a single
    /// renamed item into an existing same-category same-name item.</summary>
    public void RenameSelectedItems(string name)
    {
        name = name.Trim();
        if (SelectedItems.Count == 0)
        {
            return;
        }
        foreach (var item in SelectedItems)
        {
            item.Name = name;
        }
        TileItem? merged = null;
        if (name.Length > 0)
        {
            // Any other item of the same kind with this name joins the merge — category is
            // no barrier (the merged item keeps the first/canonical item's category).
            var sameName = Project.Items
                .Where(i => i.Name == name && ItemKind(i) == ItemKind(SelectedItems[0]))
                .ToList();
            if (sameName.Count > 1)
            {
                merged = MergeItems(sameName);
            }
        }
        if (merged is not null)
        {
            SelectedItems.Clear();
            SelectedItems.Add(merged);
            Selection.Clear();
            Selection.AddRange(merged.TileKeys);
            SetStatus($"Merged into '{merged.Name}' — {merged.TileKeys.Count} frames " +
                      $"[{merged.Category}].");
            return; // SetStatus already notified
        }
        Notify();
    }

    public void SetSelectedItemsCategory(string category)
    {
        foreach (var item in SelectedItems)
        {
            item.Category = category;
        }
        Notify();
    }

    /// <summary>An item's kind is its first frame's — frames of different kinds never share
    /// an item, because the pipeline treats them differently end to end.</summary>
    private TileKind ItemKind(TileItem item) =>
        item.TileKeys.Count > 0 ? Game.KindOf(item.TileKeys[0]) : TileKind.Cutout;

    private TileItem MergeItems(List<TileItem> items)
    {
        var target = items[0];
        foreach (var source in items.Skip(1))
        {
            if (ItemKind(source) != ItemKind(target))
            {
                continue; // items of different kinds never merge
            }
            target.TileKeys.AddRange(source.TileKeys);
            Project.Items.Remove(source);
        }
        target.TileKeys = target.TileKeys
            .Distinct()
            .OrderBy(TileOrder)
            .ToList();
        target.IsAnimation = target.IsAnimation || target.TileKeys.Count > 1;
        return target;
    }

    /// <summary>Move a frame to a new position within its item (drag-handle reorder).</summary>
    public void ReorderFrame(TileItem item, string key, int targetIndex)
    {
        var current = item.TileKeys.IndexOf(key);
        if (current < 0)
        {
            return;
        }
        item.TileKeys.RemoveAt(current);
        if (current < targetIndex)
        {
            targetIndex--;
        }
        item.TileKeys.Insert(Math.Clamp(targetIndex, 0, item.TileKeys.Count), key);
        Notify();
    }

    /// <summary>Detach a frame from an item; it becomes its own unnamed item (same category)
    /// right after, so no tile is ever orphaned. Removing the last frame removes the item.</summary>
    public void RemoveFrame(TileItem item, string key)
    {
        if (!item.TileKeys.Remove(key))
        {
            return;
        }
        var standalone = new TileItem
        {
            Name = "",
            Category = item.Category,
            TileKeys = [key],
        };
        var index = Project.Items.IndexOf(item);
        Project.Items.Insert(index < 0 ? Project.Items.Count : index + 1, standalone);
        if (item.TileKeys.Count == 0)
        {
            Project.Items.Remove(item);
            SelectedItems.Remove(item);
        }
        Selection.Remove(key);
        Notify();
    }

    // ---- UI display-size preferences (persisted with the layout) ----

    /// <summary>Items-grid zoom expressed as COLUMN COUNT — cells stretch to fill the
    /// panel width, so zoom detents by columns at the current width.</summary>
    public int ItemColumns { get; set; } = 5;
    public int FrameThumbSize { get; set; } = 40;

    public void BumpItemTileSize(int direction) =>
        ItemColumns = Math.Clamp(ItemColumns - direction, 2, 12); // zoom in = fewer columns

    public void BumpFrameThumbSize(int direction) =>
        FrameThumbSize = Math.Clamp(FrameThumbSize + direction * 8, 24, 96);

    /// <summary>Build the item layer once from legacy per-tile metadata (named tiles group by
    /// Category+Name, unnamed sprites by engine actor family). No-op when items exist.</summary>
    public async Task EnsureItemsMigratedAsync()
    {
        if (Archive is null)
        {
            return;
        }
        if (Project.Items.Count == 0)
        {
            await EnsureMetadataAsync();
            var allKeys = TileKeys.ToList();
            Project.Items = ItemMigration.Build(allKeys, Project.Meta, ActorFamilyFor);
            SetStatus($"Item layer built: {Project.Items.Count} items from {allKeys.Count} tiles.");
        }
        ReconcileDuplicateItems();
        MigrateTileVersions();
        MigrateStylePrompt();
        MigrateSeamlessRuns();
    }

    /// <summary>Convert the legacy whole-group seamless flag into per-run spans: each full
    /// platter row (the old fixed column width) becomes one butted run. Idempotent.</summary>
    private void MigrateSeamlessRuns()
    {
        foreach (var group in Project.Groups)
        {
            if (group.Seamless)
            {
                var cols = Math.Max(1, group.Columns);
                for (var i = 0; i < group.TileKeys.Count; i += cols)
                {
                    var run = group.TileKeys.Skip(i).Take(cols).ToList();
                    if (run.Count >= 2)
                    {
                        group.SeamlessRuns.Add(run);
                    }
                }
                group.Seamless = false;
            }
            ValidateRuns(group);
        }
    }

    /// <summary>Build the per-frame version index from group revision records (the PNGs
    /// already exist per-tile under revisions/). Idempotent — safe on every load.</summary>
    private void MigrateTileVersions()
    {
        var added = 0;
        foreach (var group in Project.Groups)
        {
            foreach (var revision in group.Revisions)
            {
                foreach (var key in revision.TileKeys)
                {
                    var versions = Project.TileVersions.TryGetValue(key, out var list)
                        ? list
                        : Project.TileVersions[key] = [];
                    if (versions.Any(v => v.Id == revision.Id))
                    {
                        continue;
                    }
                    versions.Add(new TileVersionInfo
                    {
                        Id = revision.Id,
                        Kind = revision.Kind,
                        GroupName = group.Name,
                        Created = revision.Created,
                        File = $"revisions/{SafeName(group.Name)}/{revision.Id}/{FileNameFor(key)}",
                    });
                    added++;
                    var meta = GetMeta(key);
                    if (meta.ActiveVersionId is null && group.ActiveRevisionId == revision.Id)
                    {
                        meta.ActiveVersionId = revision.Id;
                    }
                }
            }
        }
        foreach (var versions in Project.TileVersions.Values)
        {
            versions.Sort((a, b) => a.Created.CompareTo(b.Created));
        }
        if (added > 0)
        {
            SetStatus($"Version index: {added} per-frame versions recorded from revision history.");
        }
    }

    public List<TileVersionInfo> VersionsFor(string key) =>
        Project.TileVersions.GetValueOrDefault(key) ?? [];

    /// <summary>Make one of a tile's recorded versions its live redraw (persisted).</summary>
    public async Task SwitchTileVersionAsync(string key, TileVersionInfo version)
    {
        if (!workspace.IsOpen)
        {
            SetStatus("Version switching needs the workspace.");
            return;
        }
        var png = await workspace.ReadAsync(version.File);
        if (png is null)
        {
            SetStatus($"Version file missing: {version.File}");
            return;
        }
        SetRedraw(key, await codec.DecodePngAsync(png));
        GetMeta(key).ActiveVersionId = version.Id;
        SetStatus($"{key} → version {version.Id} ({version.Kind}, {version.GroupName}).");
        Notify();
    }

    /// <summary>Merge any items of the same kind sharing one (trimmed) non-empty name —
    /// catches duplicates created before rename-merge existed or by hand-edited files.</summary>
    private void ReconcileDuplicateItems()
    {
        foreach (var item in Project.Items)
        {
            item.Name = item.Name.Trim();
            item.Category = item.Category.Trim();
        }
        var duplicateGroups = Project.Items
            .Where(i => i.Name.Length > 0)
            .GroupBy(i => (Kind: ItemKind(i), i.Name))
            .Where(g => g.Count() > 1)
            .ToList();
        foreach (var group in duplicateGroups)
        {
            MergeItems(group.ToList());
        }
        if (duplicateGroups.Count > 0)
        {
            SetStatus($"Merged {duplicateGroups.Count} duplicate item name(s) " +
                      $"({string.Join(", ", duplicateGroups.Select(g => $"'{g.Key.Name}'"))}).");
        }
    }

    /// <summary>The game's grouping key that collects one actor's frames into a single item;
    /// null means the tile stands alone.</summary>
    private string? ActorFamilyFor(string key) =>
        Archive is null ? null : Game.Metadata?.ActorFamily(Edition, key);

    /// <summary>Grey placeholder for an empty item name: the engine's own name for the first
    /// frame (statinfo name, in-game label, or humanized constant family).</summary>
    public string ItemNamePlaceholder(TileItem item)
    {
        foreach (var key in item.TileKeys)
        {
            var canonical = GetCanonical(key);
            if (canonical?.EngineName is { } engineName)
            {
                return engineName;
            }
            if (canonical?.InGameLabel is { } label)
            {
                return label;
            }
            if (ActorFamilyFor(key) is { } family)
            {
                return family.Replace('_', ' ').ToLowerInvariant();
            }
            if (canonical?.Constant is { } constant)
            {
                return constant;
            }
        }
        return item.TileKeys.FirstOrDefault() ?? "item";
    }

    /// <summary>Grey placeholder for an empty frame purpose: the engine's own constant.</summary>
    public string FramePurposePlaceholder(string key) => GetCanonical(key)?.Constant ?? key;

    public IEnumerable<string> UsedCategories() =>
        Project.Items.Select(i => i.Category)
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct()
            .OrderBy(c => c);

    /// <summary>Categories in use by items of one tile kind — the Walls/Sprites tabs filter
    /// their dropdown to these.</summary>


    /// <summary>The tile whose redraw is darkened for <paramref name="key"/> under the
    /// active game's own light/dark convention, unless the user pointed it elsewhere.</summary>
    public string? DefaultLightSource(string key) => Game.DefaultLightSource(key);

    public void InvalidateDerivedDarks()
    {
        _derivedDarks.Clear();
        foreach (var key in Project.Meta
                     .Where(kv => kv.Value.Role is PairRole.DerivedDark or PairRole.AlternateDark)
                     .Select(kv => kv.Key))
        {
            InvalidateUrl(key, redraw: true);
        }
        Notify();
    }

    // ---- Display URL cache (async fill; components re-render on completion). ----

    public string? GetUrl(string key)
    {
        var cacheKey = ShowRedraw && (HasRedraw(key) || IsDerivable(key)) ? key + "|r" : key + "|o";
        if (_urlCache.TryGetValue(cacheKey, out var url))
        {
            return url;
        }
        if (_urlPending.Add(cacheKey))
        {
            _ = FillUrlAsync(key, cacheKey);
        }
        return null;
    }

    /// <summary>Items-grid preview: the display image cropped to its significant bounds
    /// so small sprites fill the cell (same zoom-to-art as the approved-frames strip).</summary>
    public string? GetItemArtUrl(string key)
    {
        var cacheKey = ShowRedraw && (HasRedraw(key) || IsDerivable(key)) ? key + "|da" : key + "|oa";
        if (_urlCache.TryGetValue(cacheKey, out var url))
        {
            return url;
        }
        if (_urlPending.Add(cacheKey))
        {
            _ = FillUrlAsync(key, cacheKey);
        }
        return null;
    }

    /// <summary>Original-art URL regardless of the show-redraws toggle — used as the ghost
    /// reference layer in the slice placement step.</summary>
    public string? GetOriginalUrl(string key)
    {
        var cacheKey = key + "|o";
        if (_urlCache.TryGetValue(cacheKey, out var url))
        {
            return url;
        }
        if (_urlPending.Add(cacheKey))
        {
            _ = FillUrlAsync(key, cacheKey);
        }
        return null;
    }

    /// <summary>RAW redraw URL — exactly the art that ships in the approved-reference grid
    /// (light-form, ignoring the show-redraws toggle and dark-variant display logic).</summary>
    public string? GetRedrawUrl(string key)
    {
        var cacheKey = key + "|rr";
        if (_urlCache.TryGetValue(cacheKey, out var url))
        {
            return url;
        }
        if (_urlPending.Add(cacheKey))
        {
            _ = FillUrlAsync(key, cacheKey);
        }
        return null;
    }

    /// <summary>Raw redraw cropped to its visible pixels — small art fills reference-strip
    /// thumbnails instead of floating in transparent space.</summary>
    public string? GetRedrawArtUrl(string key)
    {
        var cacheKey = key + "|ra";
        if (_urlCache.TryGetValue(cacheKey, out var url))
        {
            return url;
        }
        if (_urlPending.Add(cacheKey))
        {
            _ = FillUrlAsync(key, cacheKey);
        }
        return null;
    }

    private RgbaImage RedrawArtCrop(string key) => ArtCrop(GetRedraw(key) ?? GetOriginal(key));

    /// <summary>Crop to significant bounds + a little pad so small art fills its square
    /// (the zoom-to-art treatment used by the approved-frames strip and the items grid).</summary>
    private static RgbaImage ArtCrop(RgbaImage art)
    {
        if (SpriteFootprint.SignificantBounds(art) is not { } bounds)
        {
            return art;
        }
        var pad = Math.Max(2, Math.Min(art.Width, art.Height) / 64);
        var x = Math.Max(0, bounds.X - pad);
        var y = Math.Max(0, bounds.Y - pad);
        return art.Crop(x, y,
            Math.Min(art.Width - x, bounds.W + 2 * pad),
            Math.Min(art.Height - y, bounds.H + 2 * pad));
    }

    private bool IsDerivable(string key) =>
        Project.Meta.GetValueOrDefault(key) is { Role: PairRole.DerivedDark };

    private async Task FillUrlAsync(string key, string cacheKey)
    {
        try
        {
            // Crude but effective memory cap: the cache rebuilds lazily for visible tiles.
            if (_urlCache.Count > 1600)
            {
                _urlCache.Clear();
            }
            var image = cacheKey.EndsWith("|rr") ? GetRedraw(key) ?? GetOriginal(key)
                : cacheKey.EndsWith("|ra") ? RedrawArtCrop(key)
                : cacheKey.EndsWith("|da") ? ArtCrop(GetDisplay(key))
                : cacheKey.EndsWith("|oa") ? ArtCrop(GetOriginal(key))
                : cacheKey.EndsWith("|r") ? GetDisplay(key)
                : GetOriginal(key);
            _urlCache[cacheKey] = await codec.ToDataUrlAsync(image);
        }
        catch
        {
            _urlCache[cacheKey] = "";
        }
        finally
        {
            _urlPending.Remove(cacheKey);
        }
        // Throttle re-renders while a large grid is filling its URL cache.
        if (_urlPending.Count == 0 || _urlPending.Count % 8 == 0)
        {
            Notify();
        }
    }

    private void InvalidateUrl(string key, bool redraw)
    {
        _urlCache.Remove(key + (redraw ? "|r" : "|o"));
        if (redraw)
        {
            _urlCache.Remove(key + "|rr");
            _urlCache.Remove(key + "|ra");
        }
    }

    // ---- Selection ----

    public void ToggleSelect(string key, bool additive)
    {
        if (!additive)
        {
            Selection.Clear();
            Selection.Add(key);
        }
        else if (!Selection.Remove(key))
        {
            Selection.Add(key);
        }
        Notify();
    }

    /// <summary>Add a contiguous range (shift-click) to the selection, preserving order and
    /// skipping keys that are already selected.</summary>
    public void SelectRange(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (!Selection.Contains(key))
            {
                Selection.Add(key);
            }
        }
        Notify();
    }

    // ---- Groups ----

    public TileGroup AddGroup(string name)
    {
        var group = new TileGroup { Name = name };
        Project.Groups.Add(group);
        ActiveGroup = group;
        Notify();
        return group;
    }

    /// <summary>Derived darks are generated from their light sources and never belong on
    /// generation sheets.</summary>
    public bool IsDerivedDark(string key) =>
        Project.Meta.GetValueOrDefault(key)?.Role == PairRole.DerivedDark;

    public void AddSelectionToActiveGroup()
    {
        if (ActiveGroup is null)
        {
            return;
        }
        var skipped = 0;
        foreach (var key in Selection.Where(k => !ActiveGroup.TileKeys.Contains(k)))
        {
            if (IsDerivedDark(key))
            {
                skipped++;
                continue;
            }
            ActiveGroup.TileKeys.Add(key);
        }
        if (skipped > 0)
        {
            SetStatus($"Added selection ({skipped} derived-dark frame(s) excluded — " +
                      "they're generated from their lights).");
            return;
        }
        Notify();
    }

    // ---- Export / import / generation ----

    /// <summary>Plan the layout in C# (pure math) and compose the bitmap browser-side —
    /// only the tiny 64px source tiles cross the interop boundary, and the canvas scales
    /// them natively, so the UI thread never stalls on pixel loops.</summary>
    public async Task<(byte[] Png, SheetManifest Manifest)> ComposeActiveGroupPngAsync()
    {
        var group = ActiveGroup ?? throw new InvalidOperationException("No active group.");
        var manifest = PlanGroup(group).Manifest;
        var tiles = manifest.Cells.Select(cell =>
        {
            var original = GetOriginal(cell.TileKey);
            // Alternate darks enter the sheet LIGHTENED: the model redraws them as light
            // art beside their light siblings, and display/pack re-darkens the result —
            // it can't reliably paint dark variants directly (they come back as noise).
            if (Project.Meta.GetValueOrDefault(cell.TileKey)?.Role == PairRole.AlternateDark)
            {
                original = DarkGenerator.Apply(original, Project.LightenParams);
            }
            var isSprite = Game.KindOf(cell.TileKey) == TileKind.Cutout;
            if (isSprite)
            {
                // Show the model what we want back: sprite art inset with matte clearly
                // visible on all four sides (returns kept touching the cell edges and
                // getting cut off top/bottom). The placement step re-fits art to the
                // original bounds anyway, so the inset costs nothing on the round-trip.
                original = InsetIntoMatte(original);
            }
            return (cell.X, cell.Y, cell.W, original.Width, original.Pixels, isSprite);
        });
        var png = await codec.ComposeSheetAsync(
            manifest.CanvasWidth, manifest.CanvasHeight, "#202020", tiles);
        group.LastExport = manifest;
        return (png, manifest);
    }

    /// <summary>Pad sprite art into a slightly larger transparent canvas (~8% margin per
    /// side) so the composed magenta cell shows matte around all four edges.</summary>
    private static RgbaImage InsetIntoMatte(RgbaImage art)
    {
        var pad = Math.Max(4, art.Width / 10);
        var canvas = new RgbaImage(art.Width + 2 * pad, art.Height + 2 * pad);
        canvas.Paste(art, pad, pad);
        return canvas;
    }

    /// <summary>Plan a group's sheet with its seamless runs — the platter renders straight
    /// from this so the preview always matches what gets composed.</summary>
    public PlannedLayout PlanGroup(TileGroup group) =>
        SheetComposer.PlanLayoutRuns(
            group.TileKeys, Project.Generation.TilePx, group.GutterPx, group.SeamlessRuns);

    /// <summary>Predicted square cell-grid side and API size preset for a group, without
    /// composing — shown on the Generate button.</summary>
    public (int Side, int CanvasPx, string ApiSize) PredictLayout(TileGroup group)
    {
        var planned = PlanGroup(group);
        return (planned.Side, planned.Manifest.CanvasWidth,
            ApiSizeFor(planned.Manifest.CanvasWidth));
    }

    /// <summary>Drop seamless runs that are no longer an ordered contiguous slice of the
    /// group's tile keys — reorders and removals dissolve the runs they break.</summary>
    public static void ValidateRuns(TileGroup group)
    {
        group.SeamlessRuns.RemoveAll(run =>
        {
            if (run.Count < 2)
            {
                return true;
            }
            var start = group.TileKeys.IndexOf(run[0]);
            if (start < 0 || start + run.Count > group.TileKeys.Count)
            {
                return true;
            }
            for (var i = 0; i < run.Count; i++)
            {
                if (group.TileKeys[start + i] != run[i])
                {
                    return true;
                }
            }
            return false;
        });
    }

    /// <summary>The run containing <paramref name="key"/>, if any.</summary>
    public static List<string>? RunContaining(TileGroup group, string key) =>
        group.SeamlessRuns.FirstOrDefault(run => run.Contains(key));

    /// <summary>Smallest Gemini size preset that keeps the requested per-tile resolution.</summary>
    public static string ApiSizeFor(int canvasPx) =>
        canvasPx <= 1024 ? "1K" : canvasPx <= 2048 ? "2K" : "4K";

    /// <summary>The payoff for the item/frame metadata: a per-cell map plus per-item context
    /// so the model knows exactly what every cell depicts.</summary>
    public string BuildSheetMapPrompt(SheetManifest manifest)
    {
        var itemByKey = new Dictionary<string, TileItem>();
        foreach (var item in Project.Items)
        {
            foreach (var key in item.TileKeys)
            {
                itemByKey[key] = item;
            }
        }
        string ItemName(TileItem? item) =>
            item is null ? "?"
            : item.GenerationAlias is { Length: > 0 } alias ? alias
            : item.Name.Length > 0 ? item.Name : ItemNamePlaceholder(item);

        var sb = new System.Text.StringBuilder();
        sb.Append($"\nThe sheet is a {manifest.Columns}×{manifest.Rows} cell grid ");
        sb.Append($"({manifest.TilePx}px cells); unlisted cells are empty background. ");
        sb.Append("Preserve the exact grid geometry and canvas: every cell's art stays ");
        sb.Append("inside that cell's bounds at its original position and size — never ");
        sb.Append("re-layout, enlarge, crop, or re-frame rows or cells, and leave empty ");
        sb.Append("areas empty. ");
        sb.Append("Redraw every sprite at the SAME size and position within its cell as the ");
        sb.Append("input (with the exception of insetting solid magenta background items ");
        sb.Append("slightly) — small objects must stay small with empty background around ");
        sb.Append("them; never enlarge art to fill a cell. Stay faithful to each original ");
        sb.Append("object's design — do not modernize or reinterpret it unless otherwise ");
        sb.Append("instructed to. ");
        sb.Append("Cell map (row,col from top-left):\n");
        var itemsOnSheet = new Dictionary<TileItem, List<SheetCell>>();
        foreach (var cell in manifest.Cells)
        {
            var row = cell.CellIndex / manifest.Columns + 1;
            var col = cell.CellIndex % manifest.Columns + 1;
            var item = itemByKey.GetValueOrDefault(cell.TileKey);
            var meta = Project.Meta.GetValueOrDefault(cell.TileKey);
            var purpose = meta?.GenerationAlias is { Length: > 0 } frameAlias ? frameAlias
                : meta?.Purpose is { Length: > 0 } p ? p : FramePurposePlaceholder(cell.TileKey);
            sb.Append($"({row},{col}) {ItemName(item)} — {purpose}");
            if (Game.KindOf(cell.TileKey) == TileKind.Cutout &&
                GetArtBox(cell.TileKey) is { } artBox)
            {
                var original = GetOriginal(cell.TileKey);
                var widthPct = 100 * artBox.W / original.Width;
                var heightPct = 100 * artBox.H / original.Height;
                if (Math.Max(widthPct, heightPct) <= 85)
                {
                    sb.Append(" [small object]");
                }
            }
            if (meta?.Role == PairRole.AlternateDark)
            {
                var referenceKey = meta.LightSourceKey ?? DefaultLightSource(cell.TileKey);
                if (referenceKey is not null)
                {
                    var referenceCell = manifest.Cells.FirstOrDefault(c => c.TileKey == referenceKey);
                    var referenceMeta = Project.Meta.GetValueOrDefault(referenceKey);
                    var referenceLabel = referenceCell is not null
                        ? $"cell ({referenceCell.CellIndex / manifest.Columns + 1}," +
                          $"{referenceCell.CellIndex % manifest.Columns + 1})"
                        : $"its light frame \"{(referenceMeta?.GenerationAlias is { Length: > 0 } ra ? ra : referenceMeta?.Purpose is { Length: > 0 } rp ? rp : FramePurposePlaceholder(referenceKey))}\"";
                    sb.Append($" [variant frame: match the lighting and brightness of {referenceLabel} " +
                              "exactly, keeping only this cell's own variation]");
                }
            }
            sb.Append('\n');
            if (item is not null)
            {
                itemsOnSheet.TryAdd(item, []);
                itemsOnSheet[item].Add(cell);
            }
        }
        // Call out each seamless strip so the model knows exactly which cells must flow
        // together — a sheet can mix butted strips with ordinary independent cells.
        var ordered = manifest.Cells.OrderBy(c => c.CellIndex).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (!ordered[i].Seamless)
            {
                continue;
            }
            var start = i;
            while (i + 1 < ordered.Count && ordered[i + 1].Seamless &&
                   ordered[i + 1].Y == ordered[i].Y &&
                   ordered[i + 1].X == ordered[i].X + ordered[i].W)
            {
                i++;
            }
            var row = ordered[start].CellIndex / manifest.Columns + 1;
            var colA = ordered[start].CellIndex % manifest.Columns + 1;
            var colB = ordered[i].CellIndex % manifest.Columns + 1;
            sb.Append($"CRITICAL — cells ({row},{colA}) through ({row},{colB}) are ONE " +
                      "continuous strip, butted edge-to-edge with NO gap between them in " +
                      "the input. Reproduce them exactly the same way: one unbroken " +
                      "panorama covering the strip's full width. Do NOT split the strip " +
                      "into separate cells, do NOT insert background-colored gutters, " +
                      "gaps, dividers, borders, or frames between or around them, and do " +
                      "NOT shrink, re-center, or re-space their content. The artwork must " +
                      "flow across the shared edges with no visible seam, occupying " +
                      "exactly the same pixels as the input strip.\n");
        }
        if (itemsOnSheet.Count > 0)
        {
            // The frames/angles wording only applies when some multi-cell item actually has
            // independent (non-butted) cells; pure mural strips already got their clause.
            var hasFrameItems = itemsOnSheet.Any(kv =>
                kv.Value.Count > 1 && kv.Value.Any(c => !c.Seamless));
            sb.Append(hasFrameItems
                ? "Items on this sheet (a multi-cell item's cells depict the SAME subject in " +
                  "different frames/angles — keep design, colors and proportions identical " +
                  "across them):\n"
                : "Items on this sheet:\n");
            foreach (var (item, cells) in itemsOnSheet)
            {
                sb.Append($"• {ItemName(item)} [{item.Category}] — {cells.Count} cell(s).\n");
            }
        }
        return sb.ToString();
    }

    public async Task ExportActiveGroupAsync(bool toClipboard, bool download)
    {
        try
        {
            Busy = true;
            SetStatus("Composing sheet…");
            var (png, manifest) = await ComposeActiveGroupPngAsync();
            if (toClipboard)
            {
                await codec.CopyPngToClipboardAsync(png);
            }
            if (download)
            {
                await codec.DownloadAsync($"{SafeName(ActiveGroup!.Name)}-sheet.png", png, "image/png");
            }
            SetStatus($"Sheet exported ({manifest.CanvasWidth}x{manifest.CanvasHeight}). " +
                      "Manifest stored for import.");
        }
        catch (Exception ex)
        {
            SetStatus($"Export failed: {ex.Message}");
        }
        finally
        {
            Busy = false;
            Notify();
        }
    }

    public async Task CreateImportJobAsync(byte[] rawPng)
    {
        var group = ActiveGroup;
        if (group?.LastExport is null)
        {
            SetStatus("Import ignored: active group has no exported manifest yet.");
            return;
        }
        var archived = await ArchiveRawSheetAsync("import", rawPng, group.Name);
        var job = new GenerationJob
        {
            Kind = "import",
            Group = group,
            Manifest = group.LastExport,
            State = JobState.AwaitingReview,
            SheetFile = archived,
            Number = NextJobNumber(group),
        };
        await AttachSheetPngAsync(job, rawPng);
        Jobs.Insert(0, job);
        // No tab yet — the import's toast/jobs entry opens it on click.
        SetStatus($"Received {job.SheetWidth}x{job.SheetHeight} sheet for '{group.Name}'. " +
                  "Slice it, or mark regions and request a revision.");
    }

    /// <summary>Every raw sheet that enters the app (API generation, revision, paste, file)
    /// is archived verbatim under generations/, independent of slicing.</summary>
    private async Task<string?> ArchiveRawSheetAsync(string kind, byte[] png, string? groupName = null)
    {
        if (!workspace.IsOpen || workspace.WritesBlocked)
        {
            return null;
        }
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var safeGroup = SafeName(groupName ?? ActiveGroup?.Name ?? "sheet");
            var path = $"generations/{stamp}-{safeGroup}-{kind}.png";
            await workspace.WriteAsync(path, png);
            return path;
        }
        catch (Exception ex)
        {
            SetStatus($"⚠ Could not archive raw sheet: {FirstLine(ex.Message)}");
            return null;
        }
    }

    /// <summary>Step 1 of accepting a sheet: slice and key every cell (chunked with yields —
    /// heavy C# on the single WASM thread), then enter the placement step where each keyed
    /// sprite can be scaled/moved over a ghost of its original before baking. Sprites start
    /// at what auto-normalize would do; wall-only sheets skip straight to apply.</summary>
    public async Task PrepareSliceAsync(GenerationJob job)
    {
        if (job.RawPng is null || job.State != JobState.AwaitingReview)
        {
            return;
        }
        SetStatus("Slicing…");
        Notify();
        var sheet = await EnsureJobSheetAsync(job);
        var background = SheetSlicer.EstimateBackground(sheet);
        var placements = new List<SlicePlacement>(job.Manifest.Cells.Count);
        foreach (var cell in job.Manifest.Cells)
        {
            SetStatus($"Slicing… {placements.Count + 1}/{job.Manifest.Cells.Count}");
            await Task.Delay(1);
            var result = SheetSlicer.SliceCell(sheet, background, job.Manifest, cell, SliceTargetPx);
            var isSprite = Game.KindOf(result.TileKey) == TileKind.Cutout;
            var keyMode = "-";
            var auto = SpritePlacement.Identity;
            double anchorX = 0.5, anchorY = 0.5;
            (double X, double Y, double W, double H) boundsRect = (0, 0, 1, 1);
            if (isSprite)
            {
                // Redrawn sprites come back with an opaque background fill; restore the
                // cutout, then start from the auto footprint fit — everything stays
                // hand-tunable in the placement step.
                keyMode = AlphaKeyer.KeyAuto(result.Image);
                auto = SpriteFootprint.ComputePlacement(result.Image, GetOriginal(result.TileKey));
                if (SpriteFootprint.SignificantBounds(result.Image) is { } bounds)
                {
                    anchorX = (bounds.X + bounds.W / 2.0) / result.Image.Width;
                    anchorY = (bounds.Y + bounds.H / 2.0) / result.Image.Height;
                    boundsRect = (
                        bounds.X / (double)result.Image.Width,
                        bounds.Y / (double)result.Image.Height,
                        bounds.W / (double)result.Image.Width,
                        bounds.H / (double)result.Image.Height);
                }
            }
            var placement = new SlicePlacement
            {
                TileKey = result.TileKey,
                Keyed = result.Image,
                IsSprite = isSprite,
                KeyMode = keyMode,
                UsedFallback = result.UsedFallback,
                WrapError = result.WrapError,
                Auto = auto,
                Scale = auto.Scale,
                OffsetX = auto.OffsetX,
                OffsetY = auto.OffsetY,
                AnchorX = anchorX,
                AnchorY = anchorY,
                BoundsX = boundsRect.X,
                BoundsY = boundsRect.Y,
                BoundsW = boundsRect.W,
                BoundsH = boundsRect.H,
            };
            placement.PreviewUrl = await codec.ToDataUrlAsync(result.Image);
            placements.Add(placement);
        }
        job.Sheet = null;
        job.Placements = placements;
        job.State = JobState.Placing;
        var spriteCount = placements.Count(p => p.IsSprite);
        SetStatus(spriteCount > 0
            ? $"Position {spriteCount} sprites over their ghost originals, untick any tiles " +
              "to keep as-is, then Apply."
            : $"Review {placements.Count} wall tiles — untick any to keep as-is, then Apply.");
        Notify();
        _ = PersistPlacementArtAsync(job);
    }

    /// <summary>Step 2: bake each cell's (possibly hand-tuned) placement, apply the tiles,
    /// and record the result as a new group revision (persisted per-tile, switchable).</summary>
    public async Task ApplySliceAsync(GenerationJob job)
    {
        if (job.Placements is null ||
            job.State is not (JobState.Placing or JobState.AwaitingReview))
        {
            return;
        }
        // Partial slice: unticked tiles keep whatever redraw they already have.
        var placements = job.Placements.Where(p => p.Included).ToList();
        var skipped = job.Placements.Count - placements.Count;
        if (placements.Count == 0)
        {
            SetStatus("Nothing selected to apply — tick at least one sprite (or go Back).");
            Notify();
            return;
        }
        var revisionId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var revisionDir = $"revisions/{SafeName(job.Group.Name)}/{revisionId}";
        var persistRevision = workspace.IsOpen && !workspace.WritesBlocked;
        var baselines = new List<(string Key, double Fraction)>();
        var done = 0;
        string? persistWarning = null;
        foreach (var placement in placements)
        {
            SetStatus($"Applying… {++done}/{placements.Count}");
            Notify();
            await Task.Delay(1);
            var image = placement.IsSprite
                ? SpriteFootprint.ApplyPlacement(placement.Keyed, placement.Current)
                : placement.Keyed;
            if (placement.IsSprite &&
                AlphaKeyer.BaselineFraction(image) is { } fraction)
            {
                baselines.Add((placement.TileKey, fraction));
            }
            // Encode once and reuse the bytes for both the live redraw and the revision copy
            // (SetRedraw's own persistence would encode the same image a second time).
            SetRedraw(placement.TileKey, image, persist: false);
            if (workspace.IsOpen)
            {
                try
                {
                    var png = await codec.EncodePngAsync(image);
                    await workspace.WriteAsync($"redraws/{FileNameFor(placement.TileKey)}", png);
                    if (persistRevision)
                    {
                        await workspace.WriteAsync($"{revisionDir}/{FileNameFor(placement.TileKey)}", png);
                    }
                }
                catch (Exception ex)
                {
                    persistWarning = FirstLine(ex.Message);
                }
            }
            var tileVersions = Project.TileVersions.TryGetValue(placement.TileKey, out var list)
                ? list
                : Project.TileVersions[placement.TileKey] = [];
            tileVersions.Add(new TileVersionInfo
            {
                Id = revisionId,
                Kind = job.Kind,
                GroupName = job.Group.Name,
                Created = DateTime.Now,
                File = $"{revisionDir}/{FileNameFor(placement.TileKey)}",
                Scale = placement.Scale,
                OffsetX = placement.OffsetX,
                OffsetY = placement.OffsetY,
                Rotation = placement.Rotation,
            });
            GetMeta(placement.TileKey).ActiveVersionId = revisionId;
        }
        if (persistWarning is not null)
        {
            SetStatus($"⚠ Tile persist failed: {persistWarning}");
        }
        job.Group.Revisions.Add(new GroupRevisionInfo
        {
            Id = revisionId,
            Kind = job.Kind,
            Created = DateTime.Now,
            TileKeys = placements.Select(p => p.TileKey).ToList(),
        });
        job.Group.ActiveRevisionId = revisionId;
        job.State = JobState.Done;
        job.Sheet = null;
        job.RawPng = null;
        job.PreviewUrl = null;
        job.Placements = null;
        OpenTabs.Remove(job); // finished — its tab closes itself
        if (ActiveJob == job)
        {
            ActiveJob = null;
        }
        var tuned = placements.Count(p => p.Edited);
        var fallbacks = placements.Count(p => p.UsedFallback);
        var keyModes = placements.Where(p => p.IsSprite).Select(p => p.KeyMode).Distinct().ToList();
        var worstWrap = placements.OrderByDescending(p => p.WrapError).First();
        var message = $"Sliced {placements.Count} tiles at {SliceTargetPx}px into revision {revisionId}" +
                      (tuned > 0 ? $" ({tuned} hand-placed)" : "") +
                      (skipped > 0 ? $" ({skipped} skipped)" : "") +
                      (fallbacks > 0 ? $" ({fallbacks} proportional fallbacks)" : "") +
                      (keyModes.Count > 0 ? $"; keyed via {string.Join("+", keyModes)}" : "");
        if (baselines.Count > 1)
        {
            var spread = baselines.Max(b => b.Fraction) - baselines.Min(b => b.Fraction);
            message += $"; sprite baselines {baselines.Min(b => b.Fraction):F3}–" +
                       $"{baselines.Max(b => b.Fraction):F3} (spread {spread * 100:F1}%" +
                       (spread > 0.03 ? " ⚠ ground-line drift" : "") + ")";
        }
        else if (baselines.Count == 0)
        {
            message += $"; worst wrap seam: {worstWrap.TileKey} ({worstWrap.WrapError:F1})";
        }
        SetStatus(message + ".");
    }

    /// <summary>Abandon the placement step: back to the sheet review (the sheet is still
    /// re-decodable from RawPng, so nothing is lost).</summary>
    public void CancelSlicePlacement(GenerationJob job)
    {
        if (job.State != JobState.Placing)
        {
            return;
        }
        job.Placements = null;
        job.State = JobState.AwaitingReview;
        SetStatus("Placement cancelled — sheet back in review.");
        Notify();
    }

    /// <summary>Re-activate a previously accepted revision: its tiles are loaded from the
    /// workspace and become the group's applied redraws.</summary>
    public async Task SwitchRevisionAsync(TileGroup group, string revisionId)
    {
        var info = group.Revisions.FirstOrDefault(r => r.Id == revisionId);
        if (info is null || !workspace.IsOpen)
        {
            SetStatus("Revision unavailable (needs the workspace).");
            return;
        }
        var dir = $"revisions/{SafeName(group.Name)}/{revisionId}";
        var loaded = 0;
        foreach (var key in info.TileKeys)
        {
            var png = await workspace.ReadAsync($"{dir}/{FileNameFor(key)}");
            if (png is null)
            {
                continue;
            }
            SetRedraw(key, await codec.DecodePngAsync(png));
            GetMeta(key).ActiveVersionId = revisionId;
            loaded++;
        }
        group.ActiveRevisionId = revisionId;
        SetStatus($"Switched '{group.Name}' to revision {revisionId} ({loaded} tiles).");
    }

    /// <summary>Add a style reference: persisted to the workspace under style-refs/ and
    /// appended to the ordered attachment list.</summary>
    public async Task AddStyleRefAsync(byte[] png)
    {
        if (!workspace.IsOpen || workspace.WritesBlocked)
        {
            SetStatus("Adding a style reference needs a writable workspace.");
            return;
        }
        var file = $"style-refs/{DateTime.Now:yyyyMMdd-HHmmss-fff}.png";
        await workspace.WriteAsync(file, png);
        Project.Generation.StyleRefs.Add(new StyleRefInfo { File = file });
        _styleRefUrls[file] = "data:image/png;base64," + Convert.ToBase64String(png);
        SetStatus("Style reference added.");
        Notify();
    }

    /// <summary>Replace a style reference's image with new bytes, keeping its context and
    /// position in the attachment order.</summary>
    public async Task SwapStyleRefAsync(StyleRefInfo reference, byte[] png)
    {
        if (!workspace.IsOpen || workspace.WritesBlocked)
        {
            SetStatus("Swapping a style reference needs a writable workspace.");
            return;
        }
        var file = $"style-refs/{DateTime.Now:yyyyMMdd-HHmmss-fff}.png";
        await workspace.WriteAsync(file, png);
        _styleRefUrls.Remove(reference.File);
        _styleRefFullUrls.Remove(reference.File);
        reference.File = file;
        _styleRefUrls[file] = "data:image/png;base64," + Convert.ToBase64String(png);
        SetStatus("Style reference image swapped (context kept).");
        Notify();
    }

    public void RemoveStyleRef(StyleRefInfo reference)
    {
        Project.Generation.StyleRefs.Remove(reference);
        _styleRefUrls.Remove(reference.File);
        SetStatus("Style reference removed (its file stays in the workspace).");
        Notify();
    }

    private readonly Dictionary<string, string> _styleRefUrls = [];

    /// <summary>Thumbnail data URL for a style reference (async fill).</summary>
    public string? StyleRefUrl(StyleRefInfo reference)
    {
        if (_styleRefUrls.TryGetValue(reference.File, out var url))
        {
            return url.Length > 0 ? url : null;
        }
        if (!workspace.IsOpen)
        {
            return null;
        }
        _styleRefUrls[reference.File] = "";
        _ = FillStyleRefUrlAsync(reference.File);
        return null;
    }

    private async Task FillStyleRefUrlAsync(string file)
    {
        try
        {
            var png = await workspace.ReadAsync(file);
            if (png is not null)
            {
                _styleRefUrls[file] = await codec.PreviewDataUrlAsync(png, 160);
                Notify();
            }
        }
        catch
        {
            // thumbnail only — generation reads the file directly
        }
    }

    private readonly Dictionary<string, string> _styleRefFullUrls = [];

    /// <summary>Large preview for the style-reference lightbox (async fill).</summary>
    public string? StyleRefFullUrl(StyleRefInfo reference)
    {
        if (_styleRefFullUrls.TryGetValue(reference.File, out var url))
        {
            return url.Length > 0 ? url : null;
        }
        if (!workspace.IsOpen)
        {
            return null;
        }
        _styleRefFullUrls[reference.File] = "";
        _ = FillStyleRefFullUrlAsync(reference.File);
        return null;
    }

    private async Task FillStyleRefFullUrlAsync(string file)
    {
        try
        {
            var png = await workspace.ReadAsync(file);
            if (png is not null)
            {
                _styleRefFullUrls[file] = await codec.PreviewDataUrlAsync(png, 1400);
                Notify();
            }
        }
        catch
        {
            // preview only
        }
    }

    /// <summary>Hex outline colors for the four revision blocks (matches RevisionTools'
    /// palette and the UI chip styling).</summary>
    private static readonly string[] AnnotationColors = ["#ff0000", "#005aff", "#00c800", "#ff8c00"];

    /// <summary>Thinking levels the given model supports (empty = no thinking control).
    /// Conservative best-known map; unknown models get the full menu and the API
    /// arbitrates (a rejected value surfaces as a clear job error).</summary>
    public static string[] ThinkingLevelsFor(string modelId)
    {
        var id = modelId.ToLowerInvariant();
        if (id.StartsWith("gpt-image"))
        {
            return ["low", "medium", "high"]; // maps to the OpenAI quality parameter
        }
        if (id.Contains("gemini-3.1") && id.Contains("flash"))
        {
            return ["minimal", "low", "medium", "high"];
        }
        if (id.Contains("gemini-3.1"))
        {
            return ["low", "medium", "high"];
        }
        if (id.Contains("gemini-3"))
        {
            return ["low", "high"];
        }
        if (id.Contains("gemini-2.5"))
        {
            return []; // thinkingLevel is a Gemini 3+ control
        }
        return ["minimal", "low", "medium", "high"];
    }

    /// <summary>Best-known API default thinking level per model ("" = no thinking control).
    /// Flash 3.1 family defaults to minimal (latency-first); the pro line defaults to
    /// high. Single source of truth — correct here if Google shifts a default.</summary>
    public static string DefaultThinkingFor(string modelId)
    {
        var id = modelId.ToLowerInvariant();
        if (id.StartsWith("gpt-image"))
        {
            return "high";
        }
        if (id.Contains("gemini-3.1") && id.Contains("flash"))
        {
            return "minimal";
        }
        if (id.Contains("gemini-3"))
        {
            return "high";
        }
        return "";
    }

    /// <summary>Per-run thinking override (set inline next to Generate); null follows the
    /// AI Model drawer setting, "" forces the API default.</summary>
    public string? ThinkingOverride { get; set; }

    /// <summary>The concrete level the next run resolves to (override → drawer setting →
    /// model default) — what the inline selector displays.</summary>
    public string ResolvedThinkingLevel
    {
        get
        {
            var levels = ThinkingLevelsFor(Project.Generation.ModelId);
            if (levels.Length == 0)
            {
                return "";
            }
            var candidate = ThinkingOverride ?? Project.Generation.ThinkingLevel;
            return candidate.Length > 0 && levels.Contains(candidate)
                ? candidate
                : DefaultThinkingFor(Project.Generation.ModelId);
        }
    }

    /// <summary>The thinking level actually sent: override, else the drawer setting —
    /// silently dropped to API default when the selected model doesn't support it.</summary>
    public string EffectiveThinkingLevel
    {
        get
        {
            var level = ThinkingOverride ?? Project.Generation.ThinkingLevel;
            return ThinkingLevelsFor(Project.Generation.ModelId).Contains(level) ? level : "";
        }
    }


    /// <summary>All frames eligible for the group's approved-frames reference (before
    /// per-group exclusions) — what the preview strip shows.</summary>
    public List<string> ApprovedReferenceCandidates(TileGroup group)
    {
        var groupKeys = group.TileKeys.ToHashSet();
        return Project.Items
            .Where(item => item.TileKeys.Any(groupKeys.Contains))
            .SelectMany(item => item.TileKeys)
            .Where(k => Game.KindOf(k) == TileKind.Cutout && HasRedraw(k))
            .Distinct()
            .ToList();
    }

    /// <summary>Approved redraws actually attached as the identity anchor: candidates
    /// minus the group's exclusions, gated by both toggles. Capped at 25.</summary>
    public List<string> ApprovedReferenceKeys(TileGroup group)
    {
        if (!group.UseApprovedRef)
        {
            return [];
        }
        return ApprovedReferenceCandidates(group)
            .Where(k => !group.ApprovedRefExcluded.Contains(k))
            .Take(25)
            .ToList();
    }

    /// <summary>Character reference for a group: any image showing what the subject looks
    /// like (e.g. a portrait), persisted to the workspace at refs/<group>.png.</summary>
    public async Task SetCharacterRefAsync(TileGroup group, byte[] png)
    {
        if (!workspace.IsOpen || workspace.WritesBlocked)
        {
            SetStatus("Setting a character reference needs a writable workspace.");
            return;
        }
        var file = $"refs/{SafeName(group.Name)}.png";
        await workspace.WriteAsync(file, png);
        group.CharacterRefFile = file;
        _characterRefUrls.Remove(group.Name);
        SetStatus($"Character reference set for '{group.Name}'.");
        Notify();
    }

    public void RemoveCharacterRef(TileGroup group)
    {
        group.CharacterRefFile = null;
        _characterRefUrls.Remove(group.Name);
        SetStatus($"Character reference removed from '{group.Name}' (file kept in refs/).");
        Notify();
    }

    private readonly Dictionary<string, string> _characterRefUrls = [];

    /// <summary>Thumbnail data URL for the group's character reference (async fill).</summary>
    public string? CharacterRefUrl(TileGroup group)
    {
        if (group.CharacterRefFile is null || !workspace.IsOpen)
        {
            return null;
        }
        if (_characterRefUrls.TryGetValue(group.Name, out var url))
        {
            return url;
        }
        _characterRefUrls[group.Name] = "";
        _ = FillCharacterRefUrlAsync(group);
        return null;
    }

    private async Task FillCharacterRefUrlAsync(TileGroup group)
    {
        try
        {
            var png = await workspace.ReadAsync(group.CharacterRefFile!);
            if (png is not null)
            {
                _characterRefUrls[group.Name] = await codec.PreviewDataUrlAsync(png, 160);
                Notify();
            }
        }
        catch
        {
            // thumbnail only — generation reads the file directly
        }
    }

    /// <summary>Style refs whose files are reachable right now — the single source both the
    /// prompt enumeration and the attachment list draw from, so indexes always agree.</summary>
    private List<StyleRefInfo> ActiveStyleRefs() =>
        workspace.IsOpen ? Project.Generation.StyleRefs : [];

    /// <summary>Assembles the attachment list for a generation call. Order matters and must
    /// match <see cref="BuildPrompt"/>: sheet, style refs (in order), character ref,
    /// approved frames.</summary>
    private async Task<List<byte[]>> BuildGenerationImagesAsync(TileGroup group, byte[] sheetPng)
    {
        var images = new List<byte[]> { sheetPng };
        foreach (var reference in ActiveStyleRefs())
        {
            if (await workspace.ReadAsync(reference.File) is { } png)
            {
                images.Add(png);
            }
        }
        if (group.CharacterRefFile is not null && workspace.IsOpen &&
            await workspace.ReadAsync(group.CharacterRefFile) is { } characterRef)
        {
            images.Add(characterRef);
        }
        var approvedKeys = ApprovedReferenceKeys(group);
        if (approvedKeys.Count > 0 && workspace.IsOpen)
        {
            var pngs = new List<byte[]>();
            foreach (var key in approvedKeys)
            {
                if (await workspace.ReadAsync($"redraws/{FileNameFor(key)}") is { } png)
                {
                    pngs.Add(png);
                }
            }
            if (pngs.Count > 0)
            {
                images.Add(await codec.ComposePngGridAsync(pngs, 256));
            }
        }
        return images;
    }

    /// <summary>Sheet-mechanics instructions that always apply — kept out of the
    /// user-editable style prompt (implementation detail, not art direction). Also the
    /// exact text the one-time migration strips from previously-seeded style prompts.</summary>
    private const string SheetMechanicsPrompt =
        "Keep the exact same aspect ratio, composition and framing — do not " +
        "crop, extend, or add borders. Each bright cell is a separate texture: keep every " +
        "horizontal registration line at identical heights across cells, and keep each cell's " +
        "left and right edges continuation-compatible. The dark gray background and separators " +
        "are canvas, not art — leave them flat dark gray. Do not add any watermark, text, or " +
        "signature.";

    /// <summary>One-time cleanup: earlier versions seeded the mechanics text into the
    /// user-editable style prompt — pull it out so it isn't sent twice.</summary>
    private void MigrateStylePrompt()
    {
        var prompt = Project.Generation.StylePrompt;
        var stripped = prompt.Replace(SheetMechanicsPrompt, "").Replace("  ", " ").Trim();
        if (stripped != prompt)
        {
            Project.Generation.StylePrompt = stripped;
            SetStatus("Style prompt: sheet-mechanics text moved into the built-in prompt.");
        }
    }

    public string BuildPrompt()
    {
        var group = ActiveGroup;
        var prompt = "Redraw this sprite sheet to be upscaled.\n" +
                     "Style Description: " + Project.Generation.StylePrompt + "\n" +
                     SheetMechanicsPrompt;
        var attachments = new List<string> { "the sprite sheet to redraw" };
        var styleRefCount = 0;
        foreach (var reference in ActiveStyleRefs())
        {
            attachments.Add("a style reference image — " +
                            (reference.Context is { Length: > 0 } context
                                ? context
                                : "match its art direction"));
            styleRefCount++;
        }
        if (group?.CharacterRefFile is not null)
        {
            attachments.Add(
                "a character reference showing what this sheet's subject looks like: treat " +
                "it as the definitive design — match the face, hair, build, clothing and " +
                "colors exactly so every redrawn cell reads as this exact character. It " +
                "shows APPEARANCE ONLY — never copy its composition, framing, cropping or " +
                "layout; the output must keep the input sheet's exact grid geometry");
        }
        if (group is not null && ApprovedReferenceKeys(group).Count > 0)
        {
            attachments.Add(
                "a grid of this subject's ALREADY-APPROVED redrawn frames from earlier " +
                "sheets: new frames must match their design, colors, proportions and " +
                "rendering exactly, as the same character in the same style");
        }
        if (attachments.Count > 1)
        {
            prompt += $"\n{attachments.Count} images are attached.";
            for (var i = 0; i < attachments.Count; i++)
            {
                prompt += $" Image {i + 1} is {attachments[i]}.";
                if (styleRefCount > 0 && i == styleRefCount)
                {
                    // Placed directly after the last style reference so "them" binds to
                    // the style images, not the identity references that follow.
                    prompt += " Use them as context only; do not copy their subject " +
                              "matter, characters, or layout into your output.";
                }
            }
        }
        if (group?.TileKeys.Any(k => Game.KindOf(k) == TileKind.Cutout) == true)
        {
            prompt += "\nCells with a solid magenta background are transparent-sprite cells: " +
                      "keep their backgrounds EXACTLY solid flat magenta (#FF00FF) in your " +
                      "output — no gradients, no shadows, no background detail, and never use " +
                      "magenta inside the character art itself. For cells with a solid magenta " +
                      "background, make sure the redrawn image is inset from the magenta " +
                      "background so there is visible magenta background around all four edges " +
                      "of each cell. The input sheet already shows every magenta-cell item " +
                      "inset exactly like this — keep the same framing in your output and " +
                      "never let the art touch any edge of its cell. Also for all objects with a magenta background, re-position " +
                      "them to a directly front facing perspective and from a slightly higher " +
                      "angle, and their front facing surface bottom edge should be parallel " +
                      "with the cell bottom, and only one side of the object is very narrowly " +
                      "visible.";
        }
        var custom = group?.PromptTemplate;
        return string.IsNullOrWhiteSpace(custom) ? prompt : $"{prompt}\n\n{custom}";
    }

    /// <summary>Kick off a generation as a background job: the UI stays free, the job appears
    /// in the Jobs rail, and its result awaits review when the API returns.</summary>
    public async Task StartGenerationJobAsync(string prompt)
    {
        if (string.IsNullOrWhiteSpace(ActiveProviderKey))
        {
            SetStatus(UsesOpenAi
                ? "Set your OpenAI API key first (Settings → Model API)."
                : "Set your Gemini API key first (Settings → Model API).");
            return;
        }
        if (ActiveGroup is null || ActiveGroup.TileKeys.Count == 0)
        {
            SetStatus("Select a group with tiles first.");
            return;
        }
        // Layout math is instant; the jobs appear immediately and ALL heavy work (canvas
        // compose, network, preview) happens behind awaits so the UI never stalls.
        var manifest = PlanGroup(ActiveGroup).Manifest;
        ActiveGroup.LastExport = manifest;
        var fullPrompt = prompt + BuildSheetMapPrompt(manifest);
        // Compose once up front: versions run serially and the active group can change
        // while later versions wait their turn.
        var (inputPng, _) = await ComposeActiveGroupPngAsync();
        var nextNumber = NextJobNumber(ActiveGroup);
        var versions = Math.Clamp(Project.Generation.VersionCount, 1, 4);
        var batchCts = new CancellationTokenSource();
        var batch = new List<GenerationJob>();
        for (var i = 0; i < versions; i++)
        {
            // Spread top-p across siblings so the takes genuinely diverge (Gemini only —
            // the OpenAI images API has no top-p; takes differ by sampling alone).
            double? topP = versions > 1 && !UsesOpenAi ? 0.95 - 0.10 * i : null;
            var job = new GenerationJob
            {
                Kind = "generate",
                Group = ActiveGroup,
                Manifest = manifest,
                TopP = topP,
                VariantLabel = versions > 1
                    ? (topP is null ? $"take {i + 1}" : $"top-p {topP:0.00}")
                    : null,
                State = JobState.Queued,
                Cts = batchCts, // one cancel stops the whole batch
                Number = nextNumber + i,
            };
            job.Prompts.Add(fullPrompt);
            Jobs.Insert(0, job);
            batch.Add(job);
        }
        // No tab yet — jobs live in their toasts/the jobs list until clicked open.
        _ = RunBatchAsync(batch, fullPrompt, inputPng);
        SetStatus($"{(versions > 1 ? $"{versions} generations" : "Generation")} started for " +
                  $"'{ActiveGroup.Name}' — {manifest.Columns}×{manifest.Rows} sheet at " +
                  $"{ApiSizeFor(manifest.CanvasWidth)}, running in the background.");
        Notify();
    }

    /// <summary>Run a batch's versions strictly in series: one arming countdown covers
    /// the whole batch, one cancel stops it, finished versions stay reviewable.</summary>
    private async Task RunBatchAsync(List<GenerationJob> batch, string prompt, byte[] inputPng)
    {
        try
        {
            await ArmCountdownAsync(batch[0]);
        }
        catch (OperationCanceledException)
        {
            foreach (var job in batch)
            {
                job.State = JobState.Cancelled;
                _ = AutoCloseCancelToastAsync(job);
            }
            SetStatus($"'{batch[0].Group.Name}' generation cancelled before the API call — " +
                      "no credits used.");
            Notify();
            return;
        }
        foreach (var job in batch)
        {
            if (job.Cts.IsCancellationRequested)
            {
                if (job.State == JobState.Queued)
                {
                    job.State = JobState.Cancelled;
                    _ = AutoCloseCancelToastAsync(job);
                    Notify();
                }
                continue;
            }
            await RunGenerationAsync(job, prompt, inputPng);
        }
    }

    private int _runningJobs;

    /// <summary>Polled concurrency gate: honors MaxConcurrentJobs changes immediately for
    /// jobs still waiting (single-threaded WASM, no interlocking needed).</summary>
    private async Task WaitForJobSlotAsync(GenerationJob job)
    {
        while (_runningJobs >= Math.Clamp(Project.Generation.MaxConcurrentJobs, 1, 5))
        {
            await Task.Delay(300, job.Cts.Token);
        }
        _runningJobs++;
    }

    private static readonly int[] RetryDelaysSeconds = [5, 15, 45];

    /// <summary>Rate limits (429), server errors (5xx) and HttpClient timeouts retry with
    /// escalating backoff; user cancellation is checked separately and never retried.</summary>
    private static bool IsTransientApiError(Exception ex) =>
        ex is TaskCanceledException
        || (ex is HttpRequestException http &&
            (http.Message.Contains(" 429") || http.Message.Contains(" 500") ||
             http.Message.Contains(" 502") || http.Message.Contains(" 503") ||
             http.Message.Contains(" 504") ||
             http.Message.Contains("RESOURCE_EXHAUSTED") ||
             http.Message.Contains("UNAVAILABLE")));

    private async Task<byte[]> GenerateWithRetryAsync(
        GenerationJob job, string prompt, IReadOnlyList<byte[]> images)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var modelId = Project.Generation.ModelId;
                if (OpenAiImageClient.IsOpenAiModel(modelId))
                {
                    // The Effort level rides the OpenAI quality parameter.
                    return await openai.GenerateImageAsync(
                        OpenAiApiKey, modelId, prompt, images,
                        OpenAiImageClient.SizeFor(modelId, job.Manifest.CanvasWidth),
                        EffectiveThinkingLevel is { Length: > 0 } quality ? quality : null,
                        job.Cts.Token);
                }
                return await gemini.GenerateImageAsync(
                    ApiKey, modelId, prompt, images,
                    ApiSizeFor(job.Manifest.CanvasWidth),
                    job.TopP,
                    EffectiveThinkingLevel,
                    job.Cts.Token);
            }
            catch (Exception ex) when (attempt < RetryDelaysSeconds.Length &&
                                       !job.Cts.IsCancellationRequested &&
                                       IsTransientApiError(ex))
            {
                var delay = RetryDelaysSeconds[attempt];
                SetStatus($"'{job.Group.Name}' {job.Kind}: transient API error " +
                          $"({FirstLine(ex.Message)}) — retry {attempt + 1}/" +
                          $"{RetryDelaysSeconds.Length} in {delay}s.");
                await Task.Delay(delay * 1000, job.Cts.Token);
            }
        }
    }

    private async Task RunGenerationAsync(GenerationJob job, string prompt, byte[] inputPng)
    {
        var holdingSlot = false;
        try
        {
            await WaitForJobSlotAsync(job);
            holdingSlot = true;
            job.State = JobState.Running;
            Notify();
            var resultPng = await GenerateWithRetryAsync(
                job, prompt, await BuildGenerationImagesAsync(job.Group, inputPng));
            job.SheetFile = await ArchiveRawSheetAsync(
                job.Kind == "revise" ? "rev" : "gen", resultPng, job.Group.Name) ?? job.SheetFile;
            await AttachSheetPngAsync(job, resultPng);
            job.State = JobState.AwaitingReview;
            SetStatus($"'{job.Group.Name}' {job.Kind} ready for review " +
                      $"({job.SheetWidth}x{job.SheetHeight}).");
        }
        catch (OperationCanceledException)
        {
            job.State = JobState.Cancelled;
            SetStatus($"'{job.Group.Name}' {job.Kind} cancelled" +
                      (job.Countdown > 0 ? " before the API call — no credits used." : "."));
            _ = AutoCloseCancelToastAsync(job);
        }
        catch (Exception ex)
        {
            job.State = JobState.Failed;
            job.Error = ex.Message;
            SetStatus($"'{job.Group.Name}' {job.Kind} failed: {FirstLine(ex.Message)}");
        }
        finally
        {
            if (holdingSlot)
            {
                _runningJobs--;
            }
        }
        Notify();
    }

    /// <summary>Store a new sheet on a job: raw bytes + header dimensions + a browser-built
    /// preview. No pixel decode — that happens lazily via EnsureJobSheetAsync.</summary>
    private async Task AttachSheetPngAsync(GenerationJob job, byte[] png)
    {
        job.RawPng = png;
        job.Sheet = null;
        var (width, height) = await codec.PngSizeAsync(png);
        job.SheetWidth = width;
        job.SheetHeight = height;
        job.PreviewUrl = await codec.PreviewDataUrlAsync(png, 1600);
    }

    /// <summary>Materialize decoded pixels for slice/composite operations.</summary>
    public async Task<RgbaImage> EnsureJobSheetAsync(GenerationJob job)
    {
        return job.Sheet ??= await codec.DecodePngAsync(
            job.RawPng ?? throw new InvalidOperationException("Job has no sheet."));
    }

    /// <summary>Region revision on a job's sheet, run in the background. Each color-labeled
    /// block carries its own instruction; the revised output is composited back strictly
    /// inside the union of all marked regions.</summary>
    public async Task ReviseJobAsync(GenerationJob job)
    {
        var blocks = job.Blocks
            .Where(b => b.Regions.Count > 0 && !string.IsNullOrWhiteSpace(b.Prompt))
            .ToList();
        if (job.RawPng is null || blocks.Count == 0)
        {
            SetStatus("Mark regions and give each colored set an instruction first.");
            return;
        }
        if (string.IsNullOrWhiteSpace(ActiveProviderKey))
        {
            SetStatus(UsesOpenAi
                ? "Set your OpenAI API key first (Settings → Model API)."
                : "Set your Gemini API key first (Settings → Model API).");
            return;
        }
        var originalPng = job.RawPng;
        var colored = blocks
            .SelectMany(b => b.Regions.Select(r => (r, b.ColorIndex)))
            .ToList();
        // Outlines are stroked browser-side onto the PNG — no decode, no big marshal.
        var annotatedPng = await codec.AnnotatePngAsync(originalPng,
            colored.Select(c => (c.r.X, c.r.Y, c.r.Width, c.r.Height, AnnotationColors[c.ColorIndex])));
        // Re-arm: a job whose previous revise was cancelled carries a spent token source.
        if (job.Cts.IsCancellationRequested)
        {
            job.Cts = new CancellationTokenSource();
        }
        job.CancelRequested = false;
        job.NotificationDismissed = false;
        job.State = JobState.Queued;
        SetStatus($"Revision starting for '{job.Group.Name}' ({blocks.Count} instruction set(s)).");
        Notify();
        var holdingReviseSlot = false;
        try
        {
            await ArmCountdownAsync(job);
            await WaitForJobSlotAsync(job);
            holdingReviseSlot = true;
            job.State = JobState.Running;
            Notify();
            // Revisions keep the lean attachment set (annotated sheet + style refs); the
            // identity references apply to full generations.
            var revisionImages = new List<byte[]> { annotatedPng };
            var styleRefClauses = new List<string>();
            foreach (var reference in ActiveStyleRefs())
            {
                if (await workspace.ReadAsync(reference.File) is { } refPng)
                {
                    revisionImages.Add(refPng);
                    styleRefClauses.Add(
                        $"Image {revisionImages.Count} is a style reference — " +
                        (reference.Context is { Length: > 0 } context
                            ? context
                            : "match its art direction while revising") + ".");
                }
            }
            var prompt =
                "Revise ONLY the areas outlined in colored boxes; keep every other pixel " +
                "exactly the same. Remove all colored outlines from your output. Keep the " +
                "exact canvas size and framing. " +
                string.Join(" ", blocks.Select(b =>
                    $"Areas outlined in {RevisionTools.ColorNames[b.ColorIndex]}: {b.Prompt.Trim()}.")) +
                (styleRefClauses.Count > 0
                    ? " Image 1 is the sheet to revise. " + string.Join(" ", styleRefClauses) +
                      " Use them as context only; do not copy their subject matter, " +
                      "characters, or layout into your output."
                    : "");
            job.Prompts.Add(prompt);
            var resultPng = await GenerateWithRetryAsync(job, prompt, revisionImages);
            job.SheetFile = await ArchiveRawSheetAsync("rev", resultPng, job.Group.Name)
                ?? job.SheetFile;
            // Composite needs real pixels for both sheets — the one deliberately heavy step
            // on the revision path (decode ×2, region blend, re-encode).
            var originalSheet = await codec.DecodePngAsync(originalPng);
            var revised = await codec.DecodePngAsync(resultPng);
            var composite = RevisionTools.CompositeRegions(
                originalSheet, revised, colored.Select(c => c.r).ToList());
            await AttachSheetPngAsync(job, await codec.EncodePngAsync(composite));
            job.Sheet = composite;
            job.Blocks.Clear();
            SetStatus($"'{job.Group.Name}' revision composited into the marked regions.");
        }
        catch (OperationCanceledException)
        {
            // A cancelled revision just returns to reviewing the unchanged sheet.
            SetStatus($"'{job.Group.Name}' revision cancelled — sheet unchanged.");
        }
        catch (Exception ex)
        {
            SetStatus($"'{job.Group.Name}' revision failed: {FirstLine(ex.Message)}");
        }
        if (holdingReviseSlot)
        {
            _runningJobs--;
        }
        job.CancelRequested = false;
        job.State = JobState.AwaitingReview;
        Notify();
    }

    // ---- Workspace ----

    public async Task OpenWorkspaceAsync()
    {
        if (!await workspace.PickAsync())
        {
            SetStatus("Workspace selection cancelled (or the browser lacks the File System Access API).",
                kind: "Errors");
            return;
        }
        await VerifyWorkspaceWritableAsync();
        await LoadFromWorkspaceAsync();
    }

    /// <summary>Probe the connected workspace with a real write; a read-only handle gets
    /// flagged immediately instead of crashing the first save.</summary>
    private async Task VerifyWorkspaceWritableAsync()
    {
        if (!await workspace.ProbeWriteAsync())
        {
            SetStatus($"⚠ Workspace '{workspace.Name}' is connected but the browser refused a " +
                      "write. Persistence is disabled — close the workspace and re-open it (a " +
                      "freshly picked folder usually regains write access), or run the app in " +
                      "Chrome/Edge.");
        }
    }

    /// <summary>Close the current workspace: flush a final save, then return the app to the
    /// blank slate. Opening a different folder is done via Open workspace afterwards.</summary>
    public async Task CloseWorkspaceAsync()
    {
        _autoSaveCts?.Cancel();
        if (workspace.IsOpen && !workspace.WritesBlocked)
        {
            await SaveProjectToWorkspaceAsync();
        }
        var name = workspace.Name;
        workspace.Close();
        ResetSessionState();
        SetStatus($"Workspace '{name}' closed. Open a workspace to continue.");
    }

    private void ResetSessionState()
    {
        Archive = null;
        _sourceBytes = null;
        TileKeys.Clear();
        _originals.Clear();
        _redraws.Clear();
        _derivedDarks.Clear();
        _urlCache.Clear();
        _urlPending.Clear();
        Selection.Clear();
        ActiveGroup = null;
        Project = new Project();
        Jobs.Clear();
        OpenTabs.Clear();
        ActiveJob = null;
    }

    public async Task ReconnectWorkspaceAsync(bool interactive)
    {
        if (await workspace.RestoreAsync(interactive))
        {
            await VerifyWorkspaceWritableAsync();
            await LoadFromWorkspaceAsync();
        }
        else if (interactive)
        {
            SetStatus("Workspace permission was not granted.");
        }
        else if (await workspace.HasStoredHandleAsync())
        {
            SetStatus("A workspace is remembered — click 'Reconnect workspace' to re-grant access.");
        }
    }

    /// <summary>Restore a session from the workspace folder: project.json (which names the
    /// game), then the archived game data and the redrawn tiles.</summary>
    private async Task LoadFromWorkspaceAsync()
    {
        SetStatus($"Opening workspace '{workspace.Name}'…");
        var projectJson = await workspace.ReadAsync("project.json");
        if (projectJson is not null)
        {
            Project = JsonSerializer.Deserialize<Project>(projectJson, JsonOptions) ?? new Project();
            // Before anything reads a tile id: the whole project is keyed by them, and the
            // game owns their spelling. Idempotent, so it costs nothing on a current file.
            var migrated = TileIdMigration.Apply(Project, Game);
            if (migrated > 0)
            {
                SetStatus($"Migrated {migrated} tile ids to {Game.Name}'s current scheme.",
                    "Workspace");
            }
            ActiveGroup = Project.Groups.FirstOrDefault();
            RestoreJobHistory();
        }
        // The reference table is per-game, so a workspace on a different game needs a reload.
        _metadataTask = null;
        var sources = await workspace.ListAsync("source");
        if (Archive is null && sources.Length > 0)
        {
            var sourceBytes = await workspace.ReadAsync($"source/{sources[0]}");
            if (sourceBytes is not null)
            {
                try
                {
                    LoadGameData(sourceBytes, sources[0]);
                }
                catch (Exception ex)
                {
                    SetStatus($"⚠ '{sources[0]}' is not {Game.Name} game data " +
                              $"({FirstLine(ex.Message)}) — check the workspace's game in " +
                              "Settings → Game.");
                }
            }
        }
        var keyBytes = await workspace.ReadAsync(ApiKeyFileName);
        if (keyBytes is { Length: > 0 })
        {
            _apiKey = System.Text.Encoding.UTF8.GetString(keyBytes).Trim();
        }
        var openAiKeyBytes = await workspace.ReadAsync(OpenAiKeyFileName);
        if (openAiKeyBytes is { Length: > 0 })
        {
            _openAiApiKey = System.Text.Encoding.UTF8.GetString(openAiKeyBytes).Trim();
        }
        // Legacy single style reference → first entry of the ordered StyleRefs list,
        // carrying the clause that used to be hard-coded so behavior is unchanged.
        if (Project.Generation.StyleRefs.Count == 0 &&
            await workspace.ReadAsync(StyleReferenceFileName) is { Length: > 0 })
        {
            Project.Generation.StyleRefs.Add(new StyleRefInfo
            {
                File = StyleReferenceFileName,
                Context = "this game's established art direction: match its rendering style, " +
                          "line weight, color treatment and shading exactly — but it is " +
                          "context only; do not copy its subject matter, characters, or " +
                          "layout into your output",
            });
            SetStatus("Legacy style reference migrated into the Style drawer.");
        }
        var redrawCount = 0;
        foreach (var file in await workspace.ListAsync("redraws"))
        {
            var png = await workspace.ReadAsync($"redraws/{file}");
            if (png is null || KeyForFileName(file) is not { } key)
            {
                continue;
            }
            _redraws[key] = await codec.DecodePngAsync(png);
            InvalidateUrl(key, redraw: true);
            redrawCount++;
        }
        _derivedDarks.Clear();
        await EnsureItemsMigratedAsync();
        SetStatus($"Workspace '{workspace.Name}': {Project.Items.Count} items, " +
                  $"{Project.Groups.Count} groups, {redrawCount} redraws loaded.");
    }

    /// <summary>Tile id for a file in <c>redraws/</c>, resolved through the map the archive
    /// built. Null for a file no loaded tile claims — a leftover from another game or an
    /// older id scheme, which is skipped rather than guessed at.</summary>
    private string? KeyForFileName(string fileName) => _fileToTile.GetValueOrDefault(fileName);

    /// <summary>Debounced project.json auto-save; a no-op without an open workspace.</summary>
    public void ScheduleAutoSave()
    {
        if (!workspace.IsOpen)
        {
            return;
        }
        _autoSaveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _autoSaveCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500, cts.Token);
                await SaveProjectToWorkspaceAsync();
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public async Task SaveProjectToWorkspaceAsync()
    {
        if (workspace.WritesBlocked)
        {
            return; // probed read-only; a status warning was already shown
        }
        SyncJobHistory();
        try
        {
            // Redraw art lives beside project.json as PNG files.
            await workspace.WriteAsync("project.json", JsonSerializer.SerializeToUtf8Bytes(Project, JsonOptions));
        }
        catch (Exception ex)
        {
            await workspace.ProbeWriteAsync();
            SetStatus($"⚠ Could not write project.json to '{workspace.Name}': {FirstLine(ex.Message)} " +
                      "— close and re-open the workspace, or run the app in Chrome/Edge.");
        }
    }

    private static string FirstLine(string s)
    {
        var index = s.IndexOf('\n');
        return index < 0 ? s : s[..index];
    }

    /// <summary>Apply the active game's light/dark pairing convention to every tile the user
    /// has not classified. Tiles the game has no opinion about are left alone.</summary>
    public void AutoPairTiles()
    {
        foreach (var key in TileKeys)
        {
            var meta = GetMeta(key);
            if (meta.Role != PairRole.Unclassified ||
                Game.AutoPairRole(key) is not { } role)
            {
                continue;
            }
            meta.Role = role;
            meta.LightSourceKey = role == PairRole.Light ? null : DefaultLightSource(key);
            meta.Category = meta.Category == Categories.Uncategorized
                ? Game.AutoPairCategory
                : meta.Category;
        }
        SetStatus($"Applied {Game.AutoPairDescription} to unclassified tiles — " +
                  "fix the exceptions by hand.");
    }

    private static string SafeName(string name) =>
        string.Join("-", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries))
            .Replace(' ', '-');
}
