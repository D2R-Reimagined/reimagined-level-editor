using System.Text.Json.Nodes;
using D2RLevel.Core;
using D2RLevel.Assets;

if (args.Length > 0 && args[0] == "--gameplay-audit")
{
    int maps = 0, units = 0, paths = 0, moved = 0, warnings = 0, unsupported = 0, editedPaths = 0, blockedPaths = 0;
    var actions = new Dictionary<uint, int>();
    foreach (var path in Directory.EnumerateFiles(args[1], "*.ds1", SearchOption.AllDirectories))
    {
        Ds1CollisionDocument doc;
        try { doc = Ds1CollisionDocument.Load(path); }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        { unsupported++; Console.WriteLine($"UNSUPPORTED {path}: {ex.Message}"); continue; }
        maps++; units += doc.Units.Count;
        if (doc.GameplayWarning is not null) { warnings++; Console.WriteLine($"READ ONLY {path}: {doc.GameplayWarning}"); continue; }
        var before = doc.Serialize();
        foreach (var unit in doc.Units)
        {
            paths += doc.PatrolPoints(unit.Index).Count;
            bool didMove = false;
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                try { doc.MoveUnit(unit.Index, unit.X + dx, unit.Y + dy); didMove = true; }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException or OverflowException) { }
                if (didMove) break;
                if (!doc.Serialize().SequenceEqual(before)) throw new Exception("Rejected movement changed bytes: " + path);
            }
            if (!didMove) continue;
            doc.Undo(); if (!doc.Serialize().SequenceEqual(before)) throw new Exception("Undo changed bytes: " + path);
            moved++;
        }
        // Patrol editing on every real path: patch, grow, shrink, each undone exactly.
        foreach (var unit in doc.Units)
        {
            var points = doc.PatrolPoints(unit.Index);
            foreach (var point in points) actions[point.Action] = actions.GetValueOrDefault(point.Action) + 1;
            if (points.Count == 0 || doc.PathEditWarning(unit.Index) is not null) { blockedPaths++; continue; }
            doc.MovePathPoint(unit.Index, 0, points[0].X, points[0].Y + (points[0].Y + 1 < doc.Height * 5 ? 1 : -1));
            doc.Undo(); if (!doc.Serialize().SequenceEqual(before)) throw new Exception("Path move undo changed bytes: " + path);
            doc.SetPathPointAction(unit.Index, 0, points[0].Action == 1 ? 2u : 1u);
            doc.Undo(); if (!doc.Serialize().SequenceEqual(before)) throw new Exception("Path action undo changed bytes: " + path);
            doc.InsertPathPoint(unit.Index, points.Count, points[0].X, points[0].Y, points[0].Action);
            if (doc.PatrolPoints(unit.Index).Count != points.Count + 1) throw new Exception("Insert did not extend the path: " + path);
            if (doc.Serialize().Length != before.Length + 12) throw new Exception("Insert resized by the wrong amount: " + path);
            doc.Undo(); if (!doc.Serialize().SequenceEqual(before)) throw new Exception("Path insert undo changed bytes: " + path);
            doc.RemovePathPoint(unit.Index, points.Count - 1);
            if (doc.PatrolPoints(unit.Index).Count != points.Count - 1) throw new Exception("Remove did not shorten the path: " + path);
            doc.Undo(); if (!doc.Serialize().SequenceEqual(before)) throw new Exception("Path remove undo changed bytes: " + path);
            editedPaths++;
        }
    }
    Console.WriteLine($"PASS gameplay corpus: {maps} maps, {units} units, {paths} linked patrol points, {moved} in-memory move/undo checks, {warnings} maps with movement disabled, {unsupported} unsupported files. No source writes.");
    Console.WriteLine($"PASS patrol corpus: {editedPaths} paths edited and undone byte-exactly (move, action, insert, remove); {blockedPaths} units without an editable path.");
    Console.WriteLine("Patrol action distribution: " + string.Join(", ", actions.OrderBy(p => p.Key).Select(p => $"{p.Key}x{p.Value}")));
    return;
}

