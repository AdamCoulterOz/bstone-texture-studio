using TextureStudio.Core.Generation;
using TextureStudio.Core.Imaging;
using TextureStudio.Core.Model;

namespace TextureStudio.App.Services;

public enum JobState { Queued, Running, AwaitingReview, Placing, Failed, Done, Cancelled, Interrupted }

/// <summary>A background generation/import/revision for one group. Session-scoped: results
/// persist via the raw-sheet archive and, once sliced, via group revisions — the job object
/// itself is just the live workflow state shown in the Jobs rail.</summary>
public sealed class GenerationJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>generate | import | revise</summary>
    public required string Kind { get; init; }

    /// <summary>Per-group ordinal (assigned at creation, persisted) — "Goldfire #3".</summary>
    public int Number { get; init; }

    /// <summary>Title-case source label for display: Generate / Import / Revise.</summary>
    public string KindLabel => Kind.Length == 0 ? Kind
        : char.ToUpperInvariant(Kind[0]) + Kind[1..];

    public required TileGroup Group { get; init; }

    /// <summary>Manifest snapshot from compose time — slicing uses this even if the group's
    /// layout settings change while the job is in flight.</summary>
    public required SheetManifest Manifest { get; init; }

    public DateTime Created { get; init; } = DateTime.Now;
    public JobState State { get; set; } = JobState.Running;
    public string? Error { get; set; }

    /// <summary>Cancels the arming countdown or the in-flight API call. Replaced when a
    /// job is re-armed (a new revise on a job whose previous revise was cancelled).</summary>
    public CancellationTokenSource Cts { get; set; } = new();

    /// <summary>Seconds left in the arming countdown (state == Queued) — the window to
    /// cancel before the API call starts burning credits.</summary>
    public int Countdown { get; set; }

    /// <summary>Set the moment the user asks to cancel; the toast shows "cancelling…"
    /// until the work actually stops.</summary>
    public bool CancelRequested { get; set; }

    /// <summary>The bottom-right toast for this job was dismissed (×, click-through, or
    /// the 2s auto-close after a cancel).</summary>
    public bool NotificationDismissed { get; set; }

    /// <summary>Sampling top-p for this call; null = API default (single-version runs).</summary>
    public double? TopP { get; init; }

    /// <summary>Short label distinguishing multi-version siblings in the rail.</summary>
    public string? VariantLabel { get; init; }

    /// <summary>Current sheet as PNG bytes — the source of truth. Decoded pixels
    /// (<see cref="Sheet"/>) are materialized lazily only when slicing/compositing.</summary>
    public byte[]? RawPng { get; set; }

    /// <summary>Workspace-relative path of this sheet in the generations/ archive — how a
    /// job restored from history reloads its sheet for review.</summary>
    public string? SheetFile { get; set; }

    public int SheetWidth { get; set; }
    public int SheetHeight { get; set; }

    /// <summary>Downscaled data-URL preview, built browser-side and cached — job switching
    /// never re-encodes.</summary>
    public string? PreviewUrl { get; set; }

    /// <summary>Lazily-decoded pixels of <see cref="RawPng"/>; freed when the job completes.</summary>
    public RgbaImage? Sheet { get; set; }

    /// <summary>Up to four color-labeled revision sets, each with its own regions + prompt.</summary>
    public List<RevisionBlock> Blocks { get; } = [];

    /// <summary>Every prompt sent for this job, generation first then revisions — viewable
    /// in the review panel.</summary>
    public List<string> Prompts { get; } = [];

    /// <summary>Sliced+keyed cells awaiting placement tuning (state == Placing); freed when
    /// the job completes or the placement step is cancelled.</summary>
    public List<SlicePlacement>? Placements { get; set; }

    /// <summary>Persisted placement snapshot from a previous session, hydrated into
    /// <see cref="Placements"/> the first time the tab opens (no re-slice needed).</summary>
    public List<PlacementRecord>? PendingPlacements { get; set; }
}

/// <summary>One sliced cell in the placement-tuning step: the keyed image plus a live
/// transform the user can adjust over a ghost of the original before baking.</summary>
public sealed class SlicePlacement
{
    public required string TileKey { get; init; }
    public required RgbaImage Keyed { get; init; }
    public required bool IsSprite { get; init; }
    public string KeyMode { get; init; } = "-";
    public bool UsedFallback { get; init; }
    public double WrapError { get; init; }

    /// <summary>Data URL of <see cref="Keyed"/> for the tuning canvas (sprites only).</summary>
    public string? PreviewUrl { get; set; }

    /// <summary>Unticked sprites are skipped entirely at apply time — the existing redraw
    /// stays untouched and the tile is left out of the revision (partial slice).</summary>
    public bool Included { get; set; } = true;

    /// <summary>What auto-normalize would do — the initial value and the reset target.</summary>
    public SpritePlacement Auto { get; init; } = SpritePlacement.Identity;

    public double Scale { get; set; } = 1;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    /// <summary>Clockwise degrees about the placed rect's center (sprites only).</summary>
    public double Rotation { get; set; }

    /// <summary>Content center (cell fraction) — scaling pivots here so the slider doesn't
    /// walk the art away from where the user put it.</summary>
    public double AnchorX { get; init; } = 0.5;
    public double AnchorY { get; init; } = 0.5;

    /// <summary>Significant-bounds rect of the keyed art (cell fractions) — the alignment
    /// commands aim this box, so keying specks don't skew edges.</summary>
    public double BoundsX { get; init; }
    public double BoundsY { get; init; }
    public double BoundsW { get; init; } = 1;
    public double BoundsH { get; init; } = 1;

    public SpritePlacement Current => new(Scale, OffsetX, OffsetY, Rotation);

    public bool Edited =>
        Math.Abs(Scale - Auto.Scale) > 1e-6 ||
        Math.Abs(OffsetX - Auto.OffsetX) > 1e-6 ||
        Math.Abs(OffsetY - Auto.OffsetY) > 1e-6 ||
        Math.Abs(Rotation - Auto.Rotation) > 1e-6;
}

public sealed class RevisionBlock(int colorIndex)
{
    public int ColorIndex { get; } = colorIndex;
    public List<RevisionRegion> Regions { get; } = [];
    public string Prompt { get; set; } = "";
}
