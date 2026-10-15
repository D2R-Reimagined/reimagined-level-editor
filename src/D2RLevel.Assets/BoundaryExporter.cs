using System.Text.Json;
using System.Security.Cryptography;
using D2RLevel.Core;

namespace D2RLevel.Assets;

public static class BoundaryExporter
{
    private static void RequireRuntimeDs1Path(string relativePath)
    {
        string full = "data/global/tiles/" + relativePath.Replace('\\', '/');
        if (full.Any(c => c < 32 || c > 126) || full.Length > 59)
            throw new InvalidDataException("Preset map path must be ASCII and at most 59 bytes including data/global/tiles/. Shorten the map folder or filename before export.");
    }

    public static BoundaryModelBounds Bounds(string modelFile)
    {
        var model=ModelReader.Load(modelFile);
        var points=model.Parts.SelectMany(p=>Enumerable.Range(0,p.Positions.Length/3)
            .Select(i=>new Vector3d(p.Positions[i*3],p.Positions[i*3+1],p.Positions[i*3+2]))).ToArray();
        if(points.Length==0)throw new InvalidDataException("Selected wall model has no vertices.");
        return new(new(points.Min(p=>p.X),points.Min(p=>p.Y),points.Min(p=>p.Z)),
            new(points.Max(p=>p.X),points.Max(p=>p.Y),points.Max(p=>p.Z)));
    }

