using NodaTime;
using System.Text.Json.Serialization;
using Noctaxis.Core.Measurements;

namespace Noctaxis.Core.Domain;

public readonly record struct GeoCoordinate(double Latitude, double Longitude, double ElevationMetres = 0)
{
    public const double MinimumRepresentableElevationMetres = -12_000;
    public const double MaximumRepresentableElevationMetres = 9_000;

    public GeoCoordinate Normalised() => new(
        Math.Clamp(Latitude, -90, 90),
        Angles.NormaliseLongitude(Longitude),
        Math.Clamp(ElevationMetres, MinimumRepresentableElevationMetres, MaximumRepresentableElevationMetres));

    public override string ToString() => $"{Latitude:F5}°, {Longitude:F5}°";
}

public enum TerrainElevationResolutionState
{
    Unresolved,
    TerrainResolved,
    ManualOverride
}

public sealed record ObserverElevationState(
    double? TerrainGroundElevationAslMetres = null,
    double? ManualGroundElevationOverrideAslMetres = null)
{
    [JsonIgnore]
    public bool IsManualOverride => ManualGroundElevationOverrideAslMetres.HasValue;

    [JsonIgnore]
    public TerrainElevationResolutionState ResolutionState => ManualGroundElevationOverrideAslMetres.HasValue
        ? TerrainElevationResolutionState.ManualOverride
        : TerrainGroundElevationAslMetres.HasValue
            ? TerrainElevationResolutionState.TerrainResolved
            : TerrainElevationResolutionState.Unresolved;

    [JsonIgnore]
    public double? ResolvedGroundElevationAslMetres =>
        ManualGroundElevationOverrideAslMetres ?? TerrainGroundElevationAslMetres;

    public double ResolveGroundElevationAsl(double fallbackGroundElevationAslMetres) =>
        ManualGroundElevationOverrideAslMetres ?? TerrainGroundElevationAslMetres ??
        fallbackGroundElevationAslMetres;

    public double EffectiveObserverAltitudeAsl(double fallbackGroundElevationAslMetres,
        double cameraHeightAboveGroundMetres) =>
        ResolveGroundElevationAsl(fallbackGroundElevationAslMetres) +
        AppSettings.NormaliseCameraHeight(cameraHeightAboveGroundMetres);

    public ObserverElevationState WithTerrainGroundElevation(double elevationAslMetres) =>
        this with { TerrainGroundElevationAslMetres = NormaliseElevation(elevationAslMetres) };

    public ObserverElevationState WithManualOverride(double elevationAslMetres) =>
        this with { ManualGroundElevationOverrideAslMetres = NormaliseElevation(elevationAslMetres) };

    public ObserverElevationState ResetManualOverride() =>
        this with { ManualGroundElevationOverrideAslMetres = null };

    private static double NormaliseElevation(double value) =>
        Math.Clamp(double.IsFinite(value) ? value : 0,
            GeoCoordinate.MinimumRepresentableElevationMetres, GeoCoordinate.MaximumRepresentableElevationMetres);
}

public enum AstralTargetCategory
{
    Solar, Lunar, Star, DoubleStar, Asterism, Galaxy, Nebula,
    PlanetaryNebula, EmissionNebula, ReflectionNebula, DarkNebula, SupernovaRemnant,
    OpenCluster, GlobularCluster, GalaxyCluster, Other
}

public sealed record AstralTarget(
    string Id,
    string DisplayName,
    AstralTargetCategory Category,
    double? RightAscensionHours,
    double? DeclinationDegrees,
    string CoordinateEpoch,
    string? Notes = null,
    string? PrimaryIdentifier = null,
    string? Constellation = null,
    IReadOnlyList<string>? Aliases = null,
    IReadOnlyList<string>? CatalogueIdentifiers = null,
    double? ApparentMagnitude = null,
    string? AngularSize = null,
    string? Source = null)
{
    public bool IsSun => Id.Equals("sun", StringComparison.OrdinalIgnoreCase);
    public bool IsMoon => Id.Equals("moon", StringComparison.OrdinalIgnoreCase);
    public bool HasEquatorialCoordinates => RightAscensionHours.HasValue && DeclinationDegrees.HasValue;
}

public readonly record struct HorizontalCoordinate(double AzimuthDegrees, double AltitudeDegrees)
{
    public bool IsAboveHorizon => AltitudeDegrees >= 0;
}

public sealed record TargetEvents(Instant? Rise, Instant? Transit, Instant? Set);

