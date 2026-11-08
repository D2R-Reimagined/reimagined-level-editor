using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    /// <summary>Resize level: add or remove tiles on any edge, fill new ones with ground, and reopen the resized level.</summary>
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

    /// <summary>Resizes the saved scene on disk and reopens it in place of the open documents, which must be saved.</summary>
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
        string terrain = (result.TerrainCopies > 0 ? " " + L.T("HD terrain repeated {0} time(s) to cover the new space.", result.TerrainCopies) : "")
            + (result.UnlinkedObjects > 0 ? " " + L.T("{0} object(s) on removed tiles were unlinked from the DS1 and left in place.", result.UnlinkedObjects) : "");
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

    /// <summary>The parts of <paramref name="outer"/> outside <paramref name="inner"/>, as up to four strips.</summary>
    internal static IEnumerable<Rect> Minus(Rect outer, Rect inner)
    {
        var i = Rect.Intersect(outer, inner);
        if (i.IsEmpty || i.Width <= 0 || i.Height <= 0) { yield return outer; yield break; }
        if (i.Left > outer.Left) yield return new(outer.Left, outer.Top, i.Left - outer.Left, outer.Height);
        if (outer.Right > i.Right) yield return new(i.Right, outer.Top, outer.Right - i.Right, outer.Height);
        if (i.Top > outer.Top) yield return new(i.Left, outer.Top, i.Width, i.Top - outer.Top);
        if (outer.Bottom > i.Bottom) yield return new(i.Left, i.Bottom, i.Width, outer.Bottom - i.Bottom);
    }

    /// <summary>The original map, the resized map and the tiles both keep, in original tile coordinates.</summary>
    internal static (Rect Original, Rect Resized, Rect Kept) ResizeAreas(LevelGrowth growth, int width, int height)
    {
        var original = new Rect(0, 0, width, height);
        var resized = new Rect(-growth.Left, -growth.Top, Math.Max(0, growth.NewWidth(width)), Math.Max(0, growth.NewHeight(height)));
        return (original, resized, Rect.Intersect(original, resized) is { IsEmpty: false } kept ? kept : Rect.Empty);
    }

    private static readonly System.Windows.Media.Color RemovedColor = System.Windows.Media.Color.FromArgb(150, 235, 50, 50);
    private static readonly System.Windows.Media.Color AddedColor = System.Windows.Media.Color.FromArgb(110, 60, 200, 100);

    /// <summary>Lays the resize over the 3D view: red where tiles go, green where they come, and the new edge outlined.</summary>
    private void ShowResizePreview(LevelGrowth? growth, Ds1CollisionDocument map)
    {
        if (growth is null || placementLinks is null) { Scene.ClearResizePreview(); return; }
        double units = placementLinks.Calibration.UnitsPerTile;
        Rect Hd(Rect tiles) => new(tiles.X * units, tiles.Y * units, tiles.Width * units, tiles.Height * units);
        var (original, resized, _) = ResizeAreas(growth, map.Width, map.Height);
        var patches = Minus(original, resized).Select(r => new GroundPatch(Hd(r), RemovedColor))
            .Concat(Minus(resized, original).Select(r => new GroundPatch(Hd(r), AddedColor))).ToArray();
        Scene.SetResizePreview(patches, resized.IsEmpty ? null : Hd(resized), (x, z) => npcGround.Height(x, z), units);
    }

    /// <summary>The resize, fill and terrain choice, or null when canceled.</summary>
    private (LevelGrowth Growth, GroundFill Fill, uint Tile, bool RepeatTerrain)? ResizeDialog(Ds1CollisionDocument map)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = L.T("Resize level"), FontSize = 22, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(new TextBlock { Text = L.T("Current size: {0} × {1} tiles. Use + to add tiles on an edge and − to remove them. Red tiles are removed with everything on them.", map.Width, map.Height), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        // Directions are named as the ground editor and the game draw the map: X runs down to the right, Y down to the left,
        // so each box sits beside the diamond edge it changes.
        var edges = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        for (int i = 0; i < 3; i++) edges.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 3; i++) edges.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bool startsLocked = map.GameplayWarning is not null;
        // Placements on removed tiles cannot be found without readable gameplay data, so such a map only grows.
        bool canCrop = !startsLocked;
        var removedBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 120, 110));
        TextBox Edge(string label, string axis, int row, int column, bool locked, int length)
        {
            int min = canCrop ? -(length - 1) : 0;
            var box = new TextBox { Text = "0", MaxLength = 4, Width = 48, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, IsEnabled = !locked };
            void Step(int by) { if (EdgeCount(box) is { } n) box.Text = Math.Clamp(n + by, min, 999).ToString(CultureInfo.InvariantCulture); }
            Button Stepper(string text, int by, string tip)
            {
                var button = new Button { Content = text, Width = 26, Padding = new Thickness(0), Margin = new Thickness(2, 0, 2, 0), IsEnabled = !locked, ToolTip = tip };
                button.Click += (_, _) => Step(by);
                return button;
            }
            box.PreviewKeyDown += (_, k) => { if (k.Key is System.Windows.Input.Key.Up or System.Windows.Input.Key.Down) { Step(k.Key == System.Windows.Input.Key.Up ? 1 : -1); k.Handled = true; } };
            var normal = box.Foreground;
            box.TextChanged += (_, _) => box.Foreground = EdgeCount(box) < 0 ? removedBrush : normal;
            var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right };
            controls.Children.Add(Stepper("−", -1, canCrop ? L.T("One tile fewer: removes a row of tiles from this edge once below zero.") : L.T("One tile fewer")));
            controls.Children.Add(box); controls.Children.Add(Stepper("+", 1, L.T("One tile more")));
            var cell = new StackPanel { Margin = new Thickness(3, 4, 3, 4), ToolTip = locked ? L.T("This map's gameplay data cannot be shifted, so it can only grow toward the lower edges.") : axis };
            cell.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3), HorizontalAlignment = controls.HorizontalAlignment,
                Foreground = locked ? System.Windows.Media.Brushes.Gray : System.Windows.Media.Brushes.White });
            cell.Children.Add(controls);
            Grid.SetRow(cell, row); Grid.SetColumn(cell, column);
            edges.Children.Add(cell);
            return box;
        }
        var left = Edge(L.T("↖ Upper-left"), L.T("X start: adds columns before the first one, or removes the first columns; everything kept moves with the ground."), 0, 0, startsLocked, map.Width);
        var top = Edge(L.T("Upper-right ↗"), L.T("Y start: adds rows before the first one, or removes the first rows; everything kept moves with the ground."), 0, 2, startsLocked, map.Height);
        var bottom = Edge(L.T("↙ Lower-left"), L.T("Y end: adds rows after the last one, or removes the last rows."), 2, 0, false, map.Height);
        var right = Edge(L.T("Lower-right ↘"), L.T("X end: adds columns after the last one, or removes the last columns."), 2, 2, false, map.Width);
        var preview = new System.Windows.Controls.Canvas { Width = 300, Height = 170, Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Center, ClipToBounds = true };
        Grid.SetRow(preview, 1); Grid.SetColumnSpan(preview, 3);
        edges.Children.Add(preview);
        panel.Children.Add(edges);
        panel.Children.Add(new TextBlock { Text = L.T("Red: removed · Green: added · Dashed: HD terrain"), Foreground = System.Windows.Media.Brushes.LightGray, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) });
        var size = new TextBlock { FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(size);
        var loss = new TextBlock { Foreground = removedBrush, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(loss);

        var floors = pairedScene!.Tiles.Keys.Where(k => k.Main is >= 0 and <= 63 && k.Sub is >= 0 and <= 255).OrderBy(k => k.Main).ThenBy(k => k.Sub).ToArray();
        int common = CommonFloor(pairedScene) is { } usual ? Array.IndexOf(floors, usual) : -1;
        // Two fixed choices first, then every floor the level's tileset can draw.
        var fill = new ComboBox { ItemsSource = new[] { L.T("Extend the edge ground outward"), L.T("Leave empty") }.Concat(floors.Select(k => L.T("Floor {0}:{1}", k.Main, k.Sub))).ToArray(),
            SelectedIndex = common >= 0 ? common + 2 : floors.Length > 0 ? 2 : 0, Margin = new Thickness(3), Foreground = System.Windows.Media.Brushes.Black };
        fill.WithReadableItems();
        var fillLabel = new TextBlock { Text = L.T("Fill new ground with") };
        panel.Children.Add(fillLabel); panel.Children.Add(fill);
        bool hasTerrain = document!.Entities.Any(en => en.IsTerrain && !en.HasParent && en.CanTransform);
        var terrain = new CheckBox { Content = L.T("Repeat the HD terrain mesh into the new space"), IsChecked = hasTerrain, IsEnabled = hasTerrain, Margin = new Thickness(3, 10, 3, 3), Foreground = System.Windows.Media.Brushes.White,
            ToolTip = L.T("Places whole copies of the level's terrain mesh edge to edge, one mesh width apart, so the 3D view has ground under the new tiles. Copies can reach past the new edge. Level → Fill terrain gaps repairs levels resized before. Not verified in game.") };
        panel.Children.Add(terrain);
        panel.Children.Add(new TextBlock { Text = L.T("The level is saved and reopened; previous files are kept as .bak. Changing the upper-left or upper-right edge moves every object, placement, patrol and link with the existing ground. Removing tiles deletes their ground, walls, exits, placements and patrol points; HD objects stay where they are. Level tables (LvlPrest and Levels SizeX/SizeY) are not changed; update them before testing in game."),
            TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.LightGray, Margin = new Thickness(0, 12, 0, 12) });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true }; var apply = new Button { Content = L.T("Resize"), IsDefault = true };
        buttons.Children.Add(cancel); buttons.Children.Add(apply); panel.Children.Add(buttons);

        // Terrain outlines in tile coordinates show where the level's visible ground ends.
        double units = placementLinks?.Calibration.UnitsPerTile ?? GridCalibration.FallbackUnitsPerTile;
        var terrainTiles = document.TerrainEntities().Select(TerrainExtentOf).OfType<TerrainExtent>()
            .Select(t => new Rect(t.MinX / units, t.MinZ / units, t.SizeX / units, t.SizeZ / units)).Where(r => r.Width > 0 && r.Height > 0).ToArray();

        LevelGrowth? Read()
        {
            if (EdgeCount(left) is not { } l || EdgeCount(top) is not { } t || EdgeCount(right) is not { } r || EdgeCount(bottom) is not { } b) return null;
            return new(l, t, r, b);
        }
        System.Windows.Media.Brush Paint(System.Windows.Media.Color color) => new System.Windows.Media.SolidColorBrush(color);
        void Draw(LevelGrowth growth)
        {
            preview.Children.Clear();
            var (original, resized, kept) = ResizeAreas(growth, map.Width, map.Height);
            // Fit everything worth comparing: the map before and after, and the terrain that is actually drawn.
            var all = original; all.Union(resized); foreach (var t in terrainTiles) all.Union(t);
            double gx0 = all.Left, gy0 = all.Top, gx1 = all.Right, gy1 = all.Bottom;
            // Isometric tile corners: x runs down-right, y down-left.
            double spanX = (gx1 - gy0) - (gx0 - gy1), spanY = (gx1 + gy1 - gx0 - gy0) / 2;
            double scale = Math.Min((preview.Width - 12) / spanX, (preview.Height - 12) / spanY);
            double ox = preview.Width / 2 - ((gx1 - gy0) + (gx0 - gy1)) / 2 * scale, oy = preview.Height / 2 - (gx0 + gy0 + gx1 + gy1) / 4 * scale;
            System.Windows.Point P(double x, double y) => new(ox + (x - y) * scale, oy + (x + y) / 2 * scale);
            void Diamond(Rect r, System.Windows.Media.Brush? fill, System.Windows.Media.Brush? stroke, double thickness = 1.5, bool dashed = false)
            {
                if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return;
                var shape = new System.Windows.Shapes.Polygon { Points = [P(r.Left, r.Top), P(r.Right, r.Top), P(r.Right, r.Bottom), P(r.Left, r.Bottom)], Fill = fill, Stroke = stroke, StrokeThickness = thickness };
                if (dashed) shape.StrokeDashArray = [3, 2];
                preview.Children.Add(shape);
            }
            foreach (var r in Minus(resized, original)) Diamond(r, Paint(AddedColor), null);
            foreach (var r in Minus(original, resized)) Diamond(r, Paint(RemovedColor), null);
            Diamond(kept, Paint(System.Windows.Media.Color.FromRgb(58, 74, 92)), null);
            Diamond(original, null, Paint(System.Windows.Media.Color.FromRgb(157, 176, 196)), 1);
            foreach (var t in terrainTiles) Diamond(t, null, System.Windows.Media.Brushes.White, 1, true);
            Diamond(resized, null, Paint(System.Windows.Media.Color.FromRgb(255, 232, 140)), 2);
        }
        void Update()
        {
            var growth = Read();
            ShowResizePreview(growth, map);
            if (growth is null) { size.Text = L.T("Enter whole numbers of tiles."); loss.Text = ""; apply.IsEnabled = false; return; }
            Draw(growth);
            int width = growth.NewWidth(map.Width), height = growth.NewHeight(map.Height);
            string? problem = growth.Left == 0 && growth.Top == 0 && growth.Right == 0 && growth.Bottom == 0 ? L.T("Add or remove at least one tile on one edge.")
                : width > LevelGrowth.MaximumSize || height > LevelGrowth.MaximumSize ? L.T("Levels can be at most {0} × {0} tiles.", LevelGrowth.MaximumSize)
                : width < 1 || height < 1 ? L.T("A level must keep at least one tile in each direction.") : null;
            size.Text = L.T("New size: {0} × {1} tiles", width, height) + (problem is null ? "" : " · " + problem);
            apply.IsEnabled = problem is null;
            bool adds = growth.Left > 0 || growth.Top > 0 || growth.Right > 0 || growth.Bottom > 0;
            // Ground and terrain choices only matter for new tiles.
            fillLabel.Visibility = fill.Visibility = terrain.Visibility = adds ? Visibility.Visible : Visibility.Collapsed;
            loss.Text = "";
            if (problem is null && growth.Crops)
            {
                var lost = map.Loss(growth);
                int links = placementLinks?.Warning is null ? placementLinks?.LinksRemovedBy(growth) ?? 0 : 0;
                var (original, _, kept) = ResizeAreas(growth, map.Width, map.Height);
                long tiles = (long)(original.Width * original.Height - (kept.IsEmpty ? 0 : kept.Width * kept.Height));
                loss.Text = L.T("Removes {0} tiles · {1} placements · {2} patrol points · {3} exit tiles · {4} object links", tiles, lost.Units, lost.PatrolPoints, lost.ExitTiles, links);
            }
        }
        foreach (var box in new[] { left, right, top, bottom }) box.TextChanged += (_, _) => Update();
        var dialog = new Window { Owner = this, Title = L.T("Resize level"), Width = 520, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground, Content = panel };
        // Beside the 3D view rather than over it, so the red preview there stays in sight.
        if (Scene.IsVisible && PresentationSource.FromVisual(Scene) is { CompositionTarget: { } target })
        {
            var corner = target.TransformFromDevice.Transform(Scene.PointToScreen(new System.Windows.Point(Scene.ActualWidth, 0)));
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = Math.Max(SystemParameters.VirtualScreenLeft, corner.X - dialog.Width - 12); dialog.Top = Math.Max(SystemParameters.VirtualScreenTop, corner.Y + 12);
        }
        apply.Click += (_, _) => dialog.DialogResult = true;
        try
        {
            Update();
            if (dialog.ShowDialog() != true || Read() is not { } chosen) return null;
            var (mode, tile) = fill.SelectedIndex switch
            {
                0 => (GroundFill.ExtendEdges, 0u),
                1 => (GroundFill.Empty, 0u),
                var i => (GroundFill.Tile, Ds1CollisionDocument.FloorKey(floors[i - 2].Main, floors[i - 2].Sub)),
            };
            bool adds = chosen.Left > 0 || chosen.Top > 0 || chosen.Right > 0 || chosen.Bottom > 0;
            // Nothing is filled when nothing is added, so a missing tile choice must not block a pure crop.
            if (!adds) (mode, tile) = (GroundFill.Empty, 0u);
            return (chosen, mode, tile, adds && terrain.IsChecked == true);
        }
        finally { Scene.ClearResizePreview(); }
    }

    private static int? EdgeCount(TextBox box)
    {
        string text = box.Text.Trim().Replace('−', '-');
        return text.Length == 0 || text == "-" ? 0 : int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n) ? n : null;
    }
}