    // Deliberately restricted to small exported candidate workspaces, never an entire extraction.
    public static string Export(string presetPath,string mapPath,AssetResolver fallback,BoundaryRequest request,
        string wallModel,string destination,GridCalibration? explicitCalibration=null,LevelTileset? tileset=null)
    {
        request=BoundaryPolygon.Region(request);
        var location=PresetPairing.Split(presetPath,"hd/env/preset") ?? throw new InvalidDataException("Choose a paired exported candidate.");
        var mapLocation=PresetPairing.Split(mapPath,"global/tiles") ?? throw new InvalidDataException("Choose a game-relative DS1.");
        if(!Path.GetFullPath(location.DataRoot).Equals(Path.GetFullPath(mapLocation.DataRoot),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Scene and map must belong to the same exported candidate.");
        RequireRuntimeDs1Path(mapLocation.Relative);
        destination=Path.GetFullPath(destination);if(Directory.Exists(destination)||File.Exists(destination))throw new IOException("Choose a fresh boundary output folder.");
        string source=Path.GetFullPath(location.DataRoot);
        if(destination.StartsWith(source+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Output must be outside the source candidate.");
        // Refuse linked directories before recursion as well as linked files.
        var files=CandidateFiles(source).Take(257).ToArray();
        if(files.Length>256 || files.Sum(p=>new FileInfo(p).Length)>256L*1024*1024)
            throw new InvalidDataException("Boundary export requires a small candidate workspace (at most 256 files / 256 MiB), not a full game extraction.");
        if(files.Any(p=>(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0))throw new InvalidDataException("Linked source files are unsupported.");
        var hashes=files.ToDictionary(p=>p,p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        var scene=PresetDocument.Load(presetPath);var map=Ds1CollisionDocument.Load(mapPath);var links=new PlacementLinks(scene,map);
        var project=LevelProject.ForPreset(presetPath);
        project?.VerifyMap(map);
        tileset ??= project?.Tileset;
        if(tileset is null){
            var context=LegacyFloorScene.Load(mapPath,fallback,CancellationToken.None,source);
            tileset=new LevelTileset(context.Mask,context.Dt1Paths.Select(p=>"data/global/tiles/"+
                (PresetPairing.Split(p,"global/tiles")?.Relative ?? throw new InvalidDataException("Tileset outside data.")).Replace('\\','/')).ToArray());
        }
        tileset.Validate();
        if(explicitCalibration is not null)links.SetCalibration(explicitCalibration);
        var local=new AssetResolver(source);
        string Resolve(string logical){
            var p=local.ResolveForRead(logical);if(!File.Exists(p))p=fallback.ResolveForRead(logical);
            if(File.Exists(p)){
                string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
                if(hashes.TryGetValue(p,out var before)&&before!=hash)throw new IOException("Boundary input changed during preparation: "+p);
                hashes[p]=hash;
            }
            return p;
        }
        string contourLogical="data/global/tiles/"+Path.ChangeExtension(mapLocation.Relative,"boundary.dt1").Replace('\\','/');
        var revision=BoundaryRevision.Load(presetPath);
        if(revision is not null){
            if(revision.Contour!=contourLogical || tileset.Files.Count(p=>p.Equals(contourLogical,StringComparison.OrdinalIgnoreCase))!=1 ||
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Resolve(contourLogical))))!=revision.ContourHash)
                throw new InvalidDataException("Boundary tileset changed; regeneration stopped.");
            revision.Restore(scene,map,links);
            tileset=new LevelTileset(0,tileset.Files.Where(p=>!p.Equals(contourLogical,StringComparison.OrdinalIgnoreCase)).ToArray());
        }else if(File.Exists(Resolve(contourLogical)))
            throw new IOException("This boundary has no regeneration record. Export from its original source map with this editor first.");
        if(tileset.Files.Length>=32)throw new NotSupportedException("Boundary outlines need one free tileset slot; this project already uses 32 files.");
        var bounds=Bounds(Resolve(wallModel));
        var tileSources=tileset.Files.ToDictionary(p=>p,p=>File.ReadAllBytes(Resolve(p)),StringComparer.OrdinalIgnoreCase);
        var tiles=tileset.Files.SelectMany(p=>LegacyCollision.ReadTiles(Resolve(p))).ToArray();
        var sourceCollision=new LegacyCollision(map,tiles);
        if(request.Corners is not null)BoundaryPolygon.ValidateFloor(sourceCollision,BoundaryPolygon.Layout(map,request));
        var plan=BoundaryAuthoring.Plan(map,request,bounds,links.Calibration.UnitsPerTile);
        if(plan.Cells.Any(c=>sourceCollision.At(c.X,c.Y) is {Unresolved:true} or {VariantDependent:true}))
            throw new InvalidDataException("Resolve the boundary's tile collision before export.");
        var added=BoundaryAuthoring.Apply(scene,map,links,request,wallModel,bounds);
        var result=BoundaryContour.Create(map,tileSources,request);
        // Contour substitution must preserve every collision flag, including the owned wall overrides.
        string scratch=Path.Combine(Path.GetTempPath(),"rle-boundary-"+Guid.NewGuid().ToString("N")+".ds1"),scratchTile=scratch+".dt1";
        try {
            File.WriteAllBytes(scratch,result.Map);var outlined=Ds1CollisionDocument.Load(scratch);
            File.WriteAllBytes(scratchTile,result.Tiles);
            var before=new LegacyCollision(map,tiles);var after=new LegacyCollision(outlined,tiles.Concat(LegacyCollision.ReadTiles(scratchTile)));
            for(int y=0;y<map.Height;y++)for(int x=0;x<map.Width;x++){
                var a=before.At(x,y);var b=after.At(x,y);
                if(a.Unresolved!=b.Unresolved||a.NoFloor!=b.NoFloor||a.VariantDependent!=b.VariantDependent||!a.Flags.SequenceEqual(b.Flags))throw new InvalidDataException($"Boundary contour changed gameplay collision at {x},{y}.");
            }
        } finally {if(File.Exists(scratch))File.Delete(scratch);if(File.Exists(scratchTile))File.Delete(scratchTile);}
        string stage=destination+".preparing-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(stage);
        try{
        foreach(var p in files){string target=Path.Combine(stage,"data",Path.GetRelativePath(source,p));Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(p,target);}
        string outPreset=Path.Combine(stage,"data/hd/env/preset",location.Relative),outMap=Path.Combine(stage,"data/global/tiles",mapLocation.Relative);
        File.WriteAllBytes(outPreset,scene.Serialize());File.WriteAllBytes(outMap,result.Map);
        string outTile=Path.Combine(stage,contourLogical);Directory.CreateDirectory(Path.GetDirectoryName(outTile)!);File.WriteAllBytes(outTile,result.Tiles);
        foreach(var tile in tileSources){string path=Path.Combine(stage,tile.Key);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,tile.Value);}
        // Editor tileset context travels with the pair. Runtime LvlTypes/Dt1Mask registration is a separate contract.
        var outputContext=new LevelTileset(0,[..tileset.Files,contourLogical]);
        var outputProject=project is null
            ? new LevelProject(1,Guid.NewGuid(),Path.GetFileNameWithoutExtension(presetPath),
                "data/hd/env/preset/"+location.Relative.Replace('\\','/'),"data/global/tiles/"+mapLocation.Relative.Replace('\\','/'),
                map.Width,map.Height,map.Act,outputContext,"data/hd/env/preset/"+location.Relative.Replace('\\','/'),hashes[Path.GetFullPath(presetPath)])
            : project with {Id=Guid.NewGuid(),Tileset=outputContext};
        outputProject.Validate();File.WriteAllBytes(outPreset+LevelProject.Suffix,JsonSerializer.SerializeToUtf8Bytes(outputProject));
        // The new floor identities change the structural fingerprint; rebase the existing ownership to that exact pair.
        var metadata=JsonSerializer.Deserialize<PlacementLinkFile>(links.MetadataFor(outPreset+PlacementLinks.Suffix,outMap))!;
        var reMap=Ds1CollisionDocument.Load(outMap);
        var nextRevision=BoundaryRevision.Capture(scene,links,map,reMap,added,contourLogical,
            Convert.ToHexString(SHA256.HashData(result.Tiles)),request,wallModel);
        File.WriteAllBytes(outPreset+BoundaryRevision.Suffix,JsonSerializer.SerializeToUtf8Bytes(nextRevision));
        metadata=metadata with {Fingerprint=reMap.LinkFingerprint()};
        File.WriteAllBytes(outPreset+PlacementLinks.Suffix,JsonSerializer.SerializeToUtf8Bytes(metadata));
        var reLinks=new PlacementLinks(PresetDocument.Load(outPreset),reMap);
        if(reLinks.Warning is not null||reLinks.HasBrokenLinks)throw new InvalidDataException("Boundary ownership failed to reopen.");
        File.WriteAllBytes(Path.Combine(stage,"boundary.json"),JsonSerializer.SerializeToUtf8Bytes(new {
            request,wallModel,bounds,pieces=added.Length,calibration=links.Calibration,contour=result.Cells,
            schemaVersion=4,dimensions=new[]{map.Width,map.Height},act=map.Act,tileset=outputContext,
            contourTiles=contourLogical,identities=result.Identities,
            collision="Full-tile owned DS1 blocking; each source floor's graphics and collision preserved",
            registration="Editor project prepared; runtime requires tileset-slot and automap-group registration for the exported identities",gameplay="not run"
        },new JsonSerializerOptions{WriteIndented=true}));
        foreach(var p in hashes)if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Key)))!=p.Value)
            throw new IOException("Boundary source changed during export: "+p.Key);
        Directory.Move(stage,destination);
        return Path.Combine(destination,"data/hd/env/preset",location.Relative);
        }finally{if(Directory.Exists(stage))Directory.Delete(stage,true);}
    }

    private static IEnumerable<string> CandidateFiles(string directory)
    {
        if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked candidate directories are unsupported.");
        foreach(var path in Directory.EnumerateFileSystemEntries(directory)){
            var attributes=File.GetAttributes(path);
            if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked candidate paths are unsupported.");
            if((attributes&FileAttributes.Directory)!=0){foreach(var child in CandidateFiles(path))yield return child;}
            else yield return path;
        }
    }
}
