namespace D2RLevel.Core;

public sealed record WorkspaceScene(string Name, string JsonPath, string Ds1Path);

public static class SceneWorkspace
{
    public static string Inside(string root, string path)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        path = Path.GetFullPath(path);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Scene path is outside the workspace.");
        // Avoid writing through directory junctions/symlinks outside the selected mod.
        for (var info = new FileInfo(path) as FileSystemInfo; info is not null && info.FullName.Length >= root.Length; info = Directory.GetParent(info.FullName))
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Workspace scene paths cannot traverse links or junctions.");
        return path;
    }
    public static WorkspaceScene[] Scan(string root, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        root = Path.GetFullPath(root);
        string presets = Path.Combine(root, "hd", "env", "preset");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Choose an existing workspace directory.");
        if (!Directory.Exists(presets)) return [];
        var result = new List<WorkspaceScene>();
        foreach (var json in Directory.EnumerateFiles(presets, "*.json", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            token.ThrowIfCancellationRequested();
            if (json.EndsWith(PlacementLinks.Suffix, StringComparison.OrdinalIgnoreCase) || json.EndsWith(LevelProject.Suffix, StringComparison.OrdinalIgnoreCase) || json.EndsWith(".rle-groups.json", StringComparison.OrdinalIgnoreCase)) continue;
            string relative = Path.GetRelativePath(presets, json);
            string name = relative.Replace('\\', '/');
            string ds1 = Path.Combine(root, "global", "tiles", Path.ChangeExtension(relative, ".ds1"));
            try
            {
                if (LevelProject.ForPreset(json) is { } project)
                { name = project.Name; ds1 = Inside(root, Path.Combine(root, project.Map[5..])); }
                if (PlacementLinks.LinkedDs1Path(json) is { } saved) ds1 = Inside(root, saved);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
            { /* Keep the canonical pair discoverable; opening it will display invalid link metadata. */ }
            Inside(root, json); Inside(root, ds1);
            if (File.Exists(ds1)) result.Add(new(name, json, ds1));
        }
        return result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

public sealed class WorkspaceSceneSession
{
    private readonly string root;
    public WorkspaceScene Scene { get; }
    private readonly string[] paths;
    private byte[]?[] snapshots;
    private readonly byte[]? projectSnapshot;
    public WorkspaceSceneSession(string root, WorkspaceScene scene)
    {
        this.root = Path.GetFullPath(root);
        Scene = scene with { JsonPath = Path.GetFullPath(scene.JsonPath), Ds1Path = Path.GetFullPath(scene.Ds1Path) };
        paths = [Scene.JsonPath, Scene.Ds1Path, Scene.JsonPath + PlacementLinks.Suffix];
        foreach (var path in paths) SceneWorkspace.Inside(root, path);
        snapshots = paths.Select(Read).ToArray();
        projectSnapshot = Read(scene.JsonPath + LevelProject.Suffix);
        if (snapshots[0] is null || snapshots[1] is null) throw new FileNotFoundException("The workspace scene pair is missing.");
    }
    private static byte[]? Read(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
    public void CaptureResetLinkFile() => snapshots[2] = Read(paths[2]);
    private static bool Equal(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.SequenceEqual(b);
    public void Save(PresetDocument json, Ds1CollisionDocument ds1, PlacementLinks links)
    {
        if (!Equal(Read(Scene.JsonPath + LevelProject.Suffix), projectSnapshot)) throw new IOException("Level project changed outside the editor. Reopen before saving.");
        LevelProject.ForPreset(Scene.JsonPath)?.VerifyMap(ds1);
        if (!string.Equals(json.SourcePath, Path.GetFullPath(paths[0]), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ds1.SourcePath, Path.GetFullPath(paths[1]), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Loaded documents do not match the workspace scene.");
        if (links.Warning is not null) throw new InvalidOperationException(links.Warning);
        foreach (var path in paths) SceneWorkspace.Inside(root, path);
        for (int i = 0; i < paths.Length; i++)
            if (!Equal(Read(paths[i]), snapshots[i])) throw new IOException("File changed outside the editor. Reopen the scene before saving: " + paths[i]);
        byte[][] data = [json.Serialize(), ds1.Serialize(), links.MetadataFor(paths[2], paths[1])];
        string[] staged = paths.Select(p => p + "." + Guid.NewGuid().ToString("N") + ".tmp").ToArray();
        int written = 0;
        try
        {
            for (int i = 0; i < paths.Length; i++) File.WriteAllBytes(staged[i], data[i]);
            // Verify staged map parsers before replacing either live scene file.
            if (!PresetDocument.Load(staged[0]).Serialize().SequenceEqual(data[0]) || !Ds1CollisionDocument.Load(staged[1]).Serialize().SequenceEqual(data[1]))
                throw new InvalidDataException("Staged scene failed verification.");
            var verified = new PlacementLinks(PresetDocument.Load(staged[0]), Ds1CollisionDocument.Load(staged[1]), staged[2]);
            if (verified.Warning is not null) throw new InvalidDataException(verified.Warning);
            for (int i = 0; i < paths.Length; i++)
            {
                if (snapshots[i] is not null) File.Replace(staged[i], paths[i], paths[i] + ".bak");
                else File.Move(staged[i], paths[i]);
                written++;
            }
            snapshots = data; json.MarkSaved(); ds1.MarkSaved(); links.MarkWorkspaceSaved();
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            for (int i = written - 1; i >= 0; i--)
                try { if (snapshots[i] is { } original) File.WriteAllBytes(paths[i], original); else File.Delete(paths[i]); }
                catch (Exception rollback) { errors.Add(rollback); }
            if (errors.Count > 1) throw new AggregateException("Save failed and restoration was incomplete. Recover the scene using its .bak files.", errors);
            throw;
        }
        finally { foreach (var stage in staged) if (File.Exists(stage)) File.Delete(stage); }
    }
}
