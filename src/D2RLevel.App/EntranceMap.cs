using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using D2RLevel.Core;

namespace D2RLevel.App;

/// <summary>
/// Tile-coordinate overview; empty space clears selection unless move is explicitly armed. Hovering an exit (or the selected
/// exit while nothing is hovered) draws its warp: the selection box the mouse highlights, where arriving players appear
/// and where they walk to. While a move is armed, the selected group follows the pointer, green where it can go.
/// </summary>
internal sealed class EntranceMap : FrameworkElement
{
    private static readonly Brush SelectFill = Frozen(new SolidColorBrush(Color.FromArgb(55, 255, 215, 0))), Landing = Frozen(new SolidColorBrush(Color.FromRgb(120, 220, 120))),
        Walk = Frozen(new SolidColorBrush(Color.FromRgb(80, 200, 255))), CardFill = Frozen(new SolidColorBrush(Color.FromArgb(235, 19, 27, 36))),
        GhostGood = Frozen(new SolidColorBrush(Color.FromArgb(120, 120, 220, 120))), GhostBad = Frozen(new SolidColorBrush(Color.FromArgb(130, 230, 90, 80)));
    private static readonly Pen SelectPen = Frozen(new Pen(Brushes.Gold, 1.5) { DashStyle = DashStyles.Dash }), WalkPen = Frozen(new Pen(Walk, 1.5)),
        CardPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(80, 100, 120)), 1)), GroupPen = Frozen(new Pen(Brushes.White, 1.5));
    private static T Frozen<T>(T value) where T : Freezable { value.Freeze(); return value; }

    private Ds1CollisionDocument? map;
    private DrawingGroup? terrain;
    private int? selected;
    private (int X, int Y)? hoverTile;
    private Point? pointer;
    public event Action<int?>? Selected;
    public event Action<int, int>? MoveRequested;
    public event Action<ExitCandidate>? CandidateChosen;
    private bool moveArmed;
    public bool MoveArmed { get => moveArmed; set { moveArmed = value; InvalidateVisual(); } }
    /// <summary>When the selected slot has no tiles here, an armed placement adds a one-tile hidden exit with this orientation (10 or 11).</summary>
    public int? NewExitOrientation { get; set; }
    private IReadOnlyList<ExitCandidate> candidates = [];
    /// <summary>Suggested anchor tiles for the selected slot, drawn as numbered markers; clicking one places the exit there.</summary>
    public IReadOnlyList<ExitCandidate> Candidates { get => candidates; set { candidates = value; HoveredCandidate = null; InvalidateVisual(); } }
    internal int? HoveredCandidate { get; private set; }
    public int? Selection { get => selected; set { selected = value; InvalidateVisual(); } }
    /// <summary>The warp an exit slot leads through, for a direction (l or r); null when the area context does not resolve one.</summary>
    public Func<int, string, WarpGeometry?>? WarpFor { get; set; }
    /// <summary>Where an exit slot leads, as the card names it.</summary>
    public Func<int, string?>? DestinationOf { get; set; }
    /// <summary>Why the selected group cannot move to a tile; null when it can.</summary>
    public Func<int, int, int, string?>? MoveProblem { get; set; }
    /// <summary>The exit slot under the pointer.</summary>
    internal int? HoveredSlot { get; private set; }
    /// <summary>The lines the hover card shows, for checks.</summary>
    internal IReadOnlyList<string> CardLines { get; private set; } = [];

    public void SetMap(Ds1CollisionDocument? value)
    {
        map = value; terrain = new(); hoverTile = null; HoveredSlot = null;
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
        CardLines = [];
        if (map is null) { Label(dc, L.T("No fixed preset map"), 16, 20, Brushes.LightSteelBlue); return; }
        var (s, ox, oy) = Layout();
        dc.PushTransform(new MatrixTransform(s, 0, 0, s, ox, oy)); dc.DrawDrawing(terrain); dc.Pop();
        var exits = map.ExitTiles();
        foreach (var tile in exits)
        {
            var point = new Point(ox + (tile.X + .5) * s, oy + (tile.Y + .5) * s);
            if (tile.IsExit)
            {
                var brush = tile.Main == selected ? Brushes.Gold : tile.Main == HoveredSlot ? Brushes.White : Brushes.Turquoise;
                dc.DrawEllipse(brush, new Pen(Brushes.Black, 1), point, 9, 9);
                Label(dc, tile.Main.ToString(), point.X - 4, point.Y - 8, Brushes.Black);
            }
            else dc.DrawRectangle(Brushes.MediumPurple, null, new Rect(point.X - 3, point.Y - 3, 6, 6));
        }
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i]; var centre = new Point(ox + (c.X + .5) * s, oy + (c.Y + .5) * s);
            var diamond = new StreamGeometry();
            using (var g = diamond.Open())
            {
                g.BeginFigure(new(centre.X, centre.Y - 10), true, true); g.LineTo(new(centre.X + 10, centre.Y), true, false);
                g.LineTo(new(centre.X, centre.Y + 10), true, false); g.LineTo(new(centre.X - 10, centre.Y), true, false);
            }
            diamond.Freeze(); dc.DrawGeometry(i == HoveredCandidate ? Brushes.White : Brushes.LightGreen, new Pen(Brushes.Black, 1), diamond);
            Label(dc, (i + 1).ToString(CultureInfo.InvariantCulture), centre.X - 4, centre.Y - 8, Brushes.Black);
        }
        // While placing, the selected group follows the pointer: its tiles, then its warp around the new anchor.
        // A slot with no tiles here places a new one-tile hidden exit instead.
        ExitTile[]? moving = null; bool adding = false;
        if (MoveArmed && selected is { } armed && hoverTile is { } at)
        {
            moving = exits.Where(t => t.IsExit && t.Main == armed).ToArray();
            adding = moving.Length == 0;
            if (adding) moving = NewExitOrientation is { } orientation ? [new ExitTile(0, at.X, at.Y, orientation, armed, 0, Ds1CollisionDocument.HiddenExitCell(armed))] : null;
        }
        if (moving is not null && hoverTile is { } target && selected is { } slotMoving)
        {
            int dx = target.X - moving.Min(t => t.X), dy = target.Y - moving.Min(t => t.Y);
            var problem = MoveProblem?.Invoke(slotMoving, target.X, target.Y);
            foreach (var tile in moving) dc.DrawRectangle(problem is null ? GhostGood : GhostBad, GroupPen, new Rect(ox + (tile.X + dx) * s, oy + (tile.Y + dy) * s, s, s));
            var anchor = Ds1CollisionDocument.Anchor(moving);
            var warp = WarpFor?.Invoke(slotMoving, anchor.Direction);
            if (warp is not null) DrawWarp(dc, warp, anchor.X + dx, anchor.Y + dy, s, ox, oy);
            var lines = new List<string> { problem is null
                ? adding ? L.T("Exit {0}: click to add a hidden exit here", slotMoving) : L.T("Exit {0}: click to move here", slotMoving)
                : adding ? L.T("Exit {0} cannot be added here", slotMoving) : L.T("Exit {0} cannot move here", slotMoving) };
            if (problem is not null) lines.Add(problem);
            lines.AddRange(Describe(warp));
            Card(dc, lines, problem is null ? Brushes.LightGreen : Brushes.Salmon);
        }
        else if (HoveredCandidate is { } hovered && hovered < candidates.Count && selected is { } suggestedSlot)
        {
            var c = candidates[hovered];
            dc.DrawRectangle(GhostGood, GroupPen, new Rect(ox + c.X * s, oy + c.Y * s, s, s));
            string direction = exits.Where(t => t.IsExit && t.Main == suggestedSlot).ToArray() is { Length: > 0 } group ? Ds1CollisionDocument.Anchor(group).Direction
                : NewExitOrientation == 11 ? "r" : "l";
            var warp = WarpFor?.Invoke(suggestedSlot, direction);
            if (warp is not null) DrawWarp(dc, warp, c.X, c.Y, s, ox, oy);
            Card(dc, [L.T("Suggestion {0}: tile {1}, {2} · click to place exit {3}", hovered + 1, c.X, c.Y, suggestedSlot), .. DescribeCandidate(c), .. Describe(warp)], Brushes.LightGreen);
        }
        else if ((HoveredSlot ?? selected) is { } slot && exits.Where(t => t.IsExit && t.Main == slot).ToArray() is { Length: > 0 } shown)
        {
            var anchor = Ds1CollisionDocument.Anchor(shown);
            foreach (var tile in shown) dc.DrawRectangle(null, GroupPen, new Rect(ox + tile.X * s, oy + tile.Y * s, s, s));
            var warp = WarpFor?.Invoke(slot, anchor.Direction);
            if (warp is not null) DrawWarp(dc, warp, anchor.X, anchor.Y, s, ox, oy);
            if (HoveredSlot is not null) Card(dc, [L.T("Exit {0} → {1}", slot, DestinationOf?.Invoke(slot) ?? L.T("no resolved destination")), .. Describe(warp)], Brushes.Gold);
        }
        Label(dc, L.T("{0} × {1} tiles", map.Width, map.Height), 8, ActualHeight - 22, Brushes.LightSteelBlue);
    }

    private static IEnumerable<string> Describe(WarpGeometry? warp)
    {
        if (warp is null) { yield return L.T("No lvlwarp definition resolves for this exit's direction."); yield break; }
        yield return L.T("{0} · warp {1} · direction {2}", warp.Name, warp.Id, warp.Direction);
        yield return L.T("Selection box: {0}, {1} · {2} × {3} pixels", warp.SelectX, warp.SelectY, warp.SelectDX, warp.SelectDY);
        yield return L.T("Arrive at {0}, {1} · walk to {2}, {3} subtiles", warp.OffsetX, warp.OffsetY, warp.ExitWalkX, warp.ExitWalkY);
        if (warp.NoInteract) yield return L.T("Not clickable (NoInteract)");
        else if (warp.LitVersion) yield return L.T("Lights up on hover (lit tiles {0} on)", warp.Tiles);
    }

    private static IEnumerable<string> DescribeCandidate(ExitCandidate c)
    {
        yield return L.T("Open ground where players arrive: {0:P0}", c.Openness);
        yield return c.NearestExit is { } d ? L.T("Nearest other exit: {0:F1} tiles", d) : L.T("No other exits in this map");
        if (c.Facing is { } f) yield return f > .25 ? L.T("On the preferred side") : f < -.25 ? L.T("Away from the preferred side") : L.T("Neither toward nor away from the preferred side");
    }

    /// <summary>The warp's selection box (projected to the floor), arrival and walk-to points around an anchor tile's corner.</summary>
    private static void DrawWarp(DrawingContext dc, WarpGeometry warp, int anchorX, int anchorY, double s, double ox, double oy)
    {
        double unit = s / WarpGeometry.SubtilesPerTile;
        Point At(double subX, double subY) => new(ox + anchorX * s + subX * unit, oy + anchorY * s + subY * unit);
        var corners = warp.SelectionOnFloor();
        if (warp.SelectDX > 0 && warp.SelectDY > 0 && !warp.NoInteract)
        {
            var box = new StreamGeometry();
            using (var g = box.Open())
            {
                g.BeginFigure(At(corners[0].X, corners[0].Y), true, true);
                foreach (var corner in corners.Skip(1)) g.LineTo(At(corner.X, corner.Y), true, false);
            }
            box.Freeze(); dc.DrawGeometry(SelectFill, SelectPen, box);
        }
        // Centre of the subtile each offset names.
        var arrive = At(warp.OffsetX + .5, warp.OffsetY + .5); var walk = At(warp.OffsetX + warp.ExitWalkX + .5, warp.OffsetY + warp.ExitWalkY + .5);
        if (warp.ExitWalkX != 0 || warp.ExitWalkY != 0) dc.DrawLine(WalkPen, arrive, walk);
        dc.DrawEllipse(Walk, new Pen(Brushes.Black, 1), walk, 4, 4);
        dc.DrawEllipse(Landing, new Pen(Brushes.Black, 1), arrive, 5, 5);
    }

    private void Card(DrawingContext dc, IReadOnlyList<string> lines, Brush first)
    {
        CardLines = lines;
        if (pointer is not { } at || lines.Count == 0) return;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var texts = lines.Select((line, i) => new FormattedText(line, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), i == 0 ? 13 : 12, i == 0 ? first : Brushes.LightSteelBlue, dip)).ToArray();
        double width = texts.Max(t => t.Width) + 20, height = texts.Sum(t => t.Height + 2) + 12;
        double x = at.X + 18 + width > ActualWidth ? Math.Max(4, at.X - width - 18) : at.X + 18, y = at.Y + 18 + height > ActualHeight ? Math.Max(4, at.Y - height - 18) : at.Y + 18;
        dc.DrawRoundedRectangle(CardFill, CardPen, new Rect(x, y, width, height), 4, 4);
        double top = y + 6;
        foreach (var text in texts) { dc.DrawText(text, new Point(x + 10, top)); top += text.Height + 2; }
    }

    private void Label(DrawingContext dc, string text, double x, double y, Brush brush) => dc.DrawText(
        new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
    internal void ClickTile(int x, int y)
    {
        if (map is null || x < 0 || y < 0 || x >= map.Width || y >= map.Height) { Selected?.Invoke(null); return; }
        if (MoveArmed && selected is not null) { MoveRequested?.Invoke(x, y); return; }
        Selected?.Invoke(map.ExitTiles().FirstOrDefault(t => t.IsExit && t.X == x && t.Y == y)?.Main);
    }

    /// <summary>The pointer is over a tile: hover its exit, or place the moving group there. Also drives checks.</summary>
    internal void HoverTile(int x, int y, Point? at = null)
    {
        var (s, ox, oy) = Layout();
        pointer = at ?? new Point(ox + (x + .5) * s, oy + (y + .5) * s);
        hoverTile = map is not null && x >= 0 && y >= 0 && x < map.Width && y < map.Height ? (x, y) : null;
        HoveredSlot = hoverTile is null || MoveArmed ? null : map!.ExitTiles().FirstOrDefault(t => t.IsExit && t.X == x && t.Y == y)?.Main;
        int index = MoveArmed ? -1 : candidates.ToList().FindIndex(c => c.X == x && c.Y == y);
        HoveredCandidate = index >= 0 ? index : null;
        InvalidateVisual();
    }
    /// <summary>Choose a suggestion as a click on its marker would. Also drives checks.</summary>
    internal void ChooseCandidate(int index) { if (index >= 0 && index < candidates.Count) CandidateChosen?.Invoke(candidates[index]); }
    private int? CandidateAt(Point p)
    {
        if (map is null) return null;
        var (s, ox, oy) = Layout();
        int best = -1; double bestDistance = 11;
        for (int i = 0; i < candidates.Count; i++)
        {
            double d = Math.Max(Math.Abs(ox + (candidates[i].X + .5) * s - p.X), Math.Abs(oy + (candidates[i].Y + .5) * s - p.Y));
            if (d < bestDistance) { best = i; bestDistance = d; }
        }
        return best >= 0 ? best : null;
    }
    private int? MarkerAt(Point p)
    {
        if (map is null) return null;
        var (s, ox, oy) = Layout();
        return map.ExitTiles().Where(t => t.IsExit).OrderBy(t => Math.Pow(ox + (t.X + .5) * s - p.X, 2) + Math.Pow(oy + (t.Y + .5) * s - p.Y, 2))
            .FirstOrDefault(t => Math.Abs(ox + (t.X + .5) * s - p.X) <= 10 && Math.Abs(oy + (t.Y + .5) * s - p.Y) <= 10)?.Main;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var (s, ox, oy) = Layout(); var p = e.GetPosition(this);
        HoverTile((int)Math.Floor((p.X - ox) / s), (int)Math.Floor((p.Y - oy) / s), p);
        // Markers are larger than small-scale tiles: the nearest one within reach counts as hovered.
        if (!MoveArmed && CandidateAt(p) is { } suggestion) { if (suggestion != HoveredCandidate) { HoveredCandidate = suggestion; HoveredSlot = null; InvalidateVisual(); } }
        else if (!MoveArmed && MarkerAt(p) is { } marker && marker != HoveredSlot) { HoveredSlot = marker; InvalidateVisual(); }
    }
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        pointer = null; hoverTile = null; HoveredSlot = null; HoveredCandidate = null; InvalidateVisual();
    }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e); if (e.ChangedButton != MouseButton.Left) return;
        Focus(); var (s, ox, oy) = Layout(); var p = e.GetPosition(this);
        if (!MoveArmed && CandidateAt(p) is { } chosen) { ChooseCandidate(chosen); e.Handled = true; return; }
        if (!MoveArmed && MarkerAt(p) is { } hit) { Selected?.Invoke(hit); e.Handled = true; return; }
        ClickTile((int)Math.Floor((p.X - ox) / s), (int)Math.Floor((p.Y - oy) / s)); e.Handled = true;
    }
}
