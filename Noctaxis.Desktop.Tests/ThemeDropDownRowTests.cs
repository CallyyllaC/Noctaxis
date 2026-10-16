using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Settings → Theme: every item in the drop-down takes a click, and shows hover, across its whole row — on its
/// name, in the space before it, and at the row's far end — in every appearance. Locked supporter themes stay
/// locked. (The items' theme once replaced the standard one outright, losing its transparent background, so only
/// the text could be clicked.)
/// </summary>
public sealed partial class MainViewModelTests
{
    private static async Task<(MainViewModel ViewModel, MainWindow Window, ComboBox Theme, Noctaxis.Desktop.Themes.ThemeService Themes)> ThemeSettingsAsync()
    {
        var catalogue = new Noctaxis.Core.Catalogues.OpenNgcTargetCatalogue();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, CreatePlannerStore(new AppSettings()), new FakeExporter());
        var themes = new Noctaxis.Desktop.Themes.ThemeService(Application.Current!);
        vm.AttachThemeService(themes);
        await vm.InitializeAsync();
        var window = new MainWindow { Width = 1440, Height = 1000, DataContext = vm };
        window.Show();
        ((TabControl)((Grid)window.Content!).Children[1]).SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        window.SelectSettingsCategory("Appearance");
        var combo = Assert.Single(window.GetVisualDescendants().OfType<ComboBox>(),
            box => Avalonia.Automation.AutomationProperties.GetName(box) == "Theme");
        return (vm, window, combo, themes);
    }

    private static ComboBoxItem OpenAt(MainWindow window, ComboBox combo, int index)
    {
        combo.IsDropDownOpen = true;
        Rendered();
        var item = Assert.IsType<ComboBoxItem>(combo.ContainerFromIndex(index));
        Assert.True(item.IsEffectivelyVisible);
        return item;
    }

    /// <summary>Laid out and rendered: hit testing reads the last rendered frame, so a just-opened drop-down must be drawn first.</summary>
    private static void Rendered()
    {
        // Two frames: the drop-down is positioned after its first is laid out.
        for (var frame = 0; frame < 2; frame++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Three places on a row: just inside its left edge (before the text), on the name, and at its far right.</summary>
    private static Point[] RowPoints(ComboBoxItem item, Visual relativeTo)
    {
        var name = item.GetVisualDescendants().OfType<TextBlock>().First();
        var middle = item.Bounds.Height / 2;
        return
        [
            item.TranslatePoint(new Point(3, middle), relativeTo)!.Value,
            name.TranslatePoint(new Point(name.Bounds.Width / 2, name.Bounds.Height / 2), relativeTo)!.Value,
            item.TranslatePoint(new Point(item.Bounds.Width - 3, middle), relativeTo)!.Value
        ];
    }

    private static void Click(Window window, Point point)
    {
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Rendered();
    }

    [AvaloniaTheory]
    [InlineData("builtin.noctaxis", AppearanceMode.Dark)]
    [InlineData("builtin.noctaxis", AppearanceMode.Light)]
    [InlineData("builtin.highcontrast", AppearanceMode.Dark)]
    [InlineData("builtin.highcontrast", AppearanceMode.Light)]
    public async Task EveryThemeRowTakesAClickAcrossItsWholeWidth(string family, AppearanceMode mode)
    {
        var (vm, window, combo, themes) = await ThemeSettingsAsync();
        try
        {
            await vm.ApplyAppearanceAsync(new AppearancePreferences(family, AppearanceMode: mode));
            Dispatcher.UIThread.RunJobs();
            var options = vm.ThemeOptions.ToList();
            var noctaxis = options.FindIndex(option => option.Id == "builtin.noctaxis");
            var highContrast = options.FindIndex(option => option.Id == "builtin.highcontrast");

            // The row reaches across the drop-down, and every point of it is the item's.
            var item = OpenAt(window, combo, highContrast);
            var presenter = item.GetVisualDescendants().OfType<ContentPresenter>().First();
            Assert.True(item.Bounds.Width >= combo.Bounds.Width - 4, $"row {item.Bounds.Width} px in a {combo.Bounds.Width} px box");
            Assert.True(presenter.Bounds.Width >= item.Bounds.Width - 1);
            foreach (var point in RowPoints(item, window))
            {
                var hit = window.InputHitTest(point) as Visual;
                Assert.True(ReferenceEquals(item, hit?.FindAncestorOfType<ComboBoxItem>(includeSelf: true)),
                    $"{point} hit {hit?.GetType().Name ?? "nothing"}, not the row at {item.TranslatePoint(default, window)} {item.Bounds.Size}");
            }

            // Hover fills the row, from its far right.
            window.MouseMove(RowPoints(item, window)[2]);
            Rendered();
            Assert.True(item.IsPointerOver);
            Assert.True(presenter.Background is ISolidColorBrush { Color.A: > 0 }, "hover is not drawn across the row");
            combo.IsDropDownOpen = false;
            Dispatcher.UIThread.RunJobs();

            // A click at each place selects the theme and closes the drop-down.
            for (var place = 0; place < 3; place++)
            {
                // Always the theme not chosen now, so every click is a change.
                var to = vm.ThemeOptions[highContrast].Id == (vm.Settings.Appearance ?? new()).ThemeFamilyId ? noctaxis : highContrast;
                var row = OpenAt(window, combo, to);
                Click(window, RowPoints(row, window)[place]);
                Assert.False(combo.IsDropDownOpen, $"drop-down still open after a click at place {place}");
                Assert.Equal(vm.ThemeOptions[to].Id, (vm.Settings.Appearance ?? new()).ThemeFamilyId);
                Assert.Equal(mode, (vm.Settings.Appearance ?? new()).AppearanceMode);
                Assert.Equal(vm.ThemeOptions[to], combo.SelectedItem);
            }

            // A locked supporter theme: a click anywhere on its row chooses nothing.
            var locked = options.FindIndex(option => option.IsSupporter && !option.IsAvailable);
            Assert.True(locked >= 0);
            var chosen = (vm.Settings.Appearance ?? new()).ThemeFamilyId;
            foreach (var place in new[] { 0, 1, 2 })
            {
                var row = OpenAt(window, combo, locked);
                Assert.False(row.IsEffectivelyEnabled);
                Click(window, RowPoints(row, window)[place]);
                Assert.Equal(chosen, (vm.Settings.Appearance ?? new()).ThemeFamilyId);
                Assert.NotEqual(vm.ThemeOptions[locked], combo.SelectedItem);
                combo.IsDropDownOpen = false;
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            window.Close();
            themes.Dispose();
        }
    }

    /// <summary>The keyboard still chooses a theme, and still passes over the locked ones.</summary>
    [AvaloniaFact]
    public async Task TheKeyboardStillChoosesAThemeAndSkipsLockedOnes()
    {
        var (vm, window, combo, themes) = await ThemeSettingsAsync();
        try
        {
            combo.SelectedItem = vm.ThemeOptions.Single(option => option.Id == "builtin.noctaxis");
            Dispatcher.UIThread.RunJobs();
            combo.Focus();
            void Press(Key key)
            {
                combo.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
                Dispatcher.UIThread.RunJobs();
            }
            Press(Key.Down);
            Assert.Equal("builtin.highcontrast", (vm.Settings.Appearance ?? new()).ThemeFamilyId);
            Press(Key.Down);
            Assert.Equal("builtin.highcontrast", (vm.Settings.Appearance ?? new()).ThemeFamilyId);
            Press(Key.Up);
            Assert.Equal("builtin.noctaxis", (vm.Settings.Appearance ?? new()).ThemeFamilyId);
        }
        finally
        {
            window.Close();
            themes.Dispose();
        }
    }
}
