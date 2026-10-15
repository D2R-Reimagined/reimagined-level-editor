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
/// Tiles added along each DS1 edge. <see cref="Left"/> adds columns before x = 0 and <see cref="Top"/>
/// rows before y = 0, so existing content shifts by that many tiles; the other two only append.
/// Growing never discards map data.
/// </summary>
public sealed record LevelGrowth(int Left, int Top, int Right, int Bottom)
{
    public const int MaximumSize = 512;
    public bool ShiftsContent => Left > 0 || Top > 0;
    public int NewWidth(int width) => width + Left + Right;
    public int NewHeight(int height) => height + Top + Bottom;
    public void Validate(int width, int height)
    {
        if (Left < 0 || Top < 0 || Right < 0 || Bottom < 0) throw new ArgumentOutOfRangeException(nameof(Left), "A resize can only add tiles.");
        if (Left + Top + Right + Bottom == 0) throw new ArgumentException("Add at least one tile on one edge.");
        if ((long)width + Left + Right > MaximumSize || (long)height + Top + Bottom > MaximumSize)
            throw new ArgumentOutOfRangeException(nameof(Right), $"Levels can be at most {MaximumSize} × {MaximumSize} tiles.");
    }
}

public sealed partial class Ds1CollisionDocument
{
    /// <summary>
    /// A copy of this map with tiles added around it. Every wall, floor, shadow and tag layer keeps its
    /// cells at their shifted positions; new cells are empty except the first floor, which follows
    /// <paramref name="fill"/>. Placements, patrols and tag groups move with the shifted content.
    /// </summary>
    public Ds1CollisionDocument Grown(LevelGrowth growth, GroundFill fill, uint tile = 0)
    {
        growth.Validate(Width, Height);
        if (fill == GroundFill.Tile && (tile == 0 || (tile & 255) == 0)) throw new ArgumentException("Choose a floor tile to fill new ground with.");
        if (growth.ShiftsContent && GameplayWarning is not null)
            throw new InvalidOperationException(GameplayWarning + " Placements cannot be shifted, so grow this map only on its X end and Y end edges.");
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
        var tail = bytes.AsSpan(gameplayOffset).ToArray();
        if (growth.ShiftsContent)
        {
            void Shift(int offset, int dx, int dy)
            {
                int at = offset - gameplayOffset;
                BinaryPrimitives.WriteInt32LittleEndian(tail.AsSpan(at, 4), checked(BinaryPrimitives.ReadInt32LittleEndian(tail.AsSpan(at, 4)) + dx));
                BinaryPrimitives.WriteInt32LittleEndian(tail.AsSpan(at + 4, 4), checked(BinaryPrimitives.ReadInt32LittleEndian(tail.AsSpan(at + 4, 4)) + dy));
            }
            int sx = growth.Left * 5, sy = growth.Top * 5;
            foreach (int unit in unitOffsets) Shift(unit + 8, sx, sy);
            if (gameplayTag is 1 or 2)
            {
                // Tag groups are tile rectangles: x, y, width, height and one more field.
                int groups = gameplayOffset + 4 + unitOffsets.Length * 20 + (Version >= 18 ? 4 : 0);
                int count = unchecked((int)Read(groups));
                for (int i = 0; i < count; i++) Shift(groups + 4 + i * 20, growth.Left, growth.Top);
            }
            foreach (var patrol in patrols) foreach (int point in patrol.Points.Prepend(patrol.AnchorOffset)) Shift(point, sx, sy);
        }
        output.Write(tail);
        var grown = new Ds1CollisionDocument(SourcePath, output.ToArray());
        if (grown.Width != width || grown.Height != height || grown.Units.Count != Units.Count || (GameplayWarning is null) != (grown.GameplayWarning is null))
            throw new InvalidDataException("Resized DS1 failed verification.");
        return grown;
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

    /// <summary>Copies of each top-level terrain entity at every offset, as one edit. Returns the number of copies.</summary>
    internal int RepeatTerrain(IReadOnlyList<Vector3d> offsets)
    {
        var terrain = Entities.Where(e => e.IsTerrain && !e.HasParent && e.CanTransform).ToArray();
        if (terrain.Length == 0 || offsets.Count == 0) return 0;
        return InsertCopies(terrain, terrain, offsets).Copies.Length;
    }
}

public sealed record LevelResizeResult(WorkspaceScene Scene, int Width, int Height, int TerrainCopies);

/// <summary>
/// Grows a saved workspace scene on disk: DS1, HD preset, links and level project change together,
/// are verified from staged copies, and replace the originals with .bak files kept.
/// </summary>
public static class LevelResizer
{
    /// <summary>More copies than this means the terrain mesh is tiny next to the map; repeating it would not help.</summary>
    public const int MaximumTerrainCopies = 64;

