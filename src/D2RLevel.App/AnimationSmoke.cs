using System.IO;
using System.Windows;
using D2RLevel.Assets;

namespace D2RLevel.App;

public partial class MainWindow
{
    /// <summary>
    /// Drives the model explorer's animation controls against a real character: the modes
    /// list, mode switching, scrubbing, play/pause, and that posing actually moves the mesh.
    /// </summary>
    private async Task VerifyAnimation(string output)
    {
        Directory.CreateDirectory(output);
        if (resolver is null) throw new InvalidOperationException("Animation smoke requires an asset folder.");
        string model = Argument("--animated-model") ?? "data/hd/character/enemy/fallen1/torso.model";

        var explorer = new ModelExplorer(resolver, document, _ => { }, null) { Owner = this };
        explorer.Show(); await explorer.Ready;
        // The catalog lists logical LOD0 names, so the model is requested as authored.
        await explorer.ShowModel(model);
        if (!explorer.HasAnimations) throw new InvalidOperationException(
            $"Explorer did not build an animated preview for {model}: {explorer.LastAnimationNote}");
        if (explorer.AnimationCount < 2) throw new InvalidOperationException("Expected several animation modes.");

        var report = explorer.VerifyAnimationControls();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        await Capture(explorer, Path.Combine(output, "animation-explorer.png"));
        // Bind pose is the mesh as authored; comparing it against a posed frame shows
        // whether the rig composition is orienting the character correctly.
        explorer.ShowBindPose();
        await Capture(explorer, Path.Combine(output, "animation-bind.png"));
        explorer.ShowMode("walk", 0.5);
        await Capture(explorer, Path.Combine(output, "animation-walk.png"));

        // A non-character model must not claim to be animated.
        await explorer.ShowModel("data/hd/env/model/act1/outdoors/act1_outdoors_rivers/act1_outdoors_river_01.model");
        string staticNote = explorer.HasAnimations ? "STATIC MODEL WAS ANIMATED" : "static model correctly reported unanimated";
        explorer.Close();
        if (explorer.HasAnimations) throw new InvalidOperationException("A static prop was treated as an animated character.");

        File.WriteAllText(Path.Combine(output, "smoke.txt"), report + "\nPASS " + staticNote + "\n");
    }
}
