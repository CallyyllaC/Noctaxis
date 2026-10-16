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
    [Fact]
    public async Task ConcurrentSavesProduceCompleteStateAndCancelledSavePreservesIt()
    {
        var now = Instant.FromUtc(2026, 9, 9, 20, 0);
        var store = CreateStore(now);
        var state = new PersistedState(4, new AppSettings(), [], PlanningSession.Default(now, "UTC"), null);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(index => store.SaveAsync(
            state with { Settings = new AppSettings(TimeSnapMinutes: index + 1) }, CancellationToken.None)));
        var saved = await store.LoadAsync(CancellationToken.None);
        Assert.InRange(saved.Settings.TimeSnapMinutes, 1, 20);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(state, cancelled.Token));
        Assert.Equal(saved.Settings.TimeSnapMinutes, (await store.LoadAsync(CancellationToken.None)).Settings.TimeSnapMinutes);
    }

    [Theory]
    [InlineData(15, 15)] [InlineData(55, 55)] [InlineData(37.5, 37.5)]
    [InlineData(0, 15)] [InlineData(99, 55)]
    public async Task TintStrengthPersistsClampsAndDefaultsWhenMissing(double value, double expected)
    {
        var now = Instant.FromUtc(2026, 1, 1, 0, 0); var store = CreateStore(now);
        await store.SaveAsync(new PersistedState(4, new AppSettings(CameraFraming:
            new CameraFramingSettings(TerrainTintStrengthPercent: value)), [],
            PlanningSession.Default(now, "UTC"), null), CancellationToken.None);
        Assert.Equal(expected, (await store.LoadAsync(CancellationToken.None)).Settings.EffectiveCameraFraming.TerrainTintStrengthPercent);
        var path = Path.Combine(store.StorageDirectory, "state.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json["Settings"]!["CameraFraming"]!.AsObject().Remove("TerrainTintStrengthPercent");
        await File.WriteAllTextAsync(path, json.ToJsonString());
        Assert.Equal(40, (await store.LoadAsync(CancellationToken.None)).Settings.EffectiveCameraFraming.TerrainTintStrengthPercent);
        Assert.Equal(40, new CameraFramingSettings(TerrainTintStrengthPercent: double.NaN).Normalised().TerrainTintStrengthPercent);
    }

    [Theory]
    [InlineData("#9A6F9E", "#9A6F9E")]
    [InlineData("#22bb88", "#22BB88")]
    [InlineData("invalid", "#9A6F9E")]
    [InlineData(null, "#9A6F9E")]
    public async Task TerrainColourPersistsAndMissingPropertyDefaults(string? colour, string expected)
    {
        var now = Instant.FromUtc(2026, 1, 1, 0, 0); var store = CreateStore(now);
        await store.SaveAsync(new PersistedState(4, new AppSettings(CameraFraming:
            new CameraFramingSettings(TerrainObstructionColour: colour!)), [],
            PlanningSession.Default(now, "UTC"), null), CancellationToken.None);
        Assert.Equal(expected, (await store.LoadAsync(CancellationToken.None)).Settings.EffectiveCameraFraming.TerrainObstructionColour);
        var path = Path.Combine(store.StorageDirectory, "state.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json["Settings"]!["CameraFraming"]!.AsObject().Remove("TerrainObstructionColour");
        await File.WriteAllTextAsync(path, json.ToJsonString());
        Assert.Equal("#9A6F9E", (await store.LoadAsync(CancellationToken.None)).Settings.EffectiveCameraFraming.TerrainObstructionColour);
    }

    /// <summary>
    /// Camera pitch runs from 45° down to straight up. Anything steeper down reads as -45°, and every pitch
    /// inside the range, 0° included, is kept exactly as it was.
    /// </summary>
    [Fact]
    public void CameraPitchRunsFromFortyFiveDegreesDownToStraightUp()
    {
        Assert.Equal(-45, CameraFramingSettings.MinimumCameraPitchDegrees);
        Assert.Equal(90, CameraFramingSettings.MaximumCameraPitchDegrees);
        double Pitch(double value) => new CameraFramingSettings(CameraPitchDegrees: value).Normalised().CameraPitchDegrees;
        Assert.Equal(-45, Pitch(-45));
        Assert.Equal(90, Pitch(90));
        Assert.Equal(-45, Pitch(-45.5));
        Assert.Equal(-45, Pitch(-90));
        Assert.Equal(90, Pitch(120));
        foreach (var kept in new[] { -44.5, -20, 0, 12.25, 89.9 }) Assert.Equal(kept, Pitch(kept));
        Assert.Equal(0, Pitch(double.NaN));
    }

    [Theory]
    [InlineData(-45, 0)] [InlineData(90, 50)] [InlineData(23.5, 12)] [InlineData(-30, 5)]
    // Saved by a build whose pitch went down to -90°: read as the new limit, -45°.
    [InlineData(-90, 5)] [InlineData(-60, 5)]
    [InlineData(-100, -5)] [InlineData(100, 80)]
    public async Task CameraFrameSettingsRoundTripAndClamp(double pitch, double threshold)
    {
        var now = Instant.FromUtc(2026, 1, 1, 0, 0); var store = CreateStore(now);
        await store.SaveAsync(new PersistedState(4, new AppSettings(CameraFraming: new CameraFramingSettings(
            CameraPitchDegrees: pitch, MinimumTerrainFrameCoveragePercent: threshold)), [],
            PlanningSession.Default(now, "UTC"), null), CancellationToken.None);
        var framing = (await store.LoadAsync(CancellationToken.None)).Settings.EffectiveCameraFraming;
        Assert.Equal(Math.Clamp(pitch, CameraFramingSettings.MinimumCameraPitchDegrees, CameraFramingSettings.MaximumCameraPitchDegrees), framing.CameraPitchDegrees);
        Assert.Equal(Math.Clamp(threshold, 0, 50), framing.MinimumTerrainFrameCoveragePercent);
        var path = Path.Combine(store.StorageDirectory, "state.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var properties = json["Settings"]!["CameraFraming"]!.AsObject();
        properties.Remove("CameraPitchDegrees"); properties.Remove("MinimumTerrainFrameCoveragePercent");
        await File.WriteAllTextAsync(path, json.ToJsonString());
        framing = (await store.LoadAsync(CancellationToken.None)).Settings.EffectiveCameraFraming;
        Assert.Equal(0, framing.CameraPitchDegrees); Assert.Equal(5, framing.MinimumTerrainFrameCoveragePercent);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(25)]
    [InlineData(45)]
    public async Task LegacyAngularDetailLoadsAtFixedProductionResolution(double legacyDetail)
    {
        var now = Instant.FromUtc(2026, 1, 1, 0, 0);
        var store = CreateStore(now);
        await store.SaveAsync(new PersistedState(4, new AppSettings(CameraFraming: new CameraFramingSettings()), [],
            PlanningSession.Default(now, "UTC"), null), CancellationToken.None);
        var path = Path.Combine(store.StorageDirectory, "state.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json["Settings"]!["CameraFraming"]!["TerrainCastAngularDetailDegrees"] = legacyDetail;
        await File.WriteAllTextAsync(path, json.ToJsonString());
        Assert.Equal(1, (await store.LoadAsync(CancellationToken.None)).Settings.EffectiveCameraFraming.TerrainCastAngularDetailDegrees);
    }

    [Fact]
    public async Task TerrainEnableSetting_RoundTripsAndDefaultsOnWhenAbsent()
    {
        var now = Instant.FromUtc(2026, 1, 1, 0, 0);
        var store = CreateStore(now);
        await store.SaveAsync(new PersistedState(4, new AppSettings(EnableTerrainCalculations: false), [],
            PlanningSession.Default(now, "UTC"), null), CancellationToken.None);
        Assert.False((await store.LoadAsync(CancellationToken.None)).Settings.EnableTerrainCalculations);
        var path = Path.Combine(store.StorageDirectory, "state.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.True(json["Settings"]!.AsObject().Remove("EnableTerrainCalculations"));
        await File.WriteAllTextAsync(path, json.ToJsonString());
        Assert.True((await store.LoadAsync(CancellationToken.None)).Settings.EnableTerrainCalculations);
    }

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

    [Theory]
    [InlineData(FileShare.None)]
    [InlineData(FileShare.Delete)]
    public async Task UnreadableStateFile_IsReportedUnavailableAndNeitherReplacedNorQuarantined(FileShare holderShare)
    {
        // V-01: another process holding state.json is not corruption. FileShare.Delete only blocks a
        // reader on Windows; elsewhere the file is readable and there is nothing to test.
        if (holderShare == FileShare.Delete && !OperatingSystem.IsWindows()) return;
        var now = Instant.FromUtc(2024, 1, 1, 0, 0);
        var location = new SavedLocation(Guid.NewGuid(), "Dark ridge", new GeoCoordinate(51, -1), "UTC");
        await CreateStore(now).SaveAsync(new PersistedState(4, new AppSettings(), [location],
            PlanningSession.Default(now, "UTC"), null), CancellationToken.None);
        var path = Path.Combine(_directory, "state.json");
        var store = new JsonUserDataStore(new TestPaths(_directory), NullLogger<JsonUserDataStore>.Instance, () => now,
            [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)]);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, holderShare))
        {
            var failure = await Assert.ThrowsAsync<UserStateUnavailableException>(
                () => store.LoadAsync(CancellationToken.None));
            Assert.Equal(path, failure.StatePath);
        }

        Assert.Empty(Directory.GetFiles(_directory, "state.json.corrupt-*"));
        Assert.Equal(location, Assert.Single((await store.LoadAsync(CancellationToken.None)).Locations));
    }

    [Fact]
    public async Task BrieflyHeldStateFile_IsReadOnRetry()
    {
        var now = Instant.FromUtc(2024, 1, 1, 0, 0);
        var location = new SavedLocation(Guid.NewGuid(), "Dark ridge", new GeoCoordinate(51, -1), "UTC");
        await CreateStore(now).SaveAsync(new PersistedState(4, new AppSettings(), [location],
            PlanningSession.Default(now, "UTC"), null), CancellationToken.None);
        var store = new JsonUserDataStore(new TestPaths(_directory), NullLogger<JsonUserDataStore>.Instance, () => now,
            [TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(2)]);
        var holder = new FileStream(Path.Combine(_directory, "state.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var loading = store.LoadAsync(CancellationToken.None);
        await Task.Delay(100);
        await holder.DisposeAsync();

        Assert.Equal(location, Assert.Single((await loading).Locations));
    }

    [Fact]
    public async Task Save_KeepsThePreviousStateAsRollingBackup()
    {
        var now = Instant.FromUtc(2024, 1, 1, 0, 0);
        var store = CreateStore(now);
        var backup = Path.Combine(_directory, "state.json.bak");
        var first = new SavedLocation(Guid.NewGuid(), "First", new GeoCoordinate(51, -1), "UTC");
        var second = new SavedLocation(Guid.NewGuid(), "Second", new GeoCoordinate(52, -1), "UTC");
        await store.SaveAsync(new PersistedState(4, new AppSettings(), [first],
            PlanningSession.Default(now, "UTC"), null), CancellationToken.None);
        Assert.False(File.Exists(backup));

        await store.SaveAsync(new PersistedState(4, new AppSettings(), [first, second],
            PlanningSession.Default(now, "UTC"), null), CancellationToken.None);

        Assert.Equal(2, (await store.LoadAsync(CancellationToken.None)).Locations.Count);
        var previous = Directory.CreateDirectory(Path.Combine(_directory, "previous")).FullName;
        File.Copy(backup, Path.Combine(previous, "state.json"));
        var restored = await new JsonUserDataStore(new TestPaths(previous), NullLogger<JsonUserDataStore>.Instance, () => now)
            .LoadAsync(CancellationToken.None);
        Assert.Equal(first, Assert.Single(restored.Locations));
    }

    [Theory]
    [InlineData("Session")]
    [InlineData("Lens")]
    public async Task IncompletePersistence_IsQuarantinedAndDefaultsRecover(string missingMember)
    {
        var now = Instant.FromUtc(2024, 1, 1, 0, 0);
        var store = CreateStore(now);
        var location = new SavedLocation(Guid.NewGuid(), "Ridge", new GeoCoordinate(51, -1), "UTC");
        await store.SaveAsync(new PersistedState(4, new AppSettings(), [location],
            PlanningSession.Default(now, "UTC"), null), CancellationToken.None);
        var path = Path.Combine(_directory, "state.json");
        var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        if (missingMember == "Session") document.Remove("Session");
        else document["Session"]!.AsObject().Remove("Lens");
        await File.WriteAllTextAsync(path, document.ToJsonString());

        var loaded = await CreateStore(now).LoadAsync(CancellationToken.None);

        Assert.Empty(loaded.Locations);
        Assert.Equal(now, loaded.Session.Instant);
        Assert.NotNull(loaded.Session.Lens);
        Assert.False(File.Exists(path));
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
        cache.Store(origin, "fields", conditions);

        Assert.True(cache.TryGet(new GeoCoordinate(51.51, -0.1), now, 2, "fields", out _));
        Assert.False(cache.TryGet(new GeoCoordinate(51.6, -0.1), now, 2, "fields", out _));
        clock.Now = now + Duration.FromMinutes(10);
        Assert.False(cache.TryGet(origin, now, 2, "fields", out _));
        Assert.InRange(Angles.GreatCircleDistanceMetres(origin, new GeoCoordinate(51.51, -0.1)), 1_100, 1_120);
    }

    [Fact]
    public async Task DownloadedForecast_ServesOtherPlannerTimesWithoutRefetching()
    {
        // One Open-Meteo request covers 17 days of hourly data; moving planner time must reuse it.
        var start = Instant.FromUtc(2024, 6, 21, 0, 0);
        var clock = new TestClock(start);
        var provider = CreateForecastProvider(clock, HourlyForecastJson(start, 48), out var handler, out _);
        var location = new GeoCoordinate(51.5, -0.1);
        var fields = WeatherSettings.DefaultFields;

        var first = await provider.GetWeatherAsync(new WeatherRequest(location, start, fields, 5), CancellationToken.None);
        var later = await provider.GetWeatherAsync(
            new WeatherRequest(location, start + Duration.FromHours(6), fields, 5), CancellationToken.None);
        var nextDay = await provider.GetWeatherAsync(
            new WeatherRequest(location, start + Duration.FromHours(30), fields, 5), CancellationToken.None);

        Assert.Equal(1, handler.Count);
        Assert.All(new[] { first, later, nextDay }, result => Assert.Equal(DataState.Ready, result.State));
        Assert.Equal(start, first.Conditions!.ForecastInstant);
        Assert.Equal(start + Duration.FromHours(6), later.Conditions!.ForecastInstant);
        Assert.Equal(start + Duration.FromHours(30), nextDay.Conditions!.ForecastInstant);
        // Each hour carries its own values, and the retrieval time stays that of the single download.
        Assert.Equal(6, later.Conditions.TemperatureCelsius);
        Assert.Equal(30, nextDay.Conditions.TemperatureCelsius);
        Assert.Equal(start, nextDay.Conditions.RetrievedAt);
        Assert.False(nextDay.Conditions.IsStale);
        Assert.Equal("Cached Open-Meteo forecast", nextDay.Message);
    }

    [Fact]
    public async Task DownloadedForecast_IsNotReusedWhenStaleOutOfRangeOrElsewhere()
    {
        var start = Instant.FromUtc(2024, 6, 21, 0, 0);
        var clock = new TestClock(start);
        var provider = CreateForecastProvider(clock, HourlyForecastJson(start, 48), out var handler, out var cache);
        var location = new GeoCoordinate(51.5, -0.1);
        IReadOnlyList<WeatherField> fields = [WeatherField.TotalCloudCover, WeatherField.Temperature];
        await provider.GetWeatherAsync(new WeatherRequest(location, start, fields, 5), CancellationToken.None);
        Assert.Equal(1, handler.Count);

        // A location beyond the configured cache distance is a different forecast.
        await provider.GetWeatherAsync(new WeatherRequest(new GeoCoordinate(52.5, -0.1), start + Duration.FromHours(6),
            fields, 5), CancellationToken.None);
        Assert.Equal(2, handler.Count);

        // A time the download does not cover is not answered from it.
        var outside = await provider.GetWeatherAsync(new WeatherRequest(location, start + Duration.FromDays(20),
            fields, 5), CancellationToken.None);
        Assert.Equal(3, handler.Count);
        Assert.Equal(DataState.Error, outside.State);

        // A newly enabled field is not present in the retained forecast.
        await provider.GetWeatherAsync(new WeatherRequest(location, start + Duration.FromHours(6),
            [.. fields, WeatherField.DewPoint], 5), CancellationToken.None);
        Assert.Equal(4, handler.Count);

        // Past the shared freshness window the forecast expires with the conditions cache.
        clock.Now = start + cache.MaximumAge;
        await provider.GetWeatherAsync(new WeatherRequest(location, start + Duration.FromHours(6), fields, 5),
            CancellationToken.None);
        Assert.Equal(5, handler.Count);
    }

    private static OpenMeteoWeatherProvider CreateForecastProvider(TestClock clock, string json,
        out CountingHandler handler, out GeographicWeatherCache cache)
    {
        handler = new CountingHandler(json);
        cache = new GeographicWeatherCache(clock);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.open-meteo.com/v1/") };
        return new OpenMeteoWeatherProvider(client, cache, clock,
            NullLogger<OpenMeteoWeatherProvider>.Instance, new OpenMeteoForecastCache(clock));
    }

    // Hourly rows whose temperature and cloud cover are the hour offset, so tests can tell them apart.
    private static string HourlyForecastJson(Instant start, int hours, int[]? cloud = null)
    {
        var times = string.Join(',', Enumerable.Range(0, hours)
            .Select(hour => (start + Duration.FromHours(hour)).ToUnixTimeSeconds()));
        var values = string.Join(',', Enumerable.Range(0, hours));
        if (cloud is not null)
            return "{\"hourly\":{\"time\":[" + times + "],\"temperature_2m\":[" + values +
                   "],\"cloud_cover\":[" + string.Join(',', cloud) + "],\"weather_code\":[" +
                   string.Join(',', Enumerable.Repeat(0, hours)) + "]}}";
        var codes = string.Join(',', Enumerable.Repeat(0, hours));
        return "{\"hourly\":{\"time\":[" + times + "],\"temperature_2m\":[" + values +
               "],\"cloud_cover\":[" + values + "],\"weather_code\":[" + codes + "]}}";
    }

    [Fact]
    public async Task ForcedRefresh_BypassesCacheAndReplacesIt()
    {
        var now = Instant.FromUnixTimeSeconds(1719003600);
        var clock = new TestClock(now);
        var cache = new GeographicWeatherCache(clock);
        var location = new GeoCoordinate(50.62, -2.27);
        cache.Store(location, DefaultFieldsKey, Conditions(now) with { TemperatureCelsius = 1 });
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
        Assert.True(cache.TryGet(location, now, 0, DefaultFieldsKey, out var replacement));
        Assert.Equal(14, replacement.TemperatureCelsius);
    }

    [Fact]
    public async Task CachedWeather_SelectsTheSameHourAsTheForecast()
    {
        // D03 (phase-0 audit): 10:00 cloud 10, 11:00 cloud 90. After 10:00 was mapped and cached, a request for
        // 10:50 must get 11:00, the hour the forecast itself selects, and still without another download.
        var start = Instant.FromUtc(2026, 10, 14, 10, 0);
        var clock = new TestClock(start);
        var provider = CreateForecastProvider(clock, HourlyForecastJson(start, 2, [10, 90]), out var handler, out _);
        var location = new GeoCoordinate(51.5, -0.1);
        var fields = WeatherSettings.DefaultFields;

        var ten = await provider.GetWeatherAsync(new WeatherRequest(location, start, fields, 5), CancellationToken.None);
        var tenFifty = await provider.GetWeatherAsync(
            new WeatherRequest(location, start + Duration.FromMinutes(50), fields, 5), CancellationToken.None);

        Assert.Equal((start, 10d), (ten.Conditions!.ForecastInstant, ten.Conditions.CloudCoverPercent!.Value));
        Assert.Equal((start + Duration.FromHours(1), 90d),
            (tenFifty.Conditions!.ForecastInstant, tenFifty.Conditions.CloudCoverPercent!.Value));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task CachedWeather_AgreesWithTheForecastAtEveryMinuteAcrossSeveralCachedHours()
    {
        var start = Instant.FromUtc(2026, 10, 14, 0, 0);
        var clock = new TestClock(start);
        var json = HourlyForecastJson(start, 6);
        var hourly = JsonSerializer.Deserialize<OpenMeteoResponse>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Hourly!;
        var provider = CreateForecastProvider(clock, json, out var handler, out _);
        var location = new GeoCoordinate(51.5, -0.1);
        var fields = WeatherSettings.DefaultFields;
        // Every hour is cached first, so each later answer could come from a neighbouring cached hour.
        for (var hour = 0; hour < 6; hour++)
            await provider.GetWeatherAsync(new WeatherRequest(location, start + Duration.FromHours(hour), fields, 5), CancellationToken.None);

        for (var minute = 0; minute <= 300; minute++)
        {
            var at = start + Duration.FromMinutes(minute);
            Assert.True(OpenMeteoMapper.TryMap(hourly, at, start, out var expected));
            var cached = await provider.GetWeatherAsync(new WeatherRequest(location, at, fields, 5), CancellationToken.None);
            Assert.True(expected.ForecastInstant == cached.Conditions!.ForecastInstant, $"minute {minute}");
            Assert.Equal(expected.TemperatureCelsius, cached.Conditions.TemperatureCelsius);
        }
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(29, true)]
    [InlineData(30, true)]   // At exactly half past, the earlier hour, as the forecast search keeps the first.
    [InlineData(31, false)]
    [InlineData(-29, true)]
    [InlineData(-30, false)]
    [InlineData(-31, false)]
    public void SelectsHour_IsTheForecastsOwnNearestHourRule(int minutesAfterHour, bool selected)
    {
        var hour = Instant.FromUtc(2026, 10, 14, 10, 0);
        var at = hour + Duration.FromMinutes(minutesAfterHour);
        Assert.Equal(selected, OpenMeteoMapper.SelectsHour(hour, at));
        var hourly = new OpenMeteoHourly
        {
            Time = [(hour - Duration.FromHours(1)).ToUnixTimeSeconds(), hour.ToUnixTimeSeconds(), (hour + Duration.FromHours(1)).ToUnixTimeSeconds()]
        };
        Assert.True(OpenMeteoMapper.TryMap(hourly, at, hour, out var mapped));
        Assert.Equal(selected, mapped.ForecastInstant == hour);
    }

    [Fact]
    public async Task CachedWeather_IsNotReusedForADifferentSetOfRequestedFields()
    {
        var start = Instant.FromUtc(2026, 10, 14, 10, 0);
        var clock = new TestClock(start);
        var provider = CreateForecastProvider(clock, HourlyForecastJson(start, 4), out var handler, out _);
        var location = new GeoCoordinate(51.5, -0.1);
        IReadOnlyList<WeatherField> fields = [WeatherField.TotalCloudCover, WeatherField.Temperature];
        await provider.GetWeatherAsync(new WeatherRequest(location, start, fields, 5), CancellationToken.None);
        await provider.GetWeatherAsync(new WeatherRequest(location, start + Duration.FromHours(1), fields, 5), CancellationToken.None);
        Assert.Equal(1, handler.Count);

        // The cached 11:00 conditions lack dew point: a request that asks for it is not answered from them.
        await provider.GetWeatherAsync(new WeatherRequest(location, start + Duration.FromHours(1),
            [.. fields, WeatherField.DewPoint], 5), CancellationToken.None);
        Assert.Equal(2, handler.Count);
        // The original request is still answered from its own cached hour.
        var again = await provider.GetWeatherAsync(new WeatherRequest(location, start + Duration.FromHours(1), fields, 5),
            CancellationToken.None);
        Assert.Equal(2, handler.Count);
        Assert.Equal(start + Duration.FromHours(1), again.Conditions!.ForecastInstant);
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

    private static readonly string DefaultFieldsKey =
        OpenMeteoWeatherProvider.VariablesKey(OpenMeteoVariables.For(WeatherSettings.DefaultFields));

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
