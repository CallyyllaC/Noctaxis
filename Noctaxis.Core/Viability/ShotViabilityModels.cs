using Noctaxis.Core.Domain;
using NodaTime;

namespace Noctaxis.Core.Viability;

/// <summary>The metrics of a shot-viability assessment, in the order they are presented.</summary>
public enum ShotViabilityMetric
{
    CloudCover, Darkness, TargetAltitude, TerrainClearance, UsefulWindow, Moon, LightPollution, FramePosition, FramingFit, ImageSampling
}

/// <summary>
/// Which assumptions apply to a target. Deep sky is the primary profile; point sources (stars) skip size-based
/// metrics; the Sun and Moon skip darkness, moonlight and the useful window, and use their own altitude curve.
/// </summary>
public enum ShotViabilityProfile { DeepSky, PointSource, Sun, Moon }

/// <summary>Whether a metric produced a score.</summary>
public enum ShotViabilityMetricStatus
{
    /// <summary>Measured and scored; it counts towards the overall score.</summary>
    Evaluated,
    /// <summary>It applies, but its data is missing or unreliable: it counts only against coverage.</summary>
    Unavailable,
    /// <summary>It does not apply to this kind of target: it counts nowhere.</summary>
    NotApplicable
}

/// <summary>
/// How a metric should be read, independently of the overall score: a high score may still carry one critical
/// geometry warning. <see cref="None"/> is for metrics that were not scored.
/// </summary>
public enum ShotViabilitySeverity { None, Good, Caution, Critical }

public enum ShotViabilityRating { Unavailable, VeryPoor, Poor, Fair, Good, Excellent }

public enum ShotViabilityConfidence { Low, Moderate, High }

/// <summary>One measured value behind a metric, in invariant units, with its display text.</summary>
public sealed record ShotViabilityMeasurement(string Quantity, double Value, string Unit, string Display);

/// <summary>One metric's result.</summary>
public sealed record ShotViabilityMetricResult(
    ShotViabilityMetric Metric,
    ShotViabilityMetricStatus Status,
    IReadOnlyList<ShotViabilityMeasurement> Measurements,
    double? Score,
    double Weight,
    ShotViabilitySeverity Severity,
    string Explanation)
{
    /// <summary>A stable, machine-readable identifier, for example <c>cloud-cover</c>.</summary>
    public string Id => IdOf(Metric);

    public bool IsEvaluated => Status == ShotViabilityMetricStatus.Evaluated;

    /// <summary>
    /// The points this metric adds to the overall score: its score times its share of the evaluated weight.
    /// The contributions of all evaluated metrics add up to the overall score. Null when not evaluated.
    /// </summary>
    public double? WeightedContribution { get; init; }

    /// <summary>The most this metric could have contributed with the same evaluated weight.</summary>
    public double? MaximumContribution { get; init; }

    public static string IdOf(ShotViabilityMetric metric) => metric switch
    {
        ShotViabilityMetric.CloudCover => "cloud-cover",
        ShotViabilityMetric.Darkness => "darkness",
        ShotViabilityMetric.TargetAltitude => "target-altitude",
        ShotViabilityMetric.TerrainClearance => "terrain-clearance",
        ShotViabilityMetric.UsefulWindow => "useful-window",
        ShotViabilityMetric.Moon => "moon",
        ShotViabilityMetric.LightPollution => "light-pollution",
        ShotViabilityMetric.FramePosition => "frame-position",
        ShotViabilityMetric.FramingFit => "framing-fit",
        ShotViabilityMetric.ImageSampling => "image-sampling",
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };
}

