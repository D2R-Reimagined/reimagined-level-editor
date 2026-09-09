using System.Text.Json.Nodes;

namespace D2RLevel.Core;

public sealed record NpcDefinition(string Name, string? DefinitionPath, string? Warning);

public sealed class NpcCatalog
{
    private readonly AssetResolver assets;
    private readonly AssetResolver? mod;
    private readonly Dictionary<int, string[]> presets;
    private readonly Dictionary<string, Dictionary<string, string>> monsters;
    private readonly JsonObject mappings;
    public NpcCatalog(AssetResolver assets, string? workspaceRoot)
    {
        this.assets = assets;
        if (workspaceRoot is not null) mod = new(workspaceRoot);
        presets = Table("data/global/excel/monpreset.txt").Where(r => int.TryParse(r.GetValueOrDefault("Act"), out _))
            .GroupBy(r => int.Parse(r["Act"])).ToDictionary(g => g.Key, g => g.Select(r => r.GetValueOrDefault("Place", "")).ToArray());
        monsters = Table("data/global/excel/monstats.txt").Where(r => !string.IsNullOrEmpty(r.GetValueOrDefault("Id")))
            .GroupBy(r => r["Id"], StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
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
    private Dictionary<string, string>[] Table(string path)
    {
        var lines = File.ReadAllLines(Resolve(path));
        if (lines.Length == 0) throw new InvalidDataException("Empty table: " + path);
        var header = lines[0].TrimStart('\uFEFF').Split('\t');
        return lines.Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)).Select(line =>
        {
            var cells = line.Split('\t'); var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Length; i++) if (header[i].Length > 0) row[header[i]] = i < cells.Length ? cells[i] : "";
            return row;
        }).ToArray();
    }
    public NpcDefinition Lookup(int act, Ds1Unit unit)
    {
        if (unit.Type != 1 || !presets.TryGetValue(act, out var entries) || unit.Id < 0 || unit.Id >= entries.Length)
            return new($"Unit {unit.Id}", null, "Unresolved act-specific monster preset.");
        string name = entries[unit.Id];
        if (!monsters.ContainsKey(name)) return new(name, null, "Spawn group or superunique resolution is not supported yet.");
        string appearance = mappings[name]?.GetValue<string>() ?? name;
        if (appearance.Length == 0 || appearance.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            return new(name, null, "Invalid character appearance mapping.");
        string path = $"data/hd/character/npc/{appearance}.json";
        if (!File.Exists(Resolve(path))) return new(name, null, "NPC definition is missing or this is not a town NPC.");
        return new(name, path, null);
    }
}
