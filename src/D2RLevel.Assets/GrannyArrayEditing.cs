using System.Buffers.Binary;

namespace D2RLevel.Assets;

public sealed partial class GrannySectionDocument
{
    internal void ClearReference(Address at)
    {
        Read(at,PointerSize);
        if(!references.Remove(at))return;
        var section=sections[at.Section];
        var entries=Enumerable.Range(0,section.Relocations.Length/12)
            .Where(i=>Number(section.Relocations,i*12)!=at.Offset)
            .SelectMany(i=>section.Relocations.AsSpan(i*12,12).ToArray()).ToArray();
        section.Data.AsSpan(at.Offset,PointerSize).Clear();
        sections[at.Section]=section with {Relocations=entries};
        changedTables.Add(at.Section);
    }

    internal void SetInt(Address at, int value)
    {
        Read(at, 4);
        BinaryPrimitives.WriteInt32LittleEndian(sections[at.Section].Data.AsSpan(at.Offset, 4), value);
    }

    // Pointer-free arrays only. Preserve the old payload, append the replacement,
    // and retarget every alias and the array's mixed-marshalling record together.
    internal void ReplacePlainArray(Address old, int oldLength, byte[] replacement, int oldCount, int newCount)
    {
        Read(old, oldLength);
        if (replacement.Length == 0 || oldLength <= 0 || newCount <= 0 || oldCount <= 0)
            throw new InvalidDataException("Expected nonempty array replacement.");
        if (references.Keys.Any(a => a.Section == old.Section && a.Offset >= old.Offset && a.Offset < old.Offset + oldLength) ||
            references.Values.Any(a => a.Section == old.Section && a.Offset > old.Offset && a.Offset < old.Offset + oldLength))
            throw new NotSupportedException("Array contains pointers or has interior aliases.");
        var section = sections[old.Section];
        int offset = checked((section.Data.Length + 15) & ~15);
        if ((long)offset + replacement.Length > Limit) throw new InvalidDataException("Array export exceeds the size limit.");
        var expanded = new byte[checked(offset + replacement.Length)];
        section.Data.CopyTo(expanded, 0); replacement.CopyTo(expanded, offset);
        sections[old.Section] = section with { Data = expanded };
        var target = new Address(old.Section, offset);
        foreach (var from in references.Where(p => p.Value == old).Select(p => p.Key).ToArray())
        {
            var table = sections[from.Section].Relocations;
            int entry = Enumerable.Range(0, table.Length / 12).Single(i => Number(table, i * 12) == from.Offset) * 12;
            Put(table, entry + 4, target.Section); Put(table, entry + 8, target.Offset);
            references[from] = target; changedTables.Add(from.Section);
        }
        var marshalling = sections[old.Section].Marshalling;
        for (int i = 0; i < marshalling.Length; i += 16)
            if (Number(marshalling, i + 4) == old.Offset)
            {
                if (Number(marshalling, i) != oldCount) throw new InvalidDataException("Unexpected array marshalling count.");
                Put(marshalling, i, newCount); Put(marshalling, i + 4, target.Offset);
            }
        changedTables.Add(old.Section);
    }
}
