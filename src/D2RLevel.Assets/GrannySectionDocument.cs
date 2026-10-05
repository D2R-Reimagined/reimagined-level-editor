using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using LSLib.Granny.GR2;
using LSLib.Native;

namespace D2RLevel.Assets;

/// <summary>
/// Experimental, little-endian GR2 editing without reconstructing a partial object graph.
/// Unchanged sections stay in the original file; changed sections are appended uncompressed.
/// Section-relative references and opaque data are retained. This is not terrain/physics export.
/// </summary>
public sealed partial class GrannySectionDocument
{
    internal readonly record struct Address(int Section, int Offset)
    {
        public Address Plus(int bytes) => new(Section, checked(Offset + bytes));
    }
    internal sealed record Field(string Name, MemberType Kind, Address? Type, int Count, int Offset, int Size);
    private sealed record Section(int HeaderOffset, byte[] Original, byte[] Data, byte[] Relocations, byte[] Marshalling);
    private readonly byte[] source;
    private readonly Section[] sections;
    private readonly Dictionary<Address, Address> references = [];
    private readonly Dictionary<Address, Field[]> definitions = [];
    private readonly HashSet<Address> parsing = [];
    private readonly HashSet<int> changedTables = [];
    internal int PointerSize { get; }
    internal Address RootType { get; }
    internal Address Root { get; }
    public int SectionCount => sections.Length;
    private const int Limit = 256 * 1024 * 1024;

    public static GrannySectionDocument Load(string path)
    {
        if (new FileInfo(path).Length > Limit) throw new InvalidDataException("GR2 file exceeds 256 MiB.");
        return new(File.ReadAllBytes(path));
    }

    public GrannySectionDocument(byte[] bytes)
    {
        source = bytes.ToArray();
        if (source.Length < 104 || source.Length > Limit) throw new InvalidDataException("Invalid GR2 size.");
        var format = Magic.FormatFromSignature(source[..16]);
        PointerSize = format switch { Magic.Format.LittleEndian32 => 4, Magic.Format.LittleEndian64 => 8,
            _ => throw new NotSupportedException("Only little-endian GR2 editing is supported.") };
        if (U32(source, 20) != 0 || U32(source, 32) != 7 || U32(source, 36) != source.Length || U32(source, 44) != 72)
            throw new NotSupportedException("Expected a complete GR2 v7 file with an uncompressed header.");
        int count = Number(source, 48);
        if (count is < 1 or > 64 || Number(source, 16) != checked(104 + count * 44))
            throw new InvalidDataException("Invalid GR2 section directory.");
        Range(source, 104, checked(count * 44));
        sections = new Section[count];
        int total = 0;
        for (int i = 0; i < count; i++)
        {
            int h = 104 + i * 44, compression = Number(source, h), size = Number(source, h + 12);
            int alignment = Number(source, h + 16);
            if (alignment is < 1 or > 4096 || (alignment & (alignment - 1)) != 0)
                throw new InvalidDataException("Unsupported section alignment.");
            if (Number(source, h + 20) > Number(source, h + 24) || Number(source, h + 24) > size)
                throw new InvalidDataException("Invalid section decompression stops.");
            total = checked(total + size);
            if (total > Limit) throw new InvalidDataException("Expanded GR2 exceeds 256 MiB.");
            byte[] packed = Slice(source, Number(source, h + 4), Number(source, h + 8));
            byte[] data = compression switch
            {
                0 when packed.Length == size => packed,
                1 or 2 or 3 when size > 0 => Granny2Compressor.Decompress(compression, packed, size,
                    Number(source, h + 20), Number(source, h + 24), size),
                4 when size > 0 => Granny2Compressor.Decompress4(packed, size),
                _ when size == 0 && packed.Length == 0 => [],
                _ => throw new NotSupportedException("Unsupported GR2 section compression or size.")
            };
            byte[] Table(int offsetField, int countField, int stride)
            {
                int length = checked(Number(source, h + countField) * stride);
                if (length == 0) return [];
                if (length > Limit) throw new InvalidDataException("GR2 table exceeds size limit.");
                total = checked(total + length);
                if (total > Limit) throw new InvalidDataException("Expanded GR2 tables exceed 256 MiB.");
                int offset = Number(source, h + offsetField);
                return compression == 4
                    ? Granny2Compressor.Decompress4(Slice(source, checked(offset + 4), Number(source, offset)), length)
                    : Slice(source, offset, length);
            }
            sections[i] = new(h, data.ToArray(), data, Table(28, 32, 12), Table(36, 40, 16));
        }
        RootType = new(Number(source, 52), Number(source, 56));
        Root = new(Number(source, 60), Number(source, 64));
        Read(RootType, 4); Read(Root, 1);
        for (int i = 0; i < count; i++)
        {
            var table = sections[i].Relocations;
            for (int p = 0; p < table.Length; p += 12)
            {
                var from = new Address(i, Number(table, p));
                var to = new Address(Number(table, p + 4), Number(table, p + 8));
                Read(from, PointerSize); Read(to, 1);
                if (!references.TryAdd(from, to)) throw new InvalidDataException("Duplicate GR2 relocation.");
            }
        }
    }

