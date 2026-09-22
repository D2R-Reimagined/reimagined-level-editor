using System.Text.Json.Nodes;

namespace D2RLevel.Core;

public sealed record NpcDefinition(string Name, string? DefinitionPath, string? Warning);

public sealed class NpcCatalog
{
    private readonly AssetResolver assets;
    private readonly AssetResolver? mod;
    private readonly Dictionary<int, string[]> presets;
    private readonly Dictionary<string, Dictionary<string, string>> monsters;
    private readonly Dictionary<string, string> superuniques;
    private readonly JsonObject mappings;
    public NpcCatalog(AssetResolver assets, string? workspaceRoot)
    {
        this.assets = assets;
        if (workspaceRoot is not null) mod = new(workspaceRoot);
        presets = Table("data/global/excel/monpreset.txt").Where(r => int.TryParse(r.GetValueOrDefault("Act"), out _))
            .GroupBy(r => int.Parse(r["Act"])).ToDictionary(g => g.Key, g => g.Select(r => r.GetValueOrDefault("Place", "")).ToArray());
        monsters = Table("data/global/excel/monstats.txt").Where(r => !string.IsNullOrEmpty(r.GetValueOrDefault("Id")))
            .GroupBy(r => r["Id"], StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // A preset entry naming a superunique carries its SuperUniques row, not a MonStats Id;
        // the row's Class is the base monster whose appearance the superunique wears. The table
        // is optional so a data folder without it keeps every ordinary preview working.
        superuniques = Table("data/global/excel/superuniques.txt", optional: true)
            .Where(r => !string.IsNullOrEmpty(r.GetValueOrDefault("Superunique")) && !string.IsNullOrEmpty(r.GetValueOrDefault("Class")))
            .GroupBy(r => r["Superunique"], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First()["Class"], StringComparer.OrdinalIgnoreCase);
        mappings = JsonNode.Parse(File.ReadAllText(Resolve("data/hd/character/monsters.json"))) as JsonObject
            ?? throw new InvalidDataException("Invalid HD character mapping.");
    }
    public string Resolve(string path, bool model = false)
    {
        var fallback = model ? assets.ResolvePreviewModel(path, 1) : assets.Resolve(path);
        if (mod is not null)
        {
            var candidate = model ? mod.ResolvePreviewModel(path, 1) : mod.Resolve(path);
            if (File.Exists(candidate)) return candidate;
        }
        return fallback;
    }
    private Dictionary<string, string>[] Table(string path, bool optional = false)
    {
        string resolved = Resolve(path);
        if (optional && !File.Exists(resolved)) return [];
        var lines = File.ReadAllLines(resolved);
        if (lines.Length == 0) throw new InvalidDataException("Empty table: " + path);
        var header = lines[0].TrimStart('\uFEFF').Split('\t');
        return lines.Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)).Select(line =>
        {
            var cells = line.Split('\t'); var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Length; i++) if (header[i].Length > 0) row[header[i]] = i < cells.Length ? cells[i] : "";
            return row;
        }).ToArray();
    }
    private static readonly string[] Folders = ["npc", "enemy"];
    public NpcDefinition Lookup(int act, Ds1Unit unit)
    {
        if (unit.Type != 1 || !presets.TryGetValue(act, out var entries) || unit.Id < 0 || unit.Id >= entries.Length)
            return new($"Unit {unit.Id}", null, "Unresolved act-specific monster preset.");
        string name = entries[unit.Id];
        string? id = monsters.ContainsKey(name) ? name
            : superuniques.TryGetValue(name, out var baseClass) && monsters.ContainsKey(baseClass) ? baseClass : null;
        if (id is null) return new(name, null, name.StartsWith("place_", StringComparison.OrdinalIgnoreCase)
            // monplace.txt lists these codes but holds no monster; the game picks from the level at run time.
            ? "Spawn placement code: the game chooses the monster when the level is generated."
            : "No MonStats or SuperUniques row for this preset entry.");
        string appearance = mappings[id]?.GetValue<string>() ?? id;
        if (appearance.Length == 0 || appearance.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            return new(name, null, "Invalid character appearance mapping.");
        // Town NPCs and everything hostile (monsters, summons, traps such as labfiretrap1)
        // share one definition format in two folders, and the appearance mapping does not say
        // which one holds an entry, so both are probed.
        foreach (string folder in Folders)
        {
            string path = $"data/hd/character/{folder}/{appearance}.json";
            if (File.Exists(Resolve(path))) return new(name, path, null);
        }
        return new(name, null, $"No HD character definition for appearance '{appearance}'.");
    }
}
