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
    /// <summary>The tile a group's warp is measured from: its scan anchor (a hidden tile, or sub-index 0 or 4), else its top-left tile.</summary>
    public static ExitTile Anchor(IReadOnlyCollection<ExitTile> group) =>
        group.Where(t => t.Hidden || t.Sub is 0 or 4).OrderBy(t => t.Y).ThenBy(t => t.X).FirstOrDefault() ?? group.OrderBy(t => t.Y).ThenBy(t => t.X).First();

    /// <summary>Why the slot's exit group cannot move so its top-left tile is at (x, y), or null when it can. Changes nothing.</summary>
    public string? ExitMoveProblem(int slot, int x, int y) => ExitMoveCheck(slot)(x, y);

    /// <summary>
    /// The move check for one slot with the group scanned once, so many destinations can be tested cheaply.
    /// The returned function takes the group's new top-left tile.
    /// </summary>
    internal Func<int, int, string?> ExitMoveCheck(int slot)
    {
        if (ExitMoveWarning(slot) is { } warning) return (_, _) => warning;
        var tiles = ExitTiles().Where(t => t.IsExit && t.Main == slot).ToArray();
        int minX = tiles.Min(t => t.X), minY = tiles.Min(t => t.Y);
        var source = tiles.Select(t => (t.Layer, t.X, t.Y)).ToHashSet();
        return (x, y) =>
        {
            int dx = x - minX, dy = y - minY;
            if (dx == 0 && dy == 0) return null;
            foreach (var tile in tiles)
            {
                if (ProtectedTile?.Invoke(tile.X, tile.Y) == true)
                    return "An exit overlaps an owned collision footprint. Unlink that footprint before moving.";
                if (ExitCellProblem(tile.X + dx, tile.Y + dy, source) is { } problem) return problem;
            }
            return null;
        };
    }

    /// <summary>Why one exit tile cannot occupy (x, y). Wall cells the group itself is leaving count as free.</summary>
    private string? ExitCellProblem(int x, int y, IReadOnlySet<(int Layer, int X, int Y)> vacated)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return "The exit would leave the map.";
        if (x >= Width - 1 || y >= Height - 1) return "Exit anchors must stay inside the map's final border row and column.";
        if (ProtectedTile?.Invoke(x, y) == true) return "An exit overlaps an owned collision footprint. Unlink that footprint before moving.";
        if (!CanBlock(x, y) || HasOverride(x, y)) return "Choose existing ground without a DS1 blocking override.";
        for (int layer = 0; layer < Walls.Count; layer++)
        {
            var wall = Walls[layer];
            if (!vacated.Contains((layer, x, y)) && (Cell(wall, x, y) != 0 || Read(wall.Offset + Width * Height * 4 + Index(x, y) * 4) != 0))
                return "A destination wall layer is occupied.";
        }
        return null;
    }

    /// <summary>Why a new hidden exit for an unused slot cannot be placed at (x, y), or null when it can. Changes nothing.</summary>
    public string? ExitAddProblem(int slot, int x, int y) => ExitAddCheck(slot)(x, y);

    internal Func<int, int, string?> ExitAddCheck(int slot)
    {
        if (slot is < 0 or > 7) return (_, _) => "Exit slots are numbered 0–7.";
        if (ExitTiles().Any(t => t.IsExit && t.Main == slot)) return (_, _) => "This slot already has exit tiles in this map. Move them instead.";
        var none = new HashSet<(int, int, int)>();
        return (x, y) => ExitCellProblem(x, y, none);
    }

    /// <summary>The raw wall cell of a new hidden exit: hidden flag, the slot as main index, sub-index 0 and the usual property byte.
    /// This is the encoding vanilla single-tile hidden exits (stairs, holes, hidden warps) use.</summary>
    public static uint HiddenExitCell(int slot) => 0x80000081u | ((uint)slot << 20);

    /// <summary>Write a one-tile hidden exit for an unused slot. Orientation 10 faces "l", 11 faces "r". Uses the shared scene history.</summary>
    public void AddExit(int slot, int x, int y, int orientation)
    {
        if (History.Shared) throw new InvalidOperationException("Add paired exits through their workspace links.");
        AddExitCore(slot, x, y, orientation);
    }

    internal void AddExitCore(int slot, int x, int y, int orientation)
    {
        if (orientation is not (10 or 11)) throw new ArgumentOutOfRangeException(nameof(orientation), "Exit orientation must be 10 (l) or 11 (r).");
        if (ExitAddProblem(slot, x, y) is { } problem) throw new InvalidOperationException(problem);
        var wall = Walls[0]; int index = Index(x, y), cell = wall.Offset + index * 4, orient = cell + Width * Height * 4;
        uint cellBefore = Read(cell), orientBefore = Read(orient);
        void Apply(bool after)
        {
            Write(cell, after ? HiddenExitCell(slot) : cellBefore); Write(orient, after ? (uint)orientation : orientBefore);
            wall.Orientations[index] = (int)(Read(orient) & 255);
        }
        Apply(true); History.Record(() => Apply(false), () => Apply(true), this);
    }

    internal void MoveExitCore(int slot, int x, int y)
    {
        if (ExitMoveProblem(slot, x, y) is { } problem) throw new InvalidOperationException(problem);
        var tiles = ExitTiles().Where(t => t.IsExit && t.Main == slot).ToArray();
        int dx = x - tiles.Min(t => t.X), dy = y - tiles.Min(t => t.Y);
        if (dx == 0 && dy == 0) return;
        var changes = new Dictionary<int, (uint Before, uint After)>();
        void ChangeAt(int offset, uint value) => changes[offset] = (changes.TryGetValue(offset, out var old) ? old.Before : Read(offset), value);
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
