using System.Text.Json;
namespace D2RLevel.Core;

public static class GroupMovement
{
    public static bool CanGroup(PresetEntity e) => e.GameplayUnitIndex is null && e.CanTransform && !e.HasParent && !e.IsTerrain && e.PreviewModel is not null;
    public static void Move(PresetDocument document, PlacementLinks? links, IEnumerable<PresetEntity> selection, Vector3d delta)
    {
        var members = selection.Distinct().ToArray();
        if (members.Length == 0) return;
        if (members.Any(e => !document.Entities.Contains(e) || !CanGroup(e)))
            throw new InvalidOperationException("Group movement supports unparented HD models. Select NPCs or terrain separately.");
        var targets = members.Select(e => e.Transform with { Position = new(e.Transform.Position.X + delta.X, e.Transform.Position.Y + delta.Y, e.Transform.Position.Z + delta.Z) }).ToArray();
        foreach (var target in targets) target.Validate();
        if (links is not null) links.ConnectWorkspace();
        else if (File.Exists(document.SourcePath + PlacementLinks.Suffix)) throw new InvalidOperationException("Load the linked DS1 before moving this selection.");
        document.History.Transaction(() =>
        {
            for (int i = 0; i < members.Length; i++)
                if (links is not null) links.Move(members[i], targets[i]); else document.SetTransform(members[i], targets[i]);
        }, members);
    }
}

public sealed record AssetGroupMember(string Id, string Name, string Model);
public sealed record AssetGroup(string Name, AssetGroupMember[] Members);
public sealed class AssetGroups
{
    public const string Suffix = ".rle-groups.json";
    private sealed record FileData(int Version, AssetGroup[] Groups);
    private readonly PresetDocument document;
    private AssetGroup[] groups = [];
    public string Path => document.SourcePath + Suffix;
    public string? Warning { get; private set; }
    public AssetGroups(PresetDocument document)
    {
        this.document = document;
        try
        {
            if (!File.Exists(Path)) return;
            if (new FileInfo(Path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Group metadata exceeds 4 MiB.");
            var file = JsonSerializer.Deserialize<FileData>(File.ReadAllText(Path));
            if (file is not { Version: 1, Groups: not null } || file.Groups.Any(g => g is null || string.IsNullOrWhiteSpace(g.Name) || g.Members is null || g.Members.Length < 2 || g.Members.Any(m => m is null || string.IsNullOrWhiteSpace(m.Id))))
                throw new InvalidDataException("Invalid group metadata.");
            var ids = file.Groups.SelectMany(g => g.Members).Select(m => m.Id).ToArray();
            if (ids.Distinct().Count() != ids.Length) throw new InvalidDataException("An asset belongs to multiple groups.");
            groups = file.Groups;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Warning = "Groups could not be loaded: " + ex.Message; }
    }
    public AssetGroup? Find(PresetEntity entity) => groups.FirstOrDefault(g => Resolve(g).Contains(entity));
    public PresetEntity[] Resolve(AssetGroup group) => group.Members.Select(m =>
    {
        var found = document.Entities.Where(e => GroupMovement.CanGroup(e) && e.Id == m.Id && e.Name == m.Name && e.PreviewModel == m.Model).Take(2).ToArray();
        return found.Length == 1 ? found[0] : null;
    }).OfType<PresetEntity>().ToArray();
    public void Create(string name, IEnumerable<PresetEntity> selected)
    {
        var members = selected.Distinct().ToArray();
        if (members.Length < 2 || members.Any(e => !document.Entities.Contains(e) || !GroupMovement.CanGroup(e))) throw new InvalidOperationException("Select at least two unparented HD models to group.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new InvalidOperationException("Enter a group name of 1 to 100 characters.");
        if (members.Any(e => e.Id == "(none)" || document.Entities.Count(other => other.Id == e.Id) != 1)) throw new InvalidOperationException("Saved groups require unique entity IDs.");
        var ids = members.Select(e => e.Id).ToHashSet();
        var next = groups.Where(g => !g.Members.Any(m => ids.Contains(m.Id))).Append(new AssetGroup(name.Trim(), members.Select(e => new AssetGroupMember(e.Id, e.Name, e.PreviewModel!)).ToArray())).ToArray();
        Save(next);
    }
    public void Remove(IEnumerable<PresetEntity> selected)
    {
        var removing = selected.Select(Find).OfType<AssetGroup>().ToHashSet();
        Save(groups.Where(g => !removing.Contains(g)).ToArray());
    }
    private void Save(AssetGroup[] next)
    {
        if (Warning is not null) throw new InvalidOperationException(Warning + " Repair the group metadata file before replacing it.");
        var temp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new FileData(1, next), new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(Path)) File.Replace(temp, Path, Path + ".bak"); else File.Move(temp, Path);
            groups = next;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
