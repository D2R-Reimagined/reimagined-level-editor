using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using D2RLevel.Core;

namespace D2RLevel.App;

// A placement reference on the original terrain geometry, not an HD biome shader.
public static class TerrainPreview
{
    public static LoadedScene Apply(LoadedScene loaded, LegacyFloorScene? scene, CancellationToken token)
    {
        if (!loaded.Items.Any(i => i.Entity.IsTerrain && !i.IsPlaceholder)) return loaded;
        if (scene is null) return loaded with { Diagnostics = loaded.Diagnostics.Append("Terrain: no paired DS1 floor reference; showing untextured terrain geometry.").ToArray() };
        int size = Math.Clamp(2048 / Math.Max(scene.Map.Width, scene.Map.Height), 4, 32);
        int width = scene.Map.Width * size, height = scene.Map.Height * size;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < scene.Map.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < scene.Map.Width; x++)
            {
                var tiles = scene.Map.Layers.Select(l => l[y * scene.Map.Width + x])
                    .Where(c => !c.IsEmpty).Select(c => scene.Tiles.GetValueOrDefault((c.Main, c.Sub))?.FirstOrDefault()).OfType<Dt1Floor>().ToArray();
                for (int v = 0; v < size; v++) for (int u = 0; u < size; u++)
                {
                    int target = ((y * size + v) * width + x * size + u) * 4;
                    pixels[target] = 22; pixels[target + 1] = 28; pixels[target + 2] = 24; pixels[target + 3] = 255;
                    // Unproject a classic isometric diamond to the DS1 tile's top-down square.
                    int px = Math.Clamp((int)(80 + (u - v) * 80d / size), 0, 159);
                    int py = Math.Clamp((int)((u + v + 1) * 40d / size), 0, 79);
                    foreach (var tile in tiles)
                    {
                        int index = tile.Pixels[py * 160 + px]; if (index == 0) continue;
                        for (int c = 0; c < 3; c++) pixels[target + c] = scene.Palette[index * 3 + c];
                    }
                }
            }
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); bitmap.Freeze();
        var material = new DiffuseMaterial(new ImageBrush(bitmap) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) }); material.Freeze();
        var items = loaded.Items.Select(item =>
        {
            if (!item.Entity.IsTerrain || item.IsPlaceholder) return item;
            var model = new Model3DGroup(); var transform = SceneViewport.Transform(item.Entity.Transform);
            foreach (GeometryModel3D part in item.Geometry.Children)
            {
                token.ThrowIfCancellationRequested();
                if (part.Material is DiffuseMaterial { Brush: ImageBrush }) { model.Children.Add(part); continue; }
                var mesh = ((MeshGeometry3D)part.Geometry).Clone(); mesh.TextureCoordinates.Clear();
                foreach (var vertex in mesh.Positions)
                {
                    var world = transform.Transform(vertex);
                    mesh.TextureCoordinates.Add(new(world.X / (scene.Map.Width * 10d), world.Z / (scene.Map.Height * 10d)));
                }
                model.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
            }
            model.Freeze(); return item with { Geometry = model };
        }).ToArray();
        return loaded with { Items = items, Batches = loaded.Batches is null ? null : SceneBatch.Create(items, token),
            Diagnostics = loaded.Diagnostics.Append($"Terrain: paired DS1 floor reference on original mesh ({width}×{height}); approximate 10 HD units/tile. HD biome materials, blends and stamps are not reproduced.").ToArray() };
    }
}
