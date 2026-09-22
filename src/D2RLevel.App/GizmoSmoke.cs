using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private async Task VerifyMovementGizmo(string output)
    {
        Directory.CreateDirectory(output);
        RenderingChecks.Run();
        var doc = document ?? throw new InvalidOperationException("Load a preset for gizmo smoke.");
        var entity = doc.Entities.First(e => GroupMovement.CanGroup(e) && e.Name.Contains("campfire", StringComparison.OrdinalIgnoreCase));
        SetSelection([entity]); Scene.FrameSelected();
        await Capture("gizmo.png");
        foreach (bool grouped in new[] { false, true })
        {
            var members = grouped ? doc.Entities.Where(e => GroupMovement.CanGroup(e) && Scene.GetItem(e) is not null)
                .OrderBy(e => Math.Pow(e.Transform.Position.X - entity.Transform.Position.X, 2) + Math.Pow(e.Transform.Position.Z - entity.Transform.Position.Z, 2))
                .Take(3).ToArray() : [entity];
            SetSelection(members);
            var primary = Selected!;
            foreach (var axis in Enum.GetValues<MovementAxis>())
            {
                var before = members.ToDictionary(e => e, e => e.Transform);
                var bytes = doc.Serialize();
                var handle = Scene.MovementHandles.Single(h => h.Axis == axis);
                var start = handle.Start + (handle.End - handle.Start) * .65;
                var delta = handle.End - handle.Start; delta.Normalize(); delta *= 28;
                if (!Scene.TryBeginGizmoDrag(start)) throw new InvalidOperationException("Real scene axis drag did not start.");
                Scene.ContinueDrag(start + delta);
                if (!grouped && axis == MovementAxis.Y) await Capture("gizmo-drag-y.png");
                Scene.EndDrag(true);
                var moved = primary.Transform.Position; var original = before[primary].Position;
                var translation = new Vector3d(moved.X - original.X, moved.Y - original.Y, moved.Z - original.Z);
                if (!(axis switch { MovementAxis.X => translation.X > 0 && translation.Y == 0 && translation.Z == 0,
                    MovementAxis.Y => translation.Y > 0 && translation.X == 0 && translation.Z == 0,
                    _ => translation.Z > 0 && translation.X == 0 && translation.Y == 0 }))
                    throw new InvalidOperationException("Real scene movement escaped selected axis: " + axis);
                foreach (var member in members)
                {
                    var p = member.Transform.Position; var old = before[member].Position;
                    if (Math.Abs(p.X - old.X - translation.X) + Math.Abs(p.Y - old.Y - translation.Y) + Math.Abs(p.Z - old.Z - translation.Z) > 1e-8)
                        throw new InvalidOperationException("Real group axis movement differs between members.");
                }
                var after = doc.Serialize();
                Undo_Click(this, new());
                if (!doc.Serialize().SequenceEqual(bytes)) throw new InvalidOperationException("Real axis undo did not restore JSON.");
                Redo_Click(this, new());
                if (!doc.Serialize().SequenceEqual(after)) throw new InvalidOperationException("Real axis redo did not restore JSON.");
                Undo_Click(this, new());
            }
        }
        File.WriteAllText(Path.Combine(output, "smoke.txt"),
            "PASS movement gizmo: synthetic rendering/placement regressions, all axes at two camera angles, positive/negative movement, jitter, cancel, undo/redo, group movement, read-only, terrain locks/visibility, NPC ground-only and end-on axis protection.\n" +
            "PASS real batched scene: X/Y/Z arrow hit testing and drag, single and grouped objects, exact axis constraints, one-step undo/redo, rendered default and active Y gizmo. Programmatic WPF interaction; no OS mouse automation.\n");

        async Task Capture(string filename)
        {
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, filename)); png.Save(file);
        }
    }
}
