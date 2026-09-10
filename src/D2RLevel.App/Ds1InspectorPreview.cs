using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using D2RLevel.Core;

namespace D2RLevel.App;

public sealed class Ds1InspectorPreview : StackPanel
{
    private readonly TextBlock status = new() { Text = "Open a JSON preset to find its DS1.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly Image preview = new() { Height = 180, Stretch = Stretch.Uniform, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock position = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSteelBlue };
    private readonly TextBlock calibrationText = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSteelBlue, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock calibrationWarning = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(228, 186, 116)), Margin = new Thickness(0, 2, 0, 0) };
    private readonly Dictionary<Dt1Floor, BitmapSource> textures = new();
    private LegacyFloorScene? scene;
    private PresetEntity? selected;
    private string pairStatus = "";
    private double magnification = .55;
    public event Action? OpenRequested;
    /// <summary>This pair's HD-to-DS1 registration. Every view that converts between the
    /// two formats reads it here, so the scale can never differ between panels.</summary>
    public GridCalibration Calibration { get; private set; } = GridCalibration.Unverified;
    public double UnitsPerTile => Calibration.UnitsPerTile;
    public event Action? ScaleChanged;
    public event Action? CalibrateRequested;
    public void SetCalibration(GridCalibration value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Calibration == value) return;
        Calibration = value; Refresh(); ScaleChanged?.Invoke();
    }
    public bool HasPreview => preview.Source is not null;
    public string StatusText => status.Text;
    public Ds1InspectorPreview()
    {
        Children.Add(new TextBlock { Text = "DS1 · LOCAL COLLISION", FontWeight = FontWeights.Bold }); Children.Add(status); Children.Add(preview); Children.Add(position);
        var actions = new WrapPanel(); Children.Add(actions);
        void Button(string label, Action action)
        { var b = new Button { Content = label, Padding = new Thickness(7, 4, 7, 4) }; b.Click += (_, _) => action(); actions.Children.Add(b); }
        Button("−", () => { magnification = Math.Max(.2, magnification / 1.4); Refresh(); });
        Button("+", () => { magnification = Math.Min(1.5, magnification * 1.4); Refresh(); });
        Button("Open DS1…", () => OpenRequested?.Invoke());
        Children.Add(new TextBlock { Text = "GRID CALIBRATION", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 0) });
        Children.Add(calibrationText); Children.Add(calibrationWarning);
        var scale = new WrapPanel(); Children.Add(scale);
        var calibrate = new Button { Content = "Calibrate…", Padding = new Thickness(7, 4, 7, 4),
            ToolTip = "Measure this pair's HD-to-DS1 scale from terrain or from objects already linked, or enter it by hand." };
        calibrate.Click += (_, _) => CalibrateRequested?.Invoke();
        scale.Children.Add(calibrate);
        RefreshCalibration();
    }
    private void RefreshCalibration()
    {
        calibrationText.Text = Calibration.Describe();
        calibrationWarning.Text = Calibration.Warning ?? "";
        calibrationWarning.Visibility = Calibration.Warning is null ? Visibility.Collapsed : Visibility.Visible;
    }
    public void SetScene(LegacyFloorScene? next, string message)
    { if (!ReferenceEquals(scene, next)) textures.Clear(); scene = next; pairStatus = message; Refresh(); }
    public void Select(PresetEntity? entity) { selected = entity; Refresh(); }
    private BitmapSource Texture(Dt1Floor tile)
    {
        if (textures.TryGetValue(tile, out var cached)) return cached;
        var pixels = new byte[160 * 80 * 4];
        for (int i = 0; i < tile.Pixels.Length; i++)
        {
            int index = tile.Pixels[i]; if (index == 0) continue;
            for (int c = 0; c < 3; c++) pixels[i * 4 + c] = scene!.Palette[index * 3 + c];
            pixels[i * 4 + 3] = 255;
        }
        var bitmap = BitmapSource.Create(160, 80, 96, 96, PixelFormats.Bgra32, null, pixels, 640); bitmap.Freeze(); textures[tile] = bitmap; return bitmap;
    }
    private static Geometry Diamond(double x, double y, double w, double h)
    {
        var geometry = new StreamGeometry(); using (var c = geometry.Open())
        { c.BeginFigure(new(x + w / 2, y), true, true); c.LineTo(new(x + w, y + h / 2), true, false); c.LineTo(new(x + w / 2, y + h), true, false); c.LineTo(new(x, y + h / 2), true, false); }
        geometry.Freeze(); return geometry;
    }
    public void Refresh()
    {
        preview.Source = null; preview.Visibility = Visibility.Collapsed; status.Text = pairStatus; position.Text = "";
        RefreshCalibration();
        if (scene is null) return;
        if (selected?.CanTransform != true || selected.HasParent) { position.Text = "Select an unparented HD object to inspect its DS1 surroundings."; return; }
        double scale = Calibration.UnitsPerTile;
        double x = selected.Transform.Position.X / scale, y = selected.Transform.Position.Z / scale;
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= scene.Map.Width || y >= scene.Map.Height)
        { position.Text = "Selected HD object lies outside this DS1 at the current scale. Check alignment."; return; }
        position.Text = $"Approx. tile ({x:F1}, {y:F1}) · read-only\nRed: blocked · Orange: override · Purple: unresolved";
        if (scene.Collision?.Document.IsDirty == true) status.Text += " · unsaved DS1 edits";
        var visual = new DrawingVisual();
        var blocked = new SolidColorBrush(Color.FromArgb(120, 235, 40, 40));
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(20, 27, 35)), null, new Rect(0, 0, 280, 180));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, 280, 180)));
            dc.PushTransform(new TranslateTransform(140 - (x - y) * 80 * magnification, 90 - (x + y) * 40 * magnification));
            dc.PushTransform(new ScaleTransform(magnification, magnification));
            int radius = (int)Math.Ceiling(280 / (80 * magnification)) + 2;
            for (int ty = Math.Max(0, (int)y - radius); ty <= Math.Min(scene.Map.Height - 1, (int)y + radius); ty++)
            for (int tx = Math.Max(0, (int)x - radius); tx <= Math.Min(scene.Map.Width - 1, (int)x + radius); tx++)
            {
                double px = (tx - ty - 1) * 80, py = (tx + ty) * 40;
                double screenX = 140 + (px - (x - y) * 80) * magnification, screenY = 90 + (py - (x + y) * 40) * magnification;
                if (screenX > 280 || screenX + 160 * magnification < 0 || screenY > 180 || screenY + 80 * magnification < 0) continue;
                foreach (var layer in scene.Map.Layers)
                {
                    var cell = layer[ty * scene.Map.Width + tx];
                    if (!cell.IsEmpty && scene.Tiles.TryGetValue((cell.Main, cell.Sub), out var tiles)) dc.DrawImage(Texture(tiles[0]), new Rect(px, py, 160, 80));
                }
                if (scene.Collision is { } collision)
                {
                    var tile = collision.At(tx, ty);
                    if (tile.Override || tile.Unresolved) dc.DrawGeometry(new SolidColorBrush(tile.Override ? Color.FromArgb(145, 255, 140, 20) : Color.FromArgb(140, 175, 40, 200)), null, Diamond(px, py, 160, 80));
                    else for (int sy = 0; sy < 5; sy++) for (int sx = 0; sx < 5; sx++)
                        if ((tile.Flags[sy * 5 + sx] & 9) != 0) dc.DrawGeometry(blocked, null, Diamond(px + 64 + (sx - sy) * 16, py + (sx + sy) * 8, 32, 16));
                }
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(70, 190, 220, 230)), 1 / magnification), Diamond(px, py, 160, 80));
            }
            dc.Pop(); dc.Pop(); dc.Pop();
            var pen = new Pen(Brushes.Cyan, 2); dc.DrawEllipse(null, pen, new Point(140, 90), 6, 6);
            dc.DrawLine(pen, new Point(128, 90), new Point(152, 90)); dc.DrawLine(pen, new Point(140, 78), new Point(140, 102));
        }
        var result = new RenderTargetBitmap(280, 180, 96, 96, PixelFormats.Pbgra32); result.Render(visual); result.Freeze(); preview.Source = result; preview.Visibility = Visibility.Visible;
    }
}
