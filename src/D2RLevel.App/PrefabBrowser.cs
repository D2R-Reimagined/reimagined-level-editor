using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using D2RLevel.Core;
using Microsoft.Win32;

namespace D2RLevel.App;

internal sealed record PrefabEntry(string Folder, GameplayPrefab? Prefab, string? Warning)
{
    public string Label => Prefab is { } p ? $"{p.Name}\n{p.Models.Length} HD · {p.Units.Length} units · Act {p.Act}" : Path.GetFileName(Folder) + " · " + Warning;
}
internal sealed record PrefabUnitChoice(int Index, string Label);
internal sealed class PrefabBrowser : Window
{
    private readonly string library;
    private readonly AssetResolver assets;
    private readonly PresetDocument document;
    private readonly Ds1CollisionDocument map;
    private readonly PlacementLinks links;
    private readonly PresetEntity[] selection;
    private readonly GameplayCatalog catalog;
    private readonly Func<string, (string Physical, string File, string[] Dependencies)> inspect;
    private readonly Func<string, GameplayPrefab, int, int, double, double, Task> place;
    private readonly Action saveScene;
    private readonly List<PrefabEntry> entries = [];
    private readonly ListBox list = new() { Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox search = new(), x = new() { Width = 55 }, y = new() { Width = 55 }, elevation = new() { Width = 65, Text = "0" }, scale = new() { Width = 65 };
    private readonly TextBlock details = Text(), status = Text(), counts = Text();
    private readonly TextBlock emptyStateMessage = Text();
    private readonly Border emptyState = new() { Background = new SolidColorBrush(Color.FromRgb(24, 35, 46)), Padding = new(24), Margin = new(8) };
    private readonly Button placeButton = new() { Content = L.T("Place prefab"), IsEnabled = false };
    private readonly Button create = new() { Content = L.T("Save selection as prefab…") }, open = new() { Content = L.T("Open prefab folder…") };
    private readonly Button undo = new() { Content = L.T("Undo") }, redo = new() { Content = L.T("Redo") };
    private readonly PrefabFootprint footprint = new();
    private bool busy;
    private bool previewValid;
    private int revision;
    private readonly CancellationTokenSource lifetime = new();
    internal SceneViewport Preview { get; } = new();
    internal Task PreviewReady { get; private set; } = Task.CompletedTask;
    internal string StatusText => status.Text;
    internal string EmptyStateText => emptyStateMessage.Text;
    internal bool CanPlace => placeButton.IsEnabled;
    private PrefabEntry? Selected => list.SelectedItem as PrefabEntry;
    private static TextBlock Text() => new() { TextWrapping = TextWrapping.Wrap, Margin = new(6), Foreground = Brushes.LightSteelBlue };
    public PrefabBrowser(string library, AssetResolver assets, PresetDocument document, Ds1CollisionDocument map, PlacementLinks links,
        PresetEntity[] selection, GameplayCatalog catalog, Func<string, (string Physical, string File, string[] Dependencies)> inspect,
        Func<string, GameplayPrefab, int, int, double, double, Task> place, Action saveScene)
    {
        this.library = library; this.assets = assets; this.document = document; this.map = map; this.links = links; this.selection = selection; this.catalog = catalog; this.inspect = inspect; this.place = place; this.saveScene = saveScene;
        Title = L.T("Gameplay prefabs"); Width = 1220; Height = 850; MinWidth = 1020; MinHeight = 720; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(19, 27, 36)); Foreground = Brushes.White;
        var root = new DockPanel { Margin = new(12) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = L.T("GAMEPLAY PREFABS"), FontSize = 22, Margin = new(6) });
        header.Children.Add(new TextBlock { Text = L.T("Reuse visuals, gameplay placements, patrols and owned collision as one assembled layout."), Margin = new(6, 0, 6, 8), TextWrapping = TextWrapping.Wrap });
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); status.Foreground = Brushes.LightGoldenrodYellow; footer.Children.Add(status);
        var coordinates = new WrapPanel(); footer.Children.Add(coordinates);
        void Field(string label, TextBox field) { coordinates.Children.Add(new TextBlock { Text = label, Margin = new(6), VerticalAlignment = VerticalAlignment.Center }); coordinates.Children.Add(field); }
        x.Text = (map.Width / 2).ToString(); y.Text = (map.Height / 2).ToString(); scale.Text = links.Calibration.UnitsPerTile.ToString(CultureInfo.InvariantCulture);
        Field(L.T("Anchor tile X / Y"), x); coordinates.Children.Add(y); Field(L.T("HD height"), elevation); Field(L.T("HD units / tile"), scale); coordinates.Children.Add(placeButton);
        var actions = new WrapPanel(); footer.Children.Add(actions); actions.Children.Add(undo); actions.Children.Add(redo); var save = new Button { Content = L.T("Save Scene") }; actions.Children.Add(save); var close = new Button { Content = L.T("Close") }; actions.Children.Add(close);
        footer.Children.Add(new TextBlock { Text = L.T("Placement uses shared undo. Save Scene keeps the assembled layout. Missing dependencies are copied into the workspace and remain after undo; existing assets are never overwritten."), TextWrapping = TextWrapping.Wrap, Margin = new(6) });
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new(320) }); grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); root.Children.Add(grid);
        var left = new DockPanel { Margin = new(4, 4, 12, 4) }; grid.Children.Add(left);
        var tools = new StackPanel(); DockPanel.SetDock(tools, Dock.Top); left.Children.Add(tools); tools.Children.Add(create); tools.Children.Add(open);
        tools.Children.Add(new TextBlock { Text = L.T("HOW GAMEPLAY PREFABS WORK"), FontWeight = FontWeights.SemiBold, Margin = new(6, 16, 6, 4) });
        tools.Children.Add(new TextBlock { Text = L.T("1. Select HD models in the scene before opening this window.\n2. Save the selection; the capture dialog can also include gameplay units. Linked units and owned collision come with their models.\n3. Choose a saved prefab, inspect its preview and footprint, then set an anchor tile and place it."), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSteelBlue, Margin = new(6, 0, 6, 4) });
        tools.Children.Add(new TextBlock { Text = L.T("Search prefab library"), Margin = new(6, 12, 6, 4) }); tools.Children.Add(search); tools.Children.Add(counts);
        var libraryText = Text(); libraryText.Text = L.T("WORKSPACE LIBRARY") + "\n" + library; DockPanel.SetDock(libraryText, Dock.Bottom); left.Children.Add(libraryText); left.Children.Add(list);
        var template = new FrameworkElementFactory(typeof(TextBlock)); template.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PrefabEntry.Label))); template.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); template.SetValue(MarginProperty, new Thickness(8)); list.ItemTemplate = new() { VisualTree = template };
        var right = new Grid(); Grid.SetColumn(right, 1); grid.Children.Add(right); right.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); right.RowDefinitions.Add(new() { Height = new(200) });
        var previews = new Grid(); previews.ColumnDefinitions.Add(new() { Width = new(3, GridUnitType.Star) }); previews.ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star) }); right.Children.Add(previews);
        previews.Children.Add(Preview); Grid.SetColumn(footprint, 1); previews.Children.Add(footprint);
        var detailScroll = new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(29, 39, 50)), Margin = new(0, 8, 0, 0) }; Grid.SetRow(detailScroll, 1); right.Children.Add(detailScroll);
        emptyStateMessage.FontSize = 17; emptyStateMessage.TextAlignment = TextAlignment.Center; emptyStateMessage.VerticalAlignment = VerticalAlignment.Center;
        emptyState.Child = emptyStateMessage; Grid.SetRowSpan(emptyState, 2); right.Children.Add(emptyState);
        search.TextChanged += (_, _) => Filter(); list.SelectionChanged += (_, _) => PreviewReady = SelectPrefab();
        create.Click += async (_, _) => await CaptureDialog(); open.Click += (_, _) => { var dialog = new OpenFolderDialog { Title = L.T("Open prefab folder") }; if (dialog.ShowDialog(this) == true) LoadFolder(dialog.FolderName); };
        placeButton.Click += async (_, _) => await PlaceSelected(); undo.Click += (_, _) => document.History.Undo(); redo.Click += (_, _) => document.History.Redo();
        save.Click += (_, _) => { if (busy) return; try { saveScene(); status.Text = L.T("Scene saved."); } catch (Exception ex) { status.Text = ex.Message; } }; close.Click += (_, _) => Close();
        document.History.Changed += HistoryChanged; Closing += (_, e) => { if (busy) { e.Cancel = true; status.Text = L.T("Wait for the current prefab operation to finish."); } };
        Closed += (_, _) => { lifetime.Cancel(); document.History.Changed -= HistoryChanged; };
        if (Directory.Exists(library)) foreach (string folder in Directory.EnumerateDirectories(library)) if (!Path.GetFileName(folder).StartsWith('.')) AddEntry(folder);
        Filter(); HistoryChanged(null); status.Text = L.T("The prefab library contains assemblies saved in this workspace or opened from a prefab folder.");
    }
    private void HistoryChanged(object? _) { undo.IsEnabled = !busy && document.History.CanUndo; redo.IsEnabled = !busy && document.History.CanRedo; }
    private void SetBusy(bool value) { busy = value; create.IsEnabled = open.IsEnabled = list.IsEnabled = search.IsEnabled = !value; placeButton.IsEnabled = !value && previewValid && Selected?.Prefab is not null; HistoryChanged(null); }
    private void AddEntry(string folder)
    {
        entries.RemoveAll(e => e.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));
        try { entries.Add(new(folder, GameplayPrefab.Load(folder), null)); } catch (Exception ex) { entries.Add(new(folder, null, ex.Message)); }
    }
    internal void LoadFolder(string folder) { AddEntry(folder); search.Text = ""; Filter(); list.SelectedItem = entries.Single(e => e.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)); }
    internal void SearchFor(string value) => search.Text = value;
    internal void Coordinates(int px, int py, double height = 0) { x.Text = px.ToString(); y.Text = py.ToString(); elevation.Text = height.ToString(CultureInfo.InvariantCulture); }
    private void Filter()
    {
        list.ItemsSource = entries.Where(e => e.Label.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Label).ToArray();
        counts.Text = L.T("{0} prefabs", list.Items.Count);
        UpdateEmptyState();
    }
    private void UpdateEmptyState()
    {
        emptyState.Visibility = Selected is null ? Visibility.Visible : Visibility.Collapsed;
        emptyStateMessage.Text = entries.Count == 0
            ? L.T("No gameplay prefabs saved yet.\n\nThis is your workspace library, not a built-in catalog. Select one or more HD models in the scene, reopen this window, and choose Save selection as prefab. Add any extra gameplay units in the capture dialog.\n\nYour saved assembly will appear in the list with a 3D preview and a gameplay footprint.")
            : list.Items.Count == 0
                ? L.T("No prefabs match this search. Clear the search to see your saved assemblies.")
                : L.T("Choose a prefab from the library to see its visuals, gameplay units, patrols and collision footprint.");
    }
    private async Task SelectPrefab()
    {
        UpdateEmptyState();
        int current = ++revision; previewValid = false; placeButton.IsEnabled = false; Preview.SetScene(new([], [], 0)); footprint.Set(null); details.Text = "";
        if (Selected is not { Prefab: { } prefab } entry) { status.Text = Selected?.Warning ?? L.T("Choose a prefab to inspect its contents."); return; }
        status.Text = L.T("Checking prefab dependencies…");
        try
        {
            var scene = await Task.Run(() =>
            {
                prefab.VerifyAssets(entry.Folder); var lookup = prefab.Assets.ToDictionary(a => a.Logical, StringComparer.OrdinalIgnoreCase);
                var hdScene = SceneLoader.Load(prefab.PreviewDocument(), assets, new Progress<string>(), lifetime.Token, true,
                    (logical, _) => lookup.TryGetValue(logical, out var asset) ? SceneWorkspace.Inside(entry.Folder, Path.Combine(entry.Folder, "assets", asset.File)) : throw new FileNotFoundException("Undeclared prefab dependency: " + logical));
                var items = hdScene.Items.ToList(); var visuals = new Dictionary<(GameplayAssetKind, string), GameplayPreview>();
                string dataRoot = PresetPairing.Split(document.SourcePath, "hd/env/preset")!.Value.DataRoot;
                var currentCatalog = GameplayCatalog.Load(assets, dataRoot, prefab.Act);
                for (int i = 0; i < prefab.Units.Length; i++)
                {
                    lifetime.Token.ThrowIfCancellationRequested(); var unit = prefab.Units[i];
                    var matches = currentCatalog.Assets.Where(a => a.Kind == unit.Kind && a.Key.Equals(unit.Key, StringComparison.OrdinalIgnoreCase) && a.CanPlace).ToArray();
                    if (matches.Length != 1) continue;
                    var key = (unit.Kind, unit.Key);
                    if (!visuals.TryGetValue(key, out var visual)) { visual = GameplayPreviewLoader.Load(matches[0], assets, dataRoot, lifetime.Token); visuals[key] = visual; }
                    var proxy = PresetEntity.GameplayPreview(i, unit.Key, new(new(unit.X * prefab.UnitsPerTile / 5, 0, unit.Y * prefab.UnitsPerTile / 5), new(0, 0, 0, 1), new(1, 1, 1)));
                    items.Add(new(proxy, visual.Visual.Geometry, visual.Visual.Missing));
                }
                return hdScene with { Items = items, LoadedModels = items.Count(i => !i.IsPlaceholder) };
            });
            if (current != revision || lifetime.IsCancellationRequested) return;
            Preview.SetScene(scene); footprint.Set(prefab);
            details.Text = prefab.Name + "\n" + L.T("Act {0} · {1} HD models · {2} gameplay units · {3} owned collision tiles", prefab.Act, prefab.Models.Length, prefab.Units.Length, prefab.Models.SelectMany(m => m.Collision).Distinct().Count()) + "\n" + L.T("Scale: {0} HD units / tile · {1} bundled dependencies", prefab.UnitsPerTile, prefab.Assets.Length) + "\n" + L.T("Gameplay-unit appearances use the target game assets in a static reference pose.") + "\n\n" + string.Join("\n", prefab.Units.Select(u => $"{u.Kind}: {u.Key} · ({u.X}, {u.Y}) · {u.Patrol.Length} patrol points")) + "\n\n" + string.Join("\n", prefab.Assets.Select(a => a.Logical));
            status.Text = scene.Items.Any(i => i.IsPlaceholder) ? L.T("Some HD meshes could not be previewed. Review dependencies before placement.") : L.T("Prefab loaded. Review the formation, choose an anchor and confirm the grid scale before placing.");
            previewValid = true; placeButton.IsEnabled = !busy;
        }
        catch (Exception ex) { if (current == revision) status.Text = ex.Message; }
    }
    internal async Task PlaceSelected()
    {
        if (!placeButton.IsEnabled || Selected is not { Prefab: { } prefab } entry) return;
        try
        {
            if (!int.TryParse(x.Text, out int px) || !int.TryParse(y.Text, out int py) || !double.TryParse(elevation.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double height) || !double.TryParse(scale.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double units)) throw new InvalidDataException(L.T("Enter whole anchor tiles and numeric height and grid scale."));
            SetBusy(true); await place(entry.Folder, prefab, px, py, height, units); status.Text = L.T("Prefab placed as one scene edit. Save Scene to keep it, or Undo to remove the assembly.");
        }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { SetBusy(false); }
    }
    internal async Task<string> SavePrefab(string name, int[] units, double unitsPerTile)
    {
        if (busy) throw new InvalidOperationException("A prefab operation is already running.");
        var prefab = links.CapturePrefab(name, selection.Where(e => e.GameplayUnitIndex is null), units.Concat(selection.Where(e => e.GameplayUnitIndex is not null).Select(e => e.GameplayUnitIndex!.Value)), unitsPerTile, GameplayCatalog.Load(assets, PresetPairing.Split(document.SourcePath, "hd/env/preset")!.Value.DataRoot, map.Act));
        string slug = new(name.Trim().Take(50).Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '.' ? '_' : c).ToArray());
        Directory.CreateDirectory(library); string folder = SceneWorkspace.Inside(library, Path.Combine(library, slug + "-" + Guid.NewGuid().ToString("N")[..8]));
        try { SetBusy(true); status.Text = L.T("Saving prefab and referenced assets…"); await Task.Run(() => prefab.Save(folder, inspect)); LoadFolder(folder); await PreviewReady; status.Text = L.T("Prefab saved in the workspace library. The source scene is unchanged."); return folder; }
        finally { SetBusy(false); }
    }
    internal async Task CaptureDialog()
    {
        var content = new DockPanel { Margin = new(16) }; var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); content.Children.Add(heading);
        var name = new TextBox { Text = L.T("New gameplay prefab") }; var unitsPerTile = new TextBox { Text = scale.Text };
        heading.Children.Add(new TextBlock { Text = L.T("Prefab name") }); heading.Children.Add(name);
        heading.Children.Add(new TextBlock { Text = L.T("HD units per tile (confirm the scene calibration)") }); heading.Children.Add(unitsPerTile);
        heading.Children.Add(new TextBlock { Text = L.T("Selected HD models: {0}. Linked units, visuals and collision are included automatically. Select additional units below; proximity does not add them.", selection.Count(e => e.GameplayUnitIndex is null)), TextWrapping = TextWrapping.Wrap, Margin = new(3, 10, 3, 10) });
        var choices = map.Units.Select(u => { var recipe = catalog.Assets.FirstOrDefault(a => a.Type == u.Type && a.Id == u.Id && a.CanPlace); return new PrefabUnitChoice(u.Index, $"{u.Index}: {recipe?.DisplayName ?? (u.Type + "/" + u.Id)} · ({u.X}, {u.Y})"); }).ToArray();
        var units = new ListBox { ItemsSource = choices, DisplayMemberPath = nameof(PrefabUnitChoice.Label), SelectionMode = SelectionMode.Multiple, Background = Background, Foreground = Brushes.White };
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); content.Children.Add(footer); var note = Text(); footer.Children.Add(note); var buttons = new WrapPanel(); footer.Children.Add(buttons); var accept = new Button { Content = L.T("Save prefab") }; var cancel = new Button { Content = L.T("Cancel"), IsCancel = true }; buttons.Children.Add(accept); buttons.Children.Add(cancel); content.Children.Add(units);
        var dialog = new Window { Owner = this, Title = L.T("Save gameplay prefab"), Width = 640, Height = 620, MinWidth = 520, MinHeight = 500, Content = content, Background = Background, Foreground = Foreground, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        string? chosenName = null; int[] chosenUnits = []; double chosenScale = 0;
        accept.Click += (_, _) => { if (string.IsNullOrWhiteSpace(name.Text) || !double.TryParse(unitsPerTile.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out chosenScale)) { note.Text = L.T("Enter a prefab name and numeric grid scale."); return; } chosenName = name.Text; chosenUnits = units.SelectedItems.Cast<PrefabUnitChoice>().Select(u => u.Index).ToArray(); dialog.DialogResult = true; };
        if (dialog.ShowDialog() == true) try { await SavePrefab(chosenName!, chosenUnits, chosenScale); } catch (Exception ex) { status.Text = ex.Message; }
    }
}

