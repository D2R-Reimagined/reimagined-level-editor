namespace D2RLevel.Core;

/// <summary>Finds models whose ground footprints lie close to a selection, nearest first.</summary>
public static class SelectionNeighbors
{
    /// <summary>Shortest ground-plane distance between two footprints; zero when they touch or overlap.</summary>
    public static double Gap(ModelFootprint a, ModelFootprint b)
    {
        double dx = Math.Max(0, Math.Max(a.MinX - b.MaxX, b.MinX - a.MaxX));
        double dz = Math.Max(0, Math.Max(a.MinZ - b.MaxZ, b.MinZ - a.MaxZ));
        return Math.Sqrt(dx * dx + dz * dz);
    }

    public static (T Item, double Gap)[] Find<T>(IReadOnlyList<ModelFootprint> selection, IEnumerable<(T Item, ModelFootprint Footprint)> candidates, double within, int limit)
    {
        if (selection.Count == 0 || !double.IsFinite(within) || within < 0 || limit <= 0) return [];
        // Skip anything outside the selection's combined box first so large scenes stay cheap.
        var reach = new ModelFootprint("", selection.Min(s => s.MinX) - within, selection.Min(s => s.MinZ) - within,
            selection.Max(s => s.MaxX) + within, selection.Max(s => s.MaxZ) + within);
        return candidates.Where(c => Gap(reach, c.Footprint) == 0)
            .Select(c => (c.Item, Gap: selection.Min(s => Gap(s, c.Footprint))))
            .Where(c => c.Gap <= within).OrderBy(c => c.Gap).Take(limit).ToArray();
    }
}
