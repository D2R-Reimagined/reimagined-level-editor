using System.Buffers.Binary;
using System.Numerics;
using LSLib.Granny.GR2;
using Address = D2RLevel.Assets.GrannySectionDocument.Address;
using Field = D2RLevel.Assets.GrannySectionDocument.Field;

namespace D2RLevel.Assets;

public sealed record TerrainRectangle(float MinX, float MinZ, float MaxX, float MaxZ, float Height)
{
    public void Validate()
    {
        if (new[] { MinX, MinZ, MaxX, MaxZ, Height }.Any(v => !float.IsFinite(v)) || MaxX <= MinX || MaxZ <= MinZ)
            throw new ArgumentException("Enter a finite, nonempty ground rectangle.");
    }
}
public sealed record TerrainSurfaceResult(byte[] Bytes, int OriginalVertices, int Vertices, int OriginalTriangles, int Triangles);

/// <summary>
/// Experimental flat floor extension, confined to the existing model bounds.
/// Clips old triangles at the rectangle and fills it, retaining opaque GR2 data.
/// Bridges cut boundary heights; rejects skinned/annotated/multi-mesh terrain.
/// </summary>
public static class TerrainSurfaceEdit
{
    private sealed record Vertex(Vector3 Position, byte[] Bytes);

    public static TerrainSurfaceResult Extend(byte[] source, TerrainRectangle area)
    {
        area.Validate();
        var d = new GrannySectionDocument(source); // Private transaction; failed edits cannot alter the caller.
        Field Find(Address type, string name) => d.Fields(type).Single(f => f.Name == name);
        Address At(Address data, Address type, string name) => data.Plus(Find(type, name).Offset);
        void Empty(Address data, Address type, params string[] names)
        { foreach (var name in names) if (d.Int(At(data, type, name)) != 0) throw new NotSupportedException($"Terrain {name} must be empty."); }
        var meshes = Find(d.RootType, "Meshes"); var ma = d.Root.Plus(meshes.Offset);
        if (d.Int(ma) != 1 || meshes.Type is not { } mt) throw new NotSupportedException("One terrain mesh is required.");
        var mesh = d.RequiredReference(d.RequiredReference(ma.Plus(4)));
        Empty(mesh, mt, "MorphTargets");
        var ext = At(mesh, mt, "ExtendedData"); var et = d.RequiredReference(ext); var ed = d.RequiredReference(ext.Plus(d.PointerSize));
        float scale = d.Float(At(ed, et, "VertexScale"));
        if (!float.IsFinite(scale) || scale <= 0) throw new InvalidDataException("Invalid vertex scale.");
        var lo = At(ed, et, "bbMin"); var hi = At(ed, et, "bbMax");
        if (area.MinX < d.Float(lo) - .002f || area.MaxX > d.Float(hi) + .002f || area.MinZ < d.Float(lo.Plus(8)) - .002f || area.MaxZ > d.Float(hi.Plus(8)) + .002f ||
            area.Height < d.Float(lo.Plus(4)) - .002f || area.Height > d.Float(hi.Plus(4)) + .002f)
            throw new NotSupportedException("This extension must remain within the existing terrain bounds.");
        var bones = Find(mt, "BoneBindings"); var ba = mesh.Plus(bones.Offset);
        if (d.Int(ba) != 1 || bones.Type is not { } bt) throw new NotSupportedException("Expected one rigid terrain binding.");
        Empty(d.RequiredReference(ba.Plus(4)), bt, "TriangleIndices");
        var vd = Find(mt, "PrimaryVertexData"); var vt = vd.Type!.Value; var vdata = d.RequiredReference(mesh.Plus(vd.Offset));
        Empty(vdata, vt, "VertexAnnotationSets");
        var va = At(vdata, vt, "Vertices"); var vertexType = d.RequiredReference(va); var fields = d.Fields(vertexType);
        var pos = Find(vertexType, "Position");
        if (fields.Any(f => f.Name is not ("Position" or "Normal" or "Tangent" or "TextureCoordinates0")))
            throw new NotSupportedException("Unsupported terrain vertex attributes.");
        bool packed = pos.Kind == MemberType.BinormalInt16 && pos.Count == 4;
        if (!packed && !(pos.Kind == MemberType.Real32 && pos.Count == 3)) throw new NotSupportedException("Unsupported terrain positions.");
        foreach (var f in fields.Where(f => f != pos))
            if (!(f.Name is "Normal" or "Tangent"
                ? (f.Kind == MemberType.BinormalInt8 && f.Count == 4) || (f.Kind == MemberType.Real32 && f.Count is 3 or 4)
                : (f.Kind is MemberType.Real16 or MemberType.Real32 && f.Count == 2)))
                throw new NotSupportedException($"Unsupported terrain attribute {f.Name}.");
        int stride = fields.Sum(f => f.Size), count = d.Int(va.Plus(d.PointerSize));
        if (count is < 3 or > 60000) throw new NotSupportedException("Unsupported terrain vertex count.");
        var vstart = d.RequiredReference(va.Plus(d.PointerSize + 4));
        var oldVertices = d.Read(vstart, checked(count * stride)).ToArray();
        var vertices = new List<Vertex>();
        for (int i = 0; i < count; i++)
        {
            var bytes = oldVertices.AsSpan(i * stride, stride).ToArray();
            float Axis(int axis) => (packed ? BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(pos.Offset + axis * 2)) / 32767f : BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(pos.Offset + axis * 4))) * scale;
            vertices.Add(new(new(Axis(0), Axis(1), Axis(2)), bytes));
        }
        var topologyField = Find(mt, "PrimaryTopology"); var tt = topologyField.Type!.Value; var topology = d.RequiredReference(mesh.Plus(topologyField.Offset));
        Empty(topology, tt, "VertexToVertexMap", "VertexToTriangleMap", "SideToNeighborMap", "PolygonIndexStarts", "PolygonIndices", "BonesForTriangle", "TriangleToBoneIndices", "TriAnnotationSets");
        var groups = Find(tt, "Groups"); var ga = topology.Plus(groups.Offset);
        if (d.Int(ga) != 1) throw new NotSupportedException("One terrain material group is required.");
        var group = d.RequiredReference(ga.Plus(4)); var gt = groups.Type!.Value;
        var ia = At(topology, tt, "Indices16"); int indexSize = 2;
        if (d.Int(ia) == 0) { ia = At(topology, tt, "Indices"); indexSize = 4; }
        else Empty(topology, tt, "Indices");
        int indexCount = d.Int(ia); var istart = d.RequiredReference(ia.Plus(4));
        if (indexCount <= 0 || indexCount % 3 != 0 || d.Int(At(group, gt, "TriFirst")) != 0 || d.Int(At(group, gt, "TriCount")) != indexCount / 3)
            throw new InvalidDataException("Inconsistent terrain topology.");
        var oldIndices = d.Read(istart, checked(indexCount * indexSize)).ToArray();
        int Index(int i) => indexSize == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(oldIndices.AsSpan(i * 2)) : BinaryPrimitives.ReadInt32LittleEndian(oldIndices.AsSpan(i * 4));
        var indices = new List<int>();
        float Distance(Vector3 p, int side) => side switch { 0 => p.X - area.MinX, 1 => area.MaxX - p.X, 2 => p.Z - area.MinZ, _ => area.MaxZ - p.Z };
        Vertex Interpolate(Vertex a, Vertex b, float t)
        {
            var bytes=GrannyVertexInterpolation.Attributes(a.Bytes,b.Bytes,t,fields.Where(f=>f!=pos));
            return new(Vector3.Lerp(a.Position,b.Position,t),bytes);
        }
        List<Vertex> Clip(List<Vertex> polygon, int side, bool inside)
        {
            var result=new List<Vertex>();
            for(int i=0;i<polygon.Count;i++) { var a=polygon[i];var b=polygon[(i+1)%polygon.Count];float da=Distance(a.Position,side),db=Distance(b.Position,side);bool keepA=inside?da>=0:da<=0,keepB=inside?db>=0:db<=0;
                if(keepA)result.Add(a);if(keepA!=keepB) result.Add(Interpolate(a,b,da/(da-db))); }
            return result;
        }
        void Emit(List<Vertex> polygon)
        {
            for(int i=1;i+1<polygon.Count;i++) {
                var a=polygon[0];var b=polygon[i];var c=polygon[i+1];
                if(Vector3.Cross(b.Position-a.Position,c.Position-a.Position).LengthSquared()<.000001f)continue;
                foreach(var v in new[]{a,b,c}) { indices.Add(vertices.Count);vertices.Add(v); }
            }
        }
        for(int i=0;i<indexCount;i+=3)
        {
            int a=Index(i),b=Index(i+1),c=Index(i+2);
            if(new[]{a,b,c}.Any(v=>v<0||v>=count))throw new InvalidDataException("Invalid terrain triangle index.");
            var polygon=new List<Vertex>{vertices[a],vertices[b],vertices[c]};
            var intersection=polygon;
            for(int side=0;side<4;side++)intersection=Clip(intersection,side,true);
            if(intersection.Count<3 || Math.Abs(intersection.Select((v,j)=> v.Position.X*intersection[(j+1)%intersection.Count].Position.Z-intersection[(j+1)%intersection.Count].Position.X*v.Position.Z).Sum())<.00001f)
            { indices.AddRange([a,b,c]);continue; }
            // Flatten the interior, but bridge every cut edge to the original
            // boundary height. Without these skirts, sloping floor leaves cracks.
            for(int j=0;j<intersection.Count;j++)
            {
                var edgeA=intersection[j];var vb=intersection[(j+1)%intersection.Count];
                if(!Enumerable.Range(0,4).Any(side=>Math.Abs(Distance(edgeA.Position,side))<.0001f && Math.Abs(Distance(vb.Position,side))<.0001f))continue;
                if(Math.Abs(edgeA.Position.Y-area.Height)<.0001f && Math.Abs(vb.Position.Y-area.Height)<.0001f)continue;
                var raisedA=new Vertex(new(edgeA.Position.X,area.Height,edgeA.Position.Z),edgeA.Bytes.ToArray());
                var raisedB=new Vertex(new(vb.Position.X,area.Height,vb.Position.Z),vb.Bytes.ToArray());
                Emit([edgeA,vb,raisedB,raisedA]);
                Emit([raisedA,raisedB,vb,edgeA]); // Visible from either side of a step.
            }
            for(int side=0;side<4 && polygon.Count>=3;side++) { Emit(Clip(polygon,side,false));polygon=Clip(polygon,side,true); }
        }
        var donor=vertices.Take(count).MinBy(v=>Math.Abs(v.Position.Y-area.Height))!;
        var uvField=fields.SingleOrDefault(f=>f.Name=="TextureCoordinates0");
        (Vertex A,Vertex B,Vertex C)? uvTriangle=null;
        float nearest=float.PositiveInfinity;
        for(int i=0;i<indexCount;i+=3) {
            var a=vertices[Index(i)];var b=vertices[Index(i+1)];var c=vertices[Index(i+2)];
            float determinant=(b.Position.X-a.Position.X)*(c.Position.Z-a.Position.Z)-(b.Position.Z-a.Position.Z)*(c.Position.X-a.Position.X);
            if(Math.Abs(determinant)<.001f || new[]{a,b,c}.Any(v=>Math.Abs(v.Position.Y-area.Height)>.02f))continue;
            float distance=Vector3.DistanceSquared((a.Position+b.Position+c.Position)/3,new((area.MinX+area.MaxX)/2,area.Height,(area.MinZ+area.MaxZ)/2));
            if(distance<nearest){nearest=distance;uvTriangle=(a,b,c);}
        }
        if(uvField!=null && uvTriangle==null)throw new NotSupportedException("No flat terrain triangle can supply the ground texture mapping.");
        Vertex Corner(float x,float z) {
            var bytes=donor.Bytes.ToArray();
            foreach(var f in fields.Where(f=>f.Name is "Normal" or "Tangent")) for(int i=0;i<f.Count;i++) {
                float value=i==(f.Name=="Normal"?1:0)?1:0;
                if(f.Kind==MemberType.BinormalInt8)bytes[f.Offset+i]=(byte)(value*127);
                else BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(f.Offset+i*4),value);
            }
            if(uvField is { } uv && uvTriangle is { } triangle) {
                var (a,b,c)=triangle;var ab=b.Position-a.Position;var ac=c.Position-a.Position;
                float determinant=ab.X*ac.Z-ab.Z*ac.X;
                float u=((x-a.Position.X)*ac.Z-(z-a.Position.Z)*ac.X)/determinant;
                float v=(ab.X*(z-a.Position.Z)-ab.Z*(x-a.Position.X))/determinant;
                for(int axis=0;axis<2;axis++) {
                    int offset=uv.Offset+axis*(uv.Kind==MemberType.Real16?2:4);
                    float ReadUv(Vertex vertex)=>uv.Kind==MemberType.Real16?(float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(vertex.Bytes.AsSpan(offset))):BinaryPrimitives.ReadSingleLittleEndian(vertex.Bytes.AsSpan(offset));
                    float av=ReadUv(a),value=av+u*(ReadUv(b)-av)+v*(ReadUv(c)-av);
                    if(!float.IsFinite(value))throw new InvalidDataException("Invalid ground texture coordinate.");
                    if(uv.Kind==MemberType.Real16){if(!Half.IsFinite((Half)value))throw new InvalidDataException("Ground texture coordinate overflow.");BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset),BitConverter.HalfToUInt16Bits((Half)value));}
                    else BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset),value);
                }
            }
            return new(new(x,area.Height,z),bytes);
        }
        // Upward-facing patch. Its material is the terrain's existing biome material.
        Emit([Corner(area.MinX,area.MinZ),Corner(area.MinX,area.MaxZ),Corner(area.MaxX,area.MaxZ),Corner(area.MaxX,area.MinZ)]);
        if(indexSize==2 && vertices.Count>65535)throw new NotSupportedException("Extension exceeds 16-bit topology capacity.");
        var vbytes=new byte[checked(vertices.Count*stride)];
        for(int i=0;i<vertices.Count;i++) {
            var v=vertices[i];v.Bytes.CopyTo(vbytes,i*stride);
            if(i<count)continue; // The complete original vertex payload is unchanged.
            for(int axis=0;axis<3;axis++) {
                float value=v.Position[axis]/scale;
                if(!float.IsFinite(value))throw new InvalidDataException("Nonfinite terrain vertex.");
                if(packed) { double encoded=Math.Round((double)value*32767); if(encoded<short.MinValue||encoded>short.MaxValue)throw new NotSupportedException("New ground exceeds packed coordinate range."); BinaryPrimitives.WriteInt16LittleEndian(vbytes.AsSpan(i*stride+pos.Offset+axis*2),(short)encoded); }
                else BinaryPrimitives.WriteSingleLittleEndian(vbytes.AsSpan(i*stride+pos.Offset+axis*4),value);
            }
        }
        var ibytes=new byte[checked(indices.Count*indexSize)];
        for(int i=0;i<indices.Count;i++) if(indexSize==2)BinaryPrimitives.WriteUInt16LittleEndian(ibytes.AsSpan(i*2),checked((ushort)indices[i]));else BinaryPrimitives.WriteInt32LittleEndian(ibytes.AsSpan(i*4),indices[i]);
        d.ReplacePlainArray(vstart,oldVertices.Length,vbytes,count,vertices.Count);
        d.ReplacePlainArray(istart,oldIndices.Length,ibytes,indexCount,indices.Count);
        d.SetInt(va.Plus(d.PointerSize),vertices.Count);d.SetInt(ia,indices.Count);d.SetInt(At(group,gt,"TriCount"),indices.Count/3);
        var result=d.Serialize();new GrannySectionDocument(result);
        return new(result,count,vertices.Count,indexCount/3,indices.Count/3);
    }
}
