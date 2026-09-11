using System.Text.Json;

namespace D2RLevel.Core;

public sealed record LevelTileset(uint Mask, string[] Files)
{
    public void Validate()
    {
        if (Files is null || Files.Length is < 1 or > 32 || Files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Files.Length)
            throw new InvalidDataException("A tileset needs 1–32 distinct DT1 paths.");
        foreach (string file in Files) LevelProject.ValidateAssetPath(file, "data/global/tiles/", ".dt1");
    }
}

public sealed record LevelPlacement(string Name, int Type, int Id, uint Flags);

/// <summary>Portable editor context. A project does not register a new game area.</summary>
public sealed record LevelProject(int Version, Guid Id, string Name, string Preset, string Map,
    int Width, int Height, int Act, LevelTileset Tileset, string Template, string TemplateSha256)
{
    public const string Suffix = ".rle-project.json";
    public LevelPlacement[] Placements { get; init; } = [];
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    internal static void ValidateAssetPath(string value, string prefix, string extension)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !value.EndsWith(extension, StringComparison.OrdinalIgnoreCase) || value.Contains('\\') || value.Contains(':') ||
            value.Split('/').Any(p => p is "" or "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Invalid project asset path: " + value);
    }
    public void Validate()
    {
        if (Version != 1 || Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 100 ||
            Width is < 1 or > 512 || Height is < 1 or > 512 || Act is < 1 or > 5 || Tileset is null)
            throw new InvalidDataException("Unsupported or invalid level project.");
        ValidateAssetPath(Preset, "data/hd/env/preset/", ".json");
        ValidateAssetPath(Map, "data/global/tiles/", ".ds1");
        Tileset.Validate();
        if (Placements is null || Placements.Length > 10000 || Placements.Any(p => p is null || p.Type is not (1 or 2) || p.Id < 0 || string.IsNullOrWhiteSpace(p.Name)))
            throw new InvalidDataException("Invalid template placements.");
    }
    public static LevelProject? ForPreset(string preset)
    {
        string path = preset + Suffix;
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 128 * 1024) throw new InvalidDataException("Level project exceeds 128 KiB.");
        var project = JsonSerializer.Deserialize<LevelProject>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Empty level project.");
        project.Validate();
        var location = PresetPairing.Split(preset, "hd/env/preset") ?? throw new InvalidDataException("Project preset must be inside a data workspace.");
        string expected = SceneWorkspace.Inside(location.DataRoot, Path.Combine(location.DataRoot, project.Preset[5..]));
        if (!expected.Equals(Path.GetFullPath(preset), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Project belongs to a different preset.");
        return project;
    }
    public void VerifyMap(Ds1CollisionDocument map)
    {
        if (Width != map.Width || Height != map.Height || Act != map.Act) throw new InvalidDataException("Project dimensions or act do not match the DS1.");
    }

    /// <summary>Create a new, isolated workspace directory by publishing a complete staged directory.
    /// The HD scaffold is preserved; DS1 floors are newly constructed and all units/walls start empty.</summary>
    public static WorkspaceScene CreateFromTemplate(string destination, string name, PresetDocument template, LegacyFloorScene source, uint floor, GridCalibration? calibration = null, AssetResolver? assets = null, bool keepScenery = false)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new ArgumentException("Enter a project name (1–100 characters).");
        destination = Path.GetFullPath(destination);
        string parent = Path.GetDirectoryName(destination) ?? throw new InvalidDataException("Choose a project folder below an existing directory.");
        if (!Directory.Exists(parent) || Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Choose a new project folder; existing folders are never replaced.");
        SceneWorkspace.Inside(parent, Path.Combine(destination, "data"));
        var location = PresetPairing.Split(template.SourcePath, "hd/env/preset") ?? throw new InvalidDataException("Template must have a game-relative preset path.");
        var mapLocation = PresetPairing.Split(source.Ds1Path, "global/tiles") ?? throw new InvalidDataException("Template DS1 must have a game-relative path.");
        var context = new LevelTileset(source.Mask, source.Dt1Paths.Select(p =>
            "data/global/tiles/" + (PresetPairing.Split(p, "global/tiles")?.Relative ?? throw new InvalidDataException("DT1 must have a game-relative path.")).Replace('\\', '/')).ToArray());
        if (floor != 0 && !source.Tiles.ContainsKey((new FloorCell(floor).Main, new FloorCell(floor).Sub))) throw new InvalidDataException("Starting floor is not in the selected tileset.");
        var project = new LevelProject(1, Guid.NewGuid(), name.Trim(), "data/hd/env/preset/" + location.Relative.Replace('\\', '/'),
            "data/global/tiles/" + mapLocation.Relative.Replace('\\', '/'), source.Map.Width, source.Map.Height, source.Map.Act, context,
            "data/hd/env/preset/" + location.Relative.Replace('\\', '/'), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(template.Serialize())));
        project = project with { Placements = (ForPreset(template.SourcePath)?.Placements ?? []).Concat(source.Collision?.Document.Units.Where(u => u.Type is 1 or 2 && u.Id >= 0)
            .Select(u => new LevelPlacement($"{(u.Type == 1 ? "NPC/monster" : "Object")} {u.Id}", u.Type, u.Id, u.Flags)) ?? [])
            .DistinctBy(p => (p.Type, p.Id, p.Flags)).ToArray() };
        project.Validate();
        string stage = Path.Combine(parent, ".rle-create-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            // Freeze the effective gameplay context, including mod overrides. These are local
            // project assets, not files bundled with the editor distribution.
            void Copy(string logical, string physical)
            {
                string target = SceneWorkspace.Inside(stage, Path.Combine(stage, logical));
                if (File.Exists(target))
                {
                    if (File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(physical))) return;
                    throw new InvalidDataException("Conflicting project dependency: " + logical);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(physical, target, false);
            }
            for (int i = 0; i < context.Files.Length; i++) Copy(context.Files[i], source.Dt1Paths[i]);
            string palette = SceneWorkspace.Inside(stage, Path.Combine(stage, $"data/global/palette/act{source.Map.Act}/pal.dat"));
            Directory.CreateDirectory(Path.GetDirectoryName(palette)!); File.WriteAllBytes(palette, source.Palette);
            foreach (string table in new[] { "levels", "lvlprest", "lvltypes", "lvlwarp", "monpreset", "monstats", "objects" })
            {
                string logical = $"data/global/excel/{table}.txt";
                string? physical = new[] { Path.Combine(location.DataRoot, logical[5..]), Path.Combine(mapLocation.DataRoot, logical[5..]), assets?.Resolve(logical) }
                    .FirstOrDefault(p => p is not null && File.Exists(p));
                if (physical is not null) Copy(logical, physical);
            }
            // Carry HD overrides when the template lives in a mod rather than the base extraction.
            // Unmodified game assets continue to resolve through the user's asset folder.
            if (assets is not null && !Path.GetFullPath(location.DataRoot).Equals(Path.GetFullPath(assets.DataRoot), StringComparison.OrdinalIgnoreCase))
            {
                var local = new AssetResolver(location.DataRoot);
                foreach (string logical in template.AssetPaths().Where(p => p.StartsWith("data/hd/", StringComparison.OrdinalIgnoreCase)))
                {
                    string physical = local.ResolveForRead(logical);
                    if (File.Exists(physical)) Copy("data/" + Path.GetRelativePath(location.DataRoot, physical).Replace('\\', '/'), physical);
                }
            }
            string json = SceneWorkspace.Inside(stage, Path.Combine(stage, project.Preset));
            string ds1 = SceneWorkspace.Inside(stage, Path.Combine(stage, project.Map));
            Directory.CreateDirectory(Path.GetDirectoryName(json)!); Directory.CreateDirectory(Path.GetDirectoryName(ds1)!);
            File.WriteAllBytes(json, template.AuthoringScaffold(json, keepScenery).Serialize());
            File.WriteAllBytes(ds1, Ds1CollisionDocument.Create(ds1, project.Width, project.Height, project.Act, floor).Serialize());
            File.WriteAllBytes(json + Suffix, JsonSerializer.SerializeToUtf8Bytes(project, Options));
            var checkedJson = PresetDocument.Load(json); var checkedMap = Ds1CollisionDocument.Load(ds1);
            project.VerifyMap(checkedMap);
            var links = new PlacementLinks(checkedJson, checkedMap);
            if (calibration is not null) links.SetCalibration(calibration);
            File.WriteAllBytes(json + PlacementLinks.Suffix, links.MetadataFor(json + PlacementLinks.Suffix, ds1));
            _ = ForPreset(json);
            Directory.Move(stage, destination);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        return new(project.Name, Path.GetFullPath(Path.Combine(destination, project.Preset)), Path.GetFullPath(Path.Combine(destination, project.Map)));
    }
}
