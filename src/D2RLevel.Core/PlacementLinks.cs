using System.Text.Json;
using System.Text.Json.Serialization;

namespace D2RLevel.Core;

public sealed record UnitLink(int Index, int Type, int Id, uint Flags, int X, int Y);
public sealed record PlacementLink(string EntityId, string Name, string? Model, EntityTransform Anchor, double UnitsPerTile,
    UnitLink? Unit, LinkedTile[] Tiles);
public sealed record PlacementLinkFile(int Version, string Ds1Path, string Fingerprint, PlacementLink[] Links, TileBaseline[] Baselines)
{
    /// <summary>Absent in version 1 files; those pairs fall back to the unverified default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GridCalibration? Calibration { get; init; }
}
public sealed record CollisionOwnership(LinkedTile Tile, string[] Owners, bool ProtectedBlocking);

/// <summary>One link's current usability. <see cref="Reason"/> is null while the link is intact.</summary>
public sealed record LinkHealth(PlacementLink Link, string? Reason)
{
    public bool IsHealthy => Reason is null;
}

public sealed class PlacementLinks
{
    public const string Suffix = ".rle-links.json";
    /// <summary>Version 2 added the per-pair grid calibration. Version 1 files still load.</summary>
    public const int CurrentVersion = 2;
    private readonly PresetDocument json;
    private readonly Ds1CollisionDocument ds1;
    private PlacementLinkFile file;
    // Computed on demand, never inside an edit: a transaction rolls its recorded steps
    // back in reverse order, so link state is briefly inconsistent while unwinding.
    // Health also depends on document state that changes outside this class -- direct
    // painting, undo, redo -- so the cache is stamped with both histories' versions.
    private Dictionary<string, string?>? healthCache;
    private long healthStamp = -1;
    private long StateStamp => json.History.Version + ds1.History.Version;
    public string SidecarPath { get; }
    /// <summary>Set when the whole sidecar is unusable for this pair. Individual broken
    /// links are reported through <see cref="LinkStates"/> instead.</summary>
    public string? Warning { get; private set; }
    public IReadOnlyList<PlacementLink> Links => file.Links ?? [];
    public bool HasLinks => Links.Count > 0;
    public bool HasMetadataChanges { get; private set; }
    private string savedMetadata;

    /// <summary>This pair's HD-to-DS1 registration, with the provenance it was established by.</summary>
    public GridCalibration Calibration => file.Calibration ?? GridCalibration.Unverified;

    /// <summary>Every link with its current status, in file order.</summary>
    public IReadOnlyList<LinkHealth> LinkStates =>
        Links.Select(l => new LinkHealth(l, Health.GetValueOrDefault(l.EntityId))).ToArray();
    public int BrokenLinkCount => Links.Count(l => Health.GetValueOrDefault(l.EntityId) is not null);
    public bool HasBrokenLinks => BrokenLinkCount > 0;
    private Dictionary<string, string?> Health
    {
        get
        {
            if (healthCache is null || healthStamp != StateStamp)
            { healthStamp = StateStamp; healthCache = ComputeHealth(); }
            return healthCache;
        }
    }
    private void InvalidateHealth() => healthCache = null;
    private IEnumerable<PlacementLink> Healthy => Links.Where(l => Health.GetValueOrDefault(l.EntityId) is null);

