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
    private async void LoadWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select mod data directory (containing hd/env/preset and global/tiles)", InitialDirectory = workspaceFolder ?? "" };
        if (dialog.ShowDialog(this) != true) return;
        try { await LoadWorkspace(dialog.FolderName); } catch (Exception ex) { Error(ex); }
    }
    private async Task LoadWorkspace(string folder)
    {
        if (loading is not null) return;
        WorkspaceScene[] scenes;
        using var cts = new CancellationTokenSource(); loading = cts; RefreshState();
        try { scenes = await Task.Run(() => SceneWorkspace.Scan(folder, cts.Token), cts.Token); }
        catch (OperationCanceledException) { Status.Text = "Workspace scan canceled."; return; }
        finally
        {
            loading = null; RefreshState();
            if (closeAfterCancel) { closeAfterCancel = false; Close(); }
        }
        if (!CanReplace()) return;
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
        pairedStatus = "Choose a workspace scene."; InitializeLinks();
        addedModels.Clear(); removedModels.Clear(); Scene.SetScene(new([], [], 0));
        Filter(); Ds1Preview.SetScene(null, pairedStatus); PopulateInspector();
        exploringWorkspace = true; SetWorkspaceView();
        RecentWorkspaces.ItemsSource = settings.RecentWorkspaces ?? []; RecentWorkspaces.SelectedItem = workspaceFolder;
        WorkspaceSearch.Text = ""; FilterWorkspace();
        PathLabel.Text = "Workspace: " + workspaceFolder + "  |  Assets: " + (resolver?.DataRoot ?? "not selected");
        Diagnostics.Text = "Select a scene on the left to load its JSON and DS1 together. Save Scene writes back to this workspace; assets remain in the independently selected asset folder.";
        Status.Text = workspaceScenes.Length == 0 ? "No matching JSON/DS1 pairs found in this workspace." : $"{workspaceScenes.Length} scenes found. Select a scene on the left.";
        RefreshState();
    }
    private void SetWorkspaceView()
    {
        WorkspaceBrowser.Visibility = exploringWorkspace ? Visibility.Visible : Visibility.Collapsed;
        EntityBrowser.Visibility = exploringWorkspace ? Visibility.Collapsed : Visibility.Visible;
        InspectorPanel.Visibility = exploringWorkspace ? Visibility.Collapsed : Visibility.Visible;
        ScenePanel.Visibility = exploringWorkspace ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceLanding.Visibility = exploringWorkspace ? Visibility.Visible : Visibility.Collapsed;
    }
    private void FilterWorkspace()
    {
        var matches = workspaceScenes.Where(s => s.Name.Contains(WorkspaceSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        WorkspaceScenes.ItemsSource = matches; WorkspaceCount.Text = $"{matches.Length} / {workspaceScenes.Length} scenes";
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
        if (!exploringWorkspace || loading is not null || WorkspaceScenes.SelectedItem is not WorkspaceScene scene) return;
        try { await OpenWorkspaceScene(scene); } catch (Exception ex) { Error(ex); }
        finally { WorkspaceScenes.SelectedItem = null; }
    }
    private async Task OpenWorkspaceScene(WorkspaceScene scene)
    {
        if (resolver is null) { Status.Text = "Choose the asset folder first. It can be outside the workspace."; return; }
        openingWorkspaceSession = new(workspaceFolder!, scene);
        try
        {
            await LoadPreset(scene.JsonPath);
            if (workspaceSession == openingWorkspaceSession) { exploringWorkspace = false; SetWorkspaceView(); }
        }
        finally { openingWorkspaceSession = null; RefreshState(); }
    }
    private void SaveScene_Click(object sender, RoutedEventArgs e)
    {
        if (workspaceSession is null || document is null || pairedScene?.Collision?.Document is not { } ds1 || placementLinks is null) return;
        try
        {
            workspaceSession.Save(document, ds1, placementLinks);
            RefreshState(); PopulateInspector(); ds1Window?.RefreshFromWorkspace();
            Status.Text = "Saved JSON, DS1 and links to workspace: " + workspaceSession.Scene.Name + " · previous files kept as .bak";
            Notify("Scene saved", workspaceSession.Scene.Name + "\nJSON, DS1 and links saved. Previous files kept as .bak.");
        }
        catch (Exception ex) { Error(ex); }
    }
}
