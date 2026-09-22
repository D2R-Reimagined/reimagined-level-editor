using System.Windows;
using System.Windows.Media.Media3D;
using D2RLevel.Core;

namespace D2RLevel.App;

internal static partial class RenderingChecks
{
    private static void GizmoChecks()
    {
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Movement gizmo: " + name); }
        var doc = PresetDocument.ModelPreview("data/hd/gizmo.model");
        var entity = doc.Entities[0];
        var original = entity.Transform;
        var shape = SceneViewport.Placeholder();
        var items = new SceneItem[] { new(entity, shape, true) };
        var batches = SceneBatch.Create(items, CancellationToken.None);
        var view = new SceneViewport { AllowModelDragging = true };
        view.Measure(new Size(800, 600)); view.Arrange(new Rect(0, 0, 800, 600));
        view.SetScene(new(items, [], 0, batches)); view.Select(entity); view.UpdateLayout();
        int commits = 0;
        PresetEntity[]? group = null;
        view.DragCommitted += (e, t) =>
        {
            commits++;
            if (group is null) doc.SetTransform(e, t);
            else GroupMovement.Move(doc, null, group, new(t.Position.X - e.Transform.Position.X,
                t.Position.Y - e.Transform.Position.Y, t.Position.Z - e.Transform.Position.Z));
        };
        Check(view.MovementHandles.Count == 3, "three world axes on editable selection");
        // Exercise both the default and a substantially different camera angle.
        foreach (bool rotated in new[] { false, true })
        {
            if (rotated) view.Look(700, 170, true);
            foreach (var axis in Enum.GetValues<MovementAxis>())
            {
                var handle = view.MovementHandles.Single(h => h.Axis == axis);
                var start = handle.Start + (handle.End - handle.Start) * .65;
                var movement = handle.End - handle.Start; movement.Normalize(); movement *= 36;
                Check(view.HitGizmo(start) == axis && view.HitGizmo(handle.End) == axis, "shaft and arrowhead hit " + axis);
                int previousCommits = commits;
                Check(view.TryBeginGizmoDrag(start), "arrow starts axis drag " + axis);
                view.ContinueDrag(start + movement * .01); view.EndDrag(true);
                Check(commits == previousCommits && entity.Transform == original, "axis click jitter creates no edit");
                Check(view.TryBeginGizmoDrag(start), "axis cancel starts");
                view.ContinueDrag(start + movement);
                Check(entity.Transform == original && batches.All(b => b.Model.Bounds.IsEmpty), "axis preview detaches batches without document mutation");
                view.CancelDrag();
                Check(commits == previousCommits && entity.Transform == original && batches.All(b => !b.Model.Bounds.IsEmpty), "axis cancel restores batches");
                Check(view.TryBeginGizmoDrag(start), "axis commit starts");
                view.ContinueDrag(start + movement); view.EndDrag(true);
                var p = entity.Transform.Position; var o = original.Position;
                Check(commits == previousCommits + 1 && entity.Transform.Orientation == original.Orientation && entity.Transform.Scale == original.Scale,
                    "axis commits once and preserves rotation and scale");
                Check(axis switch
                {
                    MovementAxis.X => p.X > o.X && p.Y == o.Y && p.Z == o.Z,
                    MovementAxis.Y => p.Y > o.Y && p.X == o.X && p.Z == o.Z,
                    _ => p.Z > o.Z && p.X == o.X && p.Y == o.Y
                }, "only chosen coordinate moves in positive arrow direction " + axis);
                var moved = entity.Transform;
                doc.Undo(); view.UpdateEntity(entity);
                Check(entity.Transform == original && !doc.CanUndo, "axis drag is one undo");
                doc.Redo(); view.UpdateEntity(entity);
                Check(entity.Transform == moved, "axis redo restores movement");
                doc.Undo(); view.UpdateEntity(entity);
                Check(view.TryBeginGizmoDrag(start), "negative axis drag starts");
                view.ContinueDrag(start - movement); view.EndDrag(true);
                p = entity.Transform.Position;
                Check(axis switch { MovementAxis.X => p.X < o.X, MovementAxis.Y => p.Y < o.Y, _ => p.Z < o.Z }, "reverse arrow drag moves negative " + axis);
                doc.Undo(); view.UpdateEntity(entity);
            }
        }
        var second = doc.AddModel("data/hd/second.model", new(2, 0, 0));
        view.AddItem(new(second, shape, true)); view.SelectMany([entity, second], entity);
        var secondStart = second.Transform;
        group = [entity, second];
        // Group previews share precisely the same axis delta before the host commits.
        var y = view.MovementHandles.Single(h => h.Axis == MovementAxis.Y);
        var pointer = y.Start + (y.End - y.Start) * .6;
        Check(view.TryBeginGizmoDrag(pointer), "group axis starts");
        view.ContinueDrag(pointer + new Vector(0, -40));
        Check(entity.Transform == original && second.Transform == secondStart, "group preview leaves document untouched");
        view.EndDrag(true);
        Check(entity.Transform.Position.Y != original.Position.Y && Math.Abs(entity.Transform.Position.Y - second.Transform.Position.Y) < 1e-8 &&
            second.Transform.Position.X == secondStart.Position.X && second.Transform.Position.Z == secondStart.Position.Z, "group axis delta shared");
        doc.Undo(); view.UpdateEntities(group);
        Check(entity.Transform == original && second.Transform == secondStart, "single undo restores every group member");
        doc.Redo(); view.UpdateEntities(group);
        Check(entity.Transform.Position.Y == second.Transform.Position.Y && entity.Transform != original, "single redo restores group axis move");
        view.SelectMany([], null);
        Check(view.MovementHandles.Count == 0 && view.HitGizmo(pointer) is null, "deselection removes arrows and hit targets");
        view.Select(entity); view.AllowModelDragging = false;
        Check(view.MovementHandles.Count == 0 && !view.BeginDrag(entity, pointer, MovementAxis.X), "read-only preview has no movement handles");
        view.AllowModelDragging = true; view.Select(entity); view.FrameSelected();
        // Looking almost exactly down X must never turn a tiny pointer delta into a huge move.
        view.SetOrbitPivot(new Point3D(entity.Transform.Position.X, entity.Transform.Position.Y + 1.5, entity.Transform.Position.Z));
        var direction = view.ViewDirection;
        double yaw = Math.Atan2(-direction.X, -direction.Z);
        double pitch = Math.Asin(-direction.Y / direction.Length);
        view.Look((yaw - Math.PI / 2) / .002, -pitch / .002, true);
        Check(view.MovementHandles.All(h => h.Axis != MovementAxis.X) && !view.BeginDrag(entity, pointer, MovementAxis.X), "end-on axis safely disabled");
        view.SetScene(new([], [], 0));
        Check(view.MovementHandles.Count == 0, "scene replacement clears gizmo");
        var npc = PresetEntity.GameplayPreview(0, "NPC", original);
        view.SetScene(new([new(npc, shape, true)], [], 0)); view.Select(npc);
        Check(view.MovementHandles.Select(h => h.Axis).SequenceEqual(new[] { MovementAxis.X, MovementAxis.Z }) &&
            !view.BeginDrag(npc, pointer, MovementAxis.Y), "NPC gizmo respects ground-only movement");
        var terrainDoc = PresetDocument.ModelPreview("data/hd/terrain.model");
        var terrain = terrainDoc.Entities[0];
        terrain.Components.Single(c => c["type"]!.GetValue<string>() == "ModelDefinitionComponent")["type"] = "TerrainDefinitionComponent";
        view.SetScene(new([new(terrain, shape, true)], [], 0)); view.Select(terrain);
        Check(view.MovementHandles.Count == 0 && !view.BeginDrag(terrain, pointer, MovementAxis.X), "locked terrain cannot use gizmo");
        view.TerrainLocked = false;
        Check(view.MovementHandles.Count == 3, "unlock restores terrain gizmo");
        view.SetTerrainVisible(false);
        Check(view.MovementHandles.Count == 0 && !view.BeginDrag(terrain, pointer, MovementAxis.X), "hidden terrain cannot use gizmo");
    }
}
