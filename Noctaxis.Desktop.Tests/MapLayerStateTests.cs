using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Mapsui.Layers;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Mapping;

namespace Noctaxis.Desktop.Tests;

public sealed class MapLayerStateTests
{
    private const MapLayerCapabilities Both = MapLayerCapabilities.Visibility | MapLayerCapabilities.Opacity;
    private static readonly BasemapDefinition Street = new(BasemapKind.Street, new("Street", "Base credit"));
    private static MapLayerDefinition Layer(string id, int order = 0, string? credit = null,
        MapLayerCapabilities capabilities = Both, MapLayerCapabilities persisted = MapLayerCapabilities.None) =>
        new(id, MapLayerGroup.Environmental, order, new(id, credit), capabilities, persisted);
    private static MapLayerStateController Runtime(params MapLayerDefinition[] layers) => new(new(Street, layers));

    [Fact]
    public void StateIsIndependentAndSameValuesDoNotAllocateSnapshotsOrNotifyAdapters()
    {
        var runtime = Runtime(Layer("a"), Layer("b"));
        var composition = runtime.Composition;
        var definitions = composition.Layers;
        var adapter = new RecordingAdapter();
        var other = new RecordingAdapter();
        runtime.BindAdapter("a", adapter);
        runtime.BindAdapter("b", other);
        var state = runtime["a"];
        var credits = runtime.AttributionText;
        var notifications = 0;
        runtime.AttributionChanged += (_, _) => notifications++;
        for (var i = 0; i < 100; i++)
        {
            Assert.False(runtime.SetVisibility("a", true));
            Assert.False(runtime.SetOpacity("a", 1));
            Assert.False(runtime.SetAvailability("a", MapLayerAvailability.Available));
        }
        Assert.Same(state, runtime["a"]);
        Assert.Same(credits, runtime.AttributionText);
        Assert.Equal(0, notifications);
        Assert.Single(adapter.Applied);
        Assert.True(runtime.SetVisibility("a", false));
        Assert.False(adapter.Applied.Last().IsRenderable);
        Assert.Equal(2, adapter.Applied.Count);
        Assert.Single(other.Applied);
        Assert.True(state.IsVisible); // An old snapshot remains immutable.
        Assert.Same(composition, runtime.Composition);
        Assert.Same(definitions, composition.Layers);
    }

