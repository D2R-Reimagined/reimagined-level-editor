using System.Buffers.Binary;
using LSLib.Granny.GR2;
using Address = D2RLevel.Assets.GrannySectionDocument.Address;

namespace D2RLevel.Assets;

/// <summary>Expand a rigid terrain's packed coordinate range and bounds before adding ground.</summary>
public static class TerrainGrowth
{
    public static TerrainSurfaceResult Extend(byte[] source, TerrainRectangle area)
    {
        area.Validate();
        var d = new GrannySectionDocument(source);
        GrannySectionDocument.Field F(Address t,string n)=>d.Fields(t).Single(f=>f.Name==n);
        Address At(Address a,Address t,string n)=>a.Plus(F(t,n).Offset);
        var mf=F(d.RootType,"Meshes");var ma=d.Root.Plus(mf.Offset);
        if(d.Int(ma)!=1)throw new NotSupportedException("One rigid terrain mesh required.");
        var mt=mf.Type!.Value;var m=d.RequiredReference(d.RequiredReference(ma.Plus(4)));
        var ea=At(m,mt,"ExtendedData");var et=d.RequiredReference(ea);var ed=d.RequiredReference(ea.Plus(d.PointerSize));
        float oldScale=d.Float(At(ed,et,"VertexScale"));
        var lo=At(ed,et,"bbMin");var hi=At(ed,et,"bbMax");
        float[] min=[Math.Min(d.Float(lo),area.MinX),Math.Min(d.Float(lo.Plus(4)),area.Height),Math.Min(d.Float(lo.Plus(8)),area.MinZ)];
        float[] max=[Math.Max(d.Float(hi),area.MaxX),Math.Max(d.Float(hi.Plus(4)),area.Height),Math.Max(d.Float(hi.Plus(8)),area.MaxZ)];
        float required=Math.Max(oldScale,min.Concat(max).Max(Math.Abs));
        if(!float.IsFinite(required)||required<1||required>8192)throw new NotSupportedException("Unsupported packed coordinate range.");
        short w=checked((short)Math.Ceiling(Math.Log2(required)*32767/13));
        float scale=(float)Math.Pow(2,13.0*w/32767);
        if(scale<required){w=checked((short)(w+1));scale=(float)Math.Pow(2,13.0*w/32767);}
        // Repacking rounds every coordinate to this grid. Keep both advertised
        // bound sets outside the decoded result, including newly added corners.
        float packedStep=scale/32767;
        for(int axis=0;axis<3;axis++) { min[axis]-=packedStep;max[axis]+=packedStep; }
        var vf=F(mt,"PrimaryVertexData");var vt=vf.Type!.Value;var vd=d.RequiredReference(m.Plus(vf.Offset));
        var va=At(vd,vt,"Vertices");var vertexType=d.RequiredReference(va);var pos=F(vertexType,"Position");
        if(pos is not {Kind:MemberType.BinormalInt16,Count:4})throw new NotSupportedException("Packed rigid terrain required.");
        int count=d.Int(va.Plus(d.PointerSize)),stride=d.Fields(vertexType).Sum(f=>f.Size);
        var start=d.RequiredReference(va.Plus(d.PointerSize+4));
        for(int i=0;i<count;i++) {
            var p=start.Plus(i*stride+pos.Offset);
            short oldW=BinaryPrimitives.ReadInt16LittleEndian(d.Read(p.Plus(6),2));
            float native=(float)Math.Pow(2,13.0*oldW/32767);
            if(oldW<0||Math.Abs(native/oldScale-1)>.001)throw new NotSupportedException("Source native scale disagrees with metadata.");
            for(int axis=0;axis<3;axis++) {
                short before=BinaryPrimitives.ReadInt16LittleEndian(d.Read(p.Plus(axis*2),2));
                double encoded=Math.Round(before*(double)oldScale/scale);
                if(encoded<short.MinValue||encoded>short.MaxValue)throw new InvalidDataException("Packed coordinate overflow.");
                d.SetInt16(p.Plus(axis*2),(short)encoded);
            }
            d.SetInt16(p.Plus(6),w);
        }
        d.SetFloat(At(ed,et,"VertexScale"),scale);
        var bf=F(mt,"BoneBindings");var ba=m.Plus(bf.Offset);
        if(d.Int(ba)!=1)throw new NotSupportedException("One rigid bone binding required.");
        var bone=d.RequiredReference(ba.Plus(4));var bt=bf.Type!.Value;
        for(int axis=0;axis<3;axis++) {
            d.SetFloat(lo.Plus(axis*4),min[axis]);d.SetFloat(hi.Plus(axis*4),max[axis]);
            d.SetFloat(At(bone,bt,"OBBMin").Plus(axis*4),min[axis]);d.SetFloat(At(bone,bt,"OBBMax").Plus(axis*4),max[axis]);
        }
        return TerrainSurfaceEdit.Extend(d.Serialize(),area);
    }
}
