using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using D2RLevel.Core;
using D2RLevel.Assets;
using Microsoft.Win32;

namespace D2RLevel.App;

public partial class MainWindow : Window
{
    private PresetDocument? document;
    private AssetResolver? resolver;
    private CancellationTokenSource? loading;
    private bool decoderLoaded;
    private bool closeAfterCancel;
    private bool loadedFullDetail;
    private LegacyFloorWindow? ds1Window;
    private LegacyFloorScene? pairedScene;
    private string pairedStatus = "Open a JSON preset to find its DS1.";
    private PresetEntity? Selected => Hierarchy.SelectedItem as PresetEntity;
    private readonly string[] arguments;
    private EditorSettings settings = new();
    private string SettingsPath => Argument("--settings-file") ??
        (Argument("--smoke-output") is { } output ? Path.Combine(output, "settings.json") : EditorSettings.DefaultPath);

    private string? SaveSettings()
    {
        try { settings.Save(SettingsPath); return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return "Settings could not be saved: " + ex.Message; }
    }

    public MainWindow(string[] args)
    {
        arguments = args;
        InitializeComponent();
        Ds1Preview.ScaleChanged += () => { Scene.CancelDrag(); RefreshNpcs(); };
        Ds1Preview.OpenRequested += () => Floors_Click(this, new());
        Ds1Preview.CalibrateRequested += Calibrate_Click;
        settings = EditorSettings.Load(SettingsPath, out settingsReadWarning);
        InitializeWindowPlacement();
        FullDetail.IsChecked = args.Contains("--full-detail");
        Scene.EntitySelected += SelectFromViewport;
        Scene.AllowModelDragging = true;
        Scene.DragCommitted += (entity, transform) => { try { Apply(entity, transform); } catch (Exception ex) { Error(ex); } };
        Closing += OnClosing;
        PreviewKeyDown += OnKey;
        Loaded += async (_, _) => await Startup();
        RefreshState();
    }
    private string? Argument(string name)
    {
        int i = Array.IndexOf(arguments, name);
        return i >= 0 && i + 1 < arguments.Length ? arguments[i + 1] : null;
    }
    private async Task Startup()
    {
        try
        {
            var notices = new List<string>();
            if (settingsReadWarning is not null) notices.Add(settingsReadWarning);
            if ((Argument("--data") ?? settings.AssetFolder) is { Length: > 0 } data)
            {
                try { resolver = new(data); settings = settings with { AssetFolder = resolver.DataRoot }; }
                catch (Exception ex) { notices.Add("Asset folder unavailable. Choose Asset folder… " + ex.Message); }
            }
            string bundledGranny = Path.Combine(AppContext.BaseDirectory, "Native", "granny2.dll");
            bool useBundledGranny = Argument("--granny") is null && File.Exists(bundledGranny);
            if ((Argument("--granny") ?? (useBundledGranny ? bundledGranny : settings.GrannyPath)) is { Length: > 0 } granny)
            {
                try
                {
                    var fullPath = Path.GetFullPath(granny);
                    ModelReader.ConfigureDecoder(fullPath); decoderLoaded = true;
                    // Keep the bundled path relative to the app so moving the package works.
                    if (!useBundledGranny) settings = settings with { GrannyPath = fullPath };
                }
                catch (Exception ex) { notices.Add("Granny decoder unavailable. Choose Granny decoder… " + ex.Message); }
            }
            if ((resolver is not null || decoderLoaded) && SaveSettings() is { } saveWarning) notices.Add(saveWarning);
            RefreshState();
            if (Argument("--preset") is { } path) await LoadPreset(path);
            else if (settings.WorkspaceFolder is { Length: > 0 } workspace)
            {
                try { await LoadWorkspace(workspace); }
                catch (Exception ex) { notices.Add("Workspace could not be restored: " + ex.Message); }
            }
            else if (resolver is not null) { PathLabel.Text = "Assets: " + resolver.DataRoot; Status.Text = "Saved asset folder restored" + (decoderLoaded ? " · Granny decoder loaded." : "."); }
            if (notices.Count > 0)
            {
                Status.Text = string.Join("\n", notices);
                Diagnostics.Text += "\n" + string.Join("\n", notices);
            }
            if (Argument("--smoke-output") is { } output)
            {
                if (arguments.Contains("--authoring-smoke")) { await VerifyAuthoring(output); Application.Current.Shutdown(0); return; }
                if (arguments.Contains("--workspace-smoke") || arguments.Contains("--workspace-restore-smoke"))
                { await VerifyWorkspace(output); Application.Current.Shutdown(0); return; }
                if (arguments.Contains("--group-smoke")) { await VerifyGroups(output); Application.Current.Shutdown(0); return; }
                if (arguments.Contains("--npc-smoke")) { await VerifyNpcs(output); Application.Current.Shutdown(0); return; }
                if (arguments.Contains("--animation-smoke")) { await VerifyAnimation(output); Application.Current.Shutdown(0); return; }
                if (arguments.Contains("--links-smoke")) { await VerifyLinkedWorkspace(output); Application.Current.Shutdown(0); return; }
                if (document is null) throw new InvalidOperationException("Smoke test requires a loaded preset.");
                string orbitMetrics = arguments.Contains("--benchmark") ? await Scene.MeasureOrbit() : "";
                var entity = document.Entities.FirstOrDefault(e => e.CanTransform && e.Name.Equals(Argument("--select-name"), StringComparison.OrdinalIgnoreCase))
                    ?? document.Entities.FirstOrDefault(e => e.CanTransform && e.Name.Contains("campfire", StringComparison.OrdinalIgnoreCase))
                    ?? document.Entities.First(e => e.CanTransform && e.PreviewModel is not null);
                Hierarchy.SelectedItem = entity;
                if (arguments.Contains("--benchmark"))
                {
                    Scene.FrameSelected();
                    orbitMetrics += "\nLocal view: " + await Scene.MeasureOrbit(160);
                    if (!arguments.Contains("--no-batching"))
                    {
                        Scene.FocusArea();
                        orbitMetrics += "\nFocus area: " + await Scene.MeasureOrbit(160);
                    }
                }
                var original = entity.Transform;
                document.SetTransform(entity, original with { Position = original.Position with { X = original.Position.X + 1 } });
                Scene.UpdateEntity(entity);
                document.Undo(); Scene.UpdateEntity(entity);
                document.Redo(); Scene.UpdateEntity(entity);
                Directory.CreateDirectory(output);
                document.SaveCopy(Path.Combine(output, "edited-preset.json"));
                if (PresetDocument.Load(Path.Combine(output, "edited-preset.json")).Entities[entity.Index].Transform.Position.X != original.Position.X + 1)
                    throw new InvalidOperationException("Smoke save verification failed.");
                PopulateInspector(); RefreshState();
                if (document.Entities.Count > 20) Scene.FrameAll(); else Scene.FrameSelected();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                UpdateLayout();
                var pickingProbe = new SceneViewport();
                RenderingChecks.Run();
                pickingProbe.Measure(new Size(400, 400)); pickingProbe.Arrange(new Rect(0, 0, 400, 400));
                var probeEntity = PresetDocument.ModelPreview("data/hd/probe.model").Entities[0];
                pickingProbe.SetScene(new([new(probeEntity, SceneViewport.Placeholder(), true)], [], 0));
                pickingProbe.UpdateLayout();
                if (pickingProbe.Pick(new Point(200, 200)) != probeEntity)
                    throw new InvalidOperationException("Explicit click picking failed with automatic hit testing disabled.");
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(this);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(output, "editor.png"))) encoder.Save(file);
                File.WriteAllText(Path.Combine(output, "smoke.txt"), "PASS: scene load, selection, transform, undo, redo, save/reopen, camera navigation checks and WPF render\n" + Status.Text + "\n" + Diagnostics.Text);
                File.AppendAllText(Path.Combine(output, "smoke.txt"), "\nDS1 status: " + Ds1Preview.StatusText);
                if (arguments.Contains("--terrain-smoke"))
                {
                    var beforeTerrain = document.Serialize();
                    Scene.FrameAll();
                    File.AppendAllText(Path.Combine(output, "smoke.txt"), "\n" + Scene.VerifyTerrainVisibility());
                    File.AppendAllText(Path.Combine(output, "smoke.txt"), "\n" + VerifyTerrainLock());
                    if (!document.Serialize().SequenceEqual(beforeTerrain)) throw new InvalidOperationException("Terrain preview modified preset data.");
                    await Capture(this, "terrain-on.png");
                    ShowTerrain.IsChecked = false; Terrain_Click(this, new()); await Capture(this, "terrain-off.png");
                    ShowTerrain.IsChecked = true; Terrain_Click(this, new());
                }
                if (orbitMetrics.Length > 0) File.AppendAllText(Path.Combine(output, "smoke.txt"), "\n" + orbitMetrics);
                    async Task Capture(Window window, string name)
                    {
                        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                        window.UpdateLayout();
                        var capture = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        capture.Render(window);
                        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(capture));
                        using var file = File.Create(Path.Combine(output, name)); png.Save(file);
                    }
                if (arguments.Contains("--features") && resolver is not null)
                {
                    var explorer = new ModelExplorer(resolver, document, _ => { }, AddModelFromPreview) { Owner = this };
                    explorer.Show(); await explorer.Ready;
                    await explorer.ShowModel(entity.PreviewModel!);
                    if (!explorer.PreviewLoaded || explorer.ModelCount == 0) throw new InvalidOperationException("Explorer failed to load real model/catalog.");
                    await Capture(explorer, "model-explorer.png");
                    File.AppendAllText(Path.Combine(output, "smoke.txt"), $"\nPASS explorer: {explorer.ModelCount} catalog entries, real textured model preview");
                    if (!explorer.CanAddPreview) throw new InvalidOperationException("Successful preview did not enable insertion.");
                    int countBeforeInsert = document.Entities.Count;
                    explorer.AddPreviewToScene();
                    var inserted = Selected ?? throw new InvalidOperationException("Inserted model was not selected.");
                    if (document.Entities.Count != countBeforeInsert + 1 || inserted.PreviewModel != entity.PreviewModel)
                        throw new InvalidOperationException("Explorer did not insert the previewed model.");
                    var start = inserted.Transform;
                    var center = new Point(Scene.ActualWidth / 2, Scene.ActualHeight / 2);
                    if (!Scene.BeginDrag(inserted, center)) throw new InvalidOperationException("Cannot begin real-model drag.");
                    Scene.ContinueDrag(center + new Vector(50, 25)); Scene.EndDrag(true);
                    if (inserted.Transform == start || inserted.Transform.Position.Y != start.Position.Y)
                        throw new InvalidOperationException("Real-model drag did not move horizontally.");
                    var end = inserted.Transform;
                    Undo_Click(this, new());
                    if (inserted.Transform != start) throw new InvalidOperationException("Drag undo failed.");
                    Undo_Click(this, new());
                    if (document.Entities.Contains(inserted)) throw new InvalidOperationException("Insertion undo failed.");
                    Redo_Click(this, new()); Redo_Click(this, new());
                    if (inserted.Transform != end || !document.Entities.Contains(inserted)) throw new InvalidOperationException("Insertion/drag redo failed.");
                    document.SaveCopy(Path.Combine(output, "inserted-preset.json"));
                    var reopenedInsert = PresetDocument.Load(Path.Combine(output, "inserted-preset.json"));
                    if (reopenedInsert.Entities.Last().Transform != end || reopenedInsert.Entities.Last().Id != inserted.Id)
                        throw new InvalidOperationException("Inserted model save/reopen failed.");
                    await LoadScene(document, resolver);
                    Undo_Click(this, new()); Undo_Click(this, new()); Redo_Click(this, new()); Redo_Click(this, new());
                    if (Selected != inserted || inserted.Transform != end) throw new InvalidOperationException("Reload insertion history failed.");
                    Scene.FrameSelected(); await Capture(this, "inserted-model.png");
                    File.AppendAllText(Path.Combine(output, "smoke.txt"), "\nPASS explorer insertion, real model drag, two-step undo/redo, save/reopen and history after asset reload");
                    var matched = PresetPairing.Find(document.SourcePath, resolver) ?? throw new InvalidOperationException("No matching DS1.");
                    var legacy = pairedScene ?? throw new InvalidOperationException("DS1 was not automatically loaded alongside JSON.");
                    if (legacy.MissingCells != 0) throw new InvalidOperationException("Unresolved legacy floors.");
                    if (!Ds1Preview.HasPreview || ds1Window is not null) throw new InvalidOperationException("Inline DS1 preview must work before opening its editor.");
                    await Capture(this, "inline-ds1.png");
                    Ds1Preview.SetScene(null, "No matching DS1 detected.");
                    if (Ds1Preview.HasPreview) throw new InvalidOperationException("Missing DS1 left stale preview content.");
                    Ds1Preview.SetScene(pairedScene, pairedStatus);
                    Floors_Click(this, new());
                    var floorWindow = ds1Window ?? throw new InvalidOperationException("Cached DS1 editor did not open.");
                    floorWindow.WindowState = WindowState.Minimized;
                    Floors_Click(this, new());
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    if (!ReferenceEquals(ds1Window, floorWindow) || floorWindow.WindowState == WindowState.Minimized || !floorWindow.IsActive || floorWindow.Topmost)
                        throw new InvalidOperationException("Open DS1 failed to restore/focus the existing normal window.");
                    floorWindow.WindowState = WindowState.Maximized;
                    floorWindow.WindowState = WindowState.Minimized;
                    Floors_Click(this, new());
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    if (floorWindow.WindowState != WindowState.Maximized || !floorWindow.IsActive)
                        throw new InvalidOperationException("Open DS1 did not restore the previous maximized state.");
                    floorWindow.WindowState = WindowState.Normal;
                    File.AppendAllText(Path.Combine(output, "smoke.txt"), "\nPASS Open DS1 restores/focuses the existing window, including maximized restore, without always-on-top.");
                    if (!ReferenceEquals(legacy.Collision!.Document, floorWindow.EditableCollision)) throw new InvalidOperationException("Inline preview and editor must share the DS1 document.");
                    File.AppendAllText(Path.Combine(output, "smoke.txt"), "\n" + floorWindow.VerifyCollisionEditing(output));
                    File.AppendAllText(Path.Combine(output, "smoke.txt"), "\n" + floorWindow.VerifyFootprintSuggestion(output));
                    File.AppendAllText(Path.Combine(output, "smoke.txt"), "\n" + floorWindow.VerifyGameplayEditing(output));
                    if (!IsEnabled || !floorWindow.IsEnabled) throw new InvalidOperationException("Both editors must stay interactive.");
                    Scene.FrameSelected();
                    File.AppendAllText(Path.Combine(output, "smoke.txt"), "\nPASS inline DS1: automatic pair, preview before opening editor, shared editable document; both views enabled.");
                    File.AppendAllText(Path.Combine(output, "smoke.txt"), "\nPaired DS1: " + matched.Ds1Path);
                    await Capture(floorWindow, "legacy-floors.png"); floorWindow.Close();
                    File.AppendAllText(Path.Combine(output, "smoke.txt"), $"\nPASS legacy: {legacy.Map.Width}x{legacy.Map.Height}, {legacy.Dt1Paths.Length} DT1 files, no unresolved cells");
                }
                Application.Current.Shutdown(0);
            }
        }
        catch (Exception ex)
        {
            if (Argument("--smoke-output") is { } output)
            { Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); Application.Current.Shutdown(1); }
            else Error(ex);
        }
    }

    private bool CanReplace()
    {
        if ((document?.IsDirty == true || placementLinks?.HasMetadataChanges == true ||
            (document?.History.Shared == true && pairedScene?.Collision?.Document.IsDirty == true)) &&
            MessageBox.Show(this, "Discard unsaved JSON, DS1 or link edits? Use Save linked pair to keep them together.", "Unsaved changes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
        ds1Window?.Close();
        return ds1Window is null; // A canceled DS1 close also cancels preset replacement/app exit.
    }
    private async Task LoadPreset(string path)
    {
        var candidate = PresetDocument.Load(path);
        bool openedProject = openingWorkspaceSession is null && LevelProject.ForPreset(path) is not null;
        if (openedProject)
        {
            string root = PresetPairing.Split(path, "hd/env/preset")!.Value.DataRoot;
            var project = LevelProject.ForPreset(path)!;
            var scene = new WorkspaceScene(project.Name, path, SceneWorkspace.Inside(root, Path.Combine(root, project.Map[5..])));
            openingWorkspaceSession = new(root, scene);
        }
        try
        {
            await LoadScene(candidate, resolver);
            if (openedProject && document == candidate)
            {
                workspaceFolder = PresetPairing.Split(path, "hd/env/preset")!.Value.DataRoot;
                workspaceScenes = SceneWorkspace.Scan(workspaceFolder);
            }
        }
        finally { if (openedProject) { openingWorkspaceSession = null; RefreshState(); } }
    }
    private async Task LoadScene(PresetDocument candidate, AssetResolver? candidateResolver)
    {
        if (loading is not null) return;
        Scene.CancelDrag();
        using var cts = new CancellationTokenSource();
        loading = cts; RefreshState();
        try
        {
            var progress = new Progress<string>(s => { if (loading == cts && !cts.IsCancellationRequested) Status.Text = s; });
            bool fullDetail = FullDetail.IsChecked == true;
            var result = await Task.Run(() => SceneLoader.Load(candidate, candidateResolver, progress, cts.Token, fullDetail), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            var pairResult = document == candidate && pairedScene is not null
                ? (pairedScene, pairedStatus) : await LoadPairedDs1(candidate, candidateResolver, cts.Token);
            if (string.Equals(openingWorkspaceSession?.Scene.JsonPath, candidate.SourcePath, StringComparison.OrdinalIgnoreCase) && pairResult.Item1?.Collision is null)
                throw new InvalidDataException("Workspace scene could not load its DS1: " + pairResult.Item2);
            var nextNpcs = await Task.Run(() => NpcPreviewLoader.Load(pairResult.Item1, candidateResolver, PresetPairing.Split(candidate.SourcePath, "hd/env/preset")?.DataRoot, progress, cts.Token), cts.Token);
            result = await Task.Run(() => TerrainPreview.Apply(result, pairResult.Item1, cts.Token), cts.Token);
            var nextGround = await Task.Run(() => new NpcGround(result.Items, cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (document != candidate)
            {
                addedModels.Clear(); removedModels.Clear();
                workspaceSession = string.Equals(openingWorkspaceSession?.Scene.JsonPath, candidate.SourcePath, StringComparison.OrdinalIgnoreCase) ? openingWorkspaceSession : null;
                exploringWorkspace = false; SetWorkspaceView();
            }
            document = candidate; resolver = candidateResolver; assetGroups = new(candidate);
            (pairedScene, pairedStatus) = pairResult;
            Ds1Preview.SetScene(pairedScene, pairedStatus);
            foreach (var item in result.Items) if (addedModels.ContainsKey(item.Entity)) addedModels[item.Entity] = item;
            loadedFullDetail = fullDetail;
            Scene.SetScene(arguments.Contains("--no-batching") ? result with { Batches = null } : result);
            Scene.SetTerrainVisible(ShowTerrain.IsChecked == true);
            npcItems.Clear(); npcGround = nextGround; npcPreview = nextNpcs; npcAct = pairedScene?.Map.Act ?? 0; RefreshNpcs();
            InitializeLinks();
            Search.Text = ""; Filter();
            Diagnostics.Text = string.Join(Environment.NewLine, result.Diagnostics.Concat(npcPreview.Diagnostics));
            Diagnostics.Text += $"\nWPF rendering tier: {System.Windows.Media.RenderCapability.Tier >> 16} · process mode: {System.Windows.Media.RenderOptions.ProcessRenderMode}";
            Status.Text = $"{result.LoadedModels}/{result.Items.Count} model instances loaded · {result.Items.Count - result.LoadedModels} missing/unsupported markers · {result.Diagnostics.Count} diagnostics";
            if (result.MissingDecoderModels > 0) Status.Text = $"{result.LoadedModels}/{result.Items.Count} models loaded · {result.MissingDecoderModels} need a Granny decoder — click Granny decoder…";
            PathLabel.Text = candidate.SourcePath + "  |  Assets: " + (resolver?.DataRoot ?? "not selected");
            PopulateInspector();
        }
        catch (OperationCanceledException) { Status.Text = "Load canceled. Previous document retained."; }
        finally
        {
            loading = null; RefreshState();
            if (closeAfterCancel) { closeAfterCancel = false; Close(); }
        }
    }
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "D2R preset JSON|*.json", Title = "Open extracted HD preset" };
        if (dialog.ShowDialog(this) != true || !CanReplace()) return;
        try { await LoadPreset(dialog.FileName); } catch (Exception ex) { Error(ex); }
    }
    private async void Root_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select extracted data folder (containing hd/)" };
        if (dialog.ShowDialog(this) != true) return;
        ds1Window?.Close();
        if (ds1Window is not null) return;
        try
        {
            var next = new AssetResolver(dialog.FolderName);
            if (document is not null) await LoadScene(document, next);
            else { resolver = next; Status.Text = "Asset folder: " + resolver.DataRoot; }
            if (ReferenceEquals(resolver, next))
            {
                settings = settings with { AssetFolder = next.DataRoot };
                if (SaveSettings() is { } warning) Status.Text += "\n" + warning;
            }
        }
        catch (Exception ex) { Error(ex); }
    }
    private async void Reload_Click(object sender, RoutedEventArgs e)
    { if (document is null) return; try { await LoadScene(document, resolver); } catch (Exception ex) { Error(ex); } }
    private async void Detail_Click(object sender, RoutedEventArgs e)
    {
        if (document is null) return;
        var selected = Selected;
        try { await LoadScene(document, resolver); }
        catch (Exception ex) { Error(ex); }
        finally
        {
            FullDetail.IsChecked = loadedFullDetail;
            if (selected is not null) { Hierarchy.SelectedItem = selected; Scene.FrameSelected(); }
        }
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (document is null) return;
        if (workspaceSession is not null) { SaveScene_Click(sender, e); return; }
        if (document.History.Shared) { SavePair_Click(sender, e); return; }
        var dialog = new SaveFileDialog { Filter = "D2R preset JSON|*.json", FileName = Path.GetFileNameWithoutExtension(document.SourcePath) + ".edited.json", Title = "Save a separate edited preset" };
        if (dialog.ShowDialog(this) != true) return;
        try { document.SaveCopy(dialog.FileName); Status.Text = "Saved and verified: " + dialog.FileName; RefreshState(); Notify("Preset saved", dialog.FileName); } catch (Exception ex) { Error(ex); }
    }
    private void Audit_Click(object sender, RoutedEventArgs e)
    {
        if (document is null || resolver is null) { Status.Text = "Open a preset and select the asset folder first."; return; }
        var dialog = new SaveFileDialog { Filter = "Asset list|*.txt", FileName = "missing-assets.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try { var missing = resolver.Missing(document); File.WriteAllLines(dialog.FileName, missing); Status.Text = $"Exported {missing.Length} missing paths to {dialog.FileName}"; }
        catch (Exception ex) { Error(ex); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => loading?.Cancel();
    private void Terrain_Click(object sender, RoutedEventArgs e) => Scene.SetTerrainVisible(ShowTerrain.IsChecked == true);
    /// <summary>
    /// Locked terrain must refuse both routes that can move it: the viewport drag and
    /// the inspector transform. Unlocking must restore both.
    /// </summary>
    private string VerifyTerrainLock()
    {
        var terrain = document!.Entities.FirstOrDefault(e => e.IsTerrain && e.CanTransform && !e.HasParent)
            ?? throw new InvalidOperationException("No transformable terrain in this scene.");
        var prop = document.Entities.First(e => !e.IsTerrain && e.CanTransform && !e.HasParent && e.PreviewModel is not null);
        var before = document.Serialize();
        var centre = new Point(Scene.ActualWidth / 2, Scene.ActualHeight / 2);

        LockTerrain.IsChecked = true; LockTerrain_Click(this, new());
        if (Scene.BeginDrag(terrain, centre)) { Scene.CancelDrag(); throw new InvalidOperationException("Locked terrain still started a drag."); }
        if (!Scene.IsLocked(terrain) || Scene.IsLocked(prop)) throw new InvalidOperationException("Lock applied to the wrong entities.");
        bool refused = false;
        try { Apply(terrain, terrain.Transform with { Position = terrain.Transform.Position with { X = terrain.Transform.Position.X + 5 } }); }
        catch (InvalidOperationException) { refused = true; }
        if (!refused) throw new InvalidOperationException("Locked terrain accepted an inspector transform.");
        if (!document.Serialize().SequenceEqual(before)) throw new InvalidOperationException("A refused terrain move still changed the preset.");
        Hierarchy.SelectedItem = terrain; RefreshState();
        if (TransformPanel.IsEnabled) throw new InvalidOperationException("Transform panel stayed enabled for locked terrain.");

        LockTerrain.IsChecked = false; LockTerrain_Click(this, new());
        if (Scene.IsLocked(terrain)) throw new InvalidOperationException("Unlocking did not release terrain.");
        if (!Scene.BeginDrag(terrain, centre)) throw new InvalidOperationException("Unlocked terrain could not be dragged.");
        Scene.CancelDrag();
        LockTerrain.IsChecked = true; LockTerrain_Click(this, new());
        Hierarchy.SelectedItem = null; RefreshState();
        if (!document.Serialize().SequenceEqual(before)) throw new InvalidOperationException("Lock verification changed the preset.");
        return "PASS terrain lock: drag and inspector transform both refused while locked, both restored when unlocked, preset unchanged.";
    }

    private void LockTerrain_Click(object sender, RoutedEventArgs e)
    {
        Scene.CancelDrag();
        Scene.TerrainLocked = LockTerrain.IsChecked == true;
        Status.Text = Scene.TerrainLocked
            ? "Terrain locked. Viewport clicks pass through it; select it in the entity list to inspect it."
            : "Terrain unlocked. It can be clicked and dragged in the viewport again.";
        RefreshState();
    }
    private void Models_Click(object sender, RoutedEventArgs e)
    {
        if (resolver is null) { Status.Text = "Select the extracted asset folder first."; return; }
        var explorer = new ModelExplorer(resolver, document, entity =>
        {
            Search.Text = ""; Hierarchy.SelectedItem = entity; Hierarchy.ScrollIntoView(entity); Scene.FrameSelected();
        }, AddModelFromPreview) { Owner = this };
        if (Selected?.PreviewModel is { } path) explorer.Loaded += async (_, _) => await explorer.ShowModel(path);
        explorer.ShowDialog();
    }
    private void AddModelFromPreview(string path, SceneItem preview)
    {
        if (document is null) return;
        var entity = document.AddModel(path, Scene.PlacementPosition, preview.TexturePaths);
        var item = preview with { Entity = entity };
        addedModels[entity] = item; Scene.AddItem(item);
        Search.Text = ""; Filter(); Hierarchy.SelectedItem = entity; Hierarchy.ScrollIntoView(entity);
        Scene.FrameSelected(); RefreshState();
        Status.Text = "Added " + entity.Name + ". Left-drag to move; Esc cancels a drag. Saves as an HD decorative model (no collision).";
    }
    private async void Floors_Click(object sender, RoutedEventArgs e)
    {
        if (ds1Window is not null) { FocusDs1(ds1Window); return; }
        if (pairedScene is not null) { ShowDs1(new LegacyFloorWindow(pairedScene, Selected is { } current ? Scene.GetFootprint(current) : null)); return; }
        if (resolver is null) { Status.Text = "Select the extracted asset folder first."; return; }
        PresetPair? pair;
        try { pair = document is null ? null : PresetPairing.Find(document.SourcePath, resolver, settings.PresetPairs); }
        catch (Exception ex) { Error(ex); return; }
        string? paired = pair?.Ds1Path;
        if (paired is null || !File.Exists(paired))
        {
            var dialog = new OpenFileDialog { Filter = "Legacy preset|*.ds1", Title = "Choose the matching DS1 inside global/tiles", InitialDirectory = resolver.Resolve("data/global/tiles") };
            if (dialog.ShowDialog(this) != true) return; paired = dialog.FileName;
        }
        using var cts = new CancellationTokenSource(); loading = cts; RefreshState();
        try
        {
            Status.Text = "Resolving DS1 floors through LvlPrest, Levels and LvlTypes…";
            var overrideRoot = pair?.Source == "Base asset fallback" && document is not null ? PresetPairing.Split(document.SourcePath, "hd/env/preset")?.DataRoot : null;
            var result = await Task.Run(() => LegacyFloorScene.Load(paired, resolver, cts.Token, overrideRoot), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            string? pairingWarning = null;
            if (document is not null)
            {
                var pairs = new Dictionary<string, string>(settings.PresetPairs ?? [], StringComparer.OrdinalIgnoreCase);
                // Remember explicit choices, not automatic fallbacks that could hide a new mod override later.
                if (pair is null) { pairs[document.SourcePath] = paired; settings = settings with { PresetPairs = pairs }; pairingWarning = SaveSettings(); }
            }
            ShowDs1(new LegacyFloorWindow(result, Selected is { } selected ? Scene.GetFootprint(selected) : null));
            Status.Text = $"Legacy floors: {result.Map.Width} × {result.Map.Height} · {result.MissingCells} unresolved cells";
            if (pairingWarning is not null) Status.Text += "\n" + pairingWarning;
        }
        catch (OperationCanceledException) { Status.Text = "Legacy floor load canceled."; }
        catch (Exception ex) { Error(ex); }
        finally { loading = null; RefreshState(); if (closeAfterCancel) { closeAfterCancel = false; Close(); } }
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (Hierarchy is not null) Filter(); }
    private void ShowDs1(LegacyFloorWindow window)
    {
        ds1Window = window; window.Owner = this;
        window.SharedUnitsPerTile = () => Ds1Preview.UnitsPerTile;
        void RefreshPair()
        {
            pairedScene = window.FloorScene;
            pairedStatus = "DS1: " + Path.GetFileName(pairedScene.Ds1Path);
            Ds1Preview.SetScene(pairedScene, pairedStatus);
            InitializeLinks();
            RefreshNpcs();
        }
        RefreshPair();
        window.SceneChanged += RefreshPair;
        window.UnitSelected += index =>
        {
            if (!syncingNpcs && Selected?.GameplayUnitIndex != index) { Search.Text = ""; Hierarchy.SelectedItem = npcItems.FirstOrDefault(i => i.Entity.GameplayUnitIndex == index)?.Entity ?? Selected; }
            RefreshPathOverlay();
        };
        window.PathChanged += () => { RefreshNpcs(); RefreshPathOverlay(); RefreshState(); };
        window.Closed += (_, _) => ds1Window = null;
        window.Activated += (_, _) => window.UpdateHdFootprint(Selected is { } current ? Scene.GetFootprint(current) : null);
        window.LinkUnitRequested += (index, scale) =>
        {
            if (EditLink(links => links.LinkUnit(Selected ?? throw new InvalidOperationException("Select an HD model first."), index, scale)))
                window.ReviewLinkedCollision();
        };
        window.LinkFootprintRequested += (tiles, scale, claim) => EditLink(links => links.LinkFootprint(Selected ?? throw new InvalidOperationException("Select an HD model first."), tiles, scale, claim));
        window.SaveWorkspaceRequested += () => SavePair_Click(this, new());
        window.ConfigureLinkedCollision(placementLinks, Selected?.GameplayUnitIndex is null ? Selected : null);
        window.ConfigurePlacements(document is null ? null : LevelProject.ForPreset(document.SourcePath)?.Placements);
        window.Show();
    }
    private void Filter()
    {
        var search = Search.Text.Trim();
        var selected = SelectedEntities;
        var matches = document?.Entities.Concat(npcItems.Select(i => i.Entity)).Where(e => e.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            e.ModelPaths.Any(p => p.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray() ?? [];
        changingSelection = true;
        try { Hierarchy.ItemsSource = matches; }
        finally { changingSelection = false; }
        SetSelection(selected.Where(matches.Contains));
        EntityCount.Text = $"{matches.Length} / {(document?.Entities.Count ?? 0) + npcItems.Count} entities";
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (changingSelection) return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0 && SelectedEntities.Length == 1 && Selected is { } entity && assetGroups?.Find(entity) is { } group)
        {
            var members = assetGroups.Resolve(group);
            if (members.Length > 1) { Search.Text = ""; SetSelection(members); return; }
        }
        UpdateSelection();
    }
    private void PopulateInspector()
    {
        RefreshLinkStatus();
        RefreshPathOverlay();
        Ds1Preview.Select(Selected);
        ds1Window?.UpdateHdFootprint(Selected is { } current ? Scene.GetFootprint(current) : null);
        ds1Window?.ConfigureLinkedCollision(placementLinks, Selected?.GameplayUnitIndex is null ? Selected : null);
        var entity = Selected;
        SelectedName.Text = entity?.Name ?? "Select an entity";
        SelectedInfo.Text = entity is null ? "" : entity.GameplayUnitIndex is { } npcIndex ? $"DS1 unit #{npcIndex} · gameplay placement" : $"ID {entity.Id}\n{entity.Components.Count} preserved components";
        ModelLabel.Text = entity?.GameplayUnitIndex is not null ? "DS1 character · static reference pose" : entity?.PreviewModel ?? "No static model";
        RawJson.Text = entity?.RawJson ?? "";
        TransformPanel.IsEnabled = loading is null && entity?.CanTransform == true && !entity.HasParent && entity.GameplayUnitIndex is null && !Scene.IsLocked(entity);
        if (entity is not null && Scene.IsLocked(entity))
            SelectedInfo.Text += "\nTerrain is locked. Untick Lock terrain to move it.";
        if (entity?.CanTransform != true) return;
        var t = entity.Transform;
        double[] values = [t.Position.X, t.Position.Y, t.Position.Z, t.Orientation.X, t.Orientation.Y, t.Orientation.Z, t.Orientation.W, t.Scale.X, t.Scale.Y, t.Scale.Z];
        TextBox[] fields = [PX, PY, PZ, QX, QY, QZ, QW, SX, SY, SZ];
        for (int i = 0; i < fields.Length; i++) fields[i].Text = values[i].ToString("G17", CultureInfo.InvariantCulture);
    }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entity || document is null) return;
        try
        {
            double Read(TextBox field) => double.Parse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
            Apply(entity, new(new(Read(PX), Read(PY), Read(PZ)), new(Read(QX), Read(QY), Read(QZ), Read(QW)), new(Read(SX), Read(SY), Read(SZ))));
        }
        catch (Exception ex) { Error(ex); }
    }
    private void Apply(PresetEntity entity, EntityTransform transform)
    {
        if (Scene.IsLocked(entity))
            throw new InvalidOperationException("Terrain is locked. Untick Lock terrain in the toolbar to move it.");
        if (SelectedEntities.Length > 1 && SelectedEntities.Contains(entity))
        {
            if (transform.Orientation != entity.Transform.Orientation || transform.Scale != entity.Transform.Scale)
                throw new InvalidOperationException("Groups support translation only.");
            var p = entity.Transform.Position; var t = transform.Position;
            var members = SelectedEntities;
            GroupMovement.Move(document!, placementLinks, members, new(t.X-p.X, t.Y-p.Y, t.Z-p.Z));
            Status.Text = $"Moved {members.Length} assets together. Linked DS1 units and collision follow; Ctrl+Z undoes the entire move."; return;
        }
        if (entity.GameplayUnitIndex is not null) { MoveNpc(entity, transform); return; }
        if (placementLinks is not null) placementLinks.Move(entity, transform);
        else if (File.Exists(document!.SourcePath + PlacementLinks.Suffix)) throw new InvalidOperationException("Load the linked DS1 before moving objects. " + pairedStatus);
        else document!.SetTransform(entity, transform);
        Scene.UpdateEntity(entity); PopulateInspector(); RefreshState();
    }
    private void Nudge_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { CanTransform: true } entity || document is null) return;
        try
        {
            var t = entity.Transform; string tag = (string)((Button)sender).Tag; double delta = tag[1] == '+' ? 1 : -1;
            Apply(entity, t with { Position = tag[0] == 'x' ? t.Position with { X = t.Position.X + delta } : t.Position with { Z = t.Position.Z + delta } });
        }
        catch (Exception ex) { Error(ex); }
    }
    private void Rotate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { CanTransform: true } entity || document is null) return;
        try
        {
            var t = entity.Transform; var q = t.Orientation;
            var rotation = new System.Windows.Media.Media3D.Quaternion(new System.Windows.Media.Media3D.Vector3D(0, 1, 0), 15) *
                new System.Windows.Media.Media3D.Quaternion(q.X, q.Y, q.Z, q.W);
            rotation.Normalize();
            Apply(entity, t with { Orientation = new(rotation.X, rotation.Y, rotation.Z, rotation.W) });
        }
        catch (Exception ex) { Error(ex); }
    }
    private readonly Dictionary<PresetEntity, SceneItem> addedModels = new();
    private readonly HashSet<PresetEntity> removedModels = new();
    private void SyncHistory(PresetEntity entity)
    {
        if (!document!.Entities.Contains(entity))
        {
            if (Scene.GetItem(entity) is { } removed) addedModels[entity] = removed;
            Scene.RemoveItem(entity); removedModels.Add(entity);
        }
        else
        {
            if (removedModels.Remove(entity) && addedModels.TryGetValue(entity, out var item)) Scene.AddItem(item);
            Scene.UpdateEntity(entity);
        }
        Filter(); Hierarchy.SelectedItem = document.Entities.Contains(entity) ? entity : null;
        PopulateInspector(); RefreshState();
    }
    private void Undo_Click(object sender, RoutedEventArgs e) { Scene.CancelDrag(); if (document?.Undo() is { } entity) SyncHistory(entity); }
    private void Redo_Click(object sender, RoutedEventArgs e) { Scene.CancelDrag(); if (document?.Redo() is { } entity) SyncHistory(entity); }
    private void Frame_Click(object sender, RoutedEventArgs e) => Scene.FrameSelected();
    private void FocusArea_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) { Status.Text = "Select a prop to center the local editing area."; return; }
        if (!Scene.FocusArea()) Status.Text = "Focus area is available for large, batched scenes; use Frame selected for this scene.";
    }
    private void RefreshState()
    {
        bool busy = loading is not null;
        Scene.IsEnabled = !busy;
        DecoderNotice.Visibility = ModelReader.IsDecoderConfigured ? Visibility.Collapsed : Visibility.Visible;
        Toolbar.IsEnabled = !busy; Hierarchy.IsEnabled = !busy; TransformPanel.IsEnabled = !busy && Selected?.CanTransform == true && !Selected.HasParent && Selected.GameplayUnitIndex is null && !Scene.IsLocked(Selected);
        UpdateGroupControls();
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UndoButton.IsEnabled = document?.CanUndo == true; RedoButton.IsEnabled = document?.CanRedo == true;
        WorkspaceExplorerButton.IsEnabled = !busy && workspaceFolder is not null;
        SaveSceneButton.IsEnabled = !busy && workspaceSession is not null && placementLinks?.Warning is null && pairedScene?.Collision is not null;
        WorkspaceScenes.IsEnabled = !busy;
        SaveCopyButton.Visibility = workspaceSession is not null || exploringWorkspace ? Visibility.Collapsed : Visibility.Visible;
        SavePairButton.Content = workspaceSession is not null ? "Save Scene" : "Save linked pair…";
        DeleteModelButton.IsEnabled = !busy && SelectedEntities.Length == 1 && Selected is { HasParent: false, IsTerrain: false, PreviewModel: not null };
        RefreshLinkStatus();
        bool dirty = document?.IsDirty == true || placementLinks?.HasMetadataChanges == true || (document?.History.Shared == true && pairedScene?.Collision?.Document.IsDirty == true);
        Title = $"Reimagined Level Editor · {(document is null ? "Workspace" : Path.GetFileName(document.SourcePath))}{(dirty ? " *" : "")}";
    }
    private void Notify(string title, string message, bool error = false) => ToastManager.Show(ds1Window?.IsActive == true ? ds1Window : this, title, message, error);
    private void Error(Exception ex) { Status.Text = ex.Message; Notify("Action failed", ex.Message, true); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (loading is not null) { e.Cancel = true; closeAfterCancel = true; loading.Cancel(); return; }
        if (!CanReplace()) e.Cancel = true;
    }
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (loading is null && e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None &&
            (Scene.IsKeyboardFocusWithin || Hierarchy.IsKeyboardFocusWithin) && Keyboard.FocusedElement is not TextBox)
        { DeleteModel_Click(this, new()); e.Handled = true; return; }
        if (loading is not null || Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.S) { Save_Click(this, new()); e.Handled = true; }
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.Z) { Undo_Click(this, new()); e.Handled = true; }
        if (e.Key == Key.Y) { Redo_Click(this, new()); e.Handled = true; }
    }
}
