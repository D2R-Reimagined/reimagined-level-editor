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
        if (act is < 1 or > 6) throw new InvalidDataException("Invalid DS1 act.");
        act = Math.Min(act, 5); // Expansion maps store one act past the five that have palettes.
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

/// <summary>Where a scene's DT1 tileset came from, in descending order of authority.</summary>
public enum TilesetSource
{
    /// <summary>A level project's stored tileset.</summary>
    Project,
    /// <summary>LvlPrest → Levels → LvlTypes, the set the game itself would load.</summary>
    LevelTables,
    /// <summary>The DS1's own header, for a room no LvlPrest row assigns to a level.</summary>
    Ds1Header,
}

public sealed record LegacyFloorScene(Ds1Floors Map, IReadOnlyDictionary<(int Main, int Sub), Dt1Floor[]> Tiles,
    byte[] Palette, string Ds1Path, string[] Dt1Paths, uint Mask, TilesetSource TilesetSource = TilesetSource.LevelTables)
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
        var collision = Ds1CollisionDocument.Load(ds1Path);
        string[] paths;
        uint mask;
        TilesetSource source;
        if (context is not null)
        {
            context.Validate();
            mask = context.Mask;
            paths = context.Files.Select(Resolve).ToArray();
            source = TilesetSource.Project;
        }
        else if (LevelTablePreset(ds1Path, resolver, Normalize, Resolve) is { } preset)
        {
            var level = Table(Resolve("data/global/excel/levels.txt")).Single(r => r.GetValueOrDefault("Id") == preset["LevelId"]);
            var type = Table(Resolve("data/global/excel/lvltypes.txt")).Single(r => r.GetValueOrDefault("Id") == level["LevelType"]);
            mask = uint.Parse(preset["Dt1Mask"], System.Globalization.CultureInfo.InvariantCulture);
            paths = Enumerable.Range(0, 32).Where(i => (mask & (1u << i)) != 0)
                .Select(i => type[$"File {i + 1}"]).Where(p => p.Length > 0 && p != "0")
                .Select(p => Resolve("data/global/tiles/" + p.Replace('\\', '/'))).ToArray();
            source = TilesetSource.LevelTables;
        }
        else
        {
            // Rooms the level generator assembles carry LevelId 0, and a hand-made map has no
            // LvlPrest row at all; neither names a level, so neither reaches LvlTypes. The DS1's
            // own header still lists the DT1s it was authored against.
            paths = EmbeddedTileset(collision, Resolve);
            if (paths.Length == 0) throw new InvalidDataException(
                "This DS1 is assigned to no level and its header lists no DT1 file that exists here. Open it through a level project that names the tileset.");
            mask = paths.Length >= 32 ? uint.MaxValue : (1u << paths.Length) - 1;
            source = TilesetSource.Ds1Header;
        }
        // No LvlTypes row lists blank.dt1, yet maps across every act fill unused ground with its
        // floor 30 — the engine keeps it loaded for all of them. Appending it last leaves the
        // level's own tiles first in each variant list.
        string blank = Resolve("data/global/tiles/act1/outdoors/blank.dt1");
        if (File.Exists(blank) && !paths.Contains(blank, StringComparer.OrdinalIgnoreCase)) paths = [.. paths, blank];
        var tiles = new List<Dt1Floor>();
        foreach (var path in paths) { token.ThrowIfCancellationRequested(); tiles.AddRange(Dt1Reader.LoadFloors(path)); }
        var map = Ds1Floors.Load(ds1Path);
        var palette = File.ReadAllBytes(Resolve($"data/global/palette/act{map.Act}/pal.dat"));
        if (palette.Length != 768) throw new InvalidDataException("Expected a 256-color BGR act palette.");
        var collisionTiles = new List<Dt1CollisionTile>();
        foreach (var path in paths) { token.ThrowIfCancellationRequested(); collisionTiles.AddRange(LegacyCollision.ReadTiles(path)); }
        return new(map, tiles.GroupBy(t => (t.Main, t.Sub)).ToDictionary(g => g.Key, g => g.ToArray()), palette, ds1Path, paths, mask, source)
            { Collision = new(collision, collisionTiles) };
    }

    /// <summary>The LvlPrest row that assigns this DS1 to a level, or null when none does.</summary>
    private static Dictionary<string, string>? LevelTablePreset(string ds1Path, AssetResolver resolver,
        Func<string, string> normalize, Func<string, string> resolve)
    {
        string relative;
        // A map outside the extraction, or one whose name is ambiguous, simply has no row.
        try { relative = normalize(PresetPairing.RelativeDs1(ds1Path, resolver)); } catch (InvalidDataException) { return null; }
        var matches = Table(resolve("data/global/excel/lvlprest.txt"))
            .Where(r => Enumerable.Range(1, 6).Any(i => r.TryGetValue($"File{i}", out var f) && normalize(f) == relative)).ToArray();
        return matches.Length == 1 && matches[0].GetValueOrDefault("LevelId") is { } id && id != "0" && id.Length > 0 ? matches[0] : null;
    }

    /// <summary>
    /// DT1 paths a DS1 records for itself, as `\d2\data\global\tiles\…\name.tg1`. Entries that do
    /// not resolve to a file present here are dropped rather than failing the load, so a map that
    /// references one unshipped tile still opens with the rest of its tileset.
    /// </summary>
    private static string[] EmbeddedTileset(Ds1CollisionDocument collision, Func<string, string> resolve)
    {
        var paths = new List<string>();
        foreach (string entry in collision.TileFiles)
        {
            string name = entry.Replace('\\', '/').ToLowerInvariant();
            int at = name.IndexOf("global/tiles/", StringComparison.Ordinal);
            if (at < 0) continue;
            string asset = "data/" + name[at..];
            if (asset.EndsWith(".tg1", StringComparison.Ordinal)) asset = asset[..^4] + ".dt1";
            if (!asset.EndsWith(".dt1", StringComparison.Ordinal)) continue;
            string resolved;
            try { resolved = resolve(asset); } catch (InvalidDataException) { continue; }
            if (File.Exists(resolved) && !paths.Contains(resolved, StringComparer.OrdinalIgnoreCase)) paths.Add(resolved);
        }
        return [.. paths];
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
