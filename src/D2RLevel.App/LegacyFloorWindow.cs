using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using D2RLevel.Core;

namespace D2RLevel.App;

public sealed partial class LegacyFloorWindow : Window
{
    private LegacyFloorScene scene;
    private readonly Image image = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock inspector = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10) };
    private readonly CheckBox[] layers;
    private readonly CheckBox grid = new() { Content = "Tile grid", Foreground = Brushes.White, Margin = new Thickness(12), IsChecked = true };
    private readonly Dictionary<Dt1Floor, BitmapSource> bitmaps = new();
    private readonly double scale;
    private readonly Slider zoom = new() { Minimum = 0.1, Maximum = 2, Value = 0.35, Width = 180, Margin = new Thickness(12) };
    private ScrollViewer? collisionScroll;
    public LegacyFloorScene FloorScene => scene;

    public LegacyFloorWindow(LegacyFloorScene scene, ModelFootprint? footprint = null)
    {
        this.scene = scene;
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/ReimaginedLevelEditor.ico"));
        Title = "DS1 collision and floors · " + Path.GetFileName(scene.Ds1Path); Width = 1250; Height = 850;
        Background = new SolidColorBrush(Color.FromRgb(20, 27, 35));
        scale = Math.Min(1, 3000d / ((scene.Map.Width + scene.Map.Height) * 80));
        var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
        var heading = new TextBlock { Text = $"DS1 v{scene.Map.Version} · {scene.Map.Width} × {scene.Map.Height} tiles · Act {scene.Map.Act} · Dt1Mask {scene.Mask} · {scene.Dt1Paths.Length} DT1 files · {scene.MissingCells} unresolved floor cells", Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var source = new TextBlock { Text = "DS1: " + scene.Ds1Path, Margin = new Thickness(8, 0, 8, 4), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(source, Dock.Top); root.Children.Add(source);
        var controls = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(controls, Dock.Top); root.Children.Add(controls);
        layers = Enumerable.Range(0, scene.Map.Layers.Length).Select(i => new CheckBox { Content = $"Floor {i + 1}", IsChecked = true, Foreground = Brushes.White, Margin = new Thickness(12) }).ToArray();
        foreach (var toggle in layers.Append(grid)) { controls.Children.Add(toggle); toggle.Click += (_, _) => Render(); }
        controls.Children.Add(new TextBlock { Text = "Zoom", VerticalAlignment = VerticalAlignment.Center }); controls.Children.Add(zoom);
        zoom.ValueChanged += (_, _) => ResizeImage();
        InitializeCollision(root);
        InitializeFootprint(root, footprint);
        InitializeGameplay(root);
        InitializePaths(root);
        var note = new TextBlock { Text = "Collision tools edit the real DS1 whole-tile Unwalkable flag. Clearing overrides retains DT1 wall/floor blocking.\nRed: blocked subtiles · Orange: DS1 override · Purple: unresolved · Yellow outline: variant-dependent. Draft cyan: footprint · Draft magenta: shared ownership.\nLinked HD moves translate their unit and owned collision together on release. Other owners and protected blocking remain. Save linked pair to export both maps and ownership.", Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(note, Dock.Bottom); root.Children.Add(note);
        DockPanel.SetDock(inspector, Dock.Bottom); root.Children.Add(inspector);
        var imageHost = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        imageHost.Children.Add(image); imageHost.Children.Add(strokePreview);
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = imageHost };
        collisionScroll = scroll;
        var viewport = new Grid(); viewport.Children.Add(scroll); root.Children.Add(viewport);
        HookPanning(scroll);
        image.MouseLeftButtonDown += (_, e) =>
        {
            var point = e.GetPosition(image);
            double a = point.X / zoom.Value / scale / 80 - scene.Map.Height, b = point.Y / zoom.Value / scale / 40;
            int x = (int)Math.Floor((a + b) / 2), y = (int)Math.Floor((b - a) / 2);
            if (x < 0 || y < 0 || x >= scene.Map.Width || y >= scene.Map.Height) return;
            inspector.Text = $"Tile ({x}, {y})\n" + string.Join("\n", scene.Map.Layers.Select((l, i) =>
            {
                var cell = scene.Collision is { } collision ? new FloorCell(collision.Document.Cell(collision.Document.Floors[i], x, y)) : l[y * scene.Map.Width + x];
                var found = scene.Tiles.GetValueOrDefault((cell.Main, cell.Sub));
                return $"Floor {i + 1}: " + (cell.IsEmpty ? "empty" : $"main {cell.Main}, sub {cell.Sub}, raw 0x{cell.Raw:X8} · " +
                    (found is null ? "UNRESOLVED" : $"{Path.GetFileName(found[0].Source)} · {found.Length} variant(s)"));
            }));
            InspectCollision(x, y);
        };
        HookPainting();
        Render();
    }

    private BitmapSource Bitmap(Dt1Floor tile)
    {
        if (bitmaps.TryGetValue(tile, out var bitmap)) return bitmap;
        byte[] pixels = new byte[160 * 80 * 4];
        for (int i = 0; i < tile.Pixels.Length; i++)
        {
            int index = tile.Pixels[i];
            if (index == 0) continue;
            for (int c = 0; c < 3; c++) pixels[i * 4 + c] = scene.Palette[index * 3 + c];
            pixels[i * 4 + 3] = 255;
        }
        bitmap = BitmapSource.Create(160, 80, 96, 96, PixelFormats.Bgra32, null, pixels, 640);
        bitmap.Freeze(); bitmaps[tile] = bitmap; return bitmap;
    }

    private void Render()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            for (int l = 0; l < layers.Length; l++)
            {
                if (layers[l].IsChecked != true) continue;
                for (int y = 0; y < scene.Map.Height; y++) for (int x = 0; x < scene.Map.Width; x++)
                {
                    var cell = scene.Map.Layers[l][y * scene.Map.Width + x]; if (cell.IsEmpty) continue;
                    double px = (x - y + scene.Map.Height - 1) * 80, py = (x + y) * 40;
                    if (scene.Tiles.TryGetValue((cell.Main, cell.Sub), out var variants)) dc.DrawImage(Bitmap(variants[0]), new Rect(px, py, 160, 80));
                    else dc.DrawGeometry(Brushes.DarkMagenta, null, Diamond(px, py));
                }
            }
            if (grid.IsChecked == true)
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(65, 160, 200, 210)), 1 / scale);
                for (int y = 0; y < scene.Map.Height; y++) for (int x = 0; x < scene.Map.Width; x++)
                    dc.DrawGeometry(null, pen, Diamond((x - y + scene.Map.Height - 1) * 80, (x + y) * 40));
            }
            DrawCollision(dc);
            DrawGameplay(dc);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling((scene.Map.Width + scene.Map.Height) * 80 * scale),
            (int)Math.Ceiling((scene.Map.Width + scene.Map.Height) * 40 * scale), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); image.Source = bitmap;
        ResizeImage();
    }
    private void ResizeImage()
    {
        if (image.Source is BitmapSource bitmap) { image.Width = bitmap.PixelWidth * zoom.Value; image.Height = bitmap.PixelHeight * zoom.Value; }
    }
    private static Geometry Diamond(double x, double y)
    {
        var g = new StreamGeometry();
        using var c = g.Open(); c.BeginFigure(new Point(x + 80, y), true, true);
        c.LineTo(new Point(x + 160, y + 40), true, false); c.LineTo(new Point(x + 80, y + 80), true, false); c.LineTo(new Point(x, y + 40), true, false);
        return g;
    }
}
