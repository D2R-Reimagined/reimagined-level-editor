using System.Globalization;
using System.Text.Json.Nodes;

namespace D2RLevel.Core;

public sealed record ModelDuplication(PresetEntity[] Sources, PresetEntity[] Copies);
/// <summary>Models placed together in one edit, such as a row painted from the tileset palette.</summary>
public sealed record ModelPlacement(PresetEntity[] Added);

/// <summary>
/// HD objects copied for pasting back into the same document. Each source is snapshotted when copied,
/// so moving or deleting the originals afterwards does not change what pastes.
/// </summary>
public sealed class ModelClipboard
{
    internal ModelClipboard(PresetDocument document, PresetEntity[] sources, PresetEntity[] snapshots)
    { Document = document; Sources = sources; Snapshots = snapshots; }
    public PresetDocument Document { get; }
    public PresetEntity[] Sources { get; }
    /// <summary>Detached copies of <see cref="Sources"/> as they were when copied; same order.</summary>
    public PresetEntity[] Snapshots { get; }
}

public sealed partial class PresetDocument
{
    public const int MaximumDuplicateModels = 256;

    /// <summary>Repeat complete standalone HD entities along a world axis as one edit.
    /// Gameplay placements and editor link/group metadata are not duplicated.</summary>
    public ModelDuplication DuplicateModels(IEnumerable<PresetEntity> sources, Vector3d step, int repetitions)
    {
        var originals = sources.Distinct().ToArray();
        if (originals.Length == 0 || originals.Any(e => !entities.Contains(e) || !GroupMovement.CanGroup(e)))
            throw new InvalidOperationException("Duplication requires standalone HD models from this document.");
        if (repetitions < 1 || repetitions > MaximumDuplicateModels / originals.Length)
            throw new ArgumentOutOfRangeException(nameof(repetitions), "A drag can create at most 256 models.");
        double[] coordinates = [step.X, step.Y, step.Z];
        if (coordinates.Any(v => !double.IsFinite(v)) || coordinates.Count(v => v != 0) != 1)
            throw new ArgumentException("Duplication requires a finite step along exactly one axis.", nameof(step));
        var offsets = Enumerable.Range(1, repetitions).Select(r => new Vector3d(step.X * r, step.Y * r, step.Z * r));
        return InsertCopies(originals, originals, offsets);
    }

    /// <summary>Snapshot standalone HD entities, including prefab placements, for <see cref="PasteModels"/>.</summary>
    public ModelClipboard CopyModels(IEnumerable<PresetEntity> sources)
    {
        var originals = sources.Distinct().ToArray();
        if (originals.Length == 0 || originals.Length > MaximumDuplicateModels || originals.Any(e => !entities.Contains(e) || !GroupMovement.CanGroup(e)))
            throw new InvalidOperationException("Copy supports 1 to 256 standalone HD models from this document.");
        return new(this, originals, originals.Select(e => new PresetEntity((JsonObject)e.Data.DeepClone(), -1)).ToArray());
    }

    /// <summary>Insert one copy of each clipboard entity, moved by <paramref name="offset"/> from where it was copied, as one edit.
    /// The history subject is a <see cref="ModelDuplication"/> whose sources are the clipboard's sources.</summary>
    public ModelDuplication PasteModels(ModelClipboard clipboard, Vector3d offset)
    {
        if (clipboard.Document != this) throw new InvalidOperationException("Copied models can only be pasted into the scene they came from.");
        if (!double.IsFinite(offset.X) || !double.IsFinite(offset.Y) || !double.IsFinite(offset.Z))
            throw new ArgumentException("Paste offset must be finite.", nameof(offset));
        return InsertCopies(clipboard.Sources, clipboard.Snapshots, [offset]);
    }

    /// <summary>Copies of each template at each offset, with unique names and IDs, inserted and recorded as one edit.</summary>
    private ModelDuplication InsertCopies(PresetEntity[] sources, PresetEntity[] templates, IEnumerable<Vector3d> offsets)
    {
        var names = entities.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = entities.Select(e => e.Id).ToHashSet();
        var copies = new List<PresetEntity>();
        foreach (var offset in offsets)
            foreach (var original in templates)
            {
                var start = original.Transform;
                var position = new Vector3d(start.Position.X + offset.X, start.Position.Y + offset.Y, start.Position.Z + offset.Z);
                (start with { Position = position }).Validate();
                var data = (JsonObject)original.Data.DeepClone();
                string basename = original.Name + "_copy", name = basename;
                for (int suffix = 2; !names.Add(name); suffix++) name = basename + suffix;
                uint id;
                do { id = (uint)Random.Shared.NextInt64(1, (long)uint.MaxValue + 1); }
                while (!ids.Add(id.ToString(CultureInfo.InvariantCulture)));
                data["id"] = id; data["name"] = name;
                var copy = new PresetEntity(data, nextIndex++);
                PatchVector(copy.TransformNode!, "position", position, start.Position);
                copies.Add(copy);
            }
        var result = new ModelDuplication(sources, copies.ToArray());
        var array = (JsonArray)root["entities"]!;
        void Insert()
        {
            foreach (var copy in result.Copies) { array.Add(copy.Data); entities.Add(copy); }
            dirtyCache = null;
        }
        void Remove()
        {
            foreach (var copy in result.Copies) { array.Remove(copy.Data); entities.Remove(copy); }
            dirtyCache = null;
        }
        Insert(); History.Record(Remove, Insert, result);
        return result;
    }
}
