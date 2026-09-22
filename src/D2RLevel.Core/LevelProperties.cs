namespace D2RLevel.Core;

public sealed record LevelPresetContext(GameDataRow Preset, GameDataRow? Level, GameDataRow? LevelType, string? Warning)
{
    public string DisplayName => $"{Preset["Name"]} · Def {Preset["Def"]} · LevelId {Preset["LevelId"]}";
    public string[] Variants => Enumerable.Range(1, 6).Select(i => Preset[$"File{i}"])
        .Where(v => v.Length > 0 && v != "0").ToArray();
}

/// <summary>Saved game-data context, independent of scene selection and of the renderer.</summary>
public sealed record LevelProperties(string? MapPath, IReadOnlyList<LevelPresetContext> Contexts,
    IReadOnlyList<GameDataTable> Sources, string? Warning, string? ProjectName)
{
    public static LevelProperties Load(string presetPath, string? mapPath, AssetResolver? assets)
    {
        var root = PresetPairing.Split(presetPath, "hd/env/preset")?.DataRoot
            ?? (mapPath is null ? null : PresetPairing.Split(mapPath, "global/tiles")?.DataRoot);
        var tables = new GameDataTables(assets, root);
        string? projectName = null;
        try { projectName = LevelProject.ForPreset(presetPath)?.Name; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { return new(mapPath, [], [], "Project context could not be read: " + ex.Message, null); }
        // Do not infer an area from a filename alone: standalone copies can share base map names.
        if (mapPath is null || PresetPairing.Split(mapPath, "global/tiles") is not { } location)
            return new(mapPath, [], [], "No paired DS1 with a game-relative path. Open a scene paired with a map under global/tiles.", projectName);
        var presets = tables.Read("lvlprest");
        string relative = Normalize(location.Relative);
        var matches = presets.Rows.Where(row => Enumerable.Range(1, 6).Any(i => Normalize(row[$"File{i}"]) == relative)).ToArray();
        var contexts = new List<LevelPresetContext>();
        foreach (var preset in matches)
        {
            GameDataRow? level = null, type = null;
            string? warning = null;
            string id = preset["LevelId"];
            if (id.Length == 0 || id == "0")
                warning = "This preset has no direct area assignment (LevelId 0 or blank). It may be a reusable generated room.";
            else
            {
                var levels = tables.Read("levels").Rows.Where(r => r["Id"] == id).ToArray();
                if (levels.Length != 1) warning = $"LevelId {id} resolves to {levels.Length} area rows; area properties are unavailable.";
                else
                {
                    level = levels[0];
                    var types = tables.Read("lvltypes").Rows.Where(r => level["LevelType"].Length > 0 && r["Id"] == level["LevelType"]).ToArray();
                    if (types.Length == 1) type = types[0];
                    else warning = $"LevelType {level["LevelType"]} resolves to {types.Length} tileset rows.";
                }
            }
            contexts.Add(new(preset, level, type, warning));
        }
        return new(mapPath, contexts.AsReadOnly(), tables.Loaded,
            presets.Warning is not null ? "Preset table unavailable: " + presets.Warning : matches.Length switch
            {
                0 => "No preset row references this DS1. An area assignment cannot be established.",
                > 1 => "Several preset rows reference this DS1. Choose a context to inspect; no area was selected automatically.",
                _ => null
            }, projectName);
    }

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').ToLowerInvariant();
    public static string DifficultyColumn(string column, int difficulty) => column + (difficulty switch
    { 0 => "", 1 => "(N)", 2 => "(H)", _ => throw new ArgumentOutOfRangeException(nameof(difficulty)) });
    public static IReadOnlyList<(string Column, string Value)> MonsterPool(GameDataRow level, int difficulty)
    {
        _ = DifficultyColumn("", difficulty);
        string prefix = difficulty == 0 ? "mon" : "nmon";
        return Enumerable.Range(1, 25).Select(i => (Column: prefix + i, Value: level[prefix + i]))
            .Where(p => p.Value.Length > 0 && p.Value != "0").ToArray();
    }
}
