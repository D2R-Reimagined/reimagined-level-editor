using System.Text.Json;
using D2RLevel.Core;
using D2RLevel.Assets;

internal static class BoundaryPortabilityChecks
{
    public static void Run(string assetsRoot,string decoder,string destination,bool polygon=false,bool regenerate=false)
    {
        destination=Path.GetFullPath(destination);
        if(Directory.Exists(destination))throw new IOException("Choose a fresh portability output.");
        ModelReader.ConfigureDecoder(Path.GetFullPath(decoder));
        var assets=new AssetResolver(assetsRoot);
        const string tileLogical="data/global/tiles/act1/caves/cave.dt1";
        var tiles=LegacyCollision.ReadTiles(assets.Resolve(tileLogical));
        var floor=tiles.Where(t=>t.Orientation==0).GroupBy(t=>(t.Main,t.Sub))
            .First(g=>g.All(t=>t.Flags.All(f=>(f&9)==0)&&t.Flags.SequenceEqual(g.First().Flags))).Key;
        var reports=new List<object>();
        foreach(var (name,width,height) in polygon?new[]{("polygon",26,24),("wide-polygon",31,27)}:new[]{("elbow",19,14),("wide-elbow",27,18)})
        {
            string root=Path.Combine(destination,name,"data"),preset=Path.Combine(root,$"hd/env/preset/act1/{name}/map.json"),ds1=Path.Combine(root,$"global/tiles/act1/{name}/map.ds1");
            Directory.CreateDirectory(Path.GetDirectoryName(preset)!);Directory.CreateDirectory(Path.GetDirectoryName(ds1)!);
            // This is an authoring fixture: actual DT1 and wall assets, independently created DS1, no HD terrain claim.
            File.WriteAllText(preset,"{\"entities\":[]}");
            var map=Ds1CollisionDocument.Create(ds1,width,height,1);
            for(int y=1;y<height-2;y++)for(int x=1;x<(polygon||y<height/2?width-2:width-5);x++)
                map.PaintFloor(0,[(x,y)],Ds1CollisionDocument.FloorKey(floor.Main,floor.Sub));
            File.WriteAllBytes(ds1,map.Serialize());
            var context=new LevelTileset(1,[tileLogical]);
            var sourceProject=new LevelProject(1,Guid.NewGuid(),name,"data/hd/env/preset/act1/"+name+"/map.json",
                "data/global/tiles/act1/"+name+"/map.ds1",width,height,1,context,"data/hd/env/preset/act1/"+name+"/map.json",
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(preset))));
            File.WriteAllBytes(preset+LevelProject.Suffix,JsonSerializer.SerializeToUtf8Bytes(sourceProject));
            var sourceLinks=new PlacementLinks(PresetDocument.Load(preset),map);sourceLinks.SetCalibration(GridCalibration.Typed(10));
            File.WriteAllBytes(preset+PlacementLinks.Suffix,sourceLinks.MetadataFor(preset+PlacementLinks.Suffix,ds1));
            var request=BoundaryAuthoring.Suggest(new(map,tiles)) ?? throw new InvalidDataException("Independent map has no supported boundary.");
            if(polygon)request=request with {Corners=[new(4,4),new(19,4),new(19,10),new(12,10),new(12,17),new(4,17)],GateEdge=0,GateStart=5};
            var beforeMap=File.ReadAllBytes(ds1);var beforeScene=File.ReadAllBytes(preset);
            string output=BoundaryExporter.Export(preset,ds1,assets,request,
                "data/hd/env/model/act1/caves/act1_caves_walls/R_wall01.model",Path.Combine(destination,name+"-export"),GridCalibration.Typed(10),context);
            var project=LevelProject.ForPreset(output)!;
            var data=PresetPairing.Split(output,"hd/env/preset")!.Value.DataRoot;
            var afterMap=Ds1CollisionDocument.Load(Path.Combine(data,project.Map[5..]));project.VerifyMap(afterMap);
            var scene=PresetDocument.Load(output);var links=new PlacementLinks(scene,afterMap);
            var actualTiles=project.Tileset.Files.SelectMany(p=>LegacyCollision.ReadTiles(Path.Combine(data,p[5..]))).ToArray();
            var originalCollision=new LegacyCollision(map,tiles);var after=new LegacyCollision(afterMap,actualTiles);
            int checks=0;void Require(bool b,string message){checks++;if(!b)throw new InvalidDataException(message);}
            Require(links.Warning is null&&!links.HasBrokenLinks&&links.Links.Count==scene.Entities.Count,"Export links must reopen healthy.");
            var walls=links.Links.SelectMany(l=>l.Tiles).ToHashSet();
            for(int y=0;y<height;y++)for(int x=0;x<width;x++){
                var a=originalCollision.At(x,y);var b=after.At(x,y);
                Require(a.Unresolved==b.Unresolved&&a.NoFloor==b.NoFloor&&a.VariantDependent==b.VariantDependent,"Floor semantics changed.");
                Require(walls.Contains(new(x,y))?b.BlockedSubtiles==25:a.Flags.SequenceEqual(b.Flags),"Collision changed outside authored walls.");
            }
            Require(File.ReadAllBytes(ds1).SequenceEqual(beforeMap)&&File.ReadAllBytes(preset).SequenceEqual(beforeScene),"Source pair changed.");
            if(polygon){
                var shape=BoundaryPolygon.Layout(map,request);
                foreach(var cell in shape.Gate.Concat(shape.Approaches))Require(after.At(cell.X,cell.Y).BlockedSubtiles==0,"Polygon gate must connect interior and exterior.");
                Require(walls.SetEquals(shape.Walls),"Exported walls must follow the concave outline exactly.");
                Require(!walls.Contains(new(16,17))&&walls.Contains(new(12,14)),"Concave cutout must not become a bounding rectangle.");
            }
            else for(int i=0;i<request.GateWidth;i++){
                var p=request.GateSide switch{
                    BoundarySide.North=>(X:request.GateStart+i,Y:request.MinY,DX:0,DY:-1),
                    BoundarySide.South=>(X:request.GateStart+i,Y:request.MaxY-1,DX:0,DY:1),
                    BoundarySide.West=>(X:request.MinX,Y:request.GateStart+i,DX:-1,DY:0),
                    _=>(X:request.MaxX-1,Y:request.GateStart+i,DX:1,DY:0)};
                Require(after.At(p.X,p.Y).BlockedSubtiles==0&&after.At(p.X+p.DX,p.Y+p.DY).BlockedSubtiles==0,"Gate must connect to open exterior floor.");
            }
            var saved=links.ExportPair(Path.Combine(destination,name+"-saved"));
            var savedMap=Ds1CollisionDocument.Load(PlacementLinks.LinkedDs1Path(saved)!);
            var savedLinks=new PlacementLinks(PresetDocument.Load(saved),savedMap);
            Require(savedLinks.Warning is null&&!savedLinks.HasBrokenLinks&&savedMap.Serialize().SequenceEqual(afterMap.Serialize()),"Saved pair failed to reopen.");
            int regenerationChecks=regenerate?BoundaryRegenerationChecks.Run(output,assets,Path.Combine(destination,name+"-regeneration")):0;
            reports.Add(new{name,width,height,request,wallCount=walls.Count,checks,regenerationChecks,output,sourcePreserved=true,saveReopen="passed",hdTerrain="not part of this authoring fixture",editorUi="not run",gameplay="not run"});
        }
        File.WriteAllBytes(Path.Combine(destination,"portability.json"),JsonSerializer.SerializeToUtf8Bytes(reports,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PASS two independent maps exported and saved/reopened; full collision comparison passed. Polygon="+polygon);
    }
}