/// <summary>A whole assessment: the overall score, how much of it could be assessed, and every metric.</summary>
public sealed record ShotViabilityAssessment(
    string PolicyId,
    AstralTarget Target,
    Instant Instant,
    ShotViabilityProfile Profile,
    double? Score,
    ShotViabilityRating Rating,
    double Coverage,
    ShotViabilityConfidence Confidence,
    IReadOnlyList<ShotViabilityMetricResult> Metrics)
{
    /// <summary>The overall score as shown: a whole number, or null when nothing could be scored.</summary>
    public int? RoundedScore => Score is double score ? (int)Math.Round(score, MidpointRounding.AwayFromZero) : null;

    /// <summary>The most severe of the metrics' severities.</summary>
    public ShotViabilitySeverity HighestSeverity => Metrics.Select(metric => metric.Severity).DefaultIfEmpty().Max();

    /// <summary>Metrics that are cautions or critical, most severe first, in presentation order within each.</summary>
    public IReadOnlyList<ShotViabilityMetricResult> Warnings => Metrics
        .Where(metric => metric.Severity >= ShotViabilitySeverity.Caution)
        .OrderByDescending(metric => metric.Severity).ToArray();

    public ShotViabilityMetricResult this[ShotViabilityMetric metric] => Metrics.Single(result => result.Metric == metric);
}

/// <summary>Why the light-pollution value at the place is, or is not, known.</summary>
public enum LightPollutionDataStatus { Available, NotInstalled, Loading, OutsideCoverage, Unreadable }

/// <summary>
/// The light-pollution atlas's value at the place: the artificial zenith sky brightness as a multiple of the
/// natural sky (the David Lorenz atlas's LPI). Null unless <see cref="Status"/> is Available.
/// </summary>
public sealed record ShotViabilityLightPollution(LightPollutionDataStatus Status, double? ArtificialToNaturalRatio = null)
{
    public static ShotViabilityLightPollution Of(double ratio) => new(LightPollutionDataStatus.Available, ratio);
}

/// <summary>The camera's pointing and field of view, as the Camera frame shows them.</summary>
public sealed record ShotViabilityCamera(double BearingDegrees, double PitchDegrees, double HorizontalFovDegrees, double VerticalFovDegrees);

/// <summary>
/// What the sensor records: the focal length and the camera profile's pixel pitch, when it has a resolution.
/// The framing multiplier scales the sensor as the field of view does.
/// </summary>
public sealed record ShotViabilitySensor(double FocalLengthMillimetres, double? PixelPitchMicrometres, double FramingMultiplier = 1);

/// <summary>
/// The focused target's and the Sun's paths over a span around the planned moment, sampled at the same
/// instants, from which the useful time window is followed. Calculated once per place, target and span, and
/// reused while the planned moment stays inside it.
/// </summary>
public sealed record ShotWindowPaths(
    string TargetId,
    GeoCoordinate Observer,
    Instant Start,
    Instant End,
    IReadOnlyList<AstralPathSample> Target,
    IReadOnlyList<AstralPathSample> Sun)
{
    /// <summary>
    /// Whether these paths belong to this target and place and cover the window from <paramref name="instant"/>.
    /// The place is its latitude and longitude: a resolved ground elevation moves a path by far less than its
    /// sampling, so it does not make the paths another place's.
    /// </summary>
    public bool Covers(string targetId, GeoCoordinate observer, Instant instant) =>
        string.Equals(TargetId, targetId, StringComparison.OrdinalIgnoreCase) &&
        Math.Abs(Observer.Latitude - observer.Latitude) < 1e-9 && Math.Abs(Observer.Longitude - observer.Longitude) < 1e-9 &&
        instant >= Start && instant + Duration.FromTimeSpan(ShotViabilityPolicy.UsefulWindowHorizon) <= End;
}

/// <summary>
/// Everything one assessment reads. It is plain data: the Planner fills it from its current snapshot, and a
/// future Scout can fill it for candidate places and times without any user interface. A missing value makes
/// its metric unavailable; nothing is assumed in its place.
/// </summary>
public sealed record ShotViabilityInput(
    AstralTarget Target,
    Instant Instant,
    HorizontalCoordinate TargetPosition,
    double? SunAltitudeDegrees = null,
    HorizontalCoordinate? MoonPosition = null,
    double? MoonIlluminatedFraction = null,
    WeatherResult? Weather = null,
    TerrainHorizonProfile? Terrain = null,
    IReadOnlyList<FramingTerrainObstructionSample>? FrameTerrain = null,
    ShotViabilityCamera? Camera = null,
    ShotViabilitySensor? Sensor = null,
    ShotWindowPaths? Window = null,
    bool WindowPending = false,
    ShotViabilityLightPollution? LightPollution = null);
