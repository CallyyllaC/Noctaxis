using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Mapsui.Layers;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Mapping;
using Noctaxis.Desktop.Services;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The Planner map credits what it is drawing, in one strip of its own: a compact line built from the
/// active layers' metadata, and Map data &amp; licences behind an info button, with every link opened by
/// the application's one launcher. Mapsui's own attribution links are not drawn.
/// </summary>
public sealed class MapAttributionTests
{
    private static readonly string[] BasemapCredits = ["openstreetmap", "openfreemap", "openmaptiles"];

    private static string[] Ids(MapLayerStateController runtime) => runtime.ActiveAttributions.Select(credit => credit.Id).ToArray();

    [AvaloniaFact]
    public void TheBasemapAloneIsCreditedForItsDataItsTilesAndItsSchema()
    {
        var map = LightPollutionMapBinding.CreateComposition();
        Assert.Equal(BasemapCredits, Ids(map.Runtime));
        Assert.Equal("© OpenStreetMap contributors · OpenFreeMap · © OpenMapTiles", map.Runtime.AttributionText);
        map.Map.Dispose();
    }

    /// <summary>An overlay is credited while it is drawn, and only then: hidden, transparent or unavailable, it is not.</summary>
    [AvaloniaFact]
    public void TerrainAndLightPollutionAreCreditedOnlyWhileTheyAreDrawn()
    {
        var map = LightPollutionMapBinding.CreateComposition();
        var terrain = TerrainReliefMapBinding.LayerId;
        var light = LightPollutionMapBinding.LayerId;

        map.Runtime.SetAvailability(terrain, MapLayerAvailability.Available);
        Assert.Equal([.. BasemapCredits, "tilezen-terrarium"], Ids(map.Runtime));
        Assert.Equal("© OpenStreetMap contributors · OpenFreeMap · © OpenMapTiles · Terrain: Mapzen/Tilezen", map.Runtime.AttributionText);

        map.Runtime.SetAvailability(light, MapLayerAvailability.Available);
        // Light pollution is off until it is switched on; available alone, it is not drawn.
        Assert.Equal([.. BasemapCredits, "tilezen-terrarium"], Ids(map.Runtime));
        map.Runtime.SetVisibility(light, true);
        Assert.Equal([.. BasemapCredits, "tilezen-terrarium", Core.LightPollution.LorenzAtlas.ProviderId], Ids(map.Runtime));
        Assert.EndsWith(" · Terrain: Mapzen/Tilezen · Light pollution: David Lorenz", map.Runtime.AttributionText);

        map.Runtime.SetVisibility(terrain, false);
        Assert.DoesNotContain("tilezen-terrarium", Ids(map.Runtime));
        map.Runtime.SetOpacity(light, 0);
        Assert.Equal(BasemapCredits, Ids(map.Runtime));
        map.Runtime.SetOpacity(light, .5);
        map.Runtime.SetAvailability(light, MapLayerAvailability.Unavailable);
        Assert.Equal(BasemapCredits, Ids(map.Runtime));
        map.Map.Dispose();
    }

    /// <summary>A credit two providers owe is given once, where it is first owed.</summary>
    [AvaloniaFact]
    public void ASharedCreditIsGivenOnce()
    {
        var overlay = new MapLayerDefinition("osm-overlay", MapLayerGroup.Environmental, 0,
            new("OSM-derived overlay", Credits: [MapAttributions.OpenStreetMap, new("other", "Other", "Other credit")]));
        var map = new PlannerMapComposition(new OpenStreetMapBasemap(), [overlay]);
        Assert.Equal(["openstreetmap", "other"], Ids(map.Runtime));
        Assert.Equal("© OpenStreetMap contributors · Other", map.Runtime.AttributionText);
        Assert.Equal(["openstreetmap", "other"], map.Composition.Attributions.Select(credit => credit.Id));
        map.Map.Dispose();
    }

