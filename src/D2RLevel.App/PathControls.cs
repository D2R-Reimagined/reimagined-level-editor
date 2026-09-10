using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using D2RLevel.Core;

namespace D2RLevel.App;

public sealed partial class LegacyFloorWindow
{
    private readonly CheckBox editPath = new() { Content = "Edit path", Foreground = Brushes.White, Margin = new Thickness(8),
        ToolTip = "Drag a patrol point to move it. Click empty ground to append a point, right-click a point to remove it." };
    private readonly ComboBox pathAction = new() { Width = 210, Foreground = Brushes.Black, Margin = new Thickness(4) };
    private readonly TextBlock pathStatus = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSteelBlue };
    private int selectedPoint = -1;
    private bool draggingPoint, refreshingAction;
    /// <summary>Raised whenever a patrol edit changes the document, so the HD view can redraw.</summary>
    public event Action? PathChanged;

    private int SelectedUnit => unitList.SelectedIndex;
    /// <summary>The unit whose path the HD view should draw, or -1.</summary>
    public int SelectedUnitIndex => unitList.SelectedIndex;
    /// <summary>The patrol point highlighted for editing, or -1.</summary>
    public int SelectedPathPoint => editPath.IsChecked == true ? selectedPoint : -1;

    /// <summary>Turns path editing off when another map tool takes over left-click.</summary>
    private void DisarmPathEditing()
    {
        if (editPath.IsChecked != true) return;
        editPath.IsChecked = false; selectedPoint = -1; draggingPoint = false;
        RefreshPathStatus();
    }

    private void InitializePaths(DockPanel root)
    {
        var row = new WrapPanel(); DockPanel.SetDock(row, Dock.Top); root.Children.Add(row);
        row.Children.Add(editPath);
        row.Children.Add(new TextBlock { Text = "Point action", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        row.Children.Add(pathAction);
        var remove = new Button { Content = "Remove point", ToolTip = "Remove the selected patrol point. Removing the last one deletes the path." };
        row.Children.Add(remove);
        DockPanel.SetDock(pathStatus, Dock.Top); root.Children.Add(pathStatus);

        pathAction.IsEditable = true;
        pathAction.WithReadableItems();
        pathAction.ItemsSource = Ds1PathAction.Catalog;
        pathAction.SelectionChanged += (_, _) => { if (!refreshingAction && pathAction.SelectedItem is Ds1PathAction a) ApplyAction(a.Value); };
        pathAction.LostFocus += (_, _) => CommitTypedAction();
        pathAction.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitTypedAction(); e.Handled = true; } };
        remove.Click += (_, _) => RemoveSelectedPoint();

        editPath.Click += (_, _) =>
        {
            // Footprint drawing claims the same left-click, so arming one disarms the other.
            if (editPath.IsChecked == true && footprintDraw is not null) footprintDraw.IsChecked = false;
            selectedPoint = -1; CancelStroke(); dismissCollisionDraft?.Invoke(); Render(); RefreshPathStatus();
        };
        unitList.SelectionChanged += (_, _) => { if (!refreshingUnits) { selectedPoint = -1; RefreshPathStatus(); } };
        CollisionViewChanged += () => RefreshPathStatus();

        image.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (editPath.IsChecked != true || CollisionDocument is null || SelectedUnit < 0) return;
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            var point = e.GetPosition(image);
            int hit = PointAt(point);
            if (hit >= 0) { selectedPoint = hit; draggingPoint = true; image.CaptureMouse(); Render(); RefreshPathStatus(); }
            else if (SubtileAt(point) is { } cell) AppendPoint(cell.X, cell.Y);
            e.Handled = true;
        };
        image.PreviewMouseMove += (_, e) =>
        {
            if (!draggingPoint || CollisionDocument is null) return;
            if (SubtileAt(e.GetPosition(image)) is { } cell) DragPoint(cell.X, cell.Y);
            e.Handled = true;
        };
        image.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!draggingPoint) return;
            draggingPoint = false; image.ReleaseMouseCapture(); e.Handled = true;
        };
        image.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (editPath.IsChecked != true || CollisionDocument is null || SelectedUnit < 0) return;
            int hit = PointAt(e.GetPosition(image));
            if (hit < 0) return;
            selectedPoint = hit; RemoveSelectedPoint(); e.Handled = true;
        };
        image.LostMouseCapture += (_, _) => draggingPoint = false;
        RefreshPathStatus();
    }

    /// <summary>Subtile under a point in image space, or null when it falls outside the map.</summary>
    private (int X, int Y)? SubtileAt(Point point)
    {
        if (CollisionDocument is not { } doc) return null;
        double a = point.X / zoom.Value / scale / 80 - scene.Map.Height, b = point.Y / zoom.Value / scale / 40;
        int x = (int)Math.Round((a + b) * 2.5), y = (int)Math.Round((b - a) * 2.5);
        return x < 0 || y < 0 || x >= doc.Width * 5 || y >= doc.Height * 5 ? null : (x, y);
    }

    /// <summary>Index of the patrol point under a point in image space, or -1.</summary>
    private int PointAt(Point point)
    {
        if (CollisionDocument is not { } doc || SelectedUnit < 0) return -1;
        var points = doc.PatrolPoints(SelectedUnit);
        double best = 12; int found = -1;
        for (int i = 0; i < points.Count; i++)
        {
            var at = UnitPoint(points[i].X, points[i].Y);
            double distance = (new Point(at.X * scale * zoom.Value, at.Y * scale * zoom.Value) - point).Length;
            if (distance <= best) { best = distance; found = i; }
        }
        return found;
    }

    private void PathEdit(Action edit, string success)
    {
        if (CollisionDocument is null || SelectedUnit < 0) return;
        try { CancelStroke(); edit(); CollisionChanged(); PathChanged?.Invoke(); pathStatus.Text = success; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException)
        { pathStatus.Text = ex.Message; }
        RefreshPathStatus(keepMessage: true);
    }

    private void DragPoint(int x, int y)
    {
        var doc = CollisionDocument!;
        if (selectedPoint < 0 || selectedPoint >= doc.PatrolPoints(SelectedUnit).Count) return;
        var current = doc.PatrolPoints(SelectedUnit)[selectedPoint];
        if (current.X == x && current.Y == y) return;
        PathEdit(() => doc.MovePathPoint(SelectedUnit, selectedPoint, x, y), $"Moved point {selectedPoint + 1} to ({x}, {y}). Ctrl+Z undoes each step.");
    }

    private void AppendPoint(int x, int y)
    {
        var doc = CollisionDocument!;
        int at = doc.PatrolPoints(SelectedUnit).Count;
        uint action = at > 0 ? doc.PatrolPoints(SelectedUnit)[at - 1].Action : 1;
        PathEdit(() =>
        {
            doc.InsertPathPoint(SelectedUnit, at, x, y, action);
            selectedPoint = at;
        }, $"Added point {at + 1} at ({x}, {y}) with action {action}.");
    }

    private void RemoveSelectedPoint()
    {
        if (CollisionDocument is not { } doc || SelectedUnit < 0) return;
        if (selectedPoint < 0 || selectedPoint >= doc.PatrolPoints(SelectedUnit).Count)
        { pathStatus.Text = "Select a patrol point first."; return; }
        int index = selectedPoint;
        PathEdit(() =>
        {
            doc.RemovePathPoint(SelectedUnit, index);
            selectedPoint = Math.Min(index, doc.PatrolPoints(SelectedUnit).Count - 1);
        }, $"Removed point {index + 1}. Ctrl+Z restores it.");
    }

    private void ApplyAction(uint action)
    {
        if (CollisionDocument is not { } doc || SelectedUnit < 0) return;
        if (selectedPoint < 0 || selectedPoint >= doc.PatrolPoints(SelectedUnit).Count) return;
        if (doc.PatrolPoints(SelectedUnit)[selectedPoint].Action == action) return;
        int index = selectedPoint;
        PathEdit(() => doc.SetPathPointAction(SelectedUnit, index, action), $"Point {index + 1} now uses action {action}.");
    }

    private void CommitTypedAction()
    {
        if (refreshingAction || pathAction.SelectedItem is Ds1PathAction) return;
        var text = (pathAction.Text ?? "").Trim();
        // Accept a bare number so a mod's own action code can be entered.
        var digits = new string(text.TakeWhile(char.IsAsciiDigit).ToArray());
        if (uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out uint value)) ApplyAction(value);
        else { pathStatus.Text = "Enter a whole action number, or choose one the base game uses."; RefreshPathStatus(keepMessage: true); }
    }

    private void RefreshPathStatus(bool keepMessage = false)
    {
        if (CollisionDocument is not { } doc || SelectedUnit < 0)
        {
            editPath.IsEnabled = pathAction.IsEnabled = false;
            if (!keepMessage) pathStatus.Text = "Select a DS1 unit to inspect or edit its patrol path.";
            return;
        }
        string? warning = doc.PathEditWarning(SelectedUnit);
        editPath.IsEnabled = warning is null;
        if (warning is not null) { editPath.IsChecked = false; selectedPoint = -1; }
        var points = doc.PatrolPoints(SelectedUnit);
        if (selectedPoint >= points.Count) selectedPoint = points.Count - 1;
        pathAction.IsEnabled = warning is null && selectedPoint >= 0;

        refreshingAction = true;
        try
        {
            if (selectedPoint >= 0 && selectedPoint < points.Count)
            {
                uint action = points[selectedPoint].Action;
                pathAction.SelectedItem = Ds1PathAction.Catalog.FirstOrDefault(a => a.Value == action);
                if (pathAction.SelectedItem is null) pathAction.Text = Ds1PathAction.Describe(action);
            }
            else { pathAction.SelectedItem = null; pathAction.Text = ""; }
        }
        finally { refreshingAction = false; }

        if (keepMessage) return;
        if (warning is not null) { pathStatus.Text = "Path editing unavailable: " + warning; return; }
        pathStatus.Text = points.Count == 0
            ? "No patrol path. Tick Edit path and click the map to lay down the first point."
            : $"{points.Count} patrol point{(points.Count == 1 ? "" : "s")}" +
              (selectedPoint >= 0 ? $" · point {selectedPoint + 1} selected at ({points[selectedPoint].X}, {points[selectedPoint].Y}), {Ds1PathAction.Describe(points[selectedPoint].Action)}" : "") +
              ". Drag to move, click empty ground to append, right-click a point to remove. Moving the unit translates the whole path.";
    }

    /// <summary>Draws the selected unit's path with numbered points on top of the unit markers.</summary>
    private void DrawPathEditing(DrawingContext dc)
    {
        if (CollisionDocument is not { } doc || SelectedUnit < 0 || showUnits.IsChecked != true) return;
        var points = doc.PatrolPoints(SelectedUnit);
        if (points.Count == 0) return;
        double radius = 5 / (scale * zoom.Value);
        bool editing = editPath.IsChecked == true;
        for (int i = 0; i < points.Count; i++)
        {
            var at = UnitPoint(points[i].X, points[i].Y);
            bool current = editing && i == selectedPoint;
            if (editing)
                dc.DrawEllipse(current ? Brushes.Gold : Brushes.White, new Pen(Brushes.Black, radius / 4), at,
                    current ? radius : radius * .7, current ? radius : radius * .7);
            var label = new FormattedText((i + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), radius * 1.6, Brushes.Black, 1)
            { TextAlignment = TextAlignment.Center };
            if (editing) dc.DrawText(label, new Point(at.X, at.Y - radius * .95));
        }
    }

    internal string VerifyPathEditing()
    {
        var doc = CollisionDocument!;
        if (!pathAction.HasReadableItems() || !unitList.HasReadableItems() || !collisionTool.HasReadableItems())
            throw new InvalidOperationException("A DS1 combo box would render its items in the panel foreground, which is illegible on the popup.");
        int unit = doc.Units.Select((u, i) => i).FirstOrDefault(i => doc.PatrolPoints(i).Count > 0 && doc.PathEditWarning(i) is null, -1);
        if (unit < 0) throw new InvalidOperationException("No editable patrol path in this map.");
        var before = doc.Serialize();
        unitList.SelectedIndex = unit; editPath.IsChecked = true;
        var points = doc.PatrolPoints(unit);
        selectedPoint = 0;
        var start = points[0];
        DragPoint(start.X, start.Y + (start.Y + 1 < doc.Height * 5 ? 1 : -1));
        if (doc.PatrolPoints(unit)[0].Y == start.Y) throw new InvalidOperationException("Path point drag did not move the point.");
        AppendPoint(start.X, start.Y);
        if (doc.PatrolPoints(unit).Count != points.Count + 1) throw new InvalidOperationException("Append did not extend the path.");
        ApplyAction(points[0].Action == 1 ? 2u : 1u);
        RemoveSelectedPoint();
        if (doc.PatrolPoints(unit).Count != points.Count) throw new InvalidOperationException("Remove did not shorten the path.");
        while (doc.CanUndo && !doc.Serialize().SequenceEqual(before)) doc.Undo();
        if (!doc.Serialize().SequenceEqual(before)) throw new InvalidOperationException("Path editing did not undo to the original bytes.");
        CollisionChanged(); RefreshPathStatus();
        return $"PASS patrol path: unit #{unit} with {points.Count} points; drag, append, action change, removal and undo back to original bytes.";
    }
}