public sealed record TwilightEvents(
    Instant? Sunrise,
    Instant? Sunset,
    Instant? CivilDawn,
    Instant? CivilDusk,
    Instant? NauticalDawn,
    Instant? NauticalDusk,
    Instant? AstronomicalDawn,
    Instant? AstronomicalDusk);

public sealed record TargetPosition(
    AstralTarget Target,
    Instant Instant,
    HorizontalCoordinate Horizontal,
    TargetEvents Events,
    double? MoonIlluminatedFraction = null,
    double? MoonPhaseAngleDegrees = null,
    TwilightEvents? Twilight = null);

public sealed record AstronomyContext(TargetPosition Sun, TargetPosition Moon);

public sealed record AstralPathSample(Instant Instant, HorizontalCoordinate Horizontal)
{
    public bool IsAboveHorizon => Horizontal.IsAboveHorizon;
}

public sealed record AstralPath(
    LocalDate LocalDate,
    string TimeZoneId,
    Duration SampleInterval,
    IReadOnlyList<AstralPathSample> Samples,
    TargetEvents Events,
    Instant SelectedInstant);

public enum SensorPreset { FullFrame, ApsCCanon, ApsC, MicroFourThirds, OneInch, Custom }
public enum CameraOrientation { Landscape, Portrait }

public sealed record LensConfiguration(
    SensorPreset Preset = SensorPreset.FullFrame,
    double SensorWidthMillimetres = 36,
    double SensorHeightMillimetres = 24,
    double FocalLengthMillimetres = 24,
    CameraOrientation Orientation = CameraOrientation.Landscape,
    double FramingMultiplier = 1)
{
    public static LensConfiguration ForPreset(SensorPreset preset) => preset switch
    {
        SensorPreset.FullFrame => new(preset, 36, 24, 24),
        SensorPreset.ApsCCanon => new(preset, 22.3, 14.9, 18),
        SensorPreset.ApsC => new(preset, 23.6, 15.7, 18),
        SensorPreset.MicroFourThirds => new(preset, 17.3, 13, 14),
        SensorPreset.OneInch => new(preset, 13.2, 8.8, 10),
        _ => new(preset, 36, 24, 24)
    };
}

public sealed record CameraProfile(
    string Id,
    string DisplayName,
    double SensorWidthMillimetres,
    double SensorHeightMillimetres,
    string? Manufacturer = null,
    string? Model = null)
{
    [JsonIgnore]
    public bool IsValid => ValidationMessage is null;

    [JsonIgnore]
    public string? ValidationMessage =>
        string.IsNullOrWhiteSpace(Id) ? "Camera identifier is required." :
        string.IsNullOrWhiteSpace(DisplayName) ? "Camera name is required." :
        !double.IsFinite(SensorWidthMillimetres) || SensorWidthMillimetres <= 0 ?
            "Sensor width must be greater than zero." :
        !double.IsFinite(SensorHeightMillimetres) || SensorHeightMillimetres <= 0 ?
            "Sensor height must be greater than zero." : null;
}

public sealed record LensProfile(
    string Id,
    string DisplayName,
    double MinimumFocalLengthMillimetres,
    double MaximumFocalLengthMillimetres,
    string? Manufacturer = null,
    string? Model = null)
{
    [JsonIgnore]
    public bool IsPrime => IsValid &&
        Math.Abs(MinimumFocalLengthMillimetres - MaximumFocalLengthMillimetres) < 1e-9;

    [JsonIgnore]
    public bool IsValid => ValidationMessage is null;

    [JsonIgnore]
    public string? ValidationMessage =>
        string.IsNullOrWhiteSpace(Id) ? "Lens identifier is required." :
        string.IsNullOrWhiteSpace(DisplayName) ? "Lens name is required." :
        !double.IsFinite(MinimumFocalLengthMillimetres) || MinimumFocalLengthMillimetres <= 0 ?
            "Minimum focal length must be greater than zero." :
        !double.IsFinite(MaximumFocalLengthMillimetres) || MaximumFocalLengthMillimetres <= 0 ?
            "Maximum focal length must be greater than zero." :
        MinimumFocalLengthMillimetres > MaximumFocalLengthMillimetres ?
            "Minimum focal length cannot exceed maximum focal length." : null;

    public double ClampFocalLength(double focalLengthMillimetres)
    {
        if (!IsValid) throw new InvalidOperationException(ValidationMessage);
        var value = double.IsFinite(focalLengthMillimetres)
            ? focalLengthMillimetres
            : MinimumFocalLengthMillimetres;
        return Math.Clamp(value, MinimumFocalLengthMillimetres, MaximumFocalLengthMillimetres);
    }
}

