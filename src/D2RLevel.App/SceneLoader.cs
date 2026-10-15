using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using D2RLevel.Core;
using D2RLevel.Assets;

namespace D2RLevel.App;

public static class SceneLoader
{
    /// <summary>World units covered by one repeat of a biome layer texture, about one tile.</summary>
    private const double TerrainTextureSpan = 10;

    public static LoadedScene Load(PresetDocument doc, AssetResolver? resolver, IProgress<string> progress, CancellationToken token, bool fullDetail = true, Func<string, bool, string>? resolve = null)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        int preferredLod = fullDetail ? 0 : doc.Entities.Count > 2000 ? 4 : 2;
        if (resolve is null && resolver is not null && PresetPairing.Split(doc.SourcePath, "hd/env/preset") is { } location)
        {
            var local = new AssetResolver(location.DataRoot);
            resolve = (path, model) =>
            {
                string candidate = model ? local.ResolvePreviewModel(path, preferredLod) : local.Resolve(path);
                return System.IO.File.Exists(candidate) ? candidate : model ? resolver.ResolvePreviewModel(path, preferredLod) : resolver.Resolve(path);
            };
        }
        long lastProgress = -100, textureBytes = 0;
        var items = new List<SceneItem>();
        var messages = new HashSet<string>();
        var needsDecoder = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = new Dictionary<string, Model3DGroup?>(StringComparer.OrdinalIgnoreCase);
        var modelTextures = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var textures = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        var proxy = SceneViewport.Placeholder();
        Material defaultMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(144, 153, 147)));
        defaultMaterial.Freeze();
        int loaded = 0;
        Material Texture(string albedo)
        {
            if (textures.TryGetValue(albedo, out var cached)) return cached;
            cached = defaultMaterial;
            try
            {
                cached = PreviewMaterials.FromTexture(resolve?.Invoke(albedo, false) ?? resolver!.Resolve(albedo),
                    fullDetail ? 1024 : 512, out int decoded);
                textureBytes += decoded;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { messages.Add($"Texture {albedo}: {ex.Message}"); }
            return textures[albedo] = cached;
        }
        // The preset's own biome wins over the terrain model's, which is often the placeholder test biome.
        string? sceneBiome = doc.Biome;
        var biomeMaterials = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        Material BiomeMaterial(string modelBiome)
        {
            string biome = sceneBiome ?? modelBiome;
            if (biomeMaterials.TryGetValue(biome, out var cached)) return cached;
            cached = defaultMaterial;
            try
            {
                if (BiomeReader.BaseLayerAlbedo(resolve?.Invoke(biome, false) ?? resolver!.Resolve(biome)) is not { } albedo)
                    messages.Add($"Biome {biome} has no terrain layer textures.");
                else if (Texture(albedo) is var layer && layer != defaultMaterial)
                    cached = PreviewMaterials.BiomeLayer(layer);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { messages.Add($"Biome {biome}: {ex.Message}"); }
            return biomeMaterials[biome] = cached;
        }
        Model3DGroup? LoadModel(string path)
        {
            if (models.TryGetValue(path, out var model)) return model;
            model = null;
            try
            {
                if (resolver is null) throw new InvalidOperationException("No asset folder selected.");
                var asset = ModelReader.Load(resolve?.Invoke(path, true) ?? resolver.ResolvePreviewModel(path, preferredLod));
                modelTextures[path] = asset.TexturePaths ?? [];
                model = new Model3DGroup();
                foreach (var part in asset.Parts)
                {
                    token.ThrowIfCancellationRequested();
                    var geometry = new MeshGeometry3D();
                    for (int i = 0; i < part.Positions.Length; i += 3)
                    {
                        geometry.Positions.Add(new(part.Positions[i], part.Positions[i + 1], part.Positions[i + 2]));
                        geometry.Normals.Add(new(part.Normals[i], part.Normals[i + 1], part.Normals[i + 2]));
                    }
                    // Terrain UVs span the whole preset once; biome layers tile by world position instead.
                    if (part.Surface == SurfaceKind.Biome)
                        for (int i = 0; i < part.Positions.Length; i += 3)
                            geometry.TextureCoordinates.Add(new(part.Positions[i] / TerrainTextureSpan, part.Positions[i + 2] / TerrainTextureSpan));
                    else
                        for (int i = 0; i < part.UVs.Length; i += 2) geometry.TextureCoordinates.Add(new(part.UVs[i], part.UVs[i + 1]));
                    for (int i = 0; i < part.Indices.Length; i += 3)
                    { geometry.TriangleIndices.Add(part.Indices[i]); geometry.TriangleIndices.Add(part.Indices[i + 1]); geometry.TriangleIndices.Add(part.Indices[i + 2]); }
                    Material material = part.Surface switch
                    {
                        SurfaceKind.Textured when part.AlbedoPath is { } albedo => Texture(albedo),
                        SurfaceKind.Biome => BiomeMaterial(part.SurfaceDetail!),
                        SurfaceKind.Water => PreviewMaterials.Water,
                        SurfaceKind.Blood => PreviewMaterials.Blood,
                        _ => defaultMaterial,
                    };
                    if (part.Surface == SurfaceKind.Unknown) messages.Add($"Unrecognized material ({part.SurfaceDetail}): {path} / {part.Name}");
                    model.Children.Add(new GeometryModel3D(geometry, material) { BackMaterial = material });
                }
                model.Freeze();
            }
            catch (MissingModelDecoderException) { needsDecoder.Add(path); model = null; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { messages.Add($"Model {path}: {ex.Message}"); model = null; }
            models[path] = model;
            return model;
        }
        // Prefab contents are flattened into the placement's local space and cached per file. They are only
        // previewed: the placement keeps its single transform and the prefab JSON is never edited.
        var prefabs = new Dictionary<string, Prefab?>(StringComparer.OrdinalIgnoreCase);
        var expanding = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Prefab? LoadPrefab(string path)
        {
            if (prefabs.TryGetValue(path, out var prefab)) return prefab;
            if (!expanding.Add(path)) { messages.Add($"Prefab cycle skipped: {path}"); return null; }
            try
            {
                if (resolver is null) throw new InvalidOperationException("No asset folder selected.");
                var source = PresetDocument.Load(resolve?.Invoke(path, false) ?? resolver.Resolve(path));
                var group = new Model3DGroup(); int missing = 0, blocked = 0, nested = 0;
                foreach (var child in source.Entities)
                {
                    token.ThrowIfCancellationRequested();
                    if (!child.CanTransform) continue;
                    Model3DGroup? geometry;
                    if (child.PrefabPath is { } inner)
                    {
                        nested++;
                        if (LoadPrefab(inner) is not { } expanded) { missing++; continue; }
                        geometry = expanded.Geometry; missing += expanded.Missing; blocked += expanded.Blocked; nested += expanded.Nested;
                    }
                    else if (child.PreviewModel is { } model)
                    {
                        geometry = LoadModel(model);
                        if (geometry is null) { if (needsDecoder.Contains(model)) blocked++; else missing++; continue; }
                    }
                    else continue;
                    var matrix = SceneViewport.Transform(child.Transform).Value;
                    foreach (GeometryModel3D part in geometry.Children) group.Children.Add(SceneBatch.Bake(part, matrix));
                }
                group.Freeze();
                prefab = new(group, missing, blocked, nested);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { messages.Add($"Prefab {path}: {ex.Message}"); prefab = null; }
            expanding.Remove(path);
            prefabs[path] = prefab;
            return prefab;
        }
        int prefabPlacements = 0, prefabsExpanded = 0, prefabMissing = 0, prefabBlocked = 0, prefabNested = 0;
        foreach (var entity in doc.Entities)
        {
            token.ThrowIfCancellationRequested();
            if (!entity.CanTransform) continue;
            if (entity.HasParent) { messages.Add($"Parented entity not previewed: {entity.Name}"); continue; }
            var path = entity.PreviewModel;
            var prefabPath = path is null ? entity.PrefabPath : null;
            if (path is null && prefabPath is null) continue;
            if (timer.ElapsedMilliseconds - lastProgress >= 100)
            {
                lastProgress = timer.ElapsedMilliseconds;
                progress.Report($"Loading {items.Count + 1} / {doc.Entities.Count} · {entity.Name}");
            }
            if (prefabPath is not null)
            {
                prefabPlacements++;
                var prefab = LoadPrefab(prefabPath);
                // A prefab of only lights, sounds or effects has nothing to draw, like other mesh-less entities.
                if (prefab is { Geometry.Children.Count: 0, Missing: 0, Blocked: 0 }) continue;
                if (prefab is not null) { prefabMissing += prefab.Missing; prefabBlocked += prefab.Blocked; prefabNested += prefab.Nested; }
                bool drawn = prefab is { Geometry.Children.Count: > 0 };
                if (drawn) { prefabsExpanded++; loaded++; }
                items.Add(new(entity, drawn ? prefab!.Geometry : proxy, !drawn));
                continue;
            }
            var model = LoadModel(path!);
            if (model is not null) loaded++;
            items.Add(new(entity, model ?? proxy, model is null, modelTextures.GetValueOrDefault(path!)));
        }
        if (doc.Entities.Any(e => e.ModelPaths.Count > 1)) messages.Add("Variation preview uses the first listed model; all variants remain preserved in JSON.");
        messages.Add("Preview: static meshes + albedo. Terrain without a paired DS1 shows its biome's base layer; water is a flat tint. Particles, biome blending and skeletal animation are not rendered. DS1 NPC previews use a static reference pose.");
        long Triangles(Model3D model) => model is GeometryModel3D { Geometry: MeshGeometry3D mesh } ? mesh.TriangleIndices.Count / 3 :
            model is Model3DGroup group ? group.Children.Sum(Triangles) : 0;
        messages.Add($"Scene metrics: {(fullDetail ? "Full detail" : $"Balanced (LOD{preferredLod} / 512px)")} · {items.Sum(i => Triangles(i.Geometry)):N0} instanced triangles · {textureBytes / 1048576d:F1} MiB decoded textures · {timer.Elapsed.TotalSeconds:F2}s load");
        if (prefabPlacements > 0)
            messages.Add($"Prefabs: {prefabsExpanded} / {prefabPlacements} placements expanded read-only from {prefabs.Count(p => p.Value is not null)} prefab files · {prefabNested} nested placements · {prefabMissing + prefabBlocked} prefab meshes missing");
        foreach (var group in items.GroupBy(i => i.Entity.PreviewModel ?? i.Entity.PrefabPath).OrderByDescending(g => g.Sum(i => Triangles(i.Geometry))).Take(5))
            messages.Add($"Heavy mesh: {group.Sum(i => Triangles(i.Geometry)):N0} triangles across {group.Count()} instances · {group.Key}");
        SceneBatch[]? batches = null;
        if (items.Count > 200)
        {
            progress.Report("Combining scene geometry for rendering…");
            var batching = System.Diagnostics.Stopwatch.StartNew();
            batches = SceneBatch.Create(items, token);
            messages.Add($"Render batches: {items.Sum(i => i.Geometry.Children.Count):N0} mesh submissions → {batches.Length:N0} spatial batches · {batching.Elapsed.TotalSeconds:F2}s preparation");
        }
        int blocked = items.Count(i => i.Entity.PreviewModel is { } path && needsDecoder.Contains(path)) + prefabBlocked;
        var diagnostics = messages.ToList();
        if (blocked > 0) diagnostics.Insert(0, $"{blocked} model instances need a Granny decoder. The files were found, but their compressed geometry cannot be read. Click Granny decoder… and choose a 64-bit granny2.dll; the scene reloads automatically.");
        return new(items, diagnostics, loaded, batches, blocked);
    }

    private sealed record Prefab(Model3DGroup Geometry, int Missing, int Blocked, int Nested);
}
