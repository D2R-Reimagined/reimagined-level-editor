using System.Text.Json.Nodes;

namespace D2RLevel.Core;

/// <summary>The ground-plane box a terrain mesh covers, in HD world units.</summary>
public sealed record TerrainExtent(double MinX, double MinZ, double MaxX, double MaxZ)
{
    public double SizeX => MaxX - MinX;
    public double SizeZ => MaxZ - MinZ;
    public TerrainExtent Offset(double dx, double dz) => new(MinX + dx, MinZ + dz, MaxX + dx, MaxZ + dz);
    public bool Contains(double x, double z) => x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;

    /// <summary>Where a mesh would sit if it spans exactly the DS1 grid from its own origin, for terrain that has not been measured.</summary>
    public static TerrainExtent FromGrid(Vector3d position, int width, int height, double unitsPerTile) =>
        new(position.X, position.Z, position.X + width * unitsPerTile, position.Z + height * unitsPerTile);
}

/// <summary>Terrain copies added and removed together so the HD ground covers the whole map.</summary>
public sealed record TerrainRepair(PresetEntity Source, PresetEntity[] Removed, PresetEntity[] Added);

/// <summary>
/// Lays copies of one terrain mesh on its own grid: every copy sits a whole number of mesh widths and depths
/// from the source, so copies meet edge to edge with no gap or overlap, however the mesh lines up with the DS1.
/// </summary>
public static class TerrainTiling
{
    /// <summary>Fraction of a mesh width that still counts as lined up with the source.</summary>
    public const double Tolerance = .01;

    /// <summary>
    /// Offsets from the source, in whole mesh sizes, of each grid cell that holds the centre of a DS1 tile that
    /// no kept terrain covers. The map spans 0..width and 0..height tiles from the world origin.
    /// </summary>
    public static Vector3d[] CoverOffsets(TerrainExtent source, IEnumerable<TerrainExtent> kept, int width, int height, double unitsPerTile)
    {
        if (!(source.SizeX > 0) || !(source.SizeZ > 0) || !double.IsFinite(source.SizeX) || !double.IsFinite(source.SizeZ))
            throw new InvalidOperationException("The terrain mesh has no measurable size.");
        var cover = kept.Append(source).ToArray();
        var cells = new SortedSet<(int J, int I)>();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double cx = (x + .5) * unitsPerTile, cz = (y + .5) * unitsPerTile;
                if (cover.Any(e => e.Contains(cx, cz))) continue;
                cells.Add(((int)Math.Floor((cz - source.MinZ) / source.SizeZ), (int)Math.Floor((cx - source.MinX) / source.SizeX)));
            }
        cells.Remove((0, 0));
        return cells.Select(c => new Vector3d(c.I * source.SizeX, 0, c.J * source.SizeZ)).ToArray();
    }

    /// <summary>
    /// Copies of the source mesh that do not sit on its grid, or that repeat a cell another copy already fills.
    /// <paramref name="copies"/> are the other terrain entities with the same model and size as the source.
    /// </summary>
    public static T[] Misaligned<T>(TerrainExtent source, IEnumerable<(T Entity, TerrainExtent Extent)> copies)
    {
        var filled = new HashSet<(long, long)> { (0, 0) };
        var off = new List<T>();
        foreach (var (entity, extent) in copies)
        {
            double i = (extent.MinX - source.MinX) / source.SizeX, j = (extent.MinZ - source.MinZ) / source.SizeZ;
            bool onGrid = Math.Abs(i - Math.Round(i)) < Tolerance && Math.Abs(j - Math.Round(j)) < Tolerance
                && Math.Abs(extent.SizeX - source.SizeX) < Tolerance * source.SizeX && Math.Abs(extent.SizeZ - source.SizeZ) < Tolerance * source.SizeZ;
            if (!onGrid || !filled.Add(((long)Math.Round(i), (long)Math.Round(j)))) off.Add(entity);
        }
        return off.ToArray();
    }
}

public sealed partial class PresetDocument
{
    /// <summary>Removes terrain copies and places copies of <paramref name="source"/> at each offset, as one edit.</summary>
    public TerrainRepair RepairTerrain(PresetEntity source, IEnumerable<PresetEntity> remove, IReadOnlyList<Vector3d> offsets)
    {
        var removed = remove.Distinct().ToArray();
        if (!entities.Contains(source) || !source.IsTerrain || source.HasParent || !source.CanTransform)
            throw new InvalidOperationException("Terrain repair copies a standalone terrain entity from this document.");
        if (removed.Any(e => e == source || !entities.Contains(e) || !e.IsTerrain || e.HasParent))
            throw new InvalidOperationException("Only other standalone terrain copies can be removed.");
        if (removed.Length == 0 && offsets.Count == 0) throw new InvalidOperationException("The terrain already covers the map.");
        TerrainRepair? result = null;
        History.Transaction(() =>
        {
            var array = (JsonArray)root["entities"]!;
            foreach (var entity in removed)
            {
                int index = entities.IndexOf(entity), nodeIndex = array.IndexOf(entity.Data);
                void Remove() { array.Remove(entity.Data); entities.Remove(entity); dirtyCache = null; }
                void Restore() { array.Insert(nodeIndex, entity.Data); entities.Insert(index, entity); dirtyCache = null; }
                Remove(); History.Record(Restore, Remove);
            }
            var added = offsets.Count == 0 ? [] : InsertCopies([source], [source], offsets).Copies;
            result = new(source, removed, added);
        }, () => result);
        return result!;
    }
}
