using System.Buffers.Binary;

namespace D2RLevel.Core;

public sealed partial class Ds1CollisionDocument
{
    /// <summary>Grow towards positive X/Y in a detached copy; preserve every original cell and gameplay byte.</summary>
    public Ds1CollisionDocument GrowCopy(int width, int height)
    {
        if (History.Shared) throw new InvalidOperationException("Detach linked authoring before resizing.");
        if (width < Width || height < Height || width > 512 || height > 512)
            throw new ArgumentOutOfRangeException(nameof(width), "Growth must retain the original grid and remain within 512 cells per axis.");
        int start = Walls[0].Offset, oldSize = checked(Width * Height * 4);
        int layers = (gameplayOffset - start) / oldSize;
        if (start + layers * oldSize != gameplayOffset) throw new InvalidDataException("Unaligned DS1 layers.");
        int newSize = checked(width * height * 4);
        var result = new byte[checked(start + layers * newSize + bytes.Length - gameplayOffset)];
        bytes.AsSpan(0, start).CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), width - 1);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), height - 1);
        for (int layer = 0; layer < layers; layer++)
            for (int y = 0; y < Height; y++)
                bytes.AsSpan(start + layer * oldSize + y * Width * 4, Width * 4)
                    .CopyTo(result.AsSpan(start + layer * newSize + y * width * 4));
        bytes.AsSpan(gameplayOffset).CopyTo(result.AsSpan(start + layers * newSize));
        return new Ds1CollisionDocument(SourcePath, result);
    }
}
