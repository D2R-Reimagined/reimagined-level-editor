using System.Buffers.Binary;

namespace D2RLevel.Assets;

/// <summary>Preserve existing packed catalog entries and register five tiers of clipped scenery.</summary>
public static class GroundModelCatalog
{
    private sealed record Record(byte[] Bytes, int Child);
    public static uint Key(string path)
    {
        if (!path.StartsWith("data/hd/") || !path.EndsWith(".model") || path.Any(c => c < 33 || c > 126 || c is ':' or '\\') || path.Split('/').Any(p => p is "" or "." or ".."))
            throw new ArgumentException("Invalid model reference.");
        uint key = 2166136261;
        foreach (char c in path.ToLowerInvariant()) key = unchecked((key ^ c) * 16777619);
        return key;
    }
    public static byte[] Register(byte[] source, string donor, string target, IReadOnlyList<int> keptMeshes)
    {
        if (source.Length < 24 || source.Length > 64 * 1024 * 1024) throw new InvalidDataException("Invalid model catalog size.");
        int U(int at) => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(at,4)));
        int[] counts = [U(0),U(8),U(16)], starts = [checked(4+U(4)),checked(12+U(12)),checked(20+U(20))], strides = [44,12,32];
        if (starts[0]!=24 || starts[1]!=24+counts[0]*44L || starts[2]!=starts[1]+counts[1]*12L || source.Length!=starts[2]+counts[2]*32L)
            throw new InvalidDataException("Unsupported model catalog layout.");
        int Child(int at,int array)
        {
            long delta=(long)at+U(at)-starts[array];
            if(delta<0 || delta%strides[array]!=0 || delta/strides[array]>counts[array])throw new InvalidDataException("Invalid catalog pointer.");
            return (int)(delta/strides[array]);
        }
        var models=Enumerable.Range(0,counts[0]).Select(i=>new Record(source.AsSpan(starts[0]+i*44,44).ToArray(),Child(starts[0]+i*44+36,1))).ToList();
        var lods=Enumerable.Range(0,counts[1]).Select(i=>new Record(source.AsSpan(starts[1]+i*12,12).ToArray(),Child(starts[1]+i*12+8,2))).ToList();
        var meshes=Enumerable.Range(0,counts[2]).Select(i=>source.AsSpan(starts[2]+i*32,32).ToArray()).ToList();
        static uint Read(byte[] bytes,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at,4));
        static void Put(byte[] bytes,int at,int value)=>BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at,4),value);
        void ValidateChildren(List<Record> records,int offset,int total)
        {
            var owned=new bool[total];
            foreach(var r in records) {long end=r.Child+(long)Read(r.Bytes,offset);if(end>total)throw new InvalidDataException("Invalid catalog children.");for(int i=r.Child;i<end;i++){if(owned[i])throw new InvalidDataException("Overlapping catalog children.");owned[i]=true;}}
            if(owned.Any(v=>!v))throw new InvalidDataException("Unowned catalog children.");
        }
        ValidateChildren(models,32,lods.Count);ValidateChildren(lods,4,meshes.Count);
        for(int i=1;i<models.Count;i++)if(Read(models[i-1].Bytes,0)>=Read(models[i].Bytes,0))throw new InvalidDataException("Unsorted catalog.");
        uint targetKey=Key(target);
        if(models.Any(m=>Read(m.Bytes,0)==targetKey))throw new InvalidDataException("Clipped model already registered.");
        var original=models.SingleOrDefault(m=>Read(m.Bytes,0)==Key(donor))??throw new InvalidDataException("Scenery donor is absent from the model catalog.");
        int count=checked((int)Read(original.Bytes,32));if(count==0)throw new InvalidDataException("Scenery donor has no LODs.");
        var first=lods[original.Child];int meshCount=checked((int)Read(first.Bytes,4));
        if (keptMeshes.Count == 0 || keptMeshes.Any(i => i < 0 || i >= meshCount) || !keptMeshes.SequenceEqual(keptMeshes.Distinct().Order()))
            throw new InvalidDataException("Invalid clipped scenery mesh selection.");
        var raw=original.Bytes.ToArray();Put(raw,0,unchecked((int)targetKey));Put(raw,32,5);models.Add(new(raw,lods.Count));
        for(int tier=0;tier<5;tier++) {var lr=lods[original.Child+Math.Min(tier,count-1)].Bytes.ToArray();Put(lr,4,keptMeshes.Count);lods.Add(new(lr,meshes.Count));foreach(int i in keptMeshes)meshes.Add(meshes[first.Child+i].ToArray());}
        models=models.OrderBy(m=>Read(m.Bytes,0)).ToList();
        int s1=checked(24+models.Count*44),s2=checked(s1+lods.Count*12);
        var result=new byte[checked(s2+meshes.Count*32)];
        Put(result,0,models.Count);Put(result,4,20);Put(result,8,lods.Count);Put(result,12,s1-12);Put(result,16,meshes.Count);Put(result,20,s2-20);
        for(int i=0;i<models.Count;i++){int at=24+i*44;models[i].Bytes.CopyTo(result,at);Put(result,at+36,s1+models[i].Child*12-at-36);}
        for(int i=0;i<lods.Count;i++){int at=s1+i*12;lods[i].Bytes.CopyTo(result,at);Put(result,at+8,s2+lods[i].Child*32-at-8);}
        for(int i=0;i<meshes.Count;i++)meshes[i].CopyTo(result,s2+i*32);
        return result;
    }
}
