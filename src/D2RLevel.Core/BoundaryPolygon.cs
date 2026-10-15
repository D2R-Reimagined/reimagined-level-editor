namespace D2RLevel.Core;

public sealed record BoundaryPolygonLayout(LinkedTile[] Walls, double[] Angles, LinkedTile[] Gate,
    LinkedTile[] Interior, LinkedTile[] Approaches);

/// <summary>A simple closed outline through tile centres. Edges are horizontal or vertical.</summary>
public static class BoundaryPolygon
{
    public static BoundaryRequest Region(BoundaryRequest request)
    {
        if(request.Corners is not {Length:>0} corners)return request;
        return request with {MinX=corners.Min(p=>p.X),MinY=corners.Min(p=>p.Y),
            MaxX=checked(corners.Max(p=>p.X)+1),MaxY=checked(corners.Max(p=>p.Y)+1)};
    }

    public static BoundaryPolygonLayout Layout(Ds1CollisionDocument map,BoundaryRequest request)
    {
        var corners=request.Corners ?? throw new ArgumentException("Choose outline corners.");
        if(corners.Length is <4 or >64 || corners.Any(p=>p is null||p.X<0||p.Y<0||p.X>=map.Width-1||p.Y>=map.Height-1))
            throw new ArgumentException("Choose 4 to 64 corners inside the final map border.");
        if(request.GateEdge<0||request.GateEdge>=corners.Length||request.GateWidth<3)
            throw new ArgumentException("Choose a gate segment and a width of at least three tiles.");
        var full=new List<(LinkedTile Cell,double Angle,bool Gate)>();
        long area=0;
        for(int i=0;i<corners.Length;i++){
            var a=corners[i];var b=corners[(i+1)%corners.Length];
            if((a.X==b.X)==(a.Y==b.Y))throw new ArgumentException("Each segment must be horizontal or vertical, with different corners.");
            area+=(long)a.X*b.Y-(long)b.X*a.Y;
        }
        if(area==0)throw new ArgumentException("The outline must enclose an area.");
        var seen=new HashSet<LinkedTile>();
        var gate=new List<LinkedTile>();var approaches=new List<LinkedTile>();
        for(int i=0;i<corners.Length;i++){
            var a=corners[i];var b=corners[(i+1)%corners.Length];
            int dx=Math.Sign(b.X-a.X),dy=Math.Sign(b.Y-a.Y),length=Math.Abs(b.X-a.X)+Math.Abs(b.Y-a.Y);
            if(length<2)throw new ArgumentException("Keep at least two tiles between corners.");
            if(i==request.GateEdge&&(request.GateStart<1||(long)request.GateStart+request.GateWidth>=length))
                throw new ArgumentException("Keep the gate away from both segment corners.");
            if(full.Count+(long)length>512)throw new ArgumentException("The outline is too long (maximum 512 perimeter tiles).");
            int nx=dy*Math.Sign(area),ny=-dx*Math.Sign(area);
            double angle=ny<0?0:ny>0?Math.PI:nx<0?-Math.PI/2:Math.PI/2;
            for(int step=0;step<length;step++){
                var cell=new LinkedTile(a.X+dx*step,a.Y+dy*step);
                if(!seen.Add(cell))throw new ArgumentException("The outline crosses or touches itself. Choose a simple closed outline.");
                bool opening=i==request.GateEdge&&step>=request.GateStart&&step<request.GateStart+request.GateWidth;
                full.Add((cell,angle,opening));
                if(opening){gate.Add(cell);approaches.Add(new(cell.X+nx,cell.Y+ny));approaches.Add(new(cell.X-nx,cell.Y-ny));}
            }
        }
        // Integer ray casting is sufficient: boundary cells were explicitly excluded above.
        bool Inside(int x,int y){
            bool inside=false;
            for(int i=0,j=corners.Length-1;i<corners.Length;j=i++){
                var a=corners[i];var b=corners[j];
                if((a.Y>y)!=(b.Y>y)&&x<a.X)inside=!inside;
            }
            return inside;
        }
        var region=Region(request);var interior=new HashSet<LinkedTile>();
        for(int y=region.MinY;y<region.MaxY;y++)for(int x=region.MinX;x<region.MaxX;x++)
            if(!seen.Contains(new(x,y))&&Inside(x,y))interior.Add(new(x,y));
        if(interior.Count==0)throw new ArgumentException("The outline needs open interior floor.");
        var reached=new HashSet<LinkedTile>();var pending=new Queue<LinkedTile>();pending.Enqueue(interior.First());
        while(pending.TryDequeue(out var p)){
            if(!interior.Contains(p)||!reached.Add(p))continue;
            pending.Enqueue(new(p.X-1,p.Y));pending.Enqueue(new(p.X+1,p.Y));pending.Enqueue(new(p.X,p.Y-1));pending.Enqueue(new(p.X,p.Y+1));
        }
        if(reached.Count!=interior.Count)throw new ArgumentException("The outline pinches off its interior. Widen the connecting passage.");
        for(int i=0;i<approaches.Count;i+=2)
            if(interior.Contains(approaches[i])||!interior.Contains(approaches[i+1])||seen.Contains(approaches[i]))
                throw new ArgumentException("The gate must connect interior and exterior floor.");
        var walls=full.Where(p=>!p.Gate).ToArray();
        if(walls.Length>256)throw new ArgumentException("This experiment supports at most 256 boundary pieces.");
        return new(walls.Select(p=>p.Cell).ToArray(),walls.Select(p=>p.Angle).ToArray(),gate.ToArray(),interior.ToArray(),approaches.ToArray());
    }

    public static void ValidateFloor(LegacyCollision collision,BoundaryPolygonLayout layout)
    {
        foreach(var p in layout.Walls.Concat(layout.Interior).Concat(layout.Gate).Concat(layout.Approaches))
            if(p.X<0||p.Y<0||p.X>=collision.Document.Width-1||p.Y>=collision.Document.Height-1||
                collision.At(p.X,p.Y) is not {NoFloor:false,Unresolved:false,VariantDependent:false,BlockedSubtiles:0})
                throw new InvalidOperationException($"Prepare clear, resolved floor at {p.X},{p.Y}, including both gate approaches.");
    }
}
