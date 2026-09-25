using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Services;

namespace Noctaxis.Desktop.Tests;

public sealed class ExternalMapTests
{
    [Theory]
    [InlineData(ExternalMapProvider.OpenStreetMap, "https://www.openstreetmap.org/?mlat=-33.5&mlon=-70.25#map=16/-33.5/-70.25")]
    [InlineData(ExternalMapProvider.GoogleMaps, "https://www.google.com/maps/search/?api=1&query=-33.5%2C-70.25")]
    [InlineData(ExternalMapProvider.Mapillary, "https://www.mapillary.com/app/?lat=-33.5&lng=-70.25&z=16&focus=map")]
    public void ProviderUrlsUseInvariantCoordinates(ExternalMapProvider provider, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(expected, ExternalMapService.CreateUri(-33.5, -70.25, provider)!.AbsoluteUri);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(null, 0d)]
    [InlineData(0d, null)]
    [InlineData(double.NaN, 0d)]
    [InlineData(0d, double.PositiveInfinity)]
    [InlineData(-91d, 0d)]
    [InlineData(91d, 0d)]
    [InlineData(0d, -181d)]
    [InlineData(0d, 181d)]
    public void InvalidCoordinatesNeverReachLauncher(double? latitude, double? longitude)
    {
        var launcher = new RejectUnexpectedLaunch();
        Assert.False(new ExternalMapService(launcher).TryOpen(latitude, longitude, ExternalMapProvider.OpenStreetMap, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void BoundsAndUnknownProvidersAreHandled()
    {
        Assert.NotNull(ExternalMapService.CreateUri(-90, 180, ExternalMapProvider.OpenStreetMap));
        Assert.NotNull(ExternalMapService.CreateUri(90, -180, ExternalMapProvider.OpenStreetMap));
        Assert.Null(ExternalMapService.CreateUri(0, 0, (ExternalMapProvider)99));
    }

    [Fact]
    public void PlannerAndSettingsMarkupWireOneActionAndProviderDropdown()
    {
        var document = System.Xml.Linq.XDocument.Load(TestPaths.MainWindowMarkup);
        var action = Assert.Single(document.Descendants(), e => e.Name.LocalName == "Button" && e.Attribute("Content")?.Value == "Open in Maps");
        Assert.Equal("{Binding OpenInMapsCommand}", action.Attribute("Command")?.Value);
        Assert.Contains(action.Ancestors(), e => e.Attribute("Header")?.Value == "Planner");
        var selector = Assert.Single(document.Descendants(), e => e.Name.LocalName == "ComboBox" && e.Attribute("ItemsSource")?.Value == "{Binding ExternalMapProviders}");
        Assert.Equal("{Binding SettingsExternalMapProvider, Mode=TwoWay}", selector.Attribute("SelectedItem")?.Value);
        Assert.Contains(selector.Ancestors(), e => e.Attribute("Header")?.Value == "General");
    }

    private sealed class RejectUnexpectedLaunch : IExternalUriLauncher
    {
        public bool TryOpen(Uri uri, out string? error) => throw new InvalidOperationException("Unexpected browser launch");
    }

    private sealed record Paths(string Directory) : IUserDataPathProvider
    {
        public string GetApplicationDataDirectory() => Directory;
    }

    [Fact]
    public async Task RealStoreRoundTripsEveryProviderAndDefaultsMissingSetting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "noctaxis-external-maps-" + Guid.NewGuid());
        try
        {
            var store = new JsonUserDataStore(new Paths(directory), NullLogger<JsonUserDataStore>.Instance);
            var state = await store.LoadAsync(CancellationToken.None);
            Assert.Equal(ExternalMapProvider.OpenStreetMap, state.Settings.ExternalMapProvider);
            foreach (var provider in Enum.GetValues<ExternalMapProvider>())
            {
                await store.SaveAsync(state with { Settings = state.Settings with { ExternalMapProvider = provider } }, CancellationToken.None);
                var reloaded = await new JsonUserDataStore(new Paths(directory), NullLogger<JsonUserDataStore>.Instance).LoadAsync(CancellationToken.None);
                Assert.Equal(provider, reloaded.Settings.ExternalMapProvider);
            }
            var path = Path.Combine(directory, "state.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            foreach (var legacy in new System.Text.Json.Nodes.JsonNode[]
            {
                System.Text.Json.Nodes.JsonValue.Create("GoogleStreetView")!,
                System.Text.Json.Nodes.JsonValue.Create(2)!
            })
            {
                json["Settings"]!["ExternalMapProvider"] = legacy;
                json["Settings"]!["Units"] = "Imperial";
                await File.WriteAllTextAsync(path, json.ToJsonString());
                var migrated = await store.LoadAsync(CancellationToken.None);
                Assert.Equal(ExternalMapProvider.GoogleMaps, migrated.Settings.ExternalMapProvider);
                Assert.Equal("Imperial", migrated.Settings.Units);
                Assert.True(File.Exists(path));
            }
            json["Settings"]!.AsObject().Remove("ExternalMapProvider");
            await File.WriteAllTextAsync(path, json.ToJsonString());
            Assert.Equal(ExternalMapProvider.OpenStreetMap, (await store.LoadAsync(CancellationToken.None)).Settings.ExternalMapProvider);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

public sealed partial class MainViewModelTests
{
    [Fact]
    [CoversSettingsInput("SettingsExternalMapProvider")]
    public async Task ExternalMapsUsesSavedProviderAndCurrentPinWithSaveResetSemantics()
    {
        var launcher = new FakeUriLauncher(true, null);
        var catalogue = new OpenNgcTargetCatalogue();
        var store = CreateSupporterStore(new AppSettings());
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter(), externalUriLauncher: launcher);
        await vm.InitializeAsync();
        Assert.Equal(new[] { "OpenStreetMap", "Google Maps", "Mapillary" }, vm.ExternalMapProviders.Select(option => option.Label));
        foreach (var option in vm.ExternalMapProviders)
        {
            vm.SettingsExternalMapProvider = option;
            vm.ResetSettingsEditorCommand.Execute(null);
            Assert.Equal(vm.Settings.ExternalMapProvider, vm.SettingsExternalMapProvider!.Provider);
            vm.SettingsExternalMapProvider = option;
            await vm.SaveSettingsCommand.ExecuteAsync(null);
            Assert.Equal(option.Provider, store.State.Settings.ExternalMapProvider);
            vm.OpenInMapsCommand.Execute(null);
            Assert.Equal(ExternalMapService.CreateUri(vm.Latitude, vm.Longitude, option.Provider), launcher.LastUri);
        }
        var changes = 0;
        vm.OpenInMapsCommand.CanExecuteChanged += (_, _) => changes++;
        vm.Latitude = double.NaN;
        Assert.False(vm.OpenInMapsCommand.CanExecute(null));
        var last = launcher.LastUri;
        vm.OpenInMapsCommand.Execute(null);
        Assert.Equal(last, launcher.LastUri);
        vm.Latitude = -33.5;
        vm.Longitude = -70.25;
        Assert.True(vm.OpenInMapsCommand.CanExecute(null));
        Assert.True(changes >= 3);
        vm.OpenInMapsCommand.Execute(null);
        Assert.Equal(ExternalMapService.CreateUri(-33.5, -70.25, ExternalMapProvider.Mapillary), launcher.LastUri);
    }

    [Fact]
    public void ExternalMapLaunchFailureBecomesStatusMessage()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, CreateSupporterStore(new AppSettings()),
            new FakeExporter(), externalUriLauncher: new FakeUriLauncher(false, "browser unavailable"));
        vm.OpenInMapsCommand.Execute(null);
        Assert.Equal("Could not open Maps: browser unavailable", vm.StatusMessage);
    }
}