    /// <summary>
    /// Every credit is defined once, with its links, and the providers refer to it. The full wording the
    /// strip shortens is in the flyout, and each shortened label still names whoever is credited.
    /// </summary>
    [Fact]
    public void EachCreditIsDefinedOnceWithItsLinks()
    {
        Assert.Equal(MapProvider.Attribution, MapAttributions.OpenStreetMap.Label);
        Assert.Equal("https://www.openstreetmap.org/copyright", MapAttributions.OpenStreetMap.Url!.AbsoluteUri);
        Assert.Equal("ODbL", MapAttributions.OpenStreetMap.LicenceName);
        Assert.Equal("https://openfreemap.org/", MapAttributions.OpenFreeMap.Url!.AbsoluteUri);
        Assert.Equal(Core.LightPollution.LorenzAtlas.Name, MapAttributions.LightPollutionAtlas.Credit);
        Assert.Equal(Core.LightPollution.LorenzAtlas.InformationUrl, MapAttributions.LightPollutionAtlas.Url!.AbsoluteUri);
        Assert.Contains("David Lorenz", MapAttributions.LightPollutionAtlas.Label);
        Assert.Equal("Terrain: Mapzen/Tilezen Terrarium", MapAttributions.TilezenTerrarium.Credit);
        Assert.Contains("Mapzen/Tilezen", MapAttributions.TilezenTerrarium.Label);
        Assert.Same(MapAttributions.TilezenTerrarium, Assert.Single(TerrainReliefMapBinding.Definition.Provider.Credits!));
        Assert.Same(MapAttributions.LightPollutionAtlas, Assert.Single(LightPollutionMapBinding.Definition.Provider.Credits!));
        Assert.Equal([MapAttributions.OpenStreetMap, MapAttributions.OpenFreeMap, MapAttributions.OpenMapTiles],
            new Mapping.Vector.OpenFreeMapBasemap(new NoTiles()).Definition.Provider.Credits!);
    }

    /// <summary>
    /// Mapsui would draw each layer's own attribution as a link over the map, on top of the strip. Every
    /// native layer the Planner installs has that switched off, so there is one attribution surface.
    /// </summary>
    [AvaloniaFact]
    public void MapsuiDrawsNoAttributionOfItsOwn()
    {
        var map = LightPollutionMapBinding.CreateComposition();
        var overlay = new MemoryLayer { Name = "credited" };
        overlay.Attribution.Text = "Native credit";
        Assert.True(overlay.Attribution.Enabled);
        var credited = new PlannerMapComposition(additionalLayers: [new MapLayerDefinition("credited", MapLayerGroup.Environmental, 0, new("Credited"))]);
        credited.InstallNativeOverlay("credited", [overlay]);
        foreach (var composition in new[] { map, credited })
        {
            Assert.NotEmpty(composition.Map.Layers);
            Assert.All(composition.Map.Layers, layer => Assert.False(layer.Attribution.Enabled, $"{layer.Name} draws its own attribution"));
            Assert.DoesNotContain(composition.Map.GetWidgetsOfMapAndLayers(), widget => widget is Mapsui.Widgets.ButtonWidgets.HyperlinkWidget);
            composition.Map.Dispose();
        }
    }

