using System.IO;
using System.Windows;
using D2RLevel.Core;
namespace D2RLevel.App;
public partial class MainWindow
{
    private async Task VerifyGroups(string output)
    {
        Directory.CreateDirectory(output);
        var jsonPath = Path.Combine(output,"data","hd","env","preset","act1","town","towns1.json");
        var ds1Path = Path.Combine(output,"data","global","tiles","act1","town","towns1.ds1");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!); Directory.CreateDirectory(Path.GetDirectoryName(ds1Path)!);
        File.WriteAllBytes(jsonPath,document!.Serialize()); File.WriteAllBytes(ds1Path,pairedScene!.Collision!.Document.Serialize());
        await LoadPreset(jsonPath);
        var anchor = document!.Entities.First(e => GroupMovement.CanGroup(e) && e.Name.Contains("campfire"));
        double Distance(PresetEntity e) => Math.Pow(e.Transform.Position.X-anchor.Transform.Position.X,2)+Math.Pow(e.Transform.Position.Z-anchor.Transform.Position.Z,2);
        var members = document.Entities.Where(e => GroupMovement.CanGroup(e) && Scene.GetItem(e) is not null).OrderBy(Distance).Take(3).ToArray();
        SetSelection(members); GroupName.Text = "Campfire decorations"; Group_Click(this,new());
        if (assetGroups!.Find(anchor) is null || SelectedEntities.Length != 3) throw new InvalidOperationException("Group creation failed.");
        SetSelection([]); SelectFromViewport(anchor);
        if (SelectedEntities.Length != 3) throw new InvalidOperationException("Clicking a group member did not select the group.");
        Scene.FrameSelected();
        await Dispatcher.InvokeAsync(() => {}, System.Windows.Threading.DispatcherPriority.Render);
        var start = members.Select(e => e.Transform).ToArray(); var bytes = document.Serialize();
        var point = new Point(Scene.ActualWidth/2, Scene.ActualHeight/2);
        if (!Scene.BeginDrag(anchor,point)) throw new InvalidOperationException("Group drag failed to start.");
        Scene.ContinueDrag(new(point.X+90,point.Y+30)); Scene.CancelDrag();
        if (!document.Serialize().SequenceEqual(bytes)) throw new InvalidOperationException("Canceled group drag changed JSON.");
        if (!Scene.BeginDrag(anchor,point)) throw new InvalidOperationException("Group drag failed to restart.");
        Scene.ContinueDrag(new(point.X+120,point.Y+40)); Scene.EndDrag(true);
        double dx = members[0].Transform.Position.X-start[0].Position.X, dz=members[0].Transform.Position.Z-start[0].Position.Z;
        if (Math.Abs(dx)+Math.Abs(dz)<.1 || members.Where((e,i)=>Math.Abs(e.Transform.Position.X-start[i].Position.X-dx)>1e-8 || Math.Abs(e.Transform.Position.Z-start[i].Position.Z-dz)>1e-8).Any())
            throw new InvalidOperationException("Group members did not translate by identical deltas.");
        Undo_Click(this,new());
        if (!document.Serialize().SequenceEqual(bytes) || SelectedEntities.Length != 3) throw new InvalidOperationException("Group undo failed or lost selection.");
        Redo_Click(this,new());
        var moved = document.Serialize();
        if (moved.SequenceEqual(bytes)) throw new InvalidOperationException("Group redo failed.");
        document.SaveCopy(Path.Combine(output,"moved.json"));
        if (!PresetDocument.Load(Path.Combine(output,"moved.json")).Serialize().SequenceEqual(moved)) throw new InvalidOperationException("Group move save/reopen failed.");
        assetGroups = new(document);
        SetSelection([]); SelectFromViewport(anchor);
        if (SelectedEntities.Length != 3) throw new InvalidOperationException("Saved group reload failed.");
        Scene.FrameSelected(); UpdateLayout();
        await Dispatcher.InvokeAsync(() => {}, System.Windows.Threading.DispatcherPriority.Render);
        var capture = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
        capture.Render(this); var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(capture));
        using(var file=File.Create(Path.Combine(output,"group.png"))) png.Save(file);
        Ungroup_Click(this,new());
        if (new AssetGroups(document).Find(anchor) is not null) throw new InvalidOperationException("Ungroup persistence failed.");
        File.WriteAllText(Path.Combine(output,"smoke.txt"),"PASS real batched scene: create/select saved group, preview drag/cancel/commit, equal translation, single undo/redo preserving selection, JSON save/reopen, metadata reload and ungroup.");
    }
}
