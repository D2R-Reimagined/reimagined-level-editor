using System.IO;
using System.Windows;
using System.Windows.Controls;
using D2RLevel.Core;
using Microsoft.Win32;

namespace D2RLevel.App;

public partial class MainWindow
{
    /// <summary>A tileset chosen in the New level wizard, applied when its template preset loads.</summary>
    private (string Preset, LevelTileset Tileset)? chosenTileset;

    /// <summary>New level: choose a tileset and a starting layout of it, then name the project.</summary>
    private async void NewLevel_Click(object sender, RoutedEventArgs e)
    {
        if (loading is not null) return;
        if (resolver is null) { Status.Text = L.T("Choose Assets → Asset folder first, then New level to choose a tileset."); return; }
        int? preferredType = null;
        try { preferredType = document is null ? null : LevelProject.ForPreset(document.SourcePath)?.LevelType; } catch (InvalidDataException) { }
        NewLevelWizard wizard;
        try { wizard = new NewLevelWizard(resolver, workspaceFolder, pairedScene?.Map.Act, preferredType) { Owner = this }; }
        catch (Exception ex) { Error(ex); return; }
        if (wizard.ShowDialog() != true) return;
        if (wizard.BrowseRequested) { await NewLevelFromFile(); return; }
        try { if (await LoadTemplate(wizard.LevelType!, wizard.Template!)) await CreateLevel(wizard.LevelType); }
        catch (Exception ex) { Error(ex); }
    }

    /// <summary>New level using the open scene as its template, as before the wizard.</summary>
    private async void NewLevelFromScene_Click(object sender, RoutedEventArgs e)
    {
        if (loading is not null) return;
        if (document is null || pairedScene?.Collision is null || resolver is null) { NewLevel_Click(sender, e); return; }
        try { await CreateLevel(null); } catch (Exception ex) { Error(ex); }
    }

    private bool TilesetFileExists(string logical)
    {
        if (workspaceFolder is not null && File.Exists(Path.Combine(workspaceFolder, logical[5..].Replace('/', Path.DirectorySeparatorChar)))) return true;
        try { return resolver is not null && File.Exists(resolver.Resolve(logical)); } catch (InvalidDataException) { return false; }
    }

    /// <summary>Open a wizard template with the chosen tileset. False when it did not load as a paired scene.</summary>
    private async Task<bool> LoadTemplate(LevelTypeChoice type, TemplateChoice template)
    {
        var tileset = TilesetCatalog.Tileset(type, template.Dt1Mask, TilesetFileExists);
        if (!CanReplace()) return false;
        chosenTileset = (Path.GetFullPath(template.PresetPath), tileset);
        try { await LoadPreset(template.PresetPath); }
        finally { chosenTileset = null; }
        if (!string.Equals(document?.SourcePath, Path.GetFullPath(template.PresetPath), StringComparison.OrdinalIgnoreCase) || pairedScene?.Collision is null)
        { Status.Text = L.T("The layout could not load with the {0} tileset. {1}", type.Name, pairedStatus); return false; }
        return true;
    }

    private async Task NewLevelFromFile()
    {
        var template = new OpenFileDialog { Title = L.T("Choose the environment template for your new level"), Filter = "D2R preset JSON|*.json", InitialDirectory = resolver!.Resolve("data/hd/env/preset") };
        if (template.ShowDialog(this) != true || !CanReplace()) return;
        try { await LoadPreset(template.FileName); } catch (Exception ex) { Error(ex); return; }
        if (document is null || pairedScene?.Collision is null) { Status.Text = L.T("This template has no supported paired DS1. Choose a fixed preset with an explicit level context."); return; }
        try { await CreateLevel(null); } catch (Exception ex) { Error(ex); }
    }

    /// <summary>Name the project and its starting ground, then create it from the open template scene and open the tileset palette.</summary>
    private async Task CreateLevel(LevelTypeChoice? levelType)
    {
        if (document is null || pairedScene?.Collision is null || resolver is null) return;
        var panel = new StackPanel { Margin = new Thickness(20) };
        var name = new TextBox { Text = L.T("My arena"), MaxLength = 100 };
        var scenery = new CheckBox { Content = L.T("Keep existing scenery"), IsChecked = false, Margin = new Thickness(3, 10, 3, 6), Foreground = System.Windows.Media.Brushes.White };
        var floors = pairedScene.Tiles.Keys.OrderBy(k => k.Main).ThenBy(k => k.Sub).ToArray();
        // Default to the template's usual ground: the first tile by index is often rock or void that blocks movement.
        int common = CommonFloor(pairedScene) is { } usual ? Array.IndexOf(floors, usual) : -1;
        var floor = new ComboBox { ItemsSource = new[] { L.T("Empty ground") }.Concat(floors.Select(k => L.T("Floor {0}:{1}", k.Main, k.Sub))), SelectedIndex = common >= 0 ? common + 1 : floors.Length > 0 ? 1 : 0, Margin = new Thickness(3), Foreground = System.Windows.Media.Brushes.Black };
        floor.WithReadableItems();
        panel.Children.Add(new TextBlock { Text = L.T("Create a level"), FontSize = 22, Margin = new Thickness(0, 0, 0, 12) });
        if (levelType is not null) panel.Children.Add(new TextBlock { Text = L.T("Tileset: {0}", levelType.Name), FontSize = 15, Margin = new Thickness(0, 0, 0, 6) });
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
        uint tile = floor.SelectedIndex <= 0 ? 0 : Ds1CollisionDocument.FloorKey(floors[floor.SelectedIndex - 1].Main, floors[floor.SelectedIndex - 1].Sub);
        await CreateLevelAt(Path.Combine(folder.FolderName, SafeProjectName(name.Text)), name.Text, tile, scenery.IsChecked == true, levelType);
    }

    /// <summary>The floor tile the template uses most on its first layer, among those its tileset can draw.</summary>
    internal static (int Main, int Sub)? CommonFloor(LegacyFloorScene scene)
    {
        int width = scene.Map.Width;
        var best = Enumerable.Range(0, width * scene.Map.Height).Select(i => scene.FloorAt(0, i % width, i / width))
            .Where(c => !c.IsEmpty && scene.Tiles.ContainsKey((c.Main, c.Sub))).GroupBy(c => (c.Main, c.Sub))
            .OrderByDescending(g => g.Count()).FirstOrDefault();
        return best?.Key;
    }

    private static string SafeProjectName(string name)
    {
        string safe = string.Concat(name.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')).Trim('-');
        return safe.Length == 0 ? "level" : safe;
    }

    /// <summary>Create the project from the open template scene, open it as the workspace scene, and offer the tileset palette.</summary>
    internal async Task CreateLevelAt(string destination, string name, uint tile, bool keepScenery, LevelTypeChoice? levelType)
    {
        // Keep the template object until the new project is fully created and loaded.
        if (document is null || pairedScene is null || !CanReplace()) return;
        var scene = LevelProject.CreateFromTemplate(destination, name, document, pairedScene, tile, placementLinks?.Calibration, resolver, keepScenery, levelType);
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
        Status.Text = L.T("Created {0}. Draw ground in Ground / gameplay, paint walls and props from the Tileset palette, and add exits in Entrances and exits before game testing.", name);
        if (SaveSettings() is { } warning) Status.Text += "\n" + warning;
        Notify(L.T("Level created"), destination);
        // Smoke runs open the palette themselves when they need it, so their captures stay predictable.
        if (!arguments.Contains("--smoke-output")) OpenTilesetPalette();
    }
}
