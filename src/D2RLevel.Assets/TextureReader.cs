using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace D2RLevel.Assets;

public sealed record TexturePixels(int Width, int Height, byte[] Rgba);

public static class TextureReader
{
    public static TexturePixels Load(string path, int maxDimension = 1024)
    {
        using var stream = File.OpenRead(path);
        using var r = new BinaryReader(stream);
        if (stream.Length < 44) throw new InvalidDataException("Truncated D2R texture.");
        var magic = r.ReadUInt32();
        if (magic != 0x2845443C) throw new InvalidDataException($"Unknown D2R texture magic {magic:X8}.");
        var format = r.ReadUInt16();
        r.ReadUInt16();
        var width = r.ReadInt32();
        var height = r.ReadInt32();
        stream.Position = 28;
        var mips = r.ReadInt32();
        r.ReadInt32();
        if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || mips is < 1 or > 16)
            throw new InvalidDataException("Invalid texture dimensions or mip count.");
        var entries = new (int Size, int Offset)[mips];
        for (int i = 0; i < mips; i++)
        {
            var size = r.ReadInt32();
            var offsetField = checked((int)stream.Position);
            entries[i] = (size, checked(offsetField + r.ReadInt32()));
        }
        int mip = 0;
        while (mip + 1 < mips && Math.Max(width >> mip, height >> mip) > maxDimension) mip++;
        width = Math.Max(1, width >> mip); height = Math.Max(1, height >> mip);
        var entry = entries[mip];
        if (entry.Offset < 36 + mips * 8 || entry.Size <= 0 || (long)entry.Offset + entry.Size > stream.Length)
            throw new InvalidDataException("Invalid texture mip range.");
        if (width > 4096 || height > 4096) throw new InvalidDataException("Texture has no suitable preview mip.");
        stream.Position = entry.Offset;
        var data = r.ReadBytes(entry.Size);
        if (format == 31)
        {
            if (data.Length != checked(width * height * 4)) throw new InvalidDataException("Invalid RGBA mip size.");
            return new(width, height, data);
        }
        var compression = format switch
        {
            57 or 58 => CompressionFormat.Bc1,
            61 or 62 => CompressionFormat.Bc3,
            63 => CompressionFormat.Bc4,
            _ => throw new NotSupportedException($"D2R texture format {format} is not supported yet.")
        };
        int expected = checked(((width + 3) / 4) * ((height + 3) / 4) * (compression == CompressionFormat.Bc3 ? 16 : 8));
        if (data.Length != expected) throw new InvalidDataException("Invalid compressed mip size.");
        var colors = new BcDecoder().DecodeRaw(data, width, height, compression);
        var rgba = new byte[checked(width * height * 4)];
        for (var i = 0; i < colors.Length; i++)
        { rgba[i * 4] = colors[i].r; rgba[i * 4 + 1] = colors[i].g; rgba[i * 4 + 2] = colors[i].b; rgba[i * 4 + 3] = colors[i].a; }
        return new(width, height, rgba);
    }
}
