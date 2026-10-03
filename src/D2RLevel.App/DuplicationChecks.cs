using System.Windows;
using System.Windows.Media.Media3D;
using D2RLevel.Core;

namespace D2RLevel.App;

internal static partial class RenderingChecks
{
    private static void DuplicationRenderingChecks()
    {
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Duplicate drag: " + name); }
        var doc = PresetDocument.ModelPreview("data/hd/repeat.model"); var entity = doc.Entities[0];
        doc.SetTransform(entity, new(new(2, 3, 4), new(0, Math.Sin(.35), 0, Math.Cos(.35)), new(-2, 3, 4)));
        var original = entity.Transform; var shape = SceneViewport.Placeholder();
        var items = new SceneItem[] { new(entity, shape, false) };
        var batches = SceneBatch.Create(items, CancellationToken.None);
        var view = new SceneViewport { AllowModelDragging = true };
        view.Measure(new Size(1200, 900)); view.Arrange(new Rect(0, 0, 1200, 900));
        view.SetScene(new(items, [], 1, batches)); view.Select(entity); view.UpdateLayout();
        int commits = 0, moves = 0; Vector3d committedStep = default; int committedCount = 0;
        view.DragCommitted += (_, _) => moves++;
        view.DuplicationCommitted += (sources, step, count) => { commits++; committedStep = step; committedCount = count; };
        var bounds = SceneViewport.Transform(original).TransformBounds(shape.Bounds);
        var center = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        foreach (var axis in Enum.GetValues<MovementAxis>())
        {
            view.FrameSelected(); view.Dolly(-30);
            double spacing = axis switch { MovementAxis.X => bounds.SizeX, MovementAxis.Y => bounds.SizeY, _ => bounds.SizeZ };
            var direction = axis switch { MovementAxis.X => new Vector3D(1, 0, 0), MovementAxis.Y => new Vector3D(0, 1, 0), _ => new Vector3D(0, 0, 1) };
            var start = view.Project(center)!.Value;
            Point Pointer(double steps) => view.Project(center + direction * (spacing * steps))!.Value;
            int previous = commits;
            Check(view.BeginDrag(entity, start, axis, true), "starts on " + axis);
            Check(Math.Abs(view.DuplicateSpacing - spacing) < 1e-8, "spacing includes rotation and negative/nonuniform scale");
            view.ContinueDrag(Pointer(.6)); view.EndDrag(true);
            Check(commits == previous, "less than one width creates nothing");
            view.BeginDrag(entity, start, axis, true); view.ContinueDrag(Pointer(3.1));
            Check(view.DuplicatePreviewCount == 3 && view.DuplicatePreviewGeometry is Model3DGroup { Children.Count: 3 }, "fills three intervals");
            Check(entity.Transform == original && batches.All(b => !b.Model.Bounds.IsEmpty), "original stays in document and batch during copy preview");
            view.ContinueDrag(Pointer(1.1)); Check(view.DuplicatePreviewCount == 1, "dragging back shrinks row");
            view.ContinueDrag(Pointer(-2.1)); Check(view.DuplicatePreviewCount == 2, "crossing origin fills negative axis");
            view.EndDrag(true);
            var expectedStep = -direction * spacing;
            Check(commits == previous + 1 && moves == 0 && committedCount == 2 &&
                committedStep == new Vector3d(expectedStep.X, expectedStep.Y, expectedStep.Z), "release commits row once without moving source");
            Check(view.DuplicatePreviewCount == 0 && view.DuplicatePreviewGeometry is null, "commit clears previews");
            view.BeginDrag(entity, start, axis, true); view.ContinueDrag(Pointer(2.1)); view.CancelDrag();
            Check(commits == previous + 1 && view.DuplicatePreviewGeometry is null && entity.Transform == original, "cancel discards preview without edits");
        }
        var second = doc.AddModel("data/hd/second.model", new(25, 0, 0));
        view.AddItem(new(second, shape, false)); view.SelectMany([entity, second], entity); view.FrameSelected(); view.Dolly(-30);
        var combined = bounds; combined.Union(SceneViewport.Transform(second.Transform).TransformBounds(shape.Bounds));
        var anchor = view.Project(center)!.Value;
        Check(view.BeginDrag(entity, anchor, MovementAxis.X, true) && Math.Abs(view.DuplicateSpacing - combined.SizeX) < 1e-8, "group spacing uses combined extent");
        view.ContinueDrag(view.Project(center + new Vector3D(-combined.SizeX * 2.1, 0, 0))!.Value);
        Check(view.DuplicatePreviewCount == 4, "group repeats complete selection per interval");
        view.ContinueDrag(view.Project(center + new Vector3D(-combined.SizeX * 1000, 0, 0))!.Value);
        Check(view.DuplicatePreviewCount == PresetDocument.MaximumDuplicateModels, "large group drag respects total model cap");
        int beforeLoss = commits;
        view.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
            { RoutedEvent = System.Windows.Input.Mouse.LostMouseCaptureEvent });
        Check(view.DuplicatePreviewCount == 0 && view.DuplicatePreviewGeometry is null && commits == beforeLoss, "lost capture discards copies");
        view.BeginDrag(entity, anchor, MovementAxis.X, true);
        view.ContinueDrag(view.Project(center + new Vector3D(-combined.SizeX * 2.1, 0, 0))!.Value);
        view.SetScene(new([], [], 0));
        Check(view.DuplicatePreviewCount == 0 && view.DuplicatePreviewGeometry is null, "scene replacement clears copy preview");
        view.SetScene(new([new(entity, shape, true)], [], 0)); view.Select(entity);
        Check(!view.BeginDrag(entity, new(600, 450), MovementAxis.X, true), "missing model bounds cannot define repeat spacing");
        view.SetScene(new(items, [], 1)); view.Select(entity);
        Check(!view.BeginDrag(entity, new(600, 450), null, true), "Alt duplication requires an axis");

