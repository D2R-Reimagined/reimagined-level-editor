namespace D2RLevel.Core;

public static class GroundBrush
{
    public static (int X, int Y)[] Flood(Ds1CollisionDocument map, int layer, int x, int y)
    {
        var target = new FloorCell(map.Cell(map.Floors[layer], x, y));
        var pending = new Queue<(int X, int Y)>(); var visited = new HashSet<(int X, int Y)>();
        var result = new List<(int X, int Y)>(); pending.Enqueue((x, y));
        while (pending.TryDequeue(out var p))
        {
            if (p.X < 0 || p.Y < 0 || p.X >= map.Width || p.Y >= map.Height || !visited.Add(p)) continue;
            var cell = new FloorCell(map.Cell(map.Floors[layer], p.X, p.Y));
            if (cell.IsEmpty != target.IsEmpty || (!cell.IsEmpty && (cell.Main != target.Main || cell.Sub != target.Sub))) continue;
            result.Add(p);
            pending.Enqueue((p.X - 1, p.Y)); pending.Enqueue((p.X + 1, p.Y)); pending.Enqueue((p.X, p.Y - 1)); pending.Enqueue((p.X, p.Y + 1));
        }
        return result.ToArray();
    }
}
