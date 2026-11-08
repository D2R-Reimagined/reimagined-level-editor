using System.Buffers.Binary;
using System.Text.Json;

namespace D2RLevel.Core;

/// <summary>How new ground is chosen for tiles a resize adds.</summary>
public enum GroundFill
{
    /// <summary>Every new tile gets one chosen floor.</summary>
    Tile,
    /// <summary>Each new tile copies the floor of the nearest original edge tile, so paths and borders run on.</summary>
    ExtendEdges,
    /// <summary>New tiles have no floor.</summary>
    Empty,
}

/// <summary>
/// Tiles added (positive) or removed (negative) along each DS1 edge. <see cref="Left"/> works on the columns before
/// x = 0 and <see cref="Top"/> on the rows before y = 0, so existing content shifts by that many tiles; the other two
/// only append or trim. Growing never discards map data; cropping discards everything on the removed tiles.
/// </summary>
public sealed record LevelGrowth(int Left, int Top, int Right, int Bottom)
{
    public const int MaximumSize = 512;
    public bool ShiftsContent => Left != 0 || Top != 0;
    /// <summary>True when any edge loses tiles.</summary>
    public bool Crops => Left < 0 || Top < 0 || Right < 0 || Bottom < 0;
    public int NewWidth(int width) => width + Left + Right;
    public int NewHeight(int height) => height + Top + Bottom;
    public void Validate(int width, int height)
    {
        if (Left == 0 && Top == 0 && Right == 0 && Bottom == 0) throw new ArgumentException("Add or remove at least one tile on one edge.");
        if ((long)width + Left + Right > MaximumSize || (long)height + Top + Bottom > MaximumSize)
            throw new ArgumentOutOfRangeException(nameof(Right), $"Levels can be at most {MaximumSize} × {MaximumSize} tiles.");
        if ((long)width + Left + Right < 1 || (long)height + Top + Bottom < 1)
            throw new ArgumentOutOfRangeException(nameof(Left), "A level must keep at least one tile in each direction.");
    }
    /// <summary>Whether original tile (x, y) survives the resize.</summary>
    public bool Keeps(int x, int y, int width, int height) =>
        x >= -Math.Min(Left, 0) && y >= -Math.Min(Top, 0) && x < width + Math.Min(Right, 0) && y < height + Math.Min(Bottom, 0);
}

/// <summary>What a crop removes from the DS1.</summary>
public sealed record ResizeLoss(int Units, int Patrols, int PatrolPoints, int Groups, int ExitTiles)
{
    public bool IsEmpty => Units + Patrols + PatrolPoints + Groups + ExitTiles == 0;
}

public sealed partial class Ds1CollisionDocument
{
    /// <summary>The placements, patrols, tag groups and exit tiles a resize would discard; all zero when it only grows.</summary>
    public ResizeLoss Loss(LevelGrowth growth)
    {
        if (!growth.Crops) return new(0, 0, 0, 0, 0);
        int exits = ExitTiles().Count(t => t.IsExit && !growth.Keeps(t.X, t.Y, Width, Height));
        if (GameplayWarning is not null) return new(0, 0, 0, 0, exits);
        int w = growth.NewWidth(Width) * 5, h = growth.NewHeight(Height) * 5, sx = growth.Left * 5, sy = growth.Top * 5;
        bool Inside(int x, int y) => x + sx >= 0 && y + sy >= 0 && x + sx < w && y + sy < h;
        int units = Units.Count(u => !Inside(u.X, u.Y)), lostPatrols = 0, lostPoints = 0;
        foreach (var patrol in patrols)
        {
            var points = patrol.Points.Select(p => (X: unchecked((int)Read(p)), Y: unchecked((int)Read(p + 4)))).ToArray();
            int outside = points.Count(p => !Inside(p.X, p.Y));
            if (!Inside(unchecked((int)Read(patrol.AnchorOffset)), unchecked((int)Read(patrol.AnchorOffset + 4))) || (points.Length > 0 && outside == points.Length))
            { lostPatrols++; lostPoints += points.Length; }
            else lostPoints += outside;
        }
        int groups = Groups().Count(g => Clip(g, growth) is null);
        return new(units, lostPatrols, lostPoints, groups, exits);
    }

