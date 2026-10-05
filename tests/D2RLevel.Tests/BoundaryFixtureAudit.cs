using System.Text.Json;
using D2RLevel.Core;

internal static class BoundaryFixtureAudit
{
    public static void Run(string beforeData,string afterData,string output)
    {
        const string relative="global/tiles/act1/rlecave/cave.ds1";
        var before=Ds1CollisionDocument.Load(Path.Combine(beforeData,relative));var after=Ds1CollisionDocument.Load(Path.Combine(afterData,relative));
        var scene=PresetDocument.Load(Path.Combine(afterData,"hd/env/preset/act1/rlecave/cave.json"));var links=new PlacementLinks(scene,after);
        var oldTiles=new[]{"cave.dt1","rle_ground_contour.dt1"}.SelectMany(n=>LegacyCollision.ReadTiles(Path.Combine(beforeData,"global/tiles/act1/caves",n))).ToArray();
        var newTiles=new[]{"cave.dt1","rle_ground_contour.dt1","rle_boundary_contour.dt1"}.SelectMany(n=>LegacyCollision.ReadTiles(Path.Combine(afterData,"global/tiles/act1/caves",n))).ToArray();
        var a=new LegacyCollision(before,oldTiles);var b=new LegacyCollision(after,newTiles);int checks=0;
        void Require(bool value,string message){checks++;if(!value)throw new InvalidDataException(message);}
        Require(links.Warning is null&&!links.HasBrokenLinks&&links.Links.Count==27,"Boundary links must reopen healthy");
        var walls=links.Links.SelectMany(l=>l.Tiles).ToHashSet();
        for(int y=0;y<after.Height;y++)for(int x=0;x<after.Width;x++){
            var old=a.At(x,y);var current=b.At(x,y);
            Require(old.Unresolved==current.Unresolved&&old.NoFloor==current.NoFloor,"Boundary cannot change floor resolution");
            if(walls.Contains(new(x,y)))Require(old.BlockedSubtiles==0&&current.BlockedSubtiles==25,"Every wall must block all 25 subtiles");
            else Require(old.Flags.SequenceEqual(current.Flags),"No blocking changes outside wall cells");
        }
        for(int y=10;y<13;y++)Require(b.At(28,y).BlockedSubtiles==0&&!b.At(28,y).NoFloor,"West gate must remain open");
        Require(b.At(33,11).BlockedSubtiles==0,"Interior remains walkable");
        Require(before.ExitTiles().SequenceEqual(after.ExitTiles()),"Existing exits preserved");
        File.WriteAllBytes(output,JsonSerializer.SerializeToUtf8Bytes(new{checks,wallTiles=walls.Count,gate="3 open tiles",outsideBlocking="byte-equivalent flags",exits="unchanged",links="healthy",gameplay="not run"},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS {checks} registered boundary checks: {walls.Count} blocking wall cells, open gate, unchanged exits.");
    }
}
