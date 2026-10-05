using System.Buffers.Binary;
using D2RLevel.Core;

internal static class CaveContourChecks
{
    public static void Run(string folder,Action<bool,string> check,Action<Action,string> throws)
    {
        var map=Ds1CollisionDocument.Create(Path.Combine(folder,"contour-source.ds1"),7,7,1);
        map.PaintFloor(0,Enumerable.Range(1,4).SelectMany(y=>Enumerable.Range(1,4).Select(x=>(x,y))),Ds1CollisionDocument.FloorKey(5,0));
        byte[] dt1=new byte[470];
        void Put(int at,int value)=>BinaryPrimitives.WriteInt32LittleEndian(dt1.AsSpan(at),value);
        Put(0,7);Put(4,6);Put(268,2);Put(272,276);
        for(int i=0;i<2;i++){int at=276+i*96;Put(at+24,5);Put(at+72,468+i);Put(at+76,1);dt1[468+i]=(byte)(42+i);}
        string dt1Path=Path.Combine(folder,"contour-source.dt1");File.WriteAllBytes(dt1Path,dt1);
        var tiles=LegacyCollision.ReadTiles(dt1Path);var before=map.Serialize();
        var result=CaveContour.Create(map,tiles,dt1,2,2,1,1,5,5,63);
        check(result.Cells.Length==12,"contour maps all twelve rectangle perimeter tiles");
        check(result.Cells.Select(c=>c.Mask).Distinct().Count()==8,"contour represents four edges and four corners");
        check(map.Serialize().SequenceEqual(before),"contour leaves source map untouched");
        string path=Path.Combine(folder,"contour.dt1");File.WriteAllBytes(path,result.Tiles);
        var copied=LegacyCollision.ReadTiles(path);
        check(copied.Count==16&&copied.All(t=>t.Main==63),"contour clones every donor variant for each mask");
        bool graphics=true;
        for(int i=0;i<16;i++){int at=276+i*96,start=BinaryPrimitives.ReadInt32LittleEndian(result.Tiles.AsSpan(at+72));graphics&=result.Tiles[start]==42+i%2;}
        check(graphics,"contour retains each donor graphic payload exactly");
        string mapPath=Path.Combine(folder,"contour-result.ds1");File.WriteAllBytes(mapPath,result.Map);
        var after=Ds1CollisionDocument.Load(mapPath);var a=new LegacyCollision(map,tiles);var b=new LegacyCollision(after,tiles.Concat(copied));
        bool same=true,unchanged=true;
        for(int y=0;y<7;y++)for(int x=0;x<7;x++)
        {
            same&=a.At(x,y).Flags.SequenceEqual(b.At(x,y).Flags)&&a.At(x,y).Unresolved==b.At(x,y).Unresolved&&a.At(x,y).NoFloor==b.At(x,y).NoFloor;
            if(!result.Cells.Any(c=>c.X==x&&c.Y==y))unchanged&=map.Cell(map.Floors[0],x,y)==after.Cell(after.Floors[0],x,y);
        }
        check(same,"contour keeps collision identical over the entire grid");check(unchanged,"contour preserves every nonperimeter floor cell");
        throws(()=>CaveContour.Create(map,tiles,dt1,2,2,1,1,5,5,5),"contour refuses a style collision");
        throws(()=>CaveContour.Create(map,tiles,dt1,2,2,1,1,5,5,64),"contour refuses an out-of-range style");
    }
}
