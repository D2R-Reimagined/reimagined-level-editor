namespace D2RLevel.Core;

/// <summary>Models deleted together in one edit, such as a multi-selection removed with the Delete key.</summary>
public sealed record ModelDeletion(PresetEntity[] Removed)
{
    /// <summary>Standalone HD models and prefab placements. NPCs, terrain and parented entities are not deletable.</summary>
    public static bool CanDelete(PresetEntity e) => e is { HasParent: false, IsTerrain: false, SourceAsset: not null, GameplayUnitIndex: null };

    /// <summary>Deletes every model as one undoable edit, with each linked DS1 unit and owned collision.
    /// If any member fails, the members already deleted are restored and nothing changes.</summary>
    public static ModelDeletion Delete(PresetDocument document, PlacementLinks? links, IEnumerable<PresetEntity> selection)
    {
        var members = selection.Distinct().ToArray();
        if (members.Any(e => !document.Entities.Contains(e) || !CanDelete(e)))
            throw new InvalidOperationException("Deletion supports standalone HD models. NPCs, terrain and parented entities must be removed separately.");
        var deletion = new ModelDeletion(members);
        if (members.Length == 0) return deletion;
        if (links is not null) links.ConnectWorkspace();
        else if (File.Exists(document.SourcePath + PlacementLinks.Suffix)) throw new InvalidOperationException("Load the linked DS1 before deleting these objects.");
        document.History.Transaction(() =>
        {
            foreach (var member in members)
                if (links is not null) links.DeleteModel(member); else document.DeleteModel(member);
        }, deletion);
        return deletion;
    }
}