public sealed record EquipmentSettings(
    IReadOnlyList<CameraProfile>? Cameras = null,
    IReadOnlyList<LensProfile>? Lenses = null)
{
    public const string MigratedCameraId = "migrated-camera";
    public const string MigratedLensId = "migrated-lens";

    public EquipmentSettings EnsureUsable(LensConfiguration legacy)
    {
        var cameras = (Cameras ?? []).Where(profile => profile.IsValid)
            .DistinctBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        var lenses = (Lenses ?? []).Where(profile => profile.IsValid)
            .DistinctBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        if (cameras.Length == 0)
            cameras =
            [
                new CameraProfile(MigratedCameraId, CameraName(legacy),
                    PositiveOrDefault(legacy.SensorWidthMillimetres, 36),
                    PositiveOrDefault(legacy.SensorHeightMillimetres, 24))
            ];
        if (lenses.Length == 0)
        {
            var focalLength = PositiveOrDefault(legacy.FocalLengthMillimetres, 24);
            lenses = [new LensProfile(MigratedLensId, $"{focalLength:0.#} mm lens", focalLength, focalLength)];
        }
        return new EquipmentSettings(cameras, lenses);
    }

    private static string CameraName(LensConfiguration legacy) => legacy.Preset switch
    {
        SensorPreset.FullFrame => "Full Frame camera",
        SensorPreset.ApsCCanon => "Canon APS-C camera",
        SensorPreset.ApsC => "APS-C camera",
        SensorPreset.MicroFourThirds => "Micro Four Thirds camera",
        SensorPreset.OneInch => "1-inch camera",
        _ => "Custom camera"
    };

    private static double PositiveOrDefault(double value, double fallback) =>
        double.IsFinite(value) && value > 0 ? value : fallback;
}

public sealed record FieldOfView(double HorizontalDegrees, double VerticalDegrees, double DiagonalDegrees);

public enum CameraFramingDirectionSource { PrimaryTarget, ManualBearing }

public sealed record CameraFramingSettings(
    bool IsOverlayVisible = true,
    double ManualBearingDegrees = 0,
    double CompositionOffsetDegrees = 0,
    bool ShowVisibilityLimits = true,
    double ShadingOpacityPercent = 10,
    double LineThickness = 1.25,
    double TerrainCastAngularDetailDegrees = 10)
{
    public const double DefaultTerrainCastAngularDetailDegrees = 10;
    public const double MinimumTerrainCastAngularDetailDegrees = 1;
    public const double MaximumTerrainCastAngularDetailDegrees = 45;

    public CameraFramingSettings Normalised() => this with
    {
        ShadingOpacityPercent = Math.Clamp(
            double.IsFinite(ShadingOpacityPercent) ? ShadingOpacityPercent : 10, 0, 50),
        LineThickness = Math.Clamp(double.IsFinite(LineThickness) ? LineThickness : 1.25, 0.5, 5),
        TerrainCastAngularDetailDegrees = Math.Clamp(
            double.IsFinite(TerrainCastAngularDetailDegrees)
                ? TerrainCastAngularDetailDegrees
                : DefaultTerrainCastAngularDetailDegrees,
            MinimumTerrainCastAngularDetailDegrees,
            MaximumTerrainCastAngularDetailDegrees)
    };
}

public sealed record CameraFramingGuide(
    double CentreBearingDegrees,
    double HorizontalFieldOfViewDegrees,
    double LeftEdgeBearingDegrees,
    double RightEdgeBearingDegrees,
    CameraFramingDirectionSource DirectionSource);

public sealed record FramingTerrainObstructionSample(
    double BearingDegrees,
    bool IsObstructed,
    double? FirstObstructionDistanceMetres = null,
    IReadOnlyList<HorizonVisibilitySegment>? VisibilitySegments = null)
{
    public double? GroundFirstObstructionDistanceMetres => FirstObstructionDistanceMetres;
    [JsonIgnore]
    public IReadOnlyList<HorizonVisibilitySegment> EffectiveVisibilitySegments => VisibilitySegments ?? [];
}

public enum HorizonVisibilityState
{
    Visible,
    TerrainOccluded
}

public readonly record struct HorizonVisibilitySegment(
    double StartDistanceMetres,
    double EndDistanceMetres,
    HorizonVisibilityState State);

