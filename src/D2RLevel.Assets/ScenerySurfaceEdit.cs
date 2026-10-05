using System.Buffers.Binary;
using System.Numerics;
using LSLib.Granny.GR2;
using Address = D2RLevel.Assets.GrannySectionDocument.Address;

namespace D2RLevel.Assets;

/// <summary>Cut an open world-space column out of rigid scenery, retaining every outside triangle fragment.</summary>
public static class ScenerySurfaceEdit
{
    private sealed record Vertex(Vector3 Local, Vector3 World, byte[] Bytes, int Original);

    public static byte[] Cut(byte[] source, Matrix4x4 world, TerrainRectangle area)
    {
        area.Validate();
        if (!Matrix4x4.Invert(world, out _)) throw new ArgumentException("Scenery transform is singular.");
        var d = new GrannySectionDocument(source);
        GrannySectionDocument.Field Find(Address t, string n) => d.Fields(t).Single(f => f.Name == n);
        Address At(Address a, Address t, string n) => a.Plus(Find(t,n).Offset);
        void Empty(Address a, Address t, params string[] names)
        { foreach (var n in names) if (d.Int(At(a,t,n)) != 0) throw new NotSupportedException($"Scenery {n} must be empty."); }
        var meshes = Find(d.RootType,"Meshes"); var ma = d.Root.Plus(meshes.Offset);
        int meshCount = d.Int(ma); var mt = meshes.Type!.Value; var meshArray = d.RequiredReference(ma.Plus(4));
        if (meshCount is < 1 or > 256) throw new NotSupportedException("Unsupported scenery mesh count.");
        // Inspect original aliases before ReplacePlainArray retargets them. A later
        // mesh must not mistake the first mesh's appended array for an independent one.
        var originalArrays=new HashSet<Address>();
        for(int mi=0;mi<meshCount;mi++)
        {
            var mesh=d.RequiredReference(meshArray.Plus(mi*d.PointerSize));
            var vf=Find(mt,"PrimaryVertexData");var vd=d.RequiredReference(mesh.Plus(vf.Offset));
            var va=At(vd,vf.Type!.Value,"Vertices");
            var tf=Find(mt,"PrimaryTopology");var top=d.RequiredReference(mesh.Plus(tf.Offset));
            var ia=At(top,tf.Type!.Value,"Indices16");if(d.Int(ia)==0)ia=At(top,tf.Type.Value,"Indices");
            if(!originalArrays.Add(d.RequiredReference(va.Plus(d.PointerSize+4))) || !originalArrays.Add(d.RequiredReference(ia.Plus(4))))
                throw new NotSupportedException("Shared scenery arrays require a separate edit contract.");
        }
        var editedArrays = new HashSet<Address>();
        for (int mi=0; mi<meshCount; mi++)
        {
            var mesh = d.RequiredReference(meshArray.Plus(mi*d.PointerSize));
            Empty(mesh,mt,"MorphTargets");
            var bones = Find(mt,"BoneBindings"); var ba = mesh.Plus(bones.Offset);
            if (d.Int(ba)!=1) throw new NotSupportedException("Scenery must have one rigid binding per mesh.");
            Empty(d.RequiredReference(ba.Plus(4)),bones.Type!.Value,"TriangleIndices");
            var ext = At(mesh,mt,"ExtendedData"); var et = d.RequiredReference(ext); var ed = d.RequiredReference(ext.Plus(d.PointerSize));
            float scale = d.Float(At(ed,et,"VertexScale"));
            if (!float.IsFinite(scale)||scale<=0) throw new InvalidDataException("Invalid scenery scale.");
            var vf = Find(mt,"PrimaryVertexData"); var vt = vf.Type!.Value; var vd = d.RequiredReference(mesh.Plus(vf.Offset));
            Empty(vd,vt,"VertexAnnotationSets");
            var va = At(vd,vt,"Vertices"); var vertexType = d.RequiredReference(va); var fields = d.Fields(vertexType);
            var pos = Find(vertexType,"Position");
            bool packed = pos.Kind==MemberType.BinormalInt16 && pos.Count==4;
            if (!packed && !(pos.Kind==MemberType.Real32 && pos.Count==3)) throw new NotSupportedException("Unsupported scenery position format.");
            foreach (var f in fields.Where(f=>f!=pos))
                if (!(f.Name is "Normal" or "Tangent" ? (f.Kind==MemberType.BinormalInt8&&f.Count==4)||(f.Kind==MemberType.Real32&&f.Count is 3 or 4)
                    : f.Name.StartsWith("TextureCoordinates",StringComparison.Ordinal)&&(f.Kind is MemberType.Real16 or MemberType.Real32)&&f.Count==2))
                    throw new NotSupportedException($"Unsupported scenery attribute {f.Name}.");
            int stride = fields.Sum(f=>f.Size), count = d.Int(va.Plus(d.PointerSize));
            if (count is < 3 or > 1000000) throw new InvalidDataException("Invalid scenery vertex count.");
            var start = d.RequiredReference(va.Plus(d.PointerSize+4));
            if (!editedArrays.Add(start)) throw new NotSupportedException("Shared scenery vertex arrays require a separate edit contract.");
            var old = d.Read(start,checked(count*stride)).ToArray();
            var vertices = new List<Vertex>(count);
            for (int i=0;i<count;i++)
            {
                var bytes = old.AsSpan(i*stride,stride).ToArray();
                float Axis(int a) => scale*(packed?BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(pos.Offset+a*2))/32767f:BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(pos.Offset+a*4)));
                var p = new Vector3(Axis(0),Axis(1),Axis(2));
                vertices.Add(new(p,Vector3.Transform(p,world),bytes,i));
            }
            var tf = Find(mt,"PrimaryTopology"); var tt = tf.Type!.Value; var top = d.RequiredReference(mesh.Plus(tf.Offset));
            Empty(top,tt,"VertexToVertexMap","VertexToTriangleMap","SideToNeighborMap","PolygonIndexStarts","PolygonIndices","BonesForTriangle","TriangleToBoneIndices","TriAnnotationSets");
            var ia = At(top,tt,"Indices16"); int indexSize=2;
            if(d.Int(ia)==0){ia=At(top,tt,"Indices");indexSize=4;}else Empty(top,tt,"Indices");
            int indexCount=d.Int(ia);var indexStart=d.RequiredReference(ia.Plus(4));
            if(indexCount<=0||indexCount%3!=0)throw new InvalidDataException("Invalid scenery topology.");
            if(!editedArrays.Add(indexStart))throw new NotSupportedException("Shared scenery topology is not supported.");
            var oldIndices=d.Read(indexStart,checked(indexCount*indexSize)).ToArray();
            int Index(int i)=>indexSize==2?BinaryPrimitives.ReadUInt16LittleEndian(oldIndices.AsSpan(i*2)):BinaryPrimitives.ReadInt32LittleEndian(oldIndices.AsSpan(i*4));
            var gf=Find(tt,"Groups");var ga=top.Plus(gf.Offset);int groupCount=d.Int(ga);var gt=gf.Type!.Value;var groups=d.RequiredReference(ga.Plus(4));int gs=d.Fields(gt).Sum(f=>f.Size);
            var indices=new List<int>();int expected=0;
            float Distance(Vertex v,int side)=>side switch{0=>v.World.X-area.MinX,1=>area.MaxX-v.World.X,2=>v.World.Z-area.MinZ,_=>area.MaxZ-v.World.Z};
            Vertex Interpolate(Vertex a,Vertex b,float t)
            {
                var bytes=GrannyVertexInterpolation.Attributes(a.Bytes,b.Bytes,t,fields.Where(f=>f!=pos));
                return new(Vector3.Lerp(a.Local,b.Local,t),Vector3.Lerp(a.World,b.World,t),bytes,-1);
            }
            List<Vertex> Clip(List<Vertex> p,int side,bool inside)
            {
                var result=new List<Vertex>();
                for(int i=0;i<p.Count;i++){var a=p[i];var b=p[(i+1)%p.Count];float da=Distance(a,side),db=Distance(b,side);bool ka=inside?da>=0:da<=0,kb=inside?db>=0:db<=0;if(ka)result.Add(a);if(ka!=kb)result.Add(Interpolate(a,b,da/(da-db)));}
                return result;
            }
            void Emit(List<Vertex> p)
            {
                for(int i=1;i+1<p.Count;i++)
                {
                    var triangle=new[]{p[0],p[i],p[i+1]};
                    if(Vector3.Cross(triangle[1].World-triangle[0].World,triangle[2].World-triangle[0].World).LengthSquared()<1e-8f)continue;
                    foreach(var v in triangle){if(v.Original>=0)indices.Add(v.Original);else{indices.Add(vertices.Count);vertices.Add(v);}}
                }
            }
            for(int g=0;g<groupCount;g++)
            {
                var group=groups.Plus(g*gs);int first=d.Int(At(group,gt,"TriFirst")),triangles=d.Int(At(group,gt,"TriCount"));
                if(first!=expected || (long)(first+triangles)*3>indexCount)throw new InvalidDataException("Non-contiguous scenery material groups.");
                expected+=triangles;int newFirst=indices.Count/3;
                for(int i=first*3;i<(first+triangles)*3;i+=3)
                {
                    int a=Index(i),b=Index(i+1),c=Index(i+2);
                    if(new[]{a,b,c}.Any(j=>j<0||j>=count))throw new InvalidDataException("Invalid scenery index.");
                    var polygon=new List<Vertex>{vertices[a],vertices[b],vertices[c]};var intersection=polygon;
                    for(int side=0;side<4;side++)intersection=Clip(intersection,side,true);
                    if(intersection.Count<3 || !intersection.Any(v=>Enumerable.Range(0,4).All(side=>Distance(v,side)>1e-5f)))
                    {
                        // A triangle can surround the rectangle without an interior vertex.
                        var center=intersection.Count==0?Vector3.Zero:intersection.Aggregate(Vector3.Zero,(s,v)=>s+v.World)/intersection.Count;
                        if(intersection.Count<3 || center.X<=area.MinX+1e-5f||center.X>=area.MaxX-1e-5f||center.Z<=area.MinZ+1e-5f||center.Z>=area.MaxZ-1e-5f){indices.AddRange([a,b,c]);continue;}
                    }
                    for(int side=0;side<4&&polygon.Count>=3;side++){Emit(Clip(polygon,side,false));polygon=Clip(polygon,side,true);}
                }
                d.SetInt(At(group,gt,"TriFirst"),newFirst);d.SetInt(At(group,gt,"TriCount"),indices.Count/3-newFirst);
            }
            if(expected*3!=indexCount)throw new InvalidDataException("Ungrouped scenery triangles.");
            if(indexSize==2&&vertices.Count>65535)throw new NotSupportedException("Scenery cut exceeds 16-bit capacity.");
            var replacement=new byte[checked(vertices.Count*stride)];old.CopyTo(replacement,0);
            for(int i=count;i<vertices.Count;i++)
            {
                var v=vertices[i];v.Bytes.CopyTo(replacement,i*stride);
                for(int axis=0;axis<3;axis++)
                {
                    float value=v.Local[axis]/scale;
                    if(!float.IsFinite(value))throw new InvalidDataException("Nonfinite scenery position.");
                    if(packed){double encoded=Math.Round((double)value*32767);if(encoded<short.MinValue||encoded>short.MaxValue)throw new InvalidDataException("Scenery position overflow.");BinaryPrimitives.WriteInt16LittleEndian(replacement.AsSpan(i*stride+pos.Offset+axis*2),(short)encoded);}
                    else BinaryPrimitives.WriteSingleLittleEndian(replacement.AsSpan(i*stride+pos.Offset+axis*4),value);
                }
            }
            var ib=new byte[checked(indices.Count*indexSize)];
            for(int i=0;i<indices.Count;i++)if(indexSize==2)BinaryPrimitives.WriteUInt16LittleEndian(ib.AsSpan(i*2),checked((ushort)indices[i]));else BinaryPrimitives.WriteInt32LittleEndian(ib.AsSpan(i*4),indices[i]);
            if(vertices.Count!=count)d.ReplacePlainArray(start,old.Length,replacement,count,vertices.Count);
            if(indices.Count>0)d.ReplacePlainArray(indexStart,oldIndices.Length,ib,indexCount,indices.Count);
            else d.ClearReference(ia.Plus(4));
            d.SetInt(va.Plus(d.PointerSize),vertices.Count);d.SetInt(ia,indices.Count);
        }
        return d.Serialize();
    }
}
