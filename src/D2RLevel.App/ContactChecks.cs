using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using D2RLevel.Core;

namespace D2RLevel.App;

internal static partial class RenderingChecks
{
    private static void ContactChecks()
    {
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Contact indicator: " + name); }
        // A 2×2×2 block standing on its origin, like a floor, wall or roof piece.
        var mesh = new MeshGeometry3D();
        foreach (double x in new[] { -1.0, 1 }) foreach (double y in new[] { 0.0, 2 }) foreach (double z in new[] { -1.0, 1 }) mesh.Positions.Add(new(x, y, z));
        foreach (int i in new[] { 0,1,3,0,3,2, 4,6,7,4,7,5, 0,4,5,0,5,1, 2,3,7,2,7,6, 0,2,6,0,6,4, 1,5,7,1,7,3 }) mesh.TriangleIndices.Add(i);
        var block = new Model3DGroup(); block.Children.Add(new GeometryModel3D(mesh, new DiffuseMaterial(Brushes.Gray))); block.Freeze();
        var doc = PresetDocument.ModelPreview("data/hd/floor.model");
        var floor = doc.Entities[0];
        var roof = doc.AddModel("data/hd/roof.model", new(0, 2.02, 0));
        var wall = doc.AddModel("data/hd/wall.model", new(2, 0, 0));
        SceneItem[] items = [new(floor, block, false), new(roof, block, false), new(wall, block, false)];
        var view = new SceneViewport { AllowModelDragging = true };
        view.Measure(new Size(800, 600)); view.Arrange(new Rect(0, 0, 800, 600));
        view.SetScene(new(items, [], 0, SceneBatch.Create(items, CancellationToken.None)));
        int commits = 0;
        view.DragCommitted += (e, t) => { commits++; doc.SetTransform(e, t); };
        view.SelectMany([roof], roof); view.FrameSelected(); view.UpdateLayout();

        var lift = view.MovementHandles.Single(h => h.Axis == MovementAxis.Y);
        var grip = lift.Start + (lift.End - lift.Start) * .6;
        var up = lift.End - lift.Start; up.Normalize();
        Check(view.TryBeginGizmoDrag(grip), "roof Y drag starts");
        view.ContinueDrag(grip - up * 6, snap: false);
        Check(view.Support is { Other: var under, Gap: < 0 } && under == floor, "unsnapped roof reports how far it sinks into the floor");
        Check(!view.Contacts.Any(c => c.Kind == ContactKind.Touching && c.Other == floor), "a roof sunk into the floor is not touching it");
        view.ContinueDrag(grip - up * 6, snap: true);
        Check(view.Support is { Other: var below, Gap: var gap } && below == floor && Math.Abs(gap) < 1e-9, "roof snaps onto the floor top");
        Check(view.Contacts.Any(c => c is { Kind: ContactKind.Touching, Axis: MovementAxis.Y } && c.Other == floor && Math.Abs(c.Plane - 2) < 1e-9),
            "floor top is the touching face");
        Check(view.ContactFaceCount > 0 && view.ContactLabel is not null, "touching face and height label are drawn on screen");
        view.EndDrag(true);
        Check(commits == 1 && Math.Abs(roof.Transform.Position.Y - 2) < 1e-9 && roof.Transform.Position.X == 0 && roof.Transform.Position.Z == 0,
            "snapped height is what commits, on the dragged axis only");
        Check(view.Contacts.Count == 0 && view.Support is null && view.ContactFaceCount == 0 && view.ContactLabel is null, "release clears the indicator and its label");

        // The wall already stands flush against the floor; a small ground drag holds it there.
        view.SelectMany([wall], wall);
        var center = view.Project(new Point3D(2, 1, 0))!.Value;
        Check(view.BeginDrag(wall, center), "wall ground drag starts");
        view.ContinueDrag(center + new Vector(6, 3), snap: true);
        Check(view.Contacts.Any(c => c is { Kind: ContactKind.Touching, Axis: MovementAxis.X } && c.Other == floor && Math.Abs(c.Plane - 1) < 1e-9),
            "wall side touches the floor side");
        Check(view.Contacts.Any(c => c is { Kind: ContactKind.Touching, Axis: MovementAxis.Y, Other: null }) && view.Support is { Other: null },
            "wall base rests on the ground plane");
        Check(view.Contacts.Any(c => c is { Kind: ContactKind.Aligned, Axis: MovementAxis.Z } && c.Other == floor), "wall front lines up with the floor front");
        view.CancelDrag();
        Check(view.Contacts.Count == 0 && view.ContactLabel is null && commits == 1 && wall.Transform.Position.X == 2, "cancel clears the indicator without an edit");

        // Placeholders stand in for missing art, so their boxes never snap or report contact.
        var proxy = doc.AddModel("data/hd/missing.model", new(0, 4.03, 0));
        view.AddItem(new(proxy, SceneViewport.Placeholder(), true)); view.SelectMany([proxy], proxy);
        lift = view.MovementHandles.Single(h => h.Axis == MovementAxis.Y);
        grip = lift.Start + (lift.End - lift.Start) * .6;
        Check(view.TryBeginGizmoDrag(grip), "placeholder drag starts");
        view.ContinueDrag(grip - up * 6, snap: true);
        Check(view.Contacts.Count == 0 && view.Support is null, "placeholder drags show no contact");
        view.CancelDrag();
    }
}
