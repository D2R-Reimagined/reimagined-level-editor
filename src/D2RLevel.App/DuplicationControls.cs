using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private void DuplicateAlongAxis(PresetEntity[] sources, Vector3d step, int repetitions)
    {
        try
        {
            if (document is null) return;
            var result = document.DuplicateModels(sources, step, repetitions);
            Status.Text = L.T("Created {0} HD copies. Ctrl+Z undoes the row. DS1 units and collision links are not copied.", result.Copies.Length);
        }
        catch (Exception ex) { Error(ex); }
    }

    private void SyncDuplication(ModelDuplication duplication)
    {
        if (document is null) return;
        for (int i = 0; i < duplication.Copies.Length; i++)
        {
            var copy = duplication.Copies[i];
            if (document.Entities.Contains(copy))
            {
                if (Scene.GetItem(copy) is null)
                {
                    if (!addedModels.TryGetValue(copy, out var item))
                    {
                        var source = duplication.Sources[i % duplication.Sources.Length];
                        var sourceItem = Scene.GetItem(source) ?? addedModels.GetValueOrDefault(source)
                            ?? throw new InvalidOperationException("Source model geometry is unavailable.");
                        item = sourceItem with { Entity = copy };
                        addedModels[copy] = item;
                    }
                    Scene.AddItem(item);
                }
                removedModels.Remove(copy);
            }
            else
            {
                if (Scene.GetItem(copy) is { } item) addedModels[copy] = item;
                Scene.RemoveItem(copy); removedModels.Add(copy);
            }
        }
        Search.Text = ""; Filter();
        SetSelection(document.Entities.Contains(duplication.Copies[0])
            ? duplication.Copies.TakeLast(duplication.Sources.Length) : duplication.Sources);
        PopulateInspector(); RefreshState();
    }
}
