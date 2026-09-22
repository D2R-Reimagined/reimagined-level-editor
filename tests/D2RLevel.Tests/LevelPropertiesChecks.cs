using D2RLevel.Core;

internal static class LevelPropertiesChecks
{
    public static void Run(string folder, Action<bool, string> check, Action<Action, string> throws)
    {
        string assets = Path.Combine(folder, "level-data-base"), mod = Path.Combine(folder, "level-data-mod");
        foreach (string root in new[] { assets, mod })
        {
            Directory.CreateDirectory(Path.Combine(root, "hd", "env", "preset", "act1"));
            Directory.CreateDirectory(Path.Combine(root, "global", "excel"));
            Directory.CreateDirectory(Path.Combine(root, "global", "tiles", "act1"));
        }
        var resolver = new AssetResolver(assets);
        string preset = Path.Combine(mod, "hd", "env", "preset", "act1", "arena.json");
        string map = Path.Combine(mod, "global", "tiles", "act1", "arena.ds1");
        void Table(string root, string name, string value) => File.WriteAllText(Path.Combine(root, "global", "excel", name + ".txt"), value);
        const string header = "Name\tDef\tLevelId\tFile1\tFile2\tDt1Mask\n";
        Table(assets, "lvlprest", header + "Arena\t7\t42\tAct1\\ARENA.DS1\tAct1/other.ds1\t1\n");
        Table(assets, "levels", "Name\tId\tAct\tLevelType\tMonLvlEx\tMonLvlEx(N)\tMonLvlEx(H)\tMonDen\tMonDen(N)\tMonDen(H)\tmon1\tnmon1\nBase area\t42\t0\t3\t10\t40\t80\t100\t200\t300\tfallen1\tfallen2\n");
        Table(assets, "lvltypes", "Name\tId\tFile 1\nCave\t3\tAct1/cave.dt1\n");
        var initial = LevelProperties.Load(preset, map, resolver);
        check(initial.Contexts is [{ Level: not null, LevelType: not null }] && initial.Contexts[0].Level!["Name"] == "Base area", "level properties resolve exact case-insensitive DS1 path through preset, area and tileset");
        check(initial.Contexts[0].Variants.Length == 2 && initial.Sources.All(s => !s.IsOverride), "level properties retain variants and base source provenance");
        Table(mod, "levels", "\uFEFFName\tId\tLevelType\tMonLvlEx\tMonLvlEx(N)\tMonLvlEx(H)\tMonDen\tMonDen(N)\tMonDen(H)\tmon1\tnmon1\nMod area\t42\t3\t11\t44\t88\t101\t202\t303\tzombie1\tzombie2\n");
        var updated = LevelProperties.Load(preset, map, resolver);
        var level = updated.Contexts.Single().Level!;
        check(level["Name"] == "Mod area" && level["Act"] == "" && level.Line == 2 && updated.Sources.Single(s => s.Name == "levels").IsOverride, "workspace table overrides whole base table with BOM and row provenance");
        check(level[LevelProperties.DifficultyColumn("MonLvlEx", 0)] == "11" && level[LevelProperties.DifficultyColumn("MonLvlEx", 1)] == "44" && level[LevelProperties.DifficultyColumn("MonLvlEx", 2)] == "88", "difficulty selects distinct expansion level columns");
        check(level[LevelProperties.DifficultyColumn("MonDen", 2)] == "303" && LevelProperties.MonsterPool(level, 0).Single().Value == "zombie1" && LevelProperties.MonsterPool(level, 1).Single().Value == "zombie2" && LevelProperties.MonsterPool(level, 2).Single().Column == "nmon1", "density uses difficulty suffix and Nightmare and Hell share nmon pool");
        check(initial.Contexts[0].Level!["Name"] == "Base area", "table refresh leaves previous snapshot unchanged");
        throws(() => LevelProperties.DifficultyColumn("MonDen", 3), "invalid difficulty rejected");
        Table(mod, "lvlprest", header + "Reusable room\t8\t0\tact1/arena.ds1\t0\t1\n");
        var reusable = LevelProperties.Load(preset, map, resolver);
        check(reusable.Contexts is [{ Level: null, Warning: not null }] && reusable.Sources.Count == 1, "LevelId zero does not resolve null area or infer an owner");
        Table(mod, "lvlprest", header + "First\t8\t42\tact1/arena.ds1\t0\t1\nSecond\t9\t0\tact1/arena.ds1\t0\t1\n");
        var ambiguous = LevelProperties.Load(preset, map, resolver);
        check(ambiguous.Contexts.Count == 2 && ambiguous.Warning is not null, "multiple preset references preserved as explicit contexts");
        Table(mod, "levels", "Name\tId\tLevelType\nA\t42\t3\nB\t42\t3\n");
        check(LevelProperties.Load(preset, map, resolver).Contexts[0] is { Level: null, Warning: not null }, "duplicate area IDs do not silently select a row");
        Table(mod, "levels", "Name\tId\tId\nInvalid\t42\t42\n");
        var malformed = LevelProperties.Load(preset, map, resolver);
        check(malformed.Contexts[0].Level is null && malformed.Sources.Single(s => s.Name == "levels").Warning is not null, "malformed override reports unavailable without falling back to base");
        Table(mod, "levels", "Name\tId\nA\t42\n");
        Table(mod, "lvltypes", "Name\tId\nUnassigned\t\n");
        check(LevelProperties.Load(preset, map, resolver).Contexts[0] is { Level: not null, LevelType: null, Warning: not null }, "missing tileset ID cannot match an empty table ID");
        var project = new LevelProject(1, Guid.NewGuid(), "Authored arena", "data/hd/env/preset/act1/arena.json", "data/global/tiles/act1/arena.ds1",
            8, 8, 1, new(1, ["data/global/tiles/act1/cave.dt1"]), "template.json", "");
        File.WriteAllText(preset + LevelProject.Suffix, System.Text.Json.JsonSerializer.Serialize(project));
        check(LevelProperties.Load(preset, map, resolver).ProjectName == "Authored arena", "authored project is identified separately from retained area context");
        File.WriteAllText(preset + LevelProject.Suffix, "{");
        check(LevelProperties.Load(preset, map, resolver) is { Contexts.Count: 0, Warning: not null }, "malformed authored context returns diagnostic without guessing");
        File.Delete(preset + LevelProject.Suffix);
        Table(mod, "lvlprest", header + "Other\t1\t42\tact2/arena.ds1\t0\t1\n");
        check(LevelProperties.Load(preset, map, resolver) is { Contexts.Count: 0, Warning: not null }, "same basename in another directory never matches");
        check(LevelProperties.Load(preset, Path.Combine(folder, "arena.ds1"), resolver) is { Contexts.Count: 0, Warning: not null }, "standalone map does not acquire a guessed area");
        check(LevelProperties.Load(preset, null, resolver) is { Contexts.Count: 0, Warning: not null }, "missing pair returns useful empty state");
        Table(mod, "lvlprest", "");
        check(LevelProperties.Load(preset, map, resolver) is { Contexts.Count: 0, Warning: not null }, "empty preset override returns diagnostics");
        check(LevelProperties.Load(preset, map, null).Sources.Single().IsOverride, "workspace tables can be read without base assets");
        throws(() => new GameDataTables(resolver, mod).Read("../levels"), "table reader rejects path traversal");
    }
}
