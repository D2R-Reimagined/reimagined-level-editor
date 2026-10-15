using System.Text.Json.Nodes;

namespace D2RLevel.Assets;

/// <summary>
/// Reads the terrain layers of a biome JSON. The game splat-blends these layers per vertex;
/// a static preview shades terrain with the base layer only.
/// </summary>
public static class BiomeReader
{
    /// <summary>
    /// The albedo of the biome's first usable terrain layer, which is the layer the game paints
    /// where no other layer is weighted. Placeholder test textures are skipped.
    /// </summary>
    public static string? BaseLayerAlbedo(string physicalPath)
    {
        var info = new FileInfo(physicalPath);
        if (!info.Exists) throw new FileNotFoundException("Biome is not extracted.", physicalPath);
        if (info.Length > 16 * 1024 * 1024) throw new InvalidDataException("Biome exceeds the editor's 16 MiB limit.");
        var root = JsonNode.Parse(File.ReadAllText(physicalPath)) as JsonObject ?? throw new InvalidDataException("Expected a biome object.");
        foreach (var quality in new[] { "terrainDataUltra", "terrainDataHigh", "terrainDataMed", "terrainDataLow" })
        {
            if (root[quality]?["terrainLayers"] is not JsonArray layers) continue;
            foreach (var layer in layers)
                if (layer?["Albedo"] is JsonValue value && value.TryGetValue<string>(out var albedo) && albedo.Length > 0 &&
                    !albedo.Replace('\\', '/').Contains("/terrain/test/", StringComparison.OrdinalIgnoreCase))
                    return albedo.Replace('\\', '/');
        }
        return null;
    }
}
