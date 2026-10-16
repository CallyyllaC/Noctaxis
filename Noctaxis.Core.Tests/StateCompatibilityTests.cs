using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using NodaTime;

namespace Noctaxis.Core.Tests;

/// <summary>
/// <c>state.json</c> from earlier versions still loads, keeping what the user set. The fixtures under
/// Samples/Compatibility were written by those versions' own <see cref="JsonUserDataStore"/>, built from the commits
/// named in their file names:
/// - a68eff4 (2026-08-06), the only version that wrote schema 3;
/// - ef58a7b (2026-09-04), the first that wrote schema 4;
/// - d735bcf (2026-09-30), the last committed schema-4 writer.
/// Each is loaded and saved again by the current store, and every value removed or changed on the way must be one
/// listed here, each a known migration. Anything else is data lost or reinterpreted and fails.
/// </summary>
public sealed class StateCompatibilityTests
{
    private sealed record Paths(string Root) : IUserDataPathProvider { public string GetApplicationDataDirectory() => Root; }

    private static readonly Instant Now = Instant.FromUtc(2026, 10, 8, 12, 0);
    private static readonly Guid Ridge = Guid.Parse("6f1c2b9e-3a4d-4c1e-9b7a-2d5e8f0a1b3c");
    private static readonly Guid Harbour = Guid.Parse("0b9d8c7e-6f5a-4b3c-8d2e-1f0a9b8c7d6e");

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Samples", "Compatibility", name);

