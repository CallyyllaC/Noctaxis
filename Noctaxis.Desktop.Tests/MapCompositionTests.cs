using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Mapsui.Layers;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Mapping;

namespace Noctaxis.Desktop.Tests;

public sealed class MapCompositionTests
{
    private static readonly MapProviderMetadata Provider = new("Test");
    private static readonly BasemapDefinition Street = new(BasemapKind.Street, Provider);

    [Fact]
    public void BasemapSlotIsRequiredAndCannotBeRegisteredAsAnOverlay()
    {
        Assert.Throws<ArgumentNullException>(() => new MapComposition(null!, []));
        Assert.Throws<ArgumentException>(() => new MapComposition(Street,
            [new("second", MapLayerGroup.Basemap, 0, Provider)]));
        Assert.Single(new MapComposition(Street, []).Layers);
    }

    [Fact]
    public void GroupsCoexistAndOrderIsDeterministicWithStableIdTieBreaks()
    {
        MapLayerDefinition[] overlays =
        [
            new("pin", MapLayerGroup.Interaction, 0, Provider),
            new("weather", MapLayerGroup.Environmental, 1, Provider),
            new("terrain", MapLayerGroup.Environmental, 0, Provider),
            new("target", MapLayerGroup.Planning, 0, Provider),
            new("camera", MapLayerGroup.Planning, 0, Provider)
        ];
        var composition = new MapComposition(Street, overlays);
        Assert.Equal(new[] { "basemap", "terrain", "weather", "camera", "target", "pin" },
            composition.Layers.Select(x => x.Id));
        Assert.Equal(composition.Layers, new MapComposition(Street, overlays.Reverse()).Layers);
        Assert.Throws<ArgumentException>(() => new MapComposition(Street, [overlays[0], overlays[0]]));
    }

    [Fact]
    public void AttributionComesFromAllProvidersAndDeduplicatesSharedCredits()
    {
        var composition = new MapComposition(Street,
        [
            new("a", MapLayerGroup.Environmental, 0, new("A", "Credit A")),
            new("b", MapLayerGroup.Planning, 0, new("B", "Credit A")),
            new("c", MapLayerGroup.Environmental, 1, new("C", "Credit C"))
        ]);
        Assert.Equal("Credit A · Credit C", composition.AttributionText);
    }

    [Fact]
    public void ReplacementPreservesOverlaysAndDoesNotDuplicateNativeLayers()
    {
        var initial = new TestBasemap(2);
        var controller = new PlannerMapComposition(initial);
        var overlay = new MemoryLayer();
        controller.Map.Layers.Add(overlay);
        var replacement = new TestBasemap(1);
        controller.ReplaceBasemap(replacement);
        var composition = controller.Composition;
        controller.ReplaceBasemap(replacement);
        Assert.Same(composition, controller.Composition);
        Assert.Equal(1, replacement.Creations);
        Assert.Equal(2, controller.Map.Layers.Count);
        Assert.Same(replacement.Layers[0], controller.Map.Layers.First());
        Assert.Same(overlay, controller.Map.Layers.Last());
        Assert.Single(composition.Layers, x => x.Group == MapLayerGroup.Basemap);
        Assert.Equal(4, composition.Layers.Count(x => x.Group != MapLayerGroup.Basemap));
        Assert.Throws<ArgumentException>(() => controller.ReplaceBasemap(new TestBasemap(0)));
        Assert.Same(composition, controller.Composition);
        Assert.Equal(2, controller.Map.Layers.Count);
        controller.Map.Dispose();
    }

    [AvaloniaFact]
    public void ProductionUsesStreetOsmAndReattachmentDoesNotRegisterAgain()
    {
        var view = new NoctaxisMapView();
        var controller = view.CompositionForTesting;
        var composition = controller.Composition;
        Assert.Equal(BasemapKind.Street, composition.Basemap.Kind);
        Assert.Equal("OpenStreetMap", composition.Basemap.Provider.DisplayName);
        Assert.Equal(MapProvider.Attribution, composition.AttributionText);
        Assert.Single(controller.Map.Layers);
        var native = controller.Map.Layers.First();
        native.Enabled = false; // Offline lifecycle test, retaining the actual registration.
        var window = new Window { Content = view, Width = 400, Height = 300 };
        try
        {
            window.Show();
            for (var i = 0; i < 3; i++)
            {
                window.Content = null;
                Assert.False(view.AnimationTimerEnabled);
                window.Content = view;
                controller.Map.Navigator.CenterOnAndZoomTo(new(1000 * i, 2000), 200, 0);
                Assert.Same(composition, controller.Composition);
                Assert.Same(native, Assert.Single(controller.Map.Layers));
                Assert.False(view.AnimationTimerEnabled);
            }
        }
        finally { window.Close(); }
    }

    private sealed class TestBasemap(int count) : IMapsuiBasemap
    {
        public BasemapDefinition Definition => Street;
        public int Creations { get; private set; }
        public ILayer[] Layers { get; } = Enumerable.Range(0, count).Select(_ => (ILayer)new MemoryLayer()).ToArray();
        public IReadOnlyList<ILayer> CreateLayers() { Creations++; return Layers; }
    }
}
