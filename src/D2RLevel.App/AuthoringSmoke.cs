using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private async Task VerifyAuthoring(string output)
    {
        Directory.CreateDirectory(output);
        var template = document ?? throw new InvalidOperationException("Load a template for authoring smoke.");
        var source = pairedScene ?? throw new InvalidOperationException(pairedStatus);
        byte[] originalJson = File.ReadAllBytes(template.SourcePath), originalMap = File.ReadAllBytes(source.Ds1Path);
        var key = source.Tiles.Keys.First(k => k.Main is >= 0 and <= 63 && k.Sub is >= 0 and <= 255);
        var created = LevelProject.CreateFromTemplate(Path.Combine(output, "arena"), "Authoring arena", template, source,
            Ds1CollisionDocument.FloorKey(key.Main, key.Sub), placementLinks?.Calibration, resolver);
        await LoadPreset(created.JsonPath);
        if (document!.Entities.Count >= template.Entities.Count) throw new InvalidOperationException("Template scenery was not cleared for the new layout.");
        if (workspaceSession is null || placementLinks is null || pairedScene?.Collision is null) throw new InvalidOperationException($"New project did not become a saved workspace. session={workspaceSession is not null}, links={placementLinks is not null}, pair={pairedStatus}, status={Status.Text}, source={document?.SourcePath}");
        var map = pairedScene.Collision.Document;
        if (map.Units.Count != 0 || map.GameplayWarning is not null) throw new InvalidOperationException("New DS1 did not start with valid empty gameplay.");
        Floors_Click(this, new());
        var window = ds1Window ?? throw new InvalidOperationException("Ground window did not open.");
        string brush = window.VerifyGroundAuthoring();
        var recipe = LevelProject.ForPreset(created.JsonPath)!.Placements.FirstOrDefault();
        if (recipe is not null)
        {
            int index = placementLinks.AppendUnit(recipe.Type, recipe.Id, 2, 2, recipe.Flags);
            map.InsertPathPoint(index, 0, 3, 3, 1);
        }
        SaveScene_Click(this, new());
        if (map.IsDirty || placementLinks.HasMetadataChanges) throw new InvalidOperationException("Save did not mark authored map clean.");
        var expected = map.Serialize();
        await CaptureAuthoring(window, Path.Combine(output, "ground.png"));
        window.Close();
        WindowState = WindowState.Normal; Width = 1100; Height = 800;
        await CaptureAuthoring(this, Path.Combine(output, "toolbar-1100.png"));
        Activate();
        var menu = Toolbar.Children.OfType<Menu>().Single(); var view = (MenuItem)menu.Items[2]; view.Focus(); view.IsSubmenuOpen = true;
        await Task.Delay(200);
        if (view.Template.FindName("PART_Popup", view) is System.Windows.Controls.Primitives.Popup { Child: FrameworkElement popup })
        {
            // Popups can close when the smoke process does not own desktop focus. Measure
            // the actual menu visual independently; this checks layout, not OS input delivery.
            if (popup.ActualWidth == 0)
            { popup.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); popup.Arrange(new Rect(popup.DesiredSize)); }
            await CaptureElement(popup, Path.Combine(output, "view-menu.png"));
        }
        view.IsSubmenuOpen = false;
        await LoadPreset(created.JsonPath);
        if (!pairedScene!.Collision!.Document.Serialize().SequenceEqual(expected) || placementLinks!.Warning is not null) throw new InvalidOperationException("Authored map failed reopen.");
        if (!File.ReadAllBytes(template.SourcePath).SequenceEqual(originalJson) || !File.ReadAllBytes(source.Ds1Path).SequenceEqual(originalMap)) throw new InvalidOperationException("Authoring touched template files.");
        string resize = await VerifyResize(output);
        ShowWorkspaceExplorer();
        await CaptureAuthoring(this, Path.Combine(output, "workspace.png"));
        File.WriteAllText(Path.Combine(output, "authoring-smoke.txt"), $"PASS new project, explicit DT1 context, blank DS1, shared links, save/reopen, template preservation, toolbar at 1100px, menu render, workspace explorer.\n{brush}\n{resize}\nTemplate: {template.SourcePath}\nDS1: {source.Ds1Path}\nSize: {source.Map.Width}x{source.Map.Height}\nUnits placed: {(recipe is null ? 0 : 1)}\nGame compatibility not tested.");
    }
    /// <summary>Grows the open project on every edge through the same path as Resize level, then checks the reopened scene.</summary>
    private async Task<string> VerifyResize(string output)
    {
        var session = workspaceSession ?? throw new InvalidOperationException("Resize smoke needs a workspace scene.");
        var map = pairedScene!.Collision!.Document;
        int width = map.Width, height = map.Height, units = map.Units.Count, entities = document!.Entities.Count;
        var unit = units > 0 ? map.Units[0] : null;
        var usual = CommonFloor(pairedScene) ?? throw new InvalidOperationException("Template has no drawable floor.");
        uint fill = Ds1CollisionDocument.FloorKey(usual.Main, usual.Sub);
        // The dialog runs its own modal loop; fill each direction, capture it, and cancel from inside that loop.
        _ = Dispatcher.InvokeAsync(async () =>
        {
            var dialog = OwnedWindows.OfType<Window>().Last(w => w.Title == L.T("Resize level"));
            var boxes = Descendants(dialog).OfType<TextBox>().ToArray();
            foreach (var (box, n) in boxes.Zip(new[] { "3", "2", "5", "4" })) box.Text = n;
            await CaptureAuthoring(dialog, Path.Combine(output, "resize-dialog.png"));
            dialog.DialogResult = false;
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (ResizeDialog(map) is not null) throw new InvalidOperationException("Canceled resize dialog returned a choice.");
        var result = await ResizeOpenLevel(session, new(3, 2, 4, 5), GroundFill.Tile, fill, true);
        var grown = pairedScene?.Collision?.Document ?? throw new InvalidOperationException("Resized level did not reopen with its DS1: " + pairedStatus);
        if (grown.Width != width + 7 || grown.Height != height + 7 || result.Width != grown.Width || workspaceSession is null || placementLinks?.Warning is not null)
            throw new InvalidOperationException($"Resized level reopened as {grown.Width}×{grown.Height}, links: {placementLinks?.Warning}.");
        if (grown.Units.Count != units || (unit is not null && (grown.Units[0].X != unit.X + 15 || grown.Units[0].Y != unit.Y + 10)))
            throw new InvalidOperationException("Placements did not shift with the grown map.");
        if (grown.Cell(grown.Floors[0], 0, 0) != fill || grown.Cell(grown.Floors[0], grown.Width - 1, grown.Height - 1) != fill)
            throw new InvalidOperationException("New ground was not filled.");
        if (document.Entities.Count != entities + result.TerrainCopies || result.TerrainCopies == 0)
            throw new InvalidOperationException($"HD terrain was not repeated: {result.TerrainCopies} copies.");
        // Measured from the reopened meshes: the copies must meet edge to edge and leave no tile without ground.
        var measured = document.TerrainEntities().Select(TerrainExtentOf).OfType<TerrainExtent>().ToArray();
        int gaps = TerrainTiling.CoverOffsets(measured[0], measured.Skip(1), grown.Width, grown.Height, placementLinks!.Calibration.UnitsPerTile).Length;
        int misaligned = TerrainTiling.Misaligned(measured[0], measured.Skip(1).Select((m, i) => (i, m))).Length;
        if (gaps != 0 || misaligned != 0) throw new InvalidOperationException($"Repeated terrain left {gaps} uncovered grid cells and {misaligned} misaligned copies.");
        string repair = await VerifyTerrainRepair(output, grown);
        WindowState = WindowState.Normal; Width = 1400; Height = 900;
        await CaptureAuthoring(this, Path.Combine(output, "resized.png"));
        Floors_Click(this, new());
        if (ds1Window is { } ground) { await CaptureAuthoring(ground, Path.Combine(output, "resized-ground.png")); ground.Close(); }
        return $"PASS resize: {width}×{height} → {grown.Width}×{grown.Height}, floor {usual.Main}:{usual.Sub} fill, {units} placement(s) shifted, {result.TerrainCopies} terrain copies, links intact after reopen.\n{repair}";
    }
    /// <summary>Pushes one terrain copy off the grid, as older resizes did, then runs Fill terrain gaps through its dialog and undoes it all.</summary>
    private async Task<string> VerifyTerrainRepair(string output, Ds1CollisionDocument map)
    {
        var copy = document!.TerrainEntities()[^1]; var start = copy.Transform;
        double units = placementLinks!.Calibration.UnitsPerTile;
        document.SetTransform(copy, start with { Position = start.Position with { X = start.Position.X + units * 3.5 } });
        Scene.UpdateEntity(copy);
        _ = Dispatcher.InvokeAsync(async () =>
        {
            var dialog = OwnedWindows.OfType<Window>().Last(w => w.Title == L.T("Fill terrain gaps"));
            await CaptureAuthoring(dialog, Path.Combine(output, "terrain-gaps-dialog.png"));
            dialog.DialogResult = true;
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        FillTerrainGaps_Click(this, new());
        var measured = document.TerrainEntities().Select(TerrainExtentOf).OfType<TerrainExtent>().ToArray();
        int gaps = TerrainTiling.CoverOffsets(measured[0], measured.Skip(1), map.Width, map.Height, units).Length;
        int misaligned = TerrainTiling.Misaligned(measured[0], measured.Skip(1).Select((m, i) => (i, m))).Length;
        if (gaps != 0 || misaligned != 0 || document.TerrainEntities().Contains(copy))
            throw new InvalidOperationException($"Fill terrain gaps left {gaps} uncovered cells and {misaligned} misaligned copies; status: {Status.Text}");
        await CaptureAuthoring(this, Path.Combine(output, "terrain-repaired.png"));
        document.Undo(); document.Undo();
        if (document.IsDirty || copy.Transform != start || !document.TerrainEntities().Contains(copy)) throw new InvalidOperationException("Undo did not restore the terrain.");
        return $"PASS terrain repair: misaligned copy replaced on the mesh grid, no uncovered tiles, undo restores the scene.";
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var inner in Descendants(child)) yield return inner;
        }
    }
    private async Task CaptureAuthoring(Window window, string path)
    { await Dispatcher.InvokeAsync(() => {}, System.Windows.Threading.DispatcherPriority.Render); window.UpdateLayout(); await CaptureElement(window, path); }
    private Task CaptureElement(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream); return Task.CompletedTask;
    }
}

