using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.LightPollution;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Mapping;
using Noctaxis.Desktop.ViewModels;

namespace Noctaxis.Desktop.Tests;

public sealed class PlannerLayersTests
{
    [Fact]
    public void SettingsKeepPaletteAndDataManagementWhilePlannerOwnsLayerControls()
    {
        var document = System.Xml.Linq.XDocument.Load(TestPaths.MainWindowMarkup);
        var data = document.Descendants().Single(x => x.Name.LocalName == "TabItem" && (string?)x.Attribute("Header") == "Data");
        Assert.DoesNotContain("SettingsShowLightPollution", data.ToString());
        Assert.DoesNotContain("SettingsLightPollutionOpacityPercent", data.ToString());
        Assert.DoesNotContain("apply visibility and opacity", data.ToString());
        Assert.Contains("SettingsLightPollutionColorMap", data.ToString());
        Assert.Contains("LightPollutionColorMapDescription", data.ToString());
        foreach (var command in new[] { "OpenLightPollutionSourceCommand", "InstallLightPollutionCommand", "RemoveLightPollutionCommand" })
            Assert.Contains(command, data.ToString());
        var layers = document.Descendants().Single(x => x.Name.LocalName == "PlannerLayersControl");
        Assert.Equal("Right", (string?)layers.Parent!.Attribute("HorizontalAlignment"));
        Assert.Equal("Top", (string?)layers.Parent.Attribute("VerticalAlignment"));
    }
    [Fact]
    public async Task DragEditsAreLiveButOnlyCommitsWriteAndSameValuesAreNoOps()
    {
        var composition = LightPollutionMapBinding.CreateComposition();
        try
        {
            var vm = new PlannerLayersViewModel(composition.Runtime);
            var writes = 0; var edits = 0;
            vm.PersistAsync = () => { writes++; return Task.CompletedTask; };
            vm.PreferencesEdited += (_, _) => edits++;
            vm.ShowLightPollution = vm.ShowLightPollution;
            vm.LightPollutionOpacityPercent = vm.LightPollutionOpacityPercent;
            await vm.CommitAsync(); Assert.Equal(0, writes); Assert.Equal(0, edits);
            for (var i = 1; i <= 20; i++) vm.LightPollutionOpacityPercent = i;
            Assert.Equal(.2, composition.Runtime["light-pollution"].Opacity);
            Assert.Equal(0, writes); Assert.Equal(20, edits);
            await vm.CommitAsync(); await vm.CommitAsync(); Assert.Equal(1, writes);
        }
        finally { composition.Map.Dispose(); }
    }

    [Fact]
    public async Task PendingCommitIsOwnedAndLaterEditsAreSavedWithoutDuplicateWriters()
    {
        var composition = LightPollutionMapBinding.CreateComposition();
        try
        {
            var vm = new PlannerLayersViewModel(composition.Runtime);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var saved = new List<double>();
            vm.PersistAsync = async () => { saved.Add(vm.LightPollutionOpacityPercent); if (saved.Count == 1) await gate.Task; };
            vm.LightPollutionOpacityPercent = 25;
            var first = vm.CommitAsync();
            vm.LightPollutionOpacityPercent = 75;
            Assert.Same(first, vm.CommitAsync()); Assert.False(first.IsCompleted);
            gate.SetResult(); await first;
            Assert.Equal(new double[] { 25, 75 }, saved);
            await vm.CommitAsync(); Assert.Equal(2, saved.Count);
            var nextGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.PersistAsync = async () => { saved.Add(vm.LightPollutionOpacityPercent); await nextGate.Task; };
            vm.LightPollutionOpacityPercent = 76;
            var next = vm.CommitAsync();
            vm.LightPollutionOpacityPercent = 77; // A new drag has not committed yet.
            nextGate.SetResult(); await next;
            Assert.Equal(3, saved.Count);
            await vm.CommitAsync(); Assert.Equal(4, saved.Count);
            vm.PersistAsync = () => throw new IOException("failed save");
            vm.LightPollutionOpacityPercent = 80;
            await vm.CommitSafelyAsync(); Assert.True(vm.HasPersistenceError);
            vm.PersistAsync = () => Task.CompletedTask;
            await vm.CommitSafelyAsync(); Assert.False(vm.HasPersistenceError);
        }
        finally { composition.Map.Dispose(); }
    }

