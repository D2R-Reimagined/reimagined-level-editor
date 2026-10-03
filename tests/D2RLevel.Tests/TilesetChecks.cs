using D2RLevel.Core;

/// <summary>Tileset-first level creation and the tileset palette: level types, their templates and models, and painted rows.</summary>
internal static class TilesetChecks
{
    public static void Run(string folder, Action<bool, string> check, Action<Action, string> throws)
    {
        string data = Path.Combine(folder, "tileset-assets"), workspace = Path.Combine(folder, "tileset-workspace");
        void Bytes(string root, string relative, byte[] bytes)
        {
            string path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
        }
        void Text(string root, string relative, string text) => Bytes(root, relative, System.Text.Encoding.UTF8.GetBytes(text));
        // An empty DT1 7.6 tile table is enough for floor and collision loading.
        var dt1 = new byte[276]; BitConverter.GetBytes(7).CopyTo(dt1, 0); BitConverter.GetBytes(6).CopyTo(dt1, 4); BitConverter.GetBytes(276).CopyTo(dt1, 272);
        Text(data, "global/excel/lvltypes.txt", "Name\tId\tFile 1\tFile 2\tFile 3\tAct\nNone\t0\t0\t0\t0\t0\n" +
            "Act 1 - Cave\t3\tAct1/Caves/Cave.dt1\tAct1/Caves/Missing.dt1\tAct1/Crypt/Floor.dt1\t1\nExpansion\t\t\t\t\t\nAct 5 - Ice\t33\tExpansion/IceCave/interior.dt1\t0\t0\t5\n");
        Text(data, "global/excel/levels.txt", "Name\tId\tLevelType\nNull\t0\t0\nCave Treasure\t13\t3\nTown\t1\t1\n");
        Text(data, "global/excel/lvlprest.txt", "Name\tDef\tLevelId\tFile1\tFile2\tDt1Mask\nTreasure\t1\t13\tAct1/Caves/CaveRoom2.ds1\t0\t1\n" +
            "Cave room\t2\t0\tAct1/Caves/CaveA.ds1\tAct1/Caves/CaveRoom2.ds1\t5\nTown\t3\t1\tAct1/Town/TownE1.ds1\t0\t1\nNo preset\t4\t0\tAct1/Caves/Orphan.ds1\t0\t1\nOther folder\t5\t0\tAct1/Outdoors/Field.ds1\t0\t1\n");
        foreach (var (relative, width, height) in new[] { ("act1/caves/caveroom2", 25, 25), ("act1/caves/cavea", 9, 13), ("act1/town/towne1", 56, 40), ("act1/caves/orphan", 8, 8), ("act1/outdoors/field", 8, 8) })
        {
            Bytes(data, $"global/tiles/{relative}.ds1", Ds1CollisionDocument.Create("unused.ds1", width, height, 1).Serialize());
            if (!relative.EndsWith("orphan")) Text(data, $"hd/env/preset/{relative}.json", "{\"entities\":[]}");
        }
        Bytes(data, "global/tiles/act1/caves/cave.dt1", dt1); Bytes(data, "global/tiles/act1/crypt/floor.dt1", dt1);
        Bytes(data, "global/palette/act1/pal.dat", new byte[768]);
        foreach (string model in new[] { "act1/caves/cave_wall01_lod0", "act1/caves/cave_wall01_lod1", "act1/caves/sub/stairs_down", "act1/crypt/crypt_pillar_lod0", "act1/town/town_house_lod0" })
            Text(data, $"hd/env/model/{model}.model", "");
        Text(workspace, "hd/env/model/act1/caves/mod_rock_lod0.model", "");

        var assets = new AssetResolver(data); var tables = new GameDataTables(assets, null);
        var types = TilesetCatalog.LevelTypes(tables);
        check(types.Select(t => t.Id).SequenceEqual(new[] { 3, 33 }) && types[0] is { Name: "Act 1 - Cave", Act: 1 }
            && types[0].Folders.SequenceEqual(new[] { "act1/caves", "act1/crypt" }) && types[1].Folders.SequenceEqual(new[] { "expansion/icecave" }),
            "level types skip separators and list their DT1 folders");
        var cave = types[0];
        var templates = TilesetCatalog.Templates(tables, assets, null, cave);
        check(templates.Select(t => t.Ds1Relative).SequenceEqual(new[] { "act1/caves/caveroom2.ds1", "act1/caves/cavea.ds1" }),
            "templates list the level's preset first, then rooms in the tileset's folders, leaving out other levels, other folders and unpaired maps");
        check(templates[0] is { IsLevel: true, LevelId: 13, LevelName: "Cave Treasure", Width: 25, Height: 25, Dt1Mask: 1 }
            && templates[1] is { IsLevel: false, Width: 9, Height: 13, Dt1Mask: 5, Name: "Cave room · 1/2" }, "a template carries its level, size and mask, and names its variant");
        bool Exists(string logical) => File.Exists(assets.Resolve(logical));
        var tileset = TilesetCatalog.Tileset(cave, 5, Exists);
        check(tileset.Mask == 5 && tileset.Files.SequenceEqual(new[] { "data/global/tiles/Act1/Caves/Cave.dt1", "data/global/tiles/Act1/Crypt/Floor.dt1" }),
            "a template's Dt1Mask selects the type's DT1 files");
        check(TilesetCatalog.Tileset(cave, 0, Exists).Files.Length == 2 && TilesetCatalog.Tileset(cave, 3, Exists).Files.Length == 1,
            "an empty mask selects every file, and missing files are left out");
        throws(() => TilesetCatalog.Tileset(cave, 2, Exists), "a tileset with no available DT1 files is rejected");
        var scene = LegacyFloorScene.Load(templates[1].MapPath, assets, CancellationToken.None, null, tileset, TilesetSource.Chosen);
        check(scene.TilesetSource == TilesetSource.Chosen && scene.Mask == 5 && scene.Dt1Paths.Length == 2, "a template loads with the chosen tileset instead of its own context");
        string destination = Path.Combine(folder, "tileset-project");
        var created = LevelProject.CreateFromTemplate(destination, "Cave arena", PresetDocument.Load(templates[1].PresetPath), scene, 0, null, assets, false, cave);
        var project = LevelProject.ForPreset(created.JsonPath)!;
        check(project is { LevelType: 3, LevelTypeName: "Act 1 - Cave", Width: 9, Height: 13 } && project.Tileset.Mask == 5 && project.Tileset.Files.Length == 2,
            "a new level records its level type and freezes the chosen tileset");

        var models = TilesetCatalog.Models(assets, cave.Folders, workspace);
        check(models.Select(m => m.Path).SequenceEqual(new[] { "data/hd/env/model/act1/caves/cave_wall01.model", "data/hd/env/model/act1/caves/mod_rock.model",
            "data/hd/env/model/act1/caves/sub/stairs_down.model", "data/hd/env/model/act1/crypt/crypt_pillar.model" }),
            "tileset models come from the matching folders and the workspace, one logical name per model");
        check(models.Select(TilesetCatalog.Kind).SequenceEqual(new[] { ModelKind.Walls, ModelKind.Nature, ModelKind.Passages, ModelKind.Pillars }), "palette models are grouped by kind from their names");
        check(TilesetCatalog.Folders(["C:/x/data/global/tiles/Act1/Caves/Cave.dt1", "data/global/tiles/act1/caves/other.dt1"]).SequenceEqual(new[] { "act1/caves" }),
            "physical and logical DT1 paths name the same folder");
        Text(workspace, "hd/env/preset/act1/caves/cavea.json", "{\"entities\":[]}");
        check(TilesetCatalog.Templates(tables, assets, workspace, cave)[1].PresetPath.StartsWith(Path.GetFullPath(workspace), StringComparison.OrdinalIgnoreCase),
            "a workspace template preset wins over the asset folder");

        // A row painted from the palette is one edit with unique, rotated entities.
        var doc = PresetDocument.Load(templates[1].PresetPath);
        var quarter = new Quaterniond(0, Math.Sin(Math.PI / 4), 0, Math.Cos(Math.PI / 4));
        var row = doc.PlaceModels("data/hd/env/model/act1/caves/cave_wall01.model", [new(new(10, 0, 0), quarter, new(1, 1, 1)), new(new(20, 0, 0), quarter, new(1, 1, 1))]);
        check(row.Added.Length == 2 && doc.Entities.Count == 2 && row.Added.Select(e => e.Name).SequenceEqual(new[] { "cave_wall01", "cave_wall01_1" })
            && row.Added[0].Id != row.Added[1].Id && row.Added[1].Transform.Position.X == 20 && row.Added[0].Transform.Orientation == quarter,
            "a painted row places named, rotated copies with unique IDs");
        check(doc.AssetPaths().Contains("data/hd/env/model/act1/caves/cave_wall01.model"), "a painted row lists its model as a dependency");
        check(doc.History.Undo() is ModelPlacement undone && undone == row && doc.Entities.Count == 0 && !doc.IsDirty, "one undo removes the whole painted row");
        doc.History.Redo(); check(doc.Entities.Count == 2, "redo restores the painted row");
        throws(() => doc.PlaceModels("data/hd/x.model", []), "an empty painted row is rejected");
        throws(() => doc.PlaceModels("data/hd/x.model", [new(new(double.NaN, 0, 0), new(0, 0, 0, 1), new(1, 1, 1))]), "an invalid transform rejects the painted row");
        check(doc.Entities.Count == 2, "rejected painted rows leave the document unchanged");
        var single = doc.AddModel("data/hd/env/model/act1/caves/cave_wall01.model", new(1, 2, 3));
        check(single.Name == "cave_wall01_2" && doc.History.Undo() == (object)single && doc.Entities.Count == 2, "adding one model keeps its own history subject");
    }
}