        // DS1 units (NPCs, monsters, critters) repeat on whole subtiles, even when shown as a marker.
        var unit = PresetEntity.GameplayPreview(3, "cow", new(new(8, 0, 8), new(0, 0, 0, 1), new(1, 1, 1)));
        view.SetScene(new([new(unit, shape, true)], [], 0)); view.Select(unit); view.FrameSelected(); view.Dolly(-30);
        var unitCenter = new Point3D(8, 1.5, 8);
        Check(!view.BeginDrag(unit, view.Project(unitCenter)!.Value, MovementAxis.X, true), "unit copies need the subtile grid");
        view.UnitSubtileSize = () => 0.8;
        Check(!view.BeginDrag(unit, view.Project(unitCenter)!.Value, MovementAxis.Y, true), "unit copies stay on the ground");
        foreach (var axis in new[] { MovementAxis.X, MovementAxis.Z })
        {
            var direction = axis == MovementAxis.X ? new Vector3D(1, 0, 0) : new Vector3D(0, 0, 1);
            int previous = commits;
            Check(view.BeginDrag(unit, view.Project(unitCenter)!.Value, axis, true), "unit starts on " + axis);
            // The 2-unit marker is 2.5 subtiles wide, so copies step a whole 3 subtiles.
            Check(Math.Abs(view.DuplicateSpacing - 2.4) < 1e-9, "unit spacing rounds up to whole subtiles");
            view.ContinueDrag(view.Project(unitCenter + direction * (2.4 * 2.2))!.Value);
            Check(view.DuplicatePreviewCount == 2, "unit row previews two copies");
            view.EndDrag(true);
            var expected = direction * 2.4;
            Check(commits == previous + 1 && committedCount == 2 && moves == 0 && Math.Abs(committedStep.X - expected.X) < 1e-9 &&
                committedStep.Y == 0 && Math.Abs(committedStep.Z - expected.Z) < 1e-9, "unit row commits once with a subtile step");
        }
    }
}