    private IEnumerable<(int Offset, int X, int Y, int W, int H)> Groups()
    {
        if (gameplayTag is not (1 or 2)) yield break;
        int start = gameplayOffset + 4 + unitOffsets.Length * 20 + (Version >= 18 ? 4 : 0), count = unchecked((int)Read(start));
        for (int i = 0; i < count; i++)
        {
            int at = start + 4 + i * 20;
            yield return (at, unchecked((int)Read(at)), unchecked((int)Read(at + 4)), unchecked((int)Read(at + 8)), unchecked((int)Read(at + 12)));
        }
    }

    /// <summary>A tag group's rectangle after the resize, or null when a crop leaves none of it.</summary>
    private (int X, int Y, int W, int H)? Clip((int Offset, int X, int Y, int W, int H) g, LevelGrowth growth)
    {
        int x = checked(g.X + growth.Left), y = checked(g.Y + growth.Top);
        if (!growth.Crops) return (x, y, g.W, g.H);
        int x0 = Math.Max(x, 0), y0 = Math.Max(y, 0);
        int x1 = (int)Math.Min((long)x + g.W, growth.NewWidth(Width)), y1 = (int)Math.Min((long)y + g.H, growth.NewHeight(Height));
        return x1 > x0 && y1 > y0 ? (x0, y0, x1 - x0, y1 - y0) : null;
    }

    /// <summary>
    /// A copy of this map with tiles added around it or trimmed from its edges. Every wall, floor, shadow and tag
    /// layer keeps its cells at their shifted positions; new cells are empty except the first floor, which follows
    /// <paramref name="fill"/>. Placements, patrols and tag groups move with the shifted content; a crop drops the
    /// placements, patrol points and exits on removed tiles and clips tag groups to the new edge.
    /// </summary>
    public Ds1CollisionDocument Grown(LevelGrowth growth, GroundFill fill, uint tile = 0) => Grown(growth, fill, tile, out _);

