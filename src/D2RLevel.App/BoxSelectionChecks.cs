using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using D2RLevel.Core;

namespace D2RLevel.App;

internal static partial class RenderingChecks
{
    private static void BoxSelectionChecks()
    {
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Box selection: " + name); }
        var doc = PresetDocument.ModelPreview("data/hd/box.model"); var a = doc.Entities[0];
        var b = doc.AddModel("data/hd/box2.model", new(15, 0, 0));
        var c = doc.AddModel("data/hd/box3.model", new(30, 0, 0));
        var shape = SceneViewport.Placeholder();
        var items = new SceneItem[] { new(a, shape, false), new(b, shape, false), new(c, shape, false) };
        var view = new SceneViewport { AllowModelDragging = true };
        view.Measure(new Size(900, 700)); view.Arrange(new Rect(0, 0, 900, 700));
        view.SetScene(new(items, [], 3, SceneBatch.Create(items, CancellationToken.None))); view.UpdateLayout();
        var before = doc.Serialize(); int changes = 0, clicks = 0, moves = 0;
        PresetEntity[] selected = []; ModifierKeys modifiers = ModifierKeys.None;
        view.RegionSelected += (entities, keys) => { selected = entities; modifiers = keys; changes++; };
        view.EntitySelected += _ => clicks++;
        view.DragCommitted += (_, _) => moves++;
        Rect Screen(PresetEntity entity) => view.ProjectBounds(SceneViewport.Transform(entity.Transform).TransformBounds(shape.Bounds))!.Value;
        var box = Screen(b); box.Inflate(5, 5);
        Check(view.PickRegion(box).SequenceEqual([b]), "projected bounds identify individual batched entity");
        var crossing = new Rect(box.Left + 3, box.Top + box.Height / 2, 4, 4);
        Check(view.PickRegion(crossing).Contains(b), "partly intersecting bounds selected");
        Check(view.BeginLeftInteraction(box.TopLeft, ModifierKeys.None), "empty-space drag starts selection");
        view.ContinueMarquee(box.BottomRight);
        Check(view.SelectionRectangle == box && changes == 0, "rectangle previews without changing selection");
        view.EndMarquee(true);
        Check(changes == 1 && selected.SequenceEqual([b]) && modifiers == ModifierKeys.None && view.SelectionRectangle is null, "release selects box contents once");
        view.BeginLeftInteraction(box.BottomRight, ModifierKeys.Shift); view.ContinueMarquee(box.TopLeft); view.EndMarquee(true);
        Check(selected.SequenceEqual([b]) && modifiers == ModifierKeys.Shift, "reverse drag and additive modifier preserved");
        view.BeginLeftInteraction(new(0, 0), ModifierKeys.Control); view.ContinueMarquee(new(900, 700)); view.EndMarquee(true);
        Check(selected.Length == 3 && modifiers == ModifierKeys.Control, "full viewport selects all batched models");
        var point = view.Project(new Point3D(15, 1.5, 0))!.Value;
        Check(view.Pick(point) == b, "fixture object is pickable");
        view.BeginLeftInteraction(point, ModifierKeys.Shift); view.ContinueMarquee(point + new Vector(20, 20));
        Check(view.SelectionRectangle is not null, "modifier starts box over object");
        view.CancelDrag();
        int previous = changes;
        view.BeginLeftInteraction(point, ModifierKeys.Control); view.ContinueMarquee(point + new Vector(1, 1)); view.EndMarquee(true);
        Check(clicks == 1 && changes == previous, "modifier click jitter retains individual click selection");
        view.BeginLeftInteraction(new(0, 0), ModifierKeys.None); view.ContinueMarquee(new(1, 1)); view.EndMarquee(true);
        Check(selected.Length == 0 && modifiers == ModifierKeys.None, "empty click clears selection");
        previous = changes;
        view.BeginLeftInteraction(new(0, 0), ModifierKeys.None); view.ContinueMarquee(new(900, 700)); view.CancelDrag();
        Check(changes == previous && view.SelectionRectangle is null, "Escape/cancel preserves selection");
        view.BeginLeftInteraction(new(0, 0), ModifierKeys.None); view.ContinueMarquee(new(900, 700));
        view.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });
        Check(changes == previous && view.SelectionRectangle is null, "lost capture cancels box");
        view.BeginLeftInteraction(point, ModifierKeys.None); view.ContinueDrag(point + new Vector(20, 20)); view.EndDrag(true);
        Check(moves == 1 && view.SelectionRectangle is null, "ordinary object drag still moves");
        view.Select(b); var handle = view.MovementHandles.Single(h => h.Axis == MovementAxis.Y);
        view.BeginLeftInteraction(handle.Start + (handle.End - handle.Start) * .7, ModifierKeys.None);
        view.ContinueDrag(handle.End + new Vector(0, -20)); view.EndDrag(true);
        Check(moves == 2 && view.SelectionRectangle is null, "gizmo takes precedence over box selection");
        view.RemoveItem(b); Check(!view.PickRegion(new(0, 0, 900, 700)).Contains(b), "removed batched entity stays excluded");
        var forward = view.ViewDirection; forward.Normalize();
        var behind = view.CameraPosition - forward * 100;
        Check(view.ProjectBounds(new(behind.X - 1, behind.Y - 1, behind.Z - 1, 2, 2, 2)) is null, "behind-camera bounds never select");
        var eye = view.CameraPosition;
        Check(view.ProjectBounds(new(eye.X - 1, eye.Y - 1, eye.Z - 1, 2, 2, 2)) is { } clipped &&
            double.IsFinite(clipped.Width) && double.IsFinite(clipped.Height), "near-plane crossing clips to finite viewport bounds");
        var terrain = PresetDocument.ModelPreview("data/hd/terrain.model").Entities[0];
        terrain.Components.Single(n => n["type"]!.GetValue<string>() == "ModelDefinitionComponent")["type"] = "TerrainDefinitionComponent";
        view.SetScene(new([new(terrain, shape, false)], [], 1));
        var all = new Rect(0, 0, 900, 700);
        Check(view.PickRegion(all).Length == 0, "locked terrain excluded");
        view.TerrainLocked = false; Check(view.PickRegion(all).SequenceEqual([terrain]), "unlocked visible terrain can select");
        view.SetTerrainVisible(false); Check(view.PickRegion(all).Length == 0, "hidden terrain excluded");
        view.BeginLeftInteraction(new(0, 0), ModifierKeys.None); view.ContinueMarquee(new(500, 500));
        previous = changes; view.SetScene(new([], [], 0));
        Check(changes == previous && view.SelectionRectangle is null, "scene replacement cancels pending box");
        Check(doc.Serialize().SequenceEqual(before), "selection and preview gestures never edit document");
    }
}
