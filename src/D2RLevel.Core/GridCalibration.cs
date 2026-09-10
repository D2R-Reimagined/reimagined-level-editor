using System.Globalization;

namespace D2RLevel.Core;

/// <summary>Where a pair's HD-to-DS1 scale came from. Provenance is shown to the
/// user because an unverified default silently corrupts every spatial link.</summary>
public enum CalibrationSource
{
    /// <summary>The historical 10 HD units/tile assumption. Never verified for this pair.</summary>
    Default,
    /// <summary>A number the user typed.</summary>
    Manual,
    /// <summary>Solved from the terrain mesh extent against the DS1 tile grid.</summary>
    Terrain,
    /// <summary>Solved from HD objects already linked to DS1 placements.</summary>
    Links,
}

/// <summary>One HD position paired with the DS1 subtile it belongs to.</summary>
public sealed record CalibrationSample(double HdX, double HdZ, int SubtileX, int SubtileY);

/// <summary>
/// The HD-to-DS1 registration for one JSON/DS1 pair. The editor's whole cross-format
/// story divides by this number, so it carries its provenance and its fit error
/// rather than presenting a guess and a verified measurement identically.
/// </summary>
public sealed record GridCalibration(double UnitsPerTile, CalibrationSource Source, int Samples, double? MaxDriftTiles)
{
    /// <summary>The long-standing assumption: 10 HD units per tile, two per subtile.</summary>
    public const double FallbackUnitsPerTile = 10;

    /// <summary>Drift above this many tiles means the fit does not describe the scene.</summary>
    public const double DriftTolerance = 1.0;

    public static GridCalibration Unverified { get; } = new(FallbackUnitsPerTile, CalibrationSource.Default, 0, null);

    public static GridCalibration Typed(double unitsPerTile)
    {
        Validate(unitsPerTile);
        return new(unitsPerTile, CalibrationSource.Manual, 0, null);
    }

    /// <summary>HD units per DS1 subtile. Five subtiles span one tile.</summary>
    public double UnitsPerSubtile => UnitsPerTile / 5;

    /// <summary>True when this pair still relies on the unverified default.</summary>
    public bool IsUnverified => Source == CalibrationSource.Default;

    /// <summary>Set when a solve succeeded but its residual is too large to trust.</summary>
    public bool IsPoorFit => MaxDriftTiles > DriftTolerance;

    public static void Validate(double unitsPerTile)
    {
        if (!double.IsFinite(unitsPerTile) || unitsPerTile < .01 || unitsPerTile > 10000)
            throw new InvalidDataException("HD units/tile must be a finite number between 0.01 and 10000.");
    }

    /// <summary>Throws when this record could not have been produced by the solvers below.</summary>
    public void Validate()
    {
        Validate(UnitsPerTile);
        if (!Enum.IsDefined(Source)) throw new InvalidDataException("Unknown calibration source.");
        if (Samples < 0 || Samples > 100000) throw new InvalidDataException("Invalid calibration sample count.");
        if (MaxDriftTiles is { } drift && (!double.IsFinite(drift) || drift < 0))
            throw new InvalidDataException("Invalid calibration drift.");
        if (Source is CalibrationSource.Default or CalibrationSource.Manual && Samples != 0)
            throw new InvalidDataException("An unsolved calibration cannot carry samples.");
        if (Source is CalibrationSource.Terrain or CalibrationSource.Links && Samples < 1)
            throw new InvalidDataException("A solved calibration must record its samples.");
    }

