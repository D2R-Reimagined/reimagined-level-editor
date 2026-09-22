using D2RLevel.Core;

internal static class GameplayCatalogChecks
{
    public static void Run(string folder, Action<bool, string> check, Action<Action, string> throws)
    {
        string root = Path.Combine(folder, "gameplay-catalog");
        Directory.CreateDirectory(Path.Combine(root, "hd")); Directory.CreateDirectory(Path.Combine(root, "global", "excel"));
        void Table(string name, string text) => File.WriteAllText(Path.Combine(root, "global", "excel", name + ".txt"), text);
        Table("monpreset", "Act\tPlace\n1\tmerchant\n2\tfoe\n1\t\n1\tfoe\n1\tBoss\n1\tplace_pack\n1\tmissing\n1\tdisabled\n");
        Table("monstats", "Id\tNameStr\tnpc\tenabled\nfoe\tFallen\t0\t1\nmerchant\tTrader\t1\t1\ndisabled\tDisabled\t0\t0\nunmapped\tNot placed\t0\t1\n");
        Table("superuniques", "Superunique\tName\tClass\nBoss\tBig Boss\tfoe\nUnusedBoss\tUnused Boss\tfoe\n");
        Table("monplace", "code\nplace_pack\n");
        // Index is authoritative and act-specific; *ID from Objects must never leak into DS1.
        Table("objpreset", "Index\tAct\tObjectClass\n45\t1\tChest\n0\t2\tChest\n0\t1\tTorch\n9\t1\tChest\n");
        Table("objects", "Class\tName\t*Description\t*ID\nChest\tchest\tLarge chest\t999\nTorch\ttorch\tTorch\t1000\nUnmapped\tunmapped\tNo preset\t1001\n");
        var resolver = new AssetResolver(root);
        GameplayCatalog Load() => GameplayCatalog.Load(resolver, null, 1);
        var catalog = Load();
        GameplayAsset Entry(string key) => catalog.Assets.Single(a => a.Key == key);
        check(Entry("merchant") is { Kind: GameplayAssetKind.Npc, Type: 1, Id: 0, CanPlace: true }, "browser resolves NPC through act-local preset slot zero");
        check(Entry("foe") is { Id: 2, CanPlace: true } && Entry("foe").References[0].Line == 5, "blank monster slots count and other acts do not shift IDs");
        check(Entry("").Id == 1 && !Entry("").CanPlace, "empty monster preset retains slot and cannot be placed");
        check(GameplayCatalog.Load(resolver, null, 2).Assets.Single(a => a.Key == "foe") is { Id: 0, CanPlace: true }, "same monster has the other act's own ID");
        check(Entry("Boss") is { Kind: GameplayAssetKind.Superunique, Id: 3, MonsterClass: "foe", CanPlace: true } && Entry("Boss").References.Count == 3, "superunique retains its own preset ID and base monster reference");
        check(!Entry("disabled").CanPlace && !Entry("missing").CanPlace && !Entry("place_pack").CanPlace, "disabled, unresolved and runtime spawn entries are browse-only");
        check(catalog.Sources.Single(t => t.Name == "monplace").Warning is null && Entry("place_pack").References.Count == 2, "single-column MonPlace table resolves spawn metadata without a false malformed-table warning");
        check(!Entry("unmapped").CanPlace && !Entry("UnusedBoss").CanPlace && !Entry("Unmapped").CanPlace, "definitions without current-act mappings remain searchable but cannot place");
        check(Entry("merchant").Matches("trader") && Entry("Boss").Matches("big boss"), "browser search includes display names and definition keys");
        var chests = catalog.Assets.Where(a => a.Key == "Chest").ToArray();
        check(chests.Length == 2 && chests.Select(a => a.Id).Order().SequenceEqual(new int?[] { 9, 45 }) && chests.All(a => a.CanPlace), "objects use sparse explicit preset indices rather than row order or Objects ID");
        check(Entry("Torch") is { Type: 2, Id: 0, CanPlace: true }, "object index zero is a valid act-local placement");
        check(catalog.Validate(chests[0]) == chests[0], "valid recipe resolves against a fresh catalog");
        throws(() => catalog.Validate(Entry("unmapped")), "unmapped recipe rejected before editing");
        throws(() => GameplayCatalog.Load(resolver, null, 6), "unsupported act rejected");
        string mod = Path.Combine(folder, "gameplay-mod"); Directory.CreateDirectory(Path.Combine(mod, "global", "excel"));
        File.WriteAllText(Path.Combine(mod, "global", "excel", "monpreset.txt"), "Act\tPlace\n1\tfoe\n");
        var overridden = GameplayCatalog.Load(resolver, mod, 1);
        check(overridden.Assets.Single(a => a.Key == "foe") is { Id: 0, CanPlace: true } && !overridden.Assets.Single(a => a.Key == "merchant").CanPlace, "table-only mod overrides whole preset table with base definition fallback");
        check(overridden.Sources.Single(t => t.Name == "monpreset").IsOverride, "catalog identifies effective workspace source");
        throws(() => overridden.Validate(Entry("foe")), "changed saved monster mapping cannot place a stale selection");
        Table("objpreset", "Index\tAct\tObjectClass\n45\t1\tChest\n45\t1\tTorch\n");
        check(Load().Assets.Where(a => a.Type == 2 && a.Id == 45).All(a => !a.CanPlace), "duplicate object indices disable every conflicting recipe");
        throws(() => Load().Validate(chests[0]), "changed object mappings cannot place a stale selection");
        Table("superuniques", "Superunique\tName\tClass\nBoss\tOne\tfoe\nBoss\tTwo\tmerchant\n");
        check(!Load().Assets.Single(a => a.Key == "Boss").CanPlace, "duplicate superunique definitions are not guessed");
        Table("monstats", "Id\tNameStr\tnpc\tenabled\nfoe\tOne\t0\t1\nfoe\tTwo\t0\t1\n");
        check(!Load().Assets.Single(a => a.Key == "foe").CanPlace, "duplicate monster definitions are not guessed");
        File.WriteAllText(Path.Combine(mod, "global", "excel", "objpreset.txt"), "Index\tIndex\n0\t0\n");
        check(GameplayCatalog.Load(resolver, mod, 1).Assets.Where(a => a.Type == 2).All(a => !a.CanPlace), "malformed object override cannot fall back to base placement IDs");
        // A validated recipe creates DS1 gameplay only, with a single shared undo transaction.
        string jsonPath = Path.Combine(root, "scene.json"), mapPath = Path.Combine(root, "scene.ds1");
        File.WriteAllText(jsonPath, "{\"entities\":[],\"preserved\":42}");
        var json = PresetDocument.Load(jsonPath); var map = Ds1CollisionDocument.Create(mapPath, 8, 8, 1);
        var links = new PlacementLinks(json, map); links.ConnectWorkspace();
        var before = map.Serialize(); var jsonBefore = json.Serialize();
        var recipe = catalog.Validate(Entry("merchant"));
        links.AppendUnit(recipe.Type, recipe.Id!.Value, 10, 15, 1);
        check(map.Units is [{ Type: 1, Id: 0, X: 10, Y: 15, Flags: 1 }] && json.Serialize().SequenceEqual(jsonBefore), "catalog placement preserves exact type, preset ID, coordinates and flags without HD mutation");
        json.Undo(); check(map.Serialize().SequenceEqual(before) && links.Warning is null, "one shared undo removes catalog placement and preserves links");
        json.Redo(); var placed = map.Serialize();
        throws(() => links.AppendUnit(recipe.Type, recipe.Id.Value, -1, 15), "out-of-bounds browser placement rejected");
        check(map.Serialize().SequenceEqual(placed), "rejected placement leaves previous scene intact");
    }
}
