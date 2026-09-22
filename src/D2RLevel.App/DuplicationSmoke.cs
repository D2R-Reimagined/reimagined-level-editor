using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private async Task VerifyDuplication(string output)
    {
        Directory.CreateDirectory(output);
        RenderingChecks.Run();
        var doc = document ?? throw new InvalidOperationException("Load a preset for duplication smoke.");
        var entity = doc.Entities.First(e => GroupMovement.CanGroup(e) && e.Name == "r_wall01_06");
        var originalBytes = doc.Serialize(); var ds1Bytes = pairedScene?.Collision?.Document.Serialize();
        var originalEntities = doc.Entities.ToArray();
        foreach (bool grouped in new[] { false, true })
            foreach (var axis in Enum.GetValues<MovementAxis>())
            {
                var members = grouped ? originalEntities.Where(e => GroupMovement.CanGroup(e) && Scene.GetItem(e) is not null)
                    .OrderBy(e => Math.Pow(e.Transform.Position.X - entity.Transform.Position.X, 2) + Math.Pow(e.Transform.Position.Z - entity.Transform.Position.Z, 2))
                    .Take(2).ToArray() : [entity];
                SetSelection(members); Scene.FrameSelected(); Scene.Dolly(-18);
                var primary = Selected!;
                var item = Scene.GetItem(primary)!;
                var bounds = SceneViewport.Transform(primary.Transform).TransformBounds(item.Geometry.Bounds);
                var center = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
                var direction = axis switch { MovementAxis.X => new Vector3D(1, 0, 0), MovementAxis.Y => new Vector3D(0, 1, 0), _ => new Vector3D(0, 0, 1) };
                var handle = Scene.MovementHandles.Single(h => h.Axis == axis);
                if (!Scene.TryBeginGizmoDrag(handle.Start + (handle.End - handle.Start) * .7, true))
                    throw new InvalidOperationException("Alt-arrow did not start real duplication.");
                Scene.CancelDrag();
                var start = Scene.Project(center)!.Value;
                if (!Scene.BeginDrag(primary, start, axis, true)) throw new InvalidOperationException("Real copy drag refused.");
                double spacing = Scene.DuplicateSpacing;
                var endpoint = Scene.Project(center - direction * (spacing * 3.1))!.Value;
                Scene.ContinueDrag(endpoint);
                if (Scene.DuplicatePreviewCount != 3 * members.Length || !doc.Serialize().SequenceEqual(originalBytes))
                    throw new InvalidOperationException("Preview changed source or failed to fill intervals.");
                if (!grouped && axis == MovementAxis.X) await Capture("duplicate-preview.png");
                Scene.CancelDrag();
                if (!doc.Serialize().SequenceEqual(originalBytes) || Scene.DuplicatePreviewCount != 0)
                    throw new InvalidOperationException("Canceled duplication changed scene.");
                Scene.BeginDrag(primary, start, axis, true); Scene.ContinueDrag(endpoint); Scene.EndDrag(true);
                var copies = doc.Entities.Except(originalEntities).ToArray();
                if (copies.Length != members.Length * 3 || copies.Any(e => Scene.GetItem(e) is null) || SelectedEntities.Length != members.Length ||
                    SelectedEntities.Any(e => !copies.Contains(e))) throw new InvalidOperationException("Copy commit failed to insert geometry or select final copies.");
                for (int i = 0; i < copies.Length; i++)
                {
                    var source = members[i % members.Length]; var copy = copies[i];
                    var p = source.Transform.Position;
                    double interval = spacing * (1 + i / members.Length);
                    var expected = new Vector3d(p.X - direction.X * interval, p.Y - direction.Y * interval, p.Z - direction.Z * interval);
                    if (copy.Transform.Position != expected || copy.Transform.Orientation != source.Transform.Orientation || copy.Transform.Scale != source.Transform.Scale)
                        throw new InvalidOperationException("Copies are not spaced on model bounds.");
                }
                if (ds1Bytes is not null && !pairedScene!.Collision!.Document.Serialize().SequenceEqual(ds1Bytes))
                    throw new InvalidOperationException("HD duplication changed DS1 gameplay.");
                var after = doc.Serialize();
                string saved = Path.Combine(output, "copies.json"); doc.SaveCopy(saved);
                if (!PresetDocument.Load(saved).Serialize().SequenceEqual(after)) throw new InvalidOperationException("Copy save/reopen changed data.");
                if (!grouped && axis == MovementAxis.X)
                {
                    SetSelection(members.Concat(copies)); Scene.FrameSelected(); await Capture("duplicate-row.png");
                    await LoadScene(doc, resolver);
                    if (!ReferenceEquals(document, doc)) throw new InvalidOperationException("Asset reload replaced copy history.");
                }
                Undo_Click(this, new());
                if (!doc.Serialize().SequenceEqual(originalBytes) || copies.Any(e => Scene.GetItem(e) is not null))
                    throw new InvalidOperationException("Copy undo left data or geometry behind.");
                Redo_Click(this, new());
                if (!doc.Serialize().SequenceEqual(after) || copies.Any(e => Scene.GetItem(e) is null))
                    throw new InvalidOperationException("Copy redo failed to restore geometry or data.");
                Undo_Click(this, new());
            }
        // SaveCopy marks its exported state clean. All edits have now been undone;
        // restore the fixture's clean baseline so shutdown needs no discard dialog.
        doc.MarkSaved();
        File.WriteAllText(Path.Combine(output, "smoke.txt"),
            "PASS synthetic movement and duplication checks: scaled/rotated bounds, X/Y/Z, positive and negative fill, shrinking preview, cancellation, group spacing and unsupported selections.\n" +
            "PASS real scene Alt-axis repetition: model/group copies on all axes, originals and DS1 unchanged, unique copies selected, preview cancel, one-step undo/redo, save/reopen, geometry restoration after asset reload, rendered preview and row. Programmatic WPF interaction, not OS input.\n");

        async Task Capture(string filename)
        {
            UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            double captureScale = Math.Min(1, 1800 / ActualWidth);
            var bitmap = new RenderTargetBitmap((int)(ActualWidth * captureScale), (int)(ActualHeight * captureScale), 96 * captureScale, 96 * captureScale, PixelFormats.Pbgra32);
            bitmap.Render(this); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, filename)); png.Save(file);
        }
    }
}
