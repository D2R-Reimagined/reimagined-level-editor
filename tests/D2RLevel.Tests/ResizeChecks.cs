using System.Text.Json;
using D2RLevel.Core;

internal static class ResizeChecks
{
    public static void Run(string folder, Action<bool, string> check, Action<Action, string> throws)
    {
        string root = Path.Combine(folder, "resize"); Directory.CreateDirectory(root);
        uint grass = Ds1CollisionDocument.FloorKey(3, 7);

        // The fixture is 4 × 4 with tag groups, two units and a patrol anchored on the first.
        var (_, ds1, _) = LinkFixture.Create(root);
        var before = ds1.Serialize();
        var grown = ds1.Grown(new(2, 1, 3, 0), GroundFill.Tile, grass);
        check(grown.Width == 9 && grown.Height == 5 && ds1.Serialize().SequenceEqual(before), "grow builds a new map without touching the source");
        check(grown.Cell(grown.Floors[0], 2, 1) == ds1.Cell(ds1.Floors[0], 0, 0) && grown.Cell(grown.Floors[0], 5, 4) == ds1.Cell(ds1.Floors[0], 3, 3),
            "existing floors keep their shifted positions");
        check(grown.Cell(grown.Floors[0], 0, 0) == grass && grown.Cell(grown.Floors[0], 8, 4) == grass && grown.Cell(grown.Walls[0], 0, 0) == 0, "new ground is filled and new walls are empty");
        check(grown.Units[0] with { X = grown.Units[0].X - 10, Y = grown.Units[0].Y - 5 } == ds1.Units[0] && grown.Units[1].X == ds1.Units[1].X + 10,
            "placements shift with the X start and Y start edges");
        check(grown.PatrolPoints(0).SequenceEqual(ds1.PatrolPoints(0).Select(p => p with { X = p.X + 10, Y = p.Y + 5 })), "patrol routes shift with their unit");
        check(Ds1CollisionDocument.Load(Save(root, "grown.ds1", grown)).Serialize().SequenceEqual(grown.Serialize()) && grown.GameplayWarning is null, "grown map reloads byte-exactly");

        var appended = ds1.Grown(new(0, 0, 1, 2), GroundFill.ExtendEdges);
        check(appended.Units.SequenceEqual(ds1.Units) && appended.Cell(appended.Floors[0], 4, 5) == Ds1CollisionDocument.FloorKey(0, 0),
            "end edges keep coordinates and extend the nearest edge ground");
        ds1.Paint([(3, 3)], true);
        var extended = ds1.Grown(new(0, 0, 1, 0), GroundFill.ExtendEdges);
        check((extended.Cell(extended.Floors[0], 4, 3) & Ds1CollisionDocument.Unwalkable) == 0 && extended.HasOverride(3, 3), "extended ground does not copy blocking overrides");
        ds1.Undo();
        var empty = ds1.Grown(new(0, 0, 1, 1), GroundFill.Empty);
        check(empty.Cell(empty.Floors[0], 4, 4) == 0, "new ground can be left empty");
        throws(() => ds1.Grown(new(0, 0, 0, 0), GroundFill.Empty), "resize must change a tile");
        throws(() => ds1.Grown(new(0, 0, 509, 0), GroundFill.Empty), "resize respects the 512-tile limit");
        throws(() => ds1.Grown(new(-2, 0, -2, 0), GroundFill.Empty), "crop keeps at least one tile");

        // Cropping: the fixture's units sit on tiles (1, 1) and (2, 2); the patrol starts on the first and walks within tile 1.
        var trimmed = ds1.Grown(new(-1, 0, 0, 0), GroundFill.Empty, 0, out var kept1);
        check(trimmed.Width == 3 && trimmed.Height == 4 && trimmed.Cell(trimmed.Floors[0], 0, 0) == ds1.Cell(ds1.Floors[0], 1, 0) && kept1.SequenceEqual([0, 1]),
            "cropping X start drops the first column and shifts the rest");
        check(trimmed.Units[0] == ds1.Units[0] with { X = ds1.Units[0].X - 5 } && trimmed.PatrolPoints(0).SequenceEqual(ds1.PatrolPoints(0).Select(p => p with { X = p.X - 5 })),
            "placements and patrols shift back with a cropped start edge");
        check(Ds1CollisionDocument.Load(Save(root, "trimmed.ds1", trimmed)).Serialize().SequenceEqual(trimmed.Serialize()) && trimmed.GameplayWarning is null, "cropped map reloads byte-exactly");
        var narrowed = ds1.Grown(new(0, 0, -2, 0), GroundFill.Empty, 0, out var kept2);
        check(ds1.Loss(new(0, 0, -2, 0)) == new ResizeLoss(1, 0, 0, 0, 0) && narrowed.Units.Count == 1 && kept2.SequenceEqual([0, -1]) && narrowed.PatrolPoints(0).Count == 2,
            "placements on removed tiles are dropped and reported");
        var flat = ds1.Grown(new(0, 0, 0, -3), GroundFill.Empty);
        check(ds1.Loss(new(0, 0, 0, -3)) == new ResizeLoss(2, 1, 2, 0, 0) && flat.Units.Count == 0 && flat.Height == 1 && flat.GameplayWarning is null,
            "a patrol whose start is removed goes with it");
        ds1.InsertPathPoint(0, 2, 17, 17, 0);
        check(ds1.Loss(new(0, 0, -1, -1)) == new ResizeLoss(0, 0, 1, 0, 0) && ds1.Grown(new(0, 0, -1, -1), GroundFill.Empty).PatrolPoints(0).Count == 2,
            "patrol points on removed tiles are trimmed from a surviving route");
        ds1.Undo();
        check(ds1.Grown(new(-1, 0, 2, 0), GroundFill.Empty).Width == 5, "one edge can grow while another shrinks");
        throws(() => ds1.Grown(new(0, 0, 1, 0), GroundFill.Tile, 0), "tile fill requires a tile");

        // A saved level project with a terrain mesh, a linked model and owned collision.
        string data = Path.Combine(root, "project", "data");
        string jsonPath = Path.Combine(data, "hd", "env", "preset", "test", "arena.json"), mapPath = Path.Combine(data, "global", "tiles", "test", "arena.ds1");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!); Directory.CreateDirectory(Path.GetDirectoryName(mapPath)!);
        File.WriteAllBytes(mapPath, before);
        File.WriteAllText(jsonPath, """
            {"entities":[
              {"type":"Entity","name":"terrain","id":1,"components":[{"type":"TransformDefinitionComponent","position":{"x":0,"y":0,"z":0}},
                {"type":"TerrainDefinitionComponent"},{"type":"ModelDefinitionComponent","filename":"data/hd/t/terrain.model"}]},
              {"type":"Entity","name":"rock","id":2,"components":[{"type":"TransformDefinitionComponent","position":{"x":10,"y":0,"z":10}},
                {"type":"ModelDefinitionComponent","filename":"data/hd/a.model"}]}]}
            """);
        var project = new LevelProject(1, Guid.NewGuid(), "Arena", "data/hd/env/preset/test/arena.json", "data/global/tiles/test/arena.ds1", 4, 4, 1,
            new(1, ["data/global/tiles/act1/test.dt1"]), "data/hd/env/preset/test/arena.json", "00");
        File.WriteAllText(jsonPath + LevelProject.Suffix, JsonSerializer.Serialize(project));
        var json = PresetDocument.Load(jsonPath); var map = Ds1CollisionDocument.Load(mapPath);
        var links = new PlacementLinks(json, map); links.ConnectWorkspace();
        links.LinkUnit(json.Entities[1], 1, 10); links.LinkFootprint(json.Entities[1], [new(1, 1)], 10, false);
        File.WriteAllBytes(mapPath, map.Serialize()); links.SaveMetadata();
        var scene = new WorkspaceScene("Arena", jsonPath, mapPath);
        var unitBefore = map.Units[1];