public sealed record HorizonRayProfile(
    double BearingDegrees,
    double? HorizonAltitudeDegrees,
    IReadOnlyList<HorizonVisibilitySegment> Segments,
    bool HasTerrainData,
    double MaximumDistanceMetres);

public enum TargetLocalVisibilityState
{
    BelowAstronomicalHorizon,
    TerrainBlocked,
    Marginal,
    Clear,
    TerrainUnavailable
}

public sealed record TargetLocalVisibility(
    TargetLocalVisibilityState State,
    double TargetAltitudeDegrees,
    double? LocalHorizonAltitudeDegrees,
    double? ClearanceDegrees);

public sealed record FramingVisibilityAssessment(
    bool IsTargetTerrainObstructed,
    double? TerrainClearanceDegrees,
    double? TerrainHorizonDegrees,
    double? WeatherVisibilityDistanceMetres,
    string Status,
    IReadOnlyList<FramingTerrainObstructionSample>? TerrainObstructions = null)
{
    [JsonIgnore]
    public IReadOnlyList<FramingTerrainObstructionSample> EffectiveTerrainObstructions =>
        TerrainObstructions ?? [];
}

public enum TerrainSampleStatus : byte
{
    Valid,
    NoData,
    Water,
    Fallback,
    Error,
    Unavailable
}

public readonly record struct TerrainSightlineSample
{
    private readonly double? _terrainElevationAngleDegrees;

    public TerrainSightlineSample(double distanceMetres, double? terrainElevationMetres,
        double curvatureDropMetres, double? terrainElevationAngleDegrees,
        TerrainSampleStatus status = TerrainSampleStatus.Valid)
    {
        DistanceMetres = distanceMetres;
        TerrainElevationMetres = terrainElevationMetres;
        RawTerrainElevationMetres = terrainElevationMetres;
        CurvatureDropMetres = curvatureDropMetres;
        _terrainElevationAngleDegrees = terrainElevationAngleDegrees;
        TerrainElevationSlope = terrainElevationAngleDegrees is double angle
            ? Math.Tan(angle * Angles.DegreesToRadians) : null;
        Status = terrainElevationMetres.HasValue ? status : TerrainSampleStatus.Unavailable;
    }

    private TerrainSightlineSample(double distanceMetres, double? terrainElevationMetres,
        double curvatureDropMetres, double? terrainElevationSlope, TerrainSampleStatus status, bool slope,
        double? rawTerrainElevationMetres, Noctaxis.Core.Environment.LandCoverClass? classification,
        bool surfaceWasAdjusted,
        Noctaxis.Core.Environment.TerrainSurfaceResolutionReason? surfaceResolutionReason)
    {
        DistanceMetres = distanceMetres;
        TerrainElevationMetres = terrainElevationMetres;
        CurvatureDropMetres = curvatureDropMetres;
        TerrainElevationSlope = terrainElevationSlope;
        RawTerrainElevationMetres = rawTerrainElevationMetres ?? terrainElevationMetres;
        Classification = classification;
        SurfaceWasAdjusted = surfaceWasAdjusted;
        SurfaceResolutionReason = surfaceResolutionReason;
        _terrainElevationAngleDegrees = null;
        Status = terrainElevationMetres.HasValue ? status : TerrainSampleStatus.Unavailable;
    }

    public double DistanceMetres { get; }
    public double? TerrainElevationMetres { get; }
    public double CurvatureDropMetres { get; }
    public double? TerrainElevationSlope { get; }
    public TerrainSampleStatus Status { get; }
    public double? RawTerrainElevationMetres { get; }
    public Noctaxis.Core.Environment.LandCoverClass? Classification { get; }
    public bool SurfaceWasAdjusted { get; }
    public Noctaxis.Core.Environment.TerrainSurfaceResolutionReason? SurfaceResolutionReason { get; }
    public double? TerrainElevationAngleDegrees => _terrainElevationAngleDegrees ??
        (TerrainElevationSlope is double slope ? Math.Atan(slope) * Angles.RadiansToDegrees : null);
    public double? GroundElevationMetres => TerrainElevationMetres;
    public double? GroundElevationSlope => TerrainElevationSlope;
    public double? GroundElevationAngleDegrees => TerrainElevationAngleDegrees;
    public TerrainSampleStatus GroundStatus => Status;

    public static TerrainSightlineSample FromSlope(double distanceMetres, double? terrainElevationMetres,
        double curvatureDropMetres, double? terrainElevationSlope,
        TerrainSampleStatus status = TerrainSampleStatus.Valid,
        double? rawTerrainElevationMetres = null,
        Noctaxis.Core.Environment.LandCoverClass? classification = null,
        bool surfaceWasAdjusted = false,
        Noctaxis.Core.Environment.TerrainSurfaceResolutionReason? surfaceResolutionReason = null) =>
        new(distanceMetres, terrainElevationMetres, curvatureDropMetres, terrainElevationSlope, status, true,
            rawTerrainElevationMetres, classification, surfaceWasAdjusted, surfaceResolutionReason);
}

