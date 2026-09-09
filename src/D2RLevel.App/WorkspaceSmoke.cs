using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private async Task VerifyWorkspace(string output)
    {
        Directory.CreateDirectory(output);
        var assets = resolver ?? throw new InvalidOperationException("Smoke requires independent assets.");
        if (arguments.Contains("--workspace-restore-smoke"))
        {
            if (!exploringWorkspace || workspaceScenes.Length != 1 || workspaceFolder != settings.WorkspaceFolder)
                throw new InvalidOperationException("Workspace did not restore on startup.");
            await OpenWorkspaceScene(workspaceScenes[0]);
            if (workspaceSession is null || placementLinks?.Warning is not null || pairedScene is null)
                throw new InvalidOperationException("Restored workspace scene did not open.");
            File.WriteAllText(Path.Combine(output, "restore.txt"), "PASS: fresh process restores workspace explorer and opens saved scene with independent assets.");
            return;
        }
        var original = document ?? throw new InvalidOperationException("Provide a source preset.");
        var map = pairedScene?.Collision?.Document ?? throw new InvalidOperationException("Provide a paired DS1.");
        string root = Path.Combine(output, "mod", "data");
        string jsonPath = Path.GetFullPath(Path.Combine(root, "hd", "env", "preset", PresetPairing.Split(original.SourcePath, "hd/env/preset")!.Value.Relative));
        string ds1Path = Path.GetFullPath(Path.Combine(root, "global", "tiles", PresetPairing.Split(map.SourcePath, "global/tiles")!.Value.Relative));
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!); Directory.CreateDirectory(Path.GetDirectoryName(ds1Path)!);
        File.Copy(original.SourcePath, jsonPath); File.Copy(map.SourcePath, ds1Path);
        await LoadWorkspace(root);
        if (!exploringWorkspace || workspaceScenes.Length != 1 || resolver != assets || document is not null)
            throw new InvalidOperationException("Workspace browser did not discover pair independently of assets.");
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render); UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, "workspace.png"))) encoder.Save(stream);
        WorkspaceScenes.SelectedIndex = -1;
        WorkspaceScenes.IsEnabled = false;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render); UpdateLayout();
        var loadingBitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); loadingBitmap.Render(this);
        var loadingEncoder = new PngBitmapEncoder(); loadingEncoder.Frames.Add(BitmapFrame.Create(loadingBitmap));
        using (var stream = File.Create(Path.Combine(output, "workspace-disabled.png"))) loadingEncoder.Save(stream);
        WorkspaceScenes.IsEnabled = true;
        await OpenWorkspaceScene(workspaceScenes[0]);
        if (workspaceSession is null || exploringWorkspace || document!.SourcePath != jsonPath || pairedScene!.Collision!.Document.SourcePath != ds1Path)
            throw new InvalidOperationException("Workspace did not open the exact pair.");
        var entity = document.Entities.First(e => e.CanTransform && !e.HasParent && e.PreviewModel is not null);
        Hierarchy.SelectedItem = entity;
        double newX = entity.Transform.Position.X + 3;
        Apply(entity, entity.Transform with { Position = entity.Transform.Position with { X = newX } });
        var editable = pairedScene.Collision.Document;
        var tile = Enumerable.Range(0, editable.Height).SelectMany(y => Enumerable.Range(0, editable.Width).Select(x => (X: x, Y: y)))
            .First(p => editable.CanBlock(p.X, p.Y) && !editable.HasOverride(p.X, p.Y));
        editable.Paint([tile], true);
        SaveScene_Click(this, new());
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render); UpdateLayout();
        var savedBitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); savedBitmap.Render(this);
        var savedEncoder = new PngBitmapEncoder(); savedEncoder.Frames.Add(BitmapFrame.Create(savedBitmap));
        using (var stream = File.Create(Path.Combine(output, "save-toast.png"))) savedEncoder.Save(stream);
        if (document.IsDirty || editable.IsDirty || !File.Exists(jsonPath + PlacementLinks.Suffix) || !File.Exists(jsonPath + ".bak"))
            throw new InvalidOperationException("Save Scene did not persist all files.");
        WorkspaceExplorer_Click(this, new());
        if (!exploringWorkspace || document is not null) throw new InvalidOperationException("Workspace Explorer retained scene state.");
        await OpenWorkspaceScene(workspaceScenes[0]);
        if (document!.Entities.First(e => e.Id == entity.Id).Transform.Position.X != newX || !pairedScene!.Collision!.Document.HasOverride(tile.X, tile.Y))
            throw new InvalidOperationException("Saved scene did not reopen with both edits.");
        if (resolver != assets || EditorSettings.Load(SettingsPath, out _).WorkspaceFolder != root) throw new InvalidOperationException("Workspace persistence changed asset configuration.");
        if (arguments.Contains("--delete-smoke"))
        {
            var target = document.Entities.First(e => e.Id == entity.Id);
            var collision = pairedScene!.Collision!.Document;
            var ownedCell = Enumerable.Range(0, collision.Height).SelectMany(y => Enumerable.Range(0, collision.Width).Select(x => (X: x, Y: y)))
                .First(p => collision.CanBlock(p.X, p.Y) && !collision.HasOverride(p.X, p.Y));
            placementLinks!.LinkFootprint(target, [new(ownedCell.X, ownedCell.Y)], 10, false);
            placementLinks.LinkUnit(target, 0, 10);
            var beforeDelete = collision.Serialize(); int unitsBefore = collision.Units.Count;
            Hierarchy.SelectedItem = target; Floors_Click(this, new());
            DeleteModel_Click(this, new());
            if (document.Entities.Contains(target) || Scene.GetItem(target) is not null || collision.Units.Count != unitsBefore - 1 || collision.HasOverride(ownedCell.X, ownedCell.Y))
                throw new InvalidOperationException("Delete did not remove model, linked unit and owned collision.");
            Undo_Click(this, new());
            if (!document.Entities.Contains(target) || Scene.GetItem(target) is null || !collision.Serialize().SequenceEqual(beforeDelete))
                throw new InvalidOperationException("Undo did not restore existing model rendering and exact DS1.");
            Redo_Click(this, new()); SaveScene_Click(this, new());
            WorkspaceExplorer_Click(this, new()); await OpenWorkspaceScene(workspaceScenes[0]);
            if (document!.Entities.Any(e => e.Id == target.Id) || pairedScene!.Collision!.Document.Units.Count != unitsBefore - 1 || placementLinks!.Warning is not null)
                throw new InvalidOperationException("Deleted scene did not save and reopen correctly.");
            File.WriteAllText(Path.Combine(output, "delete.txt"), "PASS: Delete button removes existing rendered model, linked unit and owned collision with DS1 window open; undo restores geometry and exact DS1; redo, Save Scene and reopen retain deletion and valid links.");
        }
        File.WriteAllText(Path.Combine(output, "smoke.txt"), "PASS: workspace discovery, browser, exact pair load, independent assets, Save Scene button writes JSON/DS1/links with backups, explorer return, paired reopen and persisted workspace settings. All writes confined to artifact mod copy.");
    }
}
