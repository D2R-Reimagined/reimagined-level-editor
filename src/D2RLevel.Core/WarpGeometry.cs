namespace D2RLevel.Core;

/// <summary>
/// A lvlwarp row as positions around its exit: the selection box the mouse highlights (screen pixels from the warp's base,
/// in the classic 32 × 16 pixel subtile projection), where a player arriving through it appears (OffsetX/Y) and where they
/// then walk to (ExitWalkX/Y), both in subtiles from the base. The base is the exit group's anchor tile corner.
/// </summary>
public sealed record WarpGeometry(string Name, int Id, string Direction, int SelectX, int SelectY, int SelectDX, int SelectDY,
    int OffsetX, int OffsetY, int ExitWalkX, int ExitWalkY, bool LitVersion, int Tiles, bool NoInteract)
{
    public const int SubtilesPerTile = 5;

    /// <summary>A screen-pixel offset as a floor offset in subtiles: +1 subtile X is (16, 8) pixels, +1 subtile Y is (−16, 8).</summary>
    public static (double X, double Y) ScreenToSubtiles(double x, double y) => (x / 32.0 + y / 16.0, y / 16.0 - x / 32.0);

    /// <summary>The selection box's corners on the floor, in subtiles from the base: clockwise from its top-left.</summary>
    public (double X, double Y)[] SelectionOnFloor() =>
    [
        ScreenToSubtiles(SelectX, SelectY), ScreenToSubtiles(SelectX + SelectDX, SelectY),
        ScreenToSubtiles(SelectX + SelectDX, SelectY + SelectDY), ScreenToSubtiles(SelectX, SelectY + SelectDY)
    ];

    public static WarpGeometry From(GameDataRow row)
    {
        int Number(string column) => EntranceConnections.Number(row[column], 0);
        return new(row["Name"], Number("Id"), row["Direction"], Number("SelectX"), Number("SelectY"), Number("SelectDX"), Number("SelectDY"),
            Number("OffsetX"), Number("OffsetY"), Number("ExitWalkX"), Number("ExitWalkY"), Number("LitVersion") != 0, Number("Tiles"), Number("NoInteract") != 0);
    }

    /// <summary>The definition of a warp Id for an exit facing <paramref name="direction"/> (l or r): its own direction first, then "b" (both).</summary>
    public static WarpGeometry? For(IReadOnlyList<GameDataRow> rows, string direction) =>
        rows.FirstOrDefault(r => r["Direction"].Equals(direction, StringComparison.OrdinalIgnoreCase)) is { } own ? From(own)
            : rows.FirstOrDefault(r => r["Direction"].Equals("b", StringComparison.OrdinalIgnoreCase)) is { } both ? From(both) : null;
}