public sealed partial class LegacyFloorWindow
{
    internal string VerifyGroundAuthoring()
    {
        var map = CollisionDocument!;
        var before = map.Serialize();
        Point Center(int x, int y) => new((x - y + scene.Map.Height) * 80 * scale * zoom.Value, (x + y + 1) * 40 * scale * zoom.Value);
        collisionTool.SelectedIndex = 4;
        BeginGround(Center(1, 1)); painting = true; blockStroke = false;
        AddStrokePoint(Center(1, 1)); AddStrokePoint(Center(3, 1));
        if (stroke.Count != 3 || !map.Serialize().SequenceEqual(before)) throw new InvalidOperationException("Ground stroke preview mutated map or missed cells.");
        CancelStroke();
        if (!map.Serialize().SequenceEqual(before)) throw new InvalidOperationException("Canceled floor stroke mutated map.");
        BeginGround(Center(1, 1)); painting = true; blockStroke = false; AddStrokePoint(Center(1, 1)); AddStrokePoint(Center(3, 1)); CommitStroke();
        if (!scene.FloorAt(0, 2, 1).IsEmpty) throw new InvalidOperationException("Erase did not update live floor view.");
        map.Undo(); if (!map.Serialize().SequenceEqual(before)) throw new InvalidOperationException("Floor undo not exact.");
        map.Redo(); if (!scene.FloorAt(0, 2, 1).IsEmpty) throw new InvalidOperationException("Floor redo failed.");
        collisionTool.SelectedIndex = 7;
        BeginGround(Center(1, 1)); painting = true; AddStrokePoint(Center(1, 1)); AddStrokePoint(Center(3, 2)); CommitStroke();
        if (scene.FloorAt(0, 2, 1).IsEmpty) throw new InvalidOperationException("Rectangle did not fill erased floor.");
        return "PASS actual ground controls: interpolated preview, cancel, erase, live floor view, undo/redo and rectangle.";
    }
}
