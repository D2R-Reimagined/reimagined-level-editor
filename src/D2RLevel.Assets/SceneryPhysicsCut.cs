using System.Numerics;
using System.Text.Json.Nodes;

namespace D2RLevel.Assets;

internal static class SceneryPhysicsCut
{
    public static void Apply(JsonNode entity, Matrix4x4 world, TerrainRectangle area, Func<string, byte[]> readPhysics)
    {
        static Vector3 Vector(JsonNode n) => new((float)n["x"]!, (float)n["y"]!, (float)n["z"]!);
        static JsonObject Json(Vector3 p) => new(){["x"]=p.X,["y"]=p.Y,["z"]=p.Z};
        foreach(var body in entity["components"]!.AsArray().Where(c=>(string?)c?["type"]=="PhysicsBodyDefinitionComponent"))
        {
            var fixtures=body!["fixturedefs"]!.AsArray();var output=new JsonArray();
            foreach(var fixture in fixtures)
            {
                var shape=fixture!["shapetype"]!;
                if ((string?)shape["type"] == "PhysicsFileDefinition")
                {
                    var points = PhysicsMeshBounds.Read(readPhysics((string)shape["filename"]!)).WorldVertices(world);
                    if (points.Max(p => p.X) <= area.MinX || points.Min(p => p.X) >= area.MaxX ||
                        points.Max(p => p.Z) <= area.MinZ || points.Min(p => p.Z) >= area.MaxZ)
                    { output.Add(fixture.DeepClone()); continue; }
                    if (points.Min(p => p.X) >= area.MinX && points.Max(p => p.X) <= area.MaxX &&
                        points.Min(p => p.Z) >= area.MinZ && points.Max(p => p.Z) <= area.MaxZ) continue;
                    throw new NotSupportedException(FormattableString.Invariant($"The rectangle partially intersects a physics mesh (HD X {points.Min(p=>p.X):F3}..{points.Max(p=>p.X):F3}, Z {points.Min(p=>p.Z):F3}..{points.Max(p=>p.Z):F3}). Include the whole fixture or choose a different rectangle."));
                }
                if((string?)shape["type"]!="PhysicsBoxDefinition")throw new NotSupportedException("Unsupported scenery physics shape.");
                var center=Vector(shape["center"]!);var extent=Vector(shape["extents"]!);var q=shape["orientation"]!;
                var orientation=new Quaternion((float)q["x"]!,(float)q["y"]!,(float)q["z"]!,(float)q["w"]!);
                var rotation=Matrix4x4.CreateFromQuaternion(orientation);
                var frame=rotation*Matrix4x4.CreateTranslation(center)*world;
                var bounds = (from x in new[] { -extent.X, extent.X }
                              from y in new[] { -extent.Y, extent.Y }
                              from z in new[] { -extent.Z, extent.Z }
                              select Vector3.Transform(new(x, y, z), frame)).ToArray();
                if (bounds.Max(p => p.X) <= area.MinX || bounds.Min(p => p.X) >= area.MaxX ||
                    bounds.Max(p => p.Z) <= area.MinZ || bounds.Min(p => p.Z) >= area.MaxZ)
                { output.Add(fixture.DeepClone()); continue; }
                if (bounds.Min(p => p.X) >= area.MinX && bounds.Max(p => p.X) <= area.MaxX &&
                    bounds.Min(p => p.Z) >= area.MinZ && bounds.Max(p => p.Z) <= area.MaxZ) continue;
                // An axis-aligned cut in the fixture frame can represent the exact
                // remaining boxes only for level, cardinally oriented fixtures.
                if(!Matrix4x4.Invert(frame,out var inverse)||Math.Abs(frame.M12)+Math.Abs(frame.M21)+Math.Abs(frame.M23)+Math.Abs(frame.M32)>1e-4f ||
                    (Math.Abs(frame.M11)>1e-4f&&Math.Abs(frame.M13)>1e-4f))
                    throw new NotSupportedException(FormattableString.Invariant($"Scenery cut requires level, cardinal box physics (HD X {bounds.Min(p=>p.X):F3}..{bounds.Max(p=>p.X):F3}, Z {bounds.Min(p=>p.Z):F3}..{bounds.Max(p=>p.Z):F3}). Include the whole fixture or choose a different rectangle."));
                var corners=new[]{new Vector3(area.MinX,area.Height,area.MinZ),new Vector3(area.MinX,area.Height,area.MaxZ),new Vector3(area.MaxX,area.Height,area.MinZ),new Vector3(area.MaxX,area.Height,area.MaxZ)}.Select(p=>Vector3.Transform(p,inverse)).ToArray();
                float x0=Math.Max(-extent.X,corners.Min(p=>p.X)),x1=Math.Min(extent.X,corners.Max(p=>p.X));
                float z0=Math.Max(-extent.Z,corners.Min(p=>p.Z)),z1=Math.Min(extent.Z,corners.Max(p=>p.Z));
                if(x0>=x1||z0>=z1){output.Add(fixture.DeepClone());continue;}
                int piece=0;
                void Add(float minX,float minZ,float maxX,float maxZ)
                {
                    if(maxX-minX<1e-5f||maxZ-minZ<1e-5f)return;
                    var copy=fixture.DeepClone();var s=copy["shapetype"]!;
                    s["center"]=Json(center+Vector3.TransformNormal(new((minX+maxX)/2,0,(minZ+maxZ)/2),rotation));
                    s["extents"]=Json(new((maxX-minX)/2,extent.Y,(maxZ-minZ)/2));
                    copy["name"]=(string?)fixture["name"]+"_outside_"+piece++;
                    output.Add(copy);
                }
                Add(-extent.X,-extent.Z,x0,extent.Z);Add(x1,-extent.Z,extent.X,extent.Z);
                Add(x0,-extent.Z,x1,z0);Add(x0,z1,x1,extent.Z);
            }
            body["fixturedefs"]=output;
        }
    }
}
