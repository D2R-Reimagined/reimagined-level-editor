using System.IO;
using D2RLevel.Core;
using Reimagined.Integration;

namespace D2RLevel.App;

public partial class MainWindow
{
    private bool companionSceneEdited;
    private async Task RecordCompanionSmoke(string output)
    {
        var project = StudioTableContext.Project ?? throw new InvalidOperationException("Missing Studio project context.");
        var current = document ?? throw new InvalidOperationException("Missing requested scene.");
        // Smoke edits are confined to the explicitly supplied smoke directory.
        IntegrationFiles.Inside(output, current.SourcePath);
        if (workspaceSession == null || pairedScene?.Collision == null || placementLinks == null) throw new InvalidOperationException("Linked scene is not editable.");
        var properties = LevelProperties.Load(current.SourcePath, pairedScene.Ds1Path, resolver);
        var level = properties.Contexts.Single().Level ?? throw new InvalidOperationException("Scene has no level context.");
        if (level["Id"] != "1") throw new InvalidOperationException("Wrong scene context.");
        if (!companionSceneEdited)
        {
            var entity = current.Entities.First(e => e.CanTransform && !e.HasParent && e.PreviewModel != null);
            double x = entity.Transform.Position.X + 1;
            Apply(entity, entity.Transform with { Position = entity.Transform.Position with { X = x } });
            SaveScene_Click(this, new());
            if (current.IsDirty || PresetDocument.Load(current.SourcePath).Entities.First(e => e.Id == entity.Id).Transform.Position.X != x) throw new InvalidOperationException("Linked scene save failed.");
            companionSceneEdited = true;
            await OpenStudioRecord("levels", level, "Vis0");
            if (!Status.Text.StartsWith("Opened levels", StringComparison.Ordinal)) throw new InvalidOperationException("Return link failed: " + Status.Text);
        }
        await CaptureAuthoring(this, Path.Combine(output, "level-companion.png"));
        await CaptureElement(LevelDetails, Path.Combine(output, "level-companion-panel.png"));
        IntegrationFiles.Write(Path.Combine(output, "level-receipt.json"), new { projectId = project.Id, pid = Environment.ProcessId, sceneSaved = companionSceneEdited, areaLevel = level["MonLvlEx"] });
    }
}
