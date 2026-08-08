using TextureStudio.Core.Games;

namespace TextureStudio.Core.Model;

/// <summary>Rewrites tile ids persisted by an older release, everywhere they appear.
///
/// Tile ids are the spine of a project: they key the metadata dictionary, the version index,
/// every group's cell list, every seamless run, and every archived job manifest. Miss one
/// place and art silently detaches from the curation describing it — years of work in a real
/// workspace — so this walks all of them explicitly rather than trusting a sweep.
///
/// The rewrite itself is the game's (<see cref="IGame.MigrateTileId"/>) and is idempotent, so
/// this runs on every load.</summary>
public static class TileIdMigration
{
    /// <summary>Migrate every id in the project in place. Returns how many distinct ids
    /// changed, so a load can report that it happened.</summary>
    public static int Apply(Project project, IGame game)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);

        string Map(string id)
        {
            var migrated = game.MigrateTileId(id);
            if (!string.Equals(migrated, id, StringComparison.Ordinal))
            {
                changed.Add(id);
            }
            return migrated;
        }

        void MapList(List<string> ids)
        {
            for (var i = 0; i < ids.Count; i++)
            {
                ids[i] = Map(ids[i]);
            }
        }

        project.Meta = Remap(project.Meta, Map);
        foreach (var meta in project.Meta.Values)
        {
            if (meta.LightSourceKey is { Length: > 0 } light)
            {
                meta.LightSourceKey = Map(light);
            }
        }

        project.TileVersions = Remap(project.TileVersions, Map);

        foreach (var item in project.Items)
        {
            MapList(item.TileKeys);
        }

        foreach (var group in project.Groups)
        {
            MapList(group.TileKeys);
            MapList(group.ApprovedRefExcluded);
            foreach (var run in group.SeamlessRuns)
            {
                MapList(run);
            }
            foreach (var revision in group.Revisions)
            {
                MapList(revision.TileKeys);
            }
            group.LastExport = MapManifest(group.LastExport, Map);
        }

        foreach (var job in project.JobHistory)
        {
            job.Manifest = MapManifest(job.Manifest, Map);
            foreach (var placement in job.Placements)
            {
                placement.TileKey = Map(placement.TileKey);
            }
        }

        return changed.Count;
    }

    /// <summary>Rebuild a tile-keyed dictionary under migrated keys, keeping the first value
    /// if two old ids ever collapse onto one (they should not, but losing the entry outright
    /// would be worse than keeping one).</summary>
    private static Dictionary<string, TValue> Remap<TValue>(
        Dictionary<string, TValue> source, Func<string, string> map)
    {
        var result = new Dictionary<string, TValue>(source.Count, StringComparer.Ordinal);
        foreach (var (key, value) in source)
        {
            result.TryAdd(map(key), value);
        }
        return result;
    }

    /// <summary>Sheet manifests are records, so their cells are rebuilt rather than mutated.</summary>
    private static Imaging.SheetManifest? MapManifest(
        Imaging.SheetManifest? manifest, Func<string, string> map) =>
        manifest is null
            ? null
            : manifest with
            {
                Cells = [.. manifest.Cells.Select(cell => cell with { TileKey = map(cell.TileKey) })],
            };
}