public enum ObserverDatumConfidence
{
    Unavailable,
    Normal
}

public readonly record struct HorizonObstruction(
    bool HasTerrainData,
    double? TerrainFirstObstructionDistanceMetres)
{
    public bool HasGroundData => HasTerrainData;
    public bool HasEffectiveData => HasTerrainData;
    public double? GroundFirstObstructionDistanceMetres => TerrainFirstObstructionDistanceMetres;
    public double? EffectiveFirstObstructionDistanceMetres => TerrainFirstObstructionDistanceMetres;
}

public readonly record struct TerrainHorizonSample(
    double BearingDegrees,
    double? TerrainHorizonElevationDegrees,
    double? TerrainHorizonFeatureDistanceMetres,
    Noctaxis.Core.Environment.LandCoverClass? LandCover = null,
    IReadOnlyList<TerrainSightlineSample>? Sightline = null)
{
    public TerrainHorizonSample(double azimuthDegrees, double altitudeDegrees,
        double? distanceMetres = null, Noctaxis.Core.Environment.LandCoverClass? landCover = null)
        : this(azimuthDegrees, altitudeDegrees, distanceMetres, landCover, null)
    {
    }

    public double AzimuthDegrees => BearingDegrees;
    public double AltitudeDegrees => TerrainHorizonElevationDegrees ?? 0;
    public double? DistanceMetres => TerrainHorizonFeatureDistanceMetres;
    public double? GroundHorizonElevationDegrees => TerrainHorizonElevationDegrees;
    public double? GroundHorizonFeatureDistanceMetres => TerrainHorizonFeatureDistanceMetres;
    public double? EffectiveHorizonElevationDegrees => TerrainHorizonElevationDegrees;
    public double? EffectiveHorizonFeatureDistanceMetres => TerrainHorizonFeatureDistanceMetres;
}

