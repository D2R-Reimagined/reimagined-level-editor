using LSLib.Granny.GR2;

namespace D2RLevel.Assets;

public sealed partial class GrannySectionDocument
{
    // Both Meshes (references) and rigid Model.MeshBindings (one Mesh reference)
    // are pointer-sized records. Compact their relocation slots with the payload.
    internal void CompactMeshReferences(Address header, IReadOnlyList<int> keep)
    {
        int count=Int(header);var start=RequiredReference(header.Plus(4));
        var targets=Enumerable.Range(0,count).Select(i=>RequiredReference(start.Plus(i*PointerSize))).ToArray();
        if(keep.Any(i=>i<0||i>=count)||!keep.SequenceEqual(keep.Distinct().Order()))throw new InvalidDataException("Invalid mesh selection.");
        for(int i=0;i<count;i++)ClearReference(start.Plus(i*PointerSize));
        for(int i=0;i<keep.Count;i++){
            var at=start.Plus(i*PointerSize);var target=targets[keep[i]];references.Add(at,target);
            var section=sections[at.Section];var table=new byte[section.Relocations.Length+12];section.Relocations.CopyTo(table,0);
            Put(table,table.Length-12,at.Offset);Put(table,table.Length-8,target.Section);Put(table,table.Length-4,target.Offset);
            sections[at.Section]=section with {Relocations=table};changedTables.Add(at.Section);
        }
        var marshalling=sections[start.Section].Marshalling;
        for(int i=0;i<marshalling.Length;i+=16)if(Number(marshalling,i+4)==start.Offset){if(Number(marshalling,i)!=count)throw new InvalidDataException("Unexpected mesh marshalling count.");Put(marshalling,i,keep.Count);}
        SetInt(header,keep.Count);if(keep.Count==0)ClearReference(header.Plus(4));
    }
}

/// <summary>Remove fully cut rigid meshes and their model bindings before native rendering.</summary>
public static class SceneryMeshPruning
{
    public static (byte[] Bytes,int[] KeptMeshes) RemoveEmpty(byte[] source)
    {
        var d=new GrannySectionDocument(source);
        GrannySectionDocument.Field Field(GrannySectionDocument.Address t,string name)=>d.Fields(t).Single(f=>f.Name==name);
        var mf=Field(d.RootType,"Meshes");var mh=d.Root.Plus(mf.Offset);int count=d.Int(mh);var array=d.RequiredReference(mh.Plus(4));
        var keep=new List<int>();var removed=new HashSet<GrannySectionDocument.Address>();
        for(int i=0;i<count;i++){
            var mesh=d.RequiredReference(array.Plus(i*d.PointerSize));var tf=Field(mf.Type!.Value,"PrimaryTopology");var top=d.RequiredReference(mesh.Plus(tf.Offset));
            int indices=d.Int(top.Plus(Field(tf.Type!.Value,"Indices").Offset))+d.Int(top.Plus(Field(tf.Type.Value,"Indices16").Offset));
            if(indices>0)keep.Add(i);else removed.Add(mesh);
        }
        if(removed.Count==0)return(source.ToArray(),keep.ToArray());
        if(keep.Count==0)throw new NotSupportedException("The entire scenery model was cut away; remove its scene component instead of exporting an empty model.");
        var models=Field(d.RootType,"Models");var modelsHeader=d.Root.Plus(models.Offset);int modelCount=d.Int(modelsHeader);
        if(modelCount>0){var modelsArray=d.RequiredReference(modelsHeader.Plus(4));var retainedModels=new List<int>();
            for(int i=0;i<modelCount;i++){
                var model=d.RequiredReference(modelsArray.Plus(i*d.PointerSize));var bindings=Field(models.Type!.Value,"MeshBindings");
                var fields=d.Fields(bindings.Type!.Value);
                if(bindings.Kind!=MemberType.ReferenceToArray||fields.Length!=1||fields[0].Name!="Mesh"||fields[0].Kind!=MemberType.Reference)
                    throw new NotSupportedException("Unsupported model mesh binding layout.");
                var header=model.Plus(bindings.Offset);int n=d.Int(header);if(n==0)continue;var ba=d.RequiredReference(header.Plus(4));
                var selected=Enumerable.Range(0,n).Where(j=>!removed.Contains(d.RequiredReference(ba.Plus(j*d.PointerSize)))).ToArray();
                if(selected.Length!=n)d.CompactMeshReferences(header,selected);
                if(selected.Length>0)retainedModels.Add(i);
            }
            if(retainedModels.Count==0)throw new InvalidDataException("Pruned scenery has no bound models.");
            if(retainedModels.Count!=modelCount)d.CompactMeshReferences(modelsHeader,retainedModels);
        }
        d.CompactMeshReferences(mh,keep);
        return(d.Serialize(),keep.ToArray());
    }
}
