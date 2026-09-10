using System.Buffers.Binary;

namespace D2RLevel.Core;

/// <summary>
/// A patrol point's action code. What each value makes a unit do is not established by
/// anything in this repository, so values are shown raw and preserved exactly rather
/// than being labelled with a guess. The catalog lists the values vanilla actually uses,
/// measured over the base DS1 corpus; any other value can still be entered and is kept.
/// </summary>
public sealed record Ds1PathAction(uint Value, int VanillaUses)
{
    // Distribution over 136 patrol paths in the base game's global/tiles corpus.
    private static readonly Dictionary<uint, int> Observed = new()
    {
        [1] = 328, [2] = 105, [3] = 43, [4] = 34, [5] = 1,
    };
    public static bool IsVanilla(uint value) => Observed.ContainsKey(value);
    public static string Describe(uint value) => Observed.TryGetValue(value, out int uses)
        ? $"Action {value} · {uses} vanilla uses"
        : $"Action {value} · not used by the base game";
    public static IReadOnlyList<Ds1PathAction> Catalog { get; } =
        Observed.OrderBy(p => p.Key).Select(p => new Ds1PathAction(p.Key, p.Value)).ToArray();
    public override string ToString() => Describe(Value);
}

/// <summary>
/// Patrol path editing. A DS1 path record is [pointCount][anchorX][anchorY] followed by
/// 12-byte points; a path belongs to the unit standing on its anchor. Point coordinates
/// are patched in place, while inserting or removing points splices the record and
/// re-reads the gameplay block, the same way unit deletion does.
/// </summary>
public sealed partial class Ds1CollisionDocument
{
    private const int PointStride = 12;
    private const int PathHeader = 12;

    /// <summary>Null when this unit's patrol can be edited; otherwise why it cannot.</summary>
    public string? PathEditWarning(int unitIndex)
    {
        if (GameplayWarning is not null) return GameplayWarning;
        if (unitIndex < 0 || unitIndex >= unitOffsets.Length) return "No such DS1 unit.";
        var unit = Units[unitIndex];
        if (unit.Type is not (1 or 2)) return "Patrol paths belong to type 1 and type 2 records only.";
        if (Units.Count(u => u.X == unit.X && u.Y == unit.Y) != 1)
            return "Several units share this position, so a patrol here cannot be attributed to one of them. Move them apart first.";
        if (patrols.Count(p => Anchored(p, unit.X, unit.Y)) > 1)
            return "This position anchors more than one patrol record. Resolve the duplicate before editing it.";
        return null;
    }

    private (Patrol? Path, Ds1Unit Unit) RequireEditablePath(int unitIndex)
    {
        if (PathEditWarning(unitIndex) is { } warning) throw new InvalidOperationException(warning);
        var unit = Units[unitIndex];
        var found = patrols.Where(p => Anchored(p, unit.X, unit.Y)).ToArray();
        return (found.Length == 1 ? found[0] : null, unit);
    }

