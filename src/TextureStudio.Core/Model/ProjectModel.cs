using TextureStudio.Core.Imaging;

namespace TextureStudio.Core.Model;

public enum TileKind { Wall, Sprite }

/// <summary>Stable identity for a tile: "wall:12" or "sprite:107". Used as dictionary keys
/// and in sheet manifests.</summary>
public readonly record struct TileRef(TileKind Kind, int Index)
{
    public string Key => $"{(Kind == TileKind.Wall ? "wall" : "sprite")}:{Index}";

    public static TileRef Parse(string key)
    {
        var parts = key.Split(':');
        var kind = parts[0] == "wall" ? TileKind.Wall : TileKind.Sprite;
        return new TileRef(kind, int.Parse(parts[1]));
    }

    public override string ToString() => Key;
}

/// <summary>How a tile participates in the engine's light/dark convention.</summary>
public enum PairRole
{
    Unclassified,
    /// <summary>Light variant (north/south faces); a dark sibling is derived from it.</summary>
    Light,
    /// <summary>Dark variant whose art is generated from its light source tile.</summary>
    DerivedDark,
    /// <summary>Dark variant with its own art (the "alternate light art" case) —
    /// LightSourceKey points at the tile whose redraw is darkened for it, which may
    /// not be its index-adjacent sibling.</summary>
    AlternateDark,
    /// <summary>Standalone art: murals, flats, orientation-dependent walls, sprites.</summary>
    Unique,
}

public sealed class TileMeta
{
    public string Name { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Category { get; set; } = Categories.Uncategorized;
    public PairRole Role { get; set; } = PairRole.Unclassified;
    /// <summary>For DerivedDark/AlternateDark: key of the light tile whose (redrawn) art is
    /// darkened to produce this tile. Defaults to the even sibling for odd wall chunks.</summary>
    public string? LightSourceKey { get; set; }
    /// <summary>Optional stand-in for Purpose in generation prompts only — reword content
    /// that trips image-safety filters (gore, bodily fluids) without touching the curated
    /// metadata. Empty/null means the real Purpose (or its placeholder) is sent.</summary>
    public string? GenerationAlias { get; set; }
    /// <summary>Id of this tile's active redraw version (see Project.TileVersions).</summary>
    public string? ActiveVersionId { get; set; }
}

/// <summary>One persisted redraw version of a single tile — every applied slice records
/// one per included tile, so frames can be flipped between runs independently.</summary>
public sealed class TileVersionInfo
{
    public required string Id { get; set; }
    /// <summary>generate | import | revise</summary>
    public string Kind { get; set; } = "generate";
    public string GroupName { get; set; } = "";
    public DateTime Created { get; set; }
    /// <summary>Workspace-relative PNG path.</summary>
    public required string File { get; set; }
    /// <summary>Placement baked into this version (for re-use and "match applied").</summary>
    public double Scale { get; set; } = 1;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Rotation { get; set; }
}

public static class Categories
{
    public const string Uncategorized = "Uncategorized";

    public static readonly string[] Defaults =
    {
        "Surfaces", "Flats", "Doors", "Characters", "Weapons", "Items",
        "Decorations", "Effects", Uncategorized,
    };
}

/// <summary>One accepted slice result for a group: its tiles live in the workspace under
/// revisions/&lt;group&gt;/&lt;id&gt;/ and can be re-activated at any time.</summary>
public sealed class GroupRevisionInfo
{
    public string Id { get; set; } = "";
    /// <summary>generate | import | revise</summary>
    public string Kind { get; set; } = "";
    public DateTime Created { get; set; }
    public List<string> TileKeys { get; set; } = [];
}

/// <summary>The middle layer of the Category → Item → Frame model: a named thing whose
/// tiles are its frames (animation frames, rotations, or state variants). Name and Category
/// live here; per-frame Purpose/Role stay in TileMeta.</summary>
public sealed class TileItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>Empty means "show the engine name as a grey placeholder".</summary>
    public string Name { get; set; } = "";
    public string Category { get; set; } = Categories.Uncategorized;
    /// <summary>Frames form an animation — the item's grid tile cycles through them.</summary>
    public bool IsAnimation { get; set; }
    /// <summary>Optional stand-in for Name in generation prompts only — reword content that
    /// trips image-safety filters without touching the curated metadata.</summary>
    public string? GenerationAlias { get; set; }
    /// <summary>Ordered frame tile keys.</summary>
    public List<string> TileKeys { get; set; } = [];
}