    /// <param name="unitMap">Each original placement's index in the result, or -1 when a crop removed it.</param>
    public Ds1CollisionDocument Grown(LevelGrowth growth, GroundFill fill, uint tile, out int[] unitMap)
    {
        growth.Validate(Width, Height);
        if (fill == GroundFill.Tile && (tile == 0 || (tile & 255) == 0)) throw new ArgumentException("Choose a floor tile to fill new ground with.");
        if (growth.ShiftsContent && GameplayWarning is not null)
            throw new InvalidOperationException(GameplayWarning + " Placements cannot be shifted, so resize this map only on the lower-left and lower-right edges (X end and Y end).");
        if (growth.Crops && GameplayWarning is not null)
            throw new InvalidOperationException(GameplayWarning + " Placements on removed tiles cannot be found, so this map can only grow.");
        int width = growth.NewWidth(Width), height = growth.NewHeight(Height);
        int start = Walls[0].Offset, size = Width * Height * 4, arrays = (gameplayOffset - start) / size;
        int firstFloor = Floors[0].Offset;
        using var output = new MemoryStream();
        output.Write(bytes, 0, start);
        var header = output.GetBuffer();
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), width - 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), height - 1);
        var row = new byte[width * 4];
        for (int array = 0; array < arrays; array++)
        {
            int source = start + array * size;
            bool ground = source == firstFloor;
            for (int y = 0; y < height; y++)
            {
                int oy = y - growth.Top;
                for (int x = 0; x < width; x++)
                {
                    int ox = x - growth.Left;
                    uint value = 0;
                    if (ox >= 0 && oy >= 0 && ox < Width && oy < Height) value = Read(source + (oy * Width + ox) * 4);
                    else if (ground) value = fill switch
                    {
                        GroundFill.Tile => tile,
                        GroundFill.ExtendEdges => EdgeFloor(source, Math.Clamp(ox, 0, Width - 1), Math.Clamp(oy, 0, Height - 1)),
                        _ => 0,
                    };
                    BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(x * 4, 4), value);
                }
                output.Write(row);
            }
        }
        unitMap = Enumerable.Range(0, unitOffsets.Length).ToArray();
        if (GameplayWarning is not null) output.Write(bytes.AsSpan(gameplayOffset)); // Only end edges grow here, so nothing moves.
        else output.Write(ResizedGameplay(growth, width, height, unitMap));
        var grown = new Ds1CollisionDocument(SourcePath, output.ToArray());
        if (grown.Width != width || grown.Height != height || grown.Units.Count != unitMap.Count(i => i >= 0) || (GameplayWarning is null) != (grown.GameplayWarning is null))
            throw new InvalidDataException("Resized DS1 failed verification.");
        return grown;
    }

    /// <summary>The gameplay block rebuilt for the resized grid; <paramref name="unitMap"/> receives the placements' new indices.</summary>
    private byte[] ResizedGameplay(LevelGrowth growth, int width, int height, int[] unitMap)
    {
        int sx = growth.Left * 5, sy = growth.Top * 5;
        // Growing keeps every record, even one that already sat off the grid; only a crop drops anything.
        bool Inside(int x, int y) => !growth.Crops || (x >= 0 && y >= 0 && x < width * 5 && y < height * 5);
        int Value(int offset) => unchecked((int)Read(offset));
        using var o = new MemoryStream();
        var word = new byte[4];
        void Int(int value) { BinaryPrimitives.WriteInt32LittleEndian(word, value); o.Write(word); }
        var units = Units; int kept = 0;
        foreach (var u in units) unitMap[u.Index] = Inside(checked(u.X + sx), checked(u.Y + sy)) ? kept++ : -1;
        Int(kept);
        foreach (var u in units.Where(u => unitMap[u.Index] >= 0))
        {
            int at = unitOffsets[u.Index];
            Int(Value(at)); Int(Value(at + 4)); Int(checked(u.X + sx)); Int(checked(u.Y + sy)); Int(Value(at + 16));
        }
        int cursor = gameplayOffset + 4 + units.Count * 20;
        if (gameplayTag is 1 or 2)
        {
            if (Version >= 18) { o.Write(bytes, cursor, 4); cursor += 4; }
            // Tag groups are tile rectangles: x, y, width, height and one more field.
            var groups = Groups().Select(g => (g.Offset, Rect: Clip(g, growth))).Where(g => g.Rect is not null).ToArray();
            Int(groups.Length);
            foreach (var (offset, rect) in groups) { var (x, y, w, h) = rect!.Value; Int(x); Int(y); Int(w); Int(h); Int(Value(offset + 16)); }
            cursor += 4 + Value(cursor) * 20;
        }
        if (patrolCountOffset < 0) { o.Write(bytes, cursor, bytes.Length - cursor); return o.ToArray(); } // Some vanilla files end without a path count.
        var routes = new List<(int X, int Y, int[] Points)>();
        foreach (var patrol in patrols)
        {
            int ax = checked(Value(patrol.AnchorOffset) + sx), ay = checked(Value(patrol.AnchorOffset + 4) + sy);
            var points = patrol.Points.Where(p => Inside(checked(Value(p) + sx), checked(Value(p + 4) + sy))).ToArray();
            // A route whose start or every point was removed has nothing left to walk.
            if (!Inside(ax, ay) || (patrol.Points.Length > 0 && points.Length == 0)) continue;
            routes.Add((ax, ay, points));
        }
        Int(routes.Count);
        foreach (var (ax, ay, points) in routes)
        {
            Int(points.Length); Int(ax); Int(ay);
            foreach (int p in points) { Int(checked(Value(p) + sx)); Int(checked(Value(p + 4) + sy)); Int(Value(p + 8)); }
        }
        int end = PatrolBlockEnd();
        o.Write(bytes, end, bytes.Length - end);
        return o.ToArray();
    }

    /// <summary>The plain floor of an original tile, without its blocking override or other flags.</summary>
    private uint EdgeFloor(int layer, int x, int y)
    {
        var cell = new FloorCell(Read(layer + (y * Width + x) * 4));
        return cell.IsEmpty ? 0 : FloorKey(cell.Main, cell.Sub);
    }
}

public sealed partial class PresetDocument
{
    /// <summary>Moves every top-level entity that has a transform, which carries any hierarchy below it along.</summary>
    internal void ShiftRoots(Vector3d offset)
    {
        foreach (var entity in Entities.Where(e => !e.HasParent && e.CanTransform).ToArray())
        {
            var t = entity.Transform;
            SetTransform(entity, t with { Position = new(t.Position.X + offset.X, t.Position.Y + offset.Y, t.Position.Z + offset.Z) });
        }
    }

    /// <summary>The standalone terrain entities; the first is the level's own mesh, later ones are copies laid beside it.</summary>
    public PresetEntity[] TerrainEntities() => Entities.Where(e => e.IsTerrain && !e.HasParent && e.CanTransform).ToArray();
}

/// <param name="UnlinkedObjects">HD objects whose DS1 link a crop removed; the objects themselves stay where they are.</param>
public sealed record LevelResizeResult(WorkspaceScene Scene, int Width, int Height, int TerrainCopies, int UnlinkedObjects = 0);

