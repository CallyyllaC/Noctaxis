using NodaTime;
using System.Text.Json.Serialization;
using Noctaxis.Core.Measurements;

namespace Noctaxis.Core.Domain;

public readonly record struct GeoCoordinate(double Latitude, double Longitude, double ElevationMetres = 0)
{
    public GeoCoordinate Normalised() => new(
        Math.Clamp(Latitude, -90, 90),
        Angles.NormaliseLongitude(Longitude),
        Math.Clamp(ElevationMetres, -500, 9_000));

    public override string ToString() => $"{Latitude:F5}°, {Longitude:F5}°";
}

public enum AstralTargetCategory
{
    Solar, Lunar, Star, DoubleStar, Asterism, Constellation, Galaxy, Nebula,
    PlanetaryNebula, EmissionNebula, ReflectionNebula, DarkNebula, SupernovaRemnant,
    OpenCluster, GlobularCluster, GalaxyCluster, Galactic, CelestialPole, ConstellationAnchor, Other
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

public sealed record FieldOfView(double HorizontalDegrees, double VerticalDegrees, double DiagonalDegrees);

public enum CameraFramingDirectionSource { PrimaryTarget, ManualBearing }

public sealed record CameraFramingSettings(
    bool IsOverlayVisible = true,
    double ManualBearingDegrees = 0,
    double CompositionOffsetDegrees = 0,
    bool ShowVisibilityLimits = true,
    double ShadingOpacityPercent = 10,
    double LineThickness = 1.25)
{
    public CameraFramingSettings Normalised() => this with
    {
        ShadingOpacityPercent = Math.Clamp(
            double.IsFinite(ShadingOpacityPercent) ? ShadingOpacityPercent : 10, 0, 50),
        LineThickness = Math.Clamp(double.IsFinite(LineThickness) ? LineThickness : 1.25, 0.5, 5)
    };
}

public sealed record CameraFramingGuide(
    double CentreBearingDegrees,
    double HorizontalFieldOfViewDegrees,
    double LeftEdgeBearingDegrees,
    double RightEdgeBearingDegrees,
    CameraFramingDirectionSource DirectionSource);

public enum FramingLimitReason { WeatherVisibility, TerrainHorizon }

public sealed record FramingRadialLimit(
    FramingLimitReason Reason,
    double DistanceMetres,
    string Label);

public sealed record FramingVisibilityAssessment(
    bool IsTargetTerrainObstructed,
    double? TerrainClearanceDegrees,
    double? TerrainHorizonDegrees,
    IReadOnlyList<FramingRadialLimit> RadialLimits,
    string Status)
{
    [JsonIgnore]
    public FramingRadialLimit? NearestRadialLimit => RadialLimits
        .Where(limit => double.IsFinite(limit.DistanceMetres) && limit.DistanceMetres > 0)
        .OrderBy(limit => limit.DistanceMetres)
        .FirstOrDefault();
}

public readonly record struct TerrainHorizonSample(double AzimuthDegrees, double AltitudeDegrees, double? DistanceMetres = null);

public sealed record TerrainHorizonProfile(
    GeoCoordinate Observer,
    IReadOnlyList<TerrainHorizonSample> Samples,
    bool HasDemCoverage,
    string Status,
    Instant GeneratedAt)
{
    public double AltitudeAt(double azimuthDegrees)
    {
        if (Samples.Count == 0) return 0;
        var az = Angles.NormaliseDegrees(azimuthDegrees);
        var step = 360d / Samples.Count;
        var position = az / step;
        var lower = (int)Math.Floor(position) % Samples.Count;
        var upper = (lower + 1) % Samples.Count;
        var fraction = position - Math.Floor(position);
        return Samples[lower].AltitudeDegrees + (Samples[upper].AltitudeDegrees - Samples[lower].AltitudeDegrees) * fraction;
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
    string? PreferredDemFolder = null,
    SensorPreset? PreferredSensor = null,
    string? RegionDescription = null,
    bool IsFavourite = false,
    DateTimeOffset? LastUsedUtc = null,
    int SortOrder = 0,
    DateTimeOffset? DateAddedUtc = null);

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
    string? DemDirectory = null,
    string Units = "Metric",
    string SelectedTimeZoneId = "system",
    WeatherSettings? Weather = null,
    int TimeSnapMinutes = 5,
    CelestialObjectSettings? CelestialObjects = null,
    CameraFramingSettings? CameraFraming = null)
{
    public const string UseSystemTimeZoneId = "system";

    [JsonIgnore]
    public WeatherSettings EffectiveWeather => Weather ?? new();

    [JsonIgnore]
    public CelestialObjectSettings EffectiveCelestialObjects => CelestialObjects ?? new();

    [JsonIgnore]
    public CameraFramingSettings EffectiveCameraFraming => (CameraFraming ?? new()).Normalised();

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
    IReadOnlyList<CelestialObjectSelection>? VisibleObjects = null)
{
    public static PlanningSession Default(Instant now, string timeZoneId) => new(
        new GeoCoordinate(51.5074, -0.1278, 15), now, timeZoneId, "sun",
        LensConfiguration.ForPreset(SensorPreset.FullFrame), null,
        [new CelestialObjectSelection("sun", true, 0), new CelestialObjectSelection("moon", true, 1)]);

    [JsonIgnore]
    public IReadOnlyList<CelestialObjectSelection> EffectiveVisibleObjects => VisibleObjects is { Count: > 0 }
        ? VisibleObjects
        : [new CelestialObjectSelection("sun", true, 0), new CelestialObjectSelection("moon", true, 1), new CelestialObjectSelection(TargetId, true, 2)];
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
    IReadOnlyList<CelestialObjectPlan>? ObjectPlans = null)
{
    [JsonIgnore]
    public IReadOnlyList<CelestialObjectPlan> EffectiveObjectPlans => ObjectPlans ?? [new CelestialObjectPlan(Position, Path)];
}
