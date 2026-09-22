using System.Text.Json.Nodes;
using D2RLevel.Core;

internal static class PrefabChecks
{
    public static void Run(string folder, Action<bool, string> check, Action<Action, string> throws)
    {
        var (source, map, links) = LinkFixture.Create(folder);
        var sourceNode = JsonNode.Parse(source.Serialize())!;
        sourceNode["dependencies"]!["models"]![0]!["customMetadata"] = 42;
        sourceNode["entities"]![0]!["components"]![0]!["retainedTransformField"] = "keep";
        File.WriteAllText(source.SourcePath, sourceNode.ToJsonString()); source = PresetDocument.Load(source.SourcePath); links = new(source, map);
        var monsterRow = new GameDataRow(new Dictionary<string, string> { ["Id"] = "fallen", ["enabled"] = "1", ["hp"] = "10" }, "monstats.txt", 2);
        var objectRow = new GameDataRow(new Dictionary<string, string> { ["Class"] = "chest", ["Name"] = "Chest", ["Mode"] = "1" }, "objects.txt", 2);
        var monster = new GameplayAsset("fallen", "Fallen", GameplayAssetKind.Monster, 1, 1, 7, "fallen", [monsterRow], null);
        var chest = new GameplayAsset("chest", "Chest", GameplayAssetKind.Object, 1, 2, 9, null, [objectRow], null);
        var catalog = new GameplayCatalog(1, [monster, chest], []);
        links.LinkUnit(source.Entities[0], 0, 10); links.LinkUnit(source.Entities[1], 1, 10);
        links.LinkFootprint(source.Entities[0], [new(1, 1), new(2, 1)], 10, false); links.LinkFootprint(source.Entities[1], [new(2, 1)], 10, false);
        var beforeJson = source.Serialize(); var beforeMap = map.Serialize(); long version = source.History.Version;
        var prefab = links.CapturePrefab("Chest camp", source.Entities, [], 10, catalog);
        check(prefab.Models.Length == 2 && prefab.Units.Length == 2 && prefab.Units[0].Patrol.Length == 2 && prefab.Models.All(m => m.Unit is not null), "prefab captures selected models, linked units, ownership and patrols");
        check(prefab.Units[0] is { X: 0, Y: 1, Flags: 123 } && prefab.Units[0].Patrol[0] is { X: 1, Y: 2, Action: 42 } && prefab.Models[0].Collision.SequenceEqual(new[] { new LinkedTile(0, 0), new LinkedTile(1, 0) }), "prefab normalizes visual, subtile, tile and patrol coordinates to one anchor");
        check(source.Serialize().SequenceEqual(beforeJson) && map.Serialize().SequenceEqual(beforeMap) && source.History.Version == version, "saving a prefab snapshot does not mutate source or history");
        check(links.CapturePrefab("unit closure", [], [0], 10, catalog).Models.Length == 1, "explicit unit selection includes its owned HD model without proximity guessing");
        throws(() => links.CapturePrefab("wrong scale", source.Entities, [], 20, catalog), "capture rejects incompatible ownership scales");
        throws(() => links.CapturePrefab("missing recipe", source.Entities, [], 10, catalog with { Assets = [chest] }), "capture rejects unresolved gameplay identities");
        throws(() => links.CapturePrefab("empty", [], [], 10, catalog), "empty prefab cannot be saved");
        var targetCatalog = catalog with { Assets = [monster with { Id = 70 }, chest with { Id = 90 }] };
        string root = Path.Combine(folder, "prefab-workspace");
        string jsonPath = Path.Combine(root, "hd", "env", "preset", "camp.json"), ds1Path = Path.Combine(root, "global", "tiles", "camp.ds1");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!); Directory.CreateDirectory(Path.GetDirectoryName(ds1Path)!);
        File.WriteAllText(jsonPath, "{\"unknownRoot\":42,\"entities\":[],\"dependencies\":{\"textures\":[{\"path\":\"data/hd/existing.texture\",\"keep\":true}]}}");
        File.WriteAllBytes(ds1Path, Ds1CollisionDocument.Create(ds1Path, 20, 20, 1, 1).Serialize());
        var target = PresetDocument.Load(jsonPath); var targetMap = Ds1CollisionDocument.Load(ds1Path); var targetLinks = new PlacementLinks(target, targetMap);
        targetMap.Paint([(8, 8)], true); targetLinks.ConnectWorkspace();
        var originalJson = target.Serialize(); var originalMap = targetMap.Serialize();
        var placed = targetLinks.PlacePrefab(prefab, 8, 8, 3, 10, targetCatalog);
        check(placed.Models.Length == 2 && targetMap.Units.Select(u => u.Id).SequenceEqual(new[] { 70, 90 }) && targetMap.Units[0] is { X: 40, Y: 41, Flags: 123 }, "placement remaps logical recipes to target IDs while preserving flags and formation");
        check(placed.Models.All(m => m.Transform.Position == new Vector3d(80, 3, 80)) && placed.Models.Select(m => m.Id).Distinct().Count() == 2 && placed.Models.All(m => !source.Entities.Any(s => s.Id == m.Id)), "prefab gets fresh identities and translated HD transforms");
        check(placed.Models[0].Components[0]["retainedTransformField"]!.GetValue<string>() == "keep" && JsonNode.Parse(target.Serialize())!["dependencies"]!["models"]![0]!["customMetadata"]!.GetValue<int>() == 42, "prefab preserves full supported components and dependency declaration metadata");
        check(targetMap.PatrolPoints(0)[0] is { X: 41, Y: 42, Action: 42 } && targetLinks.InspectOwnership(new(9, 8)).Owners.Length == 2, "prefab translates patrol actions and preserves overlapping collision owners");
        var placedJson = target.Serialize(); var placedMap = targetMap.Serialize();
        throws(() => targetLinks.PlacePrefab(prefab, 8, 8, 3, 10, targetCatalog), "existing gameplay anchors reject overlapping placement");
        throws(() => targetLinks.PlacePrefab(prefab, 19, 19, 0, 10, targetCatalog), "out-of-bounds formation rejected before mutation");
        throws(() => targetLinks.PlacePrefab(prefab, 3, 3, double.NaN, 10, targetCatalog), "nonfinite placement height rejected");
        throws(() => targetLinks.PlacePrefab(prefab with { Act = 2 }, 3, 3, 0, 10, targetCatalog), "cross-act prefab placement rejected");
        throws(() => targetLinks.PlacePrefab(prefab, 3, 3, 0, 20, targetCatalog), "different target scale rejected");
        var changedRow = monsterRow with { Fields = new Dictionary<string, string> { ["Id"] = "fallen", ["enabled"] = "1", ["hp"] = "11" } };
        throws(() => targetLinks.PlacePrefab(prefab, 3, 3, 0, 10, targetCatalog with { Assets = [monster with { Id = 70, References = [changedRow] }, chest with { Id = 90 }] }), "changed gameplay definition cannot silently alter prefab behavior");
        check(target.Serialize().SequenceEqual(placedJson) && targetMap.Serialize().SequenceEqual(placedMap), "rejected prefab placement leaves both documents unchanged");
        target.Undo();
        check(target.Serialize().SequenceEqual(originalJson) && targetMap.Serialize().SequenceEqual(originalMap) && !targetLinks.HasLinks, "one undo restores exact JSON, DS1, dependencies and ownership including preexisting blocking");
        target.Redo(); check(target.Serialize().SequenceEqual(placedJson) && targetMap.Serialize().SequenceEqual(placedMap), "one redo restores the complete prefab assembly");
        var session = new WorkspaceSceneSession(root, new("Camp", jsonPath, ds1Path)); session.Save(target, targetMap, targetLinks);
        var reopened = new PlacementLinks(PresetDocument.Load(jsonPath), Ds1CollisionDocument.Load(ds1Path));
        check(reopened.Warning is null && !reopened.HasBrokenLinks && reopened.Links.Count == 2, "prefab scene saves and reopens with healthy gameplay and collision links");
        // Missing path storage fails after HD/unit insertion and must roll back the whole edit.
        var bareBytes = Ds1CollisionDocument.Create(ds1Path, 20, 20, 1, 1).Serialize()[..^4]; string barePath = Path.Combine(root, "bare.ds1"); File.WriteAllBytes(barePath, bareBytes);
        var bareMap = Ds1CollisionDocument.Load(barePath); var bareJson = PresetDocument.ModelPreview("data/hd/base.model"); var bareLinks = new PlacementLinks(bareJson, bareMap); var bareBefore = bareJson.Serialize();
        throws(() => bareLinks.PlacePrefab(prefab, 3, 3, 0, 10, targetCatalog), "unsupported target patrol block rejects partial prefab insertion");
        check(bareJson.Serialize().SequenceEqual(bareBefore) && bareMap.Serialize().SequenceEqual(bareBytes) && bareLinks.Links.Count == 0 && !bareJson.History.CanUndo, "failed nested prefab transaction restores all inserted entities, units and link metadata");
        string assets = Path.Combine(folder, "prefab-assets"); Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "a_lod0.model"), "model A"); File.WriteAllText(Path.Combine(assets, "b.model"), "model B"); File.WriteAllText(Path.Combine(assets, "atlas.texture"), "texture");
        (string Physical, string File, string[] Dependencies) Inspect(string logical)
        {
            string file = logical.EndsWith("a.model") ? "a_lod0.model" : Path.GetFileName(logical);
            return (Path.Combine(assets, file), "data/hd/" + file, logical.EndsWith(".model") ? ["data/hd/atlas.texture"] : []);
        }
        string package = Path.Combine(folder, "saved-prefab"); var saved = prefab.Save(package, Inspect); saved.VerifyAssets(package);
        check(saved.Assets.Length == 3 && saved.Assets.Single(a => a.Logical.EndsWith("a.model")).File.EndsWith("a_lod0.model"), "prefab bundles dependency closure and preserves logical model to physical LOD mapping");
        var loaded = GameplayPrefab.Load(package); check(loaded.Name == prefab.Name && loaded.Units[0].Patrol.SequenceEqual(prefab.Units[0].Patrol), "prefab manifest reload preserves gameplay and patrol recipes");
        check(loaded.Dependencies["models"]![0]!["customMetadata"]!.GetValue<int>() == 42, "dependency declaration metadata survives packaged save and reload");
        throws(() => prefab.Save(package, Inspect), "saving never overwrites an existing prefab package");
        string installRoot = Path.Combine(folder, "prefab-install"); Directory.CreateDirectory(installRoot);
        string[] installed = loaded.InstallAssets(package, installRoot, _ => null);
        check(installed.Length == 3 && File.ReadAllText(Path.Combine(installRoot, "hd", "a_lod0.model")) == "model A", "placement dependency installation copies only absent files with original game paths");
        check(loaded.InstallAssets(package, installRoot, logical => Path.Combine(installRoot, loaded.Assets.Single(a => a.Logical == logical).File[5..])).Length == 0, "matching installed dependencies are reused");
        File.WriteAllText(Path.Combine(installRoot, "hd", "atlas.texture"), "different");
        throws(() => loaded.InstallAssets(package, installRoot, logical => Path.Combine(installRoot, loaded.Assets.Single(a => a.Logical == logical).File[5..])), "conflicting target assets are never overwritten");
        check(File.ReadAllText(Path.Combine(installRoot, "hd", "atlas.texture")) == "different", "dependency conflict preserves destination file");
        File.AppendAllText(Path.Combine(package, "assets", "data", "hd", "atlas.texture"), "tampered"); throws(() => loaded.VerifyAssets(package), "changed bundled dependency invalidates prefab");
        throws(() => (prefab with { References = ["data/hd/../../outside"] }).Validate(), "prefab rejects dependency traversal");
        throws(() => (prefab with { References = ["data/global/excel/levels.txt"] }).Validate(), "HD bundles cannot install global gameplay table overrides");
        var duplicateOwner = prefab with { Models = [prefab.Models[0], prefab.Models[0]] }; throws(duplicateOwner.Validate, "prefab rejects ambiguous unit ownership");
        var unsupported = (JsonObject)prefab.Models[0].Entity.DeepClone(); ((JsonArray)unsupported["components"]!).Add(new JsonObject { ["type"] = "UnknownLinkedBehavior" });
        throws(() => (prefab with { Models = [prefab.Models[0] with { Entity = unsupported }] }).Validate(), "unknown entity behaviors cannot be cloned with guessed references");
        string conflictPath = Path.Combine(root, "conflict.json"); File.WriteAllText(conflictPath, "{\"entities\":[],\"dependencies\":{\"models\":[{\"path\":\"data/hd/a.model\",\"customMetadata\":43}]}}");
        var conflictJson = PresetDocument.Load(conflictPath); var conflictMap = Ds1CollisionDocument.Create(Path.Combine(root, "conflict.ds1"), 20, 20, 1, 1); var conflictLinks = new PlacementLinks(conflictJson, conflictMap); var conflictBefore = conflictJson.Serialize();
        throws(() => conflictLinks.PlacePrefab(prefab, 3, 3, 0, 10, targetCatalog), "conflicting dependency declarations block placement");
        check(conflictJson.Serialize().SequenceEqual(conflictBefore) && conflictMap.Units.Count == 0 && !conflictJson.History.CanUndo, "dependency declaration conflict leaves scene and undo history intact");
    }
}
