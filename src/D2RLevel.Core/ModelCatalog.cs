using System.Text.RegularExpressions;

namespace D2RLevel.Core;

public sealed record ModelEntry(string Path)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public override string ToString() => Path;
}

public static class ModelCatalog
{
    public static ModelEntry[] Scan(AssetResolver resolver, CancellationToken token)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(System.IO.Path.Combine(resolver.DataRoot, "hd"), "*.model", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var path = "data/" + System.IO.Path.GetRelativePath(resolver.DataRoot, file).Replace('\\', '/');
            // Logical names use LOD0; higher LODs are not duplicate catalog entries.
            path = Regex.Replace(path, "_lod0\\.model$", ".model", RegexOptions.IgnoreCase);
            if (Regex.IsMatch(path, "_lod[1-9][0-9]*\\.model$", RegexOptions.IgnoreCase)) continue;
            paths.Add(path);
        }
        return paths.Order(StringComparer.OrdinalIgnoreCase).Select(p => new ModelEntry(p)).ToArray();
    }
}
