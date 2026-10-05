namespace D2RLevel.Core;

public enum BoundarySide { North, East, South, West }
public sealed record BoundaryRequest(int MinX, int MinY, int MaxX, int MaxY,
    BoundarySide GateSide, int GateStart, int GateWidth, double Height = 10,
    LinkedTile[]? Corners = null, int GateEdge = 0);
public sealed record BoundaryModelBounds(Vector3d Min, Vector3d Max);
public sealed record BoundaryPlan(LinkedTile[] Cells, EntityTransform[] Transforms);

/// <summary>HD wall pieces and conservative, owned full-tile blocking for a rectangle or orthogonal outline.</summary>
public static class BoundaryAuthoring
{
    /// <summary>Find a clear floor rectangle using the opened map, without fixture coordinates.</summary>
    public static BoundaryRequest? Suggest(LegacyCollision collision)
    {
        var map=collision.Document;int width=map.Width-1,height=map.Height-1;
        var exits=map.ExitTiles().Select(e=>(e.X,e.Y)).ToHashSet();
        var bad=new int[width+1,height+1];
        for(int y=0;y<height;y++)for(int x=0;x<width;x++){
            bool open=collision.At(x,y) is {NoFloor:false,Unresolved:false,VariantDependent:false,BlockedSubtiles:0} && !exits.Contains((x,y));
            bad[x+1,y+1]=(open?0:1)+bad[x,y+1]+bad[x+1,y]-bad[x,y];
        }
        bool Open(int x,int y)=>x>=0&&y>=0&&x<width&&y<height&&bad[x+1,y+1]-bad[x,y+1]-bad[x+1,y]+bad[x,y]==0;
        bool Supported(BoundaryRequest r){
            for(int step=0;step<r.GateWidth;step++){
                var outside=r.GateSide switch{
                    BoundarySide.North=>(X:r.GateStart+step,Y:r.MinY-1),
                    BoundarySide.South=>(X:r.GateStart+step,Y:r.MaxY),
                    BoundarySide.West=>(X:r.MinX-1,Y:r.GateStart+step),
                    _=>(X:r.MaxX,Y:r.GateStart+step)};
                if(!Open(outside.X,outside.Y))return false;
            }
            bool Gate(int x,int y)=>r.GateSide switch{
                BoundarySide.West=>x==r.MinX&&y>=r.GateStart&&y<r.GateStart+r.GateWidth,
                BoundarySide.East=>x==r.MaxX-1&&y>=r.GateStart&&y<r.GateStart+r.GateWidth,
                BoundarySide.North=>y==r.MinY&&x>=r.GateStart&&x<r.GateStart+r.GateWidth,
                _=>y==r.MaxY-1&&x>=r.GateStart&&x<r.GateStart+r.GateWidth};
            bool After(int x,int y)=>Open(x,y)&&!((x==r.MinX||x==r.MaxX-1||y==r.MinY||y==r.MaxY-1)&&
                x>=r.MinX&&x<r.MaxX&&y>=r.MinY&&y<r.MaxY&&!Gate(x,y));
            for(int y=Math.Max(0,r.MinY-1);y<Math.Min(height,r.MaxY+1);y++)
            for(int x=Math.Max(0,r.MinX-1);x<Math.Min(width,r.MaxX+1);x++){
                if(!After(x,y))continue;
                int mask=(!After(x-1,y)?1:0)|(!After(x,y-1)?2:0)|(!After(x+1,y)?4:0)|(!After(x,y+1)?8:0);
                if(mask is not (0 or 1 or 2 or 4 or 8 or 3 or 6 or 9 or 12))return false;
            }
            return true;
        }
        for(int sx=Math.Min(10,width);sx>=5;sx--)for(int sy=Math.Min(9,height);sy>=5;sy--)
        for(int y=0;y<=height-sy;y++)for(int x=0;x<=width-sx;x++)
            if(bad[x+sx,y+sy]-bad[x,y+sy]-bad[x+sx,y]+bad[x,y]==0)
            {
                foreach(var side in Enum.GetValues<BoundarySide>()){
                    var candidate=new BoundaryRequest(x,y,x+sx,y+sy,side,
                        side is BoundarySide.North or BoundarySide.South ? x+(sx-3)/2 : y+(sy-3)/2,3);
                    if(Supported(candidate))return candidate;
                }
            }
        return null;
    }