    public PlacementLinks(PresetDocument json, Ds1CollisionDocument ds1, string? sidecarPath = null)
    {
        this.json = json; this.ds1 = ds1; SidecarPath = sidecarPath ?? json.SourcePath + Suffix;
        file = Empty();
        try
        {
            if (File.Exists(SidecarPath))
            {
                if (new FileInfo(SidecarPath).Length > 8 * 1024 * 1024) throw new InvalidDataException("Link file exceeds 8 MiB.");
                file = JsonSerializer.Deserialize<PlacementLinkFile>(File.ReadAllText(SidecarPath)) ?? throw new InvalidDataException("Invalid link file.");
                ValidateStructure();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or NullReferenceException or InvalidOperationException or OverflowException)
        { Warning = "Links disabled: " + ex.Message + " Restore the correct JSON/DS1 pair or repair the sidecar before moving objects."; }
        savedMetadata = Serialize(file);
        if (Warning is null && HasLinks) Activate();
    }

    private PlacementLinkFile Empty() =>
        new(CurrentVersion, Path.GetRelativePath(Path.GetDirectoryName(SidecarPath)!, ds1.SourcePath), ds1.LinkFingerprint(), [], []);

    public PlacementLink? Find(PresetEntity entity) => file.Links?.FirstOrDefault(l => l?.EntityId == entity.Id);

    /// <summary>Why this entity's link cannot be used, or null when it is intact or absent.</summary>
    public string? BrokenReason(PresetEntity entity) => Find(entity) is null ? null : Health.GetValueOrDefault(entity.Id);

    // Duplicate ids map to null so an ambiguous entity fails the same way it always has.
    private Dictionary<string, PresetEntity?> BuildIndex()
    {
        var map = new Dictionary<string, PresetEntity?>(StringComparer.Ordinal);
        foreach (var entity in json.Entities)
            map[entity.Id] = map.ContainsKey(entity.Id) ? null : entity;
        return map;
    }

    private PresetEntity Entity(PlacementLink link, Dictionary<string, PresetEntity?>? index = null)
    {
        var entity = (index ?? BuildIndex()).GetValueOrDefault(link.EntityId);
        if (entity is null || entity.PreviewModel != link.Model || entity.HasParent || !entity.CanTransform)
            throw new InvalidDataException($"HD identity changed for {link.Name}.");
        return entity;
    }

    /// <summary>
    /// File-level integrity: whether this sidecar describes this pair at all. These
    /// failures disable linking entirely because nothing in the file can be trusted.
    /// </summary>
    private void ValidateStructure()
    {
        if (file.Version is not (1 or CurrentVersion) || file.Links is null || file.Baselines is null)
            throw new InvalidDataException("Unrecognized link file layout.");
        // Version 1 sidecars stored a fingerprint that also covered patrol bytes; they
        // are checked against that and rewritten at the current version when saved.
        string expected = file.Version == 1 ? ds1.LegacyLinkFingerprint() : ds1.LinkFingerprint();
        if (file.Fingerprint != expected)
            throw new InvalidDataException("DS1 structure does not match the saved links.");
        if (file.Links.Length > 10000 || file.Links.Select(l => l.EntityId).Distinct().Count() != file.Links.Length)
            throw new InvalidDataException("Duplicate or excessive links.");
        var units = file.Links.Where(l => l.Unit is not null).Select(l => l.Unit!.Index).ToArray();
        if (units.Distinct().Count() != units.Length) throw new InvalidDataException("A DS1 placement is linked more than once.");
        if (file.Baselines.Select(b => new LinkedTile(b.X, b.Y)).Distinct().Count() != file.Baselines.Length)
            throw new InvalidDataException("Duplicate footprint baseline cells.");
        file.Calibration?.Validate();
        foreach (var link in file.Links)
        {
            link.Anchor.Validate(); GridCalibration.Validate(link.UnitsPerTile);
            if (link.Tiles is null || link.Tiles.Length > 4096) throw new InvalidDataException("Invalid footprint.");
        }
    }

    /// <summary>
    /// Per-link status. A link whose HD object, DS1 placement or owned collision changed
    /// outside this workspace is isolated rather than disabling every other link.
    /// </summary>
    private Dictionary<string, string?> ComputeHealth()
    {
        var index = BuildIndex();
        var baselines = (file.Baselines ?? []).Select(b => new LinkedTile(b.X, b.Y)).ToHashSet();
        var next = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var link in Links)
        {
            try
            {
                ValidateCurrent(link, index);
                foreach (var tile in Tiles(link, null, index))
                    if (!baselines.Contains(tile))
                        throw new InvalidDataException($"{link.Name}: missing footprint baseline for ({tile.X}, {tile.Y}).");
                next[link.EntityId] = null;
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or OverflowException)
            { next[link.EntityId] = ex.Message; }
        }
        return next;
    }

