using System.Security.Cryptography;

namespace D2RLevel.Core;

public sealed record LinkedTile(int X, int Y);
public sealed record TileBaseline(int X, int Y, bool Blocked);

public sealed partial class Ds1CollisionDocument
{
    internal Func<int, bool>? ProtectedUnit { get; set; }
    internal bool LinkedUnitMove;
    internal bool FloorOverride(LinkedTile tile)
    {
        var layer = Floors.FirstOrDefault(l => (Cell(l, tile.X, tile.Y) & 255) != 0)
            ?? throw new InvalidOperationException($"No editable floor at ({tile.X}, {tile.Y}).");
        return (Cell(layer, tile.X, tile.Y) & Unwalkable) != 0;
    }
    internal void SetFloorOverrides(IEnumerable<TileBaseline> states)
    {
        var changes = new List<Change>();
        foreach (var state in states)
        {
            int cell = Index(state.X, state.Y);
            var layer = Floors.FirstOrDefault(l => (Cell(l, state.X, state.Y) & 255) != 0)
                ?? throw new InvalidOperationException($"No editable floor at ({state.X}, {state.Y}).");
            int offset = layer.Offset + cell * 4; uint old = Read(offset);
            uint next = state.Blocked ? old | Unwalkable : old & ~Unwalkable;
            if (old != next) changes.Add(new(offset, old, next));
        }
        if (changes.Count == 0) return;
        foreach (var c in changes) Write(c.Offset, c.After);
        RecordChanges(changes.ToArray());
    }
    public string LinkFingerprint()
    {
        var data = bytes.ToArray();
        foreach (var layer in Floors.Concat(Walls)) for (int i = 0; i < Width * Height; i++) data[layer.Offset + i * 4 + 2] &= 0xfd;
        foreach (int offset in unitOffsets) Array.Clear(data, offset + 8, 8);
        foreach (var path in patrols) foreach (int offset in path.Points.Prepend(path.AnchorOffset)) Array.Clear(data, offset, 8);
        return Convert.ToHexString(SHA256.HashData(data));
    }
}
