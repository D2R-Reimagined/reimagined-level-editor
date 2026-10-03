using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using D2RLevel.Core;

namespace D2RLevel.App;

internal sealed record TemplateItem(TemplateChoice Template)
{
    public string Title => Template.Name;
    public string Subtitle => L.T("{0} × {1} tiles", Template.Width, Template.Height) + " · "
        + (Template.IsLevel ? L.T("level preset: {0}", Template.LevelName ?? Template.LevelId.ToString()) : L.T("generator room")) + " · " + Template.Ds1Relative;
}

/// <summary>
/// The questions a new level starts from: which act, which tileset (a LvlTypes row), and which existing map of that tileset gives
/// the starting size and HD terrain. The chosen tileset is the one the level loads and keeps, and the palette offers its models.
/// </summary>
internal sealed class NewLevelWizard : Window
{
    private readonly AssetResolver assets;
    private readonly string? workspaceRoot;
    private readonly GameDataTables tables;
    private readonly ComboBox act = new ComboBox { MinWidth = 160 }.WithReadableItems();
    private readonly ListBox types = new() { Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new(0) };
    private readonly ListBox templates = new() { Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox search = new() { ToolTip = L.T("Filter layouts by name or map path") };
    private readonly TextBlock typeInfo = Note(), templateInfo = Note(), status = Note();
    private readonly Button next = new() { Content = L.T("Next…"), IsDefault = true, IsEnabled = false, Padding = new(16, 5, 16, 5) };
    private LevelTypeChoice[] allTypes = [];
    private TemplateItem[] typeTemplates = [];
    private CancellationTokenSource? loadingTemplates;
    public LevelTypeChoice? LevelType => types.SelectedItem as LevelTypeChoice;
    public TemplateChoice? Template => (templates.SelectedItem as TemplateItem)?.Template;
    public bool BrowseRequested { get; private set; }
    public Task TemplatesReady { get; private set; } = Task.CompletedTask;
    internal int TemplateCount => typeTemplates.Length;
    private static TextBlock Note() => new() { TextWrapping = TextWrapping.Wrap, Margin = new(6), Foreground = Brushes.LightSteelBlue };

    public NewLevelWizard(AssetResolver assets, string? workspaceRoot, int? preferredAct = null, int? preferredType = null)
    {
        this.assets = assets; this.workspaceRoot = workspaceRoot; tables = new GameDataTables(assets, workspaceRoot);
        Title = L.T("New level"); Width = 1000; Height = 720; MinWidth = 820; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(20, 27, 35)); Foreground = Brushes.White;
        var root = new DockPanel { Margin = new(14) }; Content = root;
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = L.T("CREATE A LEVEL"), FontSize = 22, Margin = new(6) });
        heading.Children.Add(new TextBlock { Text = L.T("Pick the tileset the level is built from, then an existing map of that tileset to start from. The tileset decides the DT1 ground and walls the level loads, and which HD models the tileset palette offers."),
            TextWrapping = TextWrapping.Wrap, Margin = new(6, 0, 6, 10), Foreground = Brushes.LightSteelBlue });

        var footer = new DockPanel { Margin = new(0, 10, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var browse = new Button { Content = L.T("Browse for a preset file instead…"), ToolTip = L.T("Start from any HD preset with a paired DS1, using the tileset its level tables give it.") };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        buttons.Children.Add(cancel); buttons.Children.Add(next);
        DockPanel.SetDock(browse, Dock.Left); footer.Children.Add(browse);
        footer.Children.Add(status);

        var grid = new Grid(); root.Children.Add(grid);
        grid.ColumnDefinitions.Add(new() { Width = new(320) }); grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var left = new DockPanel { Margin = new(0, 0, 12, 0) }; grid.Children.Add(Panel(left));
        var leftTop = new StackPanel(); DockPanel.SetDock(leftTop, Dock.Top); left.Children.Add(leftTop);
        leftTop.Children.Add(Step(L.T("1 · Act")));
        act.ItemsSource = Enumerable.Range(1, 5).Select(a => L.T("Act {0}", a)).ToArray(); leftTop.Children.Add(act);
        leftTop.Children.Add(Step(L.T("2 · Tileset")));
        DockPanel.SetDock(typeInfo, Dock.Bottom); left.Children.Add(typeInfo);
        types.DisplayMemberPath = nameof(LevelTypeChoice.Name); left.Children.Add(types);

        var right = new DockPanel(); Grid.SetColumn(right, 1); var rightPanel = Panel(right); Grid.SetColumn(rightPanel, 1); grid.Children.Add(rightPanel);
        var rightTop = new StackPanel(); DockPanel.SetDock(rightTop, Dock.Top); right.Children.Add(rightTop);
        rightTop.Children.Add(Step(L.T("3 · Starting layout")));
        rightTop.Children.Add(new TextBlock { Text = L.T("Its size and HD terrain become the new level's scaffold. Scenery is cleared unless you keep it on the next page; DS1 ground, walls, units and exits start empty."),
            TextWrapping = TextWrapping.Wrap, Margin = new(6, 0, 6, 6), Foreground = Brushes.LightSteelBlue });
        rightTop.Children.Add(search);
        DockPanel.SetDock(templateInfo, Dock.Bottom); right.Children.Add(templateInfo);
        var item = new FrameworkElementFactory(typeof(StackPanel)); item.SetValue(MarginProperty, new Thickness(6, 4, 6, 4));
        var title = new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty, new Binding(nameof(TemplateItem.Title))); item.AppendChild(title);
        var subtitle = new FrameworkElementFactory(typeof(TextBlock)); subtitle.SetBinding(TextBlock.TextProperty, new Binding(nameof(TemplateItem.Subtitle)));
        subtitle.SetValue(TextBlock.ForegroundProperty, Brushes.LightSteelBlue); subtitle.SetValue(TextBlock.FontSizeProperty, 11d); subtitle.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); item.AppendChild(subtitle);
        templates.ItemTemplate = new() { VisualTree = item }; VirtualizingPanel.SetIsVirtualizing(templates, true); ScrollViewer.SetHorizontalScrollBarVisibility(templates, ScrollBarVisibility.Disabled);
        right.Children.Add(templates);

        act.SelectionChanged += (_, _) => FilterTypes();
        types.SelectionChanged += (_, _) => TemplatesReady = LoadTemplates();
        search.TextChanged += (_, _) => FilterTemplates();
        templates.SelectionChanged += (_, _) => ShowTemplate();
        templates.MouseDoubleClick += (_, _) => { if (next.IsEnabled) DialogResult = true; };
        next.Click += (_, _) => { if (Template is not null) DialogResult = true; };
        browse.Click += (_, _) => { BrowseRequested = true; DialogResult = true; };
        Closed += (_, _) => loadingTemplates?.Cancel();

        allTypes = TilesetCatalog.LevelTypes(tables);
        var levelTypes = tables.Read("lvltypes");
        if (allTypes.Length == 0) status.Text = levelTypes.Warning ?? L.T("No tilesets were found in lvltypes.txt.");
        var preferred = allTypes.FirstOrDefault(t => t.Id == preferredType);
        act.SelectedIndex = Math.Clamp((preferred?.Act ?? preferredAct ?? 1) - 1, 0, 4);
        if (preferred is not null) types.SelectedItem = preferred;
    }

    private static Border Panel(UIElement child) => new() { Child = child, Background = new SolidColorBrush(Color.FromRgb(29, 39, 50)), CornerRadius = new(5), Padding = new(8) };
    private static TextBlock Step(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new(6, 8, 6, 4) };

    private void FilterTypes()
    {
        int chosen = act.SelectedIndex + 1;
        var keep = LevelType;
        types.ItemsSource = allTypes.Where(t => t.Act == chosen).ToArray();
        types.SelectedItem = types.Items.Contains(keep) ? keep : types.Items.Count > 0 ? types.Items[0] : null;
    }

    private async Task LoadTemplates()
    {
        loadingTemplates?.Cancel();
        var cts = loadingTemplates = new CancellationTokenSource();
        typeTemplates = []; templates.ItemsSource = null; next.IsEnabled = false; templateInfo.Text = "";
        if (LevelType is not { } type) { typeInfo.Text = ""; return; }
        typeInfo.Text = L.T("DT1 folders: {0}", string.Join(", ", type.Folders)) + "\n" + L.T("Finding layouts and tileset models…");
        try
        {
            var (found, models) = await Task.Run(() => (TilesetCatalog.Templates(tables, assets, workspaceRoot, type, cts.Token),
                TilesetCatalog.Models(assets, type.Folders, workspaceRoot, cts.Token).Length), cts.Token);
            if (cts.IsCancellationRequested) return;
            typeTemplates = found.Select(t => new TemplateItem(t)).ToArray();
            typeInfo.Text = L.T("DT1 folders: {0}", string.Join(", ", type.Folders)) + "\n" + L.T("{0} HD models for the tileset palette.", models);
            FilterTemplates();
            status.Text = typeTemplates.Length == 0 ? L.T("No layout of this tileset has a paired HD preset here. Try another tileset, or browse for a preset file.")
                : L.T("{0} layouts. Level presets come first; generator rooms follow.", typeTemplates.Length);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) status.Text = ex.Message; }
    }

    private void FilterTemplates()
    {
        var terms = search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var keep = Template;
        var shown = typeTemplates.Where(t => terms.All(term => t.Template.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || t.Template.Ds1Relative.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        templates.ItemsSource = shown;
        templates.SelectedItem = shown.FirstOrDefault(t => t.Template == keep) ?? shown.FirstOrDefault();
    }

    private void ShowTemplate()
    {
        next.IsEnabled = Template is not null;
        templateInfo.Text = Template is { } t ? L.T("Map: {0}", t.MapPath) + "\n" + L.T("HD preset: {0}", t.PresetPath) : "";
    }

    /// <summary>Choose a tileset and layout as the lists would, for checks.</summary>
    internal async Task Choose(int levelTypeId, string? ds1Relative = null)
    {
        var type = allTypes.First(t => t.Id == levelTypeId);
        act.SelectedIndex = type.Act - 1; types.SelectedItem = type;
        await TemplatesReady;
        if (ds1Relative is not null) templates.SelectedItem = templates.Items.OfType<TemplateItem>().First(t => t.Template.Ds1Relative.Equals(ds1Relative, StringComparison.OrdinalIgnoreCase));
    }
}
