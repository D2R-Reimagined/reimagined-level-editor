using System.Text.Json;
using System.Text.Json.Nodes;
using D2RLevel.Core;

namespace D2RLevel.Assets;

public sealed record BoundaryWallRecord(string Id, string Json, PlacementLink Link);
public sealed record BoundaryFloorRecord(int Layer, int X, int Y, uint Before, uint After);
public sealed record BoundaryRevision(int Version, int Width, int Height, string Contour,
    string ContourHash, BoundaryRequest Request, string WallModel, BoundaryWallRecord[] Walls,
    BoundaryFloorRecord[] Floors)
{
    public const string Suffix = ".rle-boundary.json";

    public static BoundaryRevision? Load(string preset)
    {
        var path=preset+Suffix;
        if(!File.Exists(path))return null;
        if(new FileInfo(path).Length>8*1024*1024)throw new InvalidDataException("Boundary record exceeds 8 MiB.");
        var value=JsonSerializer.Deserialize<BoundaryRevision>(File.ReadAllBytes(path));
        if(value is null || value.Version!=1 || value.Walls is null || value.Floors is null ||
            value.Walls.Length is <1 or >256 || value.Floors.Length>512*512*2 ||
            value.Walls.Select(w=>w.Id).Distinct().Count()!=value.Walls.Length ||
            value.Floors.Select(f=>(f.Layer,f.X,f.Y)).Distinct().Count()!=value.Floors.Length)
            throw new InvalidDataException("Invalid boundary revision record.");
        return value;
    }

    // Documents are private export/preview copies. Validate everything before touching them.
    public void Restore(PresetDocument scene, Ds1CollisionDocument map, PlacementLinks links)
    {
        if(map.Width!=Width || map.Height!=Height || links.Warning is not null || links.HasBrokenLinks)
            throw new InvalidDataException("Boundary pair changed or has broken links; regeneration stopped.");
        var entities=Walls.Select(w=>{
            var entity=scene.Entities.SingleOrDefault(e=>e.Id==w.Id);
            var link=entity is null?null:links.Find(entity);
            if(entity is null || !JsonNode.DeepEquals(JsonNode.Parse(entity.RawJson),JsonNode.Parse(w.Json)) ||
                !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(link),JsonSerializer.SerializeToNode(w.Link)))
                throw new InvalidDataException("Generated wall was edited or removed; regeneration stopped: "+w.Id);
            return entity;
        }).ToArray();
        foreach(var f in Floors)
            if(f.Layer<0 || f.Layer>=map.Floors.Count || f.X<0 || f.Y<0 || f.X>=Width || f.Y>=Height ||
                map.Cell(map.Floors[f.Layer],f.X,f.Y)!=f.After)
                throw new InvalidDataException($"Generated outline floor was edited; regeneration stopped at {f.X},{f.Y}.");
        links.ConnectWorkspace();
        scene.History.Transaction(()=>{
            foreach(var entity in entities)links.DeleteModel(entity);
            foreach(var f in Floors)links.PaintFloor(f.Layer,[(f.X,f.Y)],f.Before);
        });
    }

    public static BoundaryRevision Capture(PresetDocument scene, PlacementLinks links,
        Ds1CollisionDocument before, Ds1CollisionDocument after, PresetEntity[] added,
        string contour,string contourHash,BoundaryRequest request,string wallModel)
    {
        var floors=new List<BoundaryFloorRecord>();
        for(int l=0;l<before.Floors.Count;l++)for(int y=0;y<before.Height;y++)for(int x=0;x<before.Width;x++){
            uint a=before.Cell(before.Floors[l],x,y),b=after.Cell(after.Floors[l],x,y);
            if(a!=b)floors.Add(new(l,x,y,a,b));
        }
        return new(1,before.Width,before.Height,contour,contourHash,request,wallModel,
            added.Select(e=>new BoundaryWallRecord(e.Id,e.RawJson,links.Find(e)!)).ToArray(),floors.ToArray());
    }
}
