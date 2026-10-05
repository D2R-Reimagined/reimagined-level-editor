namespace D2RLevel.Core;

public sealed record PresetPair(string JsonPath, string Ds1Path, string RelativePath, string Source);

public static class PresetPairing
{
    public static (string DataRoot, string Relative)? Split(string path, string prefix)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        string marker = "/" + prefix.Trim('/') + "/";
        int index = full.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : (full[..index], full[(index + marker.Length)..]);
    }
    public static PresetPair? Find(string jsonPath, AssetResolver resolver, IReadOnlyDictionary<string, string>? remembered = null)
    {
        jsonPath = Path.GetFullPath(jsonPath);
        var saved = remembered?.FirstOrDefault(p => p.Key.Equals(jsonPath, StringComparison.OrdinalIgnoreCase)).Value;
        if (saved is not null && File.Exists(saved)) return new(jsonPath, saved, RelativeDs1(saved, resolver), "Remembered pairing");
        if (Split(jsonPath, "hd/env/preset") is { } location)
        {
            var relative = Path.ChangeExtension(location.Relative, ".ds1").Replace('\\', '/');
            var sibling = Path.GetFullPath(Path.Combine(location.DataRoot, "global", "tiles", relative));
            if (File.Exists(sibling)) return new(jsonPath, sibling, relative, "Same data folder");
            var fallback = resolver.Resolve("data/global/tiles/" + relative);
            if (File.Exists(fallback)) return new(jsonPath, fallback, relative, "Base asset fallback");
        }
        return null; // Never pick another town variant just because its name looks similar.
    }
    public static string RelativeDs1(string path, AssetResolver resolver)
    {
        if (Split(path, "global/tiles") is { } location) return location.Relative;
        var candidates = Directory.EnumerateFiles(resolver.Resolve("data/global/tiles"), "*.ds1", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (candidates.Length == 1) return Path.GetRelativePath(resolver.Resolve("data/global/tiles"), candidates[0]).Replace('\\', '/');
        throw new InvalidDataException("Cannot identify this DS1's tileset context. Choose a file under global/tiles with its original relative path.");
    }
    public static PresetPair[] Scan(AssetResolver resolver)
    {
        var root = resolver.Resolve("data/hd/env/preset");
        return Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Select(path => Find(path, resolver)).OfType<PresetPair>().OrderBy(p => p.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
