namespace D2RLevel.Core;

/// <summary>
/// A ranked place for an exit's anchor tile. <see cref="Openness"/> is the share of walkable subtiles around where players
/// arrive; <see cref="NearestExit"/> the distance in tiles to another exit slot (null when there is none); <see cref="Facing"/>
/// how far toward the destination the tile lies, from −1 (away) to 1 (toward), when a direction is known.
/// </summary>
public sealed record ExitCandidate(int X, int Y, double Score, double Openness, double? NearestExit, double? Facing);

/// <summary>
/// Suggests where an exit slot's anchor could go. Every suggestion passes the same checks as moving or adding the exit,
/// and puts the warp's arrival and walk-to points on walkable ground. The rest is a preference, not a rule:
/// open ground around the arrival point, room from other exits and the map edge, and the side facing the destination.
/// </summary>
public static class ExitPlacement
{
    /// <summary>Radius, in subtiles, of the square sampled around the arrival point.</summary>
    private const int OpenRadius = 3;

    public static ExitCandidate[] Suggest(Ds1CollisionDocument map, LegacyCollision? collision, int slot, WarpGeometry? warp,
        (double X, double Y)? towards = null, int count = 5, CancellationToken token = default)
    {
        var group = map.ExitTiles().Where(t => t.IsExit && t.Main == slot).ToArray();
        // A group is placed by its top-left tile; its warp is measured from its anchor.
        int anchorDx = 0, anchorDy = 0;
        (int X, int Y)? current = null;
        Func<int, int, string?> problem;
        if (group.Length > 0)
        {
            var anchor = Ds1CollisionDocument.Anchor(group);
            anchorDx = anchor.X - group.Min(t => t.X); anchorDy = anchor.Y - group.Min(t => t.Y);
            current = (anchor.X, anchor.Y);
            problem = map.ExitMoveCheck(slot);
        }
        else problem = map.ExitAddCheck(slot);
        var walkable = WalkableSubtiles(map, collision, token);
        int width = map.Width * 5, height = map.Height * 5;
        bool Walkable(int sx, int sy) => sx >= 0 && sy >= 0 && sx < width && sy < height && walkable[sy * width + sx];
        var others = map.ExitTiles().Where(t => t.IsExit && t.Main != slot).Select(t => (t.X, t.Y)).Distinct().ToArray();
        (double X, double Y)? direction = towards is { } d && Math.Sqrt(d.X * d.X + d.Y * d.Y) is var length && length > 1e-9
            ? (d.X / length, d.Y / length) : null;
        var scored = new List<ExitCandidate>();
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
            {
                token.ThrowIfCancellationRequested();
                // Suggestions are alternatives; the exit's present spot is not one.
                if (current == (x, y) || problem(x - anchorDx, y - anchorDy) is not null) continue;
                // Without a warp row, players are assumed to arrive at the anchor's centre.
                int arriveX = x * 5 + (warp?.OffsetX ?? 2), arriveY = y * 5 + (warp?.OffsetY ?? 2);
                int walkX = arriveX + (warp?.ExitWalkX ?? 0), walkY = arriveY + (warp?.ExitWalkY ?? 0);
                if (!Walkable(arriveX, arriveY) || !Walkable(walkX, walkY)) continue;
                int open = 0, sampled = 0;
                for (int sy = arriveY - OpenRadius; sy <= arriveY + OpenRadius; sy++)
                    for (int sx = arriveX - OpenRadius; sx <= arriveX + OpenRadius; sx++)
                    { sampled++; if (Walkable(sx, sy)) open++; }
                double openness = (double)open / sampled;
                double? nearest = others.Length == 0 ? null : others.Min(o => Math.Sqrt((o.X - x) * (o.X - x) + (o.Y - y) * (o.Y - y)));
                int margin = Math.Min(Math.Min(x, y), Math.Min(map.Width - 1 - x, map.Height - 1 - y));
                double? facing = null;
                if (direction is { } dir)
                {
                    // Position from the map centre, scaled so each edge is ±1 whatever the map's proportions.
                    double rx = (x + .5) / map.Width * 2 - 1, ry = (y + .5) / map.Height * 2 - 1;
                    facing = Math.Clamp(rx * dir.X + ry * dir.Y, -1, 1);
                }
                double score = 4 * openness + 2 * Math.Min(nearest ?? 10, 10) / 10 + Math.Min(margin, 4) / 4.0
                    + (facing is { } f ? 3 * (f + 1) / 2 : 0);
                scored.Add(new(x, y, score, openness, nearest, facing));
            }
        // Spread the picks out so the list offers real alternatives rather than one spot and its neighbours. Among spots scoring
        // nearly as well as the best left, take the one farthest from those already picked; the first is the most central.
        double CentreDistance(ExitCandidate c) => Math.Abs(c.X + .5 - map.Width / 2.0) + Math.Abs(c.Y + .5 - map.Height / 2.0);
        double Gap(ExitCandidate c, List<ExitCandidate> picked) => picked.Count == 0 ? -CentreDistance(c) : picked.Min(p => Math.Max(Math.Abs(p.X - c.X), Math.Abs(p.Y - c.Y)));
        var picks = new List<ExitCandidate>();
        var remaining = scored.OrderByDescending(c => c.Score).ThenBy(c => c.Y).ThenBy(c => c.X).ToList();
        while (picks.Count < count)
        {
            remaining.RemoveAll(c => picks.Any(p => Math.Max(Math.Abs(p.X - c.X), Math.Abs(p.Y - c.Y)) < 3));
            if (remaining.Count == 0) break;
            double floor = remaining[0].Score - .25;
            var next = remaining.TakeWhile(c => c.Score >= floor).OrderByDescending(c => Gap(c, picks)).First();
            picks.Add(next); remaining.Remove(next);
        }
        return picks.OrderByDescending(c => c.Score).ToArray();
    }

    /// <summary>
    /// Walkable subtiles, row-major across the whole map. With DT1 collision a subtile is walkable when no tile on it blocks
    /// movement; without it, any floor tile without a DS1 blocking override counts. Exit markers have no DT1 tile, so they
    /// add no blocking of their own.
    /// </summary>
    private static bool[] WalkableSubtiles(Ds1CollisionDocument map, LegacyCollision? collision, CancellationToken token)
    {
        int width = map.Width * 5;
        var result = new bool[width * map.Height * 5];
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
            {
                token.ThrowIfCancellationRequested();
                bool ground = map.CanBlock(x, y) && !map.HasOverride(x, y);
                var flags = ground ? collision?.At(x, y).Flags : null;
                for (int sy = 0; sy < 5; sy++)
                    for (int sx = 0; sx < 5; sx++)
                        result[(y * 5 + sy) * width + x * 5 + sx] = ground && (flags is null || (flags[sy * 5 + sx] & 9) == 0);
            }
        return result;
    }
}
