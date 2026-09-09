using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using D2RLevel.Core;

namespace D2RLevel.App;

public sealed class ModelExplorer : Window
{
    private readonly AssetResolver resolver;
    private readonly PresetDocument? document;
    private readonly Action<PresetEntity> select;
    private readonly ListBox list = new() { Background = Brushes.Transparent, Foreground = Brushes.White };
    private readonly TextBox search = new() { ToolTip = "Search model names or folders" };
    private readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10) };
    private readonly TextBlock count = new() { Margin = new Thickness(10) };
    private readonly Button find = new() { Content = "Select placed instance", IsEnabled = false };
    private readonly Button add = new() { Content = "Add to scene", IsEnabled = false, ToolTip = "Place this static model at the current scene focus. Left-drag it to position it." };
    private SceneItem? previewItem;
    private string? previewPath;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? previewLoad;
    private ModelEntry[] catalog = [];
    public SceneViewport Preview { get; } = new();
    public int ModelCount => catalog.Length;
    public bool PreviewLoaded { get; private set; }
    public Task Ready { get; private set; } = Task.CompletedTask;
    public Task PreviewReady { get; private set; } = Task.CompletedTask;
    internal bool CanAddPreview => add.IsEnabled;
    internal void AddPreviewToScene() { if (add.IsEnabled) add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }

    public ModelExplorer(AssetResolver resolver, PresetDocument? document, Action<PresetEntity> select, Action<string, SceneItem>? addModel = null)
    {
        this.resolver = resolver; this.document = document; this.select = select;
        Title = "Model explorer · extracted HD assets"; Width = 1200; Height = 780;
        Background = new SolidColorBrush(Color.FromRgb(20, 27, 35));
        var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(details); footer.Children.Add(find); footer.Children.Add(add);
        add.Click += (_, _) =>
        {
            if (addModel is null || previewItem is null || previewPath is null || !PreviewLoaded) return;
            try { addModel(previewPath, previewItem); Close(); }
            catch (Exception ex) { details.Text = ex.Message; }
        };
        AddAvailable = document is not null && addModel is not null;
        var heading = new TextBlock { Text = "MODEL EXPLORER · Right drag: look · Alt + right drag: orbit · Middle: pan · Wheel: move", Margin = new Thickness(8), FontSize = 16 };
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var left = new DockPanel { Width = 430 }; DockPanel.SetDock(left, Dock.Left); root.Children.Add(left);
        var searchLabel = new TextBlock { Text = "Search model names or folders", Margin = new Thickness(6) };
        DockPanel.SetDock(searchLabel, Dock.Top); left.Children.Add(searchLabel);
        DockPanel.SetDock(search, Dock.Top); left.Children.Add(search);
        DockPanel.SetDock(count, Dock.Bottom); left.Children.Add(count); left.Children.Add(list);
        list.DisplayMemberPath = nameof(ModelEntry.Path);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        root.Children.Add(Preview);
        search.TextChanged += (_, _) => Filter();
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is ModelEntry entry) PreviewReady = LoadPreview(entry.Path); };
        find.Click += (_, _) =>
        {
            if (list.SelectedItem is ModelEntry entry && FindInstances(entry.Path).FirstOrDefault() is { } entity)
            { select(entity); Close(); }
        };
        Loaded += (_, _) => Ready = LoadCatalog();
        Closed += (_, _) => { lifetime.Cancel(); previewLoad?.Cancel(); };
    }
    private bool AddAvailable { get; }

    private IEnumerable<PresetEntity> FindInstances(string path) => document?.Entities.Where(e =>
        e.ModelPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) ?? [];

    public async Task ShowModel(string path)
    {
        await Ready;
        if (lifetime.IsCancellationRequested) return;
        search.Text = Path.GetFileNameWithoutExtension(path);
        var entry = catalog.FirstOrDefault(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        list.SelectedItem = entry;
        if (entry is not null) { list.ScrollIntoView(entry); await PreviewReady; }
    }

    private async Task LoadCatalog()
    {
        try
        {
            count.Text = "Indexing extracted models…";
            catalog = await Task.Run(() => ModelCatalog.Scan(resolver, lifetime.Token));
            if (!lifetime.IsCancellationRequested) Filter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested) details.Text = ex.Message; }
    }

    private void Filter()
    {
        var terms = search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = catalog.Where(e => terms.All(t => e.Path.Contains(t, StringComparison.OrdinalIgnoreCase))).ToArray();
        list.ItemsSource = matches;
        count.Text = $"{matches.Length:N0} / {catalog.Length:N0} models · LOD0 previews";
        previewLoad?.Cancel(); PreviewLoaded = false; find.IsEnabled = add.IsEnabled = false; previewItem = null; previewPath = null;
        Preview.SetScene(new([], [], 0)); details.Text = "Select a model to preview its geometry and textures.";
    }

    public async Task LoadPreview(string path)
    {
        previewLoad?.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        previewLoad = cts; PreviewLoaded = false; find.IsEnabled = add.IsEnabled = false; previewItem = null; previewPath = null;
        Preview.SetScene(new([], [], 0)); details.Text = "Loading " + path;
        try
        {
            var result = await Task.Run(() => SceneLoader.Load(PresetDocument.ModelPreview(path), resolver,
                new Progress<string>(), cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (previewLoad != cts) return;
            Preview.SetScene(result); PreviewLoaded = result.LoadedModels == 1;
            if (PreviewLoaded) { previewItem = result.Items.Single(); previewPath = path; add.IsEnabled = AddAvailable; }
            var instances = FindInstances(path).Count();
            find.IsEnabled = instances > 0;
            details.Text = path + $"\n{instances} placed instance(s) in this preset · Static geometry and albedo preview.\n" +
                string.Join("\n", result.Diagnostics.Where(d => !d.StartsWith("Preview:")));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (previewLoad == cts && !lifetime.IsCancellationRequested) details.Text = ex.Message; }
        finally { if (previewLoad == cts) previewLoad = null; }
    }
}
