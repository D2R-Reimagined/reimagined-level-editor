using System.Text.RegularExpressions;

namespace D2RLevel.Core;

/// <summary>A LvlTypes row: one tileset family such as "Act 1 - Cave". <see cref="Files"/> holds File 1–32 as the table spells them ("" when unused).</summary>
public sealed record LevelTypeChoice(int Id, string Name, int Act, string[] Files)
{
    /// <summary>The DT1 folders this tileset draws from, such as <c>act1/caves</c>, lower-case and in table order.</summary>
    public string[] Folders => TilesetCatalog.Folders(Files.Where(f => f.Length > 0));
    public override string ToString() => Name;
}

/// <summary>
/// A fixed map that can seed a new level of the chosen tileset: a LvlPrest file with a paired HD preset. Rows with a LevelId
/// belong to that level; rows without one are rooms the level generator assembles, offered when they live in the tileset's folders.
/// </summary>
public sealed record TemplateChoice(string Name, int LevelId, string? LevelName, string Ds1Relative, string PresetPath, string MapPath,
    int Width, int Height, uint Dt1Mask)
{
    public bool IsLevel => LevelId > 0;
    public override string ToString() => Name;
}

/// <summary>Reads tileset families and their templates from the level tables, and the HD models that belong to a tileset.</summary>
public static class TilesetCatalog
{
    public static LevelTypeChoice[] LevelTypes(GameDataTables tables) => tables.Read("lvltypes").Rows
        // Separator rows such as "Expansion" carry no Id.
        .Where(r => int.TryParse(r["Id"], out int id) && id > 0 && r["Name"].Length > 0)
        .Select(r => new LevelTypeChoice(int.Parse(r["Id"]), r["Name"], EntranceConnections.Number(r["Act"], 0),
            Enumerable.Range(1, 32).Select(i => r[$"File {i}"] is var f && f.Length > 0 && f != "0" ? f.Replace('\\', '/') : "").ToArray()))
        .Where(t => t.Act is >= 1 and <= 5 && t.Files.Any(f => f.Length > 0))
        .ToArray();

    /// <summary>Lower-case <c>actN/folder</c> for each DT1 path, in first-seen order. Accepts table paths or <c>data/global/tiles/…</c> paths.</summary>
    public static string[] Folders(IEnumerable<string> dt1Paths) => dt1Paths
        .Select(p => p.Replace('\\', '/').ToLowerInvariant())
        .Select(p => p.IndexOf("global/tiles/", StringComparison.Ordinal) is var at && at >= 0 ? p[(at + "global/tiles/".Length)..] : p)
        .Select(p => p.Split('/')).Where(parts => parts.Length >= 3).Select(parts => parts[0] + "/" + parts[1])
        .Distinct().ToArray();

