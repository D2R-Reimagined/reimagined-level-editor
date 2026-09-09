namespace D2RLevel.Core;

public sealed class AssetResolver
{
    public string DataRoot { get; }
    public AssetResolver(string root)
    {
        root = Path.GetFullPath(root);
        // Accept either the directory containing data/, or data/ itself.
        DataRoot = Directory.Exists(Path.Combine(root, "hd")) ? root : Path.Combine(root, "data");
        if (!Directory.Exists(Path.Combine(DataRoot, "hd")))
            throw new DirectoryNotFoundException("Select an extraction directory containing hd/ or data/hd/.");
    }
    public string Resolve(string path)
    {
        path = path.Replace('\\', '/');
        if (!path.StartsWith("data/", StringComparison.OrdinalIgnoreCase) ||
            path.Split('/').Any(p => p is ".." or "." or "") || path.Contains(':'))
            throw new InvalidDataException($"Invalid game asset path: {path}");
        var full = Path.GetFullPath(Path.Combine(DataRoot, path[5..]));
        if (!full.StartsWith(DataRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Asset path escapes the extraction directory.");
        return full;
    }
    public string ExtractionPath(string path)
    {
        // Presets use logical .model names; CASC commonly stores the physical LOD0
        // file. Resolve that convention without rewriting the preset reference.
        if (path.EndsWith(".model", StringComparison.OrdinalIgnoreCase) && !File.Exists(Resolve(path)) &&
            !System.Text.RegularExpressions.Regex.IsMatch(path, @"_lod\d+\.model$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return path[..^6] + "_lod0.model";
        return path;
    }
    public string ResolveForRead(string path) => Resolve(ExtractionPath(path));
    public string ResolvePreviewModel(string path, int preferredLod)
    {
        // Explicit LOD references are intentional. Only logical references use preview LODs.
        if (!System.Text.RegularExpressions.Regex.IsMatch(path, @"_lod\d+\.model$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
            path.EndsWith(".model", StringComparison.OrdinalIgnoreCase))
            for (int lod = Math.Clamp(preferredLod, 0, 4); lod > 0; lod--)
            {
                var candidate = Resolve(path[..^6] + $"_lod{lod}.model");
                if (File.Exists(candidate)) return candidate;
            }
        return ResolveForRead(path);
    }
    public string[] Missing(PresetDocument document) => document.AssetPaths().Select(ExtractionPath)
        .Where(p => !File.Exists(Resolve(p))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
