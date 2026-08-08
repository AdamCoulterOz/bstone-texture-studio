namespace TextureStudio.Core.Model;

/// <summary>Builds the item layer from the tiles themselves: tiles sharing a non-empty
/// (Category, Name) become one item; unnamed tiles group by an engine-derived family key
/// (actor prefix) so effect/actor families arrive as single items with placeholder names.</summary>
public static class ItemLayerBuilder
{
    /// <summary>familyKey resolves a tile key to a grouping key for unnamed tiles (typically
    /// the sprite constant's actor prefix); return null for "stands alone".</summary>
    public static List<TileItem> Build(
        IReadOnlyList<string> allTileKeys,
        IReadOnlyDictionary<string, TileMeta> meta,
        Func<string, string?> familyKey)
    {
        var items = new List<TileItem>();
        var byIdentity = new Dictionary<string, TileItem>();
        foreach (var key in allTileKeys)
        {
            var m = meta.GetValueOrDefault(key);
            var name = m?.Name ?? "";
            var category = m?.Category ?? Categories.Uncategorized;
            string identity;
            if (name.Length > 0)
            {
                identity = $"n|{category}|{name}";
            }
            else if (familyKey(key) is { Length: > 0 } family)
            {
                identity = $"f|{category}|{family}";
            }
            else
            {
                identity = $"s|{key}";
            }
            if (!byIdentity.TryGetValue(identity, out var item))
            {
                item = new TileItem { Name = name, Category = category };
                byIdentity[identity] = item;
                items.Add(item);
            }
            item.TileKeys.Add(key);
        }
        foreach (var item in items)
        {
            item.IsAnimation = item.TileKeys.Count > 1;
        }
        return items;
    }
}
