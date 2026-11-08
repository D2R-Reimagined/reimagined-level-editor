using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using D2RLevel.Assets;

namespace D2RLevel.App;

/// <summary>Builds the albedo material used by every preview surface, in one place.</summary>
internal static class PreviewMaterials
{
    public static Material Default { get; } = Frozen(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(144, 153, 147))));

    /// <summary>Stand-ins for the water shader, which has no color map of its own.</summary>
    public static Material Water { get; } = Liquid(Color.FromRgb(46, 78, 92));
    public static Material Blood { get; } = Liquid(Color.FromRgb(92, 16, 14));

    private static readonly DependencyProperty BiomeLayerProperty =
        DependencyProperty.RegisterAttached("BiomeLayer", typeof(bool), typeof(PreviewMaterials), new PropertyMetadata(false));

    /// <summary>
    /// A distinct copy of a biome layer texture, marked so <see cref="TerrainPreview"/> still paints
    /// the paired DS1 floor over it: that shows the level's real layout, the single layer does not.
    /// </summary>
    public static Material BiomeLayer(Material texture)
    {
        var material = new DiffuseMaterial(((DiffuseMaterial)texture).Brush);
        material.SetValue(BiomeLayerProperty, true);
        return Frozen(material);
    }

    public static bool IsBiomeLayer(Material material) => (bool)material.GetValue(BiomeLayerProperty);

    private static readonly DependencyProperty TransparencyProperty =
        DependencyProperty.RegisterAttached("Transparency", typeof(bool), typeof(PreviewMaterials), new PropertyMetadata(false));

    /// <summary>
    /// True for an albedo with cut-out areas (foliage cards, decals, broken panes). Such surfaces are
    /// seen from both sides, so they keep a back material; opaque ones are drawn front-facing only.
    /// </summary>
    public static bool HasTransparency(Material material) => (bool)material.GetValue(TransparencyProperty);

    private static Material Frozen(Material material) { material.Freeze(); return material; }

    private static Material Liquid(Color color) => Frozen(new MaterialGroup
    {
        Children = new MaterialCollection { new DiffuseMaterial(new SolidColorBrush(color)), new SpecularMaterial(new SolidColorBrush(Color.FromRgb(150, 160, 165)), 40) },
    });

    /// <summary>Decodes an albedo texture into a tiled diffuse material.</summary>
    public static Material FromTexture(string physicalPath, int maxSize, out int decodedBytes)
    {
        var pixels = TextureReader.Load(physicalPath, maxSize);
        decodedBytes = pixels.Rgba.Length;
        // WPF's Bgra32 format needs a red/blue swap.
        int cutout = 0;
        for (int i = 0; i < pixels.Rgba.Length; i += 4)
        {
            (pixels.Rgba[i], pixels.Rgba[i + 2]) = (pixels.Rgba[i + 2], pixels.Rgba[i]);
            if (pixels.Rgba[i + 3] < 128) cutout++;
        }
        var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Rgba, pixels.Width * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
        var material = new DiffuseMaterial(brush);
        // A few stray low-alpha texels (compression noise, padding) do not make a surface see-through.
        if (cutout > pixels.Rgba.Length / 4 / 1000) material.SetValue(TransparencyProperty, true);
        return Frozen(material);
    }
}