    private void ValidateSubtile(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width * 5 || y >= Height * 5)
            throw new ArgumentOutOfRangeException(nameof(x), "Patrol point lies outside the DS1 subtile grid.");
    }

    /// <summary>Moves one patrol point. Coordinates are patched in place.</summary>
    public void MovePathPoint(int unitIndex, int pointIndex, int x, int y)
    {
        var path = RequireEditablePath(unitIndex).Path ?? throw new InvalidOperationException("This unit has no patrol path.");
        if (pointIndex < 0 || pointIndex >= path.Points.Length) throw new ArgumentOutOfRangeException(nameof(pointIndex));
        ValidateSubtile(x, y);
        int offset = path.Points[pointIndex];
        var changes = new List<Change>();
        void Coordinate(int at, int value)
        {
            uint old = Read(at), next = unchecked((uint)value);
            if (old != next) changes.Add(new(at, old, next));
        }
        Coordinate(offset, x); Coordinate(offset + 4, y);
        if (changes.Count == 0) return;
        foreach (var change in changes) Write(change.Offset, change.After);
        RecordChanges(changes.ToArray());
    }

    /// <summary>Replaces one patrol point's action code.</summary>
    public void SetPathPointAction(int unitIndex, int pointIndex, uint action)
    {
        var path = RequireEditablePath(unitIndex).Path ?? throw new InvalidOperationException("This unit has no patrol path.");
        if (pointIndex < 0 || pointIndex >= path.Points.Length) throw new ArgumentOutOfRangeException(nameof(pointIndex));
        int offset = path.Points[pointIndex] + 8;
        uint old = Read(offset);
        if (old == action) return;
        Write(offset, action);
        RecordChanges([new(offset, old, action)]);
    }

    /// <summary>
    /// Inserts a patrol point, creating the unit's path record when it has none.
    /// The record grows, so the gameplay block is rebuilt and re-read as one undoable edit.
    /// </summary>
    public void InsertPathPoint(int unitIndex, int pointIndex, int x, int y, uint action)
    {
        var (path, unit) = RequireEditablePath(unitIndex);
        ValidateSubtile(x, y);
        int existing = path?.Points.Length ?? 0;
        if (pointIndex < 0 || pointIndex > existing) throw new ArgumentOutOfRangeException(nameof(pointIndex));
        if (existing >= 1000) throw new InvalidOperationException("A patrol path is limited to 1000 points.");
        if (path is null && patrolCountOffset < 0)
            throw new InvalidOperationException("This DS1 stores no patrol block, so a first path cannot be added without changing its layout. Add the path in a DS1 that already has one.");

        var point = new byte[PointStride];
        BinaryPrimitives.WriteInt32LittleEndian(point.AsSpan(0, 4), x);
        BinaryPrimitives.WriteInt32LittleEndian(point.AsSpan(4, 4), y);
        BinaryPrimitives.WriteUInt32LittleEndian(point.AsSpan(8, 4), action);

        var before = bytes.ToArray();
        byte[] after;
        if (path is not null)
        {
            var edited = bytes.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(edited.AsSpan(path.AnchorOffset - 4, 4), existing + 1);
            int at = pointIndex < existing ? path.Points[pointIndex] : path.Points[^1] + PointStride;
            if (existing == 0) at = path.AnchorOffset + 8;
            after = Splice(edited, at, 0, point);
        }
        else
        {
            // A new record goes at the end of the patrol block so existing paths keep their offsets.
            var edited = bytes.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(edited.AsSpan(patrolCountOffset, 4), patrols.Length + 1);
            var record = new byte[PathHeader + PointStride];
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0, 4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4, 4), unit.X);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8, 4), unit.Y);
            point.CopyTo(record.AsSpan(PathHeader));
            after = Splice(edited, PatrolBlockEnd(), 0, record);
        }
        ApplyGameplayBytes(before, after);
    }

    /// <summary>Removes one patrol point, and the whole record when it was the last one.</summary>
    public void RemovePathPoint(int unitIndex, int pointIndex)
    {
        var path = RequireEditablePath(unitIndex).Path ?? throw new InvalidOperationException("This unit has no patrol path.");
        if (pointIndex < 0 || pointIndex >= path.Points.Length) throw new ArgumentOutOfRangeException(nameof(pointIndex));
        var before = bytes.ToArray();
        var edited = bytes.ToArray();
        byte[] after;
        if (path.Points.Length == 1)
        {
            BinaryPrimitives.WriteInt32LittleEndian(edited.AsSpan(patrolCountOffset, 4), patrols.Length - 1);
            after = Splice(edited, path.AnchorOffset - 4, PathHeader + PointStride, []);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(edited.AsSpan(path.AnchorOffset - 4, 4), path.Points.Length - 1);
            after = Splice(edited, path.Points[pointIndex], PointStride, []);
        }
        ApplyGameplayBytes(before, after);
    }

    /// <summary>End of the last patrol record, or the start of the block when there are none.</summary>
    private int PatrolBlockEnd() => patrols.Length == 0
        ? patrolCountOffset + 4
        : patrols.Max(p => p.Points.Length == 0 ? p.AnchorOffset + 8 : p.Points[^1] + PointStride);

    private static byte[] Splice(byte[] source, int at, int remove, ReadOnlySpan<byte> insert)
    {
        if (at < 0 || remove < 0 || at + remove > source.Length) throw new InvalidDataException("Patrol edit fell outside the DS1.");
        var result = new byte[source.Length - remove + insert.Length];
        source.AsSpan(0, at).CopyTo(result);
        insert.CopyTo(result.AsSpan(at));
        source.AsSpan(at + remove).CopyTo(result.AsSpan(at + insert.Length));
        return result;
    }

    // Re-reading validates the rewritten block before the edit is accepted, so a
    // malformed splice throws instead of leaving a corrupt document behind.
    private void ApplyGameplayBytes(byte[] before, byte[] after)
    {
        void Set(byte[] value) { bytes = value.ToArray(); ReadGameplay(gameplayOffset, gameplayTag); }
        Set(after);
        if (GameplayWarning is not null)
        {
            Set(before);
            throw new InvalidDataException("The edited patrol block did not re-read cleanly; the DS1 was left unchanged.");
        }
        History.Record(() => Set(before), () => Set(after));
    }
}
