namespace D2RLevel.Core;

public sealed record ModelFootprint(string Name, double MinX, double MinZ, double MaxX, double MaxZ)
{
    // A reviewable, conservative rectangle, not a mesh-to-gameplay collision conversion.
    public (int X, int Y)[] Tiles(int width, int height, double unitsPerTile)
    {
        if (!double.IsFinite(unitsPerTile) || unitsPerTile <= 0 ||
            new[] { MinX, MinZ, MaxX, MaxZ }.Any(v => !double.IsFinite(v)) || MaxX <= MinX || MaxZ <= MinZ)
            throw new ArgumentException("Invalid footprint or HD units per tile.");
        int x0 = (int)Math.Clamp(Math.Floor(MinX / unitsPerTile), 0, width);
        int y0 = (int)Math.Clamp(Math.Floor(MinZ / unitsPerTile), 0, height);
        int x1 = (int)Math.Clamp(Math.Ceiling(MaxX / unitsPerTile), 0, width);
        int y1 = (int)Math.Clamp(Math.Ceiling(MaxZ / unitsPerTile), 0, height);
        if ((long)(x1 - x0) * (y1 - y0) > 4096) throw new InvalidOperationException("Footprint covers over 4,096 tiles. Select a smaller prop or check the scale.");
        return Enumerable.Range(y0, Math.Max(0, y1 - y0)).SelectMany(y => Enumerable.Range(x0, Math.Max(0, x1 - x0)).Select(x => (x, y))).ToArray();
    }
}
