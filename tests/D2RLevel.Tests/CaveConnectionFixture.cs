using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using D2RLevel.Core;

/// <summary>A bounded offline fixture using the existing connection and workspace save APIs.</summary>
internal static class CaveConnectionFixture
{
    private static string Hash(string file)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    public static void Prepare(string registered, string extracted, string output, string tableHelper, bool failBeforePublish=false)
    {
        registered=Path.GetFullPath(registered);extracted=Path.GetFullPath(extracted);output=Path.GetFullPath(output);
        string registeredData=Path.Combine(registered,"data");
        if(!Directory.Exists(registeredData)||!Directory.Exists(extracted))throw new DirectoryNotFoundException("Registered cave and extracted vanilla data are required.");
        if(Directory.Exists(output)||File.Exists(output))throw new IOException("Use a fresh fixture output.");
        string parent=Path.GetDirectoryName(output)!;
        SceneWorkspace.Inside(parent,output);
        // Do not accidentally create an output inside either immutable source tree.
        foreach(string source in new[]{registered,extracted})
            if((output+Path.DirectorySeparatorChar).StartsWith(source.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Fixture output must be outside its source folders.");
        var inputHashes=new[]{registered,extracted}.SelectMany(p=>Directory.EnumerateFiles(p,"*",SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p=>p,Hash,StringComparer.OrdinalIgnoreCase);
        void VerifyInputs(){foreach(var (file,hash) in inputHashes)if(Hash(file)!=hash)throw new IOException("Input changed during preparation: "+file);}
        Directory.CreateDirectory(parent);
        string stage=Path.Combine(parent,".rle-connection-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);int checks=0;bool published=false;
        void Check(bool success,string description){if(!success)throw new InvalidDataException(description);checks++;}
        void CopyTree(string source,string destination)
        {
            foreach(string file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories))
            {
                string target=SceneWorkspace.Inside(stage,Path.Combine(destination,Path.GetRelativePath(source,file)));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target,true);
            }
        }
        void Node(params string[] arguments)
        {
            var start=new ProcessStartInfo("node"){UseShellExecute=false,RedirectStandardError=true,RedirectStandardOutput=true};
            start.ArgumentList.Add(Path.GetFullPath(tableHelper));foreach(string argument in arguments)start.ArgumentList.Add(argument);
            using var process=Process.Start(start)??throw new IOException("Cannot start table preparation helper.");
            var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();process.WaitForExit();
            if(process.ExitCode!=0)throw new InvalidDataException(stdout.Result+stderr.Result);
        }
        try
        {
            string data=Path.Combine(stage,"data");
            CopyTree(extracted,data);CopyTree(registered,stage);
            var effectiveHashes=Directory.EnumerateFiles(data,"*",SearchOption.AllDirectories).ToDictionary(p=>Path.GetRelativePath(data,p),Hash,StringComparer.OrdinalIgnoreCase);
            var permittedChanges=new HashSet<string>(StringComparer.OrdinalIgnoreCase){Path.Combine("global","excel","levels.txt"),Path.Combine("global","excel","lvlprest.txt")};
            var tables=new GameDataTables(null,data);
            var levels=tables.Read("levels");var presets=tables.Read("lvlprest");var types=tables.Read("lvltypes");
            var cave=levels.Rows.Single(r=>r["Id"]=="138");var cavePreset=presets.Rows.Single(r=>r["Def"]=="1092");
            Check(cave["LevelType"]=="36"&&cavePreset["LevelId"]=="138","Cave registration must remain 138/1092/type 36.");
            Check(!levels.Rows.Any(r=>r["Id"]=="147")&&
                levels.Rows.Where(r=>int.TryParse(r["Id"],out _)).Select(r=>int.Parse(r["Id"])).SequenceEqual(Enumerable.Range(0,139))&&
                presets.Rows.Where(r=>int.TryParse(r["Def"],out _)).Select(r=>int.Parse(r["Def"])).SequenceEqual(Enumerable.Range(0,1093)),
                "Isolated vanilla fixture requires contiguous physical Levels 0..138 and LvlPrest 0..1092, without mod area 147.");
            var warp=tables.Read("lvlwarp").Rows.Single(r=>r["Id"]=="6");
            Check(warp["OffsetX"]=="5"&&warp["OffsetY"]=="0"&&warp["LitVersion"]=="0"&&warp["Direction"]=="b","Warp 6 must provide the recorded (+5,0) nonlit arrival.");
            var graph=new EntranceConnections(null,data);
            Check(graph.Variants(1).Length==4,"Expected all four native town variants.");
            Check(graph.Variants(138).Length==1,"Expected the one registered cave variant.");
            var markers=new List<object>();
            foreach(int area in new[]{1,138})foreach(string relative in graph.Variants(area))
            {
                string mapPath=graph.ResolveMap(relative);var map=Ds1CollisionDocument.Load(mapPath);
                string presetPath=Path.Combine(data,"hd/env/preset",Path.ChangeExtension(relative.Replace('\\','/'),".json"));
                var project=LevelProject.ForPreset(presetPath);project?.VerifyMap(map);
                int x=6,y=11;
                if(area==1)
                {
                    var spawn=map.ExitTiles().Where(t=>t.Main==30&&t.Orientation==10).ToArray();
                    Check(spawn.Length==1,"Native town spawn 30 must be unambiguous: "+relative);
                    x=spawn[0].X;y=spawn[0].Y+3;
                }
                int requestedX=x,requestedY=y;
                string[] dt1s;
                if(project is not null)dt1s=project.Tileset.Files.Select(p=>Path.Combine(data,p[5..])).ToArray();
                else
                {
                    var level=graph.Area(area);var preset=graph.Presets(area).Single(p=>Enumerable.Range(1,6).Any(i=>p["File"+i].Replace('\\','/').Equals(relative.Replace('\\','/'),StringComparison.OrdinalIgnoreCase)));
                    var type=types.Rows.Single(t=>t["Id"]==level["LevelType"]);uint mask=uint.Parse(preset["Dt1Mask"]);
                    dt1s=Enumerable.Range(0,32).Where(i=>(mask&(1u<<i))!=0).Select(i=>type["File "+(i+1)]).Where(p=>p.Length>0&&p!="0")
                        .Select(p=>Path.Combine(data,"global/tiles",p.Replace('\\','/'))).ToArray();
                }
                string blank=Path.Combine(data,"global/tiles/act1/outdoors/blank.dt1");
                if(File.Exists(blank))dt1s=[..dt1s,blank];
                var tiles=dt1s.Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(LegacyCollision.ReadTiles).ToArray();
                var originalCollision=new LegacyCollision(map,tiles);
                bool Available(int tileX,int tileY)=>tileX>=1&&tileY>=1&&tileX<map.Width-1&&tileY<map.Height-1&&
                    originalCollision.At(tileX,tileY) is {NoFloor:false,Unresolved:false,BlockedSubtiles:0}&&
                    map.Walls.All(w=>map.Cell(w,tileX,tileY)==0)&&!map.Units.Any(u=>u.X/5==tileX&&u.Y/5==tileY);
                if(area==1)
                {
                    // Keep native floor artwork/collision intact. Search a bounded neighborhood
                    // for a wholly clear marker and Warp 6 landing, without placed DS1 units.
                    var candidates=Enumerable.Range(-8,17).SelectMany(dy=>Enumerable.Range(-8,17).Select(dx=>(X:requestedX+dx,Y:requestedY+dy,Distance:dx*dx+dy*dy)))
                        .OrderBy(p=>p.Distance).ThenBy(p=>p.Y).ThenBy(p=>p.X).ToArray();
                    var available=candidates.Where(p=>Available(p.X,p.Y)&&Available(p.X+1,p.Y)).ToArray();
                    Check(available.Length>0,"No wholly clear native town marker/arrival pair within eight tiles: "+relative);
                    x=available[0].X;y=available[0].Y;
                }
                Check(Available(x,y)&&Available(x+1,y),"Marker/arrival pair must retain clear native collision and have no placed units.");
                void Clear(Ds1CollisionDocument document,string phase)
                {
                    var collision=new LegacyCollision(document,tiles);
                    foreach(var cell in new[]{(x,y),(x+1,y)})
                    {
                        var state=collision.At(cell.Item1,cell.Item2);
                        Check(state is {NoFloor:false,Unresolved:false,BlockedSubtiles:0},$"{relative} {phase} marker/arrival collision at {cell}: nofloor={state.NoFloor}, unresolved={state.Unresolved}, blocked={state.BlockedSubtiles}, flags={Convert.ToHexString(state.Flags)}; tiles={string.Join(',',dt1s.Select(Path.GetFileName))}; cells={string.Join(',',document.Floors.Concat(document.Walls).Select(l=>$"{l.Name}:{document.Cell(l,cell.Item1,cell.Item2):X8}/{l.Orientations[cell.Item2*document.Width+cell.Item1]}"))}");
                    }
                }
                Clear(map,"before");
                var originalLinks=new PlacementLinks(PresetDocument.Load(presetPath),map);
                Check(originalLinks.Warning is null&&!originalLinks.HasLinks,"Fixture expects valid, unlinked exported scene metadata.");
                var calibration=originalLinks.Calibration;
                byte[] sourceBytes=map.Serialize(),markerBytes=Ds1WarpAuthoring.AddMarker(map,0,x,y,7);
                int markerIndex=y*map.Width+x,wallOffset=map.Walls[0].Offset;
                var markerOffsets=new[]{wallOffset+markerIndex*4,wallOffset+map.Width*map.Height*4+markerIndex*4}.SelectMany(p=>Enumerable.Range(p,4)).ToHashSet();
                Check(sourceBytes.Length==markerBytes.Length&&Enumerable.Range(0,sourceBytes.Length).All(i=>markerOffsets.Contains(i)||sourceBytes[i]==markerBytes[i]),"Marker must preserve all unrelated DS1 bytes, floor collision, units and native paths.");
                File.WriteAllBytes(mapPath,markerBytes);
                permittedChanges.Add(Path.GetRelativePath(data,mapPath));
                map=Ds1CollisionDocument.Load(mapPath);Clear(map,"after");
                Check(map.ExitTiles().Single(t=>t.IsExit&&t.Main==7) is {Hidden:true,Orientation:10},"Hidden slot 7 marker must reopen.");
                string sidecar=presetPath+PlacementLinks.Suffix;
                permittedChanges.Add(Path.GetRelativePath(data,sidecar));
                File.Delete(sidecar);var links=new PlacementLinks(PresetDocument.Load(presetPath),map);
                links.SetCalibration(calibration);
                File.WriteAllBytes(sidecar,links.MetadataFor(sidecar,mapPath));
                var reopenedLinks=new PlacementLinks(PresetDocument.Load(presetPath),Ds1CollisionDocument.Load(mapPath));
                Check(reopenedLinks.Warning is null&&reopenedLinks.Calibration==calibration,"Rebuilt sidecar fingerprint/calibration must reopen.");
                markers.Add(new{area,map=relative,tileX=x,tileY=y,requestedTileX=requestedX,requestedTileY=requestedY,deviationX=x-requestedX,deviationY=y-requestedY,
                    placement=area==1?"Nearest wholly clear native pair within eight tiles of spawn +3":"Fixed cave tile",slot=7,warpId=6,arrivalSubtileX=x*5+5,arrivalSubtileY=y*5,dt1s=dt1s.Select(p=>Path.GetRelativePath(data,p)),calibration,
                    hdObjectAndPhysicsClearance="unverified"});
            }
            Node(data);
            // Author the actual reciprocal pair through the shared scene history and transactional save.
            var scene=SceneWorkspace.Scan(data).Single(s=>LevelProject.ForPreset(s.JsonPath)?.Map=="data/global/tiles/"+graph.Variants(138).Single().Replace('\\','/'));
            var session=new WorkspaceSceneSession(data,scene);var json=PresetDocument.Load(scene.JsonPath);var ds1=Ds1CollisionDocument.Load(scene.Ds1Path);
            var pair=new PlacementLinks(json,ds1);pair.ConnectWorkspace();
            string levelFile=Path.Combine(data,"global/excel/levels.txt");var edits=new LevelTableEdits(levelFile,data,json.History);
            var connections=new EntranceConnections(null,data,edits);var from=new AreaEndpoint(1,7);var to=new AreaEndpoint(138,7);
            byte[] before=edits.Serialize();var plan=connections.Plan(from,to);
            Check(plan.Routes.SequenceEqual(new[]{(1,7,138),(138,7,1)}),"Connection plan must contain exactly two reciprocal routes.");
            connections.Apply(from,to);byte[] applied=edits.Serialize();
            Check(connections.Connection(from).Destination==138&&connections.Connection(to).Destination==1&&edits.IsDirty,"Connection apply must stage both reciprocal routes.");
            json.Undo();Check(edits.Serialize().SequenceEqual(before)&&!edits.IsDirty,"Shared undo must restore exact unused endpoint table bytes.");
            json.Redo();Check(edits.Serialize().SequenceEqual(applied)&&edits.IsDirty,"Shared redo must restore exact reciprocal table bytes.");
            // Fail the fourth file after the paired files were replaced; every save output must roll back.
            string[] savePaths=[scene.JsonPath,scene.Ds1Path,scene.JsonPath+PlacementLinks.Suffix,levelFile];
            var snapshots=savePaths.Select(File.ReadAllBytes).ToArray();bool rejected=false;
            using(var locked=new FileStream(levelFile,FileMode.Open,FileAccess.Read,FileShare.Read))
                try{session.Save(json,ds1,pair,edits);}catch(IOException){rejected=true;}
            Check(rejected&&savePaths.Select((p,i)=>File.ReadAllBytes(p).SequenceEqual(snapshots[i])).All(v=>v),"Failed combined save must restore scene, DS1, links and Levels exactly.");
            session.Save(json,ds1,pair,edits);
            Check(!edits.IsDirty&&File.ReadAllBytes(levelFile).SequenceEqual(applied),"Combined scene/table save must persist reviewed routes.");
            var savedGraph=new EntranceConnections(null,data);
            Check(savedGraph.Connection(from) is {Destination:138,ReturnSlots:[7]}&&savedGraph.Connection(to) is {Destination:1,ReturnSlots:[7]},"Saved routes must reopen reciprocally.");
            Check(new PlacementLinks(PresetDocument.Load(scene.JsonPath),Ds1CollisionDocument.Load(scene.Ds1Path)).Warning is null,"Saved actual workspace pair must reopen.");
            Node("--verify",registeredData,data);
            Check(!Directory.EnumerateFiles(data,"*.tmp",SearchOption.AllDirectories).Any(),"Transactional save leaves no staged files.");
            foreach(string backup in Directory.EnumerateFiles(data,"*.bak",SearchOption.AllDirectories))File.Delete(backup);
            Check(effectiveHashes.Where(p=>!permittedChanges.Contains(p.Key)).All(p=>Hash(Path.Combine(data,p.Key))==p.Value),"Every unedited effective data file, preset, native model catalog and dependency remains byte-exact.");
            VerifyInputs();Check(true,"All registered/extracted inputs remain hash-exact.");
            File.WriteAllText(Path.Combine(stage,"connection-receipt.json"),JsonSerializer.Serialize(new{checks,markers,routes=plan.Routes.Select(r=>new{area=r.Area,slot=r.Slot,destination=r.Destination}),inputHashes,inputHashUnchanged=true,unrelatedEffectiveDataByteExact=true,saveFailureRollback=true,prepublicationOutputRollback=true,
                nativeEntry="not run",editorUi="not run",hiddenMarkerInterpretation="Existing native recorded method; no fresh native proof",registeredArea=new{levelId=138,presetDef=1092,levelTypeId=36},output},new JsonSerializerOptions{WriteIndented=true}));
            if(failBeforePublish)throw new IOException("Injected late fixture failure before publication.");
            Directory.Move(stage,output);
            published=true; // Commit point: no throwing verification follows publication.
            Console.WriteLine($"PASS cave connection fixture: {checks} checks, all five markers, reciprocal save/reopen. Output: {output}. Native entry not run.");
        }
        finally
        {
            if(!published)
            {
                if(Directory.Exists(stage))Directory.Delete(stage,true);
                VerifyInputs();
            }
        }
    }
}
