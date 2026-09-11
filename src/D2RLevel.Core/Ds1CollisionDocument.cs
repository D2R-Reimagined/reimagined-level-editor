using System.Buffers.Binary;

namespace D2RLevel.Core;

public sealed record Ds1CollisionLayer(string Name, int Offset, int[] Orientations);

// Retain the original bytes and edit only supported collision and gameplay fields.
// Unknown data, group records, IDs, flags and path actions remain verbatim.
public sealed partial class Ds1CollisionDocument
{
    public const uint Unwalkable = 0x00020000;
    private byte[] bytes;
    private byte[] saved;
    public EditHistory History { get; internal set; } = new();
    internal Func<int, int, bool>? ProtectedTile { get; set; }
    private sealed record Change(int Offset, uint Before, uint After);
    public string SourcePath { get; }
    public int Version { get; }
    public int Width { get; }
    public int Height { get; }
    public int Act { get; }
    public IReadOnlyList<Ds1CollisionLayer> Floors { get; }
    public IReadOnlyList<Ds1CollisionLayer> Walls { get; }
    public bool IsDirty => !bytes.AsSpan().SequenceEqual(saved);
    public bool CanUndo => History.CanUndo;
    public bool CanRedo => History.CanRedo;
    private Ds1CollisionDocument(string path, byte[]? source = null)
    {
        SourcePath = Path.GetFullPath(path);
        if (source is null && new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("DS1 exceeds 64 MiB.");
        bytes = source?.ToArray() ?? File.ReadAllBytes(path); saved = bytes.ToArray();
        using var reader = new BinaryReader(new MemoryStream(bytes, false));
        Version = reader.ReadInt32();
        if (Version is < 16 or > 18) throw new InvalidDataException("Collision editing supports DS1 versions 16–18.");
        Width = checked(reader.ReadInt32() + 1); Height = checked(reader.ReadInt32() + 1);
        Act = checked(reader.ReadInt32() + 1); int tag = reader.ReadInt32();
        if (Width is < 1 or > 512 || Height is < 1 or > 512 || Act is < 1 or > 5) throw new InvalidDataException("Invalid DS1 dimensions or act.");
        int files = reader.ReadInt32();
        if (files is < 0 or > 1024) throw new InvalidDataException("Invalid DS1 filename count.");
        for (int i = 0; i < files; i++)
        {
            int length = 0;
            while (reader.ReadByte() != 0) if (++length > 4096) throw new InvalidDataException("Unterminated DS1 filename.");
        }
        int walls = reader.ReadInt32(), floors = reader.ReadInt32();
        if (walls is < 1 or > 4 || floors is < 1 or > 2) throw new InvalidDataException("Invalid DS1 layer counts.");
        int cells = checked(Width * Height), size = checked(cells * 4);
        long required = reader.BaseStream.Position + (long)size * (2 * walls + floors + 1 + (tag is 1 or 2 ? 1 : 0));
        if (required > bytes.Length) throw new InvalidDataException("Truncated DS1 tile/shadow/tag layers.");
        var wallLayers = new List<Ds1CollisionLayer>();
        for (int l = 0; l < walls; l++)
        {
            int offset = (int)reader.BaseStream.Position; reader.BaseStream.Position += size;
            var orientations = new int[cells];
            for (int i = 0; i < cells; i++) orientations[i] = reader.ReadInt32() & 255;
            wallLayers.Add(new($"Wall {l + 1}", offset, orientations));
        }
        var floorLayers = new List<Ds1CollisionLayer>();
        for (int l = 0; l < floors; l++)
        {
            floorLayers.Add(new($"Floor {l + 1}", (int)reader.BaseStream.Position, new int[cells]));
            reader.BaseStream.Position += size;
        }
        Walls = wallLayers.AsReadOnly(); Floors = floorLayers.AsReadOnly();
        ReadGameplay((int)required, tag);
    }
    public static Ds1CollisionDocument Load(string path) => new(path);
    private int Index(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) throw new ArgumentOutOfRangeException(nameof(x), "Tile outside DS1.");
        return y * Width + x;
    }
    public uint Cell(Ds1CollisionLayer layer, int x, int y)
    {
        if (!Floors.Contains(layer) && !Walls.Contains(layer)) throw new ArgumentException("Layer belongs to another DS1.");
        return Read(layer.Offset + Index(x, y) * 4);
    }
    private uint Read(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private void Write(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    public bool HasOverride(int x, int y) => Floors.Concat(Walls).Any(l => (Cell(l, x, y) & Unwalkable) != 0);
    public bool CanBlock(int x, int y) => Floors.Any(l => (Cell(l, x, y) & 255) != 0);

    public int Paint(IEnumerable<(int X, int Y)> tiles, bool block)
    {
        var changes = new List<Change>();
        foreach (var (x, y) in tiles.Distinct())
        {
            if (ProtectedTile?.Invoke(x, y) == true) throw new InvalidOperationException("This tile belongs to a linked footprint. Unlink it before painting it directly.");
            int index = Index(x, y); // Validate entire stroke before mutating.
            var layers = block ? Floors.Where(l => (Cell(l, x, y) & 255) != 0).Take(1) : Floors.Concat(Walls);
            foreach (var layer in layers)
            {
                int offset = layer.Offset + index * 4; uint before = Read(offset);
                uint after = block ? before | Unwalkable : before & ~Unwalkable;
                if (before != after) changes.Add(new(offset, before, after));
            }
        }
        if (changes.Count == 0) return 0;
        foreach (var c in changes) Write(c.Offset, c.After);
        RecordChanges(changes.ToArray()); return changes.Count;
    }
    public void Undo()
    {
        History.Undo();
    }
    public void Redo()
    {
        History.Redo();
    }
    private void RecordChanges(Change[] changes) => History.Record(
        () => { foreach (var c in changes) Write(c.Offset, c.Before); },
        () => { foreach (var c in changes) Write(c.Offset, c.After); });
    public byte[] Serialize() => bytes.ToArray();
    public void DiscardChanges()
    {
        if (History.Shared) throw new InvalidOperationException("Linked DS1 edits belong to the shared workspace. Close or replace the JSON workspace to discard both documents together.");
        saved.CopyTo(bytes, 0); History.Clear();
    }
    public void SaveCopy(string path)
    {
        path = Path.GetFullPath(path);
        if (string.Equals(path, SourcePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Choose a copy path; the source DS1 cannot be overwritten.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (!Load(temp).Serialize().AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("DS1 save verification failed.");
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
            saved = bytes.ToArray();
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void MarkSaved() => saved = bytes.ToArray();
}