    public static BoundaryPlan Plan(Ds1CollisionDocument map, BoundaryRequest request,
        BoundaryModelBounds model, double unitsPerTile)
    {
        GridCalibration.Validate(unitsPerTile);
        request=BoundaryPolygon.Region(request);
        if (!Enum.IsDefined(request.GateSide) || request.MinX < 0 || request.MinY < 0 ||
            request.MaxX >= map.Width || request.MaxY >= map.Height ||
            request.MaxX-request.MinX < 5 || request.MaxY-request.MinY < 5 ||
            request.GateWidth < 3 || !double.IsFinite(request.Height) || request.Height <= 0)
            throw new ArgumentException("Choose a boundary inside the final border, at least five tiles wide/high, with a gate at least three tiles wide.");
        bool horizontal = request.GateSide is BoundarySide.North or BoundarySide.South;
        int low = horizontal ? request.MinX : request.MinY, high = horizontal ? request.MaxX : request.MaxY;
        if (request.Corners is null && (request.GateStart <= low || (long)request.GateStart + request.GateWidth >= high))
            throw new ArgumentException("Keep the gate away from boundary corners.");
        double dx=model.Max.X-model.Min.X, dy=model.Max.Y-model.Min.Y, dz=model.Max.Z-model.Min.Z;
        if (new[]{model.Min.X,model.Min.Y,model.Min.Z,model.Max.X,model.Max.Y,model.Max.Z,dx,dy,dz}.Any(v=>!double.IsFinite(v)) || dx<=0 || dy<=0 || dz<=0)
            throw new ArgumentException("Wall model must have finite, nonempty bounds on all axes.");
        bool Gate(int x,int y) => request.GateSide switch {
            BoundarySide.North => y==request.MinY && x>=request.GateStart && x<request.GateStart+request.GateWidth,
            BoundarySide.South => y==request.MaxY-1 && x>=request.GateStart && x<request.GateStart+request.GateWidth,
            BoundarySide.West => x==request.MinX && y>=request.GateStart && y<request.GateStart+request.GateWidth,
            _ => x==request.MaxX-1 && y>=request.GateStart && y<request.GateStart+request.GateWidth };
        var cells=new List<LinkedTile>();
        var polygon=request.Corners is null?null:BoundaryPolygon.Layout(map,request);
        if(polygon is not null)cells.AddRange(polygon.Walls);
        else for(int y=request.MinY;y<request.MaxY;y++)for(int x=request.MinX;x<request.MaxX;x++)
            if ((x==request.MinX || x==request.MaxX-1 || y==request.MinY || y==request.MaxY-1) && !Gate(x,y))
                cells.Add(new(x,y));
        if (cells.Count>256) throw new ArgumentException("This experiment supports at most 256 boundary pieces.");
        var exits=map.ExitTiles().Select(t=>new LinkedTile(t.X,t.Y)).ToHashSet();
        if(cells.Any(c=>exits.Contains(c)))throw new InvalidOperationException("Boundary overlaps an entrance or special marker.");
        if(cells.Any(c=>map.Floors.All(l=>new FloorCell(map.Cell(l,c.X,c.Y)).IsEmpty)))
            throw new InvalidOperationException("Boundary blocking requires an existing floor in every wall cell.");
        var scale=new Vector3d(unitsPerTile/dx,request.Height/dy,unitsPerTile/dz);
        var transforms=cells.Select((c,i)=> {
            double angle=polygon is not null?polygon.Angles[i]:c.Y==request.MinY?0:c.Y==request.MaxY-1?Math.PI:c.X==request.MinX?-Math.PI/2:Math.PI/2;
            double cx=(model.Min.X+model.Max.X)/2*scale.X,cz=(model.Min.Z+model.Max.Z)/2*scale.Z;
            return new EntityTransform(new((c.X+.5)*unitsPerTile-cx*Math.Cos(angle)-cz*Math.Sin(angle),
                -model.Min.Y*scale.Y,(c.Y+.5)*unitsPerTile+cx*Math.Sin(angle)-cz*Math.Cos(angle)),
                new(0,Math.Sin(angle/2),0,Math.Cos(angle/2)),scale);
        }).ToArray();
        return new(cells.ToArray(),transforms);
    }

    public static PresetEntity[] Apply(PresetDocument scene, Ds1CollisionDocument map, PlacementLinks links,
        BoundaryRequest request, string wallModel, BoundaryModelBounds bounds)
    {
        if(links.Warning is not null || links.HasBrokenLinks || links.Calibration.IsUnverified || links.Calibration.IsPoorFit)
            throw new InvalidOperationException("Resolve links and calibrate this pair before adding a boundary.");
        var plan=Plan(map,request,bounds,links.Calibration.UnitsPerTile);
        if(links.InspectOwnershipForTiles(plan.Cells).Values.Any(c=>c.Owners.Length>0))throw new InvalidOperationException("Boundary overlaps existing linked collision.");
        links.ConnectWorkspace();
        PresetEntity[] added=[];
        scene.History.Transaction(()=> {
            added=scene.PlaceModels(wallModel,plan.Transforms).Added;
            for(int i=0;i<added.Length;i++)links.LinkFootprint(added[i],[plan.Cells[i]],links.Calibration.UnitsPerTile,false);
        });
        return added;
    }
}