    /// <summary>Every leaf of a JSON document by its path, with its raw value.</summary>
    private static Dictionary<string, string> Leaves(string json)
    {
        var leaves = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(JsonElement element, string path)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject()) Walk(property.Value, $"{path}.{property.Name}");
                    break;
                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var item in element.EnumerateArray()) Walk(item, $"{path}[{index++}]");
                    if (index == 0) leaves[path] = "[]";
                    break;
                default:
                    leaves[path] = element.GetRawText();
                    break;
            }
        }
        using var document = JsonDocument.Parse(json);
        Walk(document.RootElement, "$");
        return leaves;
    }

    /// <summary>A store of its own, in a folder of its own, starting from <paramref name="json"/> if given.</summary>
    private static async Task<T> WithStoreAsync<T>(string? json, Func<JsonUserDataStore, string, Task<T>> use)
    {
        var directory = Path.Combine(Path.GetTempPath(), "noctaxis-state-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var paths = new Paths(directory);
            var file = JsonUserDataStore.StateFilePath(paths);
            if (json is not null) await File.WriteAllTextAsync(file, json);
            var result = await use(new JsonUserDataStore(paths, NullLogger<JsonUserDataStore>.Instance, () => Now), file);
            // Read as it is, never set aside as unreadable.
            Assert.Empty(Directory.GetFiles(directory, "state.json.corrupt-*"));
            return result;
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>The file loaded by the current store, and the file the current store then saves.</summary>
    private static Task<(PersistedState State, string Saved)> LoadAndSaveAsync(string json) =>
        WithStoreAsync(json, async (store, file) =>
        {
            var state = await store.LoadAsync(CancellationToken.None);
            await store.SaveAsync(state, CancellationToken.None);
            return (state, await File.ReadAllTextAsync(file));
        });

    // ---- What loading may remove or change, and nothing else -----------------------------------------------------

    /// <summary>Settings Noctaxis no longer has: dropped on load, by design.</summary>
    private static readonly Dictionary<string, string[]> Removed = new()
    {
        // The DEM folder settings, from before terrain was downloaded and cached.
        ["state-v3-a68eff4.json"] = ["$.Settings.DemDirectory", "$.Locations[0].PreferredDemFolder", "$.Locations[1].PreferredDemFolder"],
        ["state-v4-ef58a7b.json"] = [],
        // The terrain minimap's context setting, and the computed value that version wrote out alongside it.
        ["state-v4-d735bcf.json"] = ["$.Settings.TerrainMinimapContext", "$.Settings.EffectiveTerrainMinimapContext"],
    };

    /// <summary>
    /// Values loading rewrites, by design. The session's lens preset becomes Custom: the lens is now taken from the
    /// chosen camera and lens profiles, and the sensor's size is kept.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> Changed = new()
    {
        ["state-v3-a68eff4.json"] = new() { ["$.Session.Lens.Preset"] = "\"Custom\"" },
        ["state-v4-ef58a7b.json"] = new() { ["$.Session.Lens.Preset"] = "\"Custom\"" },
        ["state-v4-d735bcf.json"] = new() { ["$.Session.Lens.Preset"] = "\"Custom\"" },
    };

    [Theory]
    [InlineData("state-v3-a68eff4.json")]
    [InlineData("state-v4-ef58a7b.json")]
    [InlineData("state-v4-d735bcf.json")]
    public async Task LoadingAnEarlierFileLosesOrChangesOnlyWhatIsKnown(string fixture)
    {
        var original = await File.ReadAllTextAsync(Fixture(fixture));
        var (_, saved) = await LoadAndSaveAsync(original);
        var before = Leaves(original);
        var after = Leaves(saved);

        Assert.Equal(Removed[fixture].Order(), before.Keys.Where(path => !after.ContainsKey(path)).Order());
        var changed = before.Keys.Where(path => after.TryGetValue(path, out var value) && value != before[path])
            .ToDictionary(path => path, path => after[path]);
        Assert.Equal(Changed[fixture].OrderBy(pair => pair.Key), changed.OrderBy(pair => pair.Key));
    }

    /// <summary>Loading is settled after one pass: an earlier file, loaded and saved twice, is the same both times.</summary>
    [Theory]
    [InlineData("state-v3-a68eff4.json")]
    [InlineData("state-v4-ef58a7b.json")]
    [InlineData("state-v4-d735bcf.json")]
    public async Task MigratingTwiceChangesNothingMore(string fixture)
    {
        var (_, once) = await LoadAndSaveAsync(await File.ReadAllTextAsync(Fixture(fixture)));
        var (_, twice) = await LoadAndSaveAsync(once);
        Assert.Equal(once, twice);
    }

    // ---- What each earlier file keeps --------------------------------------------------------------------------------

    private static void HasTheCommonLocations(IReadOnlyList<SavedLocation> locations)
    {
        Assert.Equal(2, locations.Count);
        var ridge = locations[0];
        Assert.Equal((Ridge, "Dark Ridge", new GeoCoordinate(51.2, -1.3, 410), "Europe/London"), (ridge.Id, ridge.Name, ridge.Coordinate, ridge.TimeZoneId));
        Assert.Equal(("North-facing view", SensorPreset.FullFrame, "Wiltshire", true, 0),
            (ridge.Notes, ridge.PreferredSensor, ridge.RegionDescription, ridge.IsFavourite, ridge.SortOrder));
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 20, 0, 0, TimeSpan.Zero), ridge.LastUsedUtc);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), ridge.DateAddedUtc);
        var harbour = locations[1];
        Assert.Equal((Harbour, "Harbour wall", new GeoCoordinate(-33.86785, 151.20732, 4), "Australia/Sydney", 1),
            (harbour.Id, harbour.Name, harbour.Coordinate, harbour.TimeZoneId, harbour.SortOrder));
        Assert.Null(harbour.PreferredSensor);
        Assert.False(harbour.IsFavourite);
    }

    private static void HasTheCommonSettingsAndSession(PersistedState state, double focalLength)
    {
        var settings = state.Settings;
        Assert.Equal("America/Denver", settings.SelectedTimeZoneId);
        Assert.Equal(15, settings.TimeSnapMinutes);
        Assert.Equal([WeatherField.TotalCloudCover, WeatherField.WindSpeed, WeatherField.MoonPhase], settings.EffectiveWeather.EnabledFields);
        Assert.Equal(12, settings.EffectiveWeather.CacheDistanceKilometres);
        // The targets, their visibility and their order, and the focused one.
        Assert.Equal([("sun", true), ("moon", false), ("m31", true)],
            settings.EffectiveCelestialObjects.ConfiguredObjects!.OrderBy(item => item.Order).Select(item => (item.TargetId, item.IsVisible)));
        Assert.Equal("m31", settings.EffectiveCelestialObjects.DefaultPrimaryTargetId);
        var framing = settings.EffectiveCameraFraming;
        Assert.Equal((false, 120d, 5d, false, 25d, 2d),
            (framing.IsOverlayVisible, framing.ManualBearingDegrees, framing.CompositionOffsetDegrees, framing.ShowVisibilityLimits,
                framing.ShadingOpacityPercent, framing.LineThickness));

        var session = state.Session;
        Assert.Equal(new GeoCoordinate(51.2, -1.3, 410), session.Observer);
        Assert.Equal(Instant.FromUtc(2026, 10, 7, 21, 30), session.Instant);
        Assert.Equal("America/Denver", session.TimeZoneId);
        Assert.Equal("m31", session.TargetId);
        Assert.True(session.IsTargetFocused);
        Assert.Equal(Ridge, session.SavedLocationId);
        Assert.Equal((23.5, 15.6, focalLength, CameraOrientation.Portrait),
            (session.Lens.SensorWidthMillimetres, session.Lens.SensorHeightMillimetres, session.Lens.FocalLengthMillimetres, session.Lens.Orientation));
        Assert.Equal(Ridge, state.MostRecentLocationId);
        Assert.Equal(new GeoCoordinate(52, -3.5, 300), state.LastCustomCoordinate);
    }

    /// <summary>
    /// Schema 3: settings, locations and the planning session are kept. Schema 4's two additions are derived from what
    /// the file had:
    /// - equipment: a camera of the session's sensor size and a prime lens at its focal length;
    /// - observer elevation: each saved elevation, carried over as the place's manual ground elevation.
    /// </summary>
    [Fact]
    public async Task ASchemaThreeFileKeepsItsSettingsLocationsAndSession()
    {
        var (state, _) = await LoadAndSaveAsync(await File.ReadAllTextAsync(Fixture("state-v3-a68eff4.json")));
        Assert.Equal(3, state.Version);
        Assert.Equal("Imperial", state.Settings.Units);
        HasTheCommonLocations(state.Locations);
        HasTheCommonSettingsAndSession(state, focalLength: 50);

        var camera = Assert.Single(state.Settings.Equipment!.Cameras!);
        var lens = Assert.Single(state.Settings.Equipment!.Lenses!);
        Assert.Equal((23.5, 15.6), (camera.SensorWidthMillimetres, camera.SensorHeightMillimetres));
        Assert.Equal((50d, 50d), (lens.MinimumFocalLengthMillimetres, lens.MaximumFocalLengthMillimetres));
        Assert.Equal((camera.Id, lens.Id), (state.Session.CameraProfileId, state.Session.LensProfileId));

        Assert.Equal(new ObserverElevationState(null, 410), state.Session.ObserverElevation);
        Assert.Equal(new ObserverElevationState(null, 410), state.Locations[0].ObserverElevation);
        Assert.Equal(new ObserverElevationState(null, 4), state.Locations[1].ObserverElevation);
        Assert.Null(state.Settings.LegacyData);
    }

    /// <summary>The first schema-4 file: its equipment, camera height and elevations are kept exactly, not re-derived.</summary>
    [Fact]
    public async Task AnEarlySchemaFourFileKeepsItsEquipmentAndElevations()
    {
        var (state, _) = await LoadAndSaveAsync(await File.ReadAllTextAsync(Fixture("state-v4-ef58a7b.json")));
        Assert.Equal(4, state.Version);
        Assert.Equal("Imperial", state.Settings.Units);
        HasTheCommonLocations(state.Locations);
        HasTheCommonSettingsAndSession(state, focalLength: 35);
        Assert.Equal(2.5, state.Settings.EffectiveCameraHeightAboveGroundMetres);
        Assert.True(state.Settings.TerrainDebugOverlay);

        var camera = Assert.Single(state.Settings.Equipment!.Cameras!);
        Assert.Equal(("cam-xt5", "Fujifilm X-T5", 23.5, 15.6, "Fujifilm", "X-T5"),
            (camera.Id, camera.DisplayName, camera.SensorWidthMillimetres, camera.SensorHeightMillimetres, camera.Manufacturer, camera.Model));
        var lens = Assert.Single(state.Settings.Equipment!.Lenses!);
        Assert.Equal(("lens-1655", 16d, 55d), (lens.Id, lens.MinimumFocalLengthMillimetres, lens.MaximumFocalLengthMillimetres));
        Assert.Equal(("cam-xt5", "lens-1655"), (state.Session.CameraProfileId, state.Session.LensProfileId));

        // Both the terrain's and the user's own elevation, as saved.
        Assert.Equal(new ObserverElevationState(405, 420), state.Session.ObserverElevation);
        Assert.Equal(new ObserverElevationState(405, 420), state.Locations[0].ObserverElevation);
        Assert.Null(state.Locations[1].ObserverElevation);
    }

    /// <summary>The last committed schema-4 file: appearance, language, map and layer preferences and terrain settings kept.</summary>
    [Fact]
    public async Task TheLatestCommittedSchemaFourFileKeepsEveryPreference()
    {
        var (state, _) = await LoadAndSaveAsync(await File.ReadAllTextAsync(Fixture("state-v4-d735bcf.json")));
        Assert.Equal(4, state.Version);
        var settings = state.Settings;
        Assert.Equal("UK", settings.Units);
        HasTheCommonLocations(state.Locations);
        HasTheCommonSettingsAndSession(state, focalLength: 35);
        Assert.Equal(new AppearancePreferences("builtin.highcontrast", ColourVisionMode.Deuteranopia, 1.5, AppearanceMode.Light), settings.Appearance);
        Assert.Equal(ExternalMapProvider.Mapillary, settings.ExternalMapProvider);
        Assert.Equal(new LightPollutionPreferences(true, .35), settings.LightPollution);
        Assert.Equal(new TerrainReliefPreferences(false, .4), settings.TerrainRelief);
        Assert.Equal("en-GB", settings.Language);
        Assert.Equal(1L * 1024 * 1024 * 1024, settings.TerrainCacheLimitBytes);
        Assert.False(settings.EnableTerrainCalculations);
        var framing = settings.EffectiveCameraFraming;
        Assert.Equal((12.5, 15d, "#336699", 30d),
            (framing.CameraPitchDegrees, framing.MinimumTerrainFrameCoveragePercent, framing.TerrainObstructionColour, framing.TerrainTintStrengthPercent));
        Assert.Equal(new ObserverElevationState(405, 420), state.Session.ObserverElevation);
    }

    // ---- The current schema -----------------------------------------------------------------------------------

    /// <summary>
    /// The current schema, with every setting away from its default, saved and loaded: the same file comes back, and
    /// what the user set is what is read.
    /// </summary>
    [Fact]
    public async Task TheCurrentSchemaSurvivesASaveAndLoad()
    {
        var objects = new[] { new CelestialObjectSelection("sun", true, 0), new CelestialObjectSelection("moon", false, 1), new CelestialObjectSelection("m31", true, 2) };
        var elevation = new ObserverElevationState(405, 420);
        var equipment = new EquipmentSettings(
            [new CameraProfile("cam-xt5", "Fujifilm X-T5", 23.5, 15.6, "Fujifilm", "X-T5")],
            [new LensProfile("lens-1655", "XF 16-55mm", 16, 55, "Fujifilm", "XF16-55mmF2.8")]);
        var settings = new AppSettings("UK", "America/Denver", new WeatherSettings([WeatherField.TotalCloudCover, WeatherField.Moonrise], 12), 15,
            new CelestialObjectSettings(objects, "m31"),
            new CameraFramingSettings(false, 120, 5, false, 25, 2, 1, 12.5, 15, "#336699", 30, true, true, false),
            2.5, equipment, true, 1L * 1024 * 1024 * 1024, false,
            new AppearancePreferences("builtin.highcontrast", ColourVisionMode.Tritanopia, 1.25, AppearanceMode.System),
            null, ExternalMapProvider.GoogleMaps, new LightPollutionPreferences(true, .35), new TerrainReliefPreferences(false, .4), "en-GB",
            PlannerPip: new PlannerPipSettings(IsEnabled: false));
        var locations = new[]
        {
            new SavedLocation(Ridge, "Dark Ridge", new GeoCoordinate(51.2, -1.3, 410), "Europe/London", "North-facing view",
                SensorPreset.FullFrame, "Wiltshire", true, new DateTimeOffset(2026, 8, 1, 20, 0, 0, TimeSpan.Zero), 3,
                new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), elevation)
        };
        var session = new PlanningSession(new GeoCoordinate(51.2, -1.3, 410), Instant.FromUtc(2026, 10, 7, 21, 30), "America/Denver", "m31",
            new LensConfiguration(SensorPreset.Custom, 23.5, 15.6, 35, CameraOrientation.Portrait), Ridge, objects, "cam-xt5", "lens-1655", elevation);
        var state = new PersistedState(4, settings, locations, session, Ridge, new GeoCoordinate(52, -3.5, 300));

        var (first, firstFile, loaded) = await WithStoreAsync<(PersistedState, string, PersistedState)>(null, async (store, file) =>
        {
            await store.SaveAsync(state, CancellationToken.None);
            var written = await File.ReadAllTextAsync(file);
            return (state, written, await store.LoadAsync(CancellationToken.None));
        });
        var (_, again) = await LoadAndSaveAsync(firstFile);
        Assert.Equal(Leaves(firstFile), Leaves(again));

        Assert.Equal(4, loaded.Version);
        Assert.Equal(first.Settings.Appearance, loaded.Settings.Appearance);
        Assert.Equal(first.Settings.CameraFraming, loaded.Settings.CameraFraming);
        Assert.Equal(first.Settings.PlannerPip, loaded.Settings.PlannerPip);
        Assert.Equal((first.Settings.Units, first.Settings.ExternalMapProvider, first.Settings.LightPollution, first.Settings.TerrainRelief),
            (loaded.Settings.Units, loaded.Settings.ExternalMapProvider, loaded.Settings.LightPollution, loaded.Settings.TerrainRelief));
        Assert.Equal(first.Locations, loaded.Locations);
        Assert.Equal((first.Session.Observer, first.Session.Instant, first.Session.TargetId, first.Session.Lens, first.Session.ObserverElevation),
            (loaded.Session.Observer, loaded.Session.Instant, loaded.Session.TargetId, loaded.Session.Lens, loaded.Session.ObserverElevation));
        Assert.Equal((first.MostRecentLocationId, first.LastCustomCoordinate), (loaded.MostRecentLocationId, loaded.LastCustomCoordinate));
    }
}
