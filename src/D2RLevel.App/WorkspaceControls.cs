using System.IO;
using System.Windows;
using System.Windows.Controls;
using D2RLevel.Core;
using Microsoft.Win32;

namespace D2RLevel.App;

public partial class MainWindow
{
    private string? workspaceFolder;
    private WorkspaceScene[] workspaceScenes = [];
    private WorkspaceSceneSession? workspaceSession, openingWorkspaceSession;
    private bool exploringWorkspace;
    // Peeking at the scene list from an open scene: the list replaces the entity browser on the
    // left while the scene itself stays loaded in the viewport.
    private bool browsingWorkspace;
    private async void LoadWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L.T("Select mod data directory (containing hd/env/preset and global/tiles)"), InitialDirectory = workspaceFolder ?? "" };
        if (dialog.ShowDialog(this) != true) return;
        if (!CanReplace()) return;
        DisconnectStudio();
        try { await LoadWorkspace(dialog.FolderName); } catch (Exception ex) { Error(ex); }
    }
    private async Task LoadWorkspace(string folder)
    {
        if (loading is not null) return;
        WorkspaceScene[] scenes;
        using var cts = new CancellationTokenSource(); loading = cts; RefreshState();
        try { scenes = await Task.Run(() => SceneWorkspace.Scan(folder, cts.Token), cts.Token); }
        catch (OperationCanceledException) { Status.Text = L.T("Workspace scan canceled."); return; }
        finally
        {
            loading = null; RefreshState();
            if (closeAfterCancel) { closeAfterCancel = false; Close(); }
        }
        if (!CanReplace()) return;
        Program.Integration?.ClaimProject(StudioTableContext.Project?.Root ?? folder);
        workspaceFolder = Path.GetFullPath(folder); workspaceScenes = scenes;
        settings = settings with { WorkspaceFolder = workspaceFolder,
            RecentWorkspaces = new[] { workspaceFolder }.Concat(settings.RecentWorkspaces ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray() };
        ShowWorkspaceExplorer();
        if (SaveSettings() is { } warning) Status.Text += "\n" + warning;
    }
    private void WorkspaceExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (workspaceFolder is null || loading is not null || !CanReplace()) return;
        ShowWorkspaceExplorer();
    }
    private void ShowWorkspaceExplorer()
    {
        Scene.CancelDrag(); document = null; pairedScene = null; workspaceSession = null;
        connectionEdits = null;
        levelPropertiesRevision++; LevelDetails.SetSnapshot(null);
        pairedStatus = L.T("Choose a workspace scene."); InitializeLinks();
        assetGroups = null;
        npcItems.Clear(); npcPreview = new(new(), []);
        addedModels.Clear(); removedModels.Clear(); Scene.SetScene(new([], [], 0));
        Filter(); Ds1Preview.SetScene(null, pairedStatus); PopulateInspector();
        exploringWorkspace = true; browsingWorkspace = false; SetWorkspaceView();
        RecentWorkspaces.ItemsSource = settings.RecentWorkspaces ?? []; RecentWorkspaces.SelectedItem = workspaceFolder;
        WorkspaceSearch.Text = ""; FilterWorkspace();
        PathLabel.Text = L.T("Workspace: {0}", workspaceFolder) + "  |  " + L.T("Assets: {0}", resolver?.DataRoot ?? L.T("not selected"));
        Diagnostics.Text = L.T("Select a scene on the left to load its JSON and DS1 together. Save Scene writes back to this workspace; assets remain in the independently selected asset folder.");
        Status.Text = workspaceScenes.Length == 0 ? L.T("No matching JSON/DS1 pairs found in this workspace.") : L.T("{0} scenes found. Select a scene on the left.", workspaceScenes.Length);
        RefreshState();
    }
    private void SetWorkspaceView()
    {
        bool listOnLeft = exploringWorkspace || browsingWorkspace;
        WorkspaceBrowser.Visibility = listOnLeft ? Visibility.Visible : Visibility.Collapsed;
        EntityBrowser.Visibility = listOnLeft ? Visibility.Collapsed : Visibility.Visible;
        // Only leaving the scene entirely gives the viewport over to the landing page; peeking at
        // the list keeps the scene, its inspector and its viewport exactly as they were.
        InspectorPanel.Visibility = exploringWorkspace ? Visibility.Collapsed : Visibility.Visible;
        ScenePanel.Visibility = exploringWorkspace ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceLanding.Visibility = exploringWorkspace ? Visibility.Visible : Visibility.Collapsed;
        UpdateWorkspaceChevrons();
    }
    private void UpdateWorkspaceChevrons()
    {
        BackToSceneButton.Visibility = browsingWorkspace ? Visibility.Visible : Visibility.Collapsed;
        BackToWorkspaceButton.Visibility = !exploringWorkspace && !browsingWorkspace && workspaceFolder is not null && document is not null
            ? Visibility.Visible : Visibility.Collapsed;
    }
    /// <summary>Shows the workspace scene list beside the open scene, without closing it.</summary>
    private void BackToWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (workspaceFolder is null || loading is not null || exploringWorkspace || browsingWorkspace) return;
        browsingWorkspace = true; SetWorkspaceView();
        RecentWorkspaces.ItemsSource = settings.RecentWorkspaces ?? []; RecentWorkspaces.SelectedItem = workspaceFolder;
        FilterWorkspace(); WorkspaceSearch.Focus();
    }
    private void BackToScene_Click(object sender, RoutedEventArgs e)
    {
        if (!browsingWorkspace) return;
        browsingWorkspace = false; SetWorkspaceView();
    }
    private void FilterWorkspace()
    {
        var matches = workspaceScenes.Where(s => s.Name.Contains(WorkspaceSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        WorkspaceScenes.ItemsSource = matches; WorkspaceCount.Text = L.T("{0} / {1} scenes", matches.Length, workspaceScenes.Length);
    }
    private void WorkspaceSearch_Changed(object sender, TextChangedEventArgs e) { if (WorkspaceScenes is not null) FilterWorkspace(); }
    private async void RecentWorkspaces_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecentWorkspaces.SelectedItem is not string folder || folder == workspaceFolder || loading is not null) return;
        try { await LoadWorkspace(folder); } catch (Exception ex) { Error(ex); }
        finally { RecentWorkspaces.SelectedItem = workspaceFolder; }
    }
    private async void WorkspaceScenes_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((!exploringWorkspace && !browsingWorkspace) || loading is not null || WorkspaceScenes.SelectedItem is not WorkspaceScene scene) return;
        try
        {
            // A scene is still open when the list is only being peeked at, so opening another one
            // replaces it and has to ask about unsaved edits first.
            if (browsingWorkspace)
            {
                if (!CanReplace()) return;
                browsingWorkspace = false; SetWorkspaceView();
            }
            await OpenWorkspaceScene(scene);
        }
        catch (Exception ex) { Error(ex); }
        finally { WorkspaceScenes.SelectedItem = null; }
    }
    private async Task OpenWorkspaceScene(WorkspaceScene scene)
    {
        if (resolver is null) { Status.Text = L.T("Choose the asset folder first. It can be outside the workspace."); return; }
        StudioTableContext.LogicalMap = scene.LogicalMap ?? (PresetPairing.Split(scene.Ds1Path, "global/tiles") is { } location ? "global/tiles/" + location.Relative.Replace('\\', '/') : null);
        openingWorkspaceSession = new(StudioTableContext.Project?.Root ?? workspaceFolder!, scene);
        try
        {
            await LoadPreset(scene.JsonPath);
            if (workspaceSession == openingWorkspaceSession) { exploringWorkspace = false; browsingWorkspace = false; SetWorkspaceView(); }
        }
        finally { openingWorkspaceSession = null; RefreshState(); }
    }
    private void SaveScene_Click(object sender, RoutedEventArgs e)
    {
        if (workspaceSession is null || document is null || pairedScene?.Collision?.Document is not { } ds1 || placementLinks is null) return;
        try
        {
            workspaceSession.Save(document, ds1, placementLinks, connectionEdits);
            _ = RefreshLevelProperties();
            RefreshState(); PopulateInspector(); ds1Window?.RefreshFromWorkspace();
            Status.Text = L.T("Saved scene and staged connections to workspace: {0} · previous files kept as .bak", workspaceSession.Scene.Name);
            Notify(L.T("Scene saved"), workspaceSession.Scene.Name + "\n" + L.T("JSON, DS1 and links saved. Previous files kept as .bak."));
        }
        catch (Exception ex) { Error(ex); }
    }
}
