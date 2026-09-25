using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Measurements;
using Noctaxis.Desktop.Views;
using NodaTime;
namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    [AvaloniaTheory]
    [InlineData("builtin.noctaxis", AppearanceMode.Dark, 1440)]
    [InlineData("builtin.noctaxis", AppearanceMode.Light, 1440)]
    [InlineData("builtin.highcontrast", AppearanceMode.Dark, 1440)]
    [InlineData("builtin.highcontrast", AppearanceMode.Light, 1440)]
    [InlineData("builtin.noctaxis", AppearanceMode.Dark, 720)]
    [InlineData("builtin.noctaxis", AppearanceMode.Light, 720)]
    [InlineData("builtin.highcontrast", AppearanceMode.Dark, 720)]
    [InlineData("builtin.highcontrast", AppearanceMode.Light, 720)]
    [CoversSettingsInput("SelectedColourVisionValue")]
    public async Task SettingsRadioChoicesWrapAndPreserveBindings(string family, AppearanceMode appearance, int width)
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var store = new FakeStore(new(1, new AppSettings(), [], PlanningSession.Default(Instant.FromUtc(2026, 1, 1, 12, 0), "UTC"), null));
        var vm = CreateViewModel(planning, catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        var service = ((App)Application.Current!).Themes!;
        vm.AttachThemeService(service);
        await vm.ApplyAppearanceAsync(new(family, AppearanceMode: appearance));
        var window = new MainWindow { Width = width, Height = 1000, DataContext = vm };
        window.Show();
        var pages = (TabControl)((Grid)window.Content!).Children[1]; pages.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        var calls = (planning.SnapshotCalculations, planning.EnvironmentRequests, planning.ForcedRefreshes);
        var session = vm.Session;
        var mode = window.FindControl<WrapPanel>("AppearanceModeChoices")!;
        var vision = window.FindControl<WrapPanel>("ColourVisionChoices")!;
        var units = window.FindControl<WrapPanel>("MeasurementChoices")!;
        var slider = window.FindControl<Slider>("AppearanceTextSize")!;
        static RadioButton[] Radios(WrapPanel p) => p.Children.Cast<RadioButton>().ToArray();
        try
        {
            Assert.Equal(new[] { "System", "Dark", "Light" }, Radios(mode).Select(r => r.Content));
            Assert.Equal(new[] { "None", "Protanopia", "Deuteranopia", "Tritanopia" }, Radios(vision).Select(r => r.Content));
            Assert.Equal(MeasurementUnits.Options, Radios(units).Select(r => (string)r.Content!));
            foreach (var radio in Radios(mode))
            {
                radio.IsChecked = true; Dispatcher.UIThread.RunJobs();
                Assert.Single(Radios(mode), r => r.IsChecked == true);
                Assert.Equal(radio.Content!.ToString(), vm.SelectedAppearanceMode.ToString());
                Assert.Equal(vm.SelectedAppearanceMode, store.State.Settings.Appearance!.AppearanceMode);
            }
            foreach (var radio in Radios(vision))
            {
                radio.IsChecked = true; Dispatcher.UIThread.RunJobs();
                Assert.Single(Radios(vision), r => r.IsChecked == true);
                Assert.Equal(radio.Content!.ToString(), vm.SelectedColourVision!.Mode.ToString());
                Assert.Equal(vm.SelectedColourVision.Mode, store.State.Settings.Appearance!.ColourVisionMode);
            }
            var savedUnits = store.State.Settings.Units;
            foreach (var radio in Radios(units))
            {
                radio.IsChecked = true; Dispatcher.UIThread.RunJobs();
                Assert.Single(Radios(units), r => r.IsChecked == true);
                Assert.Equal(radio.Content, vm.SettingsUnits);
                Assert.Equal(savedUnits, store.State.Settings.Units); // Remains on the existing Save settings path.
            }
            foreach (var scale in new[] { 100d, 150, 200 })
            {
                await vm.ApplyAppearanceAsync(new(family, TextScale: scale / 100, AppearanceMode: appearance));
                Dispatcher.UIThread.RunJobs();
                slider.Value = scale; Dispatcher.UIThread.RunJobs();
                Assert.Equal($"{scale:0}%", window.FindControl<TextBlock>("AppearanceTextSizeValue")!.Text);
                foreach (var group in new[] { mode, vision, units })
                {
                    foreach (var radio in Radios(group))
                    {
                        Assert.True(radio.Bounds.Right <= group.Bounds.Width + 1);
                        Assert.True(radio.Bounds.Bottom <= group.Bounds.Height + 1);
                        var text = radio.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == radio.Content!.ToString());
                        Assert.True(text.TextLayout.Width <= text.Bounds.Width + 1, $"Clipped {text.Text}");
                    }
                    if (scale == 100) Assert.Single(Radios(group).Select(r => r.Bounds.Y).Distinct());
                }
                if (scale == 200 && width == 720) Assert.True(Radios(vision).Select(r => r.Bounds.Y).Distinct().Count() > 1);
                AppearancePolishTests.Capture(window, $"settings-radio-{family}-{appearance}-{width}-{scale:0}");
            }
            slider.Value = 150; Dispatcher.UIThread.RunJobs();
            Assert.Equal(1.5, store.State.Settings.Appearance!.TextScale);
            Assert.Equal("150%", window.FindControl<TextBlock>("AppearanceTextSizeValue")!.Text);
            Assert.Equal(100, slider.Minimum); Assert.Equal(200, slider.Maximum);
            Assert.Equal(10, slider.TickFrequency); Assert.True(slider.IsSnapToTickEnabled);
            slider.Focus(NavigationMethod.Tab);
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); Dispatcher.UIThread.RunJobs();
            Assert.Equal(160, slider.Value);
            Assert.Equal("160%", window.FindControl<TextBlock>("AppearanceTextSizeValue")!.Text);
            Assert.Equal(1.6, store.State.Settings.Appearance!.TextScale);
            var first = Radios(mode)[0]; first.Focus(NavigationMethod.Tab); Dispatcher.UIThread.RunJobs();
            Assert.True(first.IsFocused);
            Assert.Equal(Color.Parse(service.EffectiveTheme.Palette["FocusBorder"]), ((ISolidColorBrush)first.BorderBrush!).Color);
            AppearancePolishTests.Capture(window, $"settings-radio-focus-{family}-{appearance}-{width}");
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None); Dispatcher.UIThread.RunJobs();
            Assert.True(first.IsChecked);
            Assert.Equal(AppearanceMode.System, vm.SelectedAppearanceMode);
            Assert.NotEqual(service.EffectiveTheme.Palette["FocusBorder"], service.EffectiveTheme.Palette["SelectionBackground"]);
            Assert.Same(session, vm.Session);
            Assert.Equal(calls, (planning.SnapshotCalculations, planning.EnvironmentRequests, planning.ForcedRefreshes));
            await vm.SaveSettingsCommand.ExecuteAsync(null);
            Assert.Equal("UK", store.State.Settings.Units);
        }
        finally { window.Close(); service.Apply(new()); }
    }
}
