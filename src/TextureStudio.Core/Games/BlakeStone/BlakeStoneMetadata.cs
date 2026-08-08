using System.Text.Json;
using System.Text.RegularExpressions;
using TextureStudio.Core.Model;

namespace TextureStudio.Core.Games.BlakeStone;

/// <summary>Engine reference data for Blake Stone's sprites, extracted from the bstone
/// source: the sprite constant, the statinfo name, the in-game label and the object type,
/// keyed by edition then sprite index. Walls have no reference data.</summary>
public sealed partial class BlakeStoneMetadata : IGameMetadata
{
    /// <summary>Shape of the shipped table: edition id → sprite index → entry.</summary>
    private sealed record Entry(string C, string? N, string? T, string? U);

    private static readonly JsonSerializerOptions TableOptions =
        new() { PropertyNameCaseInsensitive = true };

    private Dictionary<string, Dictionary<string, Entry>> _table = [];

    public string AssetPath => "canonical-sprites.json";

    public bool IsLoaded { get; private set; }

    public void Load(byte[] json)
    {
        try
        {
            _table = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Entry>>>(
                json, TableOptions) ?? [];
        }
        catch (JsonException)
        {
            _table = []; // reference data is a nicety; the studio works without it
        }
        IsLoaded = true;
    }

    public CanonicalTile? Lookup(GameEdition edition, TileRef tile)
    {
        if (Find(edition, tile) is not { } entry)
        {
            return null;
        }
        return new CanonicalTile(entry.C, entry.N, entry.U, TypeLabel(entry.T), FrameLabel(entry.C));
    }

    /// <summary>Actor-family grouping key for unnamed sprites: the constant with its
    /// animation suffix stripped (SPR_GREEN_OOZE2 → GREEN_OOZE); null for walls and
    /// statics, which stand alone unless the user names them.</summary>
    public string? ActorFamily(GameEdition edition, TileRef tile)
    {
        if (Find(edition, tile) is not { } entry)
        {
            return null;
        }
        var constant = entry.C.Replace("SPR_", "");
        if (constant.StartsWith("STAT_"))
        {
            return null;
        }
        var match = AnimationSuffix().Match(constant);
        return match.Success ? match.Groups[1].Value.TrimEnd('_') : constant;
    }

    private Entry? Find(GameEdition edition, TileRef tile) =>
        tile.Kind == TileKind.Sprite &&
        _table.TryGetValue(edition.Id, out var byIndex) &&
        byIndex.TryGetValue(tile.Index.ToString(), out var entry)
            ? entry
            : null;

    private static string? TypeLabel(string? statType) => statType switch
    {
        null => null,
        "block" => "blocking object",
        "dressing" => "walk-through dressing",
        _ when statType.StartsWith("bo_") => "pickup: " + statType[3..].Replace('_', ' '),
        _ => statType,
    };

    /// <summary>Human reading of the animation tokens in an actor sprite constant, e.g.
    /// SPR_MUTHUM1_W2_7 → "walk 2 · rotation 7/8".</summary>
    private static string? FrameLabel(string constant)
    {
        var tokens = constant.Replace("SPR_", "").Split('_');
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var rotation = i + 1 < tokens.Length && tokens[i + 1].Length == 1 &&
                           char.IsBetween(tokens[i + 1][0], '1', '8')
                ? $" · rotation {tokens[i + 1]}/8"
                : "";
            switch (token)
            {
                case "W1" or "W2" or "W3" or "W4":
                    return $"walk {token[1]}{rotation}";
                case "S" when rotation.Length > 0:
                    return $"stand{rotation}";
                case "DEAD":
                    return "dead";
                case "OUCH":
                    return "hit reaction";
            }
            foreach (var (prefix, label) in new[]
                     {
                         ("SHOOT", "shoot"), ("ATTACK", "attack"), ("PAIN", "pain"),
                         ("DIE", "death"), ("DEATH", "death"), ("EXP", "explosion"),
                     })
            {
                if (token.StartsWith(prefix))
                {
                    var frame = token[prefix.Length..];
                    return frame.Length > 0 ? $"{label} frame {frame}" : label;
                }
            }
        }
        return null;
    }

    [GeneratedRegex(
        @"^(.+?)_?(W[1-4](_[1-8])?|S_[1-8]|WALK\d|FLY\d?(_\d)?|SWING\d|SHOOT\d|ATTACK\d|" +
        @"SPIT\d(_\d)?|SPIT_EXP\d_\d|PAIN(_\d)?\d?|OUCH|DIE_?\d|DEATH\d?|DEAD(_\d)?|" +
        @"EXP\d|B\d|READY|ATK\d|EMPTY|ALERT|NORMAL|FIRE(_\d)?\d?|EGG|HATCH\d|ROAM\d|" +
        @"APPEAR\d|WARP\d|WOUNDED\d|WRIST_\d|[1-8])$")]
    private static partial Regex AnimationSuffix();
}
