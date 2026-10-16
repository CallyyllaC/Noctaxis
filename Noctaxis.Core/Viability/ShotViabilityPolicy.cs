namespace Noctaxis.Core.Viability;

/// <summary>
/// Every threshold, weight and curve of the shot-viability score, in one place. Each is explained, with its
/// representative values, in docs/shot-viability-scoring.md; change them together. A change that alters any
/// score must also change <see cref="Id"/>, so a score can always be traced to the rules that produced it.
/// </summary>
public static class ShotViabilityPolicy
{
    /// <summary>The scoring policy's identifier.</summary>
    public const string Id = "shot-viability-v2";

    // ---- Weights: relative importance, out of 100 for a deep-sky target. --------------------------------

    // v2 added light pollution (10). It and the Moon both brighten the sky, so the points came mostly from
    // the sky's other factors (cloud, darkness, altitude, Moon) and one each from the three framing factors.
    public const double CloudCoverWeight = 18;
    public const double DarknessWeight = 13;
    public const double TargetAltitudeWeight = 13;
    public const double TerrainClearanceWeight = 15;
    public const double UsefulWindowWeight = 10;
    public const double MoonWeight = 9;
    public const double LightPollutionWeight = 10;
    public const double FramePositionWeight = 4;
    public const double FramingFitWeight = 4;
    public const double ImageSamplingWeight = 4;

    public static double WeightOf(ShotViabilityMetric metric) => metric switch
    {
        ShotViabilityMetric.CloudCover => CloudCoverWeight,
        ShotViabilityMetric.Darkness => DarknessWeight,
        ShotViabilityMetric.TargetAltitude => TargetAltitudeWeight,
        ShotViabilityMetric.TerrainClearance => TerrainClearanceWeight,
        ShotViabilityMetric.UsefulWindow => UsefulWindowWeight,
        ShotViabilityMetric.Moon => MoonWeight,
        ShotViabilityMetric.LightPollution => LightPollutionWeight,
        ShotViabilityMetric.FramePosition => FramePositionWeight,
        ShotViabilityMetric.FramingFit => FramingFitWeight,
        ShotViabilityMetric.ImageSampling => ImageSamplingWeight,
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };

    // ---- Cloud cover (total cloud, %). ------------------------------------------------------------------

    /// <summary>Little penalty to 10%, acceptable to 25%, half at 35%, very poor beyond 60%.</summary>
    public static readonly LogisticCurve CloudCover = new(Midpoint: 35, Scale: 8, From: 0, To: 100, Decreasing: true);
    /// <summary>Cover at or above this is critical: the forecast says the sky is effectively closed.</summary>
    public const double CloudCoverCriticalPercent = 90;
    /// <summary>A forecast further than this from the planned moment is not used.</summary>
    public static readonly TimeSpan MaximumForecastOffset = TimeSpan.FromMinutes(90);

    // ---- Darkness (the Sun's altitude, degrees). ------------------------------------------------------------

    public const double AstronomicalDarknessDegrees = -18;
    public const double NauticalDarknessDegrees = -12;
    public const double CivilTwilightDegrees = -6;

    /// <summary>Excellent at astronomical darkness, acceptable at nautical, unsuitable in daylight.</summary>
    public static readonly AnchoredCurve Darkness = new(
        (-18, 100), (-15, 88), (-12, 65), (-9, 35), (-6, 12), (-3, 4), (0, 0));

    // ---- Target altitude (degrees). -----------------------------------------------------------------------

    public const double AcceptableAltitudeDegrees = 30;
    public const double FavourableAltitudeDegrees = 45;

    /// <summary>Deep sky: rising slowly near the horizon, acceptable at 30°, excellent from 45°.</summary>
    public static readonly AnchoredCurve DeepSkyAltitude = new(
        (0, 0), (10, 8), (20, 30), (30, 65), (37.5, 87), (45, 100));

    /// <summary>
    /// The Sun and Moon: low is often the picture (a moonrise over a ridge), so the curve only discourages a
    /// disc that is half set, and gently the thickest haze just above the horizon.
    /// </summary>
    public static readonly AnchoredCurve SolarSystemAltitude = new(
        (-0.5, 0), (0, 40), (3, 70), (10, 90), (20, 100));

    // ---- Terrain clearance (target altitude above the terrain horizon at its azimuth, degrees). -------------

    /// <summary>Nearly binary: zero when hidden, a narrow transition to about 2° of clearance.</summary>
    public static readonly LogisticCurve TerrainClearance = new(Midpoint: 1, Scale: 0.35, From: 0, To: 90);
    /// <summary>Clearance up to this is reported as marginal.</summary>
    public const double MarginalClearanceDegrees = 2;
    /// <summary>
    /// Deep sky only: the most a clear target loses for landscape entering the frame, reached when terrain
    /// fills <see cref="LandscapeFullPenaltyCoverage"/> of the frame's height. Landscape in a Sun or Moon
    /// picture is usually the point, so it costs those nothing.
    /// </summary>
    public const double LandscapeMaximumPenalty = 0.25;
    public const double LandscapeFullPenaltyCoverage = 0.5;

    // ---- Useful time window (hours of favourable time remaining from the planned moment). ------------------

