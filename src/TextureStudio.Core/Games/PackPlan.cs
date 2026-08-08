namespace TextureStudio.Core.Games;

/// <summary>What an installable pack contains. Planning is pure: the game decides which files
/// exist, where they go and what has to happen to the art, while the caller does the reading,
/// transforming and writing. That is what lets the same plan drive the Pack CLI and the
/// browser, which have nothing else in common.</summary>
/// <param name="Entries">One per file the pack will contain.</param>
/// <param name="SkippedTileKeys">Tiles that could have produced a file but had no usable art
/// — reported rather than silently dropped, so a half-finished pack is obvious.</param>
public sealed record PackPlan(
    IReadOnlyList<PackEntry> Entries,
    IReadOnlyList<string> SkippedTileKeys)
{
    public static readonly PackPlan Empty = new([], []);

    /// <summary>How many entries are synthesized rather than copied — worth reporting,
    /// because they are art the user never drew.</summary>
    public int TransformedCount => Entries.Count(entry => entry.Transform != PackTransform.None);
}

/// <summary>One file in a pack.</summary>
/// <param name="Path">Pack-relative and forward-slashed, e.g. "aog/wall_00000013.png".</param>
/// <param name="SourceTileKey">Whose redraw supplies the pixels. Not always the tile the file
/// is for: a derived dark variant has no art of its own and is made from its light source's.</param>
/// <param name="Transform">What happens to those pixels on the way out.</param>
public sealed record PackEntry(string Path, string SourceTileKey, PackTransform Transform);

/// <summary>How a source redraw becomes a packed texture.</summary>
public enum PackTransform
{
    /// <summary>Write the redraw as it is.</summary>
    None,

    /// <summary>Darken it with the project's <see cref="Model.DarkParams"/>. All redraws are
    /// drawn light by pipeline convention, so every dark variant is synthesized here.</summary>
    Darken,
}

/// <summary>Where to get the source port and how to point it at a finished pack.
///
/// Links rather than spelled-out commands: the command line differs per platform and per
/// release and goes stale in this repo, whereas the port's own documentation does not.</summary>
/// <param name="PortName">The source port that consumes the pack, e.g. "BStone".</param>
/// <param name="PortUrl">Where to download it.</param>
/// <param name="PackInstructionsUrl">The port's own documentation for loading a pack.</param>
/// <param name="ExtraStep">A step the port's mod documentation does not cover but the pack
/// needs anyway, phrased as an instruction; null when there is none.</param>
/// <param name="ExtraStepUrl">Documentation for <paramref name="ExtraStep"/>, if any.</param>
public sealed record InstallGuide(
    string PortName,
    string PortUrl,
    string PackInstructionsUrl,
    string? ExtraStep = null,
    string? ExtraStepUrl = null);