public sealed record TerrainHorizonProfile(
    GeoCoordinate Observer,
    IReadOnlyList<TerrainHorizonSample> Samples,
    bool HasDemCoverage,
    string Status,
    Instant GeneratedAt,
    Noctaxis.Core.Environment.EnvironmentalValue<double>? TerrainElevationAtObserver = null,
    double ObserverHeightAboveGroundMetres = 0,
    double MaximumAnalysisDistanceMetres = 0,
    Noctaxis.Core.Environment.EnvironmentalDataState HorizonState =
        Noctaxis.Core.Environment.EnvironmentalDataState.Unavailable,
    double? ChosenObserverGroundElevationMetres = null,
    double? ObserverAbsoluteElevationMetres = null,
    ObserverDatumConfidence ObserverDatumConfidence = ObserverDatumConfidence.Unavailable,
    string? ObserverDatumMessage = null,
    bool IsComplete = true,
    int CompletedBearingCount = -1,
    Noctaxis.Core.Terrain.TerrainPipelineTimings? PipelineTimings = null,
    Noctaxis.Core.Terrain.TerrainObserverDiagnostics? ObserverDiagnostics = null)
{
    public bool HasTerrainCoverage => HasDemCoverage;
    public Noctaxis.Core.Environment.EnvironmentalValue<double>? GroundElevationAtObserver =>
        TerrainElevationAtObserver;
    public Noctaxis.Core.Environment.EnvironmentalDataState GroundHorizonState => HorizonState;
    public int EffectiveCompletedBearingCount => CompletedBearingCount < 0 ? Samples.Count : CompletedBearingCount;

    public double AltitudeAt(double azimuthDegrees) => TerrainAltitudeAt(azimuthDegrees) ?? 0;

    public double? TerrainAltitudeAt(double azimuthDegrees) =>
        !HasTerrainCoverage || Samples.Count == 0
            ? null
            : InterpolateNullable(azimuthDegrees, sample => sample.TerrainHorizonElevationDegrees);

    public double? GroundAltitudeAt(double azimuthDegrees) => TerrainAltitudeAt(azimuthDegrees);
    public double? EffectiveAltitudeAt(double azimuthDegrees) => TerrainAltitudeAt(azimuthDegrees);

    public double VisibleAltitudeAt(double azimuthDegrees) => TerrainAltitudeAt(azimuthDegrees) ?? 0;

    public HorizonObstruction TerrainObstructionAt(double bearingDegrees) =>
        FindObstructionAtAngle(bearingDegrees, 0);

    public HorizonObstruction OccultationAt(double bearingDegrees, double sightlineElevationDegrees) =>
        FindObstructionAtAngle(bearingDegrees, sightlineElevationDegrees);

    [Obsolete("Use TerrainObstructionAt for plan-view obstruction or OccultationAt for a vertical sightline.")]
    public HorizonObstruction ObstructionAt(double bearingDegrees, double sightlineElevationDegrees) =>
        OccultationAt(bearingDegrees, sightlineElevationDegrees);

    public IReadOnlyList<TerrainSightlineSample> SightlineAt(double bearingDegrees)
    {
        if (Samples.Count == 0) return [];
        var (lower, upper, fraction) = BearingNeighbours(bearingDegrees);
        var lowerSightline = Samples[lower].Sightline;
        var upperSightline = Samples[upper].Sightline;
        if (lowerSightline is null || upperSightline is null ||
            lowerSightline.Count == 0 || lowerSightline.Count != upperSightline.Count) return [];
        if (fraction <= 1e-12) return lowerSightline;
        var result = new TerrainSightlineSample[lowerSightline.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var left = lowerSightline[index];
            var right = upperSightline[index];
            result[index] = TerrainSightlineSample.FromSlope(
                left.DistanceMetres + (right.DistanceMetres - left.DistanceMetres) * fraction,
                InterpolateNullable(left.TerrainElevationMetres, right.TerrainElevationMetres, fraction),
                left.CurvatureDropMetres + (right.CurvatureDropMetres - left.CurvatureDropMetres) * fraction,
                InterpolateNullable(left.TerrainElevationSlope, right.TerrainElevationSlope, fraction),
                left.Status == right.Status ? left.Status : TerrainSampleStatus.Valid,
                InterpolateNullable(left.RawTerrainElevationMetres, right.RawTerrainElevationMetres, fraction),
                left.Classification == right.Classification ? left.Classification : null,
                left.SurfaceWasAdjusted || right.SurfaceWasAdjusted,
                left.SurfaceResolutionReason == right.SurfaceResolutionReason
                    ? left.SurfaceResolutionReason : null);
        }
        return result;
    }

    private HorizonObstruction FindObstructionAtAngle(double bearingDegrees, double sightlineElevationDegrees)
    {
        if (Samples.Count == 0 || !double.IsFinite(sightlineElevationDegrees)) return default;
        var sightline = SightlineAt(bearingDegrees);
        if (sightline.Count == 0) return default;

        var available = false;
        double? obstructionDistance = null;
        double? previousDistance = null;
        double? previousSlope = null;
        var targetSlope = Math.Tan(sightlineElevationDegrees * Angles.DegreesToRadians);
        foreach (var point in sightline)
        {
            var slope = point.TerrainElevationSlope;
            available |= slope.HasValue;
            if (!obstructionDistance.HasValue && slope >= targetSlope)
                obstructionDistance = previousDistance.HasValue
                    ? InterpolateCrossing(previousDistance.Value, previousSlope,
                        point.DistanceMetres, slope, targetSlope)
                    : point.DistanceMetres;
            previousDistance = point.DistanceMetres;
            previousSlope = slope;
        }
        return new HorizonObstruction(available, obstructionDistance);
    }

    private double? InterpolateNullable(double azimuthDegrees,
        Func<TerrainHorizonSample, double?> selector)
    {
        if (Samples.Count == 0) return null;
        var (lower, upper, fraction) = BearingNeighbours(azimuthDegrees);
        return InterpolateNullable(selector(Samples[lower]), selector(Samples[upper]), fraction);
    }

    private (int Lower, int Upper, double Fraction) BearingNeighbours(double bearingDegrees)
    {
        var position = Angles.NormaliseDegrees(bearingDegrees) / (360d / Samples.Count);
        var lower = (int)Math.Floor(position) % Samples.Count;
        return (lower, (lower + 1) % Samples.Count, position - Math.Floor(position));
    }

    private static double? InterpolateNullable(double? lower, double? upper, double fraction) =>
        lower.HasValue && upper.HasValue
            ? lower.Value + (upper.Value - lower.Value) * fraction
            : lower ?? upper;

    private static double InterpolateCrossing(double previousDistance, double? previousSlope,
        double distance, double? slope, double targetSlope)
    {
        if (!previousSlope.HasValue || !slope.HasValue || slope.Value <= previousSlope.Value)
            return distance;
        var previousRadians = Math.Atan(previousSlope.Value);
        var currentRadians = Math.Atan(slope.Value);
        var targetRadians = Math.Atan(targetSlope);
        var fraction = Math.Clamp((targetRadians - previousRadians) /
                                  (currentRadians - previousRadians), 0, 1);
        return previousDistance + (distance - previousDistance) * fraction;
    }
}

