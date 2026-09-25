using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Themes;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    [Fact]
    [CoversSettingsInput("SelectedTheme")]
    [CoversSettingsInput("SelectedAppearanceMode")]
    [CoversSettingsInput("SelectedColourVisionValue")]
    [CoversSettingsInput("TextSizePercent")]
    public async Task AppearanceChangesPreservePlanningAndExplicitColours()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var store = new FakeStore(new(1, new AppSettings(CameraFraming: new(TerrainObstructionColour: "#123456")), [],
            PlanningSession.Default(Instant.FromUtc(2026, 1, 1, 12, 0), "UTC"), null));
        var vm = CreateViewModel(planning, catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        var session = vm.Session;
        var settings = vm.Settings;
        var snapshot = vm.Snapshot;
        var calls = (planning.SnapshotCalculations, planning.EnvironmentRequests, planning.ForcedRefreshes);
        Assert.Equal(BuiltInPalettes.Definitions.Select(theme => theme.Id), vm.ThemeOptions.Select(t => t.Id));
        Assert.All(vm.ThemeOptions.Where(option => option.IsSupporter), option => Assert.False(option.IsAvailable));
        foreach (var family in new[] { "builtin.noctaxis", "builtin.highcontrast" })
        foreach (var appearance in Enum.GetValues<AppearanceMode>())
        foreach (var mode in Enum.GetValues<ColourVisionMode>())
        {
            await vm.ApplyAppearanceAsync(new(family, mode, 2, appearance));
            Assert.Equal(settings, vm.Settings with { Appearance = settings.Appearance });
            Assert.Same(session, vm.Session);
            Assert.Same(snapshot, vm.Snapshot);
            Assert.Equal(calls, (planning.SnapshotCalculations, planning.EnvironmentRequests, planning.ForcedRefreshes));
            Assert.Equal("#123456", vm.Settings.EffectiveCameraFraming.TerrainObstructionColour);
            Assert.Equal(new AppearancePreferences(family, mode, 2, appearance), store.State.Settings.Appearance);
        }
    }
}

public sealed class AppearancePersistenceTests
{
    private sealed record Paths(string Directory) : IUserDataPathProvider { public string GetApplicationDataDirectory() => Directory; }
    [Theory]
    [InlineData("builtin.dark", "builtin.noctaxis", AppearanceMode.Dark)]
    [InlineData("builtin.light", "builtin.noctaxis", AppearanceMode.Light)]
    [InlineData("builtin.system", "builtin.noctaxis", AppearanceMode.System)]
    [InlineData("builtin.highcontrast", "builtin.highcontrast", AppearanceMode.Dark)]
    public async Task RealStoreMigratesLegacyAppearanceWithoutLosingOtherSettings(string legacy, string family, AppearanceMode mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "noctaxis-migrate-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var store = new JsonUserDataStore(new Paths(directory), NullLogger<JsonUserDataStore>.Instance);
            var state = await store.LoadAsync(CancellationToken.None);
            state = state with { Settings = state.Settings with { CameraFraming = new(TerrainObstructionColour: "#123456") } };
            await store.SaveAsync(state, CancellationToken.None);
            var path = Path.Combine(directory, "state.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            json["Settings"]!["Appearance"] = new System.Text.Json.Nodes.JsonObject
            { ["SelectedThemeId"] = legacy, ["ColourVisionMode"] = "Tritanopia", ["TextScale"] = 1.75 };
            await File.WriteAllTextAsync(path, json.ToJsonString());
            var loaded = await store.LoadAsync(CancellationToken.None);
            Assert.Equal(new(family, ColourVisionMode.Tritanopia, 1.75, mode), loaded.Settings.Appearance);
            Assert.Equal("#123456", loaded.Settings.EffectiveCameraFraming.TerrainObstructionColour);
            await store.SaveAsync(loaded, CancellationToken.None);
            Assert.DoesNotContain("SelectedThemeId", await File.ReadAllTextAsync(path));
            Assert.Equal(loaded.Settings.Appearance, (await store.LoadAsync(CancellationToken.None)).Settings.Appearance);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task RealStoreRoundTripsIndependentPreferencesAndLoadsLegacySettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "noctaxis-appearance-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var store = new JsonUserDataStore(new Paths(directory), NullLogger<JsonUserDataStore>.Instance);
            var state = await store.LoadAsync(CancellationToken.None);
            var preferences = new AppearancePreferences("builtin.highcontrast", ColourVisionMode.Deuteranopia, 1.5);
            await store.SaveAsync(state with { Settings = state.Settings with { Appearance = preferences } }, CancellationToken.None);
            Assert.Equal(preferences, (await store.LoadAsync(CancellationToken.None)).Settings.Appearance);
            // Round-trip the existing shape without an appearance value.
            await store.SaveAsync(state with { Settings = new AppSettings() }, CancellationToken.None);
            var path = Path.Combine(directory, "state.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            json["Settings"]!.AsObject().Remove("Appearance");
            await File.WriteAllTextAsync(path, json.ToJsonString());
            Assert.Equal(new AppearancePreferences(), (await store.LoadAsync(CancellationToken.None)).Settings.Appearance ?? new());
        }
        finally { Directory.Delete(directory, true); }
    }
}
