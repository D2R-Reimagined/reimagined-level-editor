using System.IO;
using System.Windows;
using D2RLevel.Core;
namespace D2RLevel.App;
public partial class MainWindow
{
    private async Task VerifyNpcs(string output)
    {
        Directory.CreateDirectory(output);
        var akara = npcItems.First(i => i.Entity.Name.StartsWith("akara"));
        if (akara.IsPlaceholder) throw new InvalidOperationException(string.Join("\n", npcPreview.Diagnostics));
        var originalJson = document!.Serialize();
        var ds1 = pairedScene!.Collision!.Document;
        var originalDs1 = ds1.Serialize();
        var index = akara.Entity.GameplayUnitIndex!.Value;
        Hierarchy.SelectedItem = akara.Entity; Scene.FrameSelected();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        UpdateLayout();
        var capture = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        capture.Render(this);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(capture));
        using (var image = File.Create(Path.Combine(output, "npc-akara.png"))) png.Save(image);
        var before = ds1.Units[index]; var transform = akara.Entity.Transform;
        Apply(akara.Entity, transform with { Position = transform.Position with { X = transform.Position.X + 2 } });
        if (ds1.Units[index].X != before.X + 1 || !document.Serialize().SequenceEqual(originalJson)) throw new InvalidOperationException("NPC move must change DS1 only.");
        Undo_Click(this, new());
        if (!ds1.Serialize().SequenceEqual(originalDs1)) throw new InvalidOperationException("NPC undo failed.");
        Redo_Click(this, new());
        if (ds1.Units[index].X != before.X + 1) throw new InvalidOperationException("NPC redo failed.");
        var dragEntity = npcItems.First(i => i.Entity.GameplayUnitIndex == index).Entity;
        Hierarchy.SelectedItem = dragEntity; Scene.FrameSelected();
        var dragPoint = new Point(Scene.ActualWidth / 2, Scene.ActualHeight / 2);
        var dragBefore = ds1.Serialize();
        if (!Scene.BeginDrag(dragEntity, dragPoint)) throw new InvalidOperationException("NPC drag did not start.");
        Scene.ContinueDrag(new Point(dragPoint.X + 60, dragPoint.Y + 20)); Scene.CancelDrag();
        if (!ds1.Serialize().SequenceEqual(dragBefore)) throw new InvalidOperationException("Canceled NPC drag changed DS1.");
        if (!Scene.BeginDrag(dragEntity, dragPoint)) throw new InvalidOperationException("NPC drag did not restart.");
        Scene.ContinueDrag(new Point(dragPoint.X + 120, dragPoint.Y + 40)); Scene.EndDrag(true);
        if (dragEntity.Transform.Position.X != ds1.Units[index].X * Ds1Preview.UnitsPerTile / 5 || dragEntity.Transform.Position.Z != ds1.Units[index].Y * Ds1Preview.UnitsPerTile / 5) throw new InvalidOperationException("Committed drag retained a stale preview transform.");
        if (ds1.Serialize().SequenceEqual(dragBefore)) throw new InvalidOperationException("Committed NPC drag did not move DS1.");
        Undo_Click(this, new());
        if (!ds1.Serialize().SequenceEqual(dragBefore)) throw new InvalidOperationException("NPC drag undo failed.");
        var saved = Path.Combine(output, "npc-edited.ds1"); ds1.SaveCopy(saved);
        if (Ds1CollisionDocument.Load(saved).Units[index].X != before.X + 1) throw new InvalidOperationException("NPC save/reopen failed.");
        ShowNpcs.IsChecked = false; Npcs_Click(this, new());
        if (npcItems.Count != 0) throw new InvalidOperationException("NPC toggle failed.");
        ShowNpcs.IsChecked = true; Npcs_Click(this, new());
        if (!npcItems.Any(i => i.Entity.GameplayUnitIndex == index)) throw new InvalidOperationException("NPC toggle restore failed.");
        Floors_Click(this, new());
        ds1Window!.SelectGameplayUnit(index);
        if (Selected?.GameplayUnitIndex != index) throw new InvalidOperationException("DS1 selection sync failed.");
        Undo_Click(this, new());
        Redo_Click(this, new());
        ds1Window.Close();
        File.WriteAllText(Path.Combine(output, "smoke.txt"), $"PASS real Akara static mesh, NPC move/undo/redo, drag commit/cancel, save/reopen, unchanged JSON, toggle and DS1 selection. {npcItems.Count} NPCs; {npcItems.Count(i => !i.IsPlaceholder)} meshes.\nAkara bounds: {akara.Geometry.Bounds}\n" + string.Join("\n", npcPreview.Diagnostics));
    }
}
