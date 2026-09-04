using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Export;
using Noctaxis.Core.Persistence;
using Noctaxis.Core.Time;
using Noctaxis.Core.Weather;
using NodaTime;

namespace Noctaxis.Core.Tests;

public sealed class PersistenceAndWeatherTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NoctaxisTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SavedLocationSettingsAndSession_RoundTripThroughJson()
    {
        var now = Instant.FromUtc(2024, 6, 21, 22, 15);
        var observerElevation = new ObserverElevationState(18, 25);
        var location = new SavedLocation(Guid.NewGuid(), "Durdle Door", new GeoCoordinate(50.621, -2.276, 25), "Europe/London", "Cliff viewpoint",
            DateAddedUtc: new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero),
            ObserverElevation: observerElevation);
        var session = new PlanningSession(location.Coordinate, now, location.TimeZoneId, "galactic-centre", new LensConfiguration(FocalLengthMillimetres: 20), location.Id,
            [new("sun", true, 0), new("moon", false, 1), new("galactic-centre", true, 2)],
            "camera", "lens", observerElevation);
        var equipment = new EquipmentSettings(
            [new CameraProfile("camera", "Full Frame", 36, 24)],
            [new LensProfile("lens", "14-24 mm", 14, 24)]);
        var settings = new AppSettings("Metric", "Europe/London",
            new WeatherSettings([WeatherField.TotalCloudCover], 7.5),
            CameraFraming: new CameraFramingSettings(false, 215, 4, false, 22, 2.5),
            CameraHeightAboveGroundMetres: 1.6, Equipment: equipment);
        var store = CreateStore(now);
        var custom = new GeoCoordinate(57.1, -4.2);
        await store.SaveAsync(new PersistedState(2, settings, [location], session, location.Id, custom), CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(location, Assert.Single(loaded.Locations));
        Assert.Equal(session.Observer, loaded.Session.Observer);
        Assert.Equal(session.Instant, loaded.Session.Instant);
        Assert.Equal(session.TargetId, loaded.Session.TargetId);
        Assert.Equal(observerElevation, loaded.Session.ObserverElevation);
        Assert.Equal("camera", loaded.Session.CameraProfileId);
        Assert.Equal("lens", loaded.Session.LensProfileId);
        Assert.Equal(session.VisibleObjects!.ToArray(), loaded.Session.VisibleObjects!.ToArray());
        Assert.Equal(7.5, loaded.Settings.EffectiveWeather.CacheDistanceKilometres);
        Assert.Equal([WeatherField.TotalCloudCover], loaded.Settings.EffectiveWeather.EffectiveFields);
        Assert.False(loaded.Settings.EffectiveCameraFraming.IsOverlayVisible);
        Assert.Equal(215, loaded.Settings.EffectiveCameraFraming.ManualBearingDegrees);
        Assert.Equal(4, loaded.Settings.EffectiveCameraFraming.CompositionOffsetDegrees);
        Assert.False(loaded.Settings.EffectiveCameraFraming.ShowVisibilityLimits);
        Assert.Equal(22, loaded.Settings.EffectiveCameraFraming.ShadingOpacityPercent);
        Assert.Equal(2.5, loaded.Settings.EffectiveCameraFraming.LineThickness);
        Assert.Equal(1.6, loaded.Settings.EffectiveCameraHeightAboveGroundMetres);
        Assert.Equal(equipment.Cameras!.ToArray(), loaded.Settings.Equipment!.Cameras!.ToArray());
        Assert.Equal(equipment.Lenses!.ToArray(), loaded.Settings.Equipment!.Lenses!.ToArray());
        Assert.Equal(custom, loaded.LastCustomCoordinate);
    }

    [Fact]
    public async Task LegacyCelestialState_IsMigratedToSettingsAndLimitedWithoutDiscardingObjects()
    {
        var now = Instant.FromUtc(2024, 1, 1, 0, 0);
        var configured = Enumerable.Range(0, 10)
            .Select(index => new CelestialObjectSelection(index == 0 ? "sun" : index == 1 ? "moon" : $"legacy-{index}", true, index)).ToArray();
        var session = PlanningSession.Default(now, "UTC") with { VisibleObjects = configured, TargetId = "sun" };
        var store = CreateStore(now);
        await store.SaveAsync(new PersistedState(2, new AppSettings(), [], session, null), CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(10, loaded.Settings.EffectiveCelestialObjects.EffectiveConfiguredObjects.Count);
        Assert.Equal(8, loaded.Session.EffectiveVisibleObjects.Count(item => item.IsVisible));
        Assert.Equal(10, loaded.Session.EffectiveVisibleObjects.Count);
    }

    [Fact]
    public async Task LegacyObserverElevations_ArePreservedAsExplicitManualOverrides()
    {
        var now = Instant.FromUtc(2024, 1, 1, 0, 0);
        var location = new SavedLocation(Guid.NewGuid(), "Legacy ridge",
            new GeoCoordinate(51, -2, 345), "UTC");
        var session = PlanningSession.Default(now, "UTC") with
        {
            Observer = new GeoCoordinate(51, -2, 321),
            ObserverElevation = null
        };
        var store = CreateStore(now);
        await store.SaveAsync(new PersistedState(3, new AppSettings(), [location], session, location.Id),
            CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(321, loaded.Session.EffectiveObserverElevation.ManualGroundElevationOverrideAslMetres);
        Assert.Equal(345, Assert.Single(loaded.Locations).ObserverElevation!
            .ManualGroundElevationOverrideAslMetres);
    }

    [Fact]
    public async Task CorruptPersistence_IsQuarantinedAndDefaultsRecover()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "state.json"), "{ this is not json");
        var now = Instant.FromUtc(2024, 1, 1, 0, 0);
        var loaded = await CreateStore(now).LoadAsync(CancellationToken.None);
        Assert.Equal(now, loaded.Session.Instant);
        Assert.Empty(loaded.Locations);
        Assert.NotEmpty(Directory.GetFiles(_directory, "state.json.corrupt-*"));
    }

    [Fact]
    public void LegacyRemovedMapAndMeteosourceSettings_AreIgnored()
    {
        const string json = """{"MeteosourceApiKey":"secret","TileSource":{"UrlTemplate":"bad","Attribution":"bad"},"TimeZoneOverride":"Invalid/Old"}""";
        var settings = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(settings);
        Assert.Equal(AppSettings.UseSystemTimeZoneId, settings.SelectedTimeZoneId);
        Assert.Equal(5, settings.TimeSnapMinutes);
    }

    [Fact]
    public async Task OpenMeteoSample_MapsNearestHourlyPhotographyValues()
    {
        var json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Samples", "open-meteo-hourly.json"));
        var dto = JsonSerializer.Deserialize<OpenMeteoResponse>(json)!;
        var selected = Instant.FromUnixTimeSeconds(1719003500);
        var result = OpenMeteoMapper.Map(dto, selected, Instant.FromUtc(2024, 6, 21, 20, 0));
        Assert.Equal("Clear sky", result.Summary);
        Assert.Equal(7, result.CloudCoverPercent);
        Assert.Equal(1, result.PrecipitationProbabilityPercent);
        Assert.Equal(2.9, result.WindSpeedMetresPerSecond);
        Assert.Equal(25, result.VisibilityKilometres);
        Assert.Equal("None", result.PrecipitationType);
    }

    [Fact]
    public void OpenMeteoPartialFields_MapToNullWithoutFailure()
    {
        var dto = new OpenMeteoResponse { Hourly = new OpenMeteoHourly { Time = [1719003600], Temperature2m = [12.5] } };
        var result = OpenMeteoMapper.Map(dto, Instant.FromUnixTimeSeconds(1719003600), Instant.FromUnixTimeSeconds(1719000000));
        Assert.Equal(12.5, result.TemperatureCelsius);
        Assert.Null(result.CloudCoverPercent);
        Assert.Null(result.WindSpeedMetresPerSecond);
    }

    [Fact]
    public void GeographicCache_UsesDistanceAndExpiresAtTenMinutes()
    {
        var now = Instant.FromUtc(2024, 1, 1, 12, 0);
        var clock = new TestClock(now);
        var cache = new GeographicWeatherCache(clock);
        var origin = new GeoCoordinate(51.5, -0.1);
        var conditions = Conditions(now);
        cache.Store(origin, conditions);

        Assert.True(cache.TryGet(new GeoCoordinate(51.51, -0.1), now, 2, out _));
        Assert.False(cache.TryGet(new GeoCoordinate(51.6, -0.1), now, 2, out _));
        clock.Now = now + Duration.FromMinutes(10);
        Assert.False(cache.TryGet(origin, now, 2, out _));
        Assert.InRange(Angles.GreatCircleDistanceMetres(origin, new GeoCoordinate(51.51, -0.1)), 1_100, 1_120);
    }

    [Fact]
    public async Task ForcedRefresh_BypassesCacheAndReplacesIt()
    {
        var now = Instant.FromUnixTimeSeconds(1719003600);
        var clock = new TestClock(now);
        var cache = new GeographicWeatherCache(clock);
        var location = new GeoCoordinate(50.62, -2.27);
        cache.Store(location, Conditions(now) with { TemperatureCelsius = 1 });
        var handler = new CountingHandler(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Samples", "open-meteo-hourly.json")));
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.open-meteo.com/v1/") };
        var provider = new OpenMeteoWeatherProvider(client, cache, clock, NullLogger<OpenMeteoWeatherProvider>.Instance);
        var fields = WeatherSettings.DefaultFields;

        var cached = await provider.GetWeatherAsync(new WeatherRequest(location, now, fields, 5), CancellationToken.None);
        Assert.Equal(1, cached.Conditions!.TemperatureCelsius);
        Assert.Equal(0, handler.Count);
        var fresh = await provider.GetWeatherAsync(new WeatherRequest(location, now, fields, 5, true), CancellationToken.None);
        Assert.Equal(14, fresh.Conditions!.TemperatureCelsius);
        Assert.Equal(1, handler.Count);
        Assert.True(cache.TryGet(location, now, 0, out var replacement));
        Assert.Equal(14, replacement.TemperatureCelsius);
    }

    [Fact]
    public async Task ScoutingCard_GeneratesPngWithVersionedMetadata()
    {
        var instant = Instant.FromUtc(2024, 6, 21, 21, 0);
        var snapshot = Snapshot(instant);
        var clock = new TestClock(instant + Duration.FromMinutes(1));
        var exporter = new ScoutingCardExporter(new TimeZoneResolver(), clock);
        var png = await exporter.RenderPngAsync(snapshot,
            new ScoutingCardExportContext("Test ridge", null, new WeatherSettings(), "Metric"), CancellationToken.None);

        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8));
        var metadata = PngMetadataWriter.ReadText(png, ScoutingCardExporter.MetadataKey);
        Assert.NotNull(metadata);
        Assert.Contains("Andromeda Galaxy", metadata);
        using var document = JsonDocument.Parse(metadata);
        Assert.Equal(ScoutingCardExporter.ExportSchemaVersion, document.RootElement.GetProperty("exportSchemaVersion").GetInt32());
        Assert.True(document.RootElement.TryGetProperty("enabledWeatherFields", out _));
    }

    [Fact]
    public void TimeZoneResolver_UsesSavedZoneAndFallsBackForInvalid()
    {
        var resolver = new TimeZoneResolver();
        Assert.Equal("Europe/London", resolver.GetEffectiveId("Europe/London"));
        Assert.Equal(resolver.MachineTimeZoneId, resolver.GetEffectiveId(AppSettings.UseSystemTimeZoneId));
        Assert.Equal(resolver.MachineTimeZoneId, resolver.GetEffectiveId("Invalid/Zone"));
        Assert.Contains(resolver.MachineTimeZoneId, resolver.AvailableIds);
    }

    private static WeatherConditions Conditions(Instant instant) => new(instant, 12, 4, 5, 3, 2, 0, "None", 3, 240, 5, 14, 65, 8, 25, "Clear", instant);

    private static PlanningSnapshot Snapshot(Instant instant)
    {
        var target = new AstralTarget("andromeda", "Andromeda Galaxy", AstralTargetCategory.Galaxy, 0.712, 41.269, "J2000");
        var session = new PlanningSession(new GeoCoordinate(51.5, -0.1, 20), instant, "Europe/London", target.Id, new LensConfiguration());
        var events = new TargetEvents(instant - Duration.FromHours(2), instant + Duration.FromHours(1), instant + Duration.FromHours(4));
        var position = new TargetPosition(target, instant, new HorizontalCoordinate(120, 35), events);
        var path = new AstralPath(new LocalDate(2024, 6, 21), session.TimeZoneId, Duration.FromHours(1),
            [new AstralPathSample(instant, new HorizontalCoordinate(100, 20)), new AstralPathSample(instant + Duration.FromHours(1), new HorizontalCoordinate(120, 35))], events, instant);
        var terrain = new TerrainHorizonProfile(session.Observer, Enumerable.Range(0, 8).Select(x => new TerrainHorizonSample(x * 45, x == 2 ? 4 : 0)).ToArray(), true, "Synthetic", instant);
        var sunTarget = new AstralTarget("sun", "Sun", AstralTargetCategory.Solar, null, null, "of-date");
        var moonTarget = new AstralTarget("moon", "Moon", AstralTargetCategory.Lunar, null, null, "of-date");
        var twilight = new TwilightEvents(events.Rise, events.Set, events.Rise, events.Set, events.Rise, events.Set, events.Rise, events.Set);
        var sun = new TargetPosition(sunTarget, instant, new HorizontalCoordinate(280, -5), events, Twilight: twilight);
        var moon = new TargetPosition(moonTarget, instant, new HorizontalCoordinate(100, 20), events, .75, 180);
        return new PlanningSnapshot(session, position, path, new FieldOfView(60, 40, 70), terrain,
            new TerrainCrossings(instant, null), new WeatherResult(DataState.Ready, Conditions(instant), "Ready"), new AstronomyContext(sun, moon));
    }

    private JsonUserDataStore CreateStore(Instant now) => new(new TestPaths(_directory), NullLogger<JsonUserDataStore>.Instance, () => now);
    private sealed record TestPaths(string Path) : IUserDataPathProvider { public string GetApplicationDataDirectory() => Path; }
    private sealed class TestClock(Instant now) : IClock { public Instant Now { get; set; } = now; public Instant GetCurrentInstant() => Now; }
    private sealed class CountingHandler(string json) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
