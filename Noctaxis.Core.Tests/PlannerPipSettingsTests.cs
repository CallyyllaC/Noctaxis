using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using NodaTime;

namespace Noctaxis.Core.Tests;

/// <summary>
/// The Picture-in-Picture map's settings: on and following the pin by default, saved like any map view,
/// and an older settings file — with the retired terrain minimap's context and a Terrain panel in its
/// dock layout — still loads whole, with the shipped Picture-in-Picture defaults.
/// </summary>
public sealed class PlannerPipSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "noctaxis-pip-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void ThePictureInPictureMapIsOnAndFollowsThePinByDefault()
    {
        var pip = new AppSettings().EffectivePlannerPip;
        Assert.True(pip.IsEnabled);
        Assert.True(pip.FollowPin);
        Assert.Null(pip.EffectiveCentre);
        Assert.Equal(PlannerPipSettings.DefaultResolutionMetresPerPixel, pip.EffectiveResolution);
        // The old minimap's view, on the normal layers: terrain relief, strongly, and no light pollution.
        Assert.Equal(new TerrainReliefPreferences(true, 1), pip.EffectiveTerrainRelief);
        Assert.False(pip.EffectiveLightPollution.IsVisible);
        // A terrain-first overview: the main map has the roads and names.
        Assert.False(pip.ShowRoadsAndLabels);
        // Its relief in the stronger, small-view tone.
        Assert.True(pip.EnhanceContrast);
    }

    [Fact]
    public void ASavedViewIsKeptOnlyWhenItIsWholeAndValid()
    {
        Assert.Equal(new GeoCoordinate(53, -4), new PlannerPipSettings(CentreLatitude: 53, CentreLongitude: -4).EffectiveCentre);
        Assert.Null(new PlannerPipSettings(CentreLatitude: 53).EffectiveCentre);
        Assert.Null(new PlannerPipSettings(CentreLatitude: 95, CentreLongitude: 0).EffectiveCentre);
        Assert.Null(new PlannerPipSettings(CentreLatitude: double.NaN, CentreLongitude: 0).EffectiveCentre);
        Assert.Equal(40, new PlannerPipSettings(ResolutionMetresPerPixel: 40).EffectiveResolution);
        Assert.Equal(PlannerPipSettings.DefaultResolutionMetresPerPixel, new PlannerPipSettings(ResolutionMetresPerPixel: -1).EffectiveResolution);
        Assert.Equal(PlannerPipSettings.MaximumResolutionMetresPerPixel,
            new PlannerPipSettings(ResolutionMetresPerPixel: 1e12).EffectiveResolution);
    }

    /// <summary>
    /// A file from before the Picture-in-Picture map: the minimap's context was a terrain grid's radius,
    /// not a map view's zoom, so it is ignored and the shipped defaults apply; the Terrain panel in the dock layout is simply not a panel any more, and
    /// nothing is quarantined or reset.
    /// </summary>
    [Fact]
    public async Task AnOlderSettingsFileLoadsWholeAndIgnoresTheMinimapContext()
    {
        var now = Instant.FromUtc(2026, 10, 2, 21, 0);
        var store = Store(now);
        await store.SaveAsync(new PersistedState(4, new AppSettings(TimeSnapMinutes: 10), [],
            PlanningSession.Default(now, "UTC"), null), CancellationToken.None);
        var path = Path.Combine(store.StorageDirectory, "state.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var settings = json["Settings"]!.AsObject();
        settings.Remove("PlannerPip");
        settings["TerrainMinimapContext"] = 1.8;
        settings["PlannerDock"] = JsonNode.Parse("""
            { "Panels": [ { "PanelId": "terrain", "Mode": "Docked", "Side": "Left", "DockedSize": 254 },
                          { "PanelId": "timeline", "Mode": "Docked", "Side": "Bottom", "DockedSize": 332 } ] }
            """);
        await File.WriteAllTextAsync(path, json.ToJsonString());

        var loaded = (await store.LoadAsync(CancellationToken.None)).Settings;
        Assert.Equal(10, loaded.TimeSnapMinutes);
        Assert.Equal(new PlannerPipSettings(), loaded.EffectivePlannerPip);
        Assert.False(loaded.LegacyData?.ContainsKey("TerrainMinimapContext") ?? false);
        Assert.Equal(332, loaded.EffectivePlannerDock.EffectivePanels.Single(panel => panel.PanelId == "timeline").DockedSize);
        Assert.Empty(Directory.GetFiles(store.StorageDirectory, "*corrupt*"));
    }

    [Fact]
    public async Task ThePictureInPictureViewIsSavedAndRestored()
    {
        var now = Instant.FromUtc(2026, 10, 2, 21, 0);
        var store = Store(now);
        var pip = new PlannerPipSettings(false, false, 52.5, -3.25, 120, new(true, .4), new(true, .3), true, EnhanceContrast: false);
        await store.SaveAsync(new PersistedState(4, new AppSettings(PlannerPip: pip), [],
            PlanningSession.Default(now, "UTC"), null), CancellationToken.None);
        Assert.Equal(pip, (await store.LoadAsync(CancellationToken.None)).Settings.PlannerPip);
    }

    private JsonUserDataStore Store(Instant now) => new(new Paths(_directory), NullLogger<JsonUserDataStore>.Instance, () => now);
    private sealed record Paths(string Path) : IUserDataPathProvider { public string GetApplicationDataDirectory() => Path; }
}