public sealed class TileGroup
{
    public string Name { get; set; } = "";
    /// <summary>Ordered tile keys; order is the platter arrangement (row-major).</summary>
    public List<string> TileKeys { get; set; } = [];
    public List<GroupRevisionInfo> Revisions { get; set; } = [];
    public string? ActiveRevisionId { get; set; }
    public int Columns { get; set; } = 4;
    public int TilePx { get; set; } = 256;
    public int GutterPx { get; set; } = 16;
    /// <summary>Legacy whole-group seamless flag — migrated to <see cref="SeamlessRuns"/>
    /// on load and kept only so old project files deserialize.</summary>
    public bool Seamless { get; set; }
    /// <summary>Seamless runs: each entry is an ordered, contiguous slice of
    /// <see cref="TileKeys"/> whose cells are butted edge-to-edge on the sheet.</summary>
    public List<List<string>> SeamlessRuns { get; set; } = [];
    // API-generated images carry no visible watermark, so no corner is reserved by default;
    // set one on a group when using the copy-paste app workflow instead.
    public ReservedCorner Reserved { get; set; } = ReservedCorner.None;
    public string PromptTemplate { get; set; } = "";
    /// <summary>Manifest of the last exported sheet — imports are sliced against this.</summary>
    public SheetManifest? LastExport { get; set; }
    /// <summary>Workspace-relative path of this group's character reference image — any
    /// image showing what the subject looks like (e.g. a portrait), attached to
    /// generations with a match-this-identity clause.</summary>
    public string? CharacterRefFile { get; set; }
    /// <summary>Attach the approved-frames identity reference on this group's generations
    /// (AND-ed with the global setting).</summary>
    public bool UseApprovedRef { get; set; } = true;
    /// <summary>Frames excluded from this group's approved-frames reference grid.</summary>
    public List<string> ApprovedRefExcluded { get; set; } = [];
}

/// <summary>One style reference image: a workspace-relative file plus the author's
/// description of how the model should use it.</summary>
public sealed class StyleRefInfo
{
    public required string File { get; set; }
    public string Context { get; set; } = "";
}

public sealed class GenerationSettings
{
    public string ModelId { get; set; } = "gemini-3-pro-image";
    public string ImageSize { get; set; } = "4K";
    /// <summary>Ordered style reference images attached to generation and revision calls,
    /// each with author-supplied context describing how it should be used.</summary>
    public List<StyleRefInfo> StyleRefs { get; set; } = [];
    /// <summary>Attach a grid of the sheet's items' already-approved redrawn frames as an
    /// identity reference, so new generations align with accepted art.</summary>
    /// <summary>Generate this many candidate sheets per click (1–4). Above 1, each call
    /// uses a different top-p so the takes genuinely diverge.</summary>
    public int VersionCount { get; set; } = 1;
    /// <summary>How many jobs may call the API at once; queued jobs wait for a slot.
    /// 1 = strictly serial (rate-limit friendly).</summary>
    public int MaxConcurrentJobs { get; set; } = 1;
    /// <summary>Cell size composed into outgoing generation sheets (project-wide).</summary>
    public int TilePx { get; set; } = 512;
    /// <summary>Resolution each cell is cut back to when slicing results — normally equal
    /// to <see cref="TilePx"/> so redraws round-trip 1:1.</summary>
    public int SliceTargetPx { get; set; } = 512;
    /// <summary>Model thinking level (minimal/low/medium/high); empty = API default.</summary>
    public string ThinkingLevel { get; set; } = "";
    public string StylePrompt { get; set; } =
        "Redraw this sprite sheet in a clean cel-shaded cartoon style with confident line work " +
        "and flat colors.";
}

