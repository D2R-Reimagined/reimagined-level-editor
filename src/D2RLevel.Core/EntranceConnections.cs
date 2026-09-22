namespace D2RLevel.Core;

public sealed record AreaEndpoint(int Area, int Slot);
public sealed record AreaConnection(AreaEndpoint Endpoint, int Destination, int Warp, string Name, string DestinationName,
    IReadOnlyList<int> ReturnSlots)
{
    public string Label => $"{Endpoint.Slot} → {DestinationName}";
}
public sealed record ConnectionPlan(IReadOnlyList<(int Area, int Slot, int Destination)> Routes, string Description);

/// <summary>Resolves saved Vis/Warp pairs. Generated room ownership is deliberately not inferred.</summary>
public sealed class EntranceConnections
{
    private readonly AssetResolver? assets;
    private readonly string? root;
    private readonly LevelTableEdits? edits;
    private readonly GameDataTable levels, presets, warps;
    public IReadOnlyList<GameDataRow> Areas => (edits?.Table ?? levels).Rows.Where(r => Number(r["Id"]) > 0).ToArray();
    public IReadOnlyList<GameDataTable> Sources => [levels, presets, warps];
    public EntranceConnections(AssetResolver? assets, string? root, LevelTableEdits? edits = null)
    {
        this.assets = assets; this.root = root; this.edits = edits;
        var tables = new GameDataTables(assets, root);
        levels = tables.Read("levels"); presets = tables.Read("lvlprest"); warps = tables.Read("lvlwarp");
        foreach (var table in new[] { presets, warps })
        {
            if (table.SourcePath is { } path) edits?.Witness(path);
            if (root is not null) edits?.Witness(Path.Combine(root, "global", "excel", table.Name + ".txt"));
        }
    }
    public static int Number(string value, int fallback = -1) => int.TryParse(value, out int n) ? n : fallback;
    public GameDataRow Area(int id)
    {
        var found = Areas.Where(r => Number(r["Id"]) == id).ToArray();
        return found.Length == 1 ? found[0] : throw new InvalidDataException($"Area {id} resolves to {found.Length} Levels rows.");
    }
    public string Name(int id)
    {
        if (id == 0) return "Unconnected";
        var found = Areas.Where(r => Number(r["Id"]) == id).ToArray();
        return found.Length == 1 ? $"{found[0]["Name"]} · {id}" : $"Unresolved area {id}";
    }
    public AreaConnection Connection(AreaEndpoint endpoint)
    {
        if (endpoint.Slot is < 0 or > 7) throw new InvalidDataException("Exit slot must be 0–7.");
        var area = Area(endpoint.Area); int dest = Number(area["Vis" + endpoint.Slot], 0);
        var target = Areas.Where(r => Number(r["Id"]) == dest).ToArray();
        return new(endpoint, dest, Number(area["Warp" + endpoint.Slot]), Name(endpoint.Area), Name(dest),
            target.Length == 1 ? Enumerable.Range(0, 8).Where(s => Number(target[0]["Vis" + s], 0) == endpoint.Area).ToArray() : []);
    }
    public GameDataRow[] Presets(int area) => presets.Rows.Where(r => Number(r["LevelId"]) == area).ToArray();
    public string[] Variants(int area) => Presets(area).SelectMany(p => Enumerable.Range(1, 6).Select(i => p["File" + i]))
        .Where(f => f.Length > 0 && f != "0").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public string ResolveMap(string relative)
    {
        relative = relative.Replace('\\', '/');
        if (relative.Split('/').Any(p => p is ".." or "." or "") || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("Invalid preset map path.");
        if (root is not null)
        {
            string path = SceneWorkspace.Inside(root, Path.Combine(root, "global", "tiles", relative));
            if (File.Exists(path)) return path;
        }
        return assets?.Resolve("data/global/tiles/" + relative) ?? throw new FileNotFoundException("Map assets are unavailable.");
    }
    public IReadOnlyList<GameDataRow> WarpRows(int id) => id < 0 ? [] : warps.Rows.Where(r => Number(r["Id"]) == id).ToArray();
    private void ValidateEndpoint(AreaEndpoint endpoint)
    {
        var area = Area(endpoint.Area); var connection = Connection(endpoint);
        if (connection.Warp < 0 || !int.TryParse(area["Vis" + endpoint.Slot], out int destination) || destination < 0)
            throw new InvalidOperationException("The endpoint must have numeric Vis and valid Warp values.");
        if (Number(area["DrlgType"]) != 2) throw new InvalidOperationException("Connection authoring requires fixed preset areas (DrlgType 2). Generated areas remain inspectable.");
        var rows = Presets(endpoint.Area);
        if (rows.Length == 0 || rows.Any(r => Number(r["Scan"]) != 1)) throw new InvalidOperationException("All area presets must enable exit scanning (Scan 1).");
        var variants = Variants(endpoint.Area);
        if (variants.Length == 0) throw new InvalidOperationException("No preset maps are available for this area.");
        foreach (var relative in variants)
        {
            string path = ResolveMap(relative); var map = Ds1CollisionDocument.Load(path);
            if (map.Act != Number(area["Act"]) + 1) throw new InvalidOperationException("Preset act does not match its area.");
            var tiles = map.ExitTiles().Where(t => t.IsExit && t.Main == endpoint.Slot).ToArray();
            if (tiles.Length == 0) throw new InvalidOperationException($"Slot {endpoint.Slot} has no exit marker in {relative}.");
            foreach (string direction in tiles.Select(t => t.Direction).Distinct())
            {
                if (!tiles.Any(t => t.Direction == direction && (t.Hidden || t.Sub is 0 or 4))) throw new InvalidOperationException("An exit group has no scan anchor.");
                var definitions = WarpRows(connection.Warp).Where(w => w["Direction"].Equals("b", StringComparison.OrdinalIgnoreCase) || w["Direction"].Equals(direction, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (definitions.Length != 1) throw new InvalidOperationException($"Warp {connection.Warp} has {definitions.Length} definitions for direction {direction}.");
            }
            edits?.Witness(path);
            // A subsequently created workspace override must invalidate a base-file witness too.
            if (root is not null) edits?.Witness(Path.Combine(root, "global", "tiles", relative));
        }
    }
    public ConnectionPlan Plan(AreaEndpoint from, AreaEndpoint to)
    {
        if (from.Area == to.Area) throw new InvalidOperationException("Choose an endpoint in another area.");
        var a = Connection(from); var b = Connection(to);
        var routes = new List<(int Area, int Slot, int Destination)> { (from.Area, from.Slot, to.Area), (to.Area, to.Slot, from.Area) };
        if (a.Destination == 0 && b.Destination == 0) { }
        else
        {
            if (a.Destination <= 0 || b.Destination <= 0 || a.ReturnSlots.Count != 1 || b.ReturnSlots.Count != 1)
                throw new InvalidOperationException("Choose two unused endpoints or two connections with exactly one return slot each.");
            if (new[] { from.Area, to.Area, a.Destination, b.Destination }.Distinct().Count() != 4)
                throw new InvalidOperationException("Swapping requires two separate reciprocal pairs across four different areas.");
            if (Connection(new(a.Destination, a.ReturnSlots[0])).ReturnSlots.Count != 1 || Connection(new(b.Destination, b.ReturnSlots[0])).ReturnSlots.Count != 1)
                throw new InvalidOperationException("Multiple outgoing slots share a destination; the reciprocal pair is ambiguous.");
            routes.Add((a.Destination, a.ReturnSlots[0], b.Destination)); routes.Add((b.Destination, b.ReturnSlots[0], a.Destination));
        }
        int act = Number(Area(from.Area)["Act"]);
        if (act is < 0 or > 4 || routes.Any(r => Number(Area(r.Area)["Act"]) != act)) throw new InvalidOperationException("All connected areas must be in the same act.");
        foreach (var route in routes) ValidateEndpoint(new(route.Area, route.Slot));
        return new(routes, string.Join("\n", routes.Select(r => $"{Name(r.Area)} / slot {r.Slot}: {Connection(new(r.Area, r.Slot)).DestinationName} → {Name(r.Destination)}")));
    }
    public void Apply(AreaEndpoint from, AreaEndpoint to)
    {
        if (edits is null) throw new InvalidOperationException("Open a workspace scene to save connection changes.");
        edits.VerifyUnchanged();
        // Re-read lookup tables and map variants before applying a reviewed plan.
        var fresh = new EntranceConnections(assets, root, edits);
        foreach (string name in new[] { "lvlprest", "lvlwarp" })
        {
            var table = fresh.Sources.Single(t => t.Name == name);
            if (table.SourcePath is null || table.Warning is not null) throw new InvalidDataException("Connection lookup table is unavailable: " + name);
            edits.Witness(table.SourcePath);
            if (root is not null) edits.Witness(Path.Combine(root, "global", "excel", name + ".txt"));
        }
        edits.Apply(fresh.Plan(from, to).Routes);
    }
}