        var result = LevelResizer.Apply(data, scene, new(2, 1, 0, 3), GroundFill.Tile, grass, true);
        var json2 = PresetDocument.Load(jsonPath); var map2 = Ds1CollisionDocument.Load(mapPath); var links2 = new PlacementLinks(json2, map2);
        check(result.Width == 6 && result.Height == 8 && map2.Width == 6 && map2.Height == 8, "resize writes the grown map");
        check(LevelProject.ForPreset(jsonPath) is { Width: 6, Height: 8 }, "level project records the new size");
        var rock = json2.Entities.Single(e => e.Name == "rock");
        check(rock.Transform.Position == new Vector3d(30, 0, 20) && json2.Entities[0].Transform.Position == new Vector3d(20, 0, 10), "HD objects and terrain shift by whole tiles");
        check(links2.Warning is null && links2.BrokenLinkCount == 0 && links2.CollisionTiles(rock).SequenceEqual([new LinkedTile(3, 2)]),
            "links stay intact and footprints follow the shifted ground");
        check(map2.Units[1].X == unitBefore.X + 10 && map2.Units[1].Y == unitBefore.Y + 5 && map2.HasOverride(3, 2) && !map2.HasOverride(1, 1), "linked unit and owned collision move together");
        // Copies cover one span west, one north and one south of the original: 2 columns × 3 rows, less the original.
        var copies = json2.Entities.Where(e => e.IsTerrain).Skip(1).Select(e => e.Transform.Position).ToHashSet();
        check(result.TerrainCopies == 5 && copies.SetEquals(new Vector3d[] { new(-20, 0, -30), new(20, 0, -30), new(-20, 0, 10), new(-20, 0, 50), new(20, 0, 50) }),
            "terrain repeats edge to edge into the new space");
        check(File.Exists(jsonPath + ".bak") && File.Exists(mapPath + ".bak") && File.Exists(jsonPath + PlacementLinks.Suffix + ".bak"), "previous scene files are kept as .bak");