public sealed record TerrainCrossings(Instant? ClearsTerrain, Instant? DropsBehindTerrain);

public sealed record WeatherConditions(
    Instant ForecastInstant,
    double? CloudCoverPercent,
    double? LowCloudPercent,
    double? MediumCloudPercent,
    double? HighCloudPercent,
    double? PrecipitationProbabilityPercent,
    double? PrecipitationMillimetres,
    string? PrecipitationType,
    double? WindSpeedMetresPerSecond,
    double? WindDirectionDegrees,
    double? WindGustMetresPerSecond,
    double? TemperatureCelsius,
    double? HumidityPercent,
    double? DewPointCelsius,
    double? VisibilityKilometres,
    string Summary,
    Instant RetrievedAt,
    bool IsStale = false);

public enum DataState { Ready, Loading, MissingConfiguration, MissingCoverage, Stale, Error }

public sealed record WeatherResult(DataState State, WeatherConditions? Conditions, string Message);

public enum WeatherField
{
    TotalCloudCover, LowCloudCover, MediumCloudCover, HighCloudCover,
    PrecipitationProbability, PrecipitationAmount, PrecipitationType,
    Visibility, Temperature, DewPoint, RelativeHumidity,
    WindSpeed, WindGusts, WindDirection,
    Sunrise, Sunset, CivilTwilight, NauticalTwilight, AstronomicalTwilight,
    AstronomicalDarkness, MoonPhase, MoonIllumination, Moonrise, Moonset
}

public sealed record WeatherSettings(
    IReadOnlyList<WeatherField>? EnabledFields = null,
    double CacheDistanceKilometres = 5)
{
    public static IReadOnlyList<WeatherField> DefaultFields { get; } = Enum.GetValues<WeatherField>();

    [JsonIgnore]
    public IReadOnlyList<WeatherField> EffectiveFields => EnabledFields ?? DefaultFields;

    public bool IsEnabled(WeatherField field) => EffectiveFields.Contains(field);
}

public sealed record SavedLocation(
    Guid Id,
    string Name,
    GeoCoordinate Coordinate,
    string TimeZoneId,
    string? Notes = null,
    SensorPreset? PreferredSensor = null,
    string? RegionDescription = null,
    bool IsFavourite = false,
    DateTimeOffset? LastUsedUtc = null,
    int SortOrder = 0,
    DateTimeOffset? DateAddedUtc = null,
    ObserverElevationState? ObserverElevation = null);

public enum LocationResolutionSource { LastCustomPosition, OperatingSystemLocation, SearchResult, SystemRegion, ApplicationFallback }

public sealed record LocationResolution(
    GeoCoordinate Coordinate,
    LocationResolutionSource Source,
    double? AccuracyMetres = null,
    bool IsApproximate = false,
    string? DisplayName = null,
    string? RegionDescription = null,
    string? TimeZoneId = null);

public sealed record LocationSearchResult(
    string Id,
    string DisplayName,
    GeoCoordinate Coordinate,
    string? RegionDescription,
    string? Country,
    string? TimeZoneId,
    string Attribution);

public sealed record CelestialObjectSelection(string TargetId, bool IsVisible = true, int Order = 0);

public sealed record CelestialObjectSettings(
    IReadOnlyList<CelestialObjectSelection>? ConfiguredObjects = null,
    string DefaultPrimaryTargetId = "sun")
{
    public static IReadOnlyList<CelestialObjectSelection> Defaults { get; } =
        [new("sun", true, 0), new("moon", true, 1)];

    [JsonIgnore]
    public IReadOnlyList<CelestialObjectSelection> EffectiveConfiguredObjects =>
        ConfiguredObjects is { Count: > 0 } ? ConfiguredObjects : Defaults;
}

public static class CelestialVisibilityPolicy
{
    public const int MaximumVisibleObjects = 8;

