using System.Buffers.Binary;
using D2RLevel.Core;

internal static class CaveGrowthChecks
{
    public static void Run(string folder, Action<bool,string> check, Action<Action,string> throws)
    {
        var original=Ds1CollisionDocument.Create(Path.Combine(folder,"growth.ds1"),25,25,1,Ds1CollisionDocument.FloorKey(5,0),2,2);
        var bytes=original.Serialize();
        // Include shadow/orientation bytes and an opaque suffix to catch row-stride or tail loss.
        int layerStart=original.Walls[0].Offset,tail=layerStart+25*25*4*7;
        for(int at=layerStart;at<tail;at+=4) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at),unchecked((uint)at));
        bytes=bytes.Concat(new byte[]{0xAA,0xBB,0xCC}).ToArray();
        File.WriteAllBytes(original.SourcePath,bytes);original=Ds1CollisionDocument.Load(original.SourcePath);
        var grown=original.GrowCopy(41,25);var result=grown.Serialize();
        bool retained=true,zero=true;
        for(int l=0;l<7;l++)for(int y=0;y<25;y++)for(int x=0;x<41;x++)
        {
            uint after=BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(layerStart+(l*41*25+y*41+x)*4));
            if(x<25)retained&=after==BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(layerStart+(l*25*25+y*25+x)*4));
            else zero&=after==0;
        }
        check(retained,"growth preserves every original wall, orientation, floor and shadow cell");
        check(zero,"growth initializes only new cells to empty");
        check(result.AsSpan(layerStart+41*25*4*7).SequenceEqual(bytes.AsSpan(tail)),"growth preserves gameplay and opaque suffix verbatim");
        check(original.Serialize().SequenceEqual(bytes),"growth does not mutate source");
        check(grown.Width==41&&grown.Height==25,"24 to 40 logical growth retains stored border");
        throws(()=>original.GrowCopy(24,25),"growth refuses shrink");
        throws(()=>original.GrowCopy(513,25),"growth refuses unsupported dimensions");
        grown.SaveCopy(Path.Combine(folder,"grown.ds1"));
        check(Ds1CollisionDocument.Load(Path.Combine(folder,"grown.ds1")).Serialize().SequenceEqual(result),"grown map save and reopen are lossless");
    }
}
