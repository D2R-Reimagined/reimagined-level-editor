using System.Text.Json.Nodes;
using D2RLevel.Core;

internal static class DuplicationChecks
{
    public static void Run(string folder, Action<bool, string> check, Action<Action, string> throws)
    {
        var root = JsonNode.Parse(PresetDocument.ModelPreview("data/hd/wall.model").Serialize())!;
        var sourceNode = root["entities"]![0]!;
        sourceNode["id"] = ulong.MaxValue;
        sourceNode["future"] = new JsonObject { ["preserve"] = true };
        ((JsonArray)sourceNode["components"]!).Add(new JsonObject { ["type"] = "PhysicsBodyDefinitionComponent", ["filter"] = "wall" });
        root["dependencies"] = new JsonObject { ["future"] = new JsonArray("opaque") };
        var path = Path.Combine(folder, "duplicate-source.json"); File.WriteAllText(path, root.ToJsonString());
        var doc = PresetDocument.Load(path); var source = doc.Entities[0];
        doc.SetTransform(source, new(new(10, 20, 30), new(0, Math.Sin(.3), 0, Math.Cos(.3)), new(-2, 3, 4)));
        var before = doc.Serialize(); var transform = source.Transform; var raw = source.RawJson;
        foreach (var step in new[] { new Vector3d(8, 0, 0), new Vector3d(0, -9, 0), new Vector3d(0, 0, 12) })
        {
            var result = doc.DuplicateModels([source], step, 3);
            check(result.Copies.Length == 3 && source.RawJson == raw && doc.Entities.Count == 4, "copy row preserves source and creates requested count");
            check(doc.Entities.Select(e => e.Id).Distinct().Count() == 4 && doc.Entities.Select(e => e.Name).Distinct().Count() == 4,
                "copies have unique names and IDs");
            check(result.Copies.Select(e => e.Index).Distinct().Count() == 3 && result.Copies.All(e => e.Index != source.Index), "copy render identities are unique");
            for (int i = 0; i < 3; i++)
            {
                var copy = result.Copies[i];
                check(copy.Transform.Position == new Vector3d(transform.Position.X + step.X * (i + 1), transform.Position.Y + step.Y * (i + 1), transform.Position.Z + step.Z * (i + 1)) &&
                    copy.Transform.Scale == transform.Scale && copy.Transform.Orientation == transform.Orientation, "copy uses exact axis interval and preserves orientation and scale");
                var actual = JsonNode.Parse(copy.RawJson)!; var expected = JsonNode.Parse(raw)!;
                expected["id"] = actual["id"]!.DeepClone(); expected["name"] = copy.Name;
                expected["components"]![0]!["position"] = actual["components"]![0]!["position"]!.DeepClone();
                check(JsonNode.DeepEquals(expected, actual), "copy retains physics, model settings and unknown fields");
            }
            check(JsonNode.DeepEquals(JsonNode.Parse(doc.Serialize())!["dependencies"], root["dependencies"]), "duplication preserves shared asset dependencies");
            var after = doc.Serialize();
            var saved = Path.Combine(folder, "duplicated.json"); doc.SaveCopy(saved);
            check(PresetDocument.Load(saved).Serialize().SequenceEqual(after), "duplicate row save/reopen is exact");
            doc.Undo(); check(doc.Serialize().SequenceEqual(before), "one undo removes entire copy row");
            doc.Redo(); check(doc.Serialize().SequenceEqual(after) && result.Copies.All(doc.Entities.Contains), "redo retains copy identities and all components");
            doc.Undo();
        }
        var second = doc.AddModel("data/hd/other.model", new(1, 2, 3));
        var groupBefore = doc.Serialize();
        var group = doc.DuplicateModels([source, second], new(-5, 0, 0), 2);
        check(group.Copies.Length == 4 && group.Copies[0].Transform.Position.X == 5 && group.Copies[1].Transform.Position.X == -4 &&
            group.Copies[2].Transform.Position.X == 0 && group.Copies[3].Transform.Position.X == -9, "repeated groups preserve offsets and repeat order");
        doc.Undo(); check(doc.Serialize().SequenceEqual(groupBefore), "whole repeated group is one undo");
        var branch = doc.DuplicateModels([second], new(0, 0, 1), 1);
        check(branch.Copies[0].Index > group.Copies.Max(e => e.Index) && !doc.CanRedo, "copy branch never reuses render identities");
        doc.Undo();
        throws(() => doc.DuplicateModels([source], new(1, 1, 0), 1), "copy rejects multi-axis step");
        throws(() => doc.DuplicateModels([source], default, 1), "copy rejects zero step");
        throws(() => doc.DuplicateModels([source], new(double.NaN, 0, 0), 1), "copy rejects non-finite step");
        throws(() => doc.DuplicateModels([source], new(double.MaxValue, 0, 0), 3), "copy validates every destination before mutation");
        throws(() => doc.DuplicateModels([source, second], new(1, 0, 0), 129), "copy cap counts all group members");
        throws(() => doc.DuplicateModels([source], new(1, 0, 0), 0), "copy rejects empty row");
        throws(() => doc.DuplicateModels([PresetDocument.ModelPreview("data/hd/foreign.model").Entities[0]], new(1, 0, 0), 1), "copy rejects foreign entity");
        var npc = PresetEntity.GameplayPreview(0, "NPC", transform);
        throws(() => doc.DuplicateModels([npc], new(1, 0, 0), 1), "copy rejects DS1 proxy");
        check(doc.Serialize().SequenceEqual(groupBefore), "all rejected rows leave document untouched");
        throws(() => doc.History.Transaction(() => { doc.DuplicateModels([source], new(1, 0, 0), 2); throw new InvalidOperationException(); }), "copy row participates in transaction rollback");
        check(doc.Serialize().SequenceEqual(groupBefore), "failed transaction removes complete copy row");
    }
}
