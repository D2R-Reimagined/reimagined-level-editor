using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using D2RLevel.Core;

namespace D2RLevel.App;

internal sealed record GameplayBrowserItem(GameplayAsset Asset)
{
    public string Title => Asset.DisplayName;
    public string Subtitle => GameplayAssetBrowser.KindName(Asset.Kind) + " · " +
        (Asset.Id is { } id ? L.T("DS1 ID {0}", id) : L.T("Not mapped")) + (Asset.CanPlace ? "" : " · " + L.T("Unavailable"));
}

internal sealed class GameplayAssetBrowser : Window
{
    private readonly AssetResolver? assets;
    private readonly string? workspaceRoot;
    private readonly int act;
    private readonly Action<GameplayAsset, int, int, uint, NpcVisual?> place;
    private readonly LevelPlacement[] templates;
    private readonly string? placementWarning;
    private readonly TextBox search = new() { ToolTip = L.T("Search names, keys or preset IDs") };
    private readonly ComboBox category = new ComboBox { Margin = new(3) }.WithReadableItems();
    private readonly CheckBox available = new() { Content = L.T("Placeable in this act only"), IsChecked = true, Foreground = Brushes.White, Margin = new(6, 9, 6, 9) };
    private readonly ListBox list = new() { Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock count = new() { Margin = new(6), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap, Margin = new(10) };
    private readonly TextBlock previewNote = new() { TextWrapping = TextWrapping.Wrap, Margin = new(8), Foreground = Brushes.LightSteelBlue };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(6), Foreground = Brushes.LightGoldenrodYellow };
    private readonly TextBox x = new() { Width = 65 }, y = new() { Width = 65 }, flags = new() { Width = 100, ToolTip = L.T("Raw unsigned flags, decimal or 0x hexadecimal. Uses a matching template's flags when available; otherwise 0.") };
    private readonly Button add = new() { Content = L.T("Place gameplay unit"), IsEnabled = false };
    private readonly Button refresh = new() { Content = L.T("Refresh catalog") };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? previewLoad;
    private GameplayCatalog? catalog;
    private GameplayPreview? preview;
    private bool catalogBusy;
    internal SceneViewport Preview { get; } = new();
    public Task Ready { get; private set; } = Task.CompletedTask;
    public Task PreviewReady { get; private set; } = Task.CompletedTask;
    internal bool CanPlace => add.IsEnabled;
    internal bool HasMesh => preview?.Visual.Missing == false;
    internal string StatusText => status.Text;
    internal GameplayAsset? Selection => (list.SelectedItem as GameplayBrowserItem)?.Asset;

