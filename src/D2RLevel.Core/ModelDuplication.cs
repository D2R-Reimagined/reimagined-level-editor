using System.Globalization;
using System.Text.Json.Nodes;

namespace D2RLevel.Core;

public sealed record ModelDuplication(PresetEntity[] Sources, PresetEntity[] Copies);

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
        var names = entities.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = entities.Select(e => e.Id).ToHashSet();
        var copies = new List<PresetEntity>();
        for (int repeat = 1; repeat <= repetitions; repeat++)
            foreach (var original in originals)
            {
                var start = original.Transform;
                var position = new Vector3d(start.Position.X + step.X * repeat, start.Position.Y + step.Y * repeat, start.Position.Z + step.Z * repeat);
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
        var result = new ModelDuplication(originals, copies.ToArray());
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