public sealed class Project
{
    public int Version { get; set; } = 1;
    public string GameName { get; set; } = "";
    public Dictionary<string, TileMeta> Meta { get; set; } = [];
    public List<TileItem> Items { get; set; } = [];
    public List<TileGroup> Groups { get; set; } = [];
    /// <summary>Per-tile redraw version history (tile key → versions, oldest first).</summary>
    public Dictionary<string, List<TileVersionInfo>> TileVersions { get; set; } = [];
    public DarkParams DarkParams { get; set; } = DarkParams.Default;
    /// <summary>Inverse transform: brightens Alternate Dark originals so they enter
    /// generation sheets as light art (the model then redraws them light, and the dark
    /// transform re-darkens the result for display/pack).</summary>
    public DarkParams LightenParams { get; set; } = new(1.55, 0.85, 1.00);
    public GenerationSettings Generation { get; set; } = new();
    /// <summary>Persisted jobs rail, newest first, capped at 50 — rebuilt from the live
    /// job list on every save so dismissed jobs drop out.</summary>
    public List<JobRecord> JobHistory { get; set; } = [];
    /// <summary>What was open when the app last saved — restored on load.</summary>
    public UiState Ui { get; set; } = new();
}

/// <summary>Snapshot of one background job for cross-session persistence. The sheet
/// itself lives in the generations/ archive; <see cref="SheetFile"/> points at it so a
/// restored job can be reopened for review.</summary>
public sealed class JobRecord
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string GroupName { get; set; } = "";
    public DateTime Created { get; set; }
    /// <summary>JobState name; in-flight states are mapped to "Interrupted" on restore.</summary>
    public string State { get; set; } = "";
    public string? Error { get; set; }
    public string? VariantLabel { get; set; }
    public string? SheetFile { get; set; }
    public SheetManifest? Manifest { get; set; }
    public List<string> Prompts { get; set; } = [];
    /// <summary>Per-group ordinal so multiple jobs for one group are tellable apart.</summary>
    public int Number { get; set; }
    /// <summary>Placement-tuning snapshot (state == Placing): keyed art lives as PNGs under
    /// jobs/&lt;id&gt;/, transforms here — so Adjust &amp; Apply survives a refresh unsliced.</summary>
    public List<PlacementRecord> Placements { get; set; } = [];
}

/// <summary>One tile of a persisted placement-tuning session.</summary>
public sealed class PlacementRecord
{
    public string TileKey { get; set; } = "";
    public string File { get; set; } = "";
    public bool IsSprite { get; set; }
    public string KeyMode { get; set; } = "-";
    public bool UsedFallback { get; set; }
    public double WrapError { get; set; }
    public bool Included { get; set; } = true;
    public double Scale { get; set; } = 1;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Rotation { get; set; }
    public double AnchorX { get; set; } = 0.5;
    public double AnchorY { get; set; } = 0.5;
    public double BoundsX { get; set; }
    public double BoundsY { get; set; }
    public double BoundsW { get; set; } = 1;
    public double BoundsH { get; set; } = 1;
    public double AutoScale { get; set; } = 1;
    public double AutoOffsetX { get; set; }
    public double AutoOffsetY { get; set; }
    public double AutoRotation { get; set; }
}

/// <summary>Session UI state persisted with the project: what was open when the app
/// closed, so a reload puts the user back where they were.</summary>
public sealed class UiState
{
    public string? ActiveGroupName { get; set; }
    public List<string> OpenTabJobIds { get; set; } = [];
    public string? ActiveTabJobId { get; set; }
    /// <summary>"Wall" or "Sprite".</summary>
    public string ItemsKind { get; set; } = "Sprite";
    public string ItemsCategory { get; set; } = "";
    public bool ShowRedraw { get; set; }
    /// <summary>Notification kinds the user has hidden from the banner surface.</summary>
    public List<string> HiddenNotifications { get; set; } = [];
}
