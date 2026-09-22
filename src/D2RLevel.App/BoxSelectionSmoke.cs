using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private async Task VerifyBoxSelection(string output)
    {
        Directory.CreateDirectory(output);
        RenderingChecks.Run();
        // Group metadata tests write only to the disposable pair.
        var jsonPath = Path.Combine(output, "data", "hd", "env", "preset", "act1", "town", "towns1.json");
        var ds1Path = Path.Combine(output, "data", "global", "tiles", "act1", "town", "towns1.ds1");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!); Directory.CreateDirectory(Path.GetDirectoryName(ds1Path)!);
        File.WriteAllBytes(jsonPath, document!.Serialize()); File.WriteAllBytes(ds1Path, pairedScene!.Collision!.Document.Serialize());
        await LoadPreset(jsonPath);
        var doc = document!; var bytes = doc.Serialize(); var ds1Bytes = pairedScene!.Collision!.Document.Serialize();
        var anchor = doc.Entities.First(e => GroupMovement.CanGroup(e) && e.Name.Contains("campfire", StringComparison.OrdinalIgnoreCase));
        SetSelection([anchor]); Scene.FrameSelected(); Scene.FocusArea();
        UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        double width = Scene.ActualWidth, height = Scene.ActualHeight;
        Point? start = null; Rect rectangle = Rect.Empty;
        foreach (double x in new[] { .15, .05, .25, .35 })
            foreach (double y in new[] { .15, .05, .25, .35 })
            {
                var point = new Point(width * x, height * y);
                var candidate = new Rect(point, new Point(width * .85, height * .85));
                if (start is null && Scene.Pick(point) is null && Scene.HitGizmo(point) is null && Scene.PickRegion(candidate).Length >= 2)
                { start = point; rectangle = candidate; }
            }
        if (start is null) throw new InvalidOperationException("No empty drag origin in fixture view.");
        var hits = Scene.PickRegion(rectangle);
        if (hits.Any(Scene.IsLocked)) throw new InvalidOperationException("Box picked locked terrain.");
        if (!Scene.BeginLeftInteraction(start.Value, ModifierKeys.None)) throw new InvalidOperationException("Box did not start.");
        Scene.ContinueMarquee(rectangle.BottomRight);
        if (!SelectedEntities.SequenceEqual([anchor])) throw new InvalidOperationException("Preview changed selection before release.");
        await Capture("box-preview.png");
        if (Scene.SelectionRectangle is null) throw new InvalidOperationException("Fixture layout canceled the selection preview.");
        Scene.EndMarquee(true);
        AssertSelection(hits, "replace");
        await Capture("box-selected.png");
        var outside = doc.Entities.First(e => GroupMovement.CanGroup(e) && !hits.Contains(e) && Scene.GetItem(e) is not null);
        SetSelection([outside]);
        Drag(ModifierKeys.Shift); AssertSelection(hits.Append(outside), "Shift adds");
        Drag(ModifierKeys.Control); AssertSelection([outside], "Ctrl toggles existing hits off");
        Drag(ModifierKeys.Control); AssertSelection(hits.Append(outside), "Ctrl toggles new hits on");
        Scene.BeginLeftInteraction(start.Value, ModifierKeys.Control); Scene.EndMarquee(true);
        AssertSelection(hits.Append(outside), "modified empty click preserves selection");
        Scene.BeginLeftInteraction(start.Value, ModifierKeys.None); Scene.ContinueMarquee(rectangle.BottomRight); Scene.CancelDrag();
        AssertSelection(hits.Append(outside), "cancel preserves selection");
        Scene.BeginLeftInteraction(start.Value, ModifierKeys.None); Scene.EndMarquee(true);
        AssertSelection([], "empty click deselects");
        var grouped = hits.First(GroupMovement.CanGroup);
        assetGroups!.Create("Box selection fixture", [grouped, outside]);
        Drag(ModifierKeys.None); AssertSelection(hits.Append(outside), "box expands saved group");
        if (!doc.Serialize().SequenceEqual(bytes) || !pairedScene.Collision.Document.Serialize().SequenceEqual(ds1Bytes) || doc.CanUndo)
            throw new InvalidOperationException("Box selection changed scene data or edit history.");
        File.WriteAllText(Path.Combine(output, "smoke.txt"),
            "PASS synthetic box selection: projected batched bounds, partial overlap, reverse drag, modifier start over objects, click jitter, deselection, cancel/lost capture, scene replacement, hidden/locked terrain, near-plane clipping, unchanged scene data and existing movement/gizmo/duplication checks.\n" +
            $"PASS real scene: {hits.Length} box hits, selection rectangle render, hierarchy/outline synchronization, replace/add/toggle, empty-click deselection, cancellation, saved group expansion and no JSON/DS1/history edits. Programmatic WPF interaction, not OS mouse input.\n");

        void Drag(ModifierKeys modifiers)
        {
            if (!Scene.BeginLeftInteraction(start.Value, modifiers)) throw new InvalidOperationException("Box failed to restart.");
            Scene.ContinueMarquee(rectangle.BottomRight); Scene.EndMarquee(true);
        }
        void AssertSelection(IEnumerable<PresetEntity> expected, string name)
        {
            if (!SelectedEntities.ToHashSet().SetEquals(expected)) throw new InvalidOperationException("Real box selection: " + name);
        }
        async Task Capture(string filename)
        {
            UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            double scale = Math.Min(1, 1800 / ActualWidth);
            var bitmap = new RenderTargetBitmap((int)(ActualWidth * scale), (int)(ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(this); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, filename)); png.Save(file);
        }
    }
}
