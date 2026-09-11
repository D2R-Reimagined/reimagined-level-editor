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
            if (resolver is null) { Status.Text = "Choose Assets → Asset folder first, then New level to select an environment template."; return; }
            var template = new OpenFileDialog { Title = "Choose the environment template for your new level", Filter = "D2R preset JSON|*.json", InitialDirectory = resolver.Resolve("data/hd/env/preset") };
            if (template.ShowDialog(this) != true || !CanReplace()) return;
            try { await LoadPreset(template.FileName); } catch (Exception ex) { Error(ex); return; }
            if (document is null || pairedScene?.Collision is null) { Status.Text = "This template has no supported paired DS1. Choose a fixed preset with an explicit level context."; return; }
        }
        var panel = new StackPanel { Margin = new Thickness(20) };
        var name = new TextBox { Text = "My arena", MaxLength = 100 };
        var scenery = new CheckBox { Content = "Keep existing scenery", IsChecked = false, Margin = new Thickness(3, 10, 3, 6), Foreground = System.Windows.Media.Brushes.White };
        var floors = pairedScene.Tiles.Keys.OrderBy(k => k.Main).ThenBy(k => k.Sub).ToArray();
        var floor = new ComboBox { ItemsSource = new[] { "Empty ground" }.Concat(floors.Select(k => $"Floor {k.Main}:{k.Sub}")), SelectedIndex = 0, Margin = new Thickness(3), Foreground = System.Windows.Media.Brushes.Black };
        floor.WithReadableItems();
        panel.Children.Add(new TextBlock { Text = "Create a level", FontSize = 22, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = $"Environment: {Path.GetFileName(document.SourcePath)} · {pairedScene.Map.Width} × {pairedScene.Map.Height} tiles\nTerrain and environment are retained as a scaffold. Known standalone scenery is cleared unless you keep it below; unknown components and hierarchies are preserved. Gameplay starts with new floors and no walls, units or entrances.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Project name" }); panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "Starting floor" }); panel.Children.Add(floor);
        panel.Children.Add(scenery);
        panel.Children.Add(new TextBlock { Text = "Choose a parent folder next. A new project folder is created there; existing folders cannot be replaced. This creates a layout for the template's level slot, not a new area ID.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; var create = new Button { Content = "Choose folder…", IsDefault = true };
        buttons.Children.Add(cancel); buttons.Children.Add(create); panel.Children.Add(buttons);
        var dialog = new Window { Owner = this, Title = "New level", Width = 540, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground, Content = panel };
        create.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text)) dialog.DialogResult = true; };
        if (dialog.ShowDialog() != true) return;
        var folder = new OpenFolderDialog { Title = "Choose the parent folder for the new level project" };
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
                { workspaceFolder = previousFolder; workspaceScenes = previousScenes; Status.Text = "Project created at " + destination + "; opening was canceled. Previous scene retained."; return; }
            }
            catch { workspaceFolder = previousFolder; workspaceScenes = previousScenes; throw; }
            settings = settings with { WorkspaceFolder = workspaceFolder, RecentWorkspaces = new[] { workspaceFolder }.Concat(settings.RecentWorkspaces ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray() };
            Status.Text = "Created " + name.Text + ". Draw ground in Ground / gameplay. HD terrain uses the template scaffold; entrances must be authored before game testing.";
            if (SaveSettings() is { } warning) Status.Text += "\n" + warning;
            Notify("Level created", destination);
        }
        catch (Exception ex) { Error(ex); }
    }
}