    [AvaloniaFact]
    public async Task FlyoutReattachesReflectsRestoredStateAndMutatesExistingNativeLayer()
    {
        await using var paths = new AtlasTestData();
        using var http = new HttpClient(new LightPollutionInstallationTests.Handler((_, _) => throw new Exception("No network")));
        var store = new LorenzInstallation(paths, http);
        await paths.SeedAsync(store);
        var map = new PlannerMapView { LightPollutionPreferences = new(true, .35, "cividis") };
        map.ConfigureLightPollution(store);
        var composition = map.CompositionForTesting;
        composition.Map.Layers.First().Enabled = false;
        var native = composition.Map.Layers.Last();
        var originalComposition = composition.Composition;
        var restored = MapLayerPreferences.Capture(composition.Runtime);
        var controls = new PlannerLayersControl { DataContext = map.Layers };
        var root = new StackPanel { Children = { controls } };
        var window = new Window { Content = root, Width = 700, Height = 600 };
        var writes = 0; map.Layers.PersistAsync = () => { writes++; return Task.CompletedTask; };
        try
        {
            window.Show();
            var button = controls.FindControl<Button>("LayersButton")!;
            var slider = controls.FindControl<Slider>("OpacitySlider")!;
            var toggle = controls.FindControl<CheckBox>("LightPollutionToggle")!;
            var opacity = controls.FindControl<StackPanel>("OpacityControls")!;
            for (var i = 0; i < 3; i++)
            {
                button.Flyout!.ShowAt(button); Dispatcher.UIThread.RunJobs();
                Assert.True(toggle.IsChecked); Assert.True(opacity.IsVisible);
                Assert.Equal(35, slider.Value);
                Assert.True(native.Enabled);
                slider.Value = 60;
                Assert.Equal(.6, native.Opacity);
                toggle.IsChecked = false;
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(composition.Runtime["light-pollution"].IsVisible);
                Assert.False(opacity.IsVisible); Assert.False(native.Enabled);
                root.Children.Remove(controls);
                Assert.False(button.Flyout.IsOpen);
                root.Children.Add(controls);
                Assert.Equal(i + 1, writes);
                Assert.Equal("cividis", map.LightPollutionPreferences.PaletteId);
                MapLayerPreferences.Restore(composition.Runtime, restored);
                Assert.Equal(35, map.Layers.LightPollutionOpacityPercent);
                Assert.Same(native, composition.Map.Layers.Last());
                Assert.Same(originalComposition, composition.Composition);
            }
            button.Flyout!.ShowAt(button); Dispatcher.UIThread.RunJobs();
            slider.Focus(NavigationMethod.Tab);
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Assert.Equal(3, writes); // Key-down changes runtime; release commits.
            Assert.True(slider.Value > 35);
            window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Assert.Equal(4, writes);
            slider.Value = 72;
            toggle.Focus(NavigationMethod.Tab); Assert.Equal(5, writes); // Focus loss commits.
            var start = slider.TranslatePoint(new Avalonia.Point(slider.Bounds.Width * .72, slider.Bounds.Height / 2), window)!.Value;
            var end = start + new Avalonia.Vector(-50, 0);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            Assert.NotEqual(72, slider.Value);
            Assert.Equal(slider.Value / 100, composition.Runtime["light-pollution"].Opacity, 8);
            Assert.Equal(5, writes);
            window.MouseUp(end, MouseButton.Left); Assert.Equal(6, writes);
            window.MouseDown(new Avalonia.Point(650, 550), MouseButton.Left);
            window.MouseUp(new Avalonia.Point(650, 550), MouseButton.Left);
            Assert.False(button.Flyout.IsOpen);
            Assert.Equal(6, writes); // Dismiss does not write unchanged state.
        }
        finally { window.Close(); await map.ReleaseLightPollutionAsync(); }
    }
}

public sealed partial class MainViewModelTests
{
    [Fact]
    public async Task PlannerLayersPreservePaletteDraftAndPaletteSavePreservesLiveLayers()
    {
        var catalogue = new OpenNgcTargetCatalogue(); var store = CreateSupporterStore(new AppSettings());
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        Assert.Equal(new[] { "turbo", "viridis", "inferno", "magma", "cividis", "grayscale" }, vm.LightPollutionColorMapOptions.Select(x => x.Id));
        var descriptions = new HashSet<string>(); var notifications = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.LightPollutionColorMapDescription)) notifications++; };
        foreach (var palette in vm.LightPollutionColorMapOptions)
        {
            vm.SettingsLightPollutionColorMap = palette;
            Assert.False(string.IsNullOrWhiteSpace(vm.LightPollutionColorMapDescription));
            Assert.True(descriptions.Add(vm.LightPollutionColorMapDescription));
        }
        Assert.Equal(6, notifications);
        vm.SettingsLightPollutionColorMap = LightPollutionColorMaps.Resolve("cividis");
        vm.PreviewPlannerLayers(true, 27);
        await vm.CommitPlannerLayersAsync();
        Assert.Equal(new LightPollutionPreferences(true, .27, "grayscale"), store.State.Settings.LightPollution);
        Assert.Equal("cividis", vm.SettingsLightPollutionColorMap.Id);
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal(new LightPollutionPreferences(true, .27, "cividis"), store.State.Settings.LightPollution);
        vm.ResetSettingsEditorCommand.Execute(null);
        Assert.Equal("grayscale", vm.SettingsLightPollutionColorMap.Id);
        Assert.Equal(new LightPollutionPreferences(true, .27, "cividis"), vm.LightPollutionMapPreferences);
    }
}
