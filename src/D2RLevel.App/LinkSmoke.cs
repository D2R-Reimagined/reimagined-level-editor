using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private async Task VerifyLinkedWorkspace(string output)
    {
        Directory.CreateDirectory(output);
        RenderingChecks.Run();
        var json = document ?? throw new InvalidOperationException("No JSON.");
        var ds1 = pairedScene?.Collision?.Document ?? throw new InvalidOperationException("No DS1.");
        var links = placementLinks ?? throw new InvalidOperationException("No link manager.");
        var entity = json.Entities.FirstOrDefault(e => e.CanTransform && !e.HasParent && e.Name == Argument("--select-name"))
            ?? json.Entities.First(e => e.CanTransform && !e.HasParent && e.PreviewModel is not null && !e.IsTerrain);
        if (links.HasLinks) entity = json.Entities.First(e => e.Id == links.Links[0].EntityId);
        Hierarchy.SelectedItem = entity;
        Floors_Click(this, new());
        var window = ds1Window ?? throw new InvalidOperationException("DS1 window not open.");
        if (!links.HasLinks) window.VerifyLinkUnitButton(0);
        if (arguments.Contains("--alignment-smoke"))
        {
            links.AlignUnitToHd(entity);
            var aligned = links.Find(entity)!;
            var actual = ds1.Units[aligned.Unit!.Index];
            // Assert against the link's own scale, not a hardcoded 10 units/tile.
            double perSubtile = aligned.UnitsPerTile / 5;
            if (Math.Abs(actual.X - entity.Transform.Position.X / perSubtile) > .5 || Math.Abs(actual.Y - entity.Transform.Position.Z / perSubtile) > .5)
                throw new InvalidOperationException("Wagon unit alignment does not match HD origin.");
        }
        string collisionResult = arguments.Contains("--collision-smoke") ? window.VerifyOwnedCollisionEditing() : "";
        LinkedTile[] sharedStart = [];
        if (arguments.Contains("--collision-smoke"))
        {
            sharedStart = links.CollisionTiles(entity);
            var other = json.Entities.First(e => e != entity && e.CanTransform && !e.HasParent && !e.IsTerrain && e.PreviewModel is not null && links.Find(e) is null);
            links.LinkFootprint(other, sharedStart, links.Find(entity)!.UnitsPerTile, false);
            if (sharedStart.Any(t => links.InspectOwnership(t).Owners.Length != 2)) throw new InvalidOperationException("Overlap owners were not retained.");
        }
        var link = links.Find(entity) ?? throw new InvalidOperationException("Link button failed.");
        var before = entity.Transform; var mapBefore = ds1.Serialize();
        double delta = arguments.Contains("--collision-smoke") ? link.UnitsPerTile : link.UnitsPerTile / 5;
        Apply(entity, before with { Position = before.Position with { X = before.Position.X + delta } });
        if (sharedStart.Any(t => !ds1.HasOverride(t.X, t.Y))) throw new InvalidOperationException("Moving HD object removed another object's collision.");
        if (mapBefore.SequenceEqual(ds1.Serialize())) throw new InvalidOperationException("Linked DS1 did not move.");
        ds1.Undo();
        if (entity.Transform != before || !ds1.Serialize().SequenceEqual(mapBefore) || PX.Text != before.Position.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture))
            throw new InvalidOperationException("Undo from DS1 did not restore JSON and inspector.");
        Redo_Click(this, new());
        if (entity.Transform == before) throw new InvalidOperationException("Redo failed.");
        if (arguments.Contains("--collision-smoke"))
        {
            window.ReviewLinkedCollision();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            window.UpdateLayout();
            var capture = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            capture.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(capture));
            using var file = File.Create(Path.Combine(output, "collision-workspace.png")); encoder.Save(file);
        }
        window.Close();
        if (ds1Window is not null || !ds1.IsDirty) throw new InvalidOperationException("Closing shared DS1 discarded changes.");
        string exported = links.ExportPair(output);
        await LoadPreset(exported);
        entity = document!.Entities.First(e => e.Id == link.EntityId); Hierarchy.SelectedItem = entity;
        if (placementLinks?.Warning is not null || placementLinks?.HasLinks != true || !Ds1Preview.HasPreview)
            throw new InvalidOperationException("Exported pair did not automatically restore links and DS1 preview. " + pairedStatus + " " + placementLinks?.Warning);
        var reopenedBefore = entity.Transform;
        Apply(entity, reopenedBefore with { Position = reopenedBefore.Position with { X = reopenedBefore.Position.X - delta } });
        document.Undo();
        if (document.IsDirty || pairedScene!.Collision!.Document.IsDirty) throw new InvalidOperationException("Reopened undo did not restore saved pair.");
        Scene.FrameSelected();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, "linked-editor.png"))) png.Save(stream);
        File.WriteAllText(Path.Combine(output, "smoke.txt"), "PASS: DS1 link button, HD linked move, DS1 undo restores JSON inspector, main redo, closing DS1 retains edits, paired export, automatic paired reopen, persisted link movement and undo.\n" + collisionResult + "\nExported JSON: " + exported);
    }
}