    internal ReadOnlySpan<byte> Read(Address address, int length)
    {
        if (address.Section < 0 || address.Section >= sections.Length) throw new InvalidDataException("Invalid section reference.");
        var data = sections[address.Section].Data;
        Range(data, address.Offset, length);
        return data.AsSpan(address.Offset, length);
    }
    internal int Int(Address address) => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(Read(address, 4)));
    internal float Float(Address address) => BinaryPrimitives.ReadSingleLittleEndian(Read(address, 4));
    internal short Int16(Address address) => BinaryPrimitives.ReadInt16LittleEndian(Read(address, 2));
    internal void SetInt16(Address address, short value)
    {
        Read(address, 2);
        BinaryPrimitives.WriteInt16LittleEndian(sections[address.Section].Data.AsSpan(address.Offset, 2), value);
    }
    internal void SetFloat(Address address, float value)
    {
        if (!float.IsFinite(value)) throw new InvalidDataException("Non-finite vertex coordinate.");
        Read(address, 4);
        BinaryPrimitives.WriteSingleLittleEndian(sections[address.Section].Data.AsSpan(address.Offset, 4), value);
    }
    internal Address? Reference(Address address)
    {
        Read(address, PointerSize);
        return references.TryGetValue(address, out var target) ? target : null;
    }
    internal Address RequiredReference(Address address) => Reference(address) ?? throw new InvalidDataException("Missing GR2 reference.");
    internal string String(Address address)
    {
        var data = sections[address.Section].Data;
        Read(address, 1);
        int length = Array.IndexOf(data, (byte)0, address.Offset) - address.Offset;
        if (length < 0 || length > 16384) throw new InvalidDataException("Invalid GR2 string.");
        return Encoding.UTF8.GetString(data, address.Offset, length);
    }
    internal Field[] Fields(Address definition)
    {
        if (definitions.TryGetValue(definition, out var result)) return result;
        if (parsing.Count >= 32 || !parsing.Add(definition)) throw new InvalidDataException("Recursive inline GR2 type.");
        try
        {
            var fields = new List<Field>(); int offset = 0, stride = 20 + PointerSize * 3;
            for (int i = 0; i < 1024; i++)
            {
                var member = definition.Plus(checked(i * stride));
                var kind = (MemberType)Int(member);
                if (kind == MemberType.None) { result = fields.ToArray(); definitions.Add(definition, result); return result; }
                Read(member, stride);
                string name = String(RequiredReference(member.Plus(4)));
                var type = Reference(member.Plus(4 + PointerSize));
                int count = Int(member.Plus(4 + PointerSize * 2));
                int element = kind switch
                {
                    MemberType.Inline => Fields(type ?? throw new InvalidDataException("Missing inline type.")).Sum(f => f.Size),
                    MemberType.Reference or MemberType.String => PointerSize,
                    MemberType.VariantReference => PointerSize * 2,
                    MemberType.ReferenceToArray or MemberType.ArrayOfReferences => 4 + PointerSize,
                    MemberType.ReferenceToVariantArray => 4 + PointerSize * 2,
                    MemberType.Transform => 68,
                    MemberType.Real32 or MemberType.Int32 or MemberType.UInt32 => 4,
                    MemberType.Int16 or MemberType.UInt16 or MemberType.BinormalInt16 or MemberType.NormalUInt16 or MemberType.Real16 => 2,
                    MemberType.Int8 or MemberType.UInt8 or MemberType.BinormalInt8 or MemberType.NormalUInt8 => 1,
                    _ => throw new NotSupportedException($"Unsupported GR2 field {kind}.")
                };
                int size = checked(element * Math.Max(1, count));
                fields.Add(new(name, kind, type, count, offset, size)); offset = checked(offset + size);
            }
            throw new InvalidDataException("Unterminated GR2 type.");
        }
        finally { parsing.Remove(definition); }
    }

    /// <summary>Writes a candidate container; callers must separately qualify changed asset semantics.</summary>
    public byte[] Serialize()
    {
        if (changedTables.Count == 0 && sections.All(s => s.Original.AsSpan().SequenceEqual(s.Data))) return source.ToArray();
        using var output = new MemoryStream(); output.Write(source);
        var headers = source[..Number(source, 16)];
        foreach (var section in sections.Where((s, i) => changedTables.Contains(i) || !s.Original.AsSpan().SequenceEqual(s.Data)))
        {
            int h = section.HeaderOffset, alignment = Number(headers, h + 16);
            while (output.Position % alignment != 0) output.WriteByte(0);
            Put(headers, h, 0); Put(headers, h + 4, checked((int)output.Position));
            Put(headers, h + 8, section.Data.Length); Put(headers, h + 12, section.Data.Length); output.Write(section.Data);
            Put(headers, h + 28, checked((int)output.Position)); output.Write(section.Relocations);
            Put(headers, h + 32, section.Relocations.Length / 12);
            Put(headers, h + 36, checked((int)output.Position)); output.Write(section.Marshalling);
            Put(headers, h + 40, section.Marshalling.Length / 16);
            if (output.Length > Limit) throw new InvalidDataException("Export exceeds 256 MiB.");
        }
        var bytes = output.ToArray(); headers.CopyTo(bytes, 0);
        Put(bytes, 36, bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), Crc32.HashToUInt32(bytes.AsSpan(104)));
        return bytes;
    }
    public string[] SectionHashes() => sections.Select(s => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s.Data))).ToArray();
    private static uint U32(byte[] bytes, int offset) { Range(bytes, offset, 4); return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)); }
    private static int Number(byte[] bytes, int offset) => checked((int)U32(bytes, offset));
    private static void Put(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    private static byte[] Slice(byte[] bytes, int offset, int length) { Range(bytes, offset, length); return bytes.AsSpan(offset, length).ToArray(); }
    private static void Range(byte[] bytes, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > bytes.Length - length) throw new InvalidDataException("GR2 range outside data.");
    }
}