    [Fact]
    public void UnsupportedOperationsAndInvalidOpacityAreRejectedWithoutUpdates()
    {
        var runtime = Runtime(Layer("fixed", capabilities: MapLayerCapabilities.None), Layer("fade"));
        var initial = runtime["fixed"];
        Assert.Throws<InvalidOperationException>(() => runtime.SetVisibility("fixed", false));
        Assert.Throws<InvalidOperationException>(() => runtime.SetOpacity("fixed", .5));
        Assert.Throws<InvalidOperationException>(() => runtime.SetVisibility("basemap", false));
        Assert.Throws<InvalidOperationException>(() => runtime.SetOpacity("basemap", .5));
        Assert.Throws<InvalidOperationException>(() => runtime.SetActive("basemap", false));
        foreach (var opacity in new[] { -.1, 1.1, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => runtime.SetOpacity("fade", opacity));
        Assert.Throws<ArgumentOutOfRangeException>(() => runtime.SetAvailability("fade", (MapLayerAvailability)999));
        Assert.Same(initial, runtime["fixed"]);
        Assert.Equal(1, runtime["fade"].Opacity);
    }

    [Fact]
    public void AvailabilityTransitionsPreserveUserIntentAndDefinitions()
    {
        var runtime = Runtime(Layer("optional"));
        var composition = runtime.Composition;
        var adapter = new RecordingAdapter();
        runtime.BindAdapter("optional", adapter);
        runtime.SetOpacity("optional", .4);
        foreach (var availability in new[] { MapLayerAvailability.Unavailable, MapLayerAvailability.Available,
                     MapLayerAvailability.Loading, MapLayerAvailability.Available })
        {
            Assert.True(runtime.SetAvailability("optional", availability));
            Assert.Equal(availability == MapLayerAvailability.Available, adapter.Applied.Last().IsRenderable);
            Assert.True(runtime["optional"].IsVisible);
            Assert.Equal(.4, runtime["optional"].Opacity);
            Assert.Same(composition, runtime.Composition);
        }
        runtime.SetVisibility("optional", false);
        runtime.SetAvailability("optional", MapLayerAvailability.Loading);
        runtime.SetAvailability("optional", MapLayerAvailability.Available);
        Assert.False(adapter.Applied.Last().IsRenderable);
    }

    [Fact]
    public void ActiveAttributionIsOrderedDeduplicatedAndCached()
    {
        var runtime = Runtime(Layer("last", 3, "C"), Layer("first", 0, "A"),
            Layer("duplicate", 1, "A"), Layer("middle", 2, "B"));
        Assert.Equal("Base credit · A · B · C", runtime.AttributionText);
        var changes = 0;
        runtime.AttributionChanged += (_, _) => changes++;
        runtime.SetVisibility("first", false);
        Assert.Equal(0, changes); // Shared credit is still required.
        runtime.SetVisibility("duplicate", false);
        Assert.Equal("Base credit · B · C", runtime.AttributionText);
        runtime.SetActive("middle", false);
        Assert.Equal("Base credit · C", runtime.AttributionText);
        runtime.SetOpacity("last", 0);
        Assert.Equal("Base credit", runtime.AttributionText);
        runtime.SetOpacity("last", .5);
        runtime.SetAvailability("last", MapLayerAvailability.Unavailable);
        Assert.Equal("Base credit", runtime.AttributionText);
        runtime.SetAvailability("last", MapLayerAvailability.Available);
        Assert.Equal("Base credit · C", runtime.AttributionText);
        runtime.SetAvailability("basemap", MapLayerAvailability.Unavailable);
        Assert.Equal("Base credit · C", runtime.AttributionText); // Selected basemap always credited.
        Assert.Equal(6, changes);
    }

    [Fact]
    public void NativeVisibilityOpacityAndAvailabilityChangeOnlyTheRegisteredLayers()
    {
        var runtime = Runtime(Layer("a"), Layer("b"));
        using var first = new MemoryLayer();
        using var second = new MemoryLayer();
        var firstRedraws = 0;
        var secondRedraws = 0;
        runtime.BindAdapter("a", new MapsuiLayerStateAdapter([first], () => firstRedraws++));
        runtime.BindAdapter("b", new MapsuiLayerStateAdapter([second], () => secondRedraws++));
        var nativeNotifications = 0;
        first.PropertyChanged += (_, _) => nativeNotifications++;
        Assert.False(runtime.SetVisibility("a", true));
        Assert.False(runtime.SetOpacity("a", 1));
        Assert.Equal(0, nativeNotifications);
        Assert.Equal(0, firstRedraws);
        runtime.SetOpacity("a", .25);
        Assert.Equal(.25, first.Opacity);
        Assert.Equal(1, second.Opacity);
        Assert.Equal(1, firstRedraws);
        Assert.Equal(0, secondRedraws);
        runtime.SetVisibility("a", false);
        Assert.False(first.Enabled);
        Assert.True(second.Enabled);
        runtime.SetVisibility("a", true);
        runtime.SetAvailability("a", MapLayerAvailability.Loading);
        Assert.False(first.Enabled);
        runtime.SetAvailability("a", MapLayerAvailability.Available);
        Assert.True(first.Enabled);
        Assert.Equal(.25, first.Opacity);
    }

    [Fact]
    public void AdapterBindingIsIdempotentAndRejectsDuplicateOwnership()
    {
        var runtime = Runtime(Layer("a"));
        var adapter = new RecordingAdapter();
        runtime.BindAdapter("a", adapter);
        runtime.BindAdapter("a", adapter);
        Assert.Single(adapter.Applied);
        Assert.Throws<InvalidOperationException>(() => runtime.BindAdapter("a", new RecordingAdapter()));
    }

    [Fact]
    public void NativeOverlayInstallationUsesSemanticOrderAndStateDoesNotReplaceLayers()
    {
        var controller = new PlannerMapComposition(new FakeBasemap("base"), [Layer("later", 5), Layer("earlier", -1)]);
        using var map = controller.Map;
        var later = new MemoryLayer();
        var earlier = new MemoryLayer();
        controller.InstallNativeOverlay("later", [later]);
        controller.Runtime.SetAvailability("earlier", MapLayerAvailability.Unavailable);
        controller.InstallNativeOverlay("earlier", [earlier]);
        var native = map.Layers.ToArray();
        Assert.Same(earlier, native[1]);
        Assert.Same(later, native[2]);
        Assert.False(earlier.Enabled);
        var redraws = 0;
        map.RefreshGraphicsRequest += (_, _) => redraws++;
        Assert.False(controller.Runtime.SetOpacity("later", 1));
        Assert.Equal(0, redraws);
        controller.Runtime.SetOpacity("later", .6);
        Assert.True(redraws > 0); // Verify native property changes actually request a map redraw.
        var afterOpacity = redraws;
        controller.Runtime.SetOpacity("later", .6);
        Assert.Equal(afterOpacity, redraws);
        controller.Runtime.SetVisibility("earlier", false);
        Assert.Equal(native, map.Layers.ToArray());
        Assert.Throws<InvalidOperationException>(() => controller.InstallNativeOverlay("later", [later]));
        Assert.Equal(native, map.Layers.ToArray());
    }

    [Fact]
    public void BasemapActivationIsExclusiveAndFailureLeavesNativeRuntimeAndAttributionIntact()
    {
        var original = new FakeBasemap("original");
        var controller = new PlannerMapComposition(original);
        using var map = controller.Map;
        var composition = controller.Composition;
        var overlayState = controller.Runtime["observer-pin"];
        var replacement = new FakeBasemap("replacement", 2);
        controller.ReplaceBasemap(replacement);
        Assert.Equal("replacement", controller.Runtime.AttributionText);
        Assert.Equal(2, map.Layers.Count);
        Assert.Same(replacement.Definition, controller.Runtime.ActiveBasemap);
        Assert.Same(composition, controller.Composition);
        Assert.Same(overlayState, controller.Runtime["observer-pin"]);
        var activeState = controller.Runtime["basemap"];
        controller.ReplaceBasemap(replacement);
        Assert.Equal(1, replacement.Creations);
        Assert.Same(activeState, controller.Runtime["basemap"]);
        var native = map.Layers.ToArray();
        Assert.Throws<InvalidOperationException>(() => controller.ReplaceBasemap(new FakeBasemap("failed", fail: true)));
        Assert.Throws<ArgumentException>(() => controller.ReplaceBasemap(new FakeBasemap("empty", 0)));
        Assert.Equal(native, map.Layers.ToArray());
        Assert.Same(activeState, controller.Runtime["basemap"]);
        Assert.Equal("replacement", controller.Runtime.AttributionText);
        Assert.Same(replacement.Definition, controller.Runtime.ActiveBasemap);
        Assert.Single(composition.Layers, x => x.Group == MapLayerGroup.Basemap);
        Assert.True(activeState.IsActive);
    }

    [Fact]
    public void PreferencesRoundTripOnlyOptedInUserValuesNotAvailabilityOrActiveState()
    {
        MapLayerDefinition[] definitions = [Layer("user", persisted: Both), Layer("temporary"),
            Layer("visibility-only", persisted: MapLayerCapabilities.Visibility)];
        var runtime = Runtime(definitions);
        runtime.SetVisibility("user", false);
        runtime.SetOpacity("user", .35);
        runtime.SetAvailability("user", MapLayerAvailability.Unavailable);
        runtime.SetActive("user", false);
        runtime.SetOpacity("temporary", .2);
        runtime.SetOpacity("visibility-only", .7);
        var json = JsonSerializer.Serialize(MapLayerPreferences.Capture(runtime));
        Assert.DoesNotContain("Availability", json);
        Assert.DoesNotContain("IsActive", json);
        var stored = JsonSerializer.Deserialize<MapLayerPreference[]>(json)!;
        Assert.Equal(2, stored.Length);
        Assert.Null(Assert.Single(stored, x => x.LayerId == "visibility-only").Opacity);
        var restored = Runtime(definitions);
        MapLayerPreferences.Restore(restored, stored);
        Assert.False(restored["user"].IsVisible);
        Assert.Equal(.35, restored["user"].Opacity);
        Assert.Equal(MapLayerAvailability.Available, restored["user"].Availability);
        Assert.True(restored["user"].IsActive);
        Assert.Equal(1, restored["temporary"].Opacity);
        Assert.Equal(1, restored["visibility-only"].Opacity);
    }

    [Fact]
    public void PreferenceRestoreIgnoresUnknownUnsupportedAndMalformedValues()
    {
        var runtime = Runtime(Layer("user", persisted: Both), Layer("internal"));
        runtime.SetAvailability("user", MapLayerAvailability.Loading);
        MapLayerPreferences.Restore(runtime,
        [new("unknown", false, .2), new("internal", false, .2), new("user", false, double.NaN), new(null!), null!]);
        Assert.True(runtime["internal"].IsVisible);
        Assert.False(runtime["user"].IsVisible);
        Assert.Equal(1, runtime["user"].Opacity);
        Assert.Equal(MapLayerAvailability.Loading, runtime["user"].Availability);
        Assert.Throws<ArgumentException>(() => Runtime(Layer("bad", capabilities: MapLayerCapabilities.None, persisted: Both)));
        var production = new PlannerMapComposition(new FakeBasemap("base"));
        Assert.Empty(MapLayerPreferences.Capture(production.Runtime));
        production.Map.Dispose();
    }

    [AvaloniaFact]
    public void ExistingSettingsAndRuntimeShareVisibilityWhileAvailabilityKeepsPreferences()
    {
        var view = new NoctaxisMapView();
        var runtime = view.CompositionForTesting.Runtime;
        view.ShowCameraOverlay = true;
        view.ShowCelestialOverlays = true;
        Assert.True(runtime["camera-framing"].IsVisible);
        Assert.True(view.CameraLayerVisibleForTesting);
        Assert.True(view.CelestialLayerVisibleForTesting);
        runtime.SetVisibility("celestial-rays", false);
        Assert.True(view.ShowCelestialOverlays); // Readiness binding is not a user preference.
        Assert.False(view.CelestialLayerVisibleForTesting);
        view.ShowCelestialOverlays = false;
        runtime.SetVisibility("celestial-rays", true);
        Assert.False(view.CelestialLayerVisibleForTesting); // Visibility cannot bypass readiness.
        view.ShowCelestialOverlays = true;
        Assert.True(view.CelestialLayerVisibleForTesting);
        runtime.SetAvailability("camera-framing", MapLayerAvailability.Loading);
        Assert.True(view.ShowCameraOverlay);
        Assert.False(view.CameraLayerVisibleForTesting);
        Assert.False(view.EnvironmentalLayerVisibleForTesting);
        runtime.SetAvailability("camera-framing", MapLayerAvailability.Available);
        Assert.True(view.CameraLayerVisibleForTesting);
        view.FramingSettings = view.FramingSettings with { ShadingOpacityPercent = 40 };
        Assert.True(view.EnvironmentalLayerVisibleForTesting);
        runtime.SetAvailability("environmental-shading", MapLayerAvailability.Unavailable);
        Assert.False(view.EnvironmentalLayerVisibleForTesting);
        Assert.True(view.CameraLayerVisibleForTesting);
        runtime.SetAvailability("environmental-shading", MapLayerAvailability.Available);
        Assert.True(view.EnvironmentalLayerVisibleForTesting);
        view.FramingSettings = view.FramingSettings with { ShadingOpacityPercent = 0 };
        Assert.False(view.EnvironmentalLayerVisibleForTesting);
        Assert.True(view.CameraLayerVisibleForTesting);
        Assert.Throws<InvalidOperationException>(() => runtime.SetOpacity("observer-pin", .5));
        view.CompositionForTesting.Map.Dispose();
    }

    [AvaloniaFact]
    public void OptionalNativeLayerUpdatesLiveAttributionWithoutPlannerSpecificHandlers()
    {
        var controller = new PlannerMapComposition(new FakeBasemap("base"), [Layer("optional", credit: "Optional credit")]);
        var native = new MemoryLayer();
        controller.InstallNativeOverlay("optional", [native]);
        var view = new NoctaxisMapView(controller);
        var window = new Window { Content = view, Width = 500, Height = 300 };
        try
        {
            window.Show();
            Assert.Equal("base · Optional credit", view.AttributionForTesting);
            controller.Runtime.SetVisibility("optional", false);
            Assert.False(native.Enabled);
            Assert.Equal("base", view.AttributionForTesting);
            controller.Runtime.SetVisibility("optional", true);
            controller.Runtime.SetOpacity("optional", .5);
            Assert.True(native.Enabled);
            Assert.Equal(.5, native.Opacity);
            Assert.Equal("base · Optional credit", view.AttributionForTesting);
            controller.Runtime.SetAvailability("optional", MapLayerAvailability.Unavailable);
            Assert.Equal("base", view.AttributionForTesting);
            Assert.False(native.Enabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ReattachmentHasOneAttributionSubscriptionAndNavigationKeepsAllRegistrations()
    {
        var view = new NoctaxisMapView();
        var controller = view.CompositionForTesting;
        controller.ReplaceBasemap(new FakeBasemap("offline"));
        var runtime = controller.Runtime;
        var composition = controller.Composition;
        var states = composition.Layers.Select(x => runtime[x.Id]).ToArray();
        var window = new Window { Content = view, Width = 640, Height = 480 };
        try
        {
            window.Show();
            for (var cycle = 0; cycle < 3; cycle++)
            {
                Assert.Equal(1, AttributionSubscriptions(runtime));
                Assert.Equal(runtime.AttributionText, view.AttributionForTesting);
                var native = controller.Map.Layers.ToArray();
                for (var i = 0; i < 20; i++)
                    controller.Map.Navigator.CenterOnAndZoomTo(new(i * 50, i * 70), 250 + i, 0);
                Assert.Same(composition, controller.Composition);
                Assert.Equal(native, controller.Map.Layers.ToArray());
                for (var i = 0; i < states.Length; i++) Assert.Same(states[i], runtime[composition.Layers[i].Id]);
                Assert.False(view.AnimationTimerEnabled);
                window.Content = null;
                Assert.Equal(0, AttributionSubscriptions(runtime));
                var previousText = view.AttributionForTesting;
                controller.ReplaceBasemap(new FakeBasemap($"offline {cycle}"));
                Assert.Equal(previousText, view.AttributionForTesting);
                window.Content = view;
                Assert.Equal(runtime.AttributionText, view.AttributionForTesting);
                states = composition.Layers.Select(x => runtime[x.Id]).ToArray();
            }
            controller.ReplaceBasemap(new FakeBasemap("live change"));
            Assert.Equal("live change", view.AttributionForTesting);
        }
        finally { window.Close(); }
        Assert.Equal(0, AttributionSubscriptions(runtime));
    }

    private static int AttributionSubscriptions(MapLayerStateController runtime) =>
        ((Delegate?)typeof(MapLayerStateController).GetField("AttributionChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(runtime))?.GetInvocationList().Length ?? 0;

    private sealed class RecordingAdapter : IMapLayerStateAdapter
    {
        public List<MapLayerState> Applied { get; } = [];
        public void Apply(MapLayerState state) => Applied.Add(state);
    }

    private sealed class FakeBasemap(string credit, int count = 1, bool fail = false) : IMapsuiBasemap
    {
        public BasemapDefinition Definition { get; } = new(BasemapKind.Street, new(credit, credit));
        public int Creations { get; private set; }
        public IReadOnlyList<ILayer> CreateLayers()
        {
            Creations++;
            if (fail) throw new InvalidOperationException("Provider creation failed.");
            return Enumerable.Range(0, count).Select(_ => (ILayer)new MemoryLayer()).ToArray();
        }
    }
}
