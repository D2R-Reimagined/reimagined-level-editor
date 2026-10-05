using System.Buffers.Binary;
using LSLib.Granny.GR2;
using Field = D2RLevel.Assets.GrannySectionDocument.Field;

namespace D2RLevel.Assets;

internal static class GrannyVertexInterpolation
{
    public static byte[] Attributes(byte[] first, byte[] second, float t, IEnumerable<Field> fields)
    {
        if (!float.IsFinite(t) || t < 0 || t > 1) throw new InvalidDataException("Invalid interpolation fraction.");
        var bytes = first.ToArray();
                foreach(var f in fields)for(int k=0;k<f.Count;k++)
                {
                    if(f.Kind==MemberType.BinormalInt8){float value=(sbyte)first[f.Offset+k]*(1-t)+(sbyte)second[f.Offset+k]*t;bytes[f.Offset+k]=unchecked((byte)(sbyte)Math.Clamp(MathF.Round(value),-127,127));}
                    else
                    {
                        int offset=f.Offset+k*(f.Kind==MemberType.Real16?2:4);
                        float Read(byte[] data)=>f.Kind==MemberType.Real16?(float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset))):BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset));
                        float av=Read(first),value=av+(Read(second)-av)*t;
                        if(!float.IsFinite(value)||(f.Kind==MemberType.Real16&&!Half.IsFinite((Half)value)))throw new InvalidDataException("Nonfinite interpolated vertex attribute.");
                        if(f.Kind==MemberType.Real16)BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset),BitConverter.HalfToUInt16Bits((Half)value));else BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset),value);
                    }
                }
        return bytes;
    }
}