        // A mesh narrower than the map is laid at its own width, so copies meet instead of leaving strips between them.
        var narrow = new TerrainExtent(0, 0, 30, 40);
        var cover = TerrainTiling.CoverOffsets(narrow, [], 8, 4, 10);
        check(cover.SequenceEqual([new Vector3d(30, 0, 0), new Vector3d(60, 0, 0)]), "terrain copies are spaced by the mesh, not the map");
        check(TerrainTiling.CoverOffsets(narrow, [narrow.Offset(30, 0), narrow.Offset(60, 0)], 8, 4, 10).Length == 0, "covered maps need no copies");
        // An earlier resize spaced one copy by the map width (40) instead of the mesh width (30).
        var off = narrow.Offset(40, 0);
        check(TerrainTiling.Misaligned(narrow, [("aligned", narrow.Offset(30, 0)), ("off", off), ("stacked", narrow.Offset(30, 0)), ("shifted", narrow.Offset(-60, 40))])
            .SequenceEqual(["off", "stacked"]), "copies off the mesh grid or on another copy are misaligned");
        check(TerrainTiling.CoverOffsets(narrow, [narrow.Offset(30, 0), off], 8, 4, 10).SequenceEqual([new Vector3d(60, 0, 0)]), "gap filling counts copies it is told to keep");

        // Repair as one edit: remove the misaligned copy, lay grid copies, and undo both together.
        var repairJson = PresetDocument.Load(jsonPath);
        var terrain = repairJson.TerrainEntities();
        var repair = repairJson.RepairTerrain(terrain[0], [terrain[1]], [new Vector3d(-40, 0, 0)]);
        check(repair.Added.Length == 1 && repair.Removed.Length == 1 && repairJson.TerrainEntities().Length == terrain.Length
            && repairJson.TerrainEntities()[^1].Transform.Position == terrain[0].Transform.Position with { X = terrain[0].Transform.Position.X - 40 }, "terrain repair swaps copies in one edit");
        repairJson.Undo();
        check(repairJson.TerrainEntities().SequenceEqual(terrain) && !repairJson.IsDirty, "undo restores the original terrain copies");
        throws(() => repairJson.RepairTerrain(terrain[0], [terrain[0]], []), "terrain repair never removes its source");

        // Crop away the column holding the rock's owned collision: the link is dropped, the rock stays, the unit survives.
        var cropped = LevelResizer.Apply(data, scene, new(-4, 0, 0, 0), GroundFill.Empty, 0, false);
        var json3 = PresetDocument.Load(jsonPath); var map3 = Ds1CollisionDocument.Load(mapPath); var links3 = new PlacementLinks(json3, map3);
        check(cropped.Width == 2 && cropped.Height == 8 && cropped.UnlinkedObjects == 1 && LevelProject.ForPreset(jsonPath) is { Width: 2, Height: 8 }, "crop writes the smaller map and project");
        check(links3.Warning is null && links3.Links.Count == 0 && json3.Entities.Single(e => e.Name == "rock").Transform.Position == new Vector3d(-10, 0, 20),
            "links on removed tiles are dropped and their objects shift back but stay");
        check(map3.Units.Count == 1 && map3.Units[0] == map2.Units[1] with { Index = 0, X = map2.Units[1].X - 20 }, "placements on kept tiles survive the crop");

        File.WriteAllText(jsonPath + LevelProject.Suffix, "{}");
        var unchanged = File.ReadAllBytes(mapPath);
        throws(() => LevelResizer.Apply(data, scene, new(0, 0, 1, 0), GroundFill.Empty, 0, false), "invalid project stops the resize");
        check(File.ReadAllBytes(mapPath).SequenceEqual(unchanged), "a refused resize leaves the scene untouched");
    }

    private static string Save(string root, string name, Ds1CollisionDocument map)
    {
        string path = Path.Combine(root, name); File.WriteAllBytes(path, map.Serialize()); return path;
    }
}