    /// <summary>Under an hour poor, an hour acceptable, two to three favourable, then diminishing returns.</summary>
    public static readonly SaturatingCurve UsefulWindow = new(TimeConstant: 0.95);
    /// <summary>How far ahead the window is followed; longer scores the same.</summary>
    public static readonly TimeSpan UsefulWindowHorizon = TimeSpan.FromHours(12);
    /// <summary>How far behind and ahead of the planned moment window paths are calculated and cached.</summary>
    public static readonly TimeSpan WindowPathsBefore = TimeSpan.FromHours(6);
    public static readonly TimeSpan WindowPathsAfter = TimeSpan.FromHours(30);
    public static readonly TimeSpan WindowPathsInterval = TimeSpan.FromMinutes(10);

    // ---- Moon interference. --------------------------------------------------------------------------------

    /// <summary>The Moon's penalty by illuminated fraction (0 to 1), before separation and altitude.</summary>
    public static readonly AnchoredCurve MoonIllumination = new(
        (0, 100), (0.2, 85), (0.5, 50), (0.75, 25), (1, 10));
    /// <summary>
    /// How much of that penalty applies at a separation from the target (degrees): all of it within 30°, easing
    /// to 60% opposite. A score, so read as a percentage.
    /// </summary>
    public static readonly AnchoredCurve MoonSeparation = new(
        (0, 100), (30, 100), (60, 80), (90, 65), (180, 60));
    /// <summary>The Moon's altitude over which its effect rises from none (on the horizon) to full.</summary>
    public const double MoonFullEffectAltitudeDegrees = 15;

    // ---- Light pollution (David Lorenz atlas, artificial zenith brightness). ------------------------------

    /// <summary>
    /// Scored on the magnitudes of sky darkness lost to artificial light, Δm = 2.5 log10(1 + LPI), where LPI is the
    /// atlas's artificial zenith brightness as a multiple of the natural sky. Near-natural skies lose almost
    /// nothing; suburban glow (about 1.5 mag) is half marks; a city (5 mag) keeps a floor, because narrowband and
    /// bright targets are still possible there.
    /// </summary>
    public static readonly AnchoredCurve LightPollution = new(
        (0, 100), (0.1, 97), (0.3, 90), (0.75, 75), (1.2, 60), (1.5, 52), (2.5, 30), (3.6, 14), (5, 5));
    /// <summary>The natural zenith sky brightness the atlas's ratio is relative to, V mag/arcsec².</summary>
    public const double NaturalSkyMagnitude = 22.0;
    /// <summary>Plain-language bands, by the ratio of artificial to natural brightness.</summary>
    public const double DarkSiteRatio = 0.11, RuralRatio = 0.33, SomePollutionRatio = 1, SuburbanRatio = 3, BrightSuburbanRatio = 9;

    public static double MagnitudesLost(double ratio) => 2.5 * Math.Log10(1 + ratio);

    // ---- Framing. ----------------------------------------------------------------------------------------

    /// <summary>
    /// Where the target is in the frame, as the larger of its two offsets from the centre (0 centre, 1 edge):
    /// full marks inside 80%, falling to 50 at the edge, 0 outside.
    /// </summary>
    public static readonly AnchoredCurve FramePosition = new((0, 100), (0.8, 100), (1, 50));
    public const double FrameNearEdge = 0.8;

    /// <summary>
    /// How much of the frame the target fills, the larger of major axis to long side and minor to short
    /// (best alignment): comfortable to 80%, tight to 100%, then a gentle decline to a floor, because a
    /// close-up or a mosaic is a choice, not an impossibility.
    /// </summary>
    public static readonly AnchoredCurve FramingFit = new((0, 100), (0.8, 100), (1, 65), (1.5, 40), (3, 25));
    public const double FramingComfortableFill = 0.8;

    // ---- Image sampling (pixels across the target's smaller axis). -----------------------------------------

    /// <summary>Under 25 px very limited, 25–50 small, 50–100 modest, 100–200 good, over 200 excellent.</summary>
    public static readonly AnchoredCurve ImageSampling = new(
        (0, 0), (12.5, 10), (25, 30), (50, 55), (100, 75), (200, 95), (400, 100));
    public const double SamplingVeryLimitedPixels = 25;
    public const double SamplingSmallPixels = 50;
    public const double SamplingModestPixels = 100;
    public const double SamplingGoodPixels = 200;

    /// <summary>Nominal apparent diameters, arcminutes: the real ones vary by a few per cent through the year.</summary>
    public const double SunDiameterArcminutes = 32.0;
    public const double MoonDiameterArcminutes = 31.1;

    // ---- Severity and the overall rating. ------------------------------------------------------------------

    /// <summary>A metric scoring below this is a caution.</summary>
    public const double CautionBelowScore = 65;

    public const double ExcellentFrom = 80;
    public const double GoodFrom = 65;
    public const double FairFrom = 45;
    public const double PoorFrom = 25;

    public static ShotViabilityRating RatingFor(double score) =>
        score >= ExcellentFrom ? ShotViabilityRating.Excellent
        : score >= GoodFrom ? ShotViabilityRating.Good
        : score >= FairFrom ? ShotViabilityRating.Fair
        : score >= PoorFrom ? ShotViabilityRating.Poor
        : ShotViabilityRating.VeryPoor;

    /// <summary>Coverage (assessed weight over applicable weight) at or above which confidence is high.</summary>
    public const double HighConfidenceCoverage = 0.9;
    public const double ModerateConfidenceCoverage = 0.6;

    public static ShotViabilityConfidence ConfidenceFor(double coverage) =>
        coverage >= HighConfidenceCoverage ? ShotViabilityConfidence.High
        : coverage >= ModerateConfidenceCoverage ? ShotViabilityConfidence.Moderate
        : ShotViabilityConfidence.Low;
}
