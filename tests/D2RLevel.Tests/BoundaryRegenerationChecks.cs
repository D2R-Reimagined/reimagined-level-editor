using System.Text.Json;
using D2RLevel.Core;
using D2RLevel.Assets;

internal static class BoundaryRegenerationChecks
{
    public static int Run(string preset,AssetResolver assets,string destination)
    {
        int checks=0;
        void Require(bool value,string message){checks++;if(!value)throw new InvalidDataException(message);}
        const string model="data/hd/env/model/act1/caves/act1_caves_walls/R_wall01.model";
        string Map(string p)=>PlacementLinks.LinkedDs1Path(p)!;
        void Save(string p,PresetDocument scene,Ds1CollisionDocument map,PlacementLinks links){
            File.WriteAllBytes(p,scene.Serialize());File.WriteAllBytes(map.SourcePath,map.Serialize());
            File.WriteAllBytes(p+PlacementLinks.Suffix,links.MetadataFor(p+PlacementLinks.Suffix,map.SourcePath));
        }
        var scene=PresetDocument.Load(preset);var map=Ds1CollisionDocument.Load(Map(preset));var links=new PlacementLinks(scene,map);
        var unrelated=scene.PlaceModels(model,[new(new(220,0,200),new(0,0,0,1),new(1,1,1))]).Added.Single();
        links.LinkFootprint(unrelated,[new(22,20)],10,false);
        string unrelatedJson=unrelated.RawJson;Save(preset,scene,map,links);
        var originalRevision=BoundaryRevision.Load(preset)!;
        var request=originalRevision.Request with {GateStart=6,Corners=[new(4,4),new(18,4),new(18,10),new(10,10),new(10,17),new(4,17)]};
        for(int generation=0;generation<3;generation++){
            byte[] before=File.ReadAllBytes(preset),beforeMap=File.ReadAllBytes(Map(preset));
            string next=BoundaryExporter.Export(preset,Map(preset),assets,request,model,Path.Combine(destination,"generation-"+generation));
            Require(File.ReadAllBytes(preset).SequenceEqual(before)&&File.ReadAllBytes(Map(preset)).SequenceEqual(beforeMap),"Regeneration mutated its source.");
            scene=PresetDocument.Load(next);map=Ds1CollisionDocument.Load(Map(next));links=new PlacementLinks(scene,map);
            var revision=BoundaryRevision.Load(next)!;var project=LevelProject.ForPreset(next)!;
            var data=PresetPairing.Split(next,"hd/env/preset")!.Value.DataRoot;
            var collision=new LegacyCollision(map,project.Tileset.Files.SelectMany(p=>LegacyCollision.ReadTiles(Path.Combine(data,p[5..]))));
            var expected=BoundaryPolygon.Layout(map,request).Walls.ToHashSet();
            Require(scene.Entities.Count==expected.Count+1&&links.Links.Count==expected.Count+1,"Regeneration duplicated walls or dropped unrelated links.");
            Require(scene.Entities.Single(e=>e.Id==unrelated.Id).RawJson==unrelatedJson,"Unrelated HD object changed.");
            Require(project.Tileset.Files.Length==2&&revision.Walls.Length==expected.Count,"Tilesets or managed wall records accumulated.");
            Require(links.Warning is null&&!links.HasBrokenLinks,"Regenerated pair did not reopen healthy.");
            for(int y=1;y<map.Height-2;y++)for(int x=1;x<map.Width-2;x++)
                Require(collision.At(x,y).BlockedSubtiles==(expected.Contains(new(x,y))||(x==22&&y==20)?25:0),$"Stale or missing collision at {x},{y}.");
            // Every generated floor must map directly to an original source floor, never a previous generated style.
            Require(revision.Floors.All(f=>new FloorCell(f.Before).Main!=new FloorCell(f.After).Main),"Outline restoration did not recover original identities.");
            preset=next;
        }
        // A manually edited managed object is a conflict, even if its link remains healthy.
        var owned=scene.Entities.Single(e=>e.Id==BoundaryRevision.Load(preset)!.Walls[0].Id);
        var transform=owned.Transform;scene.SetTransform(owned,transform with {Scale=new(2,2,2)});Save(preset,scene,map,links);
        var conflict=Path.Combine(destination,"conflict");bool rejected=false;
        try{BoundaryExporter.Export(preset,Map(preset),assets,request,model,conflict);}catch(InvalidDataException){rejected=true;}
        Require(rejected&&!Directory.Exists(conflict),"Changed managed wall was not rejected atomically.");
        scene.SetTransform(owned,transform);Save(preset,scene,map,links);
        var recordedFloor=BoundaryRevision.Load(preset)!.Floors.First();
        links.PaintFloor(recordedFloor.Layer,[(recordedFloor.X,recordedFloor.Y)],Ds1CollisionDocument.FloorKey(0,99));
        Save(preset,scene,map,links);var conflictMap=File.ReadAllBytes(Map(preset));
        rejected=false;
        try{BoundaryExporter.Export(preset,Map(preset),assets,request,model,conflict);}catch(InvalidDataException){rejected=true;}
        Require(rejected&&!Directory.Exists(conflict)&&File.ReadAllBytes(Map(preset)).SequenceEqual(conflictMap),"Edited outline floor was not rejected without changing the source.");
        Console.WriteLine($"PASS {checks} regeneration checks: three generations, unrelated owner preserved, stale blocking cleared, edited wall and floor rejected.");
        return checks;
    }
}