if (args.Length > 0 && args[0] == "--animation-audit")
{
    // args: --animation-audit <dataRoot> <granny2.dll> [limit]
    ModelReader.ConfigureDecoder(Path.GetFullPath(args[2]));
    var resolver = new AssetResolver(args[1]);
    int limit = args.Length > 3 ? int.Parse(args[3]) : int.MaxValue;
    int rigs = 0, skinned = 0, unskinned = 0, noAssets = 0, failed = 0, posed = 0, partial = 0, staticPose = 0;
    var modeNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    double worstFrameMs = 0;
    foreach (var category in new[] { "enemy", "npc", "player" })
    {
        var root = Path.Combine(resolver.DataRoot, "hd", "character", category);
        if (!Directory.Exists(root)) continue;
        foreach (var dir in Directory.EnumerateDirectories(root).Take(limit))
        {
            string lod = args.Length > 4 ? args[4] : "lod3";
            var model = Directory.EnumerateFiles(dir, $"torso_{lod}.model").FirstOrDefault()
                ?? Directory.EnumerateFiles(dir, $"*_{lod}.model").FirstOrDefault();
            if (model is null) continue;
            var logical = "data/" + Path.GetRelativePath(resolver.DataRoot, model).Replace('\\', '/');
            var paths = CharacterAssets.Find(logical, resolver);
            if (paths is null) { noAssets++; continue; }
            try
            {
                var rig = CharacterAnimationReader.LoadRig(paths.RigPath);
                var animations = CharacterAnimationReader.LoadAnimations(paths.AnimationsPath, rig);
                var asset = ModelReader.Load(model);
                rigs++;
                if (!asset.IsSkinned) { unskinned++; continue; }
                skinned++;
                foreach (var a in animations) modeNames[a.Name] = modeNames.GetValueOrDefault(a.Name) + 1;
                var pose = new RigPose(rig);
                // Meshes may bind bones outside the shared rig; those influences are skipped.
                var unresolved = pose.UnresolvedBones(asset);
                if (unresolved.Count > 0)
                { partial++; Console.WriteLine($"PARTIAL {Path.GetFileName(dir)}: {unresolved.Count} bound bone(s) not in the rig, e.g. {unresolved[0]}"); }
                var animation = animations.FirstOrDefault(a => a.Name == "neutral") ?? animations.FirstOrDefault();
                if (animation is null) continue;
                var buffers = asset.Parts.Select(p => new float[p.Positions.Length]).ToArray();
                var normals = asset.Parts.Select(p => new float[p.Normals.Length]).ToArray();
                for (int frame = 0; frame < 8; frame++)
                {
                    var frameClock = System.Diagnostics.Stopwatch.StartNew();
                    pose.Apply(animation, animation.Duration * frame / 8f);
                    for (int i = 0; i < asset.Parts.Count; i++) pose.Skin3(asset.Parts[i], buffers[i], normals[i]);
                    worstFrameMs = Math.Max(worstFrameMs, frameClock.Elapsed.TotalMilliseconds);
                    foreach (var buffer in buffers)
                        foreach (var value in buffer)
                            if (!float.IsFinite(value)) throw new InvalidDataException("Posed vertex is not finite.");
                    posed++;
                    // A pose must stay near the bind silhouette. Degenerate keyframe data
                    // collapses a character to a point or flings it out of the world, and
                    // neither shows up as a non-finite value. Judged over the whole
                    // character: hiding one optional mesh by scaling it to zero is normal.
                    var bindBox = Extent(asset.Parts.SelectMany(p => p.Positions).ToArray());
                    var posedBox = Extent(buffers.SelectMany(b => b).ToArray());
                    if (posedBox.Size < bindBox.Size * 0.05)
                        throw new InvalidDataException($"Posed character collapsed ({posedBox.Size:F3} vs bind {bindBox.Size:F3}) in '{animation.Name}'.");
                    if (posedBox.Reach > bindBox.Reach * 20 + 10)
                        throw new InvalidDataException($"Posed character flew away ({posedBox.Reach:F1} vs bind {bindBox.Reach:F1}) in '{animation.Name}'.");
                }
                // A pose must actually move something, or the sampler is silently inert.
                var rest = asset.Parts.Select(p => new float[p.Positions.Length]).ToArray();
                pose.Apply(animation, 0);
                for (int i = 0; i < asset.Parts.Count; i++) pose.Skin3(asset.Parts[i], rest[i]);
                var moved = asset.Parts.Select(p => new float[p.Positions.Length]).ToArray();
                pose.Apply(animation, animation.Duration / 2);
                for (int i = 0; i < asset.Parts.Count; i++) pose.Skin3(asset.Parts[i], moved[i]);
                bool changed = rest.Select((b, i) => !b.SequenceEqual(moved[i])).Any(x => x);
                if (animation.Duration > 0.1f && !changed) { staticPose++; Console.WriteLine($"STATIC {Path.GetFileName(dir)}/{animation.Name}: no vertex moved."); }
                // Posing must never drift the bind pose itself.
                pose.Apply(null, 0);
                var bind = new float[asset.Parts[0].Positions.Length];
                pose.Skin3(asset.Parts[0], bind);
                for (int i = 0; i < bind.Length; i++)
                    if (Math.Abs(bind[i] - asset.Parts[0].Positions[i]) > 0.01f)
                        throw new InvalidDataException($"Bind pose does not reproduce the mesh (component {i}: {bind[i]} vs {asset.Parts[0].Positions[i]}).");
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
            { failed++; Console.WriteLine($"FAILED {Path.GetFileName(dir)}: {ex.Message}"); }
        }
    }
    Console.WriteLine($"PASS animation corpus: {rigs} rigs loaded, {skinned} skinned meshes posed over {posed} frames, {unskinned} unskinned, {noAssets} folders without a rig/animation pair, {partial} with bones outside the rig, {staticPose} whose sampled pose never moved, {failed} failures.");
    Console.WriteLine($"Worst single-frame skin: {worstFrameMs:F2}ms · total {clock.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine("Animation modes: " + string.Join(", ", modeNames.OrderByDescending(p => p.Value).Take(24).Select(p => $"{p.Key}x{p.Value}")));
    return;

    // Diagonal of the bounding box, and the furthest vertex from the origin.
    static (double Size, double Reach) Extent(float[] values)
    {
        if (values.Length == 0) return (0, 0);
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        double reach = 0;
        for (int i = 0; i < values.Length; i += 3)
        {
            float x = values[i], y = values[i + 1], z = values[i + 2];
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
            reach = Math.Max(reach, Math.Sqrt((double)x * x + (double)y * y + (double)z * z));
        }
        double dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
        return (Math.Sqrt(dx * dx + dy * dy + dz * dz), reach);
    }
}

if (args.Length > 0 && args[0] == "--animation-dump")
{
    // args: --animation-dump <dataRoot> <granny2.dll> <characterDir> <animation>
    ModelReader.ConfigureDecoder(Path.GetFullPath(args[2]));
    var dumpResolver = new AssetResolver(args[1]);
    var rig = CharacterAnimationReader.LoadRig(Directory.EnumerateFiles(Path.Combine(args[3], "skeleton"), "*.skeleton").First());
    var anims = CharacterAnimationReader.LoadAnimations(Path.Combine(args[3], "animation", "combined.animations"), rig);
    var clip = anims.First(a => a.Name.Equals(args[4], StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"rig '{rig.Name}' bones={rig.Bones.Count}, clip '{clip.Name}' dur={clip.Duration:F3} tracks={clip.Tracks.Count}");
    var root = rig.Bones[0];
    Console.WriteLine($"root bone '{root.Name}' parent={root.ParentIndex}");
    Console.WriteLine($"  bind T=({root.BindTranslation.X:F3},{root.BindTranslation.Y:F3},{root.BindTranslation.Z:F3}) R=({root.BindRotation.X:F3},{root.BindRotation.Y:F3},{root.BindRotation.Z:F3},{root.BindRotation.W:F3})");
    var rootTrack = clip.Tracks.FirstOrDefault(t => t.BoneName == root.Name);
    if (rootTrack is null) Console.WriteLine("  no track for the root bone");
    else
    {
        for (int k = 0; k < Math.Min(3, rootTrack.Times.Length); k++)
            Console.WriteLine($"  key {k} t={rootTrack.Times[k]:F3} T=({rootTrack.Translations[k].X:F3},{rootTrack.Translations[k].Y:F3},{rootTrack.Translations[k].Z:F3}) R=({rootTrack.Rotations[k].X:F3},{rootTrack.Rotations[k].Y:F3},{rootTrack.Rotations[k].Z:F3},{rootTrack.Rotations[k].W:F3})");
    }
    // Compare a few bones' world positions in bind versus posed, to see what moved.
    var bindPose = new RigPose(rig); var posedPose = new RigPose(rig);
    posedPose.Apply(clip, clip.Duration / 2);
    foreach (var name in new[] { rig.Bones[0].Name, "pelvis_bind_jnt", "head_bind_jnt", "spine_01_bind_jnt" })
    {
        if (!rig.IndexByName.TryGetValue(name, out int i)) continue;
        var s = posedPose.Skin[i];
        Console.WriteLine($"  {name}: skin row0=({s.M11:F3},{s.M12:F3},{s.M13:F3}) row1=({s.M21:F3},{s.M22:F3},{s.M23:F3}) row2=({s.M31:F3},{s.M32:F3},{s.M33:F3}) T=({s.M41:F3},{s.M42:F3},{s.M43:F3})");
    }
    return;
}

if (args.Length > 0 && args[0] == "--pairs")
{
    var pairs = PresetPairing.Scan(new AssetResolver(args[1]));
    File.WriteAllText(args[2], System.Text.Json.JsonSerializer.Serialize(pairs, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Indexed {pairs.Length} JSON/DS1 pairs."); return;
}

if (args.Length > 0 && args[0] == "--legacy")
{
    var legacy = LegacyFloorScene.Load(args[1], new AssetResolver(args[2]), CancellationToken.None);
    Console.WriteLine($"PASS DS1 {legacy.Map.Width}x{legacy.Map.Height}; {legacy.Map.Layers.Length} floors; mask {legacy.Mask}; {legacy.Dt1Paths.Length} DT1s; {legacy.Tiles.Count} keys; {legacy.MissingCells} unresolved cells");
    if (legacy.MissingCells > 0) throw new Exception("Unresolved floor cells.");
    return;
}

if (args.Length > 0 && args[0] == "--probe")
{
    if (args.Length > 2) ModelReader.ConfigureDecoder(Path.GetFullPath(args[2]));
    var model = ModelReader.Load(args[1]);
    using (var stream = File.OpenRead(args[1]))
    using (var reader = new LSLib.Granny.GR2.GR2Reader(stream))
    {
        var raw = new D2rModelRoot(); reader.Read(raw);
        foreach (var part in model.Parts)
        {
            var mesh = raw.Meshes!.First(m => m.Name == part.Name);
            float scale = mesh.ExtendedData?.VertexScale ?? 1;
            if (part.Positions[0] != mesh.PrimaryVertexData!.Vertices[0].Position.X * scale)
                throw new Exception("D2R per-mesh VertexScale was not applied.");
        }
        Console.WriteLine("PASS D2R per-mesh VertexScale contract");
    }
    Console.WriteLine($"PASS model: {model.Parts.Count} parts, {model.Parts.Sum(p => p.Indices.Length / 3)} triangles");
    foreach (var part in model.Parts) Console.WriteLine($"{part.Name}: {part.Positions.Length / 3} vertices, coordinate range {part.Positions.Min():F3} .. {part.Positions.Max():F3}, albedo={part.AlbedoPath}");
    return;
}
if (args.Length > 0 && args[0] == "--texture")
{
    var texture = TextureReader.Load(args[1]);
    Console.WriteLine($"PASS texture: {texture.Width} x {texture.Height}, {texture.Rgba.Length} bytes");
    return;
}
if (args.Length > 0 && args[0] == "--audit")
{
    var document = PresetDocument.Load(args[1]);
    var resolver = new AssetResolver(args[2]);
    var missing = resolver.Missing(document);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3]))!);
    File.WriteAllLines(args[3], missing);
    Console.WriteLine($"{document.Entities.Count} entities; {document.AssetPaths().Count()} references; {missing.Length} missing. List: {args[3]}");
    return;
}

var folder = Path.Combine(Path.GetTempPath(), "D2RLevel-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    passed++; Console.WriteLine("PASS " + name);
}
void Throws(Action action, string name)
{
    try { action(); }
    catch { Check(true, name); return; }
    throw new Exception("FAIL " + name);
}
try
{
    var source = Path.Combine(folder, "source.json");
    CollisionChecks.Run(folder, Check, Throws);
    GameplayChecks.Run(folder, Check, Throws);
    LinkChecks.Run(folder, Check, Throws);
    LinkRepairChecks.Run(folder, Check, Throws);
    PathChecks.Run(folder, Check, Throws);
    CalibrationChecks.Run(folder, Check, Throws);
    var pairBase = Path.Combine(folder, "pair-base"); var pairMod = Path.Combine(folder, "pair-mod");
    foreach (var root in new[] { pairBase, pairMod })
    {
        Directory.CreateDirectory(Path.Combine(root, "hd/env/preset/act1/town"));
        Directory.CreateDirectory(Path.Combine(root, "global/tiles/act1/town"));
    }
    var baseJson = Path.Combine(pairBase, "hd/env/preset/act1/town/towns1.json");
    var modJson = Path.Combine(pairMod, "hd/env/preset/act1/town/towns1.json");
    var baseDs1 = Path.GetFullPath(Path.Combine(pairBase, "global/tiles/act1/town/towns1.ds1"));
    var modDs1 = Path.GetFullPath(Path.Combine(pairMod, "global/tiles/act1/town/towns1.ds1"));
    File.WriteAllText(baseJson, "{}"); File.WriteAllText(modJson, "{}"); File.WriteAllText(baseDs1, "test");
    var pairResolver = new AssetResolver(pairBase);
    Check(PresetPairing.Find(modJson, pairResolver)?.Ds1Path == baseDs1, "mod JSON falls back to exact base DS1");
    File.WriteAllText(modDs1, "test");
    Check(PresetPairing.Find(modJson, pairResolver)?.Ds1Path == modDs1, "mod DS1 takes precedence over base copy");
    Check(PresetPairing.RelativeDs1(modDs1, pairResolver) == "act1/town/towns1.ds1", "mod DS1 uses canonical table context");
    var looseJson = Path.Combine(folder, "renamed.json");
    var customPairs = new Dictionary<string, string> { [looseJson] = modDs1 };
    Check(PresetPairing.Find(looseJson, pairResolver, customPairs)?.Ds1Path == modDs1, "remembered pairing resolves renamed JSON");
    Check(PresetPairing.Find(looseJson, pairResolver) is null, "no heuristic pairing of unrelated filenames");
    Check(PresetPairing.Scan(pairResolver).Length == 1, "base mapping scan indexes existing pairs");
    var pairSettingsPath = Path.Combine(folder, "pair-settings.json"); new EditorSettings(PresetPairs: customPairs).Save(pairSettingsPath);
    Check(EditorSettings.Load(pairSettingsPath, out _).PresetPairs![looseJson] == modDs1, "manual pairings survive restart");
    var footprint = new ModelFootprint("cart", 290, 340, 310, 360);
    Check(footprint.Tiles(57, 41, 10).SequenceEqual(new[] { (29,34), (30,34), (29,35), (30,35) }), "footprint maps supplied cart-scale rectangle to four DS1 tiles");
    Check(new ModelFootprint("outside", -20, -10, -1, -1).Tiles(57, 41, 10).Length == 0, "outside footprint produces no clamped edge blocking");
    Throws(() => footprint.Tiles(57, 41, 0), "reject zero footprint scale");
    var settingsFile = Path.Combine(folder, "preferences", "settings.json");
    Check(EditorSettings.Load(settingsFile, out var settingsWarning) == new EditorSettings() && settingsWarning is null, "first run uses empty settings");
    var preferences = new EditorSettings("C:/extracted/data", "C:/tools/granny2.dll");
    preferences.Save(settingsFile);
    Check(EditorSettings.Load(settingsFile, out settingsWarning) == preferences && settingsWarning is null, "asset and decoder paths survive settings reload");
    preferences = preferences with { AssetFolder = "C:/new/data" }; preferences.Save(settingsFile);
    Check(EditorSettings.Load(settingsFile, out _) == preferences, "replacing asset folder preserves decoder setting");
    File.WriteAllText(settingsFile, "{broken");
    Check(EditorSettings.Load(settingsFile, out settingsWarning) == new EditorSettings() && settingsWarning is not null, "corrupt settings recover with warning");
    File.WriteAllText(settingsFile, "null");
    Check(EditorSettings.Load(settingsFile, out settingsWarning) == new EditorSettings() && settingsWarning is not null, "null settings recover with warning");
    Check(!ModelReader.IsDecoderConfigured, "fresh process has no implicit native decoder");
    var compressedModel = Path.Combine(folder, "requires-decoder.model");
    using (var w = new BinaryWriter(File.Create(compressedModel)))
    {
        w.Write(new byte[148]);
        w.BaseStream.Position = 32; w.Write(7u); w.Write(148u); w.Write(0u); w.Write(72u); w.Write(1u);
        w.BaseStream.Position = 104; w.Write(4u); w.Write(148u); w.Write(0u); w.Write(0u);
    }
    bool missingDecoderReported = false;
    try { ModelReader.Load(compressedModel); }
    catch (MissingModelDecoderException) { missingDecoderReported = true; }
    Check(missingDecoderReported, "compressed model reports missing decoder distinctly before parsing meshes");
    const string fixture = """
    {"type":"Preset","future":{"exact":18446744073709551615},"entities":[
      {"id":18446744073709551615,"name":"Crate","components":[
        {"type":"TransformDefinitionComponent","position":{"x":1.123456789012345,"y":2,"z":3,"unknown":"retain"},"scale":{"x":1,"y":1,"z":1},"orientation":{"x":0,"y":0,"z":0,"w":1},"futureFlag":true},
        {"type":"ModelDefinitionComponent","filename":"data/hd/crate.model"},
        {"type":"Unrecognized","nested":[{"keep":true}],"id":18446744073709551615}]},
      {"id":2,"name":"Variations","components":[{"type":"ModelVariationDefinitionComponent","variations":[{"filename":"data/hd/a.model"},{"filename":"data/hd/b.model"}]}]}
    ]}
    """;
    File.WriteAllText(source, fixture, new System.Text.UTF8Encoding(true));
    var original = File.ReadAllBytes(source);
    var doc = PresetDocument.Load(source);
    var placement = PresetDocument.Load(source);
    var prop = placement.AddModel("data/hd/crate.model", new(4, 5, 6), ["data/hd/crate.texture", "data/hd/crate.texture"]);
    Check(placement.Entities.Count == 3 && placement.IsDirty && prop.Name != "Crate" && prop.Id != "2", "insert unique named static model");
    Check(prop.Transform.Position == new Vector3d(4, 5, 6) && prop.Components.Count == 2, "insert preserves position without copying physics");
    var insertionJson = JsonNode.Parse(placement.Serialize())!;
    Check(insertionJson["dependencies"]!["models"]!.AsArray().Count == 1 && insertionJson["dependencies"]!["textures"]!.AsArray().Count == 1, "insert deduplicates dependencies");
    placement.SetTransform(prop, prop.Transform with { Position = new(7, 5, 8) });
    placement.Undo(); Check(prop.Transform.Position == new Vector3d(4, 5, 6), "insert then move undo independent");
    placement.Undo(); Check(placement.Serialize().SequenceEqual(original) && !placement.IsDirty, "undo insertion restores exact bytes and missing dependency object");
    placement.Redo(); placement.Redo();
    Check(placement.Entities.Contains(prop) && prop.Transform.Position == new Vector3d(7, 5, 8), "redo preserves inserted identity and movement");
    var insertedCopy = Path.Combine(folder, "inserted.json"); placement.SaveCopy(insertedCopy);
    Check(PresetDocument.Load(insertedCopy).Entities.Last().Id == prop.Id && !placement.IsDirty, "inserted model saves and reopens");
    placement.Undo(); placement.Undo();
    var replacement = placement.AddModel("data/hd/new.model", new());
    Check(replacement.Index > prop.Index && !placement.CanRedo, "branch after undo uses stable nonreused render index");
    var safeBytes = placement.Serialize();
    Throws(() => placement.AddModel("data/../bad.model", new()), "reject traversal during insertion");
    Throws(() => placement.AddModel("data/hd/good.model", new(double.NaN, 0, 0)), "reject invalid insertion position");
    Throws(() => placement.AddModel("data/hd/good.model", new(), ["C:/bad.texture"]), "reject invalid texture dependencies");
    Check(placement.Serialize().SequenceEqual(safeBytes), "failed insertion does not mutate document");
    var dependencyFixture = Path.Combine(folder, "dependencies.json");
    File.WriteAllText(dependencyFixture, "{\"entities\":[],\"dependencies\":{\"models\":[{\"path\":\"data/hd/new.model\",\"extra\":42}],\"future\":[9]}}");
    var withDependencies = PresetDocument.Load(dependencyFixture);
    withDependencies.AddModel("data/hd/new.model", new());
    Check(JsonNode.Parse(withDependencies.Serialize())!["dependencies"]!["models"]!.AsArray().Count == 1, "existing model dependency is reused");
    withDependencies.Undo();
    Check(withDependencies.Serialize().SequenceEqual(File.ReadAllBytes(dependencyFixture)), "dependency metadata preserved after insertion undo");
    Check(original.SequenceEqual(doc.Serialize()), "pristine save byte-identical including BOM");
    Check(!doc.IsDirty && doc.Entities[0].Id == "18446744073709551615", "ulong ID preserved");
    Check(doc.Entities[1].ModelPaths.Count == 2, "all model variations discovered");
    Check(doc.AssetPaths().Count() == 3, "dependency audit includes component references");
    var entity = doc.Entities[0];
    var before = entity.Transform;
    doc.SetTransform(entity, before);
    Check(!doc.CanUndo, "no-op transform does not add history");
    var moved = before with { Position = before.Position with { Z = 8 } };
    doc.SetTransform(entity, moved);
    var changed = JsonNode.Parse(doc.Serialize())!;
    var expected = JsonNode.Parse(fixture)!;
    expected["entities"]![0]!["components"]![0]!["position"]!["z"] = 8;
    Check(JsonNode.DeepEquals(changed, expected), "only requested coordinate changed; unknown data preserved");
    Check(doc.IsDirty, "edit marks document dirty");
    doc.Undo();
    Check(!doc.IsDirty && doc.Serialize().SequenceEqual(original), "undo restores original bytes and dirty state");
    doc.Redo();
    Check(entity.Transform == moved, "redo restores edit");
    Throws(() => doc.SetTransform(entity, moved with { Position = new(double.NaN, 0, 0) }), "reject NaN");
    Throws(() => doc.SetTransform(entity, moved with { Scale = new(0, 1, 1) }), "reject zero scale");
    Throws(() => doc.SetTransform(entity, moved with { Orientation = new(0, 0, 0, 0) }), "reject invalid quaternion");
    Throws(() => doc.SaveCopy(source), "cannot overwrite original source");
    var copy = Path.Combine(folder, "edited.json");
    doc.SaveCopy(copy);
    Check(!doc.IsDirty && PresetDocument.Load(copy).Entities[0].Transform == moved, "saved edit reopens");
    doc.Undo();
    Check(doc.IsDirty, "undo after save is dirty");
    doc.SetTransform(entity, before with { Position = new(9, 2, 3) });
    Check(!doc.CanRedo, "new edit clears redo branch");
    doc.SaveCopy(copy);
    Check(File.Exists(copy + ".bak"), "overwrite of output creates backup");
    Check(File.ReadAllBytes(source).SequenceEqual(original), "source never changed");
    var hd = Path.Combine(folder, "data", "hd"); Directory.CreateDirectory(hd);
    var resolver = new AssetResolver(folder);
    Check(resolver.Resolve("data/hd/crate.model") == Path.Combine(hd, "crate.model"), "root resolution");
    Check(new AssetResolver(Path.Combine(folder, "data")).DataRoot == resolver.DataRoot, "data root selection");
    Throws(() => resolver.Resolve("data/../outside"), "reject traversal");
    Throws(() => resolver.Resolve("C:/outside"), "reject absolute reference");
    Check(resolver.Missing(doc).Length == 3, "missing file manifest");
    File.WriteAllBytes(Path.Combine(hd, "crate_lod0.model"), [0]);
    Check(resolver.ResolveForRead("data/hd/crate.model") == Path.Combine(hd, "crate_lod0.model"), "logical model resolves to physical LOD0");
    Check(resolver.ExtractionPath("data/hd/crate_lod0.model") == "data/hd/crate_lod0.model", "explicit LOD does not get a second suffix");
    Check(resolver.Missing(doc).Length == 2, "audit recognizes extracted LOD0");
    var catalog = ModelCatalog.Scan(resolver, CancellationToken.None);
    Check(catalog.Single().Path == "data/hd/crate.model", "catalog uses logical LOD0 name");
    Check(PresetDocument.ModelPreview(catalog[0].Path).Entities[0].Transform.Scale == new Vector3d(1, 1, 1), "isolated model preview has identity transform");
    var floorBuffer = new byte[160 * 80];
    Dt1Reader.DecodeBlock(Enumerable.Repeat((byte)17, 256).ToArray(), 1, floorBuffer, 0, 0);
    Check(floorBuffer.Count(p => p == 17) == 256 && floorBuffer[14] == 17 && floorBuffer[7 * 160] == 17, "DT1 diamond row geometry");
    Array.Clear(floorBuffer);
    Dt1Reader.DecodeBlock([2, 2, 9, 10, 0, 0, 1, 1, 11, 0, 0], 0x1001, floorBuffer, 0, 0);
    Check(floorBuffer[2] == 9 && floorBuffer[3] == 10 && floorBuffer[161] == 11 && floorBuffer.Count(p => p != 0) == 3, "DT1 RLE skip, run and next row");
    Throws(() => Dt1Reader.DecodeBlock([0, 3, 2], 0x2005, floorBuffer, 0, 0), "reject truncated DT1 RLE");
    Throws(() => Dt1Reader.DecodeBlock([0, 1, 2], 99, floorBuffer, 0, 0), "reject unknown DT1 block format");
    var ds1 = Path.Combine(folder, "floors.ds1");
    using (var w = new BinaryWriter(File.Create(ds1)))
    {
        foreach (var number in new[] { 18, 1, 0, 0, 0, 0, 1, 2 }) w.Write(number);
        w.Write(new byte[16]); // Two cells, one wall layer and its orientation layer.
        w.Write(0x02100A01u); w.Write(0u); w.Write(0u); w.Write(0x00100B01u);
    }
    var floors = Ds1Floors.Load(ds1);
    Check(floors.Width == 2 && floors.Height == 1 && floors.Layers.Length == 2 && floors.Act == 1, "DS1 stored dimensions, act and floor counts");
    Check(floors.Layers[0][0].Main == 33 && floors.Layers[0][0].Sub == 10 && floors.Layers[1][1].Sub == 11, "DS1 skips wall/orientation pairs and reads both floors");
    Check(new FloorCell(0x02100A00).IsEmpty, "DS1 empty flag is low byte, not whole DWORD");
    File.WriteAllBytes(ds1, File.ReadAllBytes(ds1)[..^1]);
    Throws(() => Ds1Floors.Load(ds1), "reject truncated DS1 floor layer");
    File.WriteAllBytes(Path.Combine(hd, "crate.model"), [0]);
    Check(resolver.ResolveForRead("data/hd/crate.model") == Path.Combine(hd, "crate.model"), "exact model path wins when present");
    Check(resolver.ResolvePreviewModel("data/hd/crate.model", 2) == Path.Combine(hd, "crate.model"), "preview falls back when reduced LOD absent");
    File.WriteAllBytes(Path.Combine(hd, "crate_lod1.model"), [0]);
    Check(resolver.ResolvePreviewModel("data/hd/crate.model", 2).EndsWith("crate_lod1.model"), "balanced preview falls back to LOD1");
    File.WriteAllBytes(Path.Combine(hd, "crate_lod2.model"), [0]);
    Check(resolver.ResolvePreviewModel("data/hd/crate.model", 2).EndsWith("crate_lod2.model"), "balanced preview selects LOD2");
    Check(resolver.ResolvePreviewModel("data/hd/crate.model", 0).EndsWith("crate.model"), "full detail preserves exact model resolution");
    Check(resolver.ResolvePreviewModel("data/hd/crate_lod0.model", 2).EndsWith("crate_lod0.model"), "explicit LOD reference is respected");
    // One red BC1 block validates the actual decoder and relative mip offsets.
    var texturePath = Path.Combine(folder, "red.texture");
    using (var writer = new BinaryWriter(File.Create(texturePath)))
    {
        writer.Write(0x2845443Cu); writer.Write((ushort)57); writer.Write((ushort)2818);
        writer.Write(4); writer.Write(4); writer.Write(1); writer.Write(0L); writer.Write(1); writer.Write(4);
        writer.Write(8); writer.Write(4); // offset field at 40; mip bytes at 44
        writer.Write((ushort)0xF800); writer.Write((ushort)0); writer.Write(0u);
    }
    var red = TextureReader.Load(texturePath);
    Check(red.Width == 4 && red.Height == 4 && red.Rgba[0] == 255 && red.Rgba[1] == 0 && red.Rgba[2] == 0 && red.Rgba[3] == 255, "BC1 red block and relative mip offset");
    var invalidTexture = File.ReadAllBytes(texturePath); invalidTexture[0] = 0; File.WriteAllBytes(texturePath, invalidTexture);
    Throws(() => TextureReader.Load(texturePath), "reject invalid texture magic");
    if (args.Length > 0)
    {
        var real = PresetDocument.Load(args[0]);
        Check(real.Serialize().SequenceEqual(File.ReadAllBytes(args[0])), "real town byte-identical round trip");
        var target = real.Entities.First(e => e.CanTransform);
        var transform = target.Transform;
        real.SetTransform(target, transform with { Position = transform.Position with { X = transform.Position.X + 1 } });
        var path = Path.Combine(folder, "town-edited.json"); real.SaveCopy(path);
        Check(PresetDocument.Load(path).Entities[target.Index].Transform.Position.X == transform.Position.X + 1, "real town move/save/reopen");
        real.Undo();
        Check(real.Serialize().SequenceEqual(File.ReadAllBytes(args[0])), "real town undo is lossless");
    }
    var excel = Path.Combine(resolver.DataRoot, "global", "excel"); Directory.CreateDirectory(excel);
    var characters = Path.Combine(hd, "character"); Directory.CreateDirectory(Path.Combine(characters, "npc"));
    File.WriteAllText(Path.Combine(excel, "monpreset.txt"), "Act\tPlace\n1\tgheed\n1\takara\n2\tother\n1\tspawn_group\n");
    File.WriteAllText(Path.Combine(excel, "monstats.txt"), "Id\nakaRA\ngheed\nother\n");
    File.WriteAllText(Path.Combine(characters, "monsters.json"), "{\"akara\":\"akara\",\"other\":\"akara\"}");
    File.WriteAllText(Path.Combine(characters, "npc", "akara.json"), "{}");
    var npcCatalog = new NpcCatalog(resolver, null);
    var npcUnit = new Ds1Unit(0, 1, 1, 3, 4, 0);
    Check(npcCatalog.Lookup(1, npcUnit).Name == "akara" && npcCatalog.Lookup(1, npcUnit).DefinitionPath is not null, "NPC ID resolves by zero-based act preset, not MonStats row");
    Check(npcCatalog.Lookup(2, npcUnit with { Id = 0 }).Name == "other", "NPC preset lookup is act-specific");
    Check(npcCatalog.Lookup(1, npcUnit with { Id = 2 }).Warning is not null, "Unresolved spawn group retains marker");
    Check(npcCatalog.Lookup(1, npcUnit with { Id = 99 }).Warning is not null, "Unknown NPC ID retains marker");
    var overrideRoot = Path.Combine(folder, "npc-mod"); Directory.CreateDirectory(Path.Combine(overrideRoot, "hd"));
    Directory.CreateDirectory(Path.Combine(overrideRoot, "global", "excel"));
    File.WriteAllText(Path.Combine(overrideRoot, "global", "excel", "monpreset.txt"), "Act\tPlace\n1\takara\n");
    Check(new NpcCatalog(resolver, overrideRoot).Lookup(1, npcUnit with { Id = 0 }).Name == "akara", "Mod NPC table wins while HD assets fall back");
    var npcProxy = PresetEntity.GameplayPreview(7, "Akara", new(new(6,0,8),new(0,0,0,1),new(1,1,1)));
    Check(npcProxy.Index == -8 && npcProxy.GameplayUnitIndex == 7 && npcProxy.Transform.Position.X == 6, "Transient NPC identity cannot collide with JSON entities");
    Throws(() => PresetDocument.ModelPreview("data/hd/crate.model").SetTransform(npcProxy, npcProxy.Transform), "NPC proxy cannot be written into preset JSON");
    Console.WriteLine($"All {passed} checks passed.");
}
finally { Directory.Delete(folder, true); }
