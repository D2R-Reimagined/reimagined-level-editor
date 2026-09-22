using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using D2RLevel.Core;

namespace D2RLevel.App;

internal sealed record EntranceArea(int Id, string Label);
internal sealed class EntranceEditor : Window
{
    private readonly Ds1CollisionDocument map;
    private readonly EntranceConnections connections;
    private readonly LevelTableEdits? edits;
    private readonly EditHistory history;
    private readonly Action changed;
    private readonly Action<int, int, int> moveExit;
    private readonly Action? save;
    private readonly string? editWarning;
    private readonly TextBox areaSearch = new() { ToolTip = L.T("Filter areas by name or ID") };
    private EntranceArea[] areas = [];
    private readonly ComboBox context = new ComboBox().WithReadableItems(), target = new ComboBox { IsEditable = true, IsTextSearchEnabled = true }.WithReadableItems(), variants = new ComboBox().WithReadableItems();
    private readonly ListBox routes = new() { Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new(0) };
    private readonly TextBlock details = Text(), targetTitle = Text(), planText = Text(), status = Text(), sourceTitle = Text();
    private readonly Button move = new() { Content = L.T("Move selected exit") }, preview = new() { Content = L.T("Preview connection change") }, apply = new() { Content = L.T("Apply preview"), IsEnabled = false };
    private readonly Button undo = new() { Content = L.T("Undo") }, redo = new() { Content = L.T("Redo") };
    internal EntranceMap SourceMap { get; } = new();
    internal EntranceMap TargetMap { get; } = new();
    private int? slot, targetSlot;
    private Ds1CollisionDocument? destinationMap;
    private ConnectionPlan? plan;
    private bool refreshing;
    private int? AreaId => (context.SelectedItem as EntranceArea)?.Id;
    private int? TargetId => (target.SelectedItem as EntranceArea)?.Id;
    internal string StatusText => status.Text;
    internal bool HasPlan => apply.IsEnabled;
    internal int? SelectedSlot => slot;
    private static TextBlock Text() => new() { TextWrapping = TextWrapping.Wrap, Margin = new(6), Foreground = Brushes.LightSteelBlue };
    private static Border Panel(UIElement child) => new() { Child = child, Background = new SolidColorBrush(Color.FromRgb(29, 39, 50)), CornerRadius = new(5), Margin = new(4), Padding = new(6) };
    public EntranceEditor(Ds1CollisionDocument map, LevelProperties properties, EntranceConnections connections, LevelTableEdits? edits, EditHistory history, Action<int, int, int> moveExit, Action changed, Action? save, string? editWarning)
    {
        this.map = map; this.connections = connections; this.edits = edits; this.history = history; this.moveExit = moveExit; this.changed = changed; this.save = save; this.editWarning = editWarning;
        Title = L.T("Entrances and exits"); Width = 1280; Height = 850; MinWidth = 1050; MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(19, 27, 36)); Foreground = Brushes.White;
        var root = new DockPanel { Margin = new(12) }; Content = root;
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = L.T("ENTRANCES & EXITS"), FontSize = 22, Margin = new(6) });
        heading.Children.Add(new TextBlock { Text = L.T("Inspect area links, choose exit markers on either map, and preview supported connection changes."), Margin = new(6, 0, 6, 10), TextWrapping = TextWrapping.Wrap });
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); status.Foreground = Brushes.LightGoldenrodYellow; footer.Children.Add(status);
        var actions = new WrapPanel(); footer.Children.Add(actions); actions.Children.Add(undo); actions.Children.Add(redo);
        var saveButton = new Button { Content = L.T("Save Scene"), IsEnabled = save is not null }; actions.Children.Add(saveButton);
        var close = new Button { Content = L.T("Close") }; actions.Children.Add(close); close.Click += (_, _) => Close();
        footer.Children.Add(new TextBlock { Text = L.T("Edits use shared scene undo. Save Scene writes the workspace map and Levels override. Test authored routes in game."), TextWrapping = TextWrapping.Wrap, Margin = new(6) });
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new(330) }); grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); root.Children.Add(grid);
        var left = new DockPanel(); grid.Children.Add(Panel(left));
        var sourceHead = new StackPanel(); DockPanel.SetDock(sourceHead, Dock.Top); left.Children.Add(sourceHead);
        sourceHead.Children.Add(new TextBlock { Text = L.T("AREA CONNECTIONS"), Margin = new(6), FontWeight = FontWeights.SemiBold }); sourceHead.Children.Add(context);
        routes.DisplayMemberPath = nameof(AreaConnection.Label); routes.MaxHeight = 245; ScrollViewer.SetHorizontalScrollBarVisibility(routes, ScrollBarVisibility.Disabled);
        var template = new FrameworkElementFactory(typeof(TextBlock)); template.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(AreaConnection.Label))); template.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); template.SetValue(MarginProperty, new Thickness(5)); routes.DisplayMemberPath = ""; routes.ItemTemplate = new DataTemplate { VisualTree = template };
        sourceHead.Children.Add(routes); left.Children.Add(new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var right = new DockPanel(); Grid.SetColumn(right, 1); grid.Children.Add(right);
        var author = new StackPanel(); var authorScroll = new ScrollViewer { Content = author, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 265 }; var authorPanel = Panel(authorScroll); DockPanel.SetDock(authorPanel, Dock.Bottom); right.Children.Add(authorPanel);
        author.Children.Add(new TextBlock { Text = L.T("AUTHOR CONNECTION"), FontWeight = FontWeights.SemiBold, Margin = new(6) });
        var buttons = new WrapPanel(); author.Children.Add(buttons); buttons.Children.Add(move); buttons.Children.Add(preview); buttons.Children.Add(apply); author.Children.Add(planText);
        var maps = new Grid(); maps.ColumnDefinitions.Add(new()); maps.ColumnDefinitions.Add(new()); right.Children.Add(maps);
        var sourcePane = new DockPanel(); var sourceBox = Panel(sourcePane); maps.Children.Add(sourceBox);
        var sourceTop = new StackPanel(); DockPanel.SetDock(sourceTop, Dock.Top); sourcePane.Children.Add(sourceTop); sourceTop.Children.Add(sourceTitle);
        var legend = Text(); legend.Text = L.T("Teal: exit slot · gold: selected\nPurple: special placement marker\nGray: floor and wall outline"); DockPanel.SetDock(legend, Dock.Bottom); sourcePane.Children.Add(legend); sourcePane.Children.Add(SourceMap);
        var targetPane = new DockPanel(); var targetBox = Panel(targetPane); Grid.SetColumn(targetBox, 1); maps.Children.Add(targetBox);
        var targetTop = new StackPanel(); DockPanel.SetDock(targetTop, Dock.Top); targetPane.Children.Add(targetTop); targetTop.Children.Add(new TextBlock { Text = L.T("DESTINATION / CANDIDATE AREA"), Margin = new(6) });
        targetTop.Children.Add(new TextBlock { Text = L.T("Find area by name or ID"), Margin = new(6, 2, 6, 2) }); targetTop.Children.Add(areaSearch); targetTop.Children.Add(target); targetTop.Children.Add(variants);
        DockPanel.SetDock(targetTitle, Dock.Bottom); targetPane.Children.Add(targetTitle); targetPane.Children.Add(TargetMap);
        SourceMap.SetMap(map); SourceMap.Selected += SelectSource; SourceMap.MoveRequested += MoveTo; TargetMap.Selected += s => { targetSlot = s; TargetMap.Selection = s; ClearPlan(); UpdateTargetTitle(); };
        context.WithReadableItems(nameof(EntranceArea.Label)); target.WithReadableItems(nameof(EntranceArea.Label));
        TextSearch.SetTextPath(context, nameof(EntranceArea.Label)); TextSearch.SetTextPath(target, nameof(EntranceArea.Label));
        context.ItemsSource = properties.Contexts.Where(c => c.Level is not null).Select(c => new EntranceArea(EntranceConnections.Number(c.Level!["Id"]), c.DisplayName)).ToArray();
        areas = connections.Areas.GroupBy(a => a["Id"]).Where(g => g.Count() == 1).Select(g => new EntranceArea(EntranceConnections.Number(g.Key), connections.Name(EntranceConnections.Number(g.Key)))).OrderBy(a => a.Label).ToArray(); target.ItemsSource = areas;
        areaSearch.TextChanged += (_, _) => { var selected = target.SelectedItem; target.ItemsSource = areas.Where(a => a.Label.Contains(areaSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray(); if (target.Items.Contains(selected)) target.SelectedItem = selected; };
        context.SelectionChanged += (_, _) => { slot = null; Refresh(); };
        routes.SelectionChanged += (_, _) => { if (!refreshing) SelectSource((routes.SelectedItem as AreaConnection)?.Endpoint.Slot); };
        target.SelectionChanged += (_, _) => LoadTarget(); variants.SelectionChanged += (_, _) => LoadVariant();
        move.Click += (_, _) => ArmMove(); preview.Click += (_, _) => PreviewChange(); apply.Click += (_, _) => ApplyChange();
        undo.Click += (_, _) => history.Undo(); redo.Click += (_, _) => history.Redo();
        saveButton.Click += (_, _) => Run(() => { save?.Invoke(); Refresh(); status.Text = L.T("Scene and staged connection changes saved."); });
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { SourceMap.MoveArmed = false; Refresh(); e.Handled = true; } };
        history.Changed += HistoryChanged; Closed += (_, _) => history.Changed -= HistoryChanged;
        if (context.Items.Count == 1) context.SelectedIndex = 0;
        Refresh();
        if (properties.Warning is not null && AreaId is null) status.Text = properties.Warning;
    }
    private void Run(Action action) { try { action(); } catch (Exception ex) { status.Text = ex.Message; } }
    private void HistoryChanged(object? subject) { changed(); Refresh(); }
    private void ClearPlan() { plan = null; apply.IsEnabled = false; planText.Text = L.T("Select an exit in each area, then preview. Join two unused endpoints, or swap two reciprocal pairs while preserving their return routes and Warp definitions."); }
    internal void SelectSource(int? value)
    {
        slot = value; SourceMap.MoveArmed = false; Refresh();
        if (AreaId is { } area && slot is { } s)
        {
            var connection = connections.Connection(new(area, s));
            areaSearch.Text = "";
            target.SelectedItem = target.Items.OfType<EntranceArea>().FirstOrDefault(a => a.Id == connection.Destination);
            targetSlot = connection.ReturnSlots.Count == 1 ? connection.ReturnSlots[0] : null; TargetMap.Selection = targetSlot; UpdateTargetTitle();
        }
    }
    internal void SelectTarget(int area, int? s)
    {
        target.SelectedItem = target.Items.OfType<EntranceArea>().FirstOrDefault(a => a.Id == area); targetSlot = s; TargetMap.Selection = s; ClearPlan(); UpdateTargetTitle();
    }
    internal void SearchAreas(string query) => areaSearch.Text = query;
    private void Refresh()
    {
        refreshing = true; ClearPlan(); SourceMap.Selection = slot; SourceMap.InvalidateVisual(); SourceMap.MoveArmed = false;
        sourceTitle.Text = L.T("CURRENT MAP") + "\n" + Path.GetFileName(map.SourcePath);
        routes.ItemsSource = AreaId is { } id ? Enumerable.Range(0, 8).Select(s => connections.Connection(new(id, s))).ToArray() : Array.Empty<AreaConnection>();
        routes.SelectedItem = routes.Items.OfType<AreaConnection>().FirstOrDefault(c => c.Endpoint.Slot == slot);
        details.Text = L.T("Select a numbered exit to inspect its destination. Click empty map space to clear selection.");
        if (slot is { } index)
        {
            var tiles = map.ExitTiles().Where(t => t.IsExit && t.Main == index).ToArray();
            details.Text = L.T("EXIT SLOT {0}", index) + "\n" + string.Join("\n", tiles.Select(t => $"Tile ({t.X}, {t.Y}) · wall {t.Layer + 1} · {t.Direction} · {(t.Hidden ? L.T("hidden") : L.T("visible"))}"));
            if (AreaId is { } area)
            {
                var c = connections.Connection(new(area, index));
                details.Text += "\n\n" + c.Name + "\n→ " + c.DestinationName + "\n" + L.T("Vis{0}: {1} · Warp{0}: {2}", index, c.Destination, c.Warp) + "\n" + L.T("Return slots: {0}", c.ReturnSlots.Count == 0 ? L.T("none resolved") : string.Join(", ", c.ReturnSlots));
                var definitions = connections.WarpRows(c.Warp);
                foreach (var w in definitions) details.Text += $"\n\n{w["Name"]} · {w["Direction"]}\n" + L.T("Landing offset: {0}, {1} subtiles", w["OffsetX"], w["OffsetY"]) + "\n" + L.T("Exit walk: {0}, {1} subtiles", w["ExitWalkX"], w["ExitWalkY"]) + "\n" + L.T("Selection rectangle: {0}, {1} · {2} × {3} pixels", w["SelectX"], w["SelectY"], w["SelectDX"], w["SelectDY"]);
                if (definitions.Count == 0) details.Text += "\n" + L.T("No matching Warp definition.");
                var incoming = connections.Areas.SelectMany(a => Enumerable.Range(0, 8).Where(s => EntranceConnections.Number(a["Vis" + s]) == area).Select(s => connections.Name(EntranceConnections.Number(a["Id"])) + " / " + s));
                details.Text += "\n\n" + L.T("INCOMING CONNECTIONS") + "\n" + string.Join("\n", incoming);
                if (tiles.Length == 0) details.Text += "\n\n" + L.T("This connection has no ordinary exit tile in this map. It may use generated geometry, another variant or special behavior.");
            }
            else details.Text += "\n\n" + L.T("No unambiguous area context. Tile placement can be inspected without assuming a destination.");
            details.Text += "\n\n" + (map.ExitMoveWarning(index) ?? L.T("Hidden exit group can move onto existing ground. Landing offsets and surrounding collision still need in-game checks."));
        }
        details.Text += "\n\n" + L.T("TABLE SOURCES") + "\n" + string.Join("\n", connections.Sources.Select(t => t.Name + ": " + (t.Warning ?? t.SourcePath ?? L.T("unavailable"))));
        move.IsEnabled = editWarning is null && slot is { } selected && map.ExitMoveWarning(selected) is null;
        preview.IsEnabled = edits is not null && editWarning is null && AreaId is not null && slot is not null && TargetId is not null && targetSlot is not null;
        undo.IsEnabled = history.CanUndo; redo.IsEnabled = history.CanRedo;
        status.Text = editWarning ?? (edits is null ? L.T("Open a mod workspace scene to author area connections. Marker moves can be saved with the paired DS1.") : edits.IsDirty ? L.T("Connection edits are staged. Save Scene to write the workspace Levels override.") : L.T("Select an exit marker or an area connection to begin."));
        UpdateTargetTitle(); refreshing = false;
    }
    private void LoadTarget()
    {
        targetSlot = null; TargetMap.Selection = null; ClearPlan(); variants.ItemsSource = TargetId is { } id ? connections.Variants(id) : Array.Empty<string>();
        if (variants.Items.Count > 0) variants.SelectedIndex = 0; else LoadVariant();
        UpdateTargetTitle();
    }
    private void LoadVariant()
    {
        destinationMap = null; targetSlot = null; TargetMap.Selection = null; ClearPlan();
        Run(() => { if (variants.SelectedItem is string path) { string full = connections.ResolveMap(path); destinationMap = full.Equals(map.SourcePath, StringComparison.OrdinalIgnoreCase) ? map : Ds1CollisionDocument.Load(full); } });
        TargetMap.SetMap(destinationMap); UpdateTargetTitle();
    }
    private void UpdateTargetTitle()
    {
        targetTitle.Text = (TargetId is { } id ? connections.Name(id) : L.T("Choose an area above")) + "\n" + (targetSlot is { } s && TargetId is { } a ? L.T("Slot {0} → {1}", s, connections.Connection(new(a, s)).DestinationName) : L.T("Select a numbered destination exit."));
        if (TargetId is { } targetId && EntranceConnections.Number(connections.Area(targetId)["DrlgType"]) != 2) targetTitle.Text += "\n" + L.T("Generated area: inspect links here; geometry is chosen at runtime.");
        preview.IsEnabled = edits is not null && editWarning is null && AreaId is not null && slot is not null && TargetId is not null && targetSlot is not null;
    }
    internal void ArmMove()
    {
        if (!move.IsEnabled) return;
        SourceMap.MoveArmed = true; status.Text = L.T("Click ground in the current map to move the entire selected hidden exit group. Escape cancels.");
    }
    internal void MoveTo(int x, int y)
    {
        if (!SourceMap.MoveArmed || slot is not { } s || editWarning is not null) return;
        Run(() => { moveExit(s, x, y); SourceMap.MoveArmed = false; status.Text = L.T("Moved exit {0} to tile {1}, {2}. Use Undo to restore it.", s, x, y); });
    }
    internal void PreviewChange()
    {
        ClearPlan(); if (!preview.IsEnabled) return;
        Run(() => { plan = connections.Plan(new(AreaId!.Value, slot!.Value), new(TargetId!.Value, targetSlot!.Value)); planText.Text = plan.Description; apply.IsEnabled = true; status.Text = L.T("Review every affected route above. Apply stages one undoable connection edit."); });
    }
    internal void ApplyChange()
    {
        if (!apply.IsEnabled || plan is null) return;
        Run(() => { connections.Apply(new(AreaId!.Value, slot!.Value), new(TargetId!.Value, targetSlot!.Value)); });
    }
}
