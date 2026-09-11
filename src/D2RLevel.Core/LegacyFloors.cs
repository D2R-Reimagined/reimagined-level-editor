using System.Buffers.Binary;

namespace D2RLevel.Core;

public readonly record struct FloorCell(uint Raw)
{
    public bool IsEmpty => (Raw & 255) == 0;
    public int Main => (int)((Raw >> 20) & 63);
    public int Sub => (int)((Raw >> 8) & 255);
}

public sealed record Ds1Floors(int Version, int Width, int Height, int Act, FloorCell[][] Layers)
{
    public static Ds1Floors Load(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        int version = reader.ReadInt32();
        if (version is < 16 or > 18) throw new InvalidDataException($"DS1 version {version}: floor preview supports versions 16–18.");
        int width = checked(reader.ReadInt32() + 1), height = checked(reader.ReadInt32() + 1);
        if (width is < 1 or > 512 || height is < 1 or > 512) throw new InvalidDataException("Invalid DS1 dimensions.");
        int act = checked(reader.ReadInt32() + 1);
        if (act is < 1 or > 5) throw new InvalidDataException("Invalid DS1 act.");
        reader.ReadInt32(); // Tag type; after floors, so not needed by this read-only view.
        int files = reader.ReadInt32();
        if (files is < 0 or > 1024) throw new InvalidDataException("Invalid DS1 filename count.");
        for (int i = 0; i < files; i++)
        {
            int length = 0;
            while (reader.ReadByte() != 0) if (++length > 4096) throw new InvalidDataException("Unterminated DS1 filename.");
        }
        int walls = reader.ReadInt32(), floors = reader.ReadInt32();
        if (walls is < 1 or > 4 || floors is < 1 or > 2) throw new InvalidDataException("Invalid DS1 layer counts.");
        long skip = (long)width * height * 4 * walls * 2;
        if (reader.BaseStream.Position + skip + (long)width * height * floors * 4 > reader.BaseStream.Length)
            throw new InvalidDataException("Truncated DS1 layers.");
        reader.BaseStream.Position += skip;
        var layers = new FloorCell[floors][];
        for (int l = 0; l < floors; l++)
        {
            layers[l] = new FloorCell[width * height];
            for (int i = 0; i < layers[l].Length; i++) layers[l][i] = new(reader.ReadUInt32());
        }
        return new(version, width, height, act, layers);
    }
}

public sealed record Dt1Floor(string Source, int Main, int Sub, int Rarity, byte[] Pixels);

public static class Dt1Reader
{
    public static IReadOnlyList<Dt1Floor> LoadFloors(string path)
    {
        var bytes = File.ReadAllBytes(path);
        void Range(long start, long length)
        {
            if (start < 0 || length < 0 || start + length > bytes.LongLength) throw new InvalidDataException($"Truncated DT1: {path}");
        }
        int I(int offset) { Range(offset, 4); return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)); }
        short S(int offset) { Range(offset, 2); return BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset, 2)); }
        if (I(0) != 7 || I(4) != 6) throw new InvalidDataException("Unsupported DT1 version (expected 7.6).");
        int count = I(268), headers = I(272);
        if (count is < 0 or > 100000 || headers < 276) throw new InvalidDataException("Invalid DT1 tile table.");
        Range(headers, (long)count * 96);
        var result = new List<Dt1Floor>();
        for (int i = 0; i < count; i++)
        {
            int h = headers + i * 96;
            if (I(h + 20) != 0) continue; // Floor orientation only.
            int blocks = I(h + 72), length = I(h + 76), n = I(h + 80);
            if (n is < 0 or > 4096) throw new InvalidDataException("Invalid DT1 block count.");
            Range(blocks, length); Range(blocks, (long)n * 20);
            var pixels = new byte[160 * 80];
            for (int b = 0; b < n; b++)
            {
                int bh = blocks + b * 20;
                int x = S(bh), y = S(bh + 2), format = (ushort)S(bh + 8), size = I(bh + 10), offset = I(bh + 16);
                if (offset < n * 20 || size < 0 || (long)offset + size > length) throw new InvalidDataException("Invalid DT1 block extent.");
                Range((long)blocks + offset, size);
                DecodeBlock(bytes.AsSpan(blocks + offset, size), format, pixels, x, y);
            }
            result.Add(new(path, I(h + 24), I(h + 28), I(h + 32), pixels));
        }
        return result;
    }

    public static void DecodeBlock(ReadOnlySpan<byte> data, int format, byte[] target, int originX, int originY)
    {
        if (target.Length != 160 * 80) throw new ArgumentException("Expected a 160 × 80 floor buffer.");
        void Put(int x, int y, byte color)
        {
            x += originX; y += originY;
            if (x is < 0 or >= 160 || y is < 0 or >= 80) throw new InvalidDataException("DT1 floor block exceeds its tile.");
            target[y * 160 + x] = color;
        }
        int p = 0;
        if (format == 1)
        {
            if (data.Length != 256) throw new InvalidDataException("Invalid isometric DT1 block size.");
            for (int y = 0; y < 15; y++)
            {
                int width = 4 * Math.Min(y + 1, 15 - y);
                for (int x = (32 - width) / 2; x < (32 + width) / 2; x++) Put(x, y, data[p++]);
            }
        }
        else if (format is 0x1001 or 0x2005)
        {
            int x = 0, y = 0;
            while (p < data.Length)
            {
                if (p + 2 > data.Length) throw new InvalidDataException("Truncated DT1 RLE command.");
                int skip = data[p++], run = data[p++];
                if (skip == 0 && run == 0) { x = 0; y++; continue; }
                x += skip;
                if (x + run > 32 || y >= 32 || p + run > data.Length) throw new InvalidDataException("Invalid DT1 RLE run.");
                for (int j = 0; j < run; j++) Put(x++, y, data[p++]);
            }
        }
        else throw new InvalidDataException($"Unsupported DT1 block format 0x{format:X}.");
    }
}

