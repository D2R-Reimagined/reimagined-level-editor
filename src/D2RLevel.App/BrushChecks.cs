using System.Windows;
using System.Windows.Media.Media3D;
using D2RLevel.Core;

namespace D2RLevel.App;

internal static partial class RenderingChecks
{
    /// <summary>Tileset palette painting on a synthetic viewport: snapping, rows along the dominant axis, rotation, terrain height and cancellation.</summary>
    private static void BrushChecks()
    {
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Brush: " + name); }
        var doc = PresetDocument.ModelPreview("data/hd/anchor.model"); var anchor = doc.Entities[0];
        var shape = SceneViewport.Placeholder();
        var view = new SceneViewport { AllowModelDragging = true };
        view.Measure(new Size(1200, 900)); view.Arrange(new Rect(0, 0, 1200, 900));
        view.SetScene(new([new(anchor, shape, false)], [], 1)); view.FocusOn(50, 50, 120); view.UpdateLayout();
        int commits = 0; EntityTransform[] committed = [];
        view.BrushCommitted += t => { commits++; committed = t; };
        Point At(double x, double z) => view.Project(new Point3D(x, 0, z))!.Value;
        view.Brush = new ModelBrush("data/hd/wall.model", shape, new(0, 0, 0, 1), 10);
        var (sizeX, sizeZ) = view.BrushSize();
        Check(Math.Abs(sizeX - 2) < .05 && Math.Abs(sizeZ - 2) < .05, "brush size comes from the model bounds");
        view.UpdateBrush(At(53, 47));
        Check(view.BrushPlan.Count == 1 && view.BrushPlan[0].Position == new Vector3d(50, 0, 50), "the hovering ghost snaps to the grid");
        Check(view.BeginBrush(At(51, 49)) && view.BrushStrokeActive, "a click starts a stroke");
        view.UpdateBrush(At(82, 50));
        Check(view.BrushPlan.Select(t => t.Position.X).SequenceEqual(new[] { 50d, 60, 70, 80 }) && view.BrushPlan.All(t => t.Position.Z == 50),
            "a drag lays a row in whole grid steps along X");
        view.UpdateBrush(At(50, 29));
        Check(view.BrushPlan.Select(t => t.Position.Z).SequenceEqual(new[] { 50d, 40, 30 }) && view.BrushPlan.All(t => t.Position.X == 50),
            "the row follows the dominant axis, including toward negative Z");
        view.EndBrush(true);
        Check(commits == 1 && committed.Length == 3 && !view.BrushStrokeActive, "release commits the row once");
        view.BeginBrush(At(50, 50)); view.UpdateBrush(At(90, 50)); view.CancelDrag();
        Check(commits == 1 && !view.BrushStrokeActive, "a cancelled stroke places nothing");
        ModelBrush? turned = null; view.BrushRotated += b => turned = b;
        view.RotateBrush();
        Check(turned is { } t && Math.Abs(t.Orientation.Y - Math.Sin(Math.PI / 4)) < 1e-9 && view.Brush == t, "R turns the brush a quarter turn about Y");
        view.Brush = new ModelBrush("data/hd/wall.model", shape, new(0, 0, 0, 1), 0);
        view.BeginBrush(At(50, 50)); view.UpdateBrush(At(56.2, 50));
        Check(view.BrushPlan.Count == 4 && Math.Abs(view.BrushPlan[1].Position.X - view.BrushPlan[0].Position.X - sizeX) < 1e-6, "without snapping, pieces step one model width");
        view.EndBrush(false);
        view.GroundHeight = (_, _) => 5; view.UpdateBrush(At(50, 50));
        Check(view.BrushPlan.Count == 1 && view.BrushPlan[0].Position.Y == 5, "pieces sit at the terrain height");
        view.GroundHeight = null;
        view.Select(anchor);
        Check(view.MovementHandles.Count == 0, "painting hides the movement gizmo");
        view.Brush = null; view.Select(anchor);
        Check(view.MovementHandles.Count > 0 && view.BrushPlan.Count == 0, "clearing the brush restores the gizmo and clears the ghost");
    }
}
