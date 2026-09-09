using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using D2RLevel.Core;

namespace D2RLevel.App;

internal static class RenderingChecks
{
    public static void Run()
    {
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Batch regression: " + name); }
        var doc = PresetDocument.ModelPreview("data/hd/probe.model"); var entity = doc.Entities[0];
        var shape = SceneViewport.Placeholder();
        var batch = new SceneBatch(shape.Children.Cast<GeometryModel3D>().Select(p => new SceneBatch.Part(entity, p)).ToArray());
        var view = new SceneViewport(); view.Measure(new Size(400, 400)); view.Arrange(new Rect(0, 0, 400, 400));
        view.SetScene(new([new(entity, shape, true)], [], 0, [batch])); view.UpdateLayout();
        Check(view.Pick(new Point(200, 200)) == entity, "batched picking maps to source entity");
        var initial = batch.Model.Bounds;
        var forward = view.ViewDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, new Vector3D(0, 1, 0)); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        Check(right.X > 0 && right.Z < 0 && up.X < 0 && up.Z < 0,
            "default HD camera matches DS1: native X down-right, native Z down-left");
        doc.SetTransform(entity, entity.Transform with { Position = new(10000, 0, 0) });
        view.UpdateEntity(entity);
        Check(Math.Abs(batch.Model.Bounds.X - initial.X - 10000) < 1e-6, "move updates baked geometry");
        Check(view.Pick(new Point(200, 200)) is null, "moved geometry leaves old click location");
        view.FrameSelected(); Check(view.Pick(new Point(200, 200)) == entity, "offscreen moved entity becomes visible when framed");
        doc.Undo(); view.UpdateEntity(entity); view.FrameSelected();
        Check(batch.Model.Bounds == initial && view.Pick(new Point(200, 200)) == entity, "undo restores geometry and hit mapping");
        Check(view.FocusArea(), "focus area supported for batched scenes");
        view.FrameAll(); Check(view.Pick(new Point(200, 200)) == entity, "Home restores whole-scene picking after focus mode");
        var mesh = new MeshGeometry3D { Positions = new Point3DCollection { new(0,0,0), new(1,0,0), new(0,1,0) },
            Normals = new Vector3DCollection { new(0,0,1), new(0,0,1), new(0,0,1) }, TriangleIndices = new Int32Collection { 0,1,2 } };
        var triangle = new GeometryModel3D(mesh, new DiffuseMaterial(Brushes.White)); triangle.Freeze();
        doc.SetTransform(entity, entity.Transform with { Scale = new(-2, 3, 4), Orientation = new(0, Math.Sin(0.3), 0, Math.Cos(0.3)) });
        var mirrored = new SceneBatch([new(entity, triangle)]);
        var baked = (MeshGeometry3D)mirrored.Model.Geometry;
        Check(baked.TriangleIndices.SequenceEqual(new[] { 0,2,1 }), "negative determinant reverses winding");
        var a = baked.Positions[2] - baked.Positions[0]; var b = baked.Positions[1] - baked.Positions[0];
        var expected = Vector3D.CrossProduct(a, b); expected.Normalize();
        Check((baked.Normals[0] - expected).Length < 1e-8, "inverse transpose normals under rotated nonuniform scale");
        var second = PresetDocument.ModelPreview("data/hd/second.model").Entities[0];
        var mapped = new SceneBatch([new(entity, triangle), new(second, triangle)]);
        Check(mapped.EntityAt(2) == entity && mapped.EntityAt(3) == second && mapped.EntityAt(6) is null, "vertex ranges identify separate entities");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool cancellationObserved = false;
        try { _ = new SceneBatch([new(entity, triangle)], canceled.Token); }
        catch (OperationCanceledException) { cancellationObserved = true; }
        Check(cancellationObserved, "batch construction observes cancellation");
        view.SetScene(new([], [], 0)); Check(view.Pick(new Point(200, 200)) is null, "scene replacement clears batch picking");
        CameraChecks();
        PlacementChecks();
    }

    private static void PlacementChecks()
    {
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Placement regression: " + name); }
        var doc = PresetDocument.ModelPreview("data/hd/drag.model");
        var entity = doc.Entities[0]; var original = entity.Transform;
        var shape = SceneViewport.Placeholder();
        var batch = new SceneBatch(shape.Children.Cast<GeometryModel3D>().Select(p => new SceneBatch.Part(entity, p)).ToArray());
        var view = new SceneViewport { AllowModelDragging = true };
        view.Measure(new Size(400, 400)); view.Arrange(new Rect(0, 0, 400, 400));
        view.SetScene(new([new(entity, shape, true)], [], 0, [batch])); view.UpdateLayout();
        int commits = 0;
        view.DragCommitted += (e, t) => { commits++; doc.SetTransform(e, t); };
        Check(view.BeginDrag(entity, new(200, 200)), "begin ground-plane drag");
        view.ContinueDrag(new(201, 201)); view.EndDrag(true);
        Check(commits == 0 && !doc.IsDirty, "click jitter produces no undo command");
        var initialBounds = batch.Model.Bounds;
        view.BeginDrag(entity, new(200, 200)); view.ContinueDrag(new(245, 220));
        Check(batch.Model.Bounds.IsEmpty && entity.Transform == original && !doc.IsDirty, "drag detaches geometry without editing JSON");
        var previewBatch = batch.Model;
        view.ContinueDrag(new(255, 230));
        Check(ReferenceEquals(previewBatch, batch.Model), "pointer movement does not rebuild town batches");
        view.CancelDrag();
        Check(batch.Model.Bounds == initialBounds && entity.Transform == original && commits == 0, "cancel restores batch and document");
        var anchor = view.GroundPoint(new(200, 200), 0)!.Value;
        var destination = view.GroundPoint(new(245, 220), 0)!.Value;
        view.BeginDrag(entity, new(200, 200)); view.ContinueDrag(new(245, 220)); view.EndDrag(true);
        Check(commits == 1 && entity.Transform.Position.Y == original.Position.Y && entity.Transform.Orientation == original.Orientation && entity.Transform.Scale == original.Scale,
            "drag commits once and preserves height rotation scale");
        Check(Math.Abs(entity.Transform.Position.X - destination.X + anchor.X) < 1e-8 && Math.Abs(entity.Transform.Position.Z - destination.Z + anchor.Z) < 1e-8,
            "screen drag respects native X and ground Z");
        doc.Undo(); view.UpdateEntity(entity);
        Check(entity.Transform == original && !doc.CanUndo && batch.Model.Bounds == initialBounds, "one undo restores complete drag");
        var added = doc.AddModel("data/hd/new.model", new(20, 0, 0));
        var item = new SceneItem(added, shape, false);
        view.AddItem(item); view.Select(added); view.FrameSelected();
        Check(view.Pick(new(200, 200)) == added, "new model is immediately pickable");
        // Reload folds the inserted model into batches. Undo must still hide it permanently.
        var items = new SceneItem[] { new(entity, shape, false), item };
        view.SetScene(new(items, [], 2, SceneBatch.Create(items, CancellationToken.None)));
        view.Select(added); view.FrameSelected();
        doc.Undo(); view.RemoveItem(added); view.UpdateEntity(entity);
        Check(view.Pick(new(200, 200)) is null, "undo insertion after reload stays hidden when shared batches rebuild");
        doc.Redo(); view.AddItem(item); view.UpdateEntity(added);
        Check(view.Pick(new(200, 200)) == added, "redo insertion after reload restores batch picking");
        view.BeginDrag(added, new(200, 200)); view.ContinueDrag(new(250, 225));
        view.SetScene(new([], [], 0));
        Check(added.Transform.Position == new Vector3d(20, 0, 0) && view.Pick(new(200, 200)) is null, "scene replacement cancels active drag");
    }

    private static void CameraChecks()
    {
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Camera regression: " + name); }
        bool Near(Point3D a, Point3D b) => (a - b).Length < 1e-7;
        var doc = PresetDocument.ModelPreview("data/hd/camera-probe.model");
        var originalBytes = doc.Serialize();
        var view = new SceneViewport(); view.Measure(new Size(800, 600)); view.Arrange(new Rect(0, 0, 800, 600));
        view.SetScene(new([new(doc.Entities[0], SceneViewport.Placeholder(), true)], [], 0));
        var homePosition = view.CameraPosition; var homeDirection = view.ViewDirection;
        view.Look(80, -30);
        Check(Near(view.CameraPosition, homePosition) && (view.ViewDirection - homeDirection).Length > 0.1, "look rotates without changing camera position");
        var forward = view.ViewDirection; forward.Normalize();
        view.Dolly(1); var step = view.CameraPosition - homePosition;
        Check(Vector3D.DotProduct(step, forward) > 0 && Vector3D.CrossProduct(step, forward).Length < 1e-7, "wheel moves along current view direction");
        view.Dolly(-1); Check(Near(view.CameraPosition, homePosition), "reverse wheel returns to position");
        view.Dolly(1, 4); Check((view.CameraPosition - homePosition - step * 4).Length < 1e-7, "fast modifier scales travel");
        view.Dolly(-1, 4); view.Dolly(1, 0.2);
        Check((view.CameraPosition - homePosition - step * 0.2).Length < 1e-7, "precision modifier scales travel");
        view.Dolly(-1, 0.2); view.Dolly(2000);
        Check(Vector3D.DotProduct(view.CameraPosition - homePosition, forward) > homeDirection.Length, "wheel passes old focus point without slowing or reversing");
        var traveled = view.CameraPosition; view.Look(-45, 10);
        Check(Near(view.CameraPosition, traveled), "look stays at newly traveled position");
        forward = view.ViewDirection; forward.Normalize(); var position = view.CameraPosition;
        view.Pan(20, -10);
        Check(Math.Abs(Vector3D.DotProduct(view.CameraPosition - position, forward)) < 1e-7 && (view.ViewDirection.Normalized() - forward).Length < 1e-7, "pan translates in camera plane without rotating");
        var pivot = new Point3D(0, 1.5, 0); position = view.CameraPosition;
        view.SetOrbitPivot(pivot); Check(Near(position, view.CameraPosition), "orbit pivot selection does not teleport");
        double radius = (position - pivot).Length;
        view.Look(100, 20, true);
        Check(Math.Abs((view.CameraPosition - pivot).Length - radius) < 1e-7 && !Near(view.CameraPosition, position), "explicit orbit preserves pivot radius");
        view.Look(0, 100000);
        Check(double.IsFinite(view.ViewDirection.Y) && Vector3D.CrossProduct(view.ViewDirection, new Vector3D(0, 1, 0)).Length > 0.001, "vertical look remains finite and avoids pole singularity");
        view.FrameAll();
        Check(Near(view.CameraPosition, homePosition) && (view.ViewDirection - homeDirection).Length < 1e-7, "Home restores predictable overview");
        Check(doc.Serialize().SequenceEqual(originalBytes), "camera navigation never changes preset data");
    }

    private static Vector3D Normalized(this Vector3D value) { value.Normalize(); return value; }
}