    /// <summary>
    /// The tileset a template loads with when created as this level type: the type's files selected by the template's
    /// Dt1Mask (every file when the mask selects none). Files that are not present in the asset folder or workspace are left out.
    /// </summary>
    public static LevelTileset Tileset(LevelTypeChoice type, uint mask, Func<string, bool>? exists = null)
    {
        var selected = Enumerable.Range(0, 32).Where(i => (mask & (1u << i)) != 0 && type.Files[i].Length > 0).ToArray();
        if (selected.Length == 0) { selected = Enumerable.Range(0, 32).Where(i => type.Files[i].Length > 0).ToArray(); }
        var files = selected.Select(i => "data/global/tiles/" + type.Files[i]).Where(f => exists?.Invoke(f) != false)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) throw new InvalidDataException($"None of {type.Name}'s DT1 files are available.");
        // The mask keeps LvlPrest's meaning: bits of the type's File 1–32 list.
        var tileset = new LevelTileset(selected.Aggregate(0u, (m, i) => m | 1u << i), files);
        tileset.Validate();
        return tileset;
    }

    /// <summary>
    /// Templates for a level type, level presets first. A map is offered once, under the row that names a level when there is one.
    /// Paths resolve through the workspace first, then the asset folder; maps without a paired HD preset are skipped.
    /// </summary>
    public static TemplateChoice[] Templates(GameDataTables tables, AssetResolver assets, string? workspaceDataRoot, LevelTypeChoice type, CancellationToken token = default)
    {
        var levelTypes = tables.Read("levels").Rows.Where(r => EntranceConnections.Number(r["Id"], 0) > 0)
            .GroupBy(r => EntranceConnections.Number(r["Id"])).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var folders = type.Folders.ToHashSet();
        string? Find(string logical)
        {
            if (workspaceDataRoot is not null)
            {
                string local = Path.Combine(workspaceDataRoot, logical[5..].Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(local)) return Path.GetFullPath(local);
            }
            try { string path = assets.Resolve(logical); return File.Exists(path) ? path : null; }
            catch (InvalidDataException) { return null; }
        }
        var found = new Dictionary<string, TemplateChoice>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in tables.Read("lvlprest").Rows)
        {
            token.ThrowIfCancellationRequested();
            int levelId = EntranceConnections.Number(row["LevelId"], 0);
            var level = levelId > 0 ? levelTypes.GetValueOrDefault(levelId) : null;
            if (levelId > 0 && (level is null || EntranceConnections.Number(level["LevelType"], 0) != type.Id)) continue;
            var files = Enumerable.Range(1, 6).Select(i => row["File" + i].Replace('\\', '/')).Where(f => f.Length > 0 && f != "0").ToArray();
            for (int i = 0; i < files.Length; i++)
            {
                string relative = files[i].ToLowerInvariant();
                if (relative.Split('/').Any(p => p is "" or "." or "..") || !relative.EndsWith(".ds1", StringComparison.Ordinal)) continue;
                if (levelId == 0 && !folders.Contains(string.Join('/', relative.Split('/').Take(2)))) continue;
                // A level preset replaces a room already offered under the same file.
                if (found.TryGetValue(relative, out var existing) && (existing.IsLevel || levelId == 0)) continue;
                if (Find("data/hd/env/preset/" + relative[..^4] + ".json") is not { } preset || Find("data/global/tiles/" + relative) is not { } map) continue;
                if (Header(map) is not { } size) continue;
                string name = row["Name"] + (files.Length > 1 ? $" · {i + 1}/{files.Length}" : "");
                found[relative] = new(name, levelId, level?["Name"], relative, preset, map, size.Width, size.Height,
                    uint.TryParse(row["Dt1Mask"], out uint mask) ? mask : 0);
            }
        }
        return found.Values.OrderBy(t => t.IsLevel ? 0 : 1).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>A DS1's dimensions from its header, or null when it is not a readable map.</summary>
    private static (int Width, int Height)? Header(string path)
    {
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            int version = reader.ReadInt32(), width = reader.ReadInt32() + 1, height = reader.ReadInt32() + 1;
            return version is >= 16 and <= 18 && width is >= 1 and <= 512 && height is >= 1 and <= 512 ? (width, height) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// HD environment models for a tileset's DT1 folders (<c>act1/caves</c> → <c>data/hd/env/model/act1/caves</c>), as logical
    /// LOD0 names. Models in the workspace's matching folders are included alongside the asset folder's.
    /// </summary>
    public static ModelEntry[] Models(AssetResolver assets, IEnumerable<string> folders, string? workspaceDataRoot = null, CancellationToken token = default)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in new[] { assets.DataRoot, workspaceDataRoot }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (string folder in folders)
            {
                string directory = Path.Combine(root, "hd", "env", "model", folder.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(directory)) continue;
                foreach (string file in Directory.EnumerateFiles(directory, "*.model", SearchOption.AllDirectories))
                {
                    token.ThrowIfCancellationRequested();
                    string path = "data/" + Path.GetRelativePath(root, file).Replace('\\', '/');
                    path = Regex.Replace(path, "_lod0\\.model$", ".model", RegexOptions.IgnoreCase);
                    if (Regex.IsMatch(path, "_lod[1-9][0-9]*\\.model$", RegexOptions.IgnoreCase)) continue;
                    paths.Add(path);
                }
            }
        return paths.Order(StringComparer.OrdinalIgnoreCase).Select(p => new ModelEntry(p)).ToArray();
    }

    /// <summary>A coarse kind for a model from its name, to group a tileset's pieces.</summary>
    public static ModelKind Kind(ModelEntry model)
    {
        string name = model.Name.ToLowerInvariant();
        bool Has(params string[] words) => words.Any(name.Contains);
        if (Has("door", "gate", "arch", "portcull", "entrance", "exit", "stair", "ladder", "warp", "hole")) return ModelKind.Passages;
        if (Has("wall", "palisade", "fence", "cliff", "railing", "rail", "border", "edge", "corner", "cap")) return ModelKind.Walls;
        if (Has("floor", "ground", "tile", "path", "road", "bridge", "platform", "terrain", "decal", "patch")) return ModelKind.Floors;
        if (Has("pillar", "column", "post", "beam", "support", "statue", "obelisk")) return ModelKind.Pillars;
        if (Has("tree", "bush", "plant", "grass", "rock", "stone", "stump", "log", "shrub", "vine", "root", "mushroom", "outcrop")) return ModelKind.Nature;
        return ModelKind.Props;
    }
}

/// <summary>How the tileset palette groups models, judged from their names.</summary>
public enum ModelKind { Walls, Floors, Passages, Pillars, Nature, Props }
