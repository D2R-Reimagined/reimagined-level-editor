using System.Text.Json.Nodes;

namespace D2RLevel.Core;

public sealed partial class PlacementLinks
{
    public GameplayPrefab CapturePrefab(string name, IEnumerable<PresetEntity> selection, IEnumerable<int> unitSelection, double scale, GameplayCatalog catalog)
    {
        EnsureReady(); ValidateStructure(); GridCalibration.Validate(scale);
        if (ds1.GameplayWarning is { } warning) throw new InvalidOperationException(warning);
        if (catalog.Act != ds1.Act) throw new InvalidOperationException("Gameplay catalog act does not match this scene.");
        var units = unitSelection.ToHashSet();
        var models = selection.Distinct().ToList();
        if (units.Any(i => i < 0 || i >= ds1.Units.Count)) throw new InvalidDataException("Selected gameplay unit no longer exists.");
        // Explicit ownership provides the closure; proximity never establishes membership.
        foreach (var link in Links.Where(l => l.Unit is { } u && units.Contains(u.Index)))
        { var entity = Entity(link); RequireHealthy(entity); if (!models.Contains(entity)) models.Add(entity); }
        foreach (var model in models)
        {
            if (!json.Entities.Contains(model)) throw new InvalidDataException("Selected model belongs to another scene.");
            GameplayPrefab.ValidateModel(model);
            if (Find(model) is { } link)
            {
                RequireHealthy(model);
                if (Math.Abs(link.UnitsPerTile - scale) > 1e-8) throw new InvalidOperationException("Prefab scale must match every included ownership link.");
                if (link.Unit is { } u) units.Add(u.Index);
            }
        }
        if (models.Count + units.Count == 0) throw new InvalidOperationException("Select HD models or gameplay units to save a prefab.");
        var indices = units.Order().ToArray();
        var positions = models.Select(m => (X: m.Transform.Position.X / scale, Y: m.Transform.Position.Z / scale))
            .Concat(indices.Select(i => (ds1.Units[i].X / 5d, ds1.Units[i].Y / 5d)))
            .Concat(models.SelectMany(CollisionTiles).Select(t => ((double)t.X, (double)t.Y))).ToArray();
        int originX = checked((int)Math.Floor(positions.Min(p => p.Item1))), originY = checked((int)Math.Floor(positions.Min(p => p.Item2)));
        double height = models.Count == 0 ? 0 : models[0].Transform.Position.Y;
        var recipes = indices.Select(i =>
        {
            var u = ds1.Units[i]; var matches = catalog.Assets.Where(a => a.Type == u.Type && a.Id == u.Id && a.CanPlace).ToArray();
            if (matches.Length != 1) throw new InvalidDataException($"Unit {i} does not have an unambiguous supported gameplay recipe.");
            var patrol = ds1.PatrolPoints(i);
            if (patrol.Count > 0 && ds1.PathEditWarning(i) is { } reason) throw new InvalidOperationException(reason);
            return new PrefabUnit(matches[0].Key, matches[0].Kind, u.Type, u.Flags, checked(u.X - originX * 5), checked(u.Y - originY * 5),
                patrol.Select(p => p with { X = checked(p.X - originX * 5), Y = checked(p.Y - originY * 5) }).ToArray(), GameplayPrefab.DefinitionHash(matches[0]));
        }).ToArray();
        var snapshots = models.Select(m =>
        {
            var node = (JsonObject)m.Data.DeepClone(); var component = ((JsonArray)node["components"]!).OfType<JsonObject>().Single(c => c["type"]?.GetValue<string>() == "TransformDefinitionComponent");
            var pos = m.Transform.Position; var p = component["position"] as JsonObject ?? new JsonObject();
            p["x"] = pos.X - originX * scale; p["y"] = pos.Y - height; p["z"] = pos.Z - originY * scale;
            if (component["position"] is null) component["position"] = p;
            return new PrefabModel(node, Find(m)?.Unit is { } u ? Array.IndexOf(indices, u.Index) : null,
                CollisionTiles(m).Select(t => new LinkedTile(t.X - originX, t.Y - originY)).ToArray());
        }).ToArray();
        var references = snapshots.SelectMany(m => GameplayPrefab.AssetReferences(m.Entity)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var dependencySnapshot = json.PrefabDependencySnapshot();
        var prefab = new GameplayPrefab(1, name.Trim(), ds1.Act, scale, snapshots, recipes, references)
            { SourceDependencies = dependencySnapshot, Dependencies = GameplayPrefab.FilterDependencies(dependencySnapshot, references) };
        prefab.Validate(); return prefab;
    }
    public GameplayAsset[] ValidatePrefabPlacement(GameplayPrefab prefab, int tileX, int tileY, double elevation, double scale, GameplayCatalog catalog)
    {
        EnsureReady(); ValidateStructure(); prefab.Validate(); GridCalibration.Validate(scale);
        if (ds1.GameplayWarning is { } warning) throw new InvalidOperationException(warning);
        if (prefab.Act != ds1.Act || catalog.Act != ds1.Act) throw new InvalidOperationException("Prefab and destination must use the same act.");
        if (Math.Abs(prefab.UnitsPerTile - scale) > 1e-8 || Math.Abs(Calibration.UnitsPerTile - scale) > 1e-8 || Calibration.IsPoorFit || Healthy.Any(l => Math.Abs(l.UnitsPerTile - scale) > 1e-8)) throw new InvalidOperationException("Prefab scale must match the target scene calibration and ownership links. Set a consistent scene calibration first; prefabs do not rescale geometry.");
        if (!double.IsFinite(elevation) || tileX < 0 || tileY < 0 || tileX >= ds1.Width || tileY >= ds1.Height) throw new InvalidDataException("Choose a finite height and an anchor tile inside the map.");
        var recipes = prefab.Units.Select(u =>
        {
            var matches = catalog.Assets.Where(a => a.Act == prefab.Act && a.Type == u.Type && a.Kind == u.Kind && a.Key.Equals(u.Key, StringComparison.OrdinalIgnoreCase) && a.CanPlace).ToArray();
            if (matches.Length != 1 || GameplayPrefab.DefinitionHash(matches[0]) != u.DefinitionHash) throw new InvalidDataException("Target gameplay recipe is missing, ambiguous or changed: " + u.Key);
            return matches[0];
        }).ToArray();
        void Subtile(int x, int y)
        {
            if (x < 0 || y < 0 || x >= ds1.Width * 5 || y >= ds1.Height * 5) throw new InvalidDataException("Prefab gameplay or patrol lies outside the target map.");
            if (!ds1.CanBlock(x / 5, y / 5)) throw new InvalidOperationException("Prefab gameplay requires existing ground.");
        }
        var coordinates = new HashSet<(int, int)>(ds1.Units.Select(u => (u.X, u.Y)));
        foreach (var u in prefab.Units)
        {
            int x = checked(tileX * 5 + u.X), y = checked(tileY * 5 + u.Y); Subtile(x, y);
            if (!coordinates.Add((x, y))) throw new InvalidOperationException("A gameplay anchor overlaps another unit. Choose another tile.");
            foreach (var p in u.Patrol) Subtile(checked(tileX * 5 + p.X), checked(tileY * 5 + p.Y));
        }
        var exitPositions = ds1.ExitTiles().Select(t => (t.X, t.Y)).ToHashSet();
        foreach (var m in prefab.Models)
        {
            var t = new PresetEntity(m.Entity, 0).Transform;
            var target = t with { Position = new(t.Position.X + tileX * scale, t.Position.Y + elevation, t.Position.Z + tileY * scale) }; target.Validate();
            if (target.Position.X < 0 || target.Position.Z < 0 || target.Position.X >= ds1.Width * scale || target.Position.Z >= ds1.Height * scale) throw new InvalidDataException("Prefab model anchor lies outside the target map.");
            foreach (var tile in m.Collision)
            {
                int x = checked(tileX + tile.X), y = checked(tileY + tile.Y);
                if (x < 0 || y < 0 || x >= ds1.Width || y >= ds1.Height || !ds1.CanBlock(x, y)) throw new InvalidDataException("Prefab collision requires ground inside the map.");
                if (exitPositions.Contains((x, y))) throw new InvalidOperationException("Prefab collision cannot cover an exit or special placement marker.");
            }
        }
        return recipes;
    }
    public PrefabPlacement PlacePrefab(GameplayPrefab prefab, int tileX, int tileY, double elevation, double scale, GameplayCatalog catalog)
    {
        var recipes = ValidatePrefabPlacement(prefab, tileX, tileY, elevation, scale, catalog); Activate();
        PrefabPlacement? result = null;
        // A mutable subject lets the outer transaction report all created entities as one edit.
        var subject = new PrefabPlacement(new PresetEntity[prefab.Models.Length], new int[prefab.Units.Length]);
        json.History.Transaction(() =>
        {
            var copies = json.InsertPrefab(prefab, new(tileX * scale, elevation, tileY * scale));
            copies.CopyTo(subject.Models, 0);
            for (int i = 0; i < prefab.Units.Length; i++)
            {
                var u = prefab.Units[i]; int index = AppendUnit(u.Type, recipes[i].Id!.Value, checked(tileX * 5 + u.X), checked(tileY * 5 + u.Y), u.Flags); subject.Units[i] = index;
                for (int p = 0; p < u.Patrol.Length; p++) ds1.InsertPathPoint(index, p, checked(tileX * 5 + u.Patrol[p].X), checked(tileY * 5 + u.Patrol[p].Y), u.Patrol[p].Action);
            }
            for (int i = 0; i < copies.Length; i++)
            {
                var m = prefab.Models[i];
                if (m.Unit is { } u) LinkUnit(copies[i], subject.Units[u], scale);
                if (m.Collision.Length > 0) LinkFootprint(copies[i], m.Collision.Select(t => new LinkedTile(checked(tileX + t.X), checked(tileY + t.Y))), scale, false);
            }
            result = subject;
        }, subject);
        return result!;
    }
}