    /// <summary>
    /// Fits HD = subtile x scale through the origin, matching the origin convention the
    /// rest of the editor uses. Reports the worst sample's disagreement in tiles so a
    /// scene whose origin is actually offset shows up as a bad fit instead of passing.
    /// </summary>
    public static GridCalibration FromSamples(IReadOnlyList<CalibrationSample> samples)
    {
        if (samples is null || samples.Count == 0)
            throw new InvalidOperationException("Calibration needs at least one linked reference point.");
        double product = 0, square = 0;
        foreach (var s in samples)
        {
            if (!double.IsFinite(s.HdX) || !double.IsFinite(s.HdZ))
                throw new InvalidDataException("Calibration sample has a non-finite HD position.");
            product += s.HdX * s.SubtileX + s.HdZ * s.SubtileY;
            square += (double)s.SubtileX * s.SubtileX + (double)s.SubtileY * s.SubtileY;
        }
        if (square <= 0)
            throw new InvalidOperationException("Every reference point sits at the DS1 origin; move one before calibrating.");
        double unitsPerSubtile = product / square;
        if (!double.IsFinite(unitsPerSubtile) || unitsPerSubtile <= 0)
            throw new InvalidOperationException("Reference points do not describe a positive scale. Check that the linked units match their models.");
        double drift = 0;
        foreach (var s in samples)
        {
            drift = Math.Max(drift, Math.Abs(s.HdX / unitsPerSubtile - s.SubtileX) / 5);
            drift = Math.Max(drift, Math.Abs(s.HdZ / unitsPerSubtile - s.SubtileY) / 5);
        }
        double unitsPerTile = unitsPerSubtile * 5;
        Validate(unitsPerTile);
        return new(unitsPerTile, CalibrationSource.Links, samples.Count, drift);
    }

    /// <summary>
    /// Estimates the scale from the terrain mesh extent against the DS1 tile grid.
    /// Both axes are measured independently; their disagreement, projected across the
    /// longer edge of the map, becomes the reported drift.
    /// </summary>
    public static GridCalibration FromTerrain(double hdWidth, double hdDepth, int tilesWide, int tilesHigh)
    {
        if (tilesWide < 1 || tilesHigh < 1) throw new InvalidOperationException("The DS1 has no tile grid to measure against.");
        if (!double.IsFinite(hdWidth) || !double.IsFinite(hdDepth) || hdWidth <= 0 || hdDepth <= 0)
            throw new InvalidOperationException("Terrain has no measurable extent in this scene.");
        double byWidth = hdWidth / tilesWide, byDepth = hdDepth / tilesHigh;
        double unitsPerTile = (byWidth + byDepth) / 2;
        Validate(unitsPerTile);
        // Axis disagreement accumulates across the map; report it where it is largest.
        double drift = Math.Abs(byWidth - byDepth) / unitsPerTile * Math.Max(tilesWide, tilesHigh);
        return new(unitsPerTile, CalibrationSource.Terrain, 2, drift);
    }

    public string Describe() => Source switch
    {
        CalibrationSource.Default => $"{Format(UnitsPerTile)} HD units/tile · unverified default. Calibrate before trusting placement.",
        CalibrationSource.Manual => $"{Format(UnitsPerTile)} HD units/tile · entered manually.",
        CalibrationSource.Terrain => $"{Format(UnitsPerTile)} HD units/tile · measured from terrain extent{DriftSuffix()}",
        CalibrationSource.Links => $"{Format(UnitsPerTile)} HD units/tile · solved from {Samples} linked object{(Samples == 1 ? "" : "s")}{DriftSuffix()}",
        _ => Format(UnitsPerTile) + " HD units/tile",
    };

    /// <summary>Non-null when the user should look at this calibration before relying on it.</summary>
    public string? Warning => Source switch
    {
        CalibrationSource.Default => "This pair uses the unverified 10 HD units/tile default. Unit links, footprint suggestions and NPC placement all divide by it.",
        _ when IsPoorFit && Source == CalibrationSource.Terrain =>
            $"Terrain measures {Format(UnitsPerTile)} units/tile but its two axes disagree by about {MaxDriftTiles:F1} tiles across the map. The terrain mesh may extend past the DS1 grid, or the scene may use an origin offset this calibration cannot model.",
        _ when IsPoorFit =>
            $"The best fit still misplaces a reference point by about {MaxDriftTiles:F1} tiles. Existing links may disagree with each other, or this scene's HD origin is offset from the DS1 origin.",
        _ => null,
    };

    private string DriftSuffix() => MaxDriftTiles is not { } drift ? "." :
        drift < .05 ? " · exact fit." : $" · worst point off by {drift:F2} tiles.";

    private static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
