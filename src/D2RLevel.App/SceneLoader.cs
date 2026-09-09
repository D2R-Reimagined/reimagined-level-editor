using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using D2RLevel.Core;
using D2RLevel.Assets;

namespace D2RLevel.App;

public static class SceneLoader
{
    public static LoadedScene Load(PresetDocument doc, AssetResolver? resolver, IProgress<string> progress, CancellationToken token, bool fullDetail = true, Func<string, bool, string>? resolve = null)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        int preferredLod = fullDetail ? 0 : doc.Entities.Count > 2000 ? 4 : 2;
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
        foreach (var entity in doc.Entities)
        {
            token.ThrowIfCancellationRequested();
            if (!entity.CanTransform) continue;
            if (entity.HasParent) { messages.Add($"Parented entity not previewed: {entity.Name}"); continue; }
            var path = entity.PreviewModel;
            if (path is null) continue;
            if (timer.ElapsedMilliseconds - lastProgress >= 100)
            {
                lastProgress = timer.ElapsedMilliseconds;
                progress.Report($"Loading {items.Count + 1} / {doc.Entities.Count} · {entity.Name}");
            }
            if (!models.TryGetValue(path, out var model))
            {
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
                        for (int i = 0; i < part.UVs.Length; i += 2) geometry.TextureCoordinates.Add(new(part.UVs[i], part.UVs[i + 1]));
                        for (int i = 0; i < part.Indices.Length; i += 3)
                        { geometry.TriangleIndices.Add(part.Indices[i]); geometry.TriangleIndices.Add(part.Indices[i + 1]); geometry.TriangleIndices.Add(part.Indices[i + 2]); }
                        Material material = defaultMaterial;
                        if (part.AlbedoPath is { } albedo)
                        {
                            if (!textures.TryGetValue(albedo, out var cached))
                            {
                                cached = material;
                                try
                                {
                                    var pixels = TextureReader.Load(resolve?.Invoke(albedo, false) ?? resolver.Resolve(albedo), fullDetail ? 1024 : 512);
                                    textureBytes += pixels.Rgba.Length;
                                    // WPF's Bgra32 format needs a red/blue swap.
                                    for (int i = 0; i < pixels.Rgba.Length; i += 4)
                                        (pixels.Rgba[i], pixels.Rgba[i + 2]) = (pixels.Rgba[i + 2], pixels.Rgba[i]);
                                    var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Rgba, pixels.Width * 4);
                                    bitmap.Freeze();
                                    var brush = new ImageBrush(bitmap) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
                                    cached = new DiffuseMaterial(brush);
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                { messages.Add($"Texture {albedo}: {ex.Message}"); }
                                cached.Freeze(); textures[albedo] = cached;
                            }
                            material = cached;
                        }
                        else messages.Add($"No albedo reference: {path} / {part.Name}");
                        model.Children.Add(new GeometryModel3D(geometry, material) { BackMaterial = material });
                    }
                    model.Freeze();
                }
                catch (MissingModelDecoderException) { needsDecoder.Add(path); model = null; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { messages.Add($"Model {path}: {ex.Message}"); model = null; }
                models[path] = model;
            }
            if (model is not null) loaded++;
            items.Add(new(entity, model ?? proxy, model is null, modelTextures.GetValueOrDefault(path)));
        }
        if (doc.Entities.Any(e => e.ModelPaths.Count > 1)) messages.Add("Variation preview uses the first listed model; all variants remain preserved in JSON.");
        messages.Add("Preview: static meshes + albedo. Particles, biome shading, skeletal animation is not rendered. DS1 NPC previews use a static reference pose.");
        long Triangles(Model3D model) => model is GeometryModel3D { Geometry: MeshGeometry3D mesh } ? mesh.TriangleIndices.Count / 3 :
            model is Model3DGroup group ? group.Children.Sum(Triangles) : 0;
        messages.Add($"Scene metrics: {(fullDetail ? "Full detail" : $"Balanced (LOD{preferredLod} / 512px)")} · {items.Sum(i => Triangles(i.Geometry)):N0} instanced triangles · {textureBytes / 1048576d:F1} MiB decoded textures · {timer.Elapsed.TotalSeconds:F2}s load");
        foreach (var group in items.GroupBy(i => i.Entity.PreviewModel).OrderByDescending(g => g.Sum(i => Triangles(i.Geometry))).Take(5))
            messages.Add($"Heavy mesh: {group.Sum(i => Triangles(i.Geometry)):N0} triangles across {group.Count()} instances · {group.Key}");
        SceneBatch[]? batches = null;
        if (items.Count > 200)
        {
            progress.Report("Combining scene geometry for rendering…");
            var batching = System.Diagnostics.Stopwatch.StartNew();
            batches = SceneBatch.Create(items, token);
            messages.Add($"Render batches: {items.Sum(i => i.Geometry.Children.Count):N0} mesh submissions → {batches.Length:N0} spatial batches · {batching.Elapsed.TotalSeconds:F2}s preparation");
        }
        int blocked = items.Count(i => i.Entity.PreviewModel is { } path && needsDecoder.Contains(path));
        var diagnostics = messages.ToList();
        if (blocked > 0) diagnostics.Insert(0, $"{blocked} model instances need a Granny decoder. The files were found, but their compressed geometry cannot be read. Click Granny decoder… and choose a 64-bit granny2.dll; the scene reloads automatically.");
        return new(items, diagnostics, loaded, batches, blocked);
    }
}
