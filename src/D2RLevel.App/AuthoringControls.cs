using System.IO;
using System.Windows;
using System.Windows.Controls;
using D2RLevel.Core;
using Microsoft.Win32;

namespace D2RLevel.App;

public partial class MainWindow
{
    private async void NewLevel_Click(object sender, RoutedEventArgs e)
    {
        if (loading is not null) return;
        if (document is null || pairedScene?.Collision is null || resolver is null)
        {
            if (resolver is null) { Status.Text = L.T("Choose Assets → Asset folder first, then New level to select an environment template."); return; }
            var template = new OpenFileDialog { Title = L.T("Choose the environment template for your new level"), Filter = "D2R preset JSON|*.json", InitialDirectory = resolver.Resolve("data/hd/env/preset") };
            if (template.ShowDialog(this) != true || !CanReplace()) return;
            try { await LoadPreset(template.FileName); } catch (Exception ex) { Error(ex); return; }
            if (document is null || pairedScene?.Collision is null) { Status.Text = L.T("This template has no supported paired DS1. Choose a fixed preset with an explicit level context."); return; }
        }
        var panel = new StackPanel { Margin = new Thickness(20) };
        var name = new TextBox { Text = L.T("My arena"), MaxLength = 100 };
        var scenery = new CheckBox { Content = L.T("Keep existing scenery"), IsChecked = false, Margin = new Thickness(3, 10, 3, 6), Foreground = System.Windows.Media.Brushes.White };
        var floors = pairedScene.Tiles.Keys.OrderBy(k => k.Main).ThenBy(k => k.Sub).ToArray();
        var floor = new ComboBox { ItemsSource = new[] { L.T("Empty ground") }.Concat(floors.Select(k => L.T("Floor {0}:{1}", k.Main, k.Sub))), SelectedIndex = 0, Margin = new Thickness(3), Foreground = System.Windows.Media.Brushes.Black };
        floor.WithReadableItems();
        panel.Children.Add(new TextBlock { Text = L.T("Create a level"), FontSize = 22, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = L.T("Environment: {0} · {1} × {2} tiles\nTerrain and environment are retained as a scaffold. Known standalone scenery is cleared unless you keep it below; unknown components and hierarchies are preserved. Gameplay starts with new floors and no walls, units or entrances.", Path.GetFileName(document.SourcePath), pairedScene.Map.Width, pairedScene.Map.Height), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = L.T("Project name") }); panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = L.T("Starting floor") }); panel.Children.Add(floor);
        panel.Children.Add(scenery);
        panel.Children.Add(new TextBlock { Text = L.T("Choose a parent folder next. A new project folder is created there; existing folders cannot be replaced. This creates a layout for the template's level slot, not a new area ID."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true }; var create = new Button { Content = L.T("Choose folder…"), IsDefault = true };
        buttons.Children.Add(cancel); buttons.Children.Add(create); panel.Children.Add(buttons);
        var dialog = new Window { Owner = this, Title = L.T("New level"), Width = 540, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground, Content = panel };
        create.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text)) dialog.DialogResult = true; };
        if (dialog.ShowDialog() != true) return;
        var folder = new OpenFolderDialog { Title = L.T("Choose the parent folder for the new level project") };
        if (folder.ShowDialog(this) != true) return;
        // Keep the template object until the new project is fully created and loaded.
        if (!CanReplace()) return;
        try
        {
            string safeName = string.Concat(name.Text.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')).Trim('-');
            if (safeName.Length == 0) safeName = "level";
            uint tile = floor.SelectedIndex <= 0 ? 0 : Ds1CollisionDocument.FloorKey(floors[floor.SelectedIndex - 1].Main, floors[floor.SelectedIndex - 1].Sub);
            string destination = Path.Combine(folder.FolderName, safeName);
            var scene = LevelProject.CreateFromTemplate(destination, name.Text, document, pairedScene, tile, placementLinks?.Calibration, resolver, scenery.IsChecked == true);
            var previousFolder = workspaceFolder; var previousScenes = workspaceScenes;
            workspaceFolder = Path.Combine(destination, "data"); workspaceScenes = SceneWorkspace.Scan(workspaceFolder);
            try
            {
                await OpenWorkspaceScene(scene);
                if (document?.SourcePath != scene.JsonPath)
                { workspaceFolder = previousFolder; workspaceScenes = previousScenes; Status.Text = L.T("Project created at {0}; opening was canceled. Previous scene retained.", destination); return; }
            }
            catch { workspaceFolder = previousFolder; workspaceScenes = previousScenes; throw; }
            settings = settings with { WorkspaceFolder = workspaceFolder, RecentWorkspaces = new[] { workspaceFolder }.Concat(settings.RecentWorkspaces ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray() };
            Status.Text = L.T("Created {0}. Draw ground in Ground / gameplay. HD terrain uses the template scaffold; entrances must be authored before game testing.", name.Text);
            if (SaveSettings() is { } warning) Status.Text += "\n" + warning;
            Notify(L.T("Level created"), destination);
        }
        catch (Exception ex) { Error(ex); }
    }
}
