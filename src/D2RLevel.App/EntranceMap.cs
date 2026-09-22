using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using D2RLevel.Core;

namespace D2RLevel.App;

/// <summary>Tile-coordinate overview; empty space clears selection unless move is explicitly armed.</summary>
internal sealed class EntranceMap : FrameworkElement
{
    private Ds1CollisionDocument? map;
    private DrawingGroup? terrain;
    private int? selected;
    public event Action<int?>? Selected;
    public event Action<int, int>? MoveRequested;
    public bool MoveArmed { get; set; }
    public int? Selection { get => selected; set { selected = value; InvalidateVisual(); } }
    public void SetMap(Ds1CollisionDocument? value)
    {
        map = value; terrain = new();
        if (map is not null)
        {
            using var dc = terrain.Open();
            var floor = new StreamGeometry(); using (var g = floor.Open())
                for (int y = 0; y < map.Height; y++) for (int x = 0; x < map.Width; x++)
                    if (map.CanBlock(x, y)) Rectangle(g, x, y);
            floor.Freeze(); dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(50, 65, 77)), null, floor);
            var walls = new StreamGeometry(); using (var g = walls.Open())
                for (int y = 0; y < map.Height; y++) for (int x = 0; x < map.Width; x++)
                    if (map.Walls.Any(w => (map.Cell(w, x, y) & 255) != 0 && w.Orientations[y * map.Width + x] is not 10 and not 11)) Rectangle(g, x, y);
            walls.Freeze(); dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(94, 105, 114)), null, walls);
        }
        terrain.Freeze(); InvalidateVisual();
    }
    private static void Rectangle(StreamGeometryContext g, int x, int y)
    {
        g.BeginFigure(new(x, y), true, true); g.LineTo(new(x + 1, y), true, false); g.LineTo(new(x + 1, y + 1), true, false); g.LineTo(new(x, y + 1), true, false);
    }
    private (double Scale, double X, double Y) Layout()
    {
        double scale = map is null ? 1 : Math.Max(.01, Math.Min((ActualWidth - 32) / map.Width, (ActualHeight - 40) / map.Height));
        return (scale, (ActualWidth - (map?.Width ?? 0) * scale) / 2, (ActualHeight - (map?.Height ?? 0) * scale) / 2);
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(14, 21, 29)), null, new Rect(RenderSize));
        if (map is null) { Label(dc, L.T("No fixed preset map"), 16, 20, Brushes.LightSteelBlue); return; }
        var (s, ox, oy) = Layout();
        dc.PushTransform(new MatrixTransform(s, 0, 0, s, ox, oy)); dc.DrawDrawing(terrain); dc.Pop();
        foreach (var tile in map.ExitTiles())
        {
            var point = new Point(ox + (tile.X + .5) * s, oy + (tile.Y + .5) * s);
            if (tile.IsExit)
            {
                var brush = tile.Main == selected ? Brushes.Gold : Brushes.Turquoise;
                dc.DrawEllipse(brush, new Pen(Brushes.Black, 1), point, 9, 9);
                Label(dc, tile.Main.ToString(), point.X - 4, point.Y - 8, Brushes.Black);
            }
            else dc.DrawRectangle(Brushes.MediumPurple, null, new Rect(point.X - 3, point.Y - 3, 6, 6));
        }
        Label(dc, L.T("{0} × {1} tiles", map.Width, map.Height), 8, ActualHeight - 22, Brushes.LightSteelBlue);
    }
    private void Label(DrawingContext dc, string text, double x, double y, Brush brush) => dc.DrawText(
        new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
    internal void ClickTile(int x, int y)
    {
        if (map is null || x < 0 || y < 0 || x >= map.Width || y >= map.Height) { Selected?.Invoke(null); return; }
        if (MoveArmed && selected is not null) { MoveRequested?.Invoke(x, y); return; }
        Selected?.Invoke(map.ExitTiles().FirstOrDefault(t => t.IsExit && t.X == x && t.Y == y)?.Main);
    }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e); if (e.ChangedButton != MouseButton.Left) return;
        Focus(); var (s, ox, oy) = Layout(); var p = e.GetPosition(this);
        if (!MoveArmed && map is not null)
        {
            var hit = map.ExitTiles().Where(t => t.IsExit).OrderBy(t => Math.Pow(ox + (t.X + .5) * s - p.X, 2) + Math.Pow(oy + (t.Y + .5) * s - p.Y, 2))
                .FirstOrDefault(t => Math.Abs(ox + (t.X + .5) * s - p.X) <= 10 && Math.Abs(oy + (t.Y + .5) * s - p.Y) <= 10);
            if (hit is not null) { Selected?.Invoke(hit.Main); e.Handled = true; return; }
        }
        ClickTile((int)Math.Floor((p.X - ox) / s), (int)Math.Floor((p.Y - oy) / s)); e.Handled = true;
    }
}
