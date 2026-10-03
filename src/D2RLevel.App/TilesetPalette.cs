using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using D2RLevel.Core;

namespace D2RLevel.App;

internal sealed record PaletteItem(ModelEntry Model, ModelKind Kind, bool InScene)
{
    public string Title => Model.Name;
    public string Subtitle => TilesetPalette.KindName(Kind) + " · " + Folder + (InScene ? " · " + L.T("used in this scene") : "");
    private string Folder => Path.GetDirectoryName(Model.Path.Replace("data/hd/env/model/", "", StringComparison.OrdinalIgnoreCase))!.Replace('\\', '/');
}

/// <summary>
/// The HD models that belong to a level's tileset, ready to paint into the scene. Choosing a model previews it; Paint arms the
/// viewport brush with it, snapped to the DS1 grid and turned in quarter turns. Painted pieces can also claim owned DS1
/// collision under them, so outlines drawn from walls block movement.
/// </summary>
internal sealed class TilesetPalette : Window
{
    private readonly AssetResolver assets;
    private readonly string? workspaceRoot;
    private readonly string[] folders;
    private readonly Func<IReadOnlyCollection<string>> sceneModels;
    private readonly Func<double> unitsPerTile;
    private readonly TextBox search = new() { ToolTip = L.T("Search model names or folders") };
    private readonly ComboBox kind = new ComboBox { Margin = new(3) }.WithReadableItems();
    private readonly CheckBox includeScene = new() { Content = L.T("Include models already in this scene"), IsChecked = true, Foreground = Brushes.White, Margin = new(6, 8, 6, 8),
        ToolTip = L.T("Add pieces the template or your edits already use, even from other folders.") };
    private readonly ListBox list = new() { Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock count = new() { Margin = new(6), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap, Margin = new(8), Foreground = Brushes.LightSteelBlue };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(6), Foreground = Brushes.LightGoldenrodYellow };
    private readonly ComboBox snap = new ComboBox { MinWidth = 120, ToolTip = L.T("Grid that painted pieces snap to. Rows step in whole grid units.") }.WithReadableItems();
    private readonly TextBlock rotation = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 6, 0), MinWidth = 36 };
    private readonly CheckBox block = new() { Content = L.T("Block movement under painted pieces"), Foreground = Brushes.White, Margin = new(6, 8, 6, 4),
        ToolTip = L.T("Give each painted piece an owned DS1 footprint over its bounds, so players and monsters cannot walk through it. Undo removes both.") };
    private readonly Button paint = new() { Content = L.T("Paint in viewport"), IsEnabled = false, Padding = new(14, 5, 14, 5), Margin = new(3) };
    private bool painting;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? previewLoad;
    private ModelEntry[] catalog = [];
    private Quaterniond orientation = new(0, 0, 0, 1);
    internal SceneViewport Preview { get; } = new();
    internal SceneItem? PreviewItem { get; private set; }
    internal string? ModelPath { get; private set; }
    internal int ModelCount => catalog.Length;
    internal bool Painting => painting;
    internal bool BlockMovement => block.IsEnabled && block.IsChecked == true;
    public ModelBrush? Brush { get; private set; }
    public event Action<ModelBrush?>? BrushChanged;
    public Task Ready { get; private set; } = Task.CompletedTask;
    public Task PreviewReady { get; private set; } = Task.CompletedTask;

    public TilesetPalette(AssetResolver assets, string? workspaceRoot, string tilesetName, string[] folders, Func<IReadOnlyCollection<string>> sceneModels,
        Func<double> unitsPerTile, string? blockWarning)
    {
        this.assets = assets; this.workspaceRoot = workspaceRoot; this.folders = folders; this.sceneModels = sceneModels; this.unitsPerTile = unitsPerTile;
        Title = L.T("Tileset palette · {0}", tilesetName); Width = 760; Height = 820; MinWidth = 620; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.Manual; ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(20, 27, 35)); Foreground = Brushes.White;
        var root = new DockPanel { Margin = new(12) }; Content = root;
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = L.T("TILESET PALETTE · {0}", tilesetName.ToUpperInvariant()), FontSize = 18, Margin = new(6), TextWrapping = TextWrapping.Wrap });
        heading.Children.Add(new TextBlock { Text = folders.Length == 0 ? L.T("No tileset folders are known for this scene; showing models it already uses.")
            : L.T("HD models from {0}", string.Join(", ", folders.Select(f => "hd/env/model/" + f))), Margin = new(6, 0, 6, 8), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSteelBlue });

        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var brushRow = new WrapPanel { Margin = new(0, 6, 0, 0) }; footer.Children.Add(brushRow);
        brushRow.Children.Add(new TextBlock { Text = L.T("Snap"), VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 4, 0) }); brushRow.Children.Add(snap);
        var left = new Button { Content = "⟲ 90°", ToolTip = L.T("Turn the piece a quarter turn counter-clockwise.") }; var right = new Button { Content = "⟳ 90°", ToolTip = L.T("Turn the piece a quarter turn clockwise. R does the same in the viewport.") };
        brushRow.Children.Add(new TextBlock { Text = L.T("Turn"), VerticalAlignment = VerticalAlignment.Center, Margin = new(12, 0, 4, 0) });
        brushRow.Children.Add(left); brushRow.Children.Add(rotation); brushRow.Children.Add(right);
        footer.Children.Add(block);
        if (blockWarning is not null) { block.IsEnabled = false; block.ToolTip = blockWarning; }
        var actions = new DockPanel { Margin = new(0, 6, 0, 0) }; footer.Children.Add(actions);
        var close = new Button { Content = L.T("Close"), IsCancel = true }; DockPanel.SetDock(close, Dock.Right); actions.Children.Add(close);
        actions.Children.Add(paint);
        footer.Children.Add(status);
        footer.Children.Add(new TextBlock { Text = L.T("Painted pieces are HD scenery. Without Block movement they do not stop players; DT1 walls and exits are separate. Check new layouts in game."),
            TextWrapping = TextWrapping.Wrap, Margin = new(6, 4, 6, 0), Foreground = Brushes.LightSteelBlue });

        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new(300) }); grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); root.Children.Add(grid);
        var listPanel = new DockPanel { Margin = new(0, 0, 10, 0) }; grid.Children.Add(listPanel);
        var filters = new StackPanel(); DockPanel.SetDock(filters, Dock.Top); listPanel.Children.Add(filters);
        filters.Children.Add(new TextBlock { Text = L.T("Search model names or folders"), Margin = new(6, 0, 6, 4) }); filters.Children.Add(search);
        kind.ItemsSource = new[] { L.T("All kinds") }.Concat(Enum.GetValues<ModelKind>().Select(KindName)).ToArray(); kind.SelectedIndex = 0;
        filters.Children.Add(kind); filters.Children.Add(includeScene);
        DockPanel.SetDock(count, Dock.Bottom); listPanel.Children.Add(count); listPanel.Children.Add(list);
        var item = new FrameworkElementFactory(typeof(StackPanel)); item.SetValue(MarginProperty, new Thickness(6, 4, 6, 4));
        var title = new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty, new Binding(nameof(PaletteItem.Title))); item.AppendChild(title);
        var subtitle = new FrameworkElementFactory(typeof(TextBlock)); subtitle.SetBinding(TextBlock.TextProperty, new Binding(nameof(PaletteItem.Subtitle)));
        subtitle.SetValue(TextBlock.ForegroundProperty, Brushes.LightSteelBlue); subtitle.SetValue(TextBlock.FontSizeProperty, 11d); subtitle.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); item.AppendChild(subtitle);
        list.ItemTemplate = new() { VisualTree = item }; VirtualizingPanel.SetIsVirtualizing(list, true); ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var previewPanel = new DockPanel(); Grid.SetColumn(previewPanel, 1); grid.Children.Add(previewPanel);
        DockPanel.SetDock(details, Dock.Bottom); previewPanel.Children.Add(details); previewPanel.Children.Add(Preview);

        snap.ItemsSource = new[] { L.T("Off"), L.T("Subtile (⅕ tile)"), L.T("Half tile"), L.T("Tile") }; snap.SelectedIndex = 3;
        search.TextChanged += (_, _) => Filter(); kind.SelectionChanged += (_, _) => Filter(); includeScene.Click += (_, _) => Filter();
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is PaletteItem chosen) PreviewReady = LoadPreview(chosen.Model.Path); };
        snap.SelectionChanged += (_, _) => UpdateBrush();
        left.Click += (_, _) => Turn(-90); right.Click += (_, _) => Turn(90);
        paint.Click += (_, _) => { painting = !painting; UpdateBrush(); };
        close.Click += (_, _) => Close();
        Loaded += (_, _) => Ready = LoadCatalog();
        Closed += (_, _) => { lifetime.Cancel(); previewLoad?.Cancel(); Brush = null; BrushChanged?.Invoke(null); };
        ShowRotation();
        status.Text = L.T("Choose a model, then Paint in viewport. Click places one piece; drag lays a straight row along X or Z.");
    }

    internal static string KindName(ModelKind kind) => kind switch
    {
        ModelKind.Walls => L.T("Walls and edges"),
        ModelKind.Floors => L.T("Floors and ground"),
        ModelKind.Passages => L.T("Doors and passages"),
        ModelKind.Pillars => L.T("Pillars and columns"),
        ModelKind.Nature => L.T("Nature"),
        _ => L.T("Props"),
    };

    /// <summary>The brush's grid in HD units: off, a subtile, half a tile or a tile at the scene's calibration.</summary>
    internal double SnapUnits => snap.SelectedIndex switch { 1 => unitsPerTile() / 5, 2 => unitsPerTile() / 2, 3 => unitsPerTile(), _ => 0 };

    private async Task LoadCatalog()
    {
        try
        {
            count.Text = L.T("Indexing tileset models…");
            catalog = await Task.Run(() => TilesetCatalog.Models(assets, folders, workspaceRoot, lifetime.Token));
            if (!lifetime.IsCancellationRequested) Filter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested) details.Text = ex.Message; }
    }

    private void Filter()
    {
        var used = sceneModels().Where(p => p.StartsWith("data/hd/", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var models = includeScene.IsChecked == true ? catalog.Concat(used.Where(p => !catalog.Any(c => c.Path.Equals(p, StringComparison.OrdinalIgnoreCase))).Select(p => new ModelEntry(p))) : catalog;
        var terms = search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var items = models.Select(m => new PaletteItem(m, TilesetCatalog.Kind(m), used.Contains(m.Path)))
            .Where(i => kind.SelectedIndex <= 0 || i.Kind == Enum.GetValues<ModelKind>()[kind.SelectedIndex - 1])
            .Where(i => terms.All(t => i.Model.Path.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(i => i.Kind).ThenBy(i => i.Model.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var selected = (list.SelectedItem as PaletteItem)?.Model.Path;
        list.ItemsSource = items;
        if (items.FirstOrDefault(i => i.Model.Path == selected) is { } keep) list.SelectedItem = keep;
        count.Text = L.T("{0:N0} shown · {1:N0} tileset models", items.Length, catalog.Length);
    }

    /// <summary>Select a model by its logical path and wait for its preview.</summary>
    internal async Task SelectModel(string path)
    {
        await Ready;
        search.Text = ""; kind.SelectedIndex = 0;
        list.SelectedItem = list.Items.OfType<PaletteItem>().FirstOrDefault(i => i.Model.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (list.SelectedItem is not null) { list.ScrollIntoView(list.SelectedItem); await PreviewReady; }
    }

    private async Task LoadPreview(string path)
    {
        previewLoad?.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        previewLoad = cts; PreviewItem = null; ModelPath = null; paint.IsEnabled = false; UpdateBrush();
        Preview.SetScene(new([], [], 0)); details.Text = L.T("Loading {0}", path);
        Func<string, bool, string>? resolve = null;
        if (workspaceRoot is not null && Directory.Exists(Path.Combine(workspaceRoot, "hd")))
        {
            var local = new AssetResolver(workspaceRoot);
            resolve = (asset, model) =>
            {
                string candidate = model ? local.ResolvePreviewModel(asset, 0) : local.Resolve(asset);
                return File.Exists(candidate) ? candidate : model ? assets.ResolvePreviewModel(asset, 0) : assets.Resolve(asset);
            };
        }
        try
        {
            var result = await Task.Run(() => SceneLoader.Load(PresetDocument.ModelPreview(path), assets, new Progress<string>(), cts.Token, true, resolve), cts.Token);
            if (previewLoad != cts) return;
            Preview.SetScene(result);
            if (result.LoadedModels == 1)
            {
                PreviewItem = result.Items.Single(); ModelPath = path; paint.IsEnabled = true;
                var bounds = PreviewItem.Geometry.Bounds;
                details.Text = path + "\n" + L.T("Footprint {0:F1} × {1:F1} HD units · {2:F2} × {3:F2} tiles · height {4:F1}", bounds.SizeX, bounds.SizeZ,
                    bounds.SizeX / unitsPerTile(), bounds.SizeZ / unitsPerTile(), bounds.SizeY);
            }
            else details.Text = path + "\n" + string.Join("\n", result.Diagnostics.Where(d => !d.StartsWith("Preview:")).DefaultIfEmpty(L.T("This model could not be previewed, so it cannot be painted.")));
            UpdateBrush();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (previewLoad == cts && !lifetime.IsCancellationRequested) details.Text = ex.Message; }
        finally { if (previewLoad == cts) previewLoad = null; }
    }

    private void Turn(double degrees)
    {
        var q = new System.Windows.Media.Media3D.Quaternion(new System.Windows.Media.Media3D.Vector3D(0, 1, 0), degrees)
            * new System.Windows.Media.Media3D.Quaternion(orientation.X, orientation.Y, orientation.Z, orientation.W);
        q.Normalize(); orientation = new(q.X, q.Y, q.Z, q.W);
        ShowRotation(); UpdateBrush();
    }

    /// <summary>The viewport turned the brush (R); follow it without re-arming.</summary>
    internal void FollowBrush(ModelBrush brush) { orientation = brush.Orientation; Brush = brush; ShowRotation(); }

    private void ShowRotation()
    {
        double degrees = 2 * Math.Atan2(orientation.Y, orientation.W) * 180 / Math.PI;
        rotation.Text = $"{((Math.Round(degrees) % 360) + 360) % 360:0}°";
    }

    internal void StopPainting() { painting = false; UpdateBrush(); }
    internal void StartPainting() { if (paint.IsEnabled) { painting = true; UpdateBrush(); } }

    private void UpdateBrush()
    {
        Brush = painting && PreviewItem is { } item && ModelPath is { } path ? new ModelBrush(path, item.Geometry, orientation, SnapUnits) : null;
        painting = Brush is not null;
        paint.Content = painting ? L.T("Stop painting") : L.T("Paint in viewport");
        if (painting) paint.Background = new SolidColorBrush(Color.FromRgb(40, 104, 108)); else paint.ClearValue(BackgroundProperty);
        if (Brush is not null) status.Text = L.T("Painting {0}. Click places one piece; drag lays a row. R turns it, Esc stops. Ctrl+Z undoes a row.", Path.GetFileNameWithoutExtension(Brush.ModelPath));
        else if (PreviewItem is not null) status.Text = L.T("Paint in viewport to place this piece.");
        BrushChanged?.Invoke(Brush);
    }

    internal void Report(string message) => status.Text = message;
    internal IReadOnlyList<ModelEntry> Catalog => catalog;
    internal void SetBlockMovement(bool value) => block.IsChecked = value;
    internal string StatusText => status.Text;
}
