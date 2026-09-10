using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using D2RLevel.Assets;
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
    private readonly ComboBox animations = new() { Width = 220, Foreground = Brushes.Black, Margin = new Thickness(4), IsEnabled = false };
    private readonly Button playPause = new() { Content = "Play", Width = 70, Margin = new Thickness(4), IsEnabled = false };
    private readonly Slider timeline = new() { Minimum = 0, Maximum = 1, Width = 300, Margin = new Thickness(4), IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox speed = new() { Width = 80, Foreground = Brushes.Black, Margin = new Thickness(4), IsEnabled = false };
    private readonly TextBlock animationStatus = new() { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.LightSteelBlue };
    private AnimatedPreview? animated;
    private bool syncingTimeline;
    internal bool HasAnimations => animated is not null;
    internal int AnimationCount => animated?.Animations.Count ?? 0;
    /// <summary>Why the last preview was or was not animated, for diagnostics.</summary>
    internal string LastAnimationNote { get; private set; } = "";
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
        var animationBar = BuildAnimationBar();
        DockPanel.SetDock(animationBar, Dock.Top); root.Children.Add(animationBar);
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
        Closed += (_, _) => { lifetime.Cancel(); previewLoad?.Cancel(); ClearAnimation(); };
    }
    private bool AddAvailable { get; }

    /// <summary>Shows the authored bind pose, with no animation applied.</summary>
    internal void ShowBindPose() { animated?.Select(null); RefreshAnimationStatus(); }

    /// <summary>Selects a named mode and seeks into it, for capture and verification.</summary>
    internal void ShowMode(string name, double seconds)
    {
        if (animated?.Animations.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } mode) return;
        animated.Select(mode); animations.SelectedItem = mode;
        animated.Seek(seconds); SyncTimeline(); RefreshAnimationStatus();
    }

    /// <summary>Exercises every animation control and confirms posing moves the mesh.</summary>
    internal string VerifyAnimationControls()
    {
        var preview = animated ?? throw new InvalidOperationException("No animated preview.");
        if (animations.SelectedItem is not CharacterAnimation initial) throw new InvalidOperationException("No mode selected.");

        // Sample every part: one mesh alone may be bound to bones a given clip never moves.
        double[] Sample() => preview.Model.Children.OfType<System.Windows.Media.Media3D.GeometryModel3D>()
            .Select(m => m.Geometry).OfType<System.Windows.Media.Media3D.MeshGeometry3D>()
            .SelectMany(g => g.Positions.Take(40))
            .SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray();

        // The bind pose is the mesh exactly as authored; posing must be reversible to it.
        // Restore through the preview, not the combo: re-assigning the same SelectedItem
        // raises no SelectionChanged, so the clip would silently stay unset.
        preview.Select(null); var bind = Sample();
        preview.Select(initial);
        preview.Seek(0); var atStart = Sample();
        preview.Seek(preview.Duration / 2); var atMiddle = Sample();
        if (atStart.SequenceEqual(atMiddle)) throw new InvalidOperationException($"Scrubbing '{initial.Name}' did not move the mesh.");
        if (atMiddle.Any(v => !double.IsFinite(v))) throw new InvalidOperationException("Posed mesh contains non-finite positions.");
        if (bind.SequenceEqual(atMiddle)) throw new InvalidOperationException("Posing did not move the mesh away from its bind pose.");

        // Switching modes must reset to the new clip's start and change its length.
        var other = preview.Animations.FirstOrDefault(a => a != initial && a.Duration > 0.1 && Math.Abs(a.Duration - initial.Duration) > 0.01)
            ?? preview.Animations.First(a => a != initial);
        animations.SelectedItem = other;
        if (preview.Current != other) throw new InvalidOperationException("Mode selection did not change the clip.");
        if (preview.Time != 0) throw new InvalidOperationException("Switching modes did not restart playback.");
        if (Math.Abs(timeline.Maximum - Math.Max(0.001, other.Duration)) > 0.001) throw new InvalidOperationException("Timeline length did not follow the mode.");

        // Play/pause has to start and stop the clock.
        playPause.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if (!preview.IsPlaying) throw new InvalidOperationException("Play did not start playback.");
        playPause.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if (preview.IsPlaying) throw new InvalidOperationException("Pause did not stop playback.");

        // Looping must wrap rather than run past the end.
        preview.Seek(other.Duration * 2);
        if (preview.Time > other.Duration + 0.001) throw new InvalidOperationException("Seek past the end was not clamped.");

        // Clearing the selection must return the mesh to the authored bind pose exactly.
        preview.Select(null);
        if (!bind.SequenceEqual(Sample())) throw new InvalidOperationException("Clearing the animation did not restore the bind pose.");
        preview.Select(initial); animations.SelectedItem = initial;

        // Popup items inherit the app's near-white TextBlock style unless templated.
        if (!animations.HasReadableItems() || !speed.HasReadableItems())
            throw new InvalidOperationException("An animation combo box would render its items in the panel foreground, which is illegible on the popup.");

        return $"PASS animation explorer: {preview.Animations.Count} modes on a {preview.Rig.Bones.Count}-bone rig " +
               $"({string.Join(", ", preview.Animations.Take(6).Select(a => a.Name))}…); scrub moves the mesh, " +
               "mode switch resets and retimes, play/pause toggles, seek clamps, bind pose restores.";
    }

    private WrapPanel BuildAnimationBar()
    {
        var bar = new WrapPanel { Margin = new Thickness(4, 0, 4, 4) };
        bar.Children.Add(new TextBlock { Text = "Animation", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
        bar.Children.Add(animations); bar.Children.Add(playPause);
        bar.Children.Add(new TextBlock { Text = "Speed", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        bar.Children.Add(speed); bar.Children.Add(timeline); bar.Children.Add(animationStatus);

        animations.WithReadableItems(nameof(CharacterAnimation.Name));
        speed.WithReadableItems();
        speed.ItemsSource = new[] { 0.25, 0.5, 1.0, 2.0 };
        speed.SelectedIndex = 2;
        speed.SelectionChanged += (_, _) => { if (animated is not null && speed.SelectedItem is double value) animated.Speed = value; };

        animations.SelectionChanged += (_, _) =>
        {
            if (animated is null || animations.SelectedItem is not CharacterAnimation choice) return;
            animated.Select(choice);
            timeline.Maximum = Math.Max(0.001, choice.Duration);
            SyncTimeline(); RefreshAnimationStatus();
        };
        playPause.Click += (_, _) =>
        {
            if (animated is null) return;
            if (animated.IsPlaying) animated.Pause(); else animated.Play();
            playPause.Content = animated.IsPlaying ? "Pause" : "Play";
        };
        // Dragging the timeline scrubs; the running clock writes it back through SyncTimeline.
        timeline.ValueChanged += (_, _) =>
        {
            if (syncingTimeline || animated is null) return;
            animated.Seek(timeline.Value); RefreshAnimationStatus();
        };
        return bar;
    }

    private void SyncTimeline()
    {
        if (animated is null) return;
        syncingTimeline = true;
        try { timeline.Value = Math.Clamp(animated.Time, timeline.Minimum, timeline.Maximum); }
        finally { syncingTimeline = false; }
    }

    private void RefreshAnimationStatus()
    {
        if (animated is null) { animationStatus.Text = ""; return; }
        animationStatus.Text = $"{animated.Time:F2}s / {animated.Duration:F2}s · {animated.Animations.Count} modes · {animated.Rig.Bones.Count} bones"
            + (animated.UnresolvedBones.Count > 0 ? $" · {animated.UnresolvedBones.Count} bound bone(s) outside this rig stay in bind pose" : "");
    }

    private void ClearAnimation()
    {
        animated?.Dispose(); animated = null;
        animations.ItemsSource = null; animations.IsEnabled = false;
        playPause.IsEnabled = speed.IsEnabled = timeline.IsEnabled = false;
        playPause.Content = "Play";
        syncingTimeline = true;
        try { timeline.Value = 0; timeline.Maximum = 1; }
        finally { syncingTimeline = false; }
        animationStatus.Text = "";
    }

    /// <summary>
    /// Replaces the static preview with a posable one when the selected model is a
    /// skinned character. Returns the note shown under the preview either way.
    /// </summary>
    private string TryAnimate(string path)
    {
        ClearAnimation();
        var preview = AnimatedPreview.TryCreate(path, resolver, null, out var reason);
        LastAnimationNote = reason ?? "animated";
        if (preview is null) return "Static geometry and albedo preview. " + (reason ?? "Not an animated character.");
        animated = preview;
        preview.Advanced += () => { SyncTimeline(); RefreshAnimationStatus(); };
        animations.ItemsSource = preview.Animations;
        animations.IsEnabled = playPause.IsEnabled = speed.IsEnabled = timeline.IsEnabled = true;
        speed.SelectedIndex = 2; preview.Speed = 1;
        // Prefer the idle pose, the way the game shows a character standing still.
        var first = preview.Animations.FirstOrDefault(a => a.Name.Equals("neutral", StringComparison.OrdinalIgnoreCase))
            ?? preview.Animations[0];
        animations.SelectedItem = first;
        Preview.SetScene(new([], [], 0));
        Preview.AddAnimated(preview.Model);
        return $"Skinned character · {preview.Animations.Count} animations · {preview.Rig.Bones.Count} bones. Choose a mode and press Play.";
    }

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
        ClearAnimation();
        Preview.SetScene(new([], [], 0)); details.Text = "Select a model to preview its geometry and textures.";
    }

    public async Task LoadPreview(string path)
    {
        previewLoad?.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        previewLoad = cts; PreviewLoaded = false; find.IsEnabled = add.IsEnabled = false; previewItem = null; previewPath = null;
        ClearAnimation();
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
            // A skinned character replaces the static preview with a posable one.
            string note = PreviewLoaded ? TryAnimate(path) : "Static geometry and albedo preview.";
            details.Text = path + $"\n{instances} placed instance(s) in this preset · {note}\n" +
                string.Join("\n", result.Diagnostics.Where(d => !d.StartsWith("Preview:")));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (previewLoad == cts && !lifetime.IsCancellationRequested) details.Text = ex.Message; }
        finally { if (previewLoad == cts) previewLoad = null; }
    }
}
