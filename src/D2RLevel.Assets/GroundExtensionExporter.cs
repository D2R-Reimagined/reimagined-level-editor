using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using D2RLevel.Core;

namespace D2RLevel.Assets;

public sealed record GroundExtensionRequest(int MinX, int MinY, int MaxX, int MaxY, float Height, int DonorX, int DonorY)
{
    /// <summary>Stored grid dimensions, including the final border. Omit to retain the source grid.</summary>
    public GroundMapGrowth? Growth { get; init; }
    /// <summary>Optional unused cave floor style. Table registration is prepared separately.</summary>
    public int? ContourStyle { get; init; }
}
public sealed record GroundMapGrowth(int Width, int Height);
public sealed record GroundExtensionReceipt(string Preset, string Map, string Terrain, GroundExtensionRequest Region,
    int NewlyOpenSubtiles, string[] RemovedScenery, TerrainSurfaceCounts Geometry, string Gameplay)
{
    public string[] ClippedScenery { get; init; } = [];
    public float SceneryCutPadding { get; init; }
    public int[] OriginalGrid { get; init; } = [];
    public int[] ExportedGrid { get; init; } = [];
    public Dictionary<string,int[]> RetainedSceneryMeshes { get; init; } = [];
    public int SourceUnresolvedCells { get; init; }
    public int ExportedUnresolvedCells { get; init; }
    public Dictionary<string,string> InputHashes { get; init; } = [];
}
public sealed record TerrainSurfaceCounts(int BeforeVertices, int AfterVertices, int BeforeTriangles, int AfterTriangles);

