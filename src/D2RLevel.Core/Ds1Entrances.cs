namespace D2RLevel.Core;

public sealed record ExitTile(int Layer, int X, int Y, int Orientation, int Main, int Sub, uint Raw)
{
    public bool IsExit => Main is >= 0 and < 8;
    public bool Hidden => (Raw & 0x80000000) != 0;
    public string Direction => Orientation == 10 ? "l" : "r";
}

public sealed partial class Ds1CollisionDocument
{
    public IReadOnlyList<ExitTile> ExitTiles()
    {
        var result = new List<ExitTile>();
        for (int layer = 0; layer < Walls.Count; layer++)
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
            {
                uint raw = Cell(Walls[layer], x, y); int orientation = Walls[layer].Orientations[y * Width + x];
                if ((raw & 255) != 0 && orientation is 10 or 11)
                    result.Add(new(layer, x, y, orientation, (int)((raw >> 20) & 63), (int)((raw >> 8) & 255), raw));
            }
        return result;
    }

    public string? ExitMoveWarning(int slot)
    {
        var tiles = ExitTiles().Where(t => t.IsExit && t.Main == slot).ToArray();
        if (slot is < 0 or > 7 || tiles.Length == 0) return "No exit tiles for this slot.";
        if (tiles.Any(t => !t.Hidden)) return "Visible doorway tiles cannot be moved independently of their surrounding artwork. Only hidden exit groups can move here.";
        if (tiles.Length > 8 || tiles.Select(t => (t.Layer, t.Orientation)).Distinct().Count() != 1)
            return "This exit spans multiple layers or directions, or an unsupported number of tiles.";
        var reached = new HashSet<(int, int)> { (tiles[0].X, tiles[0].Y) };
        bool changed;
        do { changed = false; foreach (var tile in tiles) if (!reached.Contains((tile.X, tile.Y)) && reached.Any(p => Math.Abs(p.Item1 - tile.X) + Math.Abs(p.Item2 - tile.Y) == 1)) changed |= reached.Add((tile.X, tile.Y)); } while (changed);
        return reached.Count == tiles.Length ? null : "This slot contains separate exit groups; their ownership is ambiguous.";
    }

    /// <summary>Move a complete hidden exit group without changing its slot, flags or artwork. Uses the shared scene history.</summary>
    public void MoveExit(int slot, int x, int y)
    {
        if (History.Shared) throw new InvalidOperationException("Move paired exits through their workspace links.");
        MoveExitCore(slot, x, y);
    }
    internal void MoveExitCore(int slot, int x, int y)
    {
        if (ExitMoveWarning(slot) is { } warning) throw new InvalidOperationException(warning);
        var tiles = ExitTiles().Where(t => t.IsExit && t.Main == slot).ToArray();
        int dx = x - tiles.Min(t => t.X), dy = y - tiles.Min(t => t.Y);
        if (dx == 0 && dy == 0) return;
        var source = tiles.Select(t => (t.Layer, t.X, t.Y)).ToHashSet();
        var changes = new Dictionary<int, (uint Before, uint After)>();
        void ChangeAt(int offset, uint value) => changes[offset] = (changes.TryGetValue(offset, out var old) ? old.Before : Read(offset), value);
        foreach (var tile in tiles)
        {
            int tx = tile.X + dx, ty = tile.Y + dy; Index(tx, ty);
            if (tx >= Width - 1 || ty >= Height - 1) throw new InvalidOperationException("Exit anchors must stay inside the map's final border row and column.");
            if (ProtectedTile?.Invoke(tile.X, tile.Y) == true || ProtectedTile?.Invoke(tx, ty) == true)
                throw new InvalidOperationException("An exit overlaps an owned collision footprint. Unlink that footprint before moving.");
            if (!CanBlock(tx, ty) || HasOverride(tx, ty)) throw new InvalidOperationException("Choose existing ground without a DS1 blocking override.");
            for (int layer = 0; layer < Walls.Count; layer++)
            {
                var wall = Walls[layer];
                if (!source.Contains((layer, tx, ty)) && (Cell(wall, tx, ty) != 0 || Read(wall.Offset + Width * Height * 4 + Index(tx, ty) * 4) != 0))
                    throw new InvalidOperationException("A destination wall layer is occupied.");
            }
        }
        foreach (var tile in tiles)
        {
            int offset = Walls[tile.Layer].Offset + Index(tile.X, tile.Y) * 4;
            ChangeAt(offset, 0); ChangeAt(offset + Width * Height * 4, 0);
        }
        foreach (var tile in tiles)
        {
            var wall = Walls[tile.Layer]; int target = wall.Offset + Index(tile.X + dx, tile.Y + dy) * 4;
            ChangeAt(target, tile.Raw); ChangeAt(target + Width * Height * 4, Read(wall.Offset + Width * Height * 4 + Index(tile.X, tile.Y) * 4));
        }
        void Apply(bool after)
        {
            foreach (var (offset, value) in changes) Write(offset, after ? value.After : value.Before);
            foreach (var wall in Walls) for (int i = 0; i < Width * Height; i++) wall.Orientations[i] = (int)(Read(wall.Offset + Width * Height * 4 + i * 4) & 255);
        }
        Apply(true); History.Record(() => Apply(false), () => Apply(true), this);
    }
}