    private static int Delta(double value, double anchor, double units) => checked((int)Math.Round((value - anchor) / units, MidpointRounding.AwayFromZero));

    private LinkedTile[] Tiles(PlacementLink link, EntityTransform? value = null, Dictionary<string, PresetEntity?>? index = null)
    {
        var t = value ?? Entity(link, index).Transform;
        int dx = Delta(t.Position.X, link.Anchor.Position.X, link.UnitsPerTile), dy = Delta(t.Position.Z, link.Anchor.Position.Z, link.UnitsPerTile);
        return link.Tiles.Select(p => new LinkedTile(checked(p.X + dx), checked(p.Y + dy))).ToArray();
    }

    // Broken links own nothing: their cells stay editable so the user can repair them.
    private HashSet<LinkedTile> Owned(PresetEntity? changing = null, EntityTransform? value = null)
    {
        var index = BuildIndex();
        return Healthy.SelectMany(l => Tiles(l, l.EntityId == changing?.Id ? value : null, index)).ToHashSet();
    }

    private void ValidateCurrent(PlacementLink link, Dictionary<string, PresetEntity?>? index = null)
    {
        var entity = Entity(link, index);
        if (entity.Transform.Orientation != link.Anchor.Orientation || entity.Transform.Scale != link.Anchor.Scale)
            throw new InvalidDataException($"{link.Name}: linked rotation/scale changed; relink this object.");
        if (link.Unit is { } unit)
        {
            if (unit.Index < 0 || unit.Index >= ds1.Units.Count) throw new InvalidDataException("Linked DS1 record no longer exists.");
            var actual = ds1.Units[unit.Index]; var t = entity.Transform;
            int x = unit.X + Delta(t.Position.X, link.Anchor.Position.X, link.UnitsPerTile / 5);
            int y = unit.Y + Delta(t.Position.Z, link.Anchor.Position.Z, link.UnitsPerTile / 5);
            if (actual.Type != unit.Type || actual.Id != unit.Id || actual.Flags != unit.Flags || actual.X != x || actual.Y != y)
                throw new InvalidDataException($"{link.Name}: linked DS1 placement changed; relink rather than guessing.");
        }
        foreach (var tile in Tiles(link, null, index))
            if (!ds1.FloorOverride(tile)) throw new InvalidDataException($"{link.Name}: owned collision was changed outside the linked workspace.");
    }

    private void ValidateHealthyLinks()
    {
        foreach (var link in Healthy.ToArray()) ValidateCurrent(link);
    }

    private PlacementLink RequireHealthy(PresetEntity entity)
    {
        var link = Find(entity) ?? throw new InvalidOperationException("This model has no DS1 link.");
        if (Health.GetValueOrDefault(entity.Id) is { } reason)
            throw new InvalidOperationException(reason + " Review links to discard it, then link this object again.");
        return link;
    }

    private void Activate()
    {
        // Merge prior edits chronologically so undo from either view remains coherent.
        if (!ReferenceEquals(ds1.History, json.History))
        {
            json.History.Merge(ds1.History);
            ds1.History = json.History;
        }
        json.History.Shared = true;
        ds1.ProtectedTile = (x, y) => Owned().Contains(new(x, y));
        ds1.ProtectedUnit = index => Healthy.Any(l => l.Unit?.Index == index);
    }
    public void ConnectWorkspace() { EnsureReady(); Activate(); }

    public int PaintFloor(int layer, IEnumerable<(int X, int Y)> cells, uint tile)
    {
        EnsureReady(); ValidateStructure(); Activate();
        int changed = 0;
        json.History.Transaction(() =>
        {
            changed = ds1.PaintFloorCore(layer, cells, tile);
            if (changed > 0) ChangeMetadata(Upgraded() with { Fingerprint = ds1.LinkFingerprint() }, null);
        });
        return changed;
    }

