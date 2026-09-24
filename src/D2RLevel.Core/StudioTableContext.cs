using System.Text.Json;
using Reimagined.Integration;

namespace D2RLevel.Core;

/// <summary>The current linked workspace's immutable profile snapshot. Native standalone workspaces do not use it.</summary>
public static class StudioTableContext
{
    private static EditorProject? project;
    private static JsonDocument? snapshot;
    private static readonly object gate = new();
    public static EditorProject? Project { get => project; set { lock (gate) { project = value; snapshot = null; } } }
    public static string? LogicalMap { get; set; }
    public static void Invalidate() { lock (gate) snapshot = null; }
    public static WorkspaceScene[]? WorkspaceScenes(string dataRoot)
    {
        lock (gate)
        {
            if (Project == null || IntegrationFiles.Canonical(dataRoot) != IntegrationFiles.Canonical(Path.Combine(Project.Root, "data"))) return null;
            var presets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var folder = Path.Combine(dataRoot, "hd/env/preset");
            if (Directory.Exists(folder)) foreach (var file in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories)) presets.Add(Path.GetRelativePath(dataRoot, file).Replace('\\', '/'));
            if (Project.Snapshot != null && Snapshot().TryGetProperty("assetOverrides", out var overrides))
                foreach (var asset in overrides.EnumerateObject()) if (asset.Name.StartsWith("hd/env/preset/", StringComparison.OrdinalIgnoreCase)) presets.Add(asset.Name);
            var scenes = new List<WorkspaceScene>();
            foreach (var logical in presets.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!logical.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || logical.Contains(".rle-", StringComparison.OrdinalIgnoreCase)) continue;
                var preset = ResolveAsset(logical); if (preset == null) continue;
                string mapLogical = "global/tiles/" + Path.ChangeExtension(logical[14..], ".ds1");
                if (LevelProject.ForPreset(preset) is { } authored) mapLogical = authored.Map[5..];
                var map = ResolveAsset(mapLogical);
                if (PlacementLinks.LinkedDs1Path(preset) is { } paired)
                {
                    IntegrationFiles.Inside(Project.Root, paired);
                    if (PresetPairing.Split(paired, "global/tiles") is { } location) { mapLogical = "global/tiles/" + location.Relative.Replace('\\', '/'); map = ResolveAsset(mapLogical) ?? paired; }
                    else map = paired;
                }
                if (map != null && File.Exists(map)) scenes.Add(new(logical[14..], preset, map, mapLogical));
            }
            return [.. scenes];
        }
    }
    private static JsonElement Snapshot()
    {
        if (snapshot == null)
        {
            IntegrationFiles.Inside(Project!.Root, Project.Snapshot!);
            snapshot = JsonDocument.Parse(File.ReadAllText(Project.Snapshot!));
            if (snapshot.RootElement.GetProperty("projectId").GetString() != Project.Id || snapshot.RootElement.GetProperty("profile").GetString() != Project.Profile)
            { snapshot = null; throw new InvalidDataException("Studio table snapshot belongs to another project/profile."); }
        }
        return snapshot.RootElement;
    }
    public static string? ResolveAsset(string logical)
    {
        lock (gate)
        {
            if (Project == null) return null;
            if (logical.StartsWith("global/excel/", StringComparison.OrdinalIgnoreCase) && logical.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && !logical[13..].Contains('/'))
                return Resolve(Path.GetFileNameWithoutExtension(logical));
            if (Project.Snapshot != null && Snapshot().TryGetProperty("assetOverrides", out var overrides) && overrides.TryGetProperty(logical, out var source))
            {
                if (source.GetString() is not { Length: > 0 } file) throw new InvalidDataException("This profile asset uses a generated transform and cannot be read directly: " + logical);
                var full = IntegrationFiles.Inside(Project.Root, file);
                if (!File.Exists(full)) throw new FileNotFoundException("Profile asset is missing.", full);
                return full;
            }
            var native = IntegrationFiles.Inside(Project.Root, "data/" + logical);
            return File.Exists(native) ? native : null;
        }
    }
    public static string? Resolve(string name)
    {
        if (Project?.Snapshot is not { } pointer) return null;
        lock (gate)
        {
        var data = Snapshot();
        if (!data.GetProperty("tables").TryGetProperty("global/excel/" + name + ".txt", out _)) return null;
        var root = data.GetProperty("root").GetString()!; IntegrationFiles.Inside(Project.Root, root);
        var file = IntegrationFiles.Inside(root, "global/excel/" + name + ".txt");
        if (!File.Exists(file)) throw new FileNotFoundException("Studio's authored table snapshot is missing. Refresh it from Mod Studio.", file);
        return file;
        }
    }
    public static string? SourceId(string table, GameDataRow row)
    {
        if (Project?.Snapshot == null) return null;
        using var json = JsonDocument.Parse(File.ReadAllText(Project.Snapshot));
        if (!json.RootElement.GetProperty("tables").TryGetProperty("global/excel/" + table + ".txt", out var info)) return null;
        // The row was read from this exact immutable snapshot, not a base table or a newer revision.
        var expected = Path.Combine(json.RootElement.GetProperty("root").GetString()!, "global/excel/" + table + ".txt");
        if (IntegrationFiles.Canonical(row.SourcePath) != IntegrationFiles.Canonical(expected)) throw new InvalidDataException("Table context changed. Refresh the level properties before following this link.");
        var ids = info.GetProperty("sourceIds"); int index = row.Line - 2;
        return index >= 0 && index < ids.GetArrayLength() ? ids[index].GetString() : null;
    }
}
