using System.Buffers.Binary;

namespace D2RLevel.Core;

public sealed record BoundaryContourIdentity(int SourceMain, int SourceSub, int Style, int[] Masks);
public sealed record BoundaryContourResult(byte[] Map, byte[] Tiles, BoundaryContourIdentity[] Identities, CaveContourCell[] Cells);

/// <summary>Outline the actual open floor near a boundary, preserving each floor's variants and graphics.</summary>
public static class BoundaryContour
{
    public static BoundaryContourResult Create(Ds1CollisionDocument map, IReadOnlyDictionary<string, byte[]> sources,
        BoundaryRequest region)
    {
        region=BoundaryPolygon.Region(region);
        var donors = new Dictionary<(int Main, int Sub), List<(byte[] Header, byte[] Payload)>>();
        var occupied = new HashSet<int>();
        foreach (var data in sources.Values)
        {
            int Read(int at) => at >= 0 && at <= data.Length-4
                ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at)) : throw new InvalidDataException("Truncated DT1.");
            int count=Read(268), table=Read(272);
            if(Read(0)!=7 || Read(4)!=6 || count<0 || count>100000 || table<276 || table+96L*count>data.Length)
                throw new InvalidDataException("Unsupported DT1 tile table.");
            for(int i=0;i<count;i++)
            {
                int at=table+i*96;
                if(Read(at+20)!=0)continue;
                int main=Read(at+24),sub=Read(at+28),start=Read(at+72),size=Read(at+76);
                occupied.Add(main);
                if(start<table+count*96L || size<0 || start>data.Length-size)throw new InvalidDataException("Invalid floor graphics span.");
                if(!donors.TryGetValue((main,sub),out var list))donors[(main,sub)]=list=[];
                list.Add((data[at..(at+96)],data[start..(start+size)]));
            }
        }
        var tiles=donors.SelectMany(pair=>pair.Value.Select(d=>{
            var flags=new byte[25];
            for(int y=0;y<5;y++)for(int x=0;x<5;x++)flags[y*5+x]=d.Header[40+(4-y)*5+x];
            return new Dt1CollisionTile("boundary donor",0,pair.Key.Main,pair.Key.Sub,flags);
        })).ToList();
        // Wall records are required for the effective collision, even though only floors are cloned.
        foreach(var data in sources.Values)
        {
            int count=BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(268)),table=BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(272));
            for(int i=0;i<count;i++){
                int at=table+i*96,orientation=BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at+20));
                if(orientation==0)continue;
                var flags=new byte[25];for(int y=0;y<5;y++)for(int x=0;x<5;x++)flags[y*5+x]=data[at+40+(4-y)*5+x];
                tiles.Add(new("boundary donor",orientation,BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at+24)),BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at+28)),flags));
            }
        }
        var collision=new LegacyCollision(map,tiles);
        foreach(var layer in map.Floors)for(int y=0;y<map.Height;y++)for(int x=0;x<map.Width;x++){
            var cell=new FloorCell(map.Cell(layer,x,y));if(!cell.IsEmpty)occupied.Add(cell.Main);
        }
        bool Open(int x,int y)=>x>=0&&y>=0&&x<map.Width-1&&y<map.Height-1&&
            collision.At(x,y) is {NoFloor:false,Unresolved:false,VariantDependent:false,BlockedSubtiles:0};
        var assignments=new List<(int X,int Y,int Offset,int Main,int Sub,int Mask)>();
        for(int y=Math.Max(0,region.MinY-1);y<Math.Min(map.Height-1,region.MaxY+1);y++)
        for(int x=Math.Max(0,region.MinX-1);x<Math.Min(map.Width-1,region.MaxX+1);x++)
        {
            if(!Open(x,y))continue;
            var layers=map.Floors.Where(l=>!new FloorCell(map.Cell(l,x,y)).IsEmpty).ToArray();
            if(layers.Length!=1)throw new NotSupportedException($"Boundary outlines require one floor layer at {x},{y}.");
            int mask=(!Open(x-1,y)?1:0)|(!Open(x,y-1)?2:0)|(!Open(x+1,y)?4:0)|(!Open(x,y+1)?8:0);
            if(mask is not (0 or 1 or 2 or 4 or 8 or 3 or 6 or 9 or 12))
                throw new NotSupportedException($"Outline at {x},{y} needs narrow-passage automap art (mask {mask}). Widen the passage.");
            var key=new FloorCell(map.Cell(layers[0],x,y));
            assignments.Add((x,y,layers[0].Offset+(y*map.Width+x)*4,key.Main,key.Sub,mask));
        }
        if(assignments.Count==0)throw new InvalidDataException("No resolved open floor surrounds this boundary.");
        // Include mask zero to clear obsolete outlines when an old contour identity becomes interior floor.
        var keys=assignments.Select(a=>(a.Main,a.Sub)).Distinct().Order().ToArray();
        var free=Enumerable.Range(0,64).Reverse().Where(s=>!occupied.Contains(s)).ToArray();
        if(free.Length<keys.Length)throw new NotSupportedException("Not enough unused floor styles for this boundary's distinct floor types.");
        var identities=keys.Select((key,i)=>new BoundaryContourIdentity(key.Main,key.Sub,free[i],
            assignments.Where(a=>a.Main==key.Main&&a.Sub==key.Sub).Select(a=>a.Mask).Distinct().Order().ToArray())).ToArray();
        var outputTiles=new List<(byte[] Header,byte[] Payload)>();
        foreach(var identity in identities)foreach(int mask in identity.Masks)foreach(var donor in donors[(identity.SourceMain,identity.SourceSub)])
        {
            var header=donor.Header.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24),identity.Style);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28),mask);
            outputTiles.Add((header,donor.Payload));
        }
        var bytes=map.Serialize();
        foreach(var a in assignments){
            uint before=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(a.Offset));
            int style=identities.Single(k=>k.SourceMain==a.Main&&k.SourceSub==a.Sub).Style;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(a.Offset),(before&~0x03F0FF00u)|((uint)style<<20)|((uint)a.Mask<<8));
        }
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        if(outputTiles.Count>100000 || 276L+outputTiles.Count*96L+outputTiles.Sum(t=>(long)t.Payload.Length)>256L*1024*1024)
            throw new NotSupportedException("Generated outline tiles exceed the 256 MiB export limit.");
        var dtHeader=new byte[276];BinaryPrimitives.WriteInt32LittleEndian(dtHeader,7);BinaryPrimitives.WriteInt32LittleEndian(dtHeader.AsSpan(4),6);
        BinaryPrimitives.WriteInt32LittleEndian(dtHeader.AsSpan(268),outputTiles.Count);BinaryPrimitives.WriteInt32LittleEndian(dtHeader.AsSpan(272),276);writer.Write(dtHeader);
        int next=checked(276+outputTiles.Count*96);
        foreach(var tile in outputTiles){BinaryPrimitives.WriteInt32LittleEndian(tile.Header.AsSpan(72),next);writer.Write(tile.Header);next=checked(next+tile.Payload.Length);}
        foreach(var tile in outputTiles)writer.Write(tile.Payload);
        return new(bytes,stream.ToArray(),identities,assignments.Select(a=>new CaveContourCell(a.X,a.Y,a.Mask)).ToArray());
    }
}