/// <summary>
/// Grows or crops a saved workspace scene on disk: DS1, HD preset, links and level project change together,
/// are verified from staged copies, and replace the originals with .bak files kept.
/// </summary>
public static class LevelResizer
{
    /// <summary>More copies than this means the terrain mesh is tiny next to the map; repeating it would not help.</summary>
    public const int MaximumTerrainCopies = 64;

    /// <param name="terrainExtents">Measured ground extents of terrain meshes before the resize, keyed by entity id. Unmeasured
    /// terrain is assumed to span the DS1 grid from its origin, which leaves gaps when the mesh is a different size.</param>
    public static LevelResizeResult Apply(string root, WorkspaceScene scene, LevelGrowth growth, GroundFill fill, uint tile, bool repeatTerrain,
        IReadOnlyDictionary<string, TerrainExtent>? terrainExtents = null)
    {
        string jsonPath = SceneWorkspace.Inside(root, scene.JsonPath), ds1Path = SceneWorkspace.Inside(root, scene.Ds1Path);
        string linksPath = SceneWorkspace.Inside(root, jsonPath + PlacementLinks.Suffix), projectPath = SceneWorkspace.Inside(root, jsonPath + LevelProject.Suffix);
        var json = PresetDocument.Load(jsonPath); var ds1 = Ds1CollisionDocument.Load(ds1Path);
        var links = new PlacementLinks(json, ds1, linksPath);
        if (links.Warning is not null) throw new InvalidOperationException(links.Warning);
        var project = LevelProject.ForPreset(jsonPath);
        project?.VerifyMap(ds1);

        var grown = ds1.Grown(growth, fill, tile, out var unitMap);
        double units = links.Calibration.UnitsPerTile;
        var shift = new Vector3d(growth.Left * units, 0, growth.Top * units);
        bool hasLinks = File.Exists(linksPath);
        // Links are measured against the HD objects where they stand now, so the sidecar is built before anything moves.
        var (sidecar, kept) = links.GrownMetadata(growth, shift, grown, unitMap, linksPath);
        if (growth.ShiftsContent) json.ShiftRoots(shift);
        int copies = 0;
        if (repeatTerrain && json.TerrainEntities() is [var source, ..] terrain)
        {
            // Whole copies of the level's own mesh, a mesh width apart, wherever a tile of the grown map has no terrain.
            TerrainExtent Extent(PresetEntity e) => (terrainExtents?.GetValueOrDefault(e.Id)?.Offset(shift.X, shift.Z))
                ?? TerrainExtent.FromGrid(e.Transform.Position, ds1.Width, ds1.Height, units);
            var offsets = TerrainTiling.CoverOffsets(Extent(source), terrain.Skip(1).Select(Extent), grown.Width, grown.Height, units);
            if (offsets.Length > MaximumTerrainCopies)
                throw new InvalidOperationException($"Repeating the terrain would need {offsets.Length} copies (limit {MaximumTerrainCopies}). Grow in smaller steps or leave the HD terrain as it is.");
            if (offsets.Length > 0) copies = json.RepairTerrain(source, [], offsets).Added.Length;
        }

        var paths = new List<string> { jsonPath, ds1Path };
        var data = new List<byte[]> { json.Serialize(), grown.Serialize() };
        if (hasLinks) { paths.Add(linksPath); data.Add(sidecar); }
        LevelProject? resized = project is null ? null : project with { Width = grown.Width, Height = grown.Height };
        if (resized is not null) { paths.Add(projectPath); data.Add(resized.Serialize()); }

        SceneWorkspace.Publish(paths.ToArray(), paths.Select(p => File.Exists(p) ? File.ReadAllBytes(p) : null).ToArray(), data.ToArray(), staged =>
        {
            var checkedJson = PresetDocument.Load(staged[0]); var checkedMap = Ds1CollisionDocument.Load(staged[1]);
            if (!checkedJson.Serialize().SequenceEqual(data[0]) || !checkedMap.Serialize().SequenceEqual(data[1]))
                throw new InvalidDataException("Resized scene failed verification.");
            // Without a sidecar there is nothing to stage; the absent file reads as an empty, healthy link set.
            var verified = new PlacementLinks(checkedJson, checkedMap, hasLinks ? staged[2] : linksPath);
            if (verified.Warning is not null) throw new InvalidDataException(verified.Warning);
            if (verified.Links.Count != kept.Count || verified.LinkStates.Any(l => l.IsHealthy != kept.GetValueOrDefault(l.Link.EntityId)))
                throw new InvalidDataException("Object links did not survive the resize.");
            if (resized is not null)
            {
                var reread = JsonSerializer.Deserialize<LevelProject>(File.ReadAllBytes(staged[^1])) ?? throw new InvalidDataException("Empty level project.");
                reread.Validate(); reread.VerifyMap(checkedMap);
            }
        });
        return new(scene, grown.Width, grown.Height, copies, links.Links.Count - kept.Count);
    }
}