public sealed record LegacyFloorScene(Ds1Floors Map, IReadOnlyDictionary<(int Main, int Sub), Dt1Floor[]> Tiles,
    byte[] Palette, string Ds1Path, string[] Dt1Paths, uint Mask)
{
    public LegacyCollision? Collision { get; init; }
    public FloorCell FloorAt(int layer, int x, int y) => Collision is { } c
        ? new(c.Document.Cell(c.Document.Floors[layer], x, y)) : Map.Layers[layer][y * Map.Width + x];
    public int MissingCells => Enumerable.Range(0, Map.Layers.Length).Sum(l =>
        Enumerable.Range(0, Map.Width * Map.Height).Count(i => { var c = FloorAt(l, i % Map.Width, i / Map.Width); return !c.IsEmpty && !Tiles.ContainsKey((c.Main, c.Sub)); }));

    public static LegacyFloorScene Load(string ds1Path, AssetResolver resolver, CancellationToken token, string? contextDataRoot = null, LevelTileset? context = null)
    {
        string Normalize(string p) => p.Replace('\\', '/').ToLowerInvariant();
        var overrideRoot = contextDataRoot ?? PresetPairing.Split(ds1Path, "global/tiles")?.DataRoot;
        string Resolve(string path)
        {
            var fallback = resolver.Resolve(path); // Validate table-provided paths before trying the mod.
            if (overrideRoot is not null)
            {
                var candidate = Path.Combine(overrideRoot, path[5..].Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate)) return candidate;
            }
            return fallback;
        }
        string[] paths;
        uint mask;
        if (context is not null)
        {
            context.Validate();
            mask = context.Mask;
            paths = context.Files.Select(Resolve).ToArray();
        }
        else
        {
        var relative = Normalize(PresetPairing.RelativeDs1(ds1Path, resolver));
        var presets = Table(Resolve("data/global/excel/lvlprest.txt"));
        var matches = presets.Where(r => Enumerable.Range(1, 6).Any(i => r.TryGetValue($"File{i}", out var f) && Normalize(f) == relative)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("DS1 must uniquely match a LvlPrest File1–6 entry inside the extraction's global/tiles folder.");
        var preset = matches[0];
        string levelId = preset["LevelId"];
        if (levelId == "0") throw new InvalidDataException("This preset needs explicit level context; only fixed LevelId presets are supported yet.");
        var level = Table(Resolve("data/global/excel/levels.txt")).Single(r => r.GetValueOrDefault("Id") == levelId);
        var type = Table(Resolve("data/global/excel/lvltypes.txt")).Single(r => r.GetValueOrDefault("Id") == level["LevelType"]);
        mask = uint.Parse(preset["Dt1Mask"], System.Globalization.CultureInfo.InvariantCulture);
        paths = Enumerable.Range(0, 32).Where(i => (mask & (1u << i)) != 0)
            .Select(i => type[$"File {i + 1}"]).Where(p => p.Length > 0 && p != "0")
            .Select(p => Resolve("data/global/tiles/" + p.Replace('\\', '/'))).ToArray();
        }
        var tiles = new List<Dt1Floor>();
        foreach (var path in paths) { token.ThrowIfCancellationRequested(); tiles.AddRange(Dt1Reader.LoadFloors(path)); }
        var map = Ds1Floors.Load(ds1Path);
        var palette = File.ReadAllBytes(Resolve($"data/global/palette/act{map.Act}/pal.dat"));
        if (palette.Length != 768) throw new InvalidDataException("Expected a 256-color BGR act palette.");
        var collisionTiles = new List<Dt1CollisionTile>();
        foreach (var path in paths) { token.ThrowIfCancellationRequested(); collisionTiles.AddRange(LegacyCollision.ReadTiles(path)); }
        return new(map, tiles.GroupBy(t => (t.Main, t.Sub)).ToDictionary(g => g.Key, g => g.ToArray()), palette, ds1Path, paths, mask)
            { Collision = new(Ds1CollisionDocument.Load(ds1Path), collisionTiles) };
    }

    private static Dictionary<string, string>[] Table(string path)
    {
        var lines = File.ReadAllLines(path);
        var headers = lines[0].Split('\t');
        return lines.Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)).Select(line =>
        {
            var fields = line.Split('\t');
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Math.Min(headers.Length, fields.Length); i++) row[headers[i]] = fields[i];
            return row;
        }).ToArray();
    }
}
