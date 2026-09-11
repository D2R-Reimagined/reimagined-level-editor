namespace D2RLevel.Core;

public sealed partial class Ds1CollisionDocument
{
    /// <summary>Construct a canonical v18 map, including an empty patrol block. No source file is required.</summary>
    public static Ds1CollisionDocument Create(string path, int width, int height, int act,
        uint floor = 0, int floorLayers = 1, int wallLayers = 1)
    {
        if (width is < 1 or > 512 || height is < 1 or > 512 || act is < 1 or > 5 ||
            floorLayers is < 1 or > 2 || wallLayers is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(width), "Invalid map dimensions, act or layer count.");
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            foreach (int value in new[] { 18, width - 1, height - 1, act - 1, 0, 0, wallLayers, floorLayers }) w.Write(value);
            w.Write(new byte[width * height * wallLayers * 8]);
            for (int layer = 0; layer < floorLayers; layer++)
                for (int cell = 0; cell < width * height; cell++) w.Write(layer == 0 ? floor : 0u);
            w.Write(new byte[width * height * 4]); // Shadow layer; tag type 0 has no groups.
            w.Write(0); // Units.
            w.Write(0); // Patrol count, allowing the first route to be authored.
        }
        return new(path, stream.ToArray());
    }

    public static uint FloorKey(int main, int sub)
    {
        if (main is < 0 or > 63 || sub is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(main));
        return 1u | ((uint)main << 20) | ((uint)sub << 8);
    }

    // Paired documents must go through PlacementLinks so the structural fingerprint is updated transactionally.
    public int PaintFloor(int layer, IEnumerable<(int X, int Y)> cells, uint tile)
    {
        if (History.Shared) throw new InvalidOperationException("Paint this paired map through its workspace links.");
        return PaintFloorCore(layer, cells, tile);
    }
    internal int PaintFloorCore(int layer, IEnumerable<(int X, int Y)> cells, uint tile)
    {
        if (layer < 0 || layer >= Floors.Count) throw new ArgumentOutOfRangeException(nameof(layer));
        if (tile != 0 && (tile & 255) == 0) throw new ArgumentException("An empty tile must be zero.");
        var changes = new List<Change>();
        foreach (var (x, y) in cells.Distinct())
        {
            int index = Index(x, y);
            if (ProtectedTile?.Invoke(x, y) == true) throw new InvalidOperationException("This floor belongs to a linked footprint. Unlink it before painting.");
            int offset = Floors[layer].Offset + index * 4;
            uint before = Read(offset);
            // Retain explicit blocking when replacing an occupied floor. Erasing removes that layer's tile.
            uint after = tile == 0 ? 0 : tile | (before & Unwalkable);
            if (before != after) changes.Add(new(offset, before, after));
        }
        if (changes.Count == 0) return 0;
        foreach (var change in changes) Write(change.Offset, change.After);
        RecordChanges(changes.ToArray());
        return changes.Count;
    }

    internal int AppendUnitCore(int type, int id, int x, int y, uint flags)
    {
        if (GameplayWarning is not null) throw new InvalidOperationException(GameplayWarning);
        if (type is not (1 or 2) || id < 0 || x < 0 || y < 0 || x >= Width * 5 || y >= Height * 5)
            throw new ArgumentOutOfRangeException(nameof(x), "Invalid placement type, ID or subtile coordinates.");
        if (Units.Count >= 100000) throw new InvalidOperationException("Too many placements.");
        if (patrols.Any(p => Anchored(p, x, y))) throw new InvalidOperationException("The placement would share an existing patrol anchor.");
        int index = Units.Count, offset = gameplayOffset + 4 + index * 20;
        var before = bytes.ToArray();
        using var output = new MemoryStream();
        output.Write(bytes, 0, offset);
        using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true))
        { writer.Write(type); writer.Write(id); writer.Write(x); writer.Write(y); writer.Write(flags); }
        output.Write(bytes, offset, bytes.Length - offset);
        var after = output.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(after.AsSpan(gameplayOffset, 4), index + 1);
        void Set(byte[] value) { bytes = value.ToArray(); ReadGameplay(gameplayOffset, gameplayTag); }
        Set(after);
        if (GameplayWarning is not null) { Set(before); throw new InvalidDataException("Placement insertion failed validation."); }
        History.Record(() => Set(before), () => Set(after));
        return index;
    }
}
