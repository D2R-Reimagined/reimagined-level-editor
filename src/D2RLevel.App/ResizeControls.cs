using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    /// <summary>Resize level: add tiles on any edge, fill them with ground, and reopen the grown level.</summary>
    private async void ResizeLevel_Click(object sender, RoutedEventArgs e)
    {
        if (loading is not null) return;
        if (workspaceSession is not { } session || document is null || pairedScene?.Collision?.Document is not { } map || placementLinks is null)
        { Status.Text = L.T("Open a level project or workspace scene to resize it."); return; }
        if (placementLinks.Warning is not null) { Status.Text = placementLinks.Warning; return; }
        bool dirty = document.IsDirty || connectionEdits?.IsDirty == true || placementLinks.HasMetadataChanges || map.IsDirty;
        if (dirty)
        {
            if (MessageBox.Show(this, L.T("Resizing works on the saved level. Save your changes now and continue?"), L.T("Resize level"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            SaveScene_Click(sender, e);
            if (document.IsDirty || map.IsDirty || placementLinks.HasMetadataChanges) return; // The save failed and reported why.
        }
        if (ResizeDialog(map) is not { } choice || !CanReplace()) return;
        try { await ResizeOpenLevel(session, choice.Growth, choice.Fill, choice.Tile, choice.RepeatTerrain); }
        catch (Exception ex) { Error(ex); }
    }

    /// <summary>Grows the saved scene on disk and reopens it in place of the open documents, which must be saved.</summary>
    private async Task<LevelResizeResult> ResizeOpenLevel(WorkspaceSceneSession session, LevelGrowth growth, GroundFill fill, uint tile, bool repeatTerrain)
    {
        // Copies are spaced by the mesh as drawn, not by the map size times the calibration, which may disagree with it.
        var extents = document!.TerrainEntities().Select(t => (t.Id, Extent: TerrainExtentOf(t))).Where(p => p.Extent is not null)
            .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Extent!);
        var result = LevelResizer.Apply(session.Root, session.Scene, growth, fill, tile, repeatTerrain, extents);
        openingWorkspaceSession = new(session.Root, session.Scene);
        try
        {
            await LoadPreset(session.Scene.JsonPath);
            if (workspaceSession == openingWorkspaceSession) { exploringWorkspace = false; browsingWorkspace = false; SetWorkspaceView(); }
        }
        finally { openingWorkspaceSession = null; RefreshState(); }
        string terrain = result.TerrainCopies > 0 ? " " + L.T("HD terrain repeated {0} time(s) to cover the new space.", result.TerrainCopies) : "";
        Status.Text = L.T("Resized to {0} × {1} tiles · previous files kept as .bak.", result.Width, result.Height) + terrain;
        Notify(L.T("Level resized"), L.T("{0} × {1} tiles", result.Width, result.Height) + terrain);
        return result;
    }

    private TerrainExtent? TerrainExtentOf(PresetEntity terrain) =>
        Scene.GetFootprint(terrain) is { } f && f.MaxX > f.MinX && f.MaxZ > f.MinZ ? new(f.MinX, f.MinZ, f.MaxX, f.MaxZ) : null;

    /// <summary>
    /// Fill terrain gaps: lays copies of the level's terrain mesh on its own grid wherever a DS1 tile has no HD ground,
    /// optionally replacing copies that sit off that grid (such as ones an earlier resize spaced by the wrong size).
    /// </summary>
    private void FillTerrainGaps_Click(object sender, RoutedEventArgs e)
    {
        if (loading is not null) return;
        if (document is null || pairedScene?.Collision?.Document is not { } map || placementLinks is not { Warning: null } links)
        { Status.Text = L.T("Open a level with its paired DS1 to fill terrain gaps."); return; }
        try
        {
            var measured = document.TerrainEntities().Select(t => (Entity: t, Extent: TerrainExtentOf(t))).Where(p => p.Extent is not null)
                .Select(p => (p.Entity, Extent: p.Extent!)).ToArray();
            if (measured.Length == 0) { Status.Text = L.T("This level has no loaded terrain mesh to repeat."); return; }
            var (source, extent) = measured[0];
            var sameMesh = measured.Skip(1).Where(p => string.Equals(p.Entity.PreviewModel, source.PreviewModel, StringComparison.OrdinalIgnoreCase) && links.Find(p.Entity) is null);
            var misaligned = TerrainTiling.Misaligned(extent, sameMesh).ToHashSet();
            double units = links.Calibration.UnitsPerTile;
            Vector3d[] Offsets(bool replace) => TerrainTiling.CoverOffsets(extent, measured.Skip(1).Where(p => !replace || !misaligned.Contains(p.Entity)).Select(p => p.Extent), map.Width, map.Height, units);
            if (misaligned.Count == 0 && Offsets(false).Length == 0) { Status.Text = L.T("The terrain already covers every tile of the map."); return; }
            if (TerrainGapsDialog(source, extent, misaligned.Count, Offsets, links.Calibration) is not { } replace) return;
            var offsets = Offsets(replace);
            if (offsets.Length > LevelResizer.MaximumTerrainCopies)
                throw new InvalidOperationException(L.T("Filling the gaps would need {0} terrain copies (limit {1}). Check the HD units/tile calibration; the map area is measured with it.", offsets.Length, LevelResizer.MaximumTerrainCopies));
            Scene.CancelDrag();
            var repair = document.RepairTerrain(source, replace ? misaligned : [], offsets);
            Notify(L.T("Terrain gaps filled"), L.T("{0} terrain copies added, {1} misaligned copies removed. Ctrl+Z undoes both.", repair.Added.Length, repair.Removed.Length));
        }
        catch (Exception ex) { Error(ex); }
    }

    /// <summary>True to replace misaligned copies, false to keep them, or null when canceled.</summary>
    private bool? TerrainGapsDialog(PresetEntity source, TerrainExtent extent, int misaligned, Func<bool, Vector3d[]> offsets, GridCalibration calibration)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = L.T("Fill terrain gaps"), FontSize = 22, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(new TextBlock { Text = L.T("Copies of {0} ({1} × {2} HD units) are laid edge to edge on its own grid wherever a DS1 tile has no HD terrain under it.",
            source.Name, Math.Round(extent.SizeX, 2), Math.Round(extent.SizeZ, 2)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        var replace = new CheckBox { Content = L.T("Replace {0} terrain copies that do not line up with it", misaligned), IsChecked = misaligned > 0, IsEnabled = misaligned > 0,
            Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(3, 0, 3, 6),
            ToolTip = L.T("Copies of the same mesh that sit off its grid or on top of another copy, such as ones an earlier resize spaced by the map size. Other terrain is never removed.") };
        panel.Children.Add(replace);
        var summary = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) };
        panel.Children.Add(summary);
        if (calibration.Warning is { } warning)
            panel.Children.Add(new TextBlock { Text = L.T("The map area comes from the HD units/tile calibration, so check it first: {0}", warning), TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.Khaki, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = L.T("One undoable edit. Save the scene to keep it. Not verified in game."), TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.LightGray, Margin = new Thickness(0, 0, 0, 12) });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true }; var apply = new Button { Content = L.T("Fill gaps"), IsDefault = true };
        buttons.Children.Add(cancel); buttons.Children.Add(apply); panel.Children.Add(buttons);
        void Update()
        {
            bool swap = replace.IsChecked == true; int added = offsets(swap).Length;
            summary.Text = L.T("{0} copies to add · {1} to remove", added, swap ? misaligned : 0);
            apply.IsEnabled = added > 0 || swap;
        }
        replace.Click += (_, _) => Update();
        Update();
        var dialog = new Window { Owner = this, Title = L.T("Fill terrain gaps"), Width = 480, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground, Content = panel };
        apply.Click += (_, _) => dialog.DialogResult = true;
        return dialog.ShowDialog() == true ? replace.IsChecked == true : null;
    }

    /// <summary>Keeps viewport geometry in step with a terrain repair as it is applied, undone and redone.</summary>
    private void SyncTerrainRepair(TerrainRepair repair)
    {
        if (document is null) return;
        foreach (var entity in repair.Removed)
        {
            if (!document.Entities.Contains(entity))
            {
                if (Scene.GetItem(entity) is { } removed) addedModels[entity] = removed;
                Scene.RemoveItem(entity); removedModels.Add(entity);
            }
            else if (removedModels.Remove(entity) && addedModels.TryGetValue(entity, out var restored)) Scene.AddItem(restored);
        }
        if (Scene.GetItem(repair.Source) is { } source)
            foreach (var copy in repair.Added.Where(c => !addedModels.ContainsKey(c)))
            {
                Vector3d from = repair.Source.Transform.Position, to = copy.Transform.Position;
                addedModels[copy] = TerrainPreview.Moved(source, copy, new(to.X - from.X, to.Y - from.Y, to.Z - from.Z));
            }
        if (repair.Added.Length > 0) SyncDuplication(new([repair.Source], repair.Added));
        else { Filter(); PopulateInspector(); RefreshState(); }
    }

    /// <summary>The growth, fill and terrain choice, or null when canceled.</summary>
    private (LevelGrowth Growth, GroundFill Fill, uint Tile, bool RepeatTerrain)? ResizeDialog(Ds1CollisionDocument map)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = L.T("Resize level"), FontSize = 22, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(new TextBlock { Text = L.T("Current size: {0} × {1} tiles. Choose how many tiles to add in each direction; nothing already in the level is removed.", map.Width, map.Height), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        // Directions are named as the ground editor and the game draw the map: X runs down to the right, Y down to the left,
        // so each box sits beside the diamond edge it grows.
        var edges = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        for (int i = 0; i < 3; i++) edges.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 3; i++) edges.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bool startsLocked = map.GameplayWarning is not null;
        TextBox Edge(string label, string axis, int row, int column, bool locked)
        {
            var box = new TextBox { Text = "0", MaxLength = 3, Width = 44, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, IsEnabled = !locked };
            void Step(int by) { if (int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) || box.Text.Trim().Length == 0) box.Text = Math.Clamp(n + by, 0, 999).ToString(CultureInfo.InvariantCulture); }
            Button Stepper(string text, int by)
            {
                var button = new Button { Content = text, Width = 26, Padding = new Thickness(0), Margin = new Thickness(2, 0, 2, 0), IsEnabled = !locked };
                button.Click += (_, _) => Step(by);
                return button;
            }
            box.PreviewKeyDown += (_, k) => { if (k.Key is System.Windows.Input.Key.Up or System.Windows.Input.Key.Down) { Step(k.Key == System.Windows.Input.Key.Up ? 1 : -1); k.Handled = true; } };
            var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right };
            controls.Children.Add(Stepper("−", -1)); controls.Children.Add(box); controls.Children.Add(Stepper("+", 1));
            var cell = new StackPanel { Margin = new Thickness(3, 4, 3, 4), ToolTip = locked ? L.T("This map's gameplay data cannot be shifted, so it can only grow toward the lower edges.") : axis };
            cell.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3), HorizontalAlignment = controls.HorizontalAlignment,
                Foreground = locked ? System.Windows.Media.Brushes.Gray : System.Windows.Media.Brushes.White });
            cell.Children.Add(controls);
            Grid.SetRow(cell, row); Grid.SetColumn(cell, column);
            edges.Children.Add(cell);
            return box;
        }
        var left = Edge(L.T("↖ Upper-left"), L.T("X start: adds columns before the first one; everything already placed moves with the existing ground."), 0, 0, startsLocked);
        var top = Edge(L.T("Upper-right ↗"), L.T("Y start: adds rows before the first one; everything already placed moves with the existing ground."), 0, 2, startsLocked);
        var bottom = Edge(L.T("↙ Lower-left"), L.T("Y end: adds rows after the last one."), 2, 0, false);
        var right = Edge(L.T("Lower-right ↘"), L.T("X end: adds columns after the last one."), 2, 2, false);
        var preview = new System.Windows.Controls.Canvas { Width = 300, Height = 170, Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Center, ClipToBounds = true };
        Grid.SetRow(preview, 1); Grid.SetColumnSpan(preview, 3);
        edges.Children.Add(preview);
        panel.Children.Add(edges);
        var size = new TextBlock { FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(size);

        var floors = pairedScene!.Tiles.Keys.Where(k => k.Main is >= 0 and <= 63 && k.Sub is >= 0 and <= 255).OrderBy(k => k.Main).ThenBy(k => k.Sub).ToArray();
        int common = CommonFloor(pairedScene) is { } usual ? Array.IndexOf(floors, usual) : -1;
        // Two fixed choices first, then every floor the level's tileset can draw.
        var fill = new ComboBox { ItemsSource = new[] { L.T("Extend the edge ground outward"), L.T("Leave empty") }.Concat(floors.Select(k => L.T("Floor {0}:{1}", k.Main, k.Sub))).ToArray(),
            SelectedIndex = common >= 0 ? common + 2 : floors.Length > 0 ? 2 : 0, Margin = new Thickness(3), Foreground = System.Windows.Media.Brushes.Black };
        fill.WithReadableItems();
        panel.Children.Add(new TextBlock { Text = L.T("Fill new ground with") }); panel.Children.Add(fill);
        bool hasTerrain = document!.Entities.Any(en => en.IsTerrain && !en.HasParent && en.CanTransform);
        var terrain = new CheckBox { Content = L.T("Repeat the HD terrain mesh into the new space"), IsChecked = hasTerrain, IsEnabled = hasTerrain, Margin = new Thickness(3, 10, 3, 3), Foreground = System.Windows.Media.Brushes.White,
            ToolTip = L.T("Places whole copies of the level's terrain mesh edge to edge, one mesh width apart, so the 3D view has ground under the new tiles. Copies can reach past the new edge. Level → Fill terrain gaps repairs levels resized before. Not verified in game.") };
        panel.Children.Add(terrain);
        panel.Children.Add(new TextBlock { Text = L.T("The level is saved and reopened; previous files are kept as .bak. Growing toward the upper-left or upper-right moves every object, placement, patrol and link with the existing ground. Level tables (LvlPrest and Levels SizeX/SizeY) are not changed; update them before testing in game."),
            TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.LightGray, Margin = new Thickness(0, 12, 0, 12) });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true }; var apply = new Button { Content = L.T("Resize"), IsDefault = true };
        buttons.Children.Add(cancel); buttons.Children.Add(apply); panel.Children.Add(buttons);

        LevelGrowth? Read()
        {
            static int? Count(TextBox box) => int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : box.Text.Trim().Length == 0 ? 0 : null;
            if (Count(left) is not { } l || Count(top) is not { } t || Count(right) is not { } r || Count(bottom) is not { } b) return null;
            return new(l, t, r, b);
        }
        void Draw(LevelGrowth growth)
        {
            preview.Children.Clear();
            // Isometric tile corners: x runs down-right, y down-left, so the grown map is one diamond around the original.
            double gx0 = -growth.Left, gy0 = -growth.Top, gx1 = map.Width + growth.Right, gy1 = map.Height + growth.Bottom;
            double spanX = (gx1 - gy0) - (gx0 - gy1), spanY = (gx1 + gy1 - gx0 - gy0) / 2;
            double scale = Math.Min((preview.Width - 12) / spanX, (preview.Height - 12) / spanY);
            double ox = preview.Width / 2 - ((gx1 - gy0) + (gx0 - gy1)) / 2 * scale, oy = preview.Height / 2 - (gx0 + gy0 + gx1 + gy1) / 4 * scale;
            System.Windows.Point P(double x, double y) => new(ox + (x - y) * scale, oy + (x + y) / 2 * scale);
            System.Windows.Shapes.Polygon Diamond(double x0, double y0, double x1, double y1, string fill, string stroke) => new()
            {
                Points = [P(x0, y0), P(x1, y0), P(x1, y1), P(x0, y1)],
                Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(fill)!,
                Stroke = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(stroke)!, StrokeThickness = 1.5,
            };
            preview.Children.Add(Diamond(gx0, gy0, gx1, gy1, "#5534A853", "#FF4CC26B"));
            preview.Children.Add(Diamond(0, 0, map.Width, map.Height, "#FF3A4A5C", "#FF9DB0C4"));
        }
        void Update()
        {
            if (Read() is not { } growth) { size.Text = L.T("Enter whole numbers of tiles."); apply.IsEnabled = false; return; }
            Draw(growth);
            int width = growth.NewWidth(map.Width), height = growth.NewHeight(map.Height);
            string? problem = growth.Left + growth.Top + growth.Right + growth.Bottom == 0 ? L.T("Add at least one tile on one edge.")
                : width > LevelGrowth.MaximumSize || height > LevelGrowth.MaximumSize ? L.T("Levels can be at most {0} × {0} tiles.", LevelGrowth.MaximumSize) : null;
            size.Text = L.T("New size: {0} × {1} tiles", width, height) + (problem is null ? "" : " · " + problem);
            apply.IsEnabled = problem is null;
        }
        foreach (var box in new[] { left, right, top, bottom }) box.TextChanged += (_, _) => Update();
        Update();
        var dialog = new Window { Owner = this, Title = L.T("Resize level"), Width = 520, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground, Content = panel };
        apply.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true || Read() is not { } chosen) return null;
        var (mode, tile) = fill.SelectedIndex switch
        {
            0 => (GroundFill.ExtendEdges, 0u),
            1 => (GroundFill.Empty, 0u),
            var i => (GroundFill.Tile, Ds1CollisionDocument.FloorKey(floors[i - 2].Main, floors[i - 2].Sub)),
        };
        return (chosen, mode, tile, terrain.IsChecked == true);
    }
}
