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
        static RadioButton[] Radios(WrapPanel p) => p.Children.Cast<RadioButton>().ToArray();
        // The Settings column is 90% of the width beside the category rail, not a fixed 720 px, and the app draws
        // the system font (Segoe UI on Windows, DejaVu Sans on Linux, SF on macOS), so whether a group fits on one
        // row differs by window and platform. What must hold everywhere is a sound layout, wrapped or not.
        void AssertLaidOut(WrapPanel group, double scale)
        {
            var radios = Radios(group);
            foreach (var radio in radios)
            {
                Assert.True(radio.Bounds.X >= -1 && radio.Bounds.Y >= -1, $"{radio.Content} starts outside its group at {scale}%");
                Assert.True(radio.Bounds.Right <= group.Bounds.Width + 1, $"{radio.Content} overruns its group at {scale}%");
                Assert.True(radio.Bounds.Bottom <= group.Bounds.Height + 1, $"{radio.Content} overruns its group at {scale}%");
                var text = radio.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == radio.Content!.ToString());
                Assert.True(text.TextLayout.Width <= text.Bounds.Width + 1, $"Clipped {text.Text} at {scale}%");
            }
            // No two choices overlap, on one row or several.
            for (var i = 0; i < radios.Length; i++)
            for (var j = i + 1; j < radios.Length; j++)
                Assert.False(radios[i].Bounds.Deflate(.5).Intersects(radios[j].Bounds.Deflate(.5)),
                    $"{radios[i].Content} overlaps {radios[j].Content} at {scale}%");
            // Wrapping is only for want of room: choices that fit on one row share it.
            if (radios.Sum(r => r.DesiredSize.Width) <= group.Bounds.Width + 1)
                Assert.Single(radios.Select(r => r.Bounds.Y).Distinct());
        }
        // Responsive layout: the page's column may use up to 90% of the space beside the rail (it is left-aligned
        // and as wide as its content up to that limit), so a wide window allows more than the 720 px it was once
        // capped at, and the column never exceeds its limit.
        void AssertColumn(WrapPanel group)
        {
            var column = group.GetVisualAncestors().OfType<StackPanel>().First(p => p.Classes.Contains("settingsCategory"));
            var viewport = column.GetVisualAncestors().OfType<ScrollViewer>().First();
            Assert.Equal(viewport.Bounds.Width * .9, column.MaxWidth, 1);
            Assert.True(column.Bounds.Width <= column.MaxWidth + 1, $"the column is {column.Bounds.Width} wide, past its {column.MaxWidth} limit");
            Assert.True(group.Bounds.Width <= column.Bounds.Width + 1, $"the group is {group.Bounds.Width} wide in a {column.Bounds.Width} column");
            if (width >= 1440) Assert.True(column.MaxWidth > 720, $"the column is still limited to {column.MaxWidth}");
        }
        try
        {
            window.SelectSettingsCategory("Appearance");
            var mode = window.FindControl<WrapPanel>("AppearanceModeChoices")!;
            var vision = window.FindControl<WrapPanel>("ColourVisionChoices")!;
            var slider = window.FindControl<Slider>("AppearanceTextSize")!;
            Assert.Equal(new[] { "System", "Dark", "Light" }, Radios(mode).Select(r => r.Content));
            Assert.Equal(new[] { "None", "Protanopia", "Deuteranopia", "Tritanopia" }, Radios(vision).Select(r => r.Content));
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
            foreach (var scale in new[] { 100d, 150, 200 })
            {
                await vm.ApplyAppearanceAsync(new(family, TextScale: scale / 100, AppearanceMode: appearance));
                Dispatcher.UIThread.RunJobs();
                slider.Value = scale; Dispatcher.UIThread.RunJobs();
                Assert.Equal($"{scale:0}%", window.FindControl<TextBlock>("AppearanceTextSizeValue")!.Text);
                AssertLaidOut(mode, scale);
                AssertLaidOut(vision, scale);
                AssertColumn(vision);
                // At 720 px and double text size the colour-vision choices must wrap rather than overrun.
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
            Assert.Equal(1.6, store.State.Settings.Appearance!.TextScale); // Keyboard steps commit at once.
            var first = Radios(mode)[0]; first.Focus(NavigationMethod.Tab); Dispatcher.UIThread.RunJobs();
            Assert.True(first.IsFocused);
            Assert.Equal(Color.Parse(service.EffectiveTheme.Palette["FocusBorder"]), ((ISolidColorBrush)first.BorderBrush!).Color);
            AppearancePolishTests.Capture(window, $"settings-radio-focus-{family}-{appearance}-{width}");
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None); Dispatcher.UIThread.RunJobs();
            Assert.True(first.IsChecked);
            Assert.Equal(AppearanceMode.System, vm.SelectedAppearanceMode);
            Assert.NotEqual(service.EffectiveTheme.Palette["FocusBorder"], service.EffectiveTheme.Palette["SelectionBackground"]);

            window.SelectSettingsCategory("General");
            var units = window.FindControl<WrapPanel>("MeasurementChoices")!;
            // Metric, UK Mixed, Imperial: the names are resource text, the saved identifiers are not.
            Assert.Equal(new[] { "Metric", "UK Mixed", "Imperial" }, Radios(units).Select(r => (string)r.Content!));
            Assert.Equal(new[] { "Metric", "UK", "Imperial" }, MeasurementUnits.Options);
            foreach (var (radio, id) in Radios(units).Zip(MeasurementUnits.Options))
            {
                radio.IsChecked = true; Dispatcher.UIThread.RunJobs();
                Assert.Single(Radios(units), r => r.IsChecked == true);
                Assert.Equal(id, vm.SettingsUnits);
                await vm.SettleSettingsAsync();
                Assert.Equal(id, store.State.Settings.Units); // Saved automatically.
            }
            foreach (var scale in new[] { 100d, 200 })
            {
                await vm.ApplyAppearanceAsync(new(family, TextScale: scale / 100, AppearanceMode: appearance));
                Dispatcher.UIThread.RunJobs();
                AssertLaidOut(units, scale);
                AssertColumn(units);
            }
            Assert.Same(session, vm.Session);
            Assert.Equal(calls, (planning.SnapshotCalculations, planning.EnvironmentRequests, planning.ForcedRefreshes));
            Assert.Equal("Imperial", store.State.Settings.Units);
        }
        finally { window.Close(); service.Apply(new()); }
    }
}
