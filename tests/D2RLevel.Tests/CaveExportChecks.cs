using System.Text.Json;
using System.Text.Json.Nodes;
using D2RLevel.Assets;
using D2RLevel.Core;
using LSLib.Granny.GR2;
using LSLib.Granny.Model;

internal sealed class CaveAuditModelRoot
{
    [Serialization(Type=MemberType.ArrayOfReferences)]
    public List<CaveAuditMesh>? Meshes=null;
}
internal sealed class CaveAuditMesh
{
    public VertexData? PrimaryVertexData=null;
    public List<BoneBinding>? BoneBindings=null;
    [Serialization(Type=MemberType.VariantReference)]
    public CaveAuditMeshData? ExtendedData=null;
}
internal sealed class CaveAuditMeshData
{
    public float VertexScale=1;
    [Serialization(ArraySize=3)] public float[] bbMin=[];
    [Serialization(ArraySize=3)] public float[] bbMax=[];
}

internal static class CaveExportChecks
{
    public static void Audit(string output,string assetsRoot,string decoder,string report)
    {
        ModelReader.ConfigureDecoder(Path.GetFullPath(decoder));
        var receipt=JsonSerializer.Deserialize<GroundExtensionReceipt>(File.ReadAllBytes(Path.Combine(output,"ground-extension.json")))!;
        string data=Path.Combine(output,"data");
        var resolver=new AssetResolver(assetsRoot);
        var map=Ds1CollisionDocument.Load(Path.Combine(data,receipt.Map));
        string preset=Path.Combine(data,receipt.Preset);
        var project=LevelProject.ForPreset(preset)!;project.VerifyMap(map);
        var local=new AssetResolver(data);
        var tiles=project.Tileset.Files.SelectMany(p=>LegacyCollision.ReadTiles(File.Exists(local.Resolve(p))?local.Resolve(p):resolver.Resolve(p))).ToArray();
        var collision=new LegacyCollision(map,tiles);
        string terrainPath=Path.Combine(data,receipt.Terrain);
        var terrain=ModelReader.Load(terrainPath);
        var region=receipt.Region;int checks=0,samples=0;
        void Check(bool value,string description){if(!value)throw new InvalidDataException(description);checks++;}
        var scene=JsonNode.Parse(File.ReadAllBytes(preset))!;
        var terrainEntity=scene["terrain"]!;
        Check(JsonNode.DeepEquals(terrainEntity,scene["entities"]!.AsArray().Single(e=>JsonNode.DeepEquals(e?["id"],terrainEntity["id"]))),"Terrain and entity physics representations agree");
        var components=terrainEntity["components"]!.AsArray();
        var transform=components.Single(c=>(string?)c?["type"]=="TransformDefinitionComponent")!;
        double Number(JsonNode node,string key)=>node[key]!.GetValue<double>();
        bool Vector(JsonNode node,double x,double y,double z)=>Number(node,"x")==x&&Number(node,"y")==y&&Number(node,"z")==z;
        bool IdentityRotation(JsonNode node)=>Vector(node,0,0,0)&&Number(node,"w")==1;
        Check(Vector(transform["position"]!,0,0,0)&&Vector(transform["scale"]!,1,1,1)&&IdentityRotation(transform["orientation"]!),"Audited cave terrain uses identity transform");
        var body=components.Single(c=>(string?)c?["type"]=="PhysicsBodyDefinitionComponent")!;
        Check((string?)body["bodytype"]=="static"&&(string?)body["filter"]=="floor","Exported support is static floor physics");
        var shape=body["fixturedefs"]!.AsArray().Single(f=>(string?)f?["name"]=="ground_extension_fixture")!["shapetype"]!;
        Check((string?)shape["type"]=="PhysicsBoxDefinition"&&IdentityRotation(shape["orientation"]!),"Exported support is an axis-aligned physics box");
        var center=shape["center"]!;var extents=shape["extents"]!;
        Check(new[]{"x","y","z"}.All(a=>double.IsFinite(Number(center,a))&&double.IsFinite(Number(extents,a))&&Number(extents,a)>0),"Physics support has finite positive extents");
        Check(Math.Abs(Number(center,"y")+Number(extents,"y")-region.Height)<1e-5,"Physics support top matches authored floor height");
        bool PhysicsSupported(double x,double z)=>Math.Abs(x-Number(center,"x"))<=Number(extents,"x")&&Math.Abs(z-Number(center,"z"))<=Number(extents,"z");
        using(var stream=File.OpenRead(terrainPath))
        using(var reader=new GR2Reader(stream))
        {
            var raw=new CaveAuditModelRoot();reader.Read(raw);
            var mesh=raw.Meshes?.Single()??throw new InvalidDataException("Expected one audited terrain mesh.");
            var vertices=mesh.PrimaryVertexData?.Vertices??throw new InvalidDataException("Audited terrain has no vertices.");
            var bounds=mesh.ExtendedData??throw new InvalidDataException("Audited terrain has no mesh bounds.");
            var bone=mesh.BoneBindings?.Single()??throw new InvalidDataException("Audited terrain has no rigid bone bounds.");
            Check(bounds.bbMin.Length==3&&bounds.bbMax.Length==3&&bone.OBBMin.Length==3&&bone.OBBMax.Length==3,"Terrain bounds have three axes");
            foreach(var vertex in vertices)
            {
                float[] position=[vertex.Position.X*bounds.VertexScale,vertex.Position.Y*bounds.VertexScale,vertex.Position.Z*bounds.VertexScale];
                for(int axis=0;axis<3;axis++)
                {
                    Check(position[axis]>=bounds.bbMin[axis]&&position[axis]<=bounds.bbMax[axis],$"Terrain vertex exceeds mesh bounds on axis {axis}");
                    Check(position[axis]>=bone.OBBMin[axis]&&position[axis]<=bone.OBBMax[axis],$"Terrain vertex exceeds bone bounds on axis {axis}");
                }
            }
        }
        bool Supported(float x,float z)
        {
            foreach(var part in terrain.Parts)for(int i=0;i<part.Indices.Length;i+=3)
            {
                var p=part.Positions;int a=part.Indices[i]*3,b=part.Indices[i+1]*3,c=part.Indices[i+2]*3;
                double abx=p[b]-p[a],abz=p[b+2]-p[a+2],acx=p[c]-p[a],acz=p[c+2]-p[a+2],det=abx*acz-abz*acx;
                if(Math.Abs(det)<1e-6)continue;
                double u=((x-p[a])*acz-(z-p[a+2])*acx)/det,v=(abx*(z-p[a+2])-abz*(x-p[a]))/det;
                if(u>=-1e-5&&v>=-1e-5&&u+v<=1.00001&&Math.Abs(p[a+1]+u*(p[b+1]-p[a+1])+v*(p[c+1]-p[a+1])-region.Height)<.02)return true;
            }
            return false;
        }
        for(int y=region.MinY;y<region.MaxY;y++)for(int x=region.MinX;x<region.MaxX;x++)
        {
            Check(collision.At(x,y) is {NoFloor:false,Unresolved:false,BlockedSubtiles:0},$"Blocked floor at {x},{y}");
            for(int sy=0;sy<5;sy++)for(int sx=0;sx<5;sx++)
            {
                Check(Supported(x*10+sx*2+1,y*10+sy*2+1),$"Missing HD support at {x},{y} subtile {sx},{sy}");
                Check(PhysicsSupported(x*10+sx*2+1,y*10+sy*2+1),$"Missing physics support at {x},{y} subtile {sx},{sy}");samples++;
            }
        }
        Check(terrain.Parts.All(p=>p.Indices.Length>0),"Empty terrain topology");
        Check(map.Width==receipt.ExportedGrid[0]&&map.Height==receipt.ExportedGrid[1],"Wrong paired dimensions");
        Check(new PlacementLinks(PresetDocument.Load(preset),map).Warning is null,"Pair sidecar does not reopen");
        var result=new{checks,groundSamples=samples,physicsSamples=samples,physics="Static exported floor box; native engine not run",logicalGrid=new[]{map.Width-1,map.Height-1},receipt.SourceUnresolvedCells,receipt.ExportedUnresolvedCells,
            project.Tileset.Files,exitMarkers=map.ExitTiles().Where(t=>t.IsExit).ToArray(),gameplay="not run"};
        File.WriteAllText(report,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(File.ReadAllText(report));
    }
}
