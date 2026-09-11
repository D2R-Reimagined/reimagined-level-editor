using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using D2RLevel.Core;
using Microsoft.Win32;

namespace D2RLevel.App;

public sealed partial class LegacyFloorWindow
{
    private readonly CheckBox showCollision = new() { Content = "Collision", Foreground = Brushes.White, IsChecked = true, Margin = new Thickness(8) };
    private readonly ComboBox collisionTool = new() { ItemsSource = new[] { "Inspect", "Paint blocked", "Clear DS1 override", "Paint floor", "Erase floor", "Pick floor", "Fill floor", "Rectangle floor" }, Foreground = Brushes.Black, SelectedIndex = 0, Width = 170, Margin = new Thickness(8) };
    private readonly Button collisionUndo = new() { Content = "Undo" }, collisionRedo = new() { Content = "Redo" };
    private readonly TextBlock collisionStatus = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private readonly Canvas strokePreview = new() { IsHitTestVisible = false };
    private readonly HashSet<(int X, int Y)> stroke = new();
    private (int X, int Y)? lastTile;
    private bool painting, blockStroke;
    private Ds1CollisionDocument? CollisionDocument => scene.Collision?.Document;
    public Ds1CollisionDocument? EditableCollision => CollisionDocument;

    private void InitializeCollision(DockPanel root)
    {
        collisionTool.WithReadableItems();
        var tools = new WrapPanel { IsEnabled = CollisionDocument is not null };
        DockPanel.SetDock(tools, Dock.Top); root.Children.Add(tools);
        tools.Children.Add(showCollision); tools.Children.Add(collisionTool);
        tools.Children.Add(collisionUndo); tools.Children.Add(collisionRedo);
        var save = new Button { Content = CollisionDocument?.History.Shared == true ? "Save scene / pair…" : "Save DS1 copy…" }; tools.Children.Add(save);
        showCollision.Click += (_, _) => Render();
        collisionTool.SelectionChanged += (_, _) => { CancelStroke(); image.Cursor = collisionTool.SelectedIndex == 0 ? Cursors.Arrow : Cursors.Cross; };
        collisionUndo.Click += (_, _) => { CancelStroke(); CollisionDocument?.Undo(); CollisionChanged(); };
        collisionRedo.Click += (_, _) => { CancelStroke(); CollisionDocument?.Redo(); CollisionChanged(); };
        save.Click += (_, _) => SaveCollisionCopy();
        var load = new Button { Content = "Open DS1 copy…" }; tools.Children.Add(load);
        var arrange = new Button { Content = "Arrange beside HD" }; tools.Children.Add(arrange);
        arrange.Click += (_, _) =>
        {
            if (Owner is not { } hd) return;
            var work = SystemParameters.WorkArea;
            if (work.Width < hd.MinWidth + 650) { collisionStatus.Text = "Use a wider display for side-by-side layout, or move the DS1 window to another monitor."; return; }
            hd.WindowState = WindowState.Normal; WindowState = WindowState.Normal;
            double split = Math.Max(hd.MinWidth, work.Width * .58);
            hd.Left = work.Left; hd.Top = work.Top; hd.Width = split; hd.Height = work.Height;
            Left = work.Left + split; Top = work.Top; Width = work.Width - split; Height = work.Height;
        };
        load.Click += (_, _) => OpenCollisionCopy();
        DockPanel.SetDock(collisionStatus, Dock.Bottom); root.Children.Add(collisionStatus);
        Closing += (_, e) =>
        {
            CancelStroke();
            if (CollisionDocument?.History.Shared == true) return;
            if (CollisionDocument?.IsDirty == true)
            {
                if (MessageBox.Show(this, "Discard unsaved DS1 edits?", "DS1 gameplay", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true;
                else { CollisionDocument.DiscardChanges(); CollisionChanged(); }
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && painting) { CancelStroke(); e.Handled = true; }
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (e.Key == Key.Z) { CancelStroke(); CollisionDocument?.Undo(); CollisionChanged(); e.Handled = true; }
            if (e.Key == Key.Y) { CancelStroke(); CollisionDocument?.Redo(); CollisionChanged(); e.Handled = true; }
            if (e.Key == Key.S) { CancelStroke(); SaveCollisionCopy(); e.Handled = true; }
        };
        RefreshCollisionState();
    }
    private void RefreshCollisionState()
    {
        collisionUndo.IsEnabled = CollisionDocument?.CanUndo == true; collisionRedo.IsEnabled = CollisionDocument?.CanRedo == true;
        Title = "DS1 gameplay and collision · " + System.IO.Path.GetFileName(scene.Ds1Path) + (CollisionDocument?.IsDirty == true ? " *" : "");
    }
    private event Action? CollisionViewChanged;
    public event Action? SceneChanged;
    public event Action? SaveWorkspaceRequested;
    public void RefreshFromWorkspace() => CollisionChanged();
    private void CollisionChanged() { CollisionViewChanged?.Invoke(); RefreshCollisionState(); Render(); SceneChanged?.Invoke(); }
    private void SaveCollisionCopy()
    {
        CancelStroke(); if (CollisionDocument is not { } doc) return;
        if (doc.History.Shared) { SaveWorkspaceRequested?.Invoke(); return; }
        var dialog = new SaveFileDialog { Filter = "Diablo II preset|*.ds1", FileName = System.IO.Path.GetFileNameWithoutExtension(scene.Ds1Path) + ".edited.ds1", Title = "Save a separate DS1 gameplay/collision copy" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (string.Equals(System.IO.Path.GetFullPath(dialog.FileName), System.IO.Path.GetFullPath(scene.Ds1Path), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose a copy path; the original DS1 cannot be overwritten.");
            doc.SaveCopy(dialog.FileName); RefreshCollisionState(); SceneChanged?.Invoke(); collisionStatus.Text = "Saved and verified DS1: " + dialog.FileName;
            ToastManager.Show(this, "DS1 saved", dialog.FileName);
        }
        catch (Exception ex) { collisionStatus.Text = ex.Message; ToastManager.Show(this, "Save failed", ex.Message, true); }
    }
    private void OpenCollisionCopy()
    {
        CancelStroke();
        if (CollisionDocument?.History.Shared == true) { collisionStatus.Text = "Open the exported linked JSON in the main window to replace both documents together."; return; }
        var dialog = new OpenFileDialog { Filter = "Diablo II preset|*.ds1", Title = "Open an edited copy of this DS1" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var copy = Ds1CollisionDocument.Load(dialog.FileName);
            var original = Ds1CollisionDocument.Load(scene.Ds1Path);
            if (!original.SameUneditedData(copy)) throw new InvalidDataException("This DS1 contains changes beyond collision and unit/path positions; open it with its own tileset context.");
            if (CollisionDocument?.IsDirty == true && MessageBox.Show(this, "Discard unsaved DS1 edits and open this copy?", "DS1 collision", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            scene = scene with { Collision = new(copy, scene.Dt1Paths.SelectMany(LegacyCollision.ReadTiles)) };
            CollisionChanged(); collisionStatus.Text = "Opened edited DS1 copy: " + dialog.FileName;
        }
        catch (Exception ex) { collisionStatus.Text = ex.Message; }
    }
    private (int X, int Y)? TileAt(Point point)
    {
        double a = point.X / zoom.Value / scale / 80 - scene.Map.Height, b = point.Y / zoom.Value / scale / 40;
        int x = (int)Math.Floor((a + b) / 2), y = (int)Math.Floor((b - a) / 2);
        return x >= 0 && y >= 0 && x < scene.Map.Width && y < scene.Map.Height ? (x, y) : null;
    }
    private void HookPainting()
    {
        image.MouseLeftButtonDown += (_, e) =>
        {
            if (collisionTool.SelectedIndex == 0 || CollisionDocument is null) return;
            CancelStroke();
            try { if (!BeginGround(e.GetPosition(image))) { e.Handled = true; return; } }
            catch (Exception ex) { collisionStatus.Text = ex.Message; e.Handled = true; return; }
            painting = true; blockStroke = collisionTool.SelectedIndex == 1;
            image.Focus(); image.CaptureMouse(); AddStrokePoint(e.GetPosition(image)); e.Handled = true;
        };
        image.MouseMove += (_, e) => { if (painting) { AddStrokePoint(e.GetPosition(image)); e.Handled = true; } };
        image.MouseLeftButtonUp += (_, e) =>
        {
            if (!painting) return;
            AddStrokePoint(e.GetPosition(image));
            CommitStroke();
            e.Handled = true;
        };
        image.LostMouseCapture += (_, _) => { if (painting) CancelStroke(); };
        Deactivated += (_, _) => CancelStroke();
        zoom.PreviewMouseDown += (_, _) => CancelStroke();
    }
    private void CommitStroke()
    {
        var cells = stroke.ToArray(); bool block = blockStroke; CancelStroke();
        try
        {
            if (strokeTool >= 3) { ApplyGround(cells); return; }
            int changed = CollisionDocument!.Paint(cells, block);
            CollisionChanged(); collisionStatus.Text = $"{changed} layer cells changed · Ctrl+Z undoes the stroke. " + (block ? "Empty floor cells are skipped." : "DT1 blocking remains in effect.");
        }
        catch (Exception ex) { collisionStatus.Text = ex.Message; }
    }

    internal string VerifyCollisionEditing(string outputFolder)
    {
        var doc = CollisionDocument ?? throw new InvalidOperationException("No editable DS1.");
        var original = doc.Serialize();
        var cells = Enumerable.Range(1, doc.Height - 2).SelectMany(y => Enumerable.Range(1, doc.Width - 3).Select(x => (X: x, Y: y)))
            .Where(p => Enumerable.Range(0, 3).All(dx => scene.Collision!.At(p.X + dx, p.Y) is { BlockedSubtiles: 0, Unresolved: false, VariantDependent: false })).ToArray();
        if (cells.Length == 0) throw new InvalidOperationException("No resolved three-tile walkable strip for collision smoke.");
        var start = cells.OrderBy(p => Math.Abs(p.X - doc.Width / 2) + Math.Abs(p.Y - doc.Height / 2)).First();
        Point Center(int x, int y) => new((x - y + doc.Height) * 80 * scale * zoom.Value, (x + y + 1) * 40 * scale * zoom.Value);
        if (TileAt(Center(start.X, start.Y)) != start) throw new InvalidOperationException("Isometric collision picking mismatch.");
        painting = true; blockStroke = true;
        AddStrokePoint(Center(start.X, start.Y)); AddStrokePoint(Center(start.X + 2, start.Y));
        if (stroke.Count != 3 || doc.IsDirty) throw new InvalidOperationException("Stroke interpolation or preview isolation failed.");
        CancelStroke();
        if (doc.IsDirty || strokePreview.Children.Count != 0) throw new InvalidOperationException("Canceled collision stroke changed the DS1.");
        painting = true; blockStroke = true;
        AddStrokePoint(Center(start.X, start.Y)); AddStrokePoint(Center(start.X + 2, start.Y)); CommitStroke();
        var changed = doc.Serialize();
        var diff = Enumerable.Range(0, original.Length).Where(i => original[i] != changed[i]).ToArray();
        if (diff.Length != 3 || diff.Any(i => (original[i] ^ changed[i]) != 2)) throw new InvalidOperationException("Collision stroke changed unexpected DS1 bytes.");
        if (Enumerable.Range(0, 3).Any(dx => scene.Collision!.At(start.X + dx, start.Y).BlockedSubtiles != 25)) throw new InvalidOperationException("Override did not block all subtiles.");
        doc.Undo(); if (!doc.Serialize().SequenceEqual(original) || doc.CanUndo) throw new InvalidOperationException("Collision stroke undo was not lossless.");
        doc.Redo(); doc.SaveCopy(System.IO.Path.Combine(outputFolder, "collision-preset.ds1"));
        var reopened = Ds1CollisionDocument.Load(System.IO.Path.Combine(outputFolder, "collision-preset.ds1"));
        if (!reopened.Serialize().SequenceEqual(changed) || !File.ReadAllBytes(doc.SourcePath).SequenceEqual(original)) throw new InvalidOperationException("Collision save/reopen or source preservation failed.");
        CollisionChanged();
        string result = $"PASS DS1 collision: painted tiles ({start.X},{start.Y})–({start.X + 2},{start.Y}); exactly three bit-17 changes; cancel, interpolation, undo/redo, save/reopen and source preservation";
        collisionStatus.Text = result;
        return result;
    }
    private void AddStrokePoint(Point point)
    {
        var tile = TileAt(point);
        if (tile is not { } current) { lastTile = null; return; }
        var previous = lastTile ?? current;
        if (strokeTool == 7)
        {
            rectangleStart ??= current;
            stroke.Clear(); strokePreview.Children.Clear();
            for (int ry = Math.Min(rectangleStart.Value.Y, current.Y); ry <= Math.Max(rectangleStart.Value.Y, current.Y); ry++)
                for (int rx = Math.Min(rectangleStart.Value.X, current.X); rx <= Math.Max(rectangleStart.Value.X, current.X); rx++) AddCell(rx, ry);
            lastTile = current;
            return;
        }
        int steps = Math.Max(Math.Abs(current.X - previous.X), Math.Abs(current.Y - previous.Y));
        for (int i = 0; i <= steps; i++)
        {
            double t = steps == 0 ? 0 : (double)i / steps;
            int x = (int)Math.Round(previous.X + (current.X - previous.X) * t), y = (int)Math.Round(previous.Y + (current.Y - previous.Y) * t);
            int radius = strokeTool >= 3 ? strokeSize / 2 : 0;
            for (int by = Math.Max(0, y - radius); by <= Math.Min(scene.Map.Height - 1, y + radius); by++)
                for (int bx = Math.Max(0, x - radius); bx <= Math.Min(scene.Map.Width - 1, x + radius); bx++) AddCell(bx, by);
        }
        lastTile = current;
        collisionStatus.Text = $"Preview: {stroke.Count} tiles · Release to apply · Esc cancels";
        void AddCell(int x, int y)
        {
            if (blockStroke && !CollisionDocument!.CanBlock(x, y)) return;
            if (!stroke.Add((x, y))) return;
            var geometry = Diamond((x - y + scene.Map.Height - 1) * 80, (x + y) * 40);
            geometry.Transform = new ScaleTransform(scale * zoom.Value, scale * zoom.Value);
            strokePreview.Children.Add(new System.Windows.Shapes.Path { Data = geometry, Fill = new SolidColorBrush(Color.FromArgb(150, blockStroke ? (byte)255 : (byte)50, 180, 30)) });
        }
    }
    private void CancelStroke()
    {
        painting = false; lastTile = null; rectangleStart = null; stroke.Clear(); strokePreview.Children.Clear();
        if (image.IsMouseCaptured) image.ReleaseMouseCapture();
    }
    private void InspectCollision(int x, int y)
    {
        if (scene.Collision is not { } collision) return;
        var tile = collision.At(x, y);
        collisionStatus.Text = $"Tile ({x}, {y}): {tile.BlockedSubtiles}/25 subtiles block movement · DS1 override: {tile.Override} · No floor: {tile.NoFloor}" +
            (tile.Unresolved ? " · UNRESOLVED tile contribution" : "") + (tile.VariantDependent ? " · Variant-dependent (combined preview)" : "");
    }
    private void DrawCollision(DrawingContext dc)
    {
        if (showCollision.IsChecked != true || scene.Collision is not { } collision) return;
        var blocked = new SolidColorBrush(Color.FromArgb(120, 235, 40, 40));
        var forced = new SolidColorBrush(Color.FromArgb(150, 255, 140, 20));
        var unknown = new SolidColorBrush(Color.FromArgb(130, 175, 40, 200));
        var variation = new Pen(Brushes.Yellow, 2 / scale);
        var blockedGeometry = new StreamGeometry(); var forcedGeometry = new StreamGeometry();
        var unknownGeometry = new StreamGeometry(); var variationGeometry = new StreamGeometry();
        using (var blockedContext = blockedGeometry.Open())
        using (var forcedContext = forcedGeometry.Open())
        using (var unknownContext = unknownGeometry.Open())
        using (var variationContext = variationGeometry.Open())
        {
        void DiamondInto(StreamGeometryContext context, double x, double y, double width = 160, double height = 80)
        {
            context.BeginFigure(new(x + width / 2, y), true, true);
            context.LineTo(new(x + width, y + height / 2), true, false);
            context.LineTo(new(x + width / 2, y + height), true, false);
            context.LineTo(new(x, y + height / 2), true, false);
        }
        for (int y = 0; y < scene.Map.Height; y++) for (int x = 0; x < scene.Map.Width; x++)
        {
            var tile = collision.At(x, y); double px = (x - y + scene.Map.Height - 1) * 80, py = (x + y) * 40;
            if (tile.Override) DiamondInto(forcedContext, px, py);
            else if (tile.Unresolved) DiamondInto(unknownContext, px, py);
            else for (int sy = 0; sy < 5; sy++) for (int sx = 0; sx < 5; sx++)
            {
                if ((tile.Flags[sy * 5 + sx] & 9) == 0) continue;
                DiamondInto(blockedContext, px + 64 + (sx - sy) * 16, py + (sx + sy) * 8, 32, 16);
            }
            if (tile.VariantDependent) DiamondInto(variationContext, px, py);
        }
        }
        blockedGeometry.Freeze(); forcedGeometry.Freeze(); unknownGeometry.Freeze(); variationGeometry.Freeze();
        dc.DrawGeometry(blocked, null, blockedGeometry); dc.DrawGeometry(forced, null, forcedGeometry);
        dc.DrawGeometry(unknown, null, unknownGeometry); dc.DrawGeometry(null, variation, variationGeometry);
    }
}
