using System.Text.Json;
using D2RLevel.Core;

internal static class AuthoringChecks
{
    public static void Run(string folder, Action<bool, string> check, Action<Action, string> throws)
    {
        string root = Path.Combine(folder, "authoring"); Directory.CreateDirectory(root);
        uint a = Ds1CollisionDocument.FloorKey(3, 7), b = Ds1CollisionDocument.FloorKey(5, 9);
        var fresh = Ds1CollisionDocument.Create(Path.Combine(root, "not-yet-created.ds1"), 8, 6, 2, a, 2, 3);
        check(!File.Exists(fresh.SourcePath) && fresh.Width == 8 && fresh.Height == 6 && fresh.Act == 2 && fresh.Floors.Count == 2 && fresh.Walls.Count == 3, "construct DS1 without input file");
        check(fresh.GameplayWarning is null && fresh.Units.Count == 0 && fresh.Cell(fresh.Floors[1], 0, 0) == 0, "new DS1 has empty gameplay and extra layers");
        var before = fresh.Serialize();
        fresh.PaintFloor(0, [(0, 0), (1, 0), (1, 0)], b);
        check(fresh.Cell(fresh.Floors[0], 1, 0) == b, "paint actual tile keys");
        fresh.Undo(); check(fresh.Serialize().SequenceEqual(before), "one undo restores entire floor stroke");
        fresh.Redo(); check(fresh.Cell(fresh.Floors[0], 1, 0) == b, "redo floor stroke"); fresh.Undo();
        throws(() => fresh.PaintFloor(0, [(0, 0), (8, 0)], b), "invalid destination rejects complete stroke");
        check(fresh.Serialize().SequenceEqual(before), "rejected stroke has no partial mutation");
        fresh.Paint([(0, 0)], true); fresh.PaintFloor(0, [(0, 0)], b);
        check(fresh.HasOverride(0, 0), "replacing tile retains explicit blocking"); fresh.Undo(); fresh.Undo();
        fresh.PaintFloor(0, Enumerable.Range(0, 6).Select(y => (4, y)), b);
        check(GroundBrush.Flood(fresh, 0, 0, 0).Length == 24, "fill stops at tile boundary");
        fresh.Undo();
        fresh.SaveCopy(Path.Combine(root, "created.ds1"));
        check(Ds1CollisionDocument.Load(Path.Combine(root, "created.ds1")).Serialize().SequenceEqual(before), "created DS1 save and reload");
        throws(() => Ds1CollisionDocument.Create("unused", 0, 2, 1), "invalid map bounds rejected");
        throws(() => Ds1CollisionDocument.FloorKey(64, 0), "invalid floor key rejected");

        var (json, ds1, links) = LinkFixture.Create(root);
        check(json.AuthoringScaffold("new.json").Entities.Count == 0 && json.Entities.Count == 2, "blank scaffold removes known standalone scenery without changing source");
        check(json.AuthoringScaffold("new.json", true).Entities.Count == 2, "scenery can be retained explicitly");
        links.ConnectWorkspace();
        links.LinkUnit(json.Entities[1], 1, 10);
        var original = ds1.Serialize(); var metadata = links.MetadataFor(links.SidecarPath, ds1.SourcePath);
        int added = links.AppendUnit(1, 7, 1, 1, 123);
        check(added == 2 && ds1.Units.Count == 3 && links.Find(json.Entities[1])?.Unit?.Index == 1, "appending unit retains existing indices");
        check(links.MetadataFor(links.SidecarPath, ds1.SourcePath).Length > 0, "append refreshes fingerprint");
        ds1.Undo();
        check(ds1.Serialize().SequenceEqual(original) && links.MetadataFor(links.SidecarPath, ds1.SourcePath).SequenceEqual(metadata), "append undo restores bytes and metadata");
        ds1.Redo(); links.DeleteUnit(0);
        check(links.Find(json.Entities[1])?.Unit?.Index == 0 && ds1.Units.Count == 2, "delete remaps later linked units");
        ds1.Undo(); ds1.Undo();
        check(ds1.Serialize().SequenceEqual(original), "structural undo retains original patrols and records");
        throws(() => links.AppendUnit(1, 7, 5, 6), "insert cannot create ambiguous patrol anchor");
        throws(() => links.DeleteUnit(1), "linked gameplay deletion requires unlink");
        links.PaintFloor(0, [(0, 0)], b);
        check(links.MetadataFor(links.SidecarPath, ds1.SourcePath).Length > 0 && links.BrokenLinkCount == 0, "floor paint preserves unrelated links");
        ds1.Undo(); check(ds1.Serialize().SequenceEqual(original), "paired floor undo restores bytes");
        links.LinkFootprint(json.Entities[0], [new(0, 0)], 10, false);
        var protectedBytes = ds1.Serialize();
        throws(() => links.PaintFloor(0, [(1, 1), (0, 0)], 0), "owned footprint blocks floor erase");
        check(ds1.Serialize().SequenceEqual(protectedBytes), "protected floor rejection is atomic");

        // Creation stages a complete project; the source and any existing destination remain untouched.
        string data = Path.Combine(root, "template", "data"), preset = Path.Combine(data, "hd", "env", "preset", "act2", "test.json");
        string map = Path.Combine(data, "global", "tiles", "act2", "test.ds1"), dt1 = Path.Combine(data, "global", "tiles", "act2", "test.dt1");
        Directory.CreateDirectory(Path.GetDirectoryName(preset)!); Directory.CreateDirectory(Path.GetDirectoryName(map)!);
        File.WriteAllText(preset, "{\"entities\":[],\"preserve\":42}"); File.WriteAllBytes(map, before);
        File.WriteAllBytes(dt1, [0, 1, 2]); // The scene fixture supplies decoded tiles; creation copies the effective file verbatim.
        var sourceDoc = Ds1CollisionDocument.Load(map);
        var source = new LegacyFloorScene(Ds1Floors.Load(map), new Dictionary<(int, int), Dt1Floor[]> { [(3, 7)] = [new(dt1, 3, 7, 1, new byte[12800])] }, new byte[768], map, [dt1], 1) { Collision = new(sourceDoc, []) };
        string destination = Path.Combine(root, "my-arena");
        var created = LevelProject.CreateFromTemplate(destination, "My arena", PresetDocument.Load(preset), source, a, GridCalibration.Typed(12));
        var project = LevelProject.ForPreset(created.JsonPath)!;
        check(project.Name == "My arena" && project.Width == 8 && project.Tileset.Files[0] == "data/global/tiles/act2/test.dt1", "portable project metadata preserves explicit context");
        check(File.ReadAllBytes(Path.Combine(destination, project.Tileset.Files[0])).SequenceEqual(File.ReadAllBytes(dt1)), "project carries effective DT1 override");
        check(File.ReadAllText(created.JsonPath).Contains("42") && File.ReadAllBytes(map).SequenceEqual(before), "project retains unknown JSON and preserves template");
        var createdJson = PresetDocument.Load(created.JsonPath); var createdMap = Ds1CollisionDocument.Load(created.Ds1Path);
        var createdLinks = new PlacementLinks(createdJson, createdMap); createdLinks.ConnectWorkspace();
        check(createdLinks.Calibration.UnitsPerTile == 12, "project inherits template grid calibration");
        check(SceneWorkspace.Scan(Path.Combine(destination, "data")).Length == 1, "project metadata not discovered as extra scene");
        var session = new WorkspaceSceneSession(Path.Combine(destination, "data"), created);
        check(session.Scene.JsonPath == createdJson.SourcePath, "project session uses canonical document paths");
        int firstUnit = createdLinks.AppendUnit(1, 0, 2, 2);
        createdMap.InsertPathPoint(firstUnit, 0, 3, 3, 1);
        check(createdMap.PatrolPoints(firstUnit).Count == 1, "first unit and first patrol can be authored on blank map");
        createdLinks.PaintFloor(0, [(0, 0)], b); session.Save(createdJson, createdMap, createdLinks);
        var reopenedJson = PresetDocument.Load(created.JsonPath); var reopenedMap = Ds1CollisionDocument.Load(created.Ds1Path);
        check(new PlacementLinks(reopenedJson, reopenedMap).Warning is null && reopenedMap.Cell(reopenedMap.Floors[0], 0, 0) == b, "authored scene and links survive workspace save/reopen");
        throws(() => LevelProject.CreateFromTemplate(destination, "Again", PresetDocument.Load(preset), source, a), "new project never overwrites existing destination");
        check(!Directory.EnumerateDirectories(root, ".rle-create-*").Any(), "creation leaves no staging directories");
        File.AppendAllText(created.JsonPath + LevelProject.Suffix, " ");
        throws(() => session.Save(createdJson, createdMap, createdLinks), "externally modified project prevents save");
        throws(() => (project with { Tileset = new(1, ["data/global/tiles/../escape.dt1"]) }).Validate(), "reject traversal in explicit tileset");
        string empty = Path.Combine(root, "empty"); Directory.CreateDirectory(empty);
        check(SceneWorkspace.Scan(empty).Length == 0, "empty workspace can be opened");
    }
}
