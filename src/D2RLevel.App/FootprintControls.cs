using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using D2RLevel.Core;

namespace D2RLevel.App;

public sealed partial class LegacyFloorWindow
{
    private Button? suggestFootprint, applyFootprint;
    private Action<ModelFootprint?>? updateFootprint;
    public event Func<LinkedTile[], double, bool, bool>? LinkFootprintRequested;
    private PlacementLinks? collisionLinks;
    private PresetEntity? collisionEntity;
    private Action? dismissCollisionDraft, reviewCollision, refreshCollisionTools;
    private Func<string>? verifyCollisionDraft;
    internal string VerifyOwnedCollisionEditing() => verifyCollisionDraft!();
    public void ConfigureLinkedCollision(PlacementLinks? links, PresetEntity? entity)
    {
        if (collisionLinks != links || collisionEntity != entity) dismissCollisionDraft?.Invoke();
        collisionLinks = links; collisionEntity = entity;
        refreshCollisionTools?.Invoke();
    }
    public void ReviewLinkedCollision() => reviewCollision?.Invoke();
    private TextBox? linkScale;
    private double LinkUnitsPerTile => double.Parse(linkScale?.Text ?? "10", CultureInfo.InvariantCulture);
    public void UpdateHdFootprint(ModelFootprint? footprint) => updateFootprint?.Invoke(footprint);
    private void InitializeFootprint(DockPanel root, ModelFootprint? footprint)
    {
        var bar = new WrapPanel(); DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        var name = new TextBlock { Text = "HD selection: " + (footprint?.Name ?? "Select a rendered model") + " · HD units/tile", Margin = new Thickness(8) }; bar.Children.Add(name);
        var units = new TextBox { Text = "10", Width = 50, Margin = new Thickness(6) }; bar.Children.Add(units);
        linkScale = units;
        var preview = new Button { Content = "Suggest model footprint" }; bar.Children.Add(preview);
        var apply = new Button { Content = "Apply suggested blocking", IsEnabled = false }; bar.Children.Add(apply);
        var link = new Button { Content = "Link footprint to HD", IsEnabled = false }; bar.Children.Add(link);
        var review = new Button { Content = "Edit linked collision", IsEnabled = false }; bar.Children.Add(review);
        var edit = new CheckBox { Content = "Draw footprint", IsEnabled = CollisionDocument is not null, Foreground = Brushes.White, Margin = new Thickness(6),
            ToolTip = "Left-drag on the map to add or remove footprint tiles. Start on a selected tile to erase, or an empty tile to add. Nothing changes until Save collision link." }; bar.Children.Add(edit);
        var remove = new Button { Content = "Remove collision link", IsEnabled = false,
            ToolTip = "Remove only this model's collision contribution, preserving other owners and protected blocking. Its unit link stays attached. Undo restores it." }; bar.Children.Add(remove);
        var claim = new CheckBox { Content = "Claim existing floor overrides", Foreground = Brushes.White, Margin = new Thickness(6),
            ToolTip = "Enable only if the existing painted blocking belongs to this model. Moving will clear that override at its old position. DT1 and wall blocking are preserved." }; bar.Children.Add(claim);
        suggestFootprint = preview; applyFootprint = apply;
        var clear = new Button { Content = "Dismiss suggestion" }; bar.Children.Add(clear);
        (int X, int Y)[] suggestion = [];
        bool drafting = false, drawing = false, adding = false;
        HashSet<(int X, int Y)> visited = [];
        (int X, int Y)? lastDraftTile = null;
        void Dismiss()
        {
            drafting = drawing = false; edit.IsChecked = false;
            claim.IsChecked = false;
            if (image.IsMouseCaptured) image.ReleaseMouseCapture();
            suggestion = []; apply.IsEnabled = false; link.IsEnabled = false;
            strokePreview.Children.Clear();
        }
        dismissCollisionDraft = Dismiss;
        refreshCollisionTools = () =>
        {
            apply.Visibility = LinkFootprintRequested is null ? Visibility.Visible : Visibility.Collapsed;
            var current = collisionLinks?.Warning is null && collisionEntity is not null ? collisionLinks?.Find(collisionEntity) : null;
            if (current?.Unit is { } linkedUnit && unitList.SelectedIndex != linkedUnit.Index) unitList.SelectedIndex = linkedUnit.Index;
            review.IsEnabled = remove.IsEnabled = current?.Tiles.Length > 0;
            if (current is not null) units.Text = current.UnitsPerTile.ToString("G17", CultureInfo.InvariantCulture);
            link.Content = current?.Tiles.Length > 0 ? "Save collision link" : "Link footprint to HD";
        };
        void DrawDraft()
        {
            strokePreview.Children.Clear();
            int shared = 0, protectedCells = 0;
            var current = collisionEntity is not null && collisionLinks?.Warning is null ? collisionLinks?.CollisionTiles(collisionEntity).ToHashSet() ?? [] : [];
            var ownershipByTile = collisionLinks?.Warning is null ? collisionLinks?.InspectOwnershipForTiles(suggestion.Select(p => new LinkedTile(p.X, p.Y))) : null;
            foreach (var (x, y) in suggestion)
            {
                var tile = new LinkedTile(x, y);
                var ownership = ownershipByTile?.GetValueOrDefault(tile);
                bool overlaps = (ownership?.Owners.Length ?? 0) > (current.Contains(tile) ? 1 : 0);
                if (overlaps) shared++;
                if (ownership?.ProtectedBlocking == true) protectedCells++;
                var geometry = Diamond((x - y + scene.Map.Height - 1) * 80, (x + y) * 40);
                geometry.Transform = new ScaleTransform(scale * zoom.Value, scale * zoom.Value);
                strokePreview.Children.Add(new System.Windows.Shapes.Path { Data = geometry,
                    Fill = new SolidColorBrush(overlaps ? Color.FromArgb(170, 220, 90, 230) : Color.FromArgb(170, 40, 190, 240)) });
            }
            apply.IsEnabled = suggestion.Length > 0 && !review.IsEnabled;
            link.IsEnabled = LinkFootprintRequested is not null && (suggestion.Length > 0 || review.IsEnabled);
            collisionStatus.Text = $"Draft: {suggestion.Length} tiles · {shared} shared with other models (magenta) · {protectedCells} with protected floor blocking. Cyan: this footprint. Draw footprint to refine; Save/Link commits one undoable edit. Existing DT1/wall blocking stays in place.";
        }
        reviewCollision = () =>
        {
            if (collisionEntity is null || collisionLinks?.Warning is not null || collisionLinks is null) return;
            try
            {
                CancelStroke(); Dismiss();
                suggestion = collisionLinks.CollisionTiles(collisionEntity).Select(t => (t.X, t.Y)).ToArray();
                if (suggestion.Length == 0 && footprint is not null)
                    suggestion = footprint.Tiles(CollisionDocument!.Width, CollisionDocument.Height, LinkUnitsPerTile)
                        .Where(t => CollisionDocument.CanBlock(t.X, t.Y)).ToArray();
                drafting = true; edit.IsChecked = true; DrawDraft();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!drafting || suggestion.Length == 0 || collisionScroll is null) return;
                    int span = suggestion.Max(t => t.X) - suggestion.Min(t => t.X) + suggestion.Max(t => t.Y) - suggestion.Min(t => t.Y) + 2;
                    zoom.Value = Math.Clamp(Math.Min(collisionScroll.ViewportWidth / (Math.Max(4, span) * 80 * scale),
                        collisionScroll.ViewportHeight / (Math.Max(4, span) * 40 * scale)), zoom.Minimum, zoom.Maximum);
                    UpdateLayout();
                    double cx = suggestion.Average(t => t.X - t.Y + scene.Map.Height) * 80 * scale * zoom.Value;
                    double cy = suggestion.Average(t => t.X + t.Y + 1) * 40 * scale * zoom.Value;
                    collisionScroll.ScrollToHorizontalOffset(Math.Max(0, cx - collisionScroll.ViewportWidth / 2));
                    collisionScroll.ScrollToVerticalOffset(Math.Max(0, cy - collisionScroll.ViewportHeight / 2));
                }));
            }
            catch (Exception ex) { Dismiss(); collisionStatus.Text = ex.Message; }
        };
        review.Click += (_, _) => reviewCollision();
        remove.Click += (_, _) =>
        {
            try { if (LinkFootprintRequested?.Invoke([], LinkUnitsPerTile, false) == true) Dismiss(); }
            catch (Exception ex) { collisionStatus.Text = ex.Message; }
        };
        preview.IsEnabled = footprint is not null;
        updateFootprint = next =>
        {
            if (Equals(footprint, next)) return;
            Dismiss(); footprint = next; preview.IsEnabled = next is not null;
            name.Text = "HD selection: " + (next?.Name ?? "Select a rendered model") + " · HD units/tile";
        };
        clear.Click += (_, _) => Dismiss(); units.TextChanged += (_, _) => Dismiss();
        CollisionViewChanged += Dismiss;
        Deactivated += (_, _) => Dismiss();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Dismiss(); };
        collisionTool.SelectionChanged += (_, _) => Dismiss();
        zoom.ValueChanged += (_, _) => { if (drafting) DrawDraft(); else Dismiss(); };
        preview.Click += (_, _) =>
        {
            if (footprint is null) return;
            CancelStroke(); Dismiss();
            try
            {
                var doc = CollisionDocument!;
                double value = double.Parse(units.Text, CultureInfo.InvariantCulture);
                suggestion = footprint.Tiles(doc.Width, doc.Height, value).Where(p => doc.CanBlock(p.X, p.Y)).ToArray();
                drafting = true; DrawDraft();
            }
            catch (Exception ex) { Dismiss(); collisionStatus.Text = ex.Message; }
        };
        apply.Click += (_, _) =>
        {
            try
            {
                int changed = CollisionDocument!.Paint(suggestion, true); Dismiss(); CollisionChanged();
                collisionStatus.Text = $"Applied {changed} suggested layer cells. Ctrl+Z undoes this footprint; save a DS1 copy to export it.";
            }
            catch (Exception ex) { collisionStatus.Text = ex.Message; }
        };
        link.Click += (_, _) =>
        {
            try { if (LinkFootprintRequested?.Invoke(suggestion.Select(p => new LinkedTile(p.X, p.Y)).ToArray(), LinkUnitsPerTile, claim.IsChecked == true) == true) Dismiss(); }
            catch (Exception ex) { collisionStatus.Text = ex.Message; }
        };
        // Any other edit invalidates the reviewed suggestion.
        collisionUndo.Click += (_, _) => Dismiss(); collisionRedo.Click += (_, _) => Dismiss();
        void DrawAt(Point point)
        {
            if (TileAt(point) is not { } tile) { lastDraftTile = null; return; }
            var cells = suggestion.ToHashSet();
            var from = lastDraftTile ?? tile; lastDraftTile = tile;
            int steps = Math.Max(Math.Abs(tile.X - from.X), Math.Abs(tile.Y - from.Y));
            for (int i = 0; i <= steps; i++)
            {
                var p = (X: from.X + (int)Math.Round((tile.X - from.X) * (double)i / Math.Max(1, steps)),
                         Y: from.Y + (int)Math.Round((tile.Y - from.Y) * (double)i / Math.Max(1, steps)));
                if (!CollisionDocument!.CanBlock(p.X, p.Y) || !visited.Add(p)) continue;
                if (adding) { if (cells.Count < 4096) cells.Add(p); } else cells.Remove(p);
            }
            suggestion = cells.ToArray(); DrawDraft();
        }
        image.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (edit.IsChecked != true) { Dismiss(); return; }
            if (TileAt(e.GetPosition(image)) is not { } tile) return;
            CancelStroke(); drafting = drawing = true; adding = !suggestion.Contains(tile); visited.Clear(); lastDraftTile = null;
            image.CaptureMouse(); DrawAt(e.GetPosition(image)); e.Handled = true;
        };
        image.PreviewMouseMove += (_, e) => { if (drawing) { DrawAt(e.GetPosition(image)); e.Handled = true; } };
        image.PreviewMouseLeftButtonUp += (_, e) =>
        { if (drawing) { DrawAt(e.GetPosition(image)); drawing = false; image.ReleaseMouseCapture(); e.Handled = true; } };
        image.LostMouseCapture += (_, _) => drawing = false;
        edit.Checked += (_, _) => { if (!drafting) { drafting = true; DrawDraft(); } };
        verifyCollisionDraft = () =>
        {
            var doc = CollisionDocument!;
            var before = doc.Serialize();
            reviewCollision();
            if (!drafting || suggestion.Length == 0 || !link.IsEnabled) throw new InvalidOperationException("No editable collision draft.");
            var first = suggestion[0]; int count = suggestion.Length;
            Point Center((int X, int Y) p) => new((p.X - p.Y + scene.Map.Height) * 80 * scale * zoom.Value,
                (p.X + p.Y + 1) * 40 * scale * zoom.Value);
            adding = false; visited.Clear(); lastDraftTile = null; DrawAt(Center(first));
            if (suggestion.Length != count - 1 || !before.SequenceEqual(doc.Serialize())) throw new InvalidOperationException("Draft erase changed DS1 or failed.");
            adding = true; visited.Clear(); lastDraftTile = null; DrawAt(Center(first));
            double oldZoom = zoom.Value; zoom.Value += .1;
            if (suggestion.Length != count || strokePreview.Children.Count != count || !before.SequenceEqual(doc.Serialize())) throw new InvalidOperationException("Draft zoom lost preview or mutated DS1.");
            zoom.Value = oldZoom;
            link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (collisionLinks!.Find(collisionEntity!) is not { Unit: not null } saved || saved.Tiles.Length != count)
                throw new InvalidOperationException("Collision UI did not preserve existing unit link.");
            doc.Undo();
            if (!before.SequenceEqual(doc.Serialize()) || collisionLinks.Find(collisionEntity!)!.Tiles.Length != 0) throw new InvalidOperationException("Draft commit undo failed.");
            doc.Redo();
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (collisionLinks.Find(collisionEntity!) is not { Unit: not null, Tiles.Length: 0 }) throw new InvalidOperationException("Remove collision removed unit link.");
            doc.Undo(); reviewCollision();
            return $"PASS collision draft: {count} reviewed tiles, add/erase, zoom preservation, no mutation before commit, combined unit link, undo/redo and removal retaining unit.";
        };
    }
    internal string VerifyFootprintSuggestion(string output)
    {
        if (suggestFootprint is null || applyFootprint is null) return "No selected model footprint.";
        var doc = CollisionDocument!; var before = doc.Serialize();
        suggestFootprint.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!applyFootprint.IsEnabled || !doc.Serialize().SequenceEqual(before) || strokePreview.Children.Count == 0)
            throw new InvalidOperationException("Footprint suggestion failed or changed DS1 before application.");
        int count = strokePreview.Children.Count;
        applyFootprint.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var after = doc.Serialize();
        if (before.SequenceEqual(after)) return $"PASS footprint: {count} tiles reviewed; already blocked.";
        doc.Undo();
        if (!doc.Serialize().SequenceEqual(before)) throw new InvalidOperationException("Footprint undo did not restore DS1.");
        doc.Redo(); doc.SaveCopy(System.IO.Path.Combine(output, "model-footprint.ds1"));
        if (!Ds1CollisionDocument.Load(System.IO.Path.Combine(output, "model-footprint.ds1")).Serialize().SequenceEqual(after))
            throw new InvalidOperationException("Footprint save/reopen failed.");
        CollisionChanged(); suggestFootprint.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return $"PASS footprint: {count} suggested tiles; explicit application, undo/redo and DS1 save/reopen.";
    }
}