    public GameplayAssetBrowser(AssetResolver? assets, string? workspaceRoot, int act, int width, int height,
        LevelPlacement[] templates, Action<GameplayAsset, int, int, uint, NpcVisual?> place, string? placementWarning = null)
    {
        this.assets = assets; this.workspaceRoot = workspaceRoot; this.act = act; this.templates = templates; this.place = place;
        this.placementWarning = placementWarning;
        Title = L.T("Gameplay assets · Act {0}", act); Width = 1120; Height = 800; MinWidth = 950; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(20, 27, 35)); Foreground = Brushes.White;
        var root = new DockPanel { Margin = new(12) }; Content = root;
        var header = new DockPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        DockPanel.SetDock(refresh, Dock.Right); header.Children.Add(refresh);
        header.Children.Add(new TextBlock { Text = L.T("GAMEPLAY ASSETS · ACT {0}", act), FontSize = 18, Margin = new(6), VerticalAlignment = VerticalAlignment.Center });
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(status);
        var coordinates = new WrapPanel { Margin = new(3, 8, 3, 0) }; footer.Children.Add(coordinates);
        coordinates.Children.Add(new TextBlock { Text = L.T("Subtile X / Y"), VerticalAlignment = VerticalAlignment.Center });
        x.Text = (width * 5 / 2).ToString(); y.Text = (height * 5 / 2).ToString(); coordinates.Children.Add(x); coordinates.Children.Add(y);
        coordinates.Children.Add(new TextBlock { Text = L.T("Flags"), VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0) }); coordinates.Children.Add(flags);
        coordinates.Children.Add(add);
        var close = new Button { Content = L.T("Close"), IsCancel = true }; close.Click += (_, _) => Close(); coordinates.Children.Add(close);
        footer.Children.Add(new TextBlock { Text = L.T("Placement adds one DS1 unit with shared undo. It does not add decorative HD models or collision footprints. NPC services and special object behavior depend on game data."), TextWrapping = TextWrapping.Wrap, Margin = new(6, 8, 6, 0), Foreground = Brushes.LightSteelBlue });
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new(340) }); grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); root.Children.Add(grid);
        var left = new DockPanel { Margin = new(0, 6, 10, 0) }; grid.Children.Add(left);
        var filters = new StackPanel(); DockPanel.SetDock(filters, Dock.Top); left.Children.Add(filters);
        filters.Children.Add(new TextBlock { Text = L.T("Search names, keys or preset IDs"), Margin = new(6, 0, 6, 4) }); filters.Children.Add(search);
        category.ItemsSource = new[] { L.T("All categories") }.Concat(Enum.GetValues<GameplayAssetKind>().Select(KindName)); category.SelectedIndex = 0;
        filters.Children.Add(category); filters.Children.Add(available); DockPanel.SetDock(count, Dock.Bottom); left.Children.Add(count); left.Children.Add(list);
        var item = new FrameworkElementFactory(typeof(StackPanel)); item.SetValue(MarginProperty, new Thickness(6));
        var title = new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty, new Binding(nameof(GameplayBrowserItem.Title))); title.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); item.AppendChild(title);
        var subtitle = new FrameworkElementFactory(typeof(TextBlock)); subtitle.SetBinding(TextBlock.TextProperty, new Binding(nameof(GameplayBrowserItem.Subtitle))); subtitle.SetValue(TextBlock.ForegroundProperty, Brushes.LightSteelBlue); subtitle.SetValue(TextBlock.FontSizeProperty, 11d); item.AppendChild(subtitle);
        list.ItemTemplate = new() { VisualTree = item }; VirtualizingPanel.SetIsVirtualizing(list, true); ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var right = new Grid { Margin = new(6) }; Grid.SetColumn(right, 1); grid.Children.Add(right);
        right.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); right.RowDefinitions.Add(new() { Height = new(210) });
        var previewPanel = new DockPanel(); var notes = new Border { Child = previewNote }; DockPanel.SetDock(notes, Dock.Bottom); previewPanel.Children.Add(notes); previewPanel.Children.Add(Preview); right.Children.Add(previewPanel);
        var info = new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(32, 40, 51)) }; Grid.SetRow(info, 1); right.Children.Add(info);
        search.TextChanged += (_, _) => Filter(); category.SelectionChanged += (_, _) => Filter(); available.Click += (_, _) => Filter();
        list.SelectionChanged += (_, _) => PreviewReady = SelectEntry();
        refresh.Click += (_, _) => Ready = LoadCatalog(); add.Click += (_, _) => Place();
        Loaded += (_, _) => Ready = LoadCatalog();
        Closed += (_, _) => { lifetime.Cancel(); previewLoad?.Cancel(); };
    }

    internal static string KindName(GameplayAssetKind kind) => kind switch
    { GameplayAssetKind.Monster => L.T("Monsters"), GameplayAssetKind.Npc => L.T("NPCs"), GameplayAssetKind.Superunique => L.T("Superuniques"), GameplayAssetKind.Object => L.T("Objects"), _ => L.T("Spawn markers") };

    private async Task LoadCatalog()
    {
        if (catalogBusy) return;
        catalogBusy = true; refresh.IsEnabled = false; catalog = null; Filter(); status.Text = L.T("Loading saved gameplay tables…");
        try
        {
            var result = await Task.Run(() => GameplayCatalog.Load(assets, workspaceRoot, act), lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested(); catalog = result;
            status.Text = string.Join("\n", new[] { placementWarning }.OfType<string>().Concat(result.Sources.Where(t => t.Warning is not null).Select(t => t.Name + ".txt: " + t.Warning)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested) status.Text = ex.Message; }
        finally { catalogBusy = false; refresh.IsEnabled = true; if (!lifetime.IsCancellationRequested) Filter(); }
    }

    private void Filter()
    {
        var filtered = catalog?.Assets.Where(a => a.Matches(search.Text) && (available.IsChecked != true || a.CanPlace)
            && (category.SelectedIndex <= 0 || (int)a.Kind == category.SelectedIndex - 1)).Select(a => new GameplayBrowserItem(a)).ToArray() ?? [];
        list.ItemsSource = filtered; count.Text = L.T("{0} / {1} entries", filtered.Length, catalog?.Assets.Count ?? 0);
        // An empty filter must clear selection, the previous preview and placement capability.
        if (list.SelectedItem is null) PreviewReady = SelectEntry();
    }

    private async Task SelectEntry()
    {
        previewLoad?.Cancel(); preview = null; add.IsEnabled = false; Preview.SetScene(new([], [], 0));
        flags.Text = "0"; details.Text = ""; previewNote.Text = L.T("Select a gameplay entry to inspect its mapping and preview.");
        if (Selection is not { } entry) return;
        var matched = templates.Where(p => p.Type == entry.Type && p.Id == entry.Id).Select(p => p.Flags).Distinct().ToArray();
        flags.Text = matched.Length == 1 ? $"0x{matched[0]:X}" : "0";
        details.Text = entry.DisplayName + "\n" + L.T("Act {0} · {1} · Type {2} · DS1 ID {3}", act, KindName(entry.Kind), entry.Type, entry.Id?.ToString() ?? L.T("Not mapped"))
            + "\n" + (entry.UnavailableReason ?? L.T("Valid preset mapping for this act.")) + "\n"
            + (matched.Length == 1 ? L.T("Flags copied from the matching scene/template placement.") : L.T("Flags default to 0; no unique matching template value."))
            + "\n\n" + L.T("Saved data references") + "\n" + string.Join("\n", entry.References.Select(r => $"{r.SourcePath} · {L.T("Line {0}", r.Line)}"));
        add.IsEnabled = entry.CanPlace && !catalogBusy && placementWarning is null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); previewLoad = cts;
        previewNote.Text = L.T("Loading reference preview…");
        try
        {
            var result = await Task.Run(() => GameplayPreviewLoader.Load(entry, assets, workspaceRoot, cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested(); if (previewLoad != cts || Selection != entry) return;
            preview = result; Preview.SetScene(new([], [], 0)); Preview.AddAnimated(result.Visual.Geometry); previewNote.Text = result.Note;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (previewLoad == cts && !lifetime.IsCancellationRequested) previewNote.Text = ex.Message; }
        finally { if (previewLoad == cts) previewLoad = null; }
    }

    private void Place()
    {
        if (!add.IsEnabled || Selection is not { } entry) return;
        try
        {
            if (!int.TryParse(x.Text, out int px) || !int.TryParse(y.Text, out int py)) throw new InvalidOperationException(L.T("Enter whole subtile coordinates."));
            string raw = flags.Text.Trim(); bool hex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (!uint.TryParse(hex ? raw[2..] : raw, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out uint bits))
                throw new InvalidOperationException(L.T("Enter flags as an unsigned integer or 0x hexadecimal value."));
            var current = GameplayCatalog.Load(assets, workspaceRoot, act).Validate(entry);
            place(current, px, py, bits, preview?.Visual);
            status.Text = L.T("Placed {0} at ({1}, {2}). One undo restores the scene.", entry.DisplayName, px, py);
        }
        catch (Exception ex) { status.Text = ex.Message; }
    }

    internal void SearchFor(string text, bool placeableOnly = true) { available.IsChecked = placeableOnly; search.Text = text; Filter(); }
    internal bool SelectKey(string key, GameplayAssetKind kind) { list.SelectedItem = list.Items.OfType<GameplayBrowserItem>().FirstOrDefault(i => i.Asset.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && i.Asset.Kind == kind); return Selection is not null; }
    internal void SetCoordinates(int px, int py) { x.Text = px.ToString(); y.Text = py.ToString(); }
    internal void SetFlags(string value) => flags.Text = value;
    internal void FilterCategory(GameplayAssetKind? kind) => category.SelectedIndex = kind is { } value ? (int)value + 1 : 0;
    internal Task RefreshCatalog() => Ready = LoadCatalog();
    internal void PlaceSelected() => add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