internal sealed class PrefabFootprint : FrameworkElement
{
    private GameplayPrefab? prefab;
    public void Set(GameplayPrefab? value) { prefab = value; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(14, 21, 29)), null, new Rect(RenderSize));
        void Label(string value, double x, double y, Brush brush) => dc.DrawText(new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
        Label(L.T("GAMEPLAY FOOTPRINT"), 10, 12, Brushes.White);
        if (prefab is null) return;
        var cells = prefab.Models.SelectMany(m => m.Collision).Distinct().ToArray();
        var points = cells.Select(t => new Point(t.X + .5, t.Y + .5)).Concat(prefab.Units.Select(u => new Point(u.X / 5d, u.Y / 5d))).Concat(prefab.Units.SelectMany(u => u.Patrol).Select(p => new Point(p.X / 5d, p.Y / 5d))).Append(new Point()).ToArray();
        double minX = points.Min(p => p.X) - 1, minY = points.Min(p => p.Y) - 1, width = Math.Max(4, points.Max(p => p.X) - minX + 1), height = Math.Max(4, points.Max(p => p.Y) - minY + 1);
        double s = Math.Max(1, Math.Min((ActualWidth - 30) / width, (ActualHeight - 100) / height)); Point At(double x, double y) => new(15 + (x - minX) * s, 45 + (y - minY) * s);
        foreach (var tile in cells) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(160, 223, 126, 68)), new Pen(Brushes.SandyBrown, 1), new Rect(At(tile.X, tile.Y), new Size(s, s)));
        foreach (var u in prefab.Units)
        {
            var point = At(u.X / 5d, u.Y / 5d); var previous = point;
            foreach (var p in u.Patrol) { var next = At(p.X / 5d, p.Y / 5d); dc.DrawLine(new Pen(Brushes.SlateGray, 1), previous, next); previous = next; }
            dc.DrawEllipse(u.Type == 1 ? Brushes.Turquoise : Brushes.Gold, new Pen(Brushes.Black, 1), point, 6, 6);
        }
        var origin = At(0, 0); dc.DrawLine(new Pen(Brushes.White, 1), new(origin.X - 5, origin.Y), new(origin.X + 5, origin.Y)); dc.DrawLine(new Pen(Brushes.White, 1), new(origin.X, origin.Y - 5), new(origin.X, origin.Y + 5));
        Label(L.T("Teal: monsters · gold: objects"), 10, ActualHeight - 46, Brushes.LightSteelBlue); Label(L.T("Orange: collision · +: anchor"), 10, ActualHeight - 26, Brushes.LightSteelBlue);
    }
}
