using System.IO;
using System.Windows;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private TilesetPalette? palette;
    /// <summary>The previewed geometry for models being painted, until their scene items exist.</summary>
    private SceneItem? placingItem;

    private void InitializePainting()
    {
        Scene.GroundHeight = (x, z) => npcGround.Height(x, z);
        Scene.BrushCommitted += transforms =>
        {
            try { PaintModels(transforms); }
            catch (Exception ex) { Error(ex); palette?.Report(ex.Message); }
        };
        Scene.BrushRotated += brush => palette?.FollowBrush(brush);
        Scene.BrushCanceled += () => palette?.StopPainting();
    }

    private void TilesetPalette_Click(object sender, RoutedEventArgs e)
    {
        if (loading is not null) return;
        try { OpenTilesetPalette(); } catch (Exception ex) { Error(ex); }
    }

    /// <summary>The tileset's DT1 folders for the open scene: from its loaded tileset, else from the HD models it already uses.</summary>
    private string[] PaletteFolders(PresetDocument current)
    {
        // blank.dt1 is appended to every tileset for unused ground; it says nothing about the level's own pieces.
        var dt1 = pairedScene?.Dt1Paths.Where(p => !p.Replace('\\', '/').EndsWith("act1/outdoors/blank.dt1", StringComparison.OrdinalIgnoreCase)) ?? [];
        var folders = TilesetCatalog.Folders(dt1);
        if (folders.Length > 0) return folders;
        return current.Entities.SelectMany(e => e.ModelPaths).Select(p => p.Replace('\\', '/').ToLowerInvariant().Split('/'))
            .Where(p => p.Length > 6 && p[0] == "data" && p[1] == "hd" && p[2] == "env" && p[3] == "model" && p[4] != "global")
            .Select(p => p[4] + "/" + p[5]).Distinct().ToArray();
    }

    internal TilesetPalette OpenTilesetPalette()
    {
        var assets = resolver ?? throw new InvalidOperationException(L.T("Choose Assets → Asset folder first."));
        var current = document ?? throw new InvalidOperationException(L.T("Open a scene to paint with its tileset."));
        if (palette is not null)
        {
            if (palette.WindowState == WindowState.Minimized) palette.WindowState = WindowState.Normal;
            palette.Activate(); return palette;
        }
        LevelProject? project = null;
        try { project = LevelProject.ForPreset(current.SourcePath); } catch (InvalidDataException) { }
        var folders = PaletteFolders(current);
        string name = project?.LevelTypeName ?? (folders.Length > 0 ? string.Join(", ", folders) : Path.GetFileNameWithoutExtension(current.SourcePath));
        string? root = PresetPairing.Split(current.SourcePath, "hd/env/preset")?.DataRoot;
        string? blockWarning = placementLinks is null || pairedScene?.Collision is null
            ? L.T("Painted pieces can block movement once the paired DS1 is loaded.") : placementLinks.Warning;
        var window = new TilesetPalette(assets, root, name, folders, () => document?.Entities.SelectMany(e => e.ModelPaths).ToArray() ?? [],
            () => Ds1Preview.UnitsPerTile, blockWarning) { Owner = this };
        // Beside the inspector, clear of the viewport's centre.
        window.Left = Math.Max(0, Left + ActualWidth - window.Width - 40); window.Top = Top + 110;
        window.BrushChanged += brush => { Scene.Brush = brush; if (brush is not null) Scene.Focus(); };
        window.Closed += (_, _) => { if (palette == window) palette = null; Scene.Brush = null; };
        palette = window; window.Show();
        return window;
    }

    /// <summary>
    /// Places one painted stroke as a single edit. With Block movement, each piece also owns the DS1 floor tiles under its bounds,
    /// in the same edit, so one undo removes both.
    /// </summary>
    private void PaintModels(EntityTransform[] transforms)
    {
        if (document is null || palette?.PreviewItem is not { } item || palette.ModelPath is not { } path || transforms.Length == 0) return;
        var doc = document;
        bool block = palette.BlockMovement && placementLinks is { Warning: null } && pairedScene?.Collision is not null;
        ModelPlacement? placed = null; int blocking = 0;
        placingItem = item;
        try
        {
            doc.History.Transaction(() =>
            {
                placed = doc.PlaceModels(path, transforms, item.TexturePaths);
                if (!block) return;
                var map = pairedScene!.Collision!.Document; double unitsPerTile = Ds1Preview.UnitsPerTile;
                foreach (var entity in placed.Added)
                {
                    var bounds = SceneViewport.Transform(entity.Transform).TransformBounds(item.Geometry.Bounds);
                    if (bounds.IsEmpty) continue;
                    var tiles = new ModelFootprint(entity.Name, bounds.X, bounds.Z, bounds.X + bounds.SizeX, bounds.Z + bounds.SizeZ).Tiles(map.Width, map.Height, unitsPerTile)
                        .Where(t => map.CanBlock(t.X, t.Y)).Select(t => new LinkedTile(t.X, t.Y)).ToArray();
                    if (tiles.Length == 0) continue;
                    placementLinks!.LinkFootprint(entity, tiles, unitsPerTile, false); blocking++;
                }
            }, () => placed);
        }
        finally { placingItem = null; }
        string piece = Path.GetFileNameWithoutExtension(path);
        Status.Text = L.T("Painted {0} × {1}. Ctrl+Z undoes the whole row.", transforms.Length, piece)
            + (block ? " " + L.T("{0} of them block movement with owned DS1 footprints.", blocking) : "");
        palette?.Report(Status.Text);
    }

    /// <summary>Keeps viewport geometry in step with a painted row as it is placed, undone and redone.</summary>
    private void SyncPlacement(ModelPlacement placement)
    {
        if (document is null) return;
        foreach (var entity in placement.Added)
        {
            if (document.Entities.Contains(entity))
            {
                if (Scene.GetItem(entity) is null)
                {
                    if (!addedModels.TryGetValue(entity, out var item))
                    {
                        item = (placingItem ?? throw new InvalidOperationException("Painted model geometry is unavailable.")) with { Entity = entity };
                        addedModels[entity] = item;
                    }
                    Scene.AddItem(item);
                }
                removedModels.Remove(entity);
            }
            else
            {
                if (Scene.GetItem(entity) is { } item) addedModels[entity] = item;
                Scene.RemoveItem(entity); removedModels.Add(entity);
            }
        }
        Search.Text = ""; Filter();
        SetSelection(placement.Added.Where(document.Entities.Contains));
        RefreshState();
    }
}
