using System.Buffers.Binary;

namespace D2RLevel.Core;

/// <summary>Authors a hidden Vis marker in an exported DS1 copy. Levels/Warp links are registered separately.</summary>
public static class Ds1WarpAuthoring
{
    public static byte[] AddMarker(Ds1CollisionDocument source, int layer, int x, int y, int slot)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (layer < 0 || layer >= source.Walls.Count || slot is < 0 or > 7 ||
            x < 1 || y < 1 || x >= source.Width - 1 || y >= source.Height - 1)
            throw new ArgumentOutOfRangeException(nameof(slot), "A warp requires an interior tile, wall layer and Vis slot 0–7.");
        if (source.History.Shared)
            throw new InvalidOperationException("Export the paired workspace before authoring its warp markers.");
        if (!source.CanBlock(x, y) || source.HasOverride(x, y) ||
            source.Walls.Any(w => source.Cell(w, x, y) != 0))
            throw new InvalidOperationException("The marker needs an unoccupied floor tile without explicit blocking.");
        foreach (var wall in source.Walls)
            for (int row = 0; row < source.Height; row++)
                for (int col = 0; col < source.Width; col++)
                    if (wall.Orientations[row * source.Width + col] is 10 or 11 &&
                        (source.Cell(wall, col, row) & 255) != 0 &&
                        ((source.Cell(wall, col, row) >> 20) & 63) == slot)
                        throw new InvalidOperationException("That Vis slot already has a marker in this preset.");
        var result = source.Serialize();
        int index = y * source.Width + x;
        int offset = source.Walls[layer].Offset;
        // Hidden left-exit marker: no DT1 wall collision or lit tile substitution.
        // The selected LvlWarp record must use LitVersion=0; its offsets determine arrival.
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset + index * 4), 0x80000081u | ((uint)slot << 20));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset + source.Width * source.Height * 4 + index * 4), 10);
        return result;
    }
}