public sealed partial class PlacementLinks
{
    /// <summary>
    /// This pair's sidecar after <see cref="Ds1CollisionDocument.Grown(LevelGrowth, GroundFill, uint, out int[])"/>: footprints,
    /// baselines and unit records move with the shifted tiles, and anchors move with their HD objects, so every surviving link keeps
    /// its current health. A link whose placement or any collision tile a crop removes is dropped the way <see cref="Unlink"/> drops
    /// one, leaving its HD object in place. Call it before the HD objects are shifted.
    /// </summary>
    /// <returns>The sidecar bytes, and each kept link's id with whether it was intact.</returns>
    internal (byte[] Data, Dictionary<string, bool> Kept) GrownMetadata(LevelGrowth growth, Vector3d shift, Ds1CollisionDocument grown, int[] unitMap, string sidecarPath)
    {
        EnsureReady(); ValidateStructure();
        int dx = growth.Left, dy = growth.Top;
        bool Inside(LinkedTile t) => t.X + dx >= 0 && t.Y + dy >= 0 && t.X + dx < grown.Width && t.Y + dy < grown.Height;
        var index = BuildIndex();
        var kept = new Dictionary<string, bool>(StringComparer.Ordinal);
        var released = new HashSet<LinkedTile>();
        var links = new List<PlacementLink>();
        foreach (var link in Links)
        {
            bool healthy = Health.GetValueOrDefault(link.EntityId) is null;
            LinkedTile[] current;
            try { current = Tiles(link, null, index); }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException) { current = link.Tiles; } // Broken: its object is gone.
            bool unitKept = link.Unit is not { } u || (u.Index >= 0 && u.Index < unitMap.Length && unitMap[u.Index] >= 0);
            if (!unitKept || !current.All(Inside))
            {
                if (healthy) released.UnionWith(current);
                continue;
            }
            kept[link.EntityId] = healthy;
            links.Add(link with
            {
                Anchor = link.Anchor with { Position = new(link.Anchor.Position.X + shift.X, link.Anchor.Position.Y + shift.Y, link.Anchor.Position.Z + shift.Z) },
                Tiles = link.Tiles.Select(t => new LinkedTile(t.X + dx, t.Y + dy)).ToArray(),
                Unit = link.Unit is { } unit ? unit with { Index = unitMap[unit.Index], X = unit.X + dx * 5, Y = unit.Y + dy * 5 } : null,
            });
        }
        var moved = file with
        {
            Version = CurrentVersion,
            Fingerprint = grown.LinkFingerprint(),
            Ds1Path = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(sidecarPath))!, grown.SourcePath),
            Links = links.ToArray(),
            // Collision a dropped link owned stays as it is now, exactly as unlinking leaves it.
            Baselines = file.Baselines.Where(b => Inside(new(b.X, b.Y)))
                .Select(b => (released.Contains(new(b.X, b.Y)) ? b with { Blocked = true } : b) with { X = b.X + dx, Y = b.Y + dy }).ToArray(),
        };
        return (System.Text.Encoding.UTF8.GetBytes(Serialize(moved)), kept);
    }

    /// <summary>How many links a resize would drop because their placement or collision lies on removed tiles.</summary>
    public int LinksRemovedBy(LevelGrowth growth)
    {
        EnsureReady();
        if (!growth.Crops) return 0;
        var index = BuildIndex();
        int count = 0;
        foreach (var link in Links)
        {
            LinkedTile[] current;
            try { current = Tiles(link, null, index); }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException) { current = link.Tiles; }
            static int Tile(int subtile) => (int)Math.Floor(subtile / 5.0);
            bool unitGone = link.Unit is { } u && (u.Index < 0 || u.Index >= ds1.Units.Count
                || !growth.Keeps(Tile(ds1.Units[u.Index].X), Tile(ds1.Units[u.Index].Y), ds1.Width, ds1.Height));
            if (unitGone || current.Any(t => !growth.Keeps(t.X, t.Y, ds1.Width, ds1.Height))) count++;
        }
        return count;
    }
}