    public static LevelResizeResult Apply(string root, WorkspaceScene scene, LevelGrowth growth, GroundFill fill, uint tile, bool repeatTerrain)
    {
        string jsonPath = SceneWorkspace.Inside(root, scene.JsonPath), ds1Path = SceneWorkspace.Inside(root, scene.Ds1Path);
        string linksPath = SceneWorkspace.Inside(root, jsonPath + PlacementLinks.Suffix), projectPath = SceneWorkspace.Inside(root, jsonPath + LevelProject.Suffix);
        var json = PresetDocument.Load(jsonPath); var ds1 = Ds1CollisionDocument.Load(ds1Path);
        var links = new PlacementLinks(json, ds1, linksPath);
        if (links.Warning is not null) throw new InvalidOperationException(links.Warning);
        int broken = links.BrokenLinkCount;
        var project = LevelProject.ForPreset(jsonPath);
        project?.VerifyMap(ds1);

        var grown = ds1.Grown(growth, fill, tile);
        double units = links.Calibration.UnitsPerTile;
        var shift = new Vector3d(growth.Left * units, 0, growth.Top * units);
        if (growth.ShiftsContent) json.ShiftRoots(shift);
        int copies = 0;
        if (repeatTerrain)
        {
            // Whole copies of the original mesh, laid edge to edge until they cover every added tile.
            double spanX = ds1.Width * units, spanZ = ds1.Height * units;
            int west = Ceiling(growth.Left, ds1.Width), east = Ceiling(growth.Right, ds1.Width);
            int north = Ceiling(growth.Top, ds1.Height), south = Ceiling(growth.Bottom, ds1.Height);
            var offsets = new List<Vector3d>();
            for (int j = -north; j <= south; j++)
                for (int i = -west; i <= east; i++)
                    if (i != 0 || j != 0) offsets.Add(new(i * spanX, 0, j * spanZ));
            if (offsets.Count > MaximumTerrainCopies)
                throw new InvalidOperationException($"Repeating the terrain would need {offsets.Count} copies (limit {MaximumTerrainCopies}). Grow in smaller steps or leave the HD terrain as it is.");
            copies = json.RepeatTerrain(offsets);
        }

        var paths = new List<string> { jsonPath, ds1Path };
        var data = new List<byte[]> { json.Serialize(), grown.Serialize() };
        bool hasLinks = File.Exists(linksPath);
        if (hasLinks) { paths.Add(linksPath); data.Add(links.GrownMetadata(growth, shift, grown, linksPath)); }
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
            if (verified.BrokenLinkCount != broken) throw new InvalidDataException("Object links did not survive the resize.");
            if (resized is not null)
            {
                var reread = JsonSerializer.Deserialize<LevelProject>(File.ReadAllBytes(staged[^1])) ?? throw new InvalidDataException("Empty level project.");
                reread.Validate(); reread.VerifyMap(checkedMap);
            }
        });
        return new(scene, grown.Width, grown.Height, copies);
    }

    private static int Ceiling(int added, int span) => added == 0 ? 0 : (added + span - 1) / span;
}

public sealed partial class PlacementLinks
{
    /// <summary>
    /// This pair's sidecar after <see cref="Ds1CollisionDocument.Grown"/>: footprints, baselines and unit
    /// records move with the shifted tiles, and anchors move with their HD objects, so every link keeps
    /// its current health.
    /// </summary>
    internal byte[] GrownMetadata(LevelGrowth growth, Vector3d shift, Ds1CollisionDocument grown, string sidecarPath)
    {
        EnsureReady(); ValidateStructure();
        int dx = growth.Left, dy = growth.Top;
        var moved = file with
        {
            Version = CurrentVersion,
            Fingerprint = grown.LinkFingerprint(),
            Ds1Path = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(sidecarPath))!, grown.SourcePath),
            Links = file.Links.Select(l => l with
            {
                Anchor = l.Anchor with { Position = new(l.Anchor.Position.X + shift.X, l.Anchor.Position.Y + shift.Y, l.Anchor.Position.Z + shift.Z) },
                Tiles = l.Tiles.Select(t => new LinkedTile(t.X + dx, t.Y + dy)).ToArray(),
                Unit = l.Unit is { } u ? u with { X = u.X + dx * 5, Y = u.Y + dy * 5 } : null,
            }).ToArray(),
            Baselines = file.Baselines.Select(b => b with { X = b.X + dx, Y = b.Y + dy }).ToArray(),
        };
        return System.Text.Encoding.UTF8.GetBytes(Serialize(moved));
    }
}
