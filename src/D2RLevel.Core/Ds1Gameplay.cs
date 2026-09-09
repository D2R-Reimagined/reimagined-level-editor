namespace D2RLevel.Core;

public sealed record Ds1Unit(int Index, int Type, int Id, int X, int Y, uint Flags);
public sealed record Ds1PathPoint(int X, int Y, uint Action);

public sealed partial class Ds1CollisionDocument
{
    private int[] unitOffsets = [];
    private sealed record Patrol(int AnchorOffset, int[] Points);
    private Patrol[] patrols = [];
    private int gameplayOffset, gameplayTag, patrolCountOffset = -1;
    public string? GameplayWarning { get; private set; }
    public IReadOnlyList<Ds1Unit> Units => unitOffsets.Select((offset, i) =>
        new Ds1Unit(i, unchecked((int)Read(offset)), unchecked((int)Read(offset + 4)),
            unchecked((int)Read(offset + 8)), unchecked((int)Read(offset + 12)), Read(offset + 16))).ToArray();

    private void ReadGameplay(int offset, int tag)
    {
        gameplayOffset = offset; gameplayTag = tag;
        unitOffsets = []; patrols = []; patrolCountOffset = -1; GameplayWarning = null;
        try
        {
            using var r = new BinaryReader(new MemoryStream(bytes, false)); r.BaseStream.Position = offset;
            int Count(int stride)
            {
                int count = r.ReadInt32();
                if (count < 0 || count > 100000 || r.BaseStream.Position + (long)count * stride > bytes.Length)
                    throw new InvalidDataException("Invalid or truncated DS1 gameplay records.");
                return count;
            }
            int units = Count(20);
            unitOffsets = Enumerable.Range(0, units).Select(i => (int)r.BaseStream.Position + i * 20).ToArray();
            r.BaseStream.Position += units * 20L;
            if (tag is 1 or 2)
            {
                if (Version >= 18) r.ReadInt32(); // Group preamble, not a header field.
                int groups = Count(20); r.BaseStream.Position += groups * 20L;
            }
            // Some vanilla files end here without a path-count dword.
            if (r.BaseStream.Position == bytes.Length) return;
            patrolCountOffset = (int)r.BaseStream.Position;
            int paths = Count(12); var parsed = new List<Patrol>();
            for (int i = 0; i < paths; i++)
            {
                int points = Count(12);
                int anchor = (int)r.BaseStream.Position; r.ReadInt32(); r.ReadInt32();
                if (r.BaseStream.Position + points * 12L > bytes.Length) throw new InvalidDataException("Truncated DS1 patrol points.");
                var offsets = Enumerable.Range(0, points).Select(n => (int)r.BaseStream.Position + n * 12).ToArray();
                r.BaseStream.Position += points * 12L; parsed.Add(new(anchor, offsets));
            }
            patrols = parsed.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException)
        {
            GameplayWarning = "Gameplay movement disabled: " + ex.Message + " Collision-only editing still preserves these bytes.";
        }
    }
    internal void DeleteLinkedUnit(int index)
    {
        if (GameplayWarning is not null) throw new InvalidOperationException(GameplayWarning);
        var units = Units; var unit = units[index];
        // A shared anchor belongs to surviving placements too; retain its patrol.
        var removedPatrols = units.Any(u => u.Index != index && u.X == unit.X && u.Y == unit.Y)
            ? [] : patrols.Where(p => Anchored(p, unit.X, unit.Y)).ToArray();
        var before = bytes.ToArray(); var edited = bytes.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(edited.AsSpan(gameplayOffset, 4), units.Count - 1);
        if (removedPatrols.Length > 0)
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(edited.AsSpan(patrolCountOffset, 4), patrols.Length - removedPatrols.Length);
        var ranges = removedPatrols.Select(p => (Start: p.AnchorOffset - 4, Length: 12 + p.Points.Length * 12))
            .Append((Start: unitOffsets[index], Length: 20)).OrderBy(p => p.Start).ToArray();
        using var output = new MemoryStream(); int cursor = 0;
        foreach (var range in ranges) { output.Write(edited, cursor, range.Start - cursor); cursor = range.Start + range.Length; }
        output.Write(edited, cursor, edited.Length - cursor); var after = output.ToArray();
        void Set(byte[] value) { bytes = value.ToArray(); ReadGameplay(gameplayOffset, gameplayTag); }
        Set(after); History.Record(() => Set(before), () => Set(after));
    }
    private bool Anchored(Patrol p, int x, int y) => unchecked((int)Read(p.AnchorOffset)) == x && unchecked((int)Read(p.AnchorOffset + 4)) == y;
    public IReadOnlyList<Ds1PathPoint> PatrolPoints(int unitIndex)
    {
        var unit = Units[unitIndex];
        return patrols.Where(p => Anchored(p, unit.X, unit.Y)).SelectMany(p => p.Points)
            .Select(o => new Ds1PathPoint(unchecked((int)Read(o)), unchecked((int)Read(o + 4)), Read(o + 8))).ToArray();
    }
    public void MoveUnit(int index, int x, int y)
    {
        if (!LinkedUnitMove && ProtectedUnit?.Invoke(index) == true)
            throw new InvalidOperationException("This placement is linked to an HD object. Move it from the JSON editor so both stay synchronized.");
        if (GameplayWarning is not null) throw new InvalidOperationException(GameplayWarning);
        var units = Units;
        if (index < 0 || index >= units.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var unit = units[index];
        if (unit.Type is not (1 or 2)) throw new InvalidOperationException("Movement supports type 1 and type 2 DS1 records only.");
        void Validate(int px, int py)
        {
            if (px < 0 || py < 0 || px >= Width * 5 || py >= Height * 5)
                throw new ArgumentOutOfRangeException(nameof(x), "Placement or translated patrol lies outside the DS1 subtile grid.");
        }
        Validate(x, y);
        if (unit.X == x && unit.Y == y) return;
        var linked = patrols.Where(p => Anchored(p, unit.X, unit.Y)).ToArray();
        if (linked.Length > 0 && units.Count(u => u.X == unit.X && u.Y == unit.Y) != 1)
            throw new InvalidOperationException("Multiple units share this patrol anchor; resolve the ambiguity before moving them.");
        if (patrols.Any(p => Anchored(p, x, y)) || (linked.Length > 0 && units.Any(u => u.Index != index && u.X == x && u.Y == y)))
            throw new InvalidOperationException("The destination would create an ambiguous patrol anchor.");
        int dx = checked(x - unit.X), dy = checked(y - unit.Y);
        var changes = new List<Change>();
        void Coordinate(int offset, int value)
        {
            uint old = Read(offset), next = unchecked((uint)value);
            if (old != next) changes.Add(new(offset, old, next));
        }
        Coordinate(unitOffsets[index] + 8, x); Coordinate(unitOffsets[index] + 12, y);
        foreach (var path in linked)
        {
            Coordinate(path.AnchorOffset, x); Coordinate(path.AnchorOffset + 4, y);
            foreach (int node in path.Points)
            {
                int px = checked(unchecked((int)Read(node)) + dx), py = checked(unchecked((int)Read(node + 4)) + dy);
                Validate(px, py); Coordinate(node, px); Coordinate(node + 4, py);
            }
        }
        foreach (var change in changes) Write(change.Offset, change.After);
        RecordChanges(changes.ToArray());
    }
    public bool SameUneditedData(Ds1CollisionDocument other)
    {
        if (bytes.Length != other.bytes.Length || Width != other.Width || Height != other.Height) return false;
        var a = bytes.ToArray(); var b = other.bytes.ToArray();
        foreach (var layer in Floors.Concat(Walls)) for (int i = 0; i < Width * Height; i++)
        { a[layer.Offset + i * 4 + 2] &= 0xfd; b[layer.Offset + i * 4 + 2] &= 0xfd; }
        if (GameplayWarning is null && other.GameplayWarning is null)
        {
            foreach (int offset in unitOffsets) { Array.Clear(a, offset + 8, 8); Array.Clear(b, offset + 8, 8); }
            foreach (var path in patrols)
                foreach (int offset in path.Points.Prepend(path.AnchorOffset)) { Array.Clear(a, offset, 8); Array.Clear(b, offset, 8); }
        }
        return a.AsSpan().SequenceEqual(b);
    }
}