    public int AppendUnit(int type, int id, int x, int y, uint flags = 0)
    {
        EnsureReady(); ValidateStructure(); Activate();
        int index = -1;
        json.History.Transaction(() =>
        {
            index = ds1.AppendUnitCore(type, id, x, y, flags);
            ChangeMetadata(Upgraded() with { Fingerprint = ds1.LinkFingerprint() }, null);
        });
        return index;
    }

    public void DeleteUnit(int index)
    {
        EnsureReady(); ValidateStructure(); Activate();
        if (index < 0 || index >= ds1.Units.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (Links.Any(l => l.Unit?.Index == index)) throw new InvalidOperationException("This unit has an HD link. Delete its linked object or unlink it first.");
        json.History.Transaction(() =>
        {
            ds1.DeleteLinkedUnit(index);
            var remaining = Links.Select(l => l.Unit is { } u && u.Index > index ? l with { Unit = u with { Index = u.Index - 1 } } : l).ToArray();
            ChangeMetadata(Upgraded() with { Links = remaining, Fingerprint = ds1.LinkFingerprint() }, null);
        });
    }

    /// <summary>Brings a version 1 file to the current version, restating its fingerprint
    /// in the current form so the upgraded file validates against the same DS1.</summary>
    private PlacementLinkFile Upgraded() => file.Version == CurrentVersion ? file
        : file with { Version = CurrentVersion, Fingerprint = ds1.LinkFingerprint() };

    public byte[] MetadataFor(string sidecarPath, string ds1Path)
    {
        EnsureReady(); ValidateStructure();
        return System.Text.Encoding.UTF8.GetBytes(Serialize(Upgraded() with
        { Ds1Path = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(sidecarPath))!, ds1Path) }));
    }
    public void MarkWorkspaceSaved() { savedMetadata = Serialize(file); HasMetadataChanges = false; }
    private void EnsureReady()
    { if (Warning is not null) throw new InvalidOperationException(Warning); }

    private PlacementLink NewLink(PresetEntity entity, double scale, UnitLink? unit, LinkedTile[] tiles) =>
        new(entity.Id, entity.Name, entity.PreviewModel, entity.Transform, scale, unit, tiles);

    public LinkedTile[] CollisionTiles(PresetEntity entity)
    {
        EnsureReady();
        return Find(entity) is { } link && Health.GetValueOrDefault(entity.Id) is null ? Tiles(link) : [];
    }

    public CollisionOwnership InspectOwnership(LinkedTile tile)
        => InspectOwnershipForTiles([tile])[tile];
    public IReadOnlyDictionary<LinkedTile, CollisionOwnership> InspectOwnershipForTiles(IEnumerable<LinkedTile> cells)
    {
        EnsureReady();
        var requested = cells.ToHashSet();
        var index = BuildIndex();
        var owners = Healthy.SelectMany(l => Tiles(l, null, index).Where(requested.Contains).Select(t => (Tile: t, l.Name)))
            .ToLookup(p => p.Tile, p => p.Name);
        var baselines = file.Baselines.ToDictionary(b => new LinkedTile(b.X, b.Y), b => b.Blocked);
        return requested.ToDictionary(t => t, t => new CollisionOwnership(t, owners[t].ToArray(),
            owners.Contains(t) ? baselines[t] : ds1.FloorOverride(t)));
    }

    private PlacementLink[] ReplaceLink(PresetEntity entity, PlacementLink? replacement) =>
        file.Links.Where(l => l.EntityId != entity.Id).Concat(replacement is null ? [] : new[] { replacement }).ToArray();

    /// <summary>
    /// Replaces this pair's registration. Existing links keep the scale they were created
    /// with, so recalibrating can never silently move anything already placed.
    /// </summary>
    public void SetCalibration(GridCalibration value)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        if (Calibration == value) return;
        file = file with { Calibration = value };
        HasMetadataChanges = Serialize(file) != savedMetadata;
    }

    /// <summary>Fits the scale to every intact unit link in this pair.</summary>
    public GridCalibration SolveCalibrationFromLinks()
    {
        EnsureReady();
        var index = BuildIndex();
        var samples = new List<CalibrationSample>();
        foreach (var link in Healthy)
        {
            if (link.Unit is not { } unit) continue;
            var position = Entity(link, index).Transform.Position;
            var actual = ds1.Units[unit.Index];
            samples.Add(new(position.X, position.Z, actual.X, actual.Y));
        }
        if (samples.Count == 0)
            throw new InvalidOperationException("No intact DS1 unit links to calibrate from. Link at least one object to its DS1 placement, or calibrate from terrain.");
        return GridCalibration.FromSamples(samples);
    }

    /// <summary>
    /// Drops broken links without touching map data. Collision they used to own keeps its
    /// current blocking, exactly as <see cref="Unlink"/> does for an intact link.
    /// </summary>
    public int DiscardBrokenLinks(IEnumerable<string>? entityIds = null)
    {
        EnsureReady();
        var targets = Links.Where(l => Health.GetValueOrDefault(l.EntityId) is not null)
            .Where(l => entityIds is null || entityIds.Contains(l.EntityId, StringComparer.Ordinal)).ToArray();
        if (targets.Length == 0) return 0;
        var dropped = targets.Select(l => l.EntityId).ToHashSet(StringComparer.Ordinal);
        var remaining = Links.Where(l => !dropped.Contains(l.EntityId)).ToArray();
        var next = file with { Links = remaining };
        var previous = file;
        void Set(PlacementLinkFile value)
        { file = value; InvalidateHealth(); HasMetadataChanges = Serialize(file) != savedMetadata; }
        Set(next);
        // Surviving links may have been blocked only by a dropped neighbour's baseline.
        var owned = Owned();
        Set(next with { Baselines = file.Baselines.Where(b => owned.Contains(new(b.X, b.Y))).ToArray() });
        var after = file;
        json.History.Record(() => Set(previous), () => Set(after));
        Activate();
        return targets.Length;
    }

    public void LinkUnit(PresetEntity entity, int index, double unitsPerTile)
    {
        EnsureReady(); GridCalibration.Validate(unitsPerTile); entity.Transform.Validate();
        var existing = Find(entity);
        if (existing is not null)
        {
            RequireHealthy(entity);
            if (existing.Unit is not null) throw new InvalidOperationException("This model already has a unit link.");
            ValidateCurrent(existing);
            if (existing.UnitsPerTile != unitsPerTile) throw new InvalidOperationException("Use the existing link's HD units/tile scale.");
        }
        if (Links.Any(l => l.Unit?.Index == index)) throw new InvalidOperationException("This DS1 placement already belongs to another HD object.");
        if (ds1.GameplayWarning is not null) throw new InvalidOperationException(ds1.GameplayWarning);
        var u = ds1.Units[index];
        if (u.Type is not (1 or 2)) throw new InvalidOperationException("Only object/NPC/monster records can be linked.");
        var link = existing ?? NewLink(entity, unitsPerTile, null, []);
        // A collision link may already have moved. Anchor the new unit to that same original transform.
        link = link with { Unit = new(u.Index, u.Type, u.Id, u.Flags,
            checked(u.X - Delta(entity.Transform.Position.X, link.Anchor.Position.X, unitsPerTile / 5)),
            checked(u.Y - Delta(entity.Transform.Position.Z, link.Anchor.Position.Z, unitsPerTile / 5))) };
        Entity(link); Activate();
        ChangeMetadata(file with { Links = ReplaceLink(entity, link) }, entity);
    }

    public void LinkFootprint(PresetEntity entity, IEnumerable<LinkedTile> cells, double unitsPerTile, bool claimExisting)
    {
        EnsureReady(); GridCalibration.Validate(unitsPerTile); entity.Transform.Validate();
        var existing = Find(entity);
        if (existing is not null) RequireHealthy(entity);
        ValidateHealthyLinks();
        if (existing is not null && existing.UnitsPerTile != unitsPerTile)
            throw new InvalidOperationException("Use the existing link's HD units/tile scale.");
        var tiles = cells.Distinct().ToArray();
        if (tiles.Length > 4096 || (tiles.Length == 0 && existing?.Tiles.Length is not > 0))
            throw new InvalidOperationException("Choose between 1 and 4096 footprint cells.");
        if (existing is not null && Tiles(existing).ToHashSet().SetEquals(tiles)) return;
        var owned = Owned(); var baseline = file.Baselines.ToDictionary(b => new LinkedTile(b.X, b.Y));
        foreach (var tile in tiles)
        {
            bool blocked = ds1.FloorOverride(tile);
            if (!owned.Contains(tile)) baseline[tile] = new(tile.X, tile.Y, claimExisting ? false : blocked);
        }
        var link = existing ?? NewLink(entity, unitsPerTile, null, []);
        int dx = Delta(entity.Transform.Position.X, link.Anchor.Position.X, unitsPerTile);
        int dy = Delta(entity.Transform.Position.Z, link.Anchor.Position.Z, unitsPerTile);
        link = link with { Tiles = tiles.Select(t => new LinkedTile(checked(t.X - dx), checked(t.Y - dy))).ToArray() };
        Entity(link); Activate();
        var nextLinks = ReplaceLink(entity, link.Unit is null && tiles.Length == 0 ? null : link);
        var index = BuildIndex();
        var nextOwned = nextLinks.Where(l => l.EntityId == entity.Id || Health.GetValueOrDefault(l.EntityId) is null)
            .SelectMany(l => Tiles(l, null, index)).ToHashSet();
        var states = owned.Union(nextOwned).Select(t => new TileBaseline(t.X, t.Y, nextOwned.Contains(t) || baseline[t].Blocked)).ToArray();
        json.History.Transaction(() =>
        {
            ds1.SetFloorOverrides(states);
            ChangeMetadata(file with { Links = nextLinks, Baselines = baseline.Where(p => nextOwned.Contains(p.Key)).Select(p => p.Value).ToArray() }, entity);
        }, entity);
    }

    public void AlignUnitToHd(PresetEntity entity)
    {
        EnsureReady();
        var link = RequireHealthy(entity);
        ValidateCurrent(link);
        var unit = link.Unit ?? throw new InvalidOperationException("This model has no linked DS1 unit.");
        int x = Delta(entity.Transform.Position.X, 0, link.UnitsPerTile / 5);
        int y = Delta(entity.Transform.Position.Z, 0, link.UnitsPerTile / 5);
        if (ds1.Units[unit.Index].X == x && ds1.Units[unit.Index].Y == y) return;
        var aligned = unit with
        {
            X = checked(x - Delta(entity.Transform.Position.X, link.Anchor.Position.X, link.UnitsPerTile / 5)),
            Y = checked(y - Delta(entity.Transform.Position.Z, link.Anchor.Position.Z, link.UnitsPerTile / 5))
        };
        json.History.Transaction(() =>
        {
            ds1.LinkedUnitMove = true;
            try { ds1.MoveUnit(unit.Index, x, y); } finally { ds1.LinkedUnitMove = false; }
            ChangeMetadata(file with { Links = ReplaceLink(entity, link with { Unit = aligned }) }, entity);
        }, entity);
    }

    public void DeleteModel(PresetEntity entity)
    {
        EnsureReady(); ValidateStructure(); Activate();
        var link = Find(entity);
        // A broken link's gameplay data was changed outside this workspace; drop the record
        // and the HD model, but never guess which DS1 bytes it still refers to.
        bool broken = link is not null && Health.GetValueOrDefault(entity.Id) is not null;
        var remaining = Links.Where(l => l.EntityId != entity.Id).ToArray();
        var index = BuildIndex();
        var owned = remaining.Where(l => Health.GetValueOrDefault(l.EntityId) is null)
            .SelectMany(l => Tiles(l, null, index)).ToHashSet();
        var baseline = file.Baselines.ToDictionary(b => new LinkedTile(b.X, b.Y));
        var states = (link is null || broken ? [] : Tiles(link, null, index))
            .Select(t => new TileBaseline(t.X, t.Y, owned.Contains(t) || baseline[t].Blocked)).ToArray();
        json.History.Transaction(() =>
        {
            ds1.SetFloorOverrides(states);
            if (!broken && link?.Unit is { } unit)
            {
                ds1.DeleteLinkedUnit(unit.Index);
                remaining = remaining.Select(l => l.Unit is { } u && u.Index > unit.Index ? l with { Unit = u with { Index = u.Index - 1 } } : l).ToArray();
            }
            ChangeMetadata(file with { Links = remaining, Fingerprint = ds1.LinkFingerprint(),
                Baselines = file.Baselines.Where(b => owned.Contains(new(b.X, b.Y))).ToArray() }, entity);
            json.DeleteModel(entity);
        }, entity);
    }

    public void Unlink(PresetEntity entity)
    {
        EnsureReady();
        // Leave current collision in place. Other owners retain shared baselines.
        var link = Find(entity); if (link is null) return;
        var stationary = Health.GetValueOrDefault(entity.Id) is null ? Tiles(link).ToHashSet() : [];
        ChangeMetadata(file with { Links = Links.Where(l => l.EntityId != entity.Id).ToArray(),
            Baselines = file.Baselines.Select(b => stationary.Contains(new(b.X, b.Y)) ? b with { Blocked = true } : b).ToArray() }, entity);
    }

    private void ChangeMetadata(PlacementLinkFile next, PresetEntity? entity)
    {
        var previous = file;
        void Set(PlacementLinkFile value)
        { file = value; InvalidateHealth(); HasMetadataChanges = Serialize(file) != savedMetadata; }
        Set(next); json.History.Record(() => Set(previous), () => Set(next), entity);
    }

    public void Move(PresetEntity entity, EntityTransform target)
    {
        EnsureReady(); target.Validate();
        if (target == entity.Transform) return;
        var link = Find(entity);
        if (link is null) { json.SetTransform(entity, target); return; }
        RequireHealthy(entity);
        ValidateHealthyLinks();
        if (target.Orientation != entity.Transform.Orientation || target.Scale != entity.Transform.Scale)
            throw new InvalidOperationException("Linked movement supports translation. Unlink before rotating or scaling, then review and relink the footprint.");
        var oldOwned = Owned(); var newOwned = Owned(entity, target);
        var before = file;
        var baseline = file.Baselines.ToDictionary(b => new LinkedTile(b.X, b.Y));
        foreach (var tile in newOwned)
        {
            bool blocked = ds1.FloorOverride(tile); // Validates every destination before mutation.
            if (!oldOwned.Contains(tile)) baseline[tile] = new(tile.X, tile.Y, blocked);
        }
        var states = oldOwned.Union(newOwned).Select(p => new TileBaseline(p.X, p.Y, newOwned.Contains(p) || baseline[p].Blocked)).ToArray();
        json.History.Transaction(() =>
        {
            if (link.Unit is { } u)
            {
                int x = checked(u.X + Delta(target.Position.X, link.Anchor.Position.X, link.UnitsPerTile / 5));
                int y = checked(u.Y + Delta(target.Position.Z, link.Anchor.Position.Z, link.UnitsPerTile / 5));
                ds1.LinkedUnitMove = true;
                try { ds1.MoveUnit(u.Index, x, y); } finally { ds1.LinkedUnitMove = false; }
            }
            ds1.SetFloorOverrides(states);
            var after = file with { Baselines = baseline.Where(p => newOwned.Contains(p.Key)).Select(p => p.Value).ToArray() };
            file = after;
            json.History.Record(() => { file = before; InvalidateHealth(); }, () => { file = after; InvalidateHealth(); });
            json.SetTransform(entity, target);
        }, entity);
    }

    private static string Serialize(PlacementLinkFile value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });

    public void SaveMetadata(string? path = null, string? ds1Path = null)
    {
        EnsureReady(); path ??= SidecarPath;
        var output = Upgraded() with { Ds1Path = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(path))!, ds1Path ?? ds1.SourcePath) };
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(temp, Serialize(output));
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
            if (path == SidecarPath) { file = Upgraded(); savedMetadata = Serialize(file); HasMetadataChanges = false; }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static string? LinkedDs1Path(string jsonPath)
    {
        string path = jsonPath + Suffix;
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Link file exceeds 8 MiB.");
        var data = JsonSerializer.Deserialize<PlacementLinkFile>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid link file.");
        if (data.Version is not (1 or CurrentVersion) || string.IsNullOrWhiteSpace(data.Ds1Path) || !data.Ds1Path.EndsWith(".ds1", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid linked DS1 path.");
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, data.Ds1Path));
    }

    public void Reset()
    {
        var previous = file; var previousWarning = Warning;
        var calibration = file.Calibration;
        try
        {
            Warning = null;
            // A reset abandons links, not the pair's measured registration.
            file = Empty() with { Calibration = calibration };
            SaveMetadata();
            InvalidateHealth();
        }
        catch { file = previous; Warning = previousWarning; throw; }
        json.History.Clear(); ds1.History.Clear();
    }

    public string ExportPair(string parentFolder)
    {
        EnsureReady(); ValidateStructure();
        string name = Path.GetFileNameWithoutExtension(json.SourcePath) + "-linked-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        string destination = Path.Combine(parentFolder, name), stage = Path.Combine(parentFolder, ".rle-export-" + Guid.NewGuid().ToString("N"));
        string jsonRelative = PresetPairing.Split(json.SourcePath, "hd/env/preset")?.Relative ?? Path.GetFileName(json.SourcePath);
        string ds1Relative = PresetPairing.Split(ds1.SourcePath, "global/tiles")?.Relative ?? Path.GetFileName(ds1.SourcePath);
        string jsonOut = Path.Combine(stage, "data", "hd", "env", "preset", jsonRelative);
        string ds1Out = Path.Combine(stage, "data", "global", "tiles", ds1Relative);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(jsonOut)!); Directory.CreateDirectory(Path.GetDirectoryName(ds1Out)!);
            var jsonBytes = json.Serialize(); var ds1Bytes = ds1.Serialize();
            File.WriteAllBytes(jsonOut, jsonBytes); File.WriteAllBytes(ds1Out, ds1Bytes);
            if (!PresetDocument.Load(jsonOut).Serialize().SequenceEqual(jsonBytes) || !Ds1CollisionDocument.Load(ds1Out).Serialize().SequenceEqual(ds1Bytes))
                throw new InvalidDataException("Linked export did not round-trip.");
            SaveMetadata(jsonOut + Suffix, ds1Out);
            var verified = new PlacementLinks(PresetDocument.Load(jsonOut), Ds1CollisionDocument.Load(ds1Out));
            if (verified.Warning is not null) throw new InvalidDataException(verified.Warning);
            // Broken links are exported as they are. Refusing would strand every other
            // edit in the pair over a record the user may still want to repair; the
            // reloaded copy reports the same links as broken, which is faithful.
            if (verified.BrokenLinkCount != BrokenLinkCount)
                throw new InvalidDataException("Exported links do not reload with the same state as the source pair.");
            Directory.Move(stage, destination);
            json.MarkSaved(); ds1.MarkSaved(); savedMetadata = Serialize(file); HasMetadataChanges = false;
            return Path.Combine(destination, "data", "hd", "env", "preset", jsonRelative);
        }
        finally
        {
            // This unique staging folder is created by this method beneath the chosen parent.
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }
}
