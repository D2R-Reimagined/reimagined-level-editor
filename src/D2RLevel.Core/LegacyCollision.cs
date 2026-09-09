using System.Buffers.Binary;

namespace D2RLevel.Core;

public sealed record Dt1CollisionTile(string Source, int Orientation, int Main, int Sub, byte[] Flags);
public sealed record TileCollision(byte[] Flags, bool Unresolved, bool VariantDependent, bool Override, bool NoFloor)
{
    public int BlockedSubtiles => Flags.Count(f => (f & 9) != 0);
}

public sealed class LegacyCollision
{
    public Ds1CollisionDocument Document { get; }
    private readonly IReadOnlyDictionary<(int Orientation, int Main, int Sub), Dt1CollisionTile[]> tiles;
    public LegacyCollision(Ds1CollisionDocument document, IEnumerable<Dt1CollisionTile> tiles)
    {
        Document = document;
        this.tiles = tiles.GroupBy(t => (t.Orientation, t.Main, t.Sub)).ToDictionary(g => g.Key, g => g.ToArray());
    }
    public TileCollision At(int x, int y)
    {
        var flags = new byte[25]; bool unknown = false, varying = false;
        bool noFloor = !Document.CanBlock(x, y), forced = Document.HasOverride(x, y);
        if (noFloor || forced) Array.Fill(flags, (byte)1);
        void Add(int orientation, int main, int sub, bool required = true)
        {
            if (!tiles.TryGetValue((orientation, main, sub), out var variants))
            {
                // Paul's editor handles these alternate wall-end orientations.
                int alternate = orientation == 18 ? 19 : 18;
                if (orientation is not (18 or 19) ||
                    (!tiles.TryGetValue((alternate, main, sub), out variants) && !tiles.TryGetValue((alternate, main, 0), out variants)))
                { unknown |= required; return; }
            }
            foreach (var variant in variants)
            {
                if (!variant.Flags.AsSpan().SequenceEqual(variants[0].Flags)) varying = true;
                for (int i = 0; i < 25; i++) flags[i] |= variant.Flags[i];
            }
        }
        foreach (var layer in Document.Floors.Concat(Document.Walls))
        {
            var cell = new FloorCell(Document.Cell(layer, x, y));
            if (cell.IsEmpty) continue;
            int orientation = layer.Orientations[y * Document.Width + x];
            Add(orientation, cell.Main, cell.Sub);
            if (orientation == 3) Add(4, cell.Main, cell.Sub, required: false);
        }
        return new(flags, unknown, varying, forced, noFloor);
    }
    public static IReadOnlyList<Dt1CollisionTile> ReadTiles(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int Read(int offset)
        {
            if (offset < 0 || offset > bytes.Length - 4) throw new InvalidDataException("Truncated DT1 collision header.");
            return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
        }
        if (Read(0) != 7 || Read(4) != 6) throw new InvalidDataException("Expected DT1 7.6.");
        int count = Read(268), start = Read(272);
        if (count is < 0 or > 100000 || start < 276 || (long)start + count * 96L > bytes.Length)
            throw new InvalidDataException("Invalid DT1 collision tile table.");
        var result = new List<Dt1CollisionTile>();
        for (int i = 0; i < count; i++)
        {
            int header = start + i * 96; var flags = new byte[25];
            // DT1 stores rows bottom-to-top; convert to DS1 X/Y subtile order.
            for (int y = 0; y < 5; y++) for (int x = 0; x < 5; x++) flags[y * 5 + x] = bytes[header + 40 + (4 - y) * 5 + x];
            result.Add(new(path, Read(header + 20), Read(header + 24), Read(header + 28), flags));
        }
        return result;
    }
}
