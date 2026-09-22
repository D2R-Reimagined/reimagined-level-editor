namespace D2RLevel.Core;

public enum GameplayAssetKind { Monster, Npc, Superunique, Object, Spawn }

public sealed record GameplayAsset(string Key, string Name, GameplayAssetKind Kind, int Act, int Type,
    int? Id, string? MonsterClass, IReadOnlyList<GameDataRow> References, string? UnavailableReason)
{
    public bool CanPlace => Id is >= 0 && UnavailableReason is null;
    public string DisplayName => Name.Equals(Key, StringComparison.OrdinalIgnoreCase) ? Key : $"{Name} · {Key}";
    public bool Matches(string search) => (DisplayName + " " + Kind + " " + Id).Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Act-specific DS1 recipes from effective saved tables. Never substitutes a definition row ID for a preset ID.</summary>
public sealed record GameplayCatalog(int Act, IReadOnlyList<GameplayAsset> Assets, IReadOnlyList<GameDataTable> Sources)
{
    public static GameplayCatalog Load(AssetResolver? assets, string? workspaceRoot, int act)
    {
        if (act is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(act));
        var data = new GameDataTables(assets, workspaceRoot);
        var monpreset = data.Read("monpreset"); var monstats = data.Read("monstats");
        var supers = data.Read("superuniques"); var places = data.Read("monplace");
        var objpreset = data.Read("objpreset"); var objects = data.Read("objects");
        static Dictionary<string, GameDataRow[]> Index(GameDataTable table, string key) => table.Rows
            .Where(r => r[key].Length > 0).GroupBy(r => r[key], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var monsters = Index(monstats, "Id"); var unique = Index(supers, "Superunique");
        var objectClasses = Index(objects, "Class"); var spawnCodes = Index(places, "code");
        var result = new List<GameplayAsset>(); var seen = new HashSet<(GameplayAssetKind, string)>();
        GameDataRow? One(Dictionary<string, GameDataRow[]> index, string key) => index.TryGetValue(key, out var rows) && rows.Length == 1 ? rows[0] : null;
        void Monster(string key, int? id, GameDataRow? preset, GameplayAssetKind? forced = null)
        {
            var refs = new List<GameDataRow>(); if (preset is not null) refs.Add(preset);
            GameplayAssetKind kind = forced ?? (monsters.ContainsKey(key) ? GameplayAssetKind.Monster : unique.ContainsKey(key) ? GameplayAssetKind.Superunique : GameplayAssetKind.Spawn);
            string name = key, monsterClass = key; string? warning = null;
            GameDataRow? monster = null;
            if (forced is null && monsters.ContainsKey(key) && unique.ContainsKey(key))
                warning = "This key matches both a monster and a superunique; the placement is ambiguous.";
            if (kind == GameplayAssetKind.Superunique)
            {
                var row = One(unique, key);
                if (row is null) warning ??= "Superunique definition is missing or duplicated.";
                else { refs.Add(row); name = row["Name"].Length > 0 ? row["Name"] : key; monsterClass = row["Class"]; monster = One(monsters, monsterClass); }
            }
            else if (kind == GameplayAssetKind.Monster) monster = One(monsters, key);
            else
            {
                var spawn = One(spawnCodes, key);
                if (spawn is not null) refs.Add(spawn);
                // Runtime spawn codes can depend on hardcoded special placement rules. Browse only.
                warning ??= spawn is null ? "Preset key has no resolved monster, superunique or spawn definition." : "Runtime spawn marker; placement rules are not supported by this browser.";
            }
            if (kind != GameplayAssetKind.Spawn)
            {
                if (monster is null) warning ??= "Monster definition is missing or duplicated.";
                else
                {
                    refs.Add(monster);
                    if (kind != GameplayAssetKind.Superunique)
                    {
                        kind = monster["npc"] == "1" ? GameplayAssetKind.Npc : GameplayAssetKind.Monster;
                        name = monster["NameStr"].Length > 0 ? monster["NameStr"] : key;
                    }
                    if (monster["enabled"] != "1") warning ??= "Monster is not enabled in MonStats.";
                }
            }
            if (id is null) warning ??= "No monster preset mapping for this act. Add a mapping in game data before placing.";
            if (key.Length == 0) { name = "Empty monster preset"; warning = "Empty preset slot; preserved for ID indexing."; }
            seen.Add((kind, key.ToLowerInvariant()));
            result.Add(new(key, name, kind, act, 1, id, monster is null ? null : monsterClass, refs.AsReadOnly(), warning));
        }
        // Blank Place cells still consume a slot. Rows from other acts do not.
        var actMonsters = monpreset.Rows.Where(r => int.TryParse(r["Act"], out int a) && a == act).ToArray();
        for (int slot = 0; slot < actMonsters.Length; slot++) Monster(actMonsters[slot]["Place"], slot, actMonsters[slot]);
        foreach (var (key, rows) in monsters)
        {
            var kind = rows.Length == 1 && rows[0]["npc"] == "1" ? GameplayAssetKind.Npc : GameplayAssetKind.Monster;
            if (!seen.Contains((kind, key.ToLowerInvariant()))) Monster(key, null, null, GameplayAssetKind.Monster);
        }
        foreach (string key in unique.Keys)
            if (!seen.Contains((GameplayAssetKind.Superunique, key.ToLowerInvariant()))) Monster(key, null, null, GameplayAssetKind.Superunique);

        var actObjects = objpreset.Rows.Where(r => int.TryParse(r["Act"], out int a) && a == act).ToArray();
        var indexes = actObjects.Where(r => int.TryParse(r["Index"], out int i) && i >= 0)
            .GroupBy(r => int.Parse(r["Index"])).ToDictionary(g => g.Key, g => g.Count());
        var usedObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Object(string key, GameDataRow? preset)
        {
            var refs = new List<GameDataRow>(); if (preset is not null) refs.Add(preset);
            int? id = preset is not null && int.TryParse(preset["Index"], out int index) && index >= 0 ? index : null;
            var row = One(objectClasses, key);
            string? warning = row is null ? "Object class is missing or duplicated." : null;
            if (row is not null) refs.Add(row);
            if (id is null) warning ??= "No valid object preset index for this act. Add a mapping in game data before placing.";
            else if (indexes[id.Value] != 1) warning = "Duplicate object preset index in this act; placement is ambiguous.";
            string name = row is null ? key : row["*Description"].Length > 0 ? row["*Description"] : row["Name"].Length > 0 ? row["Name"] : key;
            result.Add(new(key, name, GameplayAssetKind.Object, act, 2, id, null, refs.AsReadOnly(), warning));
            usedObjects.Add(key);
        }
        foreach (var preset in actObjects) Object(preset["ObjectClass"], preset);
        foreach (string key in objectClasses.Keys) if (!usedObjects.Contains(key)) Object(key, null);
        return new(act, result.OrderBy(a => a.Kind).ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Id).ToArray(), data.Loaded);
    }

    /// <summary>Resolve the selected recipe against a fresh catalog before an edit.</summary>
    public GameplayAsset Validate(GameplayAsset selection)
    {
        var found = Assets.Where(a => a.Act == selection.Act && a.Type == selection.Type && a.Id == selection.Id && a.Kind == selection.Kind
            && a.Key.Equals(selection.Key, StringComparison.OrdinalIgnoreCase) && a.MonsterClass == selection.MonsterClass).ToArray();
        if (!selection.CanPlace || found.Length != 1 || !found[0].CanPlace)
            throw new InvalidOperationException("Gameplay mapping changed or is unavailable. Refresh the browser and select a valid entry.");
        return found[0];
    }
}
