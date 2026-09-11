using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using D2RLevel.Core;

namespace D2RLevel.App;

public sealed partial class LegacyFloorWindow
{
    private readonly ComboBox unitList = new() { Width = 290, Foreground = Brushes.Black, Margin = new Thickness(4) };
    private readonly TextBox unitX = new() { Width = 55, Margin = new Thickness(4) }, unitY = new() { Width = 55, Margin = new Thickness(4) };
    private readonly CheckBox showUnits = new() { Content = "Gameplay units", IsChecked = true, Foreground = Brushes.White, Margin = new Thickness(8) };
    private readonly TextBlock unitStatus = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private bool refreshingUnits;
    public event Action<int, double>? LinkUnitRequested;
    public event Action<int>? UnitSelected;
    public void SelectGameplayUnit(int index) { if (unitList.SelectedIndex != index) unitList.SelectedIndex = index; }
    private Button? linkUnitButton;
    internal void VerifyLinkUnitButton(int index)
    {
        unitList.SelectedIndex = index;
        linkUnitButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private void InitializeGameplay(DockPanel root)
    {
        var row = new WrapPanel(); DockPanel.SetDock(row, Dock.Top); root.Children.Add(row);
        row.Children.Add(showUnits); row.Children.Add(unitList);
        row.Children.Add(new TextBlock { Text = "Subtile X / Y", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(unitX); row.Children.Add(unitY);
        var move = new Button { Content = "Move unit" }; row.Children.Add(move);
        var add = new Button { Content = "Place unit…" }; row.Children.Add(add); add.Click += (_, _) => AddPlacement();
        var delete = new Button { Content = "Delete unit" }; row.Children.Add(delete); delete.Click += (_, _) => DeletePlacement();
        var link = new Button { Content = "Link unit to HD", ToolTip = "Select an HD model in the JSON view and a DS1 unit here. Uses this pair's grid calibration." }; row.Children.Add(link);
        linkUnitButton = link;
        link.Click += (_, _) =>
        {
            try { if (unitList.SelectedIndex >= 0) LinkUnitRequested?.Invoke(unitList.SelectedIndex, LinkUnitsPerTile); }
            catch (Exception ex) { unitStatus.Text = ex.Message; }
        };
        DockPanel.SetDock(unitStatus, Dock.Top); root.Children.Add(unitStatus);
        unitList.WithReadableItems();
        unitList.SelectionChanged += (_, _) => { if (!refreshingUnits) { ShowUnit(); Render(); UnitSelected?.Invoke(unitList.SelectedIndex); } };
        showUnits.Click += (_, _) => Render();
        move.Click += (_, _) =>
        {
            if (!int.TryParse(unitX.Text, out int x) || !int.TryParse(unitY.Text, out int y)) { unitStatus.Text = "Enter whole subtile coordinates."; return; }
            MoveSelectedUnit(x, y);
        };
        // Shift-click places the selected unit. Ordinary inspect clicks pick the nearest marker.
        image.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (collisionTool.SelectedIndex != 0 || showUnits.IsChecked != true || CollisionDocument is not { } doc) return;
            var p = e.GetPosition(image);
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                double a = p.X / zoom.Value / scale / 80 - scene.Map.Height, b = p.Y / zoom.Value / scale / 40;
                MoveSelectedUnit((int)Math.Round((a + b) * 2.5), (int)Math.Round((b - a) * 2.5)); e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.None)
            {
                var nearest = doc.Units.Select(u => (Unit: u, Distance: (new Point(UnitPoint(u.X, u.Y).X * scale * zoom.Value, UnitPoint(u.X, u.Y).Y * scale * zoom.Value) - p).Length))
                    .OrderBy(u => u.Distance).FirstOrDefault();
                if (nearest.Unit is not null && nearest.Distance <= 12) { unitList.SelectedIndex = nearest.Unit.Index; e.Handled = true; }
            }
        };
        CollisionViewChanged += RefreshUnits;
        RefreshUnits();
        move.IsEnabled = CollisionDocument is { GameplayWarning: null };
    }
    private void RefreshUnits()
    {
        int selected = unitList.SelectedIndex; refreshingUnits = true;
        unitList.ItemsSource = CollisionDocument?.Units.Select(u => $"#{u.Index} · {(u.Type == 1 ? "NPC/monster" : u.Type == 2 ? "Object" : $"Type {u.Type}")} ID {u.Id} · ({u.X}, {u.Y})").ToArray();
        unitList.SelectedIndex = selected < 0 || unitList.Items.Count == 0 ? -1 : Math.Min(selected, unitList.Items.Count - 1);
        refreshingUnits = false; ShowUnit();
    }
    private void ShowUnit()
    {
        if (CollisionDocument is not { } doc || unitList.SelectedIndex < 0) { unitStatus.Text = CollisionDocument?.GameplayWarning ?? "Select the intended DS1 unit by its marker or list entry. A scenery model does not necessarily have a gameplay unit; use Add collision for its footprint."; return; }
        var unit = doc.Units[unitList.SelectedIndex]; unitX.Text = unit.X.ToString(); unitY.Text = unit.Y.ToString();
        unitStatus.Text = doc.GameplayWarning ?? $"{doc.Units.Count} units · Flags 0x{unit.Flags:X8} · {doc.PatrolPoints(unit.Index).Count} patrol points · Cyan: NPC/monster · Green: object · White: selected. Shift-click to move; linked patrol translates with it. IDs are raw DS1 IDs. Linked models move their assigned unit and collision together from the HD view.";
    }
    private void MoveSelectedUnit(int x, int y)
    {
        if (CollisionDocument is not { } doc || unitList.SelectedIndex < 0) return;
        try { CancelStroke(); doc.MoveUnit(unitList.SelectedIndex, x, y); CollisionChanged(); }
        catch (Exception ex) { unitStatus.Text = ex.Message; }
    }
    private Point UnitPoint(int x, int y) => new((x / 5d - y / 5d + scene.Map.Height) * 80, (x / 5d + y / 5d) * 40);
    private void DrawGameplay(DrawingContext dc)
    {
        if (showUnits.IsChecked != true || CollisionDocument is not { } doc) return;
        double radius = 5 / (scale * zoom.Value);
        foreach (var unit in doc.Units)
        {
            bool selected = unit.Index == unitList.SelectedIndex;
            var point = UnitPoint(unit.X, unit.Y);
            if (selected)
            {
                var previous = point;
                foreach (var node in doc.PatrolPoints(unit.Index))
                {
                    var next = UnitPoint(node.X, node.Y); dc.DrawLine(new Pen(Brushes.White, radius / 3), previous, next);
                    dc.DrawEllipse(Brushes.White, null, next, radius / 2, radius / 2); previous = next;
                }
            }
            dc.DrawEllipse(unit.Type == 1 ? Brushes.Cyan : unit.Type == 2 ? Brushes.LimeGreen : Brushes.Magenta,
                new Pen(selected ? Brushes.White : Brushes.Black, radius / 2), point, selected ? radius * 1.4 : radius, selected ? radius * 1.4 : radius);
        }
        DrawPathEditing(dc);
    }
    internal string VerifyGameplayEditing(string folder)
    {
        var doc = CollisionDocument!;
        if (doc.GameplayWarning is not null) throw new InvalidDataException(doc.GameplayWarning);
        var unit = doc.Units.First(u => u.Type is 1 or 2 && u.X > 1 && u.X < doc.Width * 5 - 2);
        var before = doc.Serialize(); unitList.SelectedIndex = unit.Index;
        MoveSelectedUnit(unit.X + 1, unit.Y);
        if (unitX.Text != (unit.X + 1).ToString()) throw new InvalidOperationException("Gameplay controls did not refresh.");
        var edited = doc.Serialize(); doc.Undo(); CollisionChanged();
        if (!doc.Serialize().SequenceEqual(before)) throw new InvalidOperationException("Gameplay undo changed unrelated bytes.");
        doc.Redo(); CollisionChanged();
        string path = Path.Combine(folder, "gameplay-preset.ds1"); doc.SaveCopy(path);
        if (!Ds1CollisionDocument.Load(path).Serialize().SequenceEqual(edited)) throw new InvalidOperationException("Gameplay save mismatch.");
        RefreshCollisionState();
        return $"PASS DS1 gameplay: {doc.Units.Count} units; moved #{unit.Index}, refreshed controls, undo/redo and verified save/reopen.";
    }
}
