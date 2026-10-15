using D2RLevel.Core;

internal static class BoundaryChecks
{
    public static void Run(string folder, Action<bool,string> check, Action<Action,string> throws)
    {
        var map=Ds1CollisionDocument.Create(Path.Combine(folder,"boundary.ds1"),16,16,1,Ds1CollisionDocument.FloorKey(0,0));
        File.WriteAllBytes(map.SourcePath,map.Serialize());
        var p=Path.Combine(folder,"boundary.json");File.WriteAllText(p,"{\"entities\":[]}");
        var scene=PresetDocument.Load(p);var links=new PlacementLinks(scene,map);
        var request=new BoundaryRequest(3,3,12,12,BoundarySide.West,6,3);
        var bounds=new BoundaryModelBounds(new(-2,-1,-3),new(3,4,2));
        throws(()=>BoundaryAuthoring.Apply(scene,map,links,request,"data/test/wall.model",bounds),"boundary requires explicit pair calibration");
        links.SetCalibration(GridCalibration.Typed(10));
        byte[] before=map.Serialize(),beforeScene=scene.Serialize();
        var added=BoundaryAuthoring.Apply(scene,map,links,request,"data/test/wall.model",bounds);
        check(added.Length==29 && links.Links.Count==29,"boundary places unique wall pieces and owned collision together");
        bool Blocked(int x,int y)=>(map.Cell(map.Floors[0],x,y)&0x20000)!=0;
        check(links.Links.All(l=>Blocked(l.Tiles[0].X,l.Tiles[0].Y)) && !Blocked(3,7) && !Blocked(7,7),"walls block; three-tile gate and interior stay open");
        var first=added[0].Transform;
        check(Math.Abs(first.Position.X+bounds.Min.X*first.Scale.X-30)<.00001,"wall minimum matches the blocked tile origin");
        var after=map.Serialize();scene.History.Undo();
        check(map.Serialize().SequenceEqual(before)&&scene.Serialize().SequenceEqual(beforeScene)&&!links.HasLinks,"one undo restores scene collision and ownership");
        scene.History.Redo();check(map.Serialize().SequenceEqual(after)&&links.Links.Count==29,"one redo restores the entire boundary");
        var export=links.ExportPair(folder);var reScene=PresetDocument.Load(export);var reMap=Ds1CollisionDocument.Load(PlacementLinks.LinkedDs1Path(export)!);var reLinks=new PlacementLinks(reScene,reMap);
        check(reLinks.Warning is null&&!reLinks.HasBrokenLinks&&reLinks.Links.Count==29&&reMap.Serialize().SequenceEqual(after),"boundary save/reopen retains blocking and links");
        reLinks.DeleteModel(reScene.Entities[0]);check(reLinks.Links.Count==28,"deleting a boundary piece removes its collision owner");
        throws(()=>BoundaryAuthoring.Plan(map,request with {GateWidth=1},bounds,10),"reject narrow gate incompatible with contour art");
        throws(()=>BoundaryAuthoring.Plan(map,request with {MaxX=16},bounds,10),"boundary preserves final map border");
        var bad=bounds with {Max=new(0,double.NaN,0)};
        throws(()=>BoundaryAuthoring.Plan(map,request,bad,10),"reject invalid model bounds without edits");
        var polygonMap=Ds1CollisionDocument.Create(Path.Combine(folder,"polygon.ds1"),26,24,1,Ds1CollisionDocument.FloorKey(0,0));
        LinkedTile[] corners=[new(4,4),new(19,4),new(19,10),new(12,10),new(12,17),new(4,17)];
        var polygon=request with {Corners=corners,GateEdge=0,GateStart=5};
        var shape=BoundaryPolygon.Layout(polygonMap,polygon);
        check(shape.Walls.Length==53&&shape.Walls.Distinct().Count()==53,"concave outline has one wall per perimeter tile outside gate");
        check(shape.Interior.Contains(new(6,14))&&!shape.Interior.Contains(new(16,14)),"L outline preserves concave cutout");
        check(shape.Gate.SequenceEqual(new LinkedTile[]{new(9,4),new(10,4),new(11,4)}),"gate offset follows selected segment");
        var reversed=polygon with {Corners=corners.Reverse().ToArray(),GateEdge=4,GateStart=8};
        var backwards=BoundaryPolygon.Layout(polygonMap,reversed);
        check(backwards.Walls.ToHashSet().SetEquals(shape.Walls)&&backwards.Interior.ToHashSet().SetEquals(shape.Interior),"clockwise and anticlockwise outlines preserve wall cells and interior");
        // Non-corner normals must point outward regardless of winding.
        var at=Array.IndexOf(shape.Walls,new LinkedTile(15,4));var reverseAt=Array.IndexOf(backwards.Walls,new LinkedTile(15,4));
        check(shape.Angles[at]==backwards.Angles[reverseAt],"wall facing remains outward after reversing corners");
        throws(()=>BoundaryPolygon.Layout(polygonMap,polygon with {Corners=[new(4,4),new(19,5),new(19,17),new(4,17)]}),"diagonal outline rejected");
        throws(()=>BoundaryPolygon.Layout(polygonMap,polygon with {Corners=[new(4,4),new(19,4),new(19,17),new(9,17),new(9,2),new(4,2)]}),"crossing outline rejected");
        throws(()=>BoundaryPolygon.Layout(polygonMap,polygon with {GateStart=0}),"gate cannot consume a corner");
        throws(()=>BoundaryPolygon.Layout(polygonMap,polygon with {Corners=[new(4,4),new(25,4),new(25,17),new(4,17)]}),"polygon cannot consume final border");
        var clearTiles=new[]{new Dt1CollisionTile("test",0,0,0,new byte[25])};
        BoundaryPolygon.ValidateFloor(new(polygonMap,clearTiles),shape);
        polygonMap.PaintFloor(0,[(9,3)],0);
        throws(()=>BoundaryPolygon.ValidateFloor(new(polygonMap,clearTiles),shape),"gate exterior requires actual walkable floor");
        polygonMap.PaintFloor(0,[(9,3)],Ds1CollisionDocument.FloorKey(0,0));
        var polygonScene=PresetDocument.Load(p);var polygonLinks=new PlacementLinks(polygonScene,polygonMap);polygonLinks.SetCalibration(GridCalibration.Typed(10));
        var polygonBefore=polygonMap.Serialize();
        var polygonAdded=BoundaryAuthoring.Apply(polygonScene,polygonMap,polygonLinks,polygon,"data/test/wall.model",bounds);
        check(polygonAdded.Length==53&&polygonLinks.Links.Count==53,"concave walls and ownership created together");
        polygonScene.History.Undo();check(polygonMap.Serialize().SequenceEqual(polygonBefore)&&!polygonLinks.HasLinks,"concave boundary undo restores collision and ownership");
    }
}
