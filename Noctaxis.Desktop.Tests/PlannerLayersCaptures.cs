using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Mapping;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    [AvaloniaFact]
    public async Task PlannerLayersWindowBindingsThemesAndOptionalCaptures()
    {
        var output = Environment.GetEnvironmentVariable("NOCTAXIS_LAYERS_CAPTURES");
        if (output is not null) Directory.CreateDirectory(output);
        var catalogue = new OpenNgcTargetCatalogue();
        var store = CreateSupporterStore(new AppSettings(LightPollution: new(true, .42, "turbo")));
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        var theme = ((App)Application.Current!).Themes!;
        vm.AttachThemeService(theme);
        var search = new Noctaxis.Desktop.ViewModels.LocationSearchViewModel(new FakeLocationSearchProvider(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Noctaxis.Desktop.ViewModels.LocationSearchViewModel>.Instance);
        var window = new MainWindow(vm, new DesktopDialogService(search)) { Width = 1440, Height = 1000 };
        var map = window.FindControl<PlannerMapView>("PlannerMap")!;
        map.CompositionForTesting.Map.Layers.First().Enabled = false;
        window.Show();
        try
        {
            var tabs = (TabControl)((Grid)window.Content!).Children[1];
            foreach (var (id, scale) in new[] { ("dark", 1d), ("light", 1d), ("highcontrast", 1d), ("dark", 2d), ("light", 2d) })
            {
                await vm.ApplyAppearanceAsync(new("builtin." + id, TextScale: scale));
                tabs.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
                var layers = window.FindControl<PlannerLayersControl>("PlannerLayers")!;
                Assert.Same(map.Layers, layers.DataContext);
                var button = layers.FindControl<Button>("LayersButton")!;
                button.Flyout!.ShowAt(button); Dispatcher.UIThread.RunJobs();
                var slider = layers.FindControl<Slider>("OpacitySlider")!;
                Assert.Equal(42, slider.Value);
                Assert.Equal(14 * scale, layers.FindControl<CheckBox>("LightPollutionToggle")!.FontSize);
                Capture(window, $"{id}-{scale * 100:0}-planner");
                Capture(TopLevel.GetTopLevel(slider)!, $"{id}-{scale * 100:0}-flyout");
                button.Flyout.Hide();
                tabs.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
                var settings = (TabControl)((Grid)((TabItem)tabs.Items[2]!).Content!).Children[0];
                settings.SelectedItem = settings.Items.Cast<TabItem>().Single(x => (string?)x.Header == "Data");
                vm.SettingsLightPollutionColorMap = LightPollutionColorMaps.Resolve("cividis");
                Dispatcher.UIThread.RunJobs();
                var description = window.FindControl<TextBlock>("LightPollutionPaletteDescription")!;
                description.BringIntoView(); Dispatcher.UIThread.RunJobs();
                Assert.Equal(vm.LightPollutionColorMapDescription, description.Text);
                Capture(window, $"{id}-{scale * 100:0}-settings");
            }
            map.Layers.LightPollutionOpacityPercent = 64;
            Assert.Equal(.64, vm.LightPollutionMapPreferences.Opacity);
            Assert.Equal("turbo", vm.LightPollutionMapPreferences.PaletteId);
            var saveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            map.Layers.PersistAsync = async () => { await saveGate.Task; await vm.CommitPlannerLayersAsync(); };
            var pending = map.Layers.CommitAsync();
            window.Close(); window.Close();
            Assert.False(window.CloseCleanup.IsCompleted);
            saveGate.SetResult(); await pending; await window.CloseCleanup;
            Assert.Equal(.64, store.State.Settings.LightPollution!.Opacity);
        }
        finally { window.Close(); await window.CloseCleanup; theme.Apply(new()); }

        void Capture(Control control, string name)
        {
            if (output is null) return;
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(control.Bounds.Width), (int)Math.Ceiling(control.Bounds.Height)));
            bitmap.Render(control); bitmap.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
        }
    }
}