    public static IReadOnlyList<CelestialObjectSelection> Normalise(IEnumerable<CelestialObjectSelection> selections)
    {
        var visible = 0;
        return selections
            .DistinctBy(item => item.TargetId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Order)
            .Select((item, index) => item with
            {
                Order = index,
                IsVisible = item.IsVisible && ++visible <= MaximumVisibleObjects
            })
            .ToArray();
    }
}

public sealed record CelestialObjectPlan(TargetPosition Position, AstralPath Path);

public sealed record AppSettings(
    string Units = "Metric",
    string SelectedTimeZoneId = "system",
    WeatherSettings? Weather = null,
    int TimeSnapMinutes = 5,
    CelestialObjectSettings? CelestialObjects = null,
    CameraFramingSettings? CameraFraming = null,
    double CameraHeightAboveGroundMetres = 1.7,
    EquipmentSettings? Equipment = null,
    bool TerrainDebugOverlay = false,
    long TerrainCacheLimitBytes = 2L * 1024 * 1024 * 1024)
{
    public const string UseSystemTimeZoneId = "system";
    [JsonIgnore]
    public long EffectiveTerrainCacheLimitBytes => Math.Clamp(TerrainCacheLimitBytes, 0, 1024L * 1024 * 1024 * 1024);
    public const double DefaultCameraHeightAboveGroundMetres = 1.7;

    [JsonIgnore]
    public WeatherSettings EffectiveWeather => Weather ?? new();

    [JsonIgnore]
    public CelestialObjectSettings EffectiveCelestialObjects => CelestialObjects ?? new();

    [JsonIgnore]
    public CameraFramingSettings EffectiveCameraFraming => (CameraFraming ?? new()).Normalised();

    [JsonIgnore]
    public double EffectiveCameraHeightAboveGroundMetres =>
        NormaliseCameraHeight(CameraHeightAboveGroundMetres);

    public EquipmentSettings EffectiveEquipment(LensConfiguration legacy) =>
        (Equipment ?? new EquipmentSettings()).EnsureUsable(legacy);

    public static double NormaliseCameraHeight(double value) =>
        Math.Clamp(double.IsFinite(value) ? value : DefaultCameraHeightAboveGroundMetres, 0, 100);

    [JsonIgnore]
    public MeasurementSystem EffectiveMeasurementSystem => MeasurementUnits.Parse(Units);

    [JsonExtensionData]
    public IDictionary<string, System.Text.Json.JsonElement>? LegacyData { get; init; }
}

public static class MapProvider
{
    public const string Attribution = "© OpenStreetMap contributors";
}

public sealed record PlanningSession(
    GeoCoordinate Observer,
    Instant Instant,
    string TimeZoneId,
    string TargetId,
    LensConfiguration Lens,
    Guid? SavedLocationId = null,
    IReadOnlyList<CelestialObjectSelection>? VisibleObjects = null,
    string? CameraProfileId = null,
    string? LensProfileId = null,
    ObserverElevationState? ObserverElevation = null)
{
    public static PlanningSession Default(Instant now, string timeZoneId) => new(
        new GeoCoordinate(51.5074, -0.1278, 15), now, timeZoneId, "sun",
        LensConfiguration.ForPreset(SensorPreset.FullFrame), null,
        [new CelestialObjectSelection("sun", true, 0), new CelestialObjectSelection("moon", true, 1)]);

    [JsonIgnore]
    public IReadOnlyList<CelestialObjectSelection> EffectiveVisibleObjects => VisibleObjects is { Count: > 0 }
        ? VisibleObjects
        : [new CelestialObjectSelection("sun", true, 0), new CelestialObjectSelection("moon", true, 1), new CelestialObjectSelection(TargetId, true, 2)];

    [JsonIgnore]
    public ObserverElevationState EffectiveObserverElevation => ObserverElevation ?? new();
}

public sealed record PlanningSnapshot(
    PlanningSession Session,
    TargetPosition Position,
    AstralPath Path,
    FieldOfView FieldOfView,
    TerrainHorizonProfile Terrain,
    TerrainCrossings TerrainCrossings,
    WeatherResult Weather,
    AstronomyContext Astronomy,
    IReadOnlyList<CelestialObjectPlan>? ObjectPlans = null,
    Noctaxis.Core.Environment.PlannerEnvironmentSnapshot? Environment = null)
{
    [JsonIgnore]
    public IReadOnlyList<CelestialObjectPlan> EffectiveObjectPlans => ObjectPlans ?? [new CelestialObjectPlan(Position, Path)];
}
