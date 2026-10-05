using System.Buffers.Binary;

namespace D2RLevel.Core;

public sealed record CaveContourCell(int X, int Y, int Mask);
public sealed record CaveContourResult(byte[] Map, byte[] Tiles, int Style, CaveContourCell[] Cells);

/// <summary>Clone a cave floor's graphics and collision; only its automap identity changes.</summary>
public static class CaveContour
{
    public static CaveContourResult Create(Ds1CollisionDocument map, IEnumerable<Dt1CollisionTile> tiles,
        byte[] donorDt1, int donorX, int donorY, int minX, int minY, int maxX, int maxY, int style, bool skipBlocked = false)
    {
        if(style is < 0 or > 63)throw new ArgumentOutOfRangeException(nameof(style));
        var all=tiles.ToArray();
        if(all.Any(t=>t.Orientation==0&&t.Main==style))throw new InvalidDataException("Contour floor style already exists in the effective tileset.");
        var collision=new LegacyCollision(map,all);
        var donorLayers=map.Floors.Where(l=>!new FloorCell(map.Cell(l,donorX,donorY)).IsEmpty).ToArray();
        if(donorLayers.Length!=1||collision.At(donorX,donorY) is not {NoFloor:false,Unresolved:false,BlockedSubtiles:0,VariantDependent:false})
            throw new NotSupportedException("Contour requires a single resolved floor layer with identical walkable donor variants.");
        var donor=new FloorCell(map.Cell(donorLayers[0],donorX,donorY));
        bool Open(int x,int y)=>x>=0&&y>=0&&x<map.Width-1&&y<map.Height-1&&collision.At(x,y) is {NoFloor:false,Unresolved:false,BlockedSubtiles:0};
        var cells=new List<CaveContourCell>();var bytes=map.Serialize();
        for(int y=minY;y<maxY;y++)for(int x=minX;x<maxX;x++)
        {
            if(!Open(x,y))
            {
                if(skipBlocked)continue;
                throw new InvalidDataException("Contour region must be fully walkable.");
            }
            int mask=(!Open(x-1,y)?1:0)|(!Open(x,y-1)?2:0)|(!Open(x+1,y)?4:0)|(!Open(x,y+1)?8:0);
            if(mask==0)continue;
            if(mask is not (1 or 2 or 4 or 8 or 3 or 6 or 9 or 12))
                throw new NotSupportedException("Contour supports broad rectangular passages; one-tile corridors and isolated tiles require different automap art.");
            var layers=map.Floors.Where(l=>!new FloorCell(map.Cell(l,x,y)).IsEmpty).ToArray();
            if(layers.Length!=1)throw new NotSupportedException("Contour requires a single floor layer.");
            uint before=map.Cell(layers[0],x,y);var key=new FloorCell(before);
            if(key.Main!=donor.Main||key.Sub!=donor.Sub)throw new NotSupportedException("Contour region must use the source floor identity.");
            uint after=(before&~0x03F0FF00u)|((uint)style<<20)|((uint)mask<<8);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(layers[0].Offset+(y*map.Width+x)*4),after);
            cells.Add(new(x,y,mask));
        }
        if(cells.Count==0)throw new InvalidDataException("No contour perimeter was generated.");
        int Read(int at){if(at<0||at>donorDt1.Length-4)throw new InvalidDataException("Truncated contour donor.");return BinaryPrimitives.ReadInt32LittleEndian(donorDt1.AsSpan(at));}
        if(Read(0)!=7||Read(4)!=6||Read(268)<1||Read(272)<276||(long)Read(272)+Read(268)*96L>donorDt1.Length)
            throw new InvalidDataException("Unsupported contour donor DT1.");
        var donors=Enumerable.Range(0,Read(268)).Select(i=>Read(272)+i*96)
            .Where(at=>Read(at+20)==0&&Read(at+24)==donor.Main&&Read(at+28)==donor.Sub).ToArray();
        if(donors.Length==0)throw new InvalidDataException("Contour donor graphics are missing.");
        var masks=cells.Select(c=>c.Mask).Distinct().Order().ToArray();
        using var output=new MemoryStream();using var writer=new BinaryWriter(output);
        byte[] header=donorDt1[..276];int count=checked(masks.Length*donors.Length),next=checked(276+count*96);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(268),count);BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(272),276);writer.Write(header);
        var payloads=new List<byte[]>();
        foreach(int mask in masks)foreach(int at in donors)
        {
            int start=Read(at+72),size=Read(at+76);
            if(start<276||size<0||start>donorDt1.Length-size)throw new InvalidDataException("Invalid contour graphics span.");
            byte[] tile=donorDt1[at..(at+96)];
            BinaryPrimitives.WriteInt32LittleEndian(tile.AsSpan(24),style);BinaryPrimitives.WriteInt32LittleEndian(tile.AsSpan(28),mask);
            BinaryPrimitives.WriteInt32LittleEndian(tile.AsSpan(72),next);writer.Write(tile);next=checked(next+size);payloads.Add(donorDt1[start..(start+size)]);
        }
        foreach(var payload in payloads)writer.Write(payload);
        return new(bytes,output.ToArray(),style,cells.ToArray());
    }
}
