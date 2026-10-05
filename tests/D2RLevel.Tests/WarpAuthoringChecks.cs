using System.Buffers.Binary;
using D2RLevel.Core;

internal static class WarpAuthoringChecks
{
    public static void Run(string folder, Action<bool,string> check, Action<Action,string> throws)
    {
        var map=Ds1CollisionDocument.Create(Path.Combine(folder,"warp-source.ds1"),8,8,1,Ds1CollisionDocument.FloorKey(5,0),wallLayers:2);
        byte[] original=map.Serialize(), marked=Ds1WarpAuthoring.AddMarker(map,0,3,3,7);
        check(map.Serialize().SequenceEqual(original),"marker authoring leaves input map byte-exact");
        string target=Path.Combine(folder,"warp-marked.ds1");File.WriteAllBytes(target,marked);
        var result=Ds1CollisionDocument.Load(target);var exit=result.ExitTiles().Single();
        check(exit is {X:3,Y:3,Main:7,Orientation:10,Hidden:true},"hidden slot seven marker survives real DS1 parser");
        var allowed=new HashSet<int>();int index=3*map.Width+3;
        foreach(int offset in new[]{map.Walls[0].Offset+index*4,map.Walls[0].Offset+map.Width*map.Height*4+index*4})
            for(int i=0;i<4;i++)allowed.Add(offset+i);
        check(original.Length==marked.Length&&Enumerable.Range(0,original.Length).All(i=>allowed.Contains(i)||original[i]==marked[i]),"marker changes only wall cell and orientation bytes");
        var floor=new Dt1CollisionTile("fixture",0,5,0,new byte[25]);
        var collision=new LegacyCollision(result,[floor]);
        check(collision.At(3,3) is {NoFloor:false,Unresolved:false,BlockedSubtiles:0},"hidden marker adds no art collision or unresolved DT1 lookup");
        throws(()=>Ds1WarpAuthoring.AddMarker(result,1,4,4,7),"marker refuses duplicate slot on another wall layer");
        foreach(var invalid in new[]{(0,0,3,7),(0,7,3,7),(0,3,3,8),(-1,3,3,7)})
            throws(()=>Ds1WarpAuthoring.AddMarker(map,invalid.Item1,invalid.Item2,invalid.Item3,invalid.Item4),"marker rejects invalid layer, border or slot");
        map.Paint([(3,3)],true);throws(()=>Ds1WarpAuthoring.AddMarker(map,0,3,3,7),"marker refuses explicit collision override");map.Undo();
        map.PaintFloor(0,[(3,3)],0);throws(()=>Ds1WarpAuthoring.AddMarker(map,0,3,3,7),"marker refuses absent floor");map.Undo();
        var blocked=new byte[25];blocked[0]=1;
        BinaryPrimitives.WriteUInt32LittleEndian(marked.AsSpan(result.Walls[1].Offset+index*4),Ds1CollisionDocument.FloorKey(9,0));
        BinaryPrimitives.WriteUInt32LittleEndian(marked.AsSpan(result.Walls[1].Offset+map.Width*map.Height*4+index*4),1);
        File.WriteAllBytes(target,marked);result=Ds1CollisionDocument.Load(target);
        check(new LegacyCollision(result,[floor,new("fixture",1,9,0,blocked)]).At(3,3).BlockedSubtiles==1,"hidden marker retains unrelated wall layer collision");
        throws(()=>Ds1WarpAuthoring.AddMarker(result,0,3,3,6),"marker refuses occupied walls");
        result.Paint([(3,3)],true);
        check(new LegacyCollision(result,[floor]).At(3,3).BlockedSubtiles==25,"hidden marker retains explicit override blocking");
        marked=Ds1WarpAuthoring.AddMarker(map,0,3,3,7);
        BinaryPrimitives.WriteUInt32LittleEndian(marked.AsSpan(map.Walls[0].Offset+index*4),0x00700081u);
        File.WriteAllBytes(target,marked);result=Ds1CollisionDocument.Load(target);
        check(new LegacyCollision(result,[floor]).At(3,3).Unresolved,"visible exit still requires DT1 collision definition");
        check(new LegacyCollision(result,[floor,new("fixture",10,7,0,blocked)]).At(3,3).BlockedSubtiles==1,"visible exit retains resolved DT1 collision flags");
        BinaryPrimitives.WriteUInt32LittleEndian(marked.AsSpan(map.Walls[0].Offset+index*4),0x80900081u);
        File.WriteAllBytes(target,marked);result=Ds1CollisionDocument.Load(target);
        check(new LegacyCollision(result,[floor]).At(3,3).Unresolved,"unknown hidden main nine still requires DT1 collision definition");
        check(new LegacyCollision(result,[floor,new("fixture",10,9,0,blocked)]).At(3,3).BlockedSubtiles==1,"unknown hidden main nine retains DT1 blocking");
        string jsonPath=Path.Combine(folder,"warp-shared.json");File.WriteAllText(jsonPath,"{\"entities\":[]}");
        new PlacementLinks(PresetDocument.Load(jsonPath),map).ConnectWorkspace();
        throws(()=>Ds1WarpAuthoring.AddMarker(map,0,3,3,7),"marker refuses live paired history");
    }
}