    private sealed class NoTiles : Mapping.Vector.IVectorTileFetcher
    {
        public Task<byte[]?> FetchAsync(int zoom, int column, int row, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
    }
}

public sealed partial class MainViewModelTests
{
    /// <summary>
    /// The strip shows the active credits beside an info button: a full-size target with a name, that
    /// opens from a click, a tap or the keyboard. It opens Map data &amp; licences, listing exactly the
    /// active credits, and each link goes through the application's launcher.
    /// </summary>
    [AvaloniaFact]
    public async Task MapDataAndLicencesListsTheActiveCreditsAndOpensTheirLinksThroughTheLauncher()
    {
        var launcher = new RecordingLauncher();
        var catalogue = new OpenNgcTargetCatalogue();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, CreatePlannerStore(new AppSettings()), new FakeExporter(),
            externalUriLauncher: launcher, persistenceDelay: (_, _) => Task.CompletedTask);
        await vm.InitializeAsync();
        var search = new LocationSearchViewModel(new FakeLocationSearchProvider(), NullLogger<LocationSearchViewModel>.Instance);
        var window = new MainWindow(vm, new DesktopDialogService(search)) { Width = 1280, Height = 820 };
        try
        {
            var map = window.FindControl<PlannerMapView>("PlannerMap")!;
            foreach (var (id, layer) in map.CompositionForTesting.NativeLayerOrder)
                if (id is "basemap" or PlannerMapComposition.BasemapReferenceId) layer.Enabled = false; // Offline.
            TestMaps.OfflinePip(window);
            window.Show();
            ((TabControl)((Grid)window.Content!).Children[1]).SelectedIndex = 1;
            Settle(window);
            var runtime = map.CompositionForTesting.Runtime;

            // One strip, and it says what the runtime says is owed.
            Assert.Equal(runtime.AttributionText, map.AttributionForTesting);
            Assert.Single(map.GetVisualDescendants().OfType<Border>(), border => border.Name == "MapAttribution");
            var info = map.AttributionInfoForTesting;
            Assert.Equal("Map data and licences", Avalonia.Automation.AutomationProperties.GetName(info));
            Assert.True(info.IsEffectivelyVisible && info.Focusable);
            Assert.True(info.Bounds.Width >= 32 && info.Bounds.Height >= 28, $"the info button is {info.Bounds.Size}");
            var flyout = Assert.IsType<Flyout>(info.Flyout);
            Assert.False(flyout.IsOpen);

            // Clear of the instruments at the map's bottom-left: the Terrain frame and the pin's place.
            var strip = map.AttributionStripForTesting;
            var row = window.FindControl<StackPanel>("PlannerMapBottomLeft")!;
            Assert.True(strip.TranslatePoint(default, window)!.Value.X >= row.TranslatePoint(new Point(row.Bounds.Width, 0), window)!.Value.X,
                "the credits should leave the bottom-left instruments their room");

            // A click or tap opens Map data & licences, listing the active credits and nothing else.
            var centre = info.TranslatePoint(new Point(info.Bounds.Width / 2, info.Bounds.Height / 2), window)!.Value;
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
            Settle(window);
            Assert.True(flyout.IsOpen, "a click should open Map data & licences");
            var details = map.AttributionDetailsForTesting;
            Assert.Equal("Map data & licences", Assert.IsType<TextBlock>(details.Children[0]).Text);
            Assert.Equal(runtime.ActiveAttributions.Select(credit => credit.Id), details.Children.Skip(1).Select(entry => (string?)entry.Tag));
            Assert.Equal(runtime.ActiveAttributions.Select(credit => credit.Credit), details.Children.Skip(1)
                .Select(entry => ((TextBlock)((StackPanel)entry).Children[0]).Text));

            // Each link goes through the launcher, to the credit's own address.
            var links = details.GetVisualDescendants().OfType<Button>().ToArray();
            Assert.Contains(links, link => ((TextBlock)link.Content!).Text == "openstreetmap.org/copyright");
            Assert.Contains(links, link => ((TextBlock)link.Content!).Text == "Licence: ODbL");
            var osm = links.First(link => ((TextBlock)link.Content!).Text == "openstreetmap.org/copyright");
            osm.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(MapAttributions.OpenStreetMap.Url, Assert.Single(launcher.Opened));

            // A credit that becomes owed appears in both places at once; one no longer owed leaves both.
            runtime.SetAvailability(TerrainReliefMapBinding.LayerId, MapLayerAvailability.Available);
            Settle(window);
            Assert.Contains("Terrain: Mapzen/Tilezen", map.AttributionForTesting);
            Assert.Contains(details.Children, entry => (string?)entry.Tag == "tilezen-terrarium");
            runtime.SetVisibility(TerrainReliefMapBinding.LayerId, false);
            Settle(window);
            Assert.DoesNotContain("Terrain", map.AttributionForTesting);
            Assert.DoesNotContain(details.Children, entry => (string?)entry.Tag == "tilezen-terrarium");
            flyout.Hide();
            Settle(window);

            // So does the keyboard.
            Assert.True(info.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Settle(window);
            Assert.True(flyout.IsOpen, "Enter on the focused info button should open Map data & licences");
            flyout.Hide();
        }
        finally { Close(window); }
    }

    private sealed class RecordingLauncher : IExternalUriLauncher
    {
        public List<Uri> Opened { get; } = [];
        public bool TryOpen(Uri uri, out string? error)
        {
            Opened.Add(uri);
            error = null;
            return true;
        }
    }
}