/// <summary>A separate, reviewable candidate containing the terrain, gameplay map and companion scene.</summary>
public static class GroundExtensionExporter
{
    public const float UnitsPerTile = 10;
    public static string Export(string presetPath, string mapPath, AssetResolver assets, LevelTileset tileset,
        GroundExtensionRequest request, string destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        destination=Path.GetFullPath(destination);
        if(Directory.Exists(destination)||File.Exists(destination))throw new IOException("Choose a new output folder.");
        var location=PresetPairing.Split(presetPath,"hd/env/preset") ?? throw new InvalidDataException("Preset must have a game-relative path.");
        var mapLocation=PresetPairing.Split(mapPath,"global/tiles") ?? throw new InvalidDataException("DS1 must have a game-relative path.");
        PresetPairing.RequireRuntimeDs1Path(mapLocation.Relative);
        var local=new AssetResolver(location.DataRoot);
        var inputHashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        string Track(string path)
        {
            path=Path.GetFullPath(path);
            if (!File.Exists(path)) return path;
            string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if(inputHashes.TryGetValue(path,out var previous)&&previous!=hash)
                throw new IOException($"Export input changed during preparation: {path}");
            inputHashes[path]=hash;return path;
        }
        string Resolve(string logical) {var p=local.ResolveForRead(logical);return Track(File.Exists(p)?p:assets.ResolveForRead(logical));}
        Track(mapPath);Track(presetPath);
        var map=Ds1CollisionDocument.Load(mapPath);
        int[] originalGrid=[map.Width,map.Height];
        var sourceMap=map.Serialize();var root=JsonNode.Parse(File.ReadAllBytes(presetPath))!.AsObject();
        var source=PresetDocument.Load(Track(presetPath));
        var links = new PlacementLinks(source, map);
        if (links.Warning is not null) throw new InvalidDataException(links.Warning);
        if (links.HasLinks) throw new NotSupportedException("This experimental export requires a scene without linked objects.");
        if (links.Calibration.UnitsPerTile != UnitsPerTile || links.Calibration.IsPoorFit)
            throw new NotSupportedException("Ground extension currently supports only cave pairs using 10 HD units per tile.");
        // Growth works on a detached map, after validating the original pair and its links.
        if (request.Growth is { } growth)
        {
            if (growth.Width - map.Width > 16 || growth.Height - map.Height > 16)
                throw new ArgumentException("Bounded cave growth supports at most 16 additional tiles per axis.");
            map = map.GrowCopy(growth.Width, growth.Height);
            sourceMap = map.Serialize();
        }
        if (map.Act != 1 || !location.Relative.Replace('\\', '/').StartsWith("act1/caves/", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Ground extension currently supports Act 1 cave presets only.");
        var terrain=root["terrain"]!.AsObject();var components=terrain["components"]!.AsArray();
        var transform=components.First(c=>(string?)c?["type"]=="TransformDefinitionComponent")!;
        foreach(var axis in new[]{"x","y","z"})
            if((double)transform["position"]![axis]! !=0 || (double)transform["scale"]![axis]! !=1 || (double)transform["orientation"]![axis]! !=0)
                throw new NotSupportedException("Terrain must use the identity scene transform.");
        if((double)transform["orientation"]!["w"]! !=1)throw new NotSupportedException("Terrain must use the identity scene transform.");
        var terrainComponent=components.First(c=>(string?)c?["type"]=="ModelDefinitionComponent")!;
        string logical=(string)terrainComponent["filename"]!;
        var collisionTiles=tileset.Files.SelectMany(p=>LegacyCollision.ReadTiles(Resolve(p))).ToArray();
        var collision=new LegacyCollision(map,collisionTiles);
        int CountUnresolved() => Enumerable.Range(0,map.Height).Sum(y=>Enumerable.Range(0,map.Width).Count(x=>collision.At(x,y).Unresolved));
        int sourceUnresolved=CountUnresolved();
        if(collision.At(request.DonorX,request.DonorY) is not {NoFloor:false,Unresolved:false,BlockedSubtiles:0})
            throw new ArgumentException("Choose a fully walkable, resolved source floor tile.");
        var area=new TerrainRectangle(request.MinX*UnitsPerTile,request.MinY*UnitsPerTile,request.MaxX*UnitsPerTile,request.MaxY*UnitsPerTile,request.Height);
        area.Validate();
        if(request.MinX<0||request.MinY<0||request.MaxX>=map.Width||request.MaxY>=map.Height)
            throw new ArgumentException("Keep the extension inside the map's final border row and column.");
        // A narrow overlap removes coplanar wall remnants and packed-coordinate
        // slivers on the exact cut plane. Authored enclosure overlaps this collar.
        const float sceneryPadding=.05f;
        var sceneryArea=area with {MinX=area.MinX-sceneryPadding,MaxX=area.MaxX+sceneryPadding,MinZ=area.MinZ-sceneryPadding,MaxZ=area.MaxZ+sceneryPadding};
        int maxSpan = request.Growth is null ? 8 : 40;
        if(request.MaxX-request.MinX>maxSpan || request.MaxY-request.MinY>maxSpan)throw new ArgumentException($"The experimental extension is limited to {maxSpan} tiles per side.");
        var cells=Enumerable.Range(request.MinY,request.MaxY-request.MinY).SelectMany(y=>Enumerable.Range(request.MinX,request.MaxX-request.MinX).Select(x=>(X:x,Y:y))).ToArray();
        int newlyOpen=cells.Sum(p=>collision.At(p.X,p.Y).BlockedSubtiles);
        if(newlyOpen==0)throw new ArgumentException("The selected region is already walkable; it would not extend the room.");
        GroundJoin.RequireWalkableConnection(collision,ModelReader.Load(Resolve(logical)),request);
        var originalUnits=map.Units.ToArray();
        if(originalUnits.Any(u=>u.X/5>=request.MinX&&u.X/5<request.MaxX&&u.Y/5>=request.MinY&&u.Y/5<request.MaxY))
            throw new NotSupportedException("The extension overlaps a gameplay placement; choose a clear area.");
        map.ExtendGround(request.MinX,request.MinY,request.MaxX,request.MaxY,request.DonorX,request.DonorY);
        if(cells.Any(p=>collision.At(p.X,p.Y) is not {NoFloor:false,Unresolved:false,BlockedSubtiles:0}))throw new InvalidDataException("The candidate floor is still blocked.");
        byte[] editedMap=map.Serialize();map.Undo();
        if(!map.Serialize().SequenceEqual(sourceMap))throw new InvalidDataException("Ground undo did not preserve the source.");
        map.Redo();if(!map.Serialize().SequenceEqual(editedMap))throw new InvalidDataException("Ground redo did not reproduce the candidate.");
        CaveContourResult? contour=null;
        string contourLogical="data/global/tiles/act1/caves/rle_ground_contour.dt1";
        if(request.ContourStyle is { } style)
        {
            if(tileset.Files.Contains(contourLogical,StringComparer.OrdinalIgnoreCase))throw new NotSupportedException("An existing contour tileset must be registered before another contour export.");
            uint donor=map.Cell(map.Floors.First(l=>!new FloorCell(map.Cell(l,request.DonorX,request.DonorY)).IsEmpty),request.DonorX,request.DonorY);
            var key=new FloorCell(donor);
            var donors=tileset.Files.Where(p=>LegacyCollision.ReadTiles(Resolve(p)).Any(t=>t.Orientation==0&&t.Main==key.Main&&t.Sub==key.Sub)).ToArray();
            if(donors.Length!=1)throw new NotSupportedException("Contour requires one unambiguous donor DT1 file.");
            contour=CaveContour.Create(map,collisionTiles,File.ReadAllBytes(Resolve(donors[0])),request.DonorX,request.DonorY,request.MinX,request.MinY,request.MaxX,request.MaxY,style);
            editedMap=contour.Map;
            if(tileset.Files.Length==32)throw new NotSupportedException("No additional project tileset slot is available.");
            // Explicit project tilesets keep the contour resolved on editor reopen.
            // Native mask/slot assignment comes from the selected registration recipe.
            tileset=new(tileset.Mask,tileset.Files.Append(contourLogical).ToArray());
        }
        var terrainBytes=File.ReadAllBytes(Resolve(logical));
        cancellationToken.ThrowIfCancellationRequested();
        var edited=request.Growth is null?TerrainSurfaceEdit.Extend(terrainBytes,area):TerrainGrowth.Extend(terrainBytes,area);
        var retainedMeshes=new Dictionary<string,int[]>();
        var catalog = File.ReadAllBytes(Resolve("data/hd/model_lod_desc.bin"));
        string modelFolder = "data/hd/env/preset/" + Path.ChangeExtension(location.Relative.Replace('\\', '/'), null) + "_ground_" + Guid.NewGuid().ToString("N");
        var removed=new List<string>();var clipped=new List<string>();var modelExports=new Dictionary<string,byte[]>();var entities=root["entities"]!.AsArray();
        string terrainLogical = modelFolder + "/terrain.model";
        catalog = GroundModelCatalog.Register(catalog, logical, terrainLogical, [0]);
        terrainComponent["filename"] = terrainLogical;
        root["dependencies"]!["models"]!.AsArray().Add(new JsonObject { ["path"] = terrainLogical });
        for (int lod = 0; lod < 5; lod++) modelExports.Add(terrainLogical[5..^6] + $"_lod{lod}.model", edited.Bytes);
        foreach(var entity in source.Entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(entity.HasParent)throw new NotSupportedException("Parented scenes are not supported by ground extension.");
            if(entity.IsTerrain)continue;
            var node=entities.Single(n=>n?["id"]?.ToJsonString()==entity.Id)!;
            bool hasPhysics = entity.Components.Any(c => (string?)c["type"] == "PhysicsBodyDefinitionComponent");
            if (!entity.CanTransform)
            {
                if (hasPhysics) throw new NotSupportedException($"{entity.Name}: collision has no supported scene transform.");
                continue;
            }
            var t=entity.Transform;t.Validate();
            var matrix=Matrix4x4.CreateScale((float)t.Scale.X,(float)t.Scale.Y,(float)t.Scale.Z)*
                Matrix4x4.CreateFromQuaternion(new((float)t.Orientation.X,(float)t.Orientation.Y,(float)t.Orientation.Z,(float)t.Orientation.W))*
                Matrix4x4.CreateTranslation((float)t.Position.X,(float)t.Position.Y,(float)t.Position.Z);
            // Physics footprints are independent of visual bounds, including entities
            // with no model. Audit and cut them before deciding what geometry to retain.
            try { SceneryPhysicsCut.Apply(node,matrix,sceneryArea, path => File.ReadAllBytes(Resolve(path))); }
            catch (NotSupportedException ex) { throw new NotSupportedException($"{entity.Name}: {ex.Message}", ex); }
            if (entity.ModelPaths.Count == 0) continue;
            bool overlaps=false, contained=true;
            foreach(var path in entity.ModelPaths)
            {
                var model=ModelReader.Load(Resolve(path));
                foreach(var part in model.Parts)
                {
                    var points=Enumerable.Range(0,part.Positions.Length/3).Select(i=>Vector3.Transform(new(part.Positions[i*3],part.Positions[i*3+1],part.Positions[i*3+2]),matrix)).ToArray();
                    if(points.Any(p=>p.X<sceneryArea.MinX || p.X>sceneryArea.MaxX || p.Z<sceneryArea.MinZ || p.Z>sceneryArea.MaxZ))contained=false;
                    // Clear the complete vertical column. Elevated wall caps and
                    // hanging scenery otherwise survive as isolated fragments.
                    if(points.Min(p=>p.X)<sceneryArea.MaxX && points.Max(p=>p.X)>sceneryArea.MinX && points.Min(p=>p.Z)<sceneryArea.MaxZ && points.Max(p=>p.Z)>sceneryArea.MinZ)overlaps=true;
                }
            }
            if(!overlaps)continue;
            if(contained)
            {
                removed.Add(entity.Name);
                var entityComponents = node["components"]!.AsArray();
                bool outsidePhysics = entityComponents.Any(c => (string?)c?["type"] == "PhysicsBodyDefinitionComponent" && c?["fixturedefs"]?.AsArray().Count > 0);
                if (outsidePhysics)
                    foreach (var component in entityComponents.Where(c => (string?)c?["type"] is "ModelDefinitionComponent" or "ModelVariationDefinitionComponent").ToArray()) entityComponents.Remove(component);
                else entities.Remove(node);
                continue;
            }
            // Shared scenery often includes the room's floor skirts and walls. Cut
            // only the selected column and keep the complete outside fragments.
            var replacements=new Dictionary<string,string>();int modelIndex=0;
            foreach(var path in entity.ModelPaths)
            {
                var cut=ScenerySurfaceEdit.Cut(File.ReadAllBytes(Resolve(path)),matrix,sceneryArea);
                var pruned=SceneryMeshPruning.RemoveEmpty(cut);var bytes=pruned.Bytes;
                string newPath=$"{modelFolder}/{entity.Id}_{modelIndex++}.model";
                replacements.Add(path,newPath);
                retainedMeshes.Add(newPath,pruned.KeptMeshes);
                catalog = GroundModelCatalog.Register(catalog, path, newPath, pruned.KeptMeshes);
                // Use the same audited geometry at every distance tier. Simplified
                // uncut LODs must never restore a wall across the new passage.
                for(int lod=0;lod<5;lod++)modelExports.Add(newPath[5..^6]+$"_lod{lod}.model",bytes);
                root["dependencies"]!["models"]!.AsArray().Add(new JsonObject{["path"]=newPath});
            }
            void ReplacePaths(JsonNode? current)
            {
                if(current is JsonObject o)foreach(var pair in o.ToArray())
                { if(pair.Value is JsonValue value && value.TryGetValue<string>(out var text)&&replacements.TryGetValue(text,out var replacement))o[pair.Key]=replacement;else ReplacePaths(pair.Value); }
                else if(current is JsonArray a)foreach(var value in a)ReplacePaths(value);
            }
            ReplacePaths(node);
            clipped.Add(entity.Name);
        }
        // Preserve the existing floor physics file and add the new horizontal surface.
        var body=components.First(c=>(string?)c?["type"]=="PhysicsBodyDefinitionComponent")!;
        var fixtures=body["fixturedefs"]!.AsArray();
        foreach (var original in fixtures)
        {
            var shape = original!["shapetype"]!;
            if ((string?)shape["type"] != "PhysicsFileDefinition")
                throw new NotSupportedException("Expected original terrain mesh physics.");
            PhysicsMeshBounds.Read(File.ReadAllBytes(Resolve((string)shape["filename"]!))).RequireFloorBelow(area);
        }
        var fixture=fixtures[0]!.DeepClone();fixture["name"]="ground_extension_fixture";
        fixture["shapetype"]=new JsonObject {
            ["type"]="PhysicsBoxDefinition",["name"]="ground_extension_floor",
            ["center"]=new JsonObject{["x"]=(area.MinX+area.MaxX)/2,["y"]=area.Height-.5f,["z"]=(area.MinZ+area.MaxZ)/2},
            ["orientation"]=new JsonObject{["x"]=0,["y"]=0,["z"]=0,["w"]=1},
            ["extents"]=new JsonObject{["x"]=(area.MaxX-area.MinX)/2,["y"]=.5,["z"]=(area.MaxZ-area.MinZ)/2}};
        fixtures.Add(fixture);
        // Presets carry the terrain both in the entity list and in the terrain field.
        // Keep the two representations identical so either consumer sees the new floor.
        for(int i=0;i<entities.Count;i++)
            if(entities[i]?["id"]?.ToJsonString()==terrain["id"]?.ToJsonString())entities[i]=terrain.DeepClone();
        string presetRelative="hd/env/preset/"+location.Relative.Replace('\\','/');
        string mapRelative="global/tiles/"+mapLocation.Relative.Replace('\\','/');
        string terrainRelative=terrainLogical[5..^6] + "_lod0.model";
        var project=new LevelProject(1,Guid.NewGuid(),Path.GetFileNameWithoutExtension(presetPath)+" ground extension","data/"+presetRelative,"data/"+mapRelative,map.Width,map.Height,map.Act,tileset,
            "data/"+presetRelative,Convert.ToHexString(SHA256.HashData(source.Serialize())));
        project.Validate();
        string parent=Path.GetDirectoryName(destination)!;Directory.CreateDirectory(parent);
        string stage=Path.Combine(parent,".ground-extension-"+Guid.NewGuid().ToString("N"));
        string Write(string relative,byte[] bytes) { cancellationToken.ThrowIfCancellationRequested();string path=SceneWorkspace.Inside(stage,Path.Combine(stage,relative));Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,bytes);return path; }
        try
        {
            // Freeze the effective gameplay context and local HD overrides. Unmodified
            // game assets continue to use the editor's independently selected extraction.
            foreach (var tile in tileset.Files)
                Write(tile,contour is not null&&tile==contourLogical?contour.Tiles:File.ReadAllBytes(Resolve(tile)));
            if(contour is not null)
                Write("cave-contour.json",JsonSerializer.SerializeToUtf8Bytes(new { contour.Style,contour.Cells,Tile=contourLogical,NativeRegistration="required",Gameplay="not run" },new JsonSerializerOptions{WriteIndented=true}));
            string palette = $"data/global/palette/act{map.Act}/pal.dat";
            Write(palette, File.ReadAllBytes(Resolve(palette)));
            foreach (string table in new[] { "levels", "lvlprest", "lvltypes", "lvlwarp", "monpreset", "monstats", "objects" })
            {
                string path = $"data/global/excel/{table}.txt";
                if (File.Exists(Resolve(path))) Write(path, File.ReadAllBytes(Resolve(path)));
            }
            if (!Path.GetFullPath(local.DataRoot).Equals(Path.GetFullPath(assets.DataRoot), StringComparison.OrdinalIgnoreCase))
                foreach (string path in source.AssetPaths().Where(p => p.StartsWith("data/hd/", StringComparison.OrdinalIgnoreCase)).Distinct())
                {
                    string physical = local.ResolveForRead(path);
                    if (File.Exists(physical)) Write("data/" + Path.GetRelativePath(local.DataRoot, physical), File.ReadAllBytes(Track(physical)));
                    if (path.EndsWith(".model", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var texture in ModelReader.Load(Resolve(path)).TexturePaths ?? [])
                            if (File.Exists(local.Resolve(texture))) Write(texture, File.ReadAllBytes(Track(local.Resolve(texture))));
                        for (int lod = 0; lod < 5; lod++)
                        {
                            string tier = path[..^6] + $"_lod{lod}.model";
                            if (File.Exists(local.Resolve(tier)))
                                Write(tier, File.ReadAllBytes(Track(local.Resolve(tier))));
                            if (File.Exists(Resolve(tier)))
                            {
                                foreach (var texture in ModelReader.Load(Resolve(tier)).TexturePaths ?? [])
                                    if (File.Exists(local.Resolve(texture))) Write(texture, File.ReadAllBytes(Track(local.Resolve(texture))));
                            }
                        }
                    }
                }
            var jsonBytes=JsonSerializer.SerializeToUtf8Bytes(root);string outPreset=Write("data/"+presetRelative,jsonBytes);
            var outMap=Write("data/"+mapRelative,editedMap);var outTerrain=Write("data/"+terrainRelative,edited.Bytes);
            Write("data/hd/model_lod_desc.bin",catalog);
            foreach(var entry in modelExports)
            {
                var written=Write("data/"+entry.Key,entry.Value);
                if(entry.Key.EndsWith("_lod0.model",StringComparison.Ordinal))ModelReader.Load(written);
            }
            Write("data/"+presetRelative+LevelProject.Suffix,JsonSerializer.SerializeToUtf8Bytes(project));
            var outputLinks = new PlacementLinks(PresetDocument.Load(outPreset), Ds1CollisionDocument.Load(outMap));
            if(contour is not null)
            {
                var contourTiles=LegacyCollision.ReadTiles(Path.Combine(stage,contourLogical));
                var afterCollision=new LegacyCollision(Ds1CollisionDocument.Load(outMap),collisionTiles.Concat(contourTiles));
                for(int y=0;y<map.Height;y++)for(int x=0;x<map.Width;x++)
                {
                    var before=collision.At(x,y);var after=afterCollision.At(x,y);
                    if(before.Unresolved!=after.Unresolved||before.NoFloor!=after.NoFloor||before.VariantDependent!=after.VariantDependent||!before.Flags.SequenceEqual(after.Flags))
                        throw new InvalidDataException($"Contour changed collision at {x},{y}.");
                }
            }
            outputLinks.SetCalibration(links.Calibration);
            Write("data/"+presetRelative+PlacementLinks.Suffix,outputLinks.MetadataFor(outPreset+PlacementLinks.Suffix,outMap));
            if(!PresetDocument.Load(outPreset).Serialize().SequenceEqual(jsonBytes)||!Ds1CollisionDocument.Load(outMap).Serialize().SequenceEqual(editedMap))throw new InvalidDataException("Paired export did not round-trip.");
            var reloaded=ModelReader.Load(outTerrain);
            if(reloaded.Parts.Sum(p=>p.Indices.Length/3)!=edited.Triangles)throw new InvalidDataException("Exported terrain topology did not reload.");
            var receipt=new GroundExtensionReceipt(presetRelative,mapRelative,terrainRelative,request,newlyOpen,removed.ToArray(),new(edited.OriginalVertices,edited.Vertices,edited.OriginalTriangles,edited.Triangles),"not run"){ClippedScenery=clipped.ToArray(),SceneryCutPadding=sceneryPadding,OriginalGrid=originalGrid,ExportedGrid=[map.Width,map.Height],RetainedSceneryMeshes=retainedMeshes,SourceUnresolvedCells=sourceUnresolved,ExportedUnresolvedCells=CountUnresolved()};
            foreach(var input in inputHashes.ToArray())
                if(!File.Exists(input.Key)||Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input.Key)))!=input.Value)
                    throw new IOException($"Export input changed during preparation: {input.Key}");
            receipt=receipt with {InputHashes=new(inputHashes)};
            Write("ground-extension.json",JsonSerializer.SerializeToUtf8Bytes(receipt,new JsonSerializerOptions{WriteIndented=true}));
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stage,destination);
            return Path.Combine(destination,"data",presetRelative);
        }
        finally { if(Directory.Exists(stage))Directory.Delete(stage,true); }
    }
}
