using D2RLevel.Core;

internal static class CalibrationChecks
{
    public static void Run(string folder, Action<bool, string> check, Action<Action, string> throws)
    {
        // A pair whose HD origin coincides with the DS1 origin at exactly 10 units/tile.
        static CalibrationSample Exact(int subX, int subY, double unitsPerTile = 10) =>
            new(subX * unitsPerTile / 5, subY * unitsPerTile / 5, subX, subY);

        var unverified = GridCalibration.Unverified;
        check(unverified.UnitsPerTile == 10 && unverified.IsUnverified && unverified.Warning is not null,
            "the default calibration reports itself as unverified");
        check(unverified.UnitsPerSubtile == 2, "five subtiles span one tile");

        var typed = GridCalibration.Typed(12.5);
        check(typed.Source == CalibrationSource.Manual && !typed.IsUnverified && typed.Warning is null && typed.Samples == 0,
            "a typed scale is manual, verified and sample-free");
        throws(() => GridCalibration.Typed(0), "reject a zero scale");
        throws(() => GridCalibration.Typed(double.NaN), "reject a non-finite scale");
        throws(() => GridCalibration.Typed(-10), "reject a negative scale");
        throws(() => GridCalibration.Typed(20000), "reject an absurd scale");

        var solved = GridCalibration.FromSamples([Exact(5, 0), Exact(0, 10), Exact(25, 30)]);
        check(Math.Abs(solved.UnitsPerTile - 10) < 1e-9 && solved.Source == CalibrationSource.Links && solved.Samples == 3,
            "consistent reference points solve the exact scale");
        check(solved.MaxDriftTiles < 1e-9 && !solved.IsPoorFit && solved.Warning is null, "an exact fit reports no drift");

        var wide = GridCalibration.FromSamples([Exact(10, 10, 17.5), Exact(30, 5, 17.5)]);
        check(Math.Abs(wide.UnitsPerTile - 17.5) < 1e-9, "the solver recovers a non-default scale");

        // One point that disagrees with the rest must not be averaged away silently.
        var conflicted = GridCalibration.FromSamples([Exact(20, 20), Exact(40, 40), new(4, 4, 40, 40)]);
        check(conflicted.IsPoorFit && conflicted.Warning is not null, "a contradictory reference point is reported, not averaged away");

        throws(() => GridCalibration.FromSamples([]), "reject calibrating from no reference points");
        throws(() => GridCalibration.FromSamples([new(10, 10, 0, 0)]), "reject reference points that all sit on the DS1 origin");
        throws(() => GridCalibration.FromSamples([new(-40, -40, 10, 10)]), "reject reference points implying a negative scale");
        throws(() => GridCalibration.FromSamples([new(double.NaN, 0, 5, 0)]), "reject a non-finite reference position");

        var terrain = GridCalibration.FromTerrain(400, 300, 40, 30);
        check(Math.Abs(terrain.UnitsPerTile - 10) < 1e-9 && terrain.Source == CalibrationSource.Terrain && terrain.MaxDriftTiles < 1e-9,
            "terrain extent measures the scale when both axes agree");
        var skewed = GridCalibration.FromTerrain(400, 330, 40, 30);
        check(skewed.IsPoorFit && skewed.Warning is not null, "terrain axes that disagree report drift across the map");
        throws(() => GridCalibration.FromTerrain(0, 300, 40, 30), "reject terrain with no measurable extent");
        throws(() => GridCalibration.FromTerrain(400, 300, 0, 30), "reject a DS1 with no tile grid");

        throws(() => new GridCalibration(10, CalibrationSource.Links, 0, 0).Validate(), "a solved calibration must record its samples");
        throws(() => new GridCalibration(10, CalibrationSource.Manual, 4, 0).Validate(), "an unsolved calibration cannot claim samples");
        throws(() => new GridCalibration(10, (CalibrationSource)99, 0, 0).Validate(), "reject an unknown calibration source");
        throws(() => new GridCalibration(10, CalibrationSource.Manual, 0, -1).Validate(), "reject negative drift");

        // Persistence and the repair flow, against a real linked pair.
        var fixture = LinkFixture.Create(folder);
        var (json, ds1, links) = fixture;
        check(links.Calibration.IsUnverified, "a fresh pair starts on the unverified default");

        var entity = json.Entities[0];
        links.LinkUnit(entity, 0, 10);
        var fromLinks = links.SolveCalibrationFromLinks();
        check(fromLinks.Source == CalibrationSource.Links && fromLinks.Samples == 1, "an existing unit link is a usable reference point");

        links.SetCalibration(fromLinks);
        check(links.Calibration == fromLinks && links.HasMetadataChanges, "setting a calibration marks the sidecar dirty");
        links.SaveMetadata();
        var reopened = new PlacementLinks(PresetDocument.Load(json.SourcePath), Ds1CollisionDocument.Load(ds1.SourcePath));
        check(reopened.Calibration == fromLinks, "the calibration survives a sidecar round trip");
        check(!links.HasMetadataChanges, "saving clears the pending calibration change");

        var pinned = links.Find(entity)!.UnitsPerTile;
        links.SetCalibration(GridCalibration.Typed(25));
        check(links.Find(entity)!.UnitsPerTile == pinned && links.Calibration.UnitsPerTile == 25,
            "recalibrating never rewrites the scale an existing link was created with");
        throws(() => links.SetCalibration(null!), "reject a null calibration");
        throws(() => links.SetCalibration(new(0, CalibrationSource.Manual, 0, 0)), "reject persisting an invalid calibration");

        // A version 1 sidecar predates calibration and must still load.
        var legacy = LinkFixture.Create(folder);
        legacy.Links.LinkUnit(legacy.Json.Entities[0], 0, 10);
        legacy.Links.SaveMetadata();
        string sidecar = legacy.Json.SourcePath + PlacementLinks.Suffix;
        // Version 1 also predates the current fingerprint form, so restate both.
        File.WriteAllText(sidecar, File.ReadAllText(sidecar)
            .Replace("\"Version\": 2", "\"Version\": 1")
            .Replace(legacy.Ds1.LinkFingerprint(), legacy.Ds1.LegacyLinkFingerprint()));
        var migrated = new PlacementLinks(PresetDocument.Load(legacy.Json.SourcePath), Ds1CollisionDocument.Load(legacy.Ds1.SourcePath));
        check(migrated.Warning is null && migrated.HasLinks && migrated.Calibration.IsUnverified,
            "a version 1 sidecar loads and falls back to the unverified default");
        migrated.SaveMetadata();
        check(File.ReadAllText(sidecar).Contains("\"Version\": 2"), "saving upgrades a version 1 sidecar in place");
    }
}
