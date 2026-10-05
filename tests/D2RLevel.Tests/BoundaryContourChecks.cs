using System.Buffers.Binary;
using D2RLevel.Core;

internal static class BoundaryContourChecks
{
    public static void Run(string folder,Action<bool,string> check,Action<Action,string> throws)
    {
        var map=Ds1CollisionDocument.Create(Path.Combine(folder,"different-act.ds1"),19,14,4);
        for(int y=1;y<12;y++)for(int x=1;x<(y<6?17:14);x++)
            map.PaintFloor(0,[(x,y)],Ds1CollisionDocument.FloorKey(x<5?7:17,0));
        map.PaintFloor(0,[(16,11)],Ds1CollisionDocument.FloorKey(63,0)); // Unresolved source identities must remain unresolved.
        var source=new byte[567];
        void Put(int at,int v)=>BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(at),v);
        Put(0,7);Put(4,6);Put(268,3);Put(272,276);
        for(int i=0;i<3;i++){
            int at=276+i*96;Put(at+24,i==2?17:7);Put(at+72,564+i);Put(at+76,1);
            source[564+i]=(byte)(81+i);source[at+40]=2;
        }
        string dt1=Path.Combine(folder,"mixed-floors.dt1");File.WriteAllBytes(dt1,source);
        var tiles=LegacyCollision.ReadTiles(dt1);var collision=new LegacyCollision(map,tiles);
        var suggested=BoundaryAuthoring.Suggest(collision);
        check(suggested is not null && suggested.MinX>=1 && suggested.MaxX<map.Width,"boundary defaults derive a clear rectangle from a different L-shaped map");
        var request=new BoundaryRequest(3,3,10,9,BoundarySide.West,4,3);
        var plan=BoundaryAuthoring.Plan(map,request,new(new(0,0,0),new(1,1,1)),8);
        map.Paint(plan.Cells.Select(c=>(c.X,c.Y)),true);
        var original=map.Serialize();
        var result=BoundaryContour.Create(map,new Dictionary<string,byte[]>{{dt1,source}},request);
        check(result.Identities.Length==2 && result.Identities.Select(i=>i.Style).Distinct().Count()==2,"mixed floor identities receive distinct available styles");
        check(result.Identities.All(i=>i.Style!=7&&i.Style!=17&&i.Style!=63),"automatic styles avoid both loaded and unresolved source identities");
        check(result.Cells.Any(c=>c.Mask==0),"interior cells clear obsolete automap outlines with mask zero");
        check(map.Serialize().SequenceEqual(original),"generic contour does not mutate the source map");
        string target=Path.Combine(folder,"different-act-out.ds1"),outTile=Path.Combine(folder,"different-act-out.dt1");
        File.WriteAllBytes(target,result.Map);File.WriteAllBytes(outTile,result.Tiles);
        var reopened=Ds1CollisionDocument.Load(target);var after=new LegacyCollision(reopened,tiles.Concat(LegacyCollision.ReadTiles(outTile)));
        bool same=true;
        for(int y=0;y<map.Height;y++)for(int x=0;x<map.Width;x++){
            var a=collision.At(x,y);var b=after.At(x,y);
            same &= a.Flags.SequenceEqual(b.Flags)&&a.Unresolved==b.Unresolved&&a.VariantDependent==b.VariantDependent&&a.NoFloor==b.NoFloor;
        }
        check(same,"all collision flags survive contour substitution on a 19x14 Act IV L-shaped map");
        int count=BinaryPrimitives.ReadInt32LittleEndian(result.Tiles.AsSpan(268));bool payloads=true;
        var variantCounts=new Dictionary<(int,int),int>();
        for(int i=0;i<count;i++){
            int at=276+i*96,style=BinaryPrimitives.ReadInt32LittleEndian(result.Tiles.AsSpan(at+24)),mask=BinaryPrimitives.ReadInt32LittleEndian(result.Tiles.AsSpan(at+28)),offset=BinaryPrimitives.ReadInt32LittleEndian(result.Tiles.AsSpan(at+72));
            var identity=result.Identities.Single(k=>k.Style==style);
            payloads &= identity.SourceMain==7 ? result.Tiles[offset] is 81 or 82 : result.Tiles[offset]==83;
            variantCounts[(style,mask)]=variantCounts.GetValueOrDefault((style,mask))+1;
        }
        check(payloads && result.Identities.All(i=>i.Masks.All(m=>variantCounts[(i.Style,m)]==(i.SourceMain==7?2:1))),"every distinct floor preserves its own graphics and all variants");
        var empty=Ds1CollisionDocument.Create(Path.Combine(folder,"empty.ds1"),13,9,2);
        check(BoundaryAuthoring.Suggest(new(empty,tiles)) is null,"missing ground yields no misleading default boundary");
        var invalid=source.ToArray();BinaryPrimitives.WriteInt32LittleEndian(invalid.AsSpan(276+72),1);
        throws(()=>BoundaryContour.Create(map,new Dictionary<string,byte[]>{{dt1,invalid}},request),"reject malformed donor payload before publishing");
    }
}
