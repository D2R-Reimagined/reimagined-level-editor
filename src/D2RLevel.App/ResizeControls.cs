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
        var result = LevelResizer.Apply(session.Root, session.Scene, growth, fill, tile, repeatTerrain);
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

    /// <summary>The growth, fill and terrain choice, or null when canceled.</summary>
    private (LevelGrowth Growth, GroundFill Fill, uint Tile, bool RepeatTerrain)? ResizeDialog(Ds1CollisionDocument map)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = L.T("Resize level"), FontSize = 22, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(new TextBlock { Text = L.T("Current size: {0} × {1} tiles. Add tiles on any edge; nothing already in the level is removed.", map.Width, map.Height), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        // Edges are named as the ground editor draws them: X runs down to the right, Y down to the left.
        var edges = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        edges.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        edges.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        TextBox Edge(string label, int row)
        {
            edges.RowDefinitions.Add(new RowDefinition());
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 8, 3) };
            var box = new TextBox { Text = "0", MaxLength = 3, Margin = new Thickness(3) };
            Grid.SetRow(text, row); Grid.SetRow(box, row); Grid.SetColumn(box, 1);
            edges.Children.Add(text); edges.Children.Add(box);
            return box;
        }
        var left = Edge(L.T("X start · upper-left edge"), 0);
        var right = Edge(L.T("X end · lower-right edge"), 1);
        var top = Edge(L.T("Y start · upper-right edge"), 2);
        var bottom = Edge(L.T("Y end · lower-left edge"), 3);
        panel.Children.Add(edges);
        var size = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
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
            ToolTip = L.T("Places whole copies of the existing terrain next to it so the 3D view has ground under the new tiles. Copies can reach past the new edge. Not verified in game.") };
        panel.Children.Add(terrain);
        panel.Children.Add(new TextBlock { Text = L.T("The level is saved and reopened; previous files are kept as .bak. Adding to an X start or Y start edge moves every object, placement, patrol and link with the existing ground. Level tables (LvlPrest and Levels SizeX/SizeY) are not changed; update them before testing in game."),
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
        void Update()
        {
            if (Read() is not { } growth) { size.Text = L.T("Enter whole numbers of tiles."); apply.IsEnabled = false; return; }
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
