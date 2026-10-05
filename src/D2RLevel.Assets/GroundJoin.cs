using D2RLevel.Core;

namespace D2RLevel.Assets;

internal static class GroundJoin
{
    public static void RequireWalkableConnection(LegacyCollision original, ModelAsset terrain, GroundExtensionRequest region)
    {
        // A complete rectangular floor is connected internally. Require an existing
        // open subtile immediately outside one edge and continuous ground at its join.
        bool Joins(int sx,int sy,float edgeX,float edgeZ)
        {
            if(sx<0||sy<0||sx>=original.Document.Width*5||sy>=original.Document.Height*5)return false;
            var cell=original.At(sx/5,sy/5);
            if(cell.NoFloor||cell.Unresolved||(cell.Flags[(sy%5)*5+sx%5]&9)!=0)return false;
            return Matches(sx*2+1,sy*2+1)&&Matches(edgeX,edgeZ);
        }
        bool Matches(float x,float z)
        {
            foreach(var part in terrain.Parts)for(int i=0;i<part.Indices.Length;i+=3)
            {
                var p=part.Positions;int a=part.Indices[i]*3,b=part.Indices[i+1]*3,c=part.Indices[i+2]*3;
                double abx=p[b]-p[a],abz=p[b+2]-p[a+2],acx=p[c]-p[a],acz=p[c+2]-p[a+2];
                double determinant=abx*acz-abz*acx;if(Math.Abs(determinant)<.00001)continue;
                double u=((x-p[a])*acz-(z-p[a+2])*acx)/determinant,v=(abx*(z-p[a+2])-abz*(x-p[a]))/determinant;
                if(u>=-.00001&&v>=-.00001&&u+v<=1.00001&&Math.Abs(p[a+1]+u*(p[b+1]-p[a+1])+v*(p[c+1]-p[a+1])-region.Height)<.02)return true;
            }
            return false;
        }
        for(int sy=region.MinY*5;sy<region.MaxY*5;sy++)
            if(Joins(region.MinX*5-1,sy,region.MinX*10,sy*2+1)||Joins(region.MaxX*5,sy,region.MaxX*10,sy*2+1))return;
        for(int sx=region.MinX*5;sx<region.MaxX*5;sx++)
            if(Joins(sx,region.MinY*5-1,sx*2+1,region.MinY*10)||Joins(sx,region.MaxY*5,sx*2+1,region.MaxY*10))return;
        throw new NotSupportedException("The extension has no walkable, level connection to the existing room.");
    }
}
