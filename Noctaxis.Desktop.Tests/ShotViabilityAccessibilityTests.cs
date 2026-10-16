using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.LightPollution;
using Noctaxis.Core.Localisation;
using Noctaxis.Core.Viability;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Services;
using Noctaxis.Desktop.Themes;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Views;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The shot-viability details' light pollution, read from the installed atlas the map already uses, and the
/// accessibility of its four statuses (Good, Warning, Critical, Unknown) in every theme, appearance and
/// colour-vision variant: each told apart by an icon shape and a word, with colour only as a supplement.
/// </summary>
public sealed partial class MainViewModelTests
{
    /// <summary>A stand-in for the atlas reader: answers by place, counting what it was asked.</summary>
    private sealed class FakeLightPollutionReader : ILightPollutionDataProvider
    {
        public List<(double Latitude, double Longitude)> Reads { get; } = [];
        public Func<double, double, Task<double?>> Answer { get; set; } = (_, _) => Task.FromResult<double?>(0.05);
        public Task<double?> SampleAsync(double latitude, double longitude, bool bilinear = false, CancellationToken cancellationToken = default)
        {
            Reads.Add((latitude, longitude));
            return Answer(latitude, longitude);
        }
    }

    private static async Task<MainViewModel> FocusedDeepSkyViewModelAsync(FakePlanning planning, OpenNgcTargetCatalogue catalogue)
    {
        var vm = CreateViewModel(planning, catalogue, CreatePlannerStore(new AppSettings()), new FakeExporter());
        await vm.InitializeAsync();
        await vm.WaitForPlannerRefreshAsync();
        var m31 = catalogue.Get("M31");
        vm.AddCelestialObjectCommand.Execute(m31);
        vm.CelestialObjects.Single(item => item.TargetId == m31.Id).FocusCommand.Execute(null);
        await vm.WaitForPlannerRefreshAsync();
        return vm;
    }

    /// <summary>
    /// Without an atlas the sky's brightness is unknown, never a perfect score. With one, the place is read once,
    /// through the map's own reader; it is read again only when the place changes, and a late answer for a place
    /// the plan has left is discarded. Outside the atlas, or unreadable, it is unknown and says why.
    /// </summary>
    [Fact]
    public async Task LightPollutionIsReadOncePerPlaceFromTheInstalledAtlas()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var vm = await FocusedDeepSkyViewModelAsync(new FakePlanning(catalogue), catalogue);
        ShotViabilityMetricResult Metric() => vm.ShotViability![ShotViabilityMetric.LightPollution];
        Assert.Equal(ShotViabilityMetricStatus.Unavailable, Metric().Status);
        Assert.Contains("install the light-pollution atlas", Metric().Explanation);
        var coverageWithout = vm.ShotViability!.Coverage;

        var reader = new FakeLightPollutionReader();
        var pending = new TaskCompletionSource<double?>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Answer = (_, _) => pending.Task;
        vm.UseLightPollutionReader(reader);
        Assert.Single(reader.Reads);
        Assert.StartsWith("Reading the light-pollution atlas", Metric().Explanation);
        pending.SetResult(3);
        await vm.LightPollutionReadForTesting;
        Assert.True(Metric().IsEvaluated);
        Assert.StartsWith("Suburban sky glow: about 4.0×", Metric().Explanation);
        Assert.True(vm.ShotViability!.Coverage > coverageWithout);
        Assert.Contains(vm.ShotViabilityRows, row => row.Id == "light-pollution" && row.Kind == StatusKind.Warning && row.SeverityLabel == "Warning");

        // A new time at the same place reads nothing.
        vm.MinutesOfDay = (vm.MinutesOfDay + 60) % (24 * 60);
        await vm.WaitForPlannerRefreshAsync();
        Assert.Single(reader.Reads);

        // A new place is read; an answer for the old one, arriving late, is not used.
        var slow = new TaskCompletionSource<double?>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Answer = (_, _) => slow.Task;
        vm.MoveObserver(new GeoCoordinate(57.1, -4.2, 300));
        await vm.WaitForPlannerRefreshAsync();
        Assert.Equal(2, reader.Reads.Count);
        var stale = vm.LightPollutionReadForTesting;
        reader.Answer = (_, _) => Task.FromResult<double?>(0.02);
        vm.MoveObserver(new GeoCoordinate(57.3, -4.5, 300));
        await vm.WaitForPlannerRefreshAsync();
        await vm.LightPollutionReadForTesting;
        slow.SetResult(50);
        await stale;
        Assert.StartsWith("Dark site", Metric().Explanation);

        // Outside the atlas, and unreadable: unknown, and why.
        reader.Answer = (_, _) => Task.FromResult<double?>(null);
        vm.MoveObserver(new GeoCoordinate(78.2, 15.6, 10));
        await vm.WaitForPlannerRefreshAsync();
        await vm.LightPollutionReadForTesting;
        Assert.Equal("Unknown: this place is outside the light-pollution atlas.", Metric().Explanation);
        reader.Answer = (_, _) => Task.FromException<double?>(new IOException("disk"));
        vm.MoveObserver(new GeoCoordinate(52.0, 0.1, 10));
        await vm.WaitForPlannerRefreshAsync();
        await vm.LightPollutionReadForTesting;
        Assert.Equal(ShotViabilityMetricStatus.Unavailable, Metric().Status);
        Assert.Contains("could not be read", Metric().Explanation);
        Assert.Contains(vm.ShotViabilityRows, row => row.Id == "light-pollution" && row.Kind == StatusKind.Information && row.SeverityLabel == "Unknown");
    }

    // ---- Status accessibility --------------------------------------------------------------------------------

    private static readonly (ShotViabilitySeverity Severity, StatusKind Kind, string Word)[] Statuses =
    [
        (ShotViabilitySeverity.Good, StatusKind.Success, "Good"), (ShotViabilitySeverity.Caution, StatusKind.Warning, "Warning"),
        (ShotViabilitySeverity.Critical, StatusKind.Error, "Critical"), (ShotViabilitySeverity.None, StatusKind.Information, "Unknown")
    ];

    /// <summary>
    /// The four statuses never rest on colour: each has its own icon shape (tick, triangle, octagon, circled i)
    /// and its own word, which the row's accessible name starts with.
    /// </summary>
    [AvaloniaFact]
    public void EachStatusHasItsOwnIconAndWordAsWellAsAColour()
    {
        _ = TestMarkup.ReadMainWindow();
        var shapes = Statuses.Select(status => new StatusText { Kind = status.Kind }.IconForTesting.Data!).ToArray();
        Assert.Equal(4, shapes.Distinct().Count());
        Assert.Equal(["Good", "Warning", "Critical"], Statuses.Take(3).Select(status => Strings.Get("Planning_Viability_Severity_" + status.Severity)));
        Assert.Equal("Unknown", Strings.Get("Planning_Viability_Status_" + ShotViabilityMetricStatus.Unavailable));
        Assert.Equal(4, Statuses.Select(status => status.Word).Distinct().Count());
    }

    /// <summary>
    /// Every status colour is readable as text (4.5:1, WCAG AA) on both surfaces it appears on, the Camera frame
    /// card and the details popover, in every theme, appearance and colour-vision variant; so is the keyboard
    /// focus ring around a metric (3:1). (That the four stay apart for each variant's own viewer is checked by
    /// ColourVisionAccessibilityTests.StatusColoursStayDistinctForTheirViewer, under simulation.)
    /// </summary>
    [Theory, MemberData(nameof(AppearanceTests.Variants), MemberType = typeof(AppearanceTests))]
    public void StatusColoursAreReadableWhereTheViabilityDetailsDrawThem(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var p = new ThemeCatalogue(isAvailable: _ => true).Resolve(new(id, mode, AppearanceMode: appearance)).Palette;
        foreach (var (_, kind, _) in Statuses)
            foreach (var surface in new[] { "SurfaceBackground", "ElevatedSurface" })
                Assert.True(ColourScience.Contrast(p[kind.ToString()], p[surface]) >= 4.5,
                    $"{id}/{appearance}/{mode}: {kind} on {surface} is {ColourScience.Contrast(p[kind.ToString()], p[surface]):F2}:1");
        Assert.True(ColourScience.Contrast(p["FocusBorder"], p["ElevatedSurface"]) >= 3, $"{id}/{appearance}/{mode}: a metric's focus ring");
    }

    /// <summary>
    /// In the running details, in every theme, appearance and colour-vision variant: the popover draws on the
    /// theme's raised surface, each metric's icon and words take the theme's own status resource for its status
    /// (never a fixed colour), and reach 4.5:1 there. All four statuses are on show: a Good forecast, a Warning
    /// (a low Sun), a Critical (the camera turned away) and Unknown (no terrain or sensor resolution).
    /// </summary>
    [AvaloniaFact]
    public async Task TheRunningDetailsUseTheThemesStatusColoursInEveryVariant()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue) { HorizontalFor = _ => new HorizontalCoordinate(180, 1) };
        var vm = CreateViewModel(planning, catalogue, CreatePlannerStore(new AppSettings()), new FakeExporter(),
            persistenceDelay: (_, _) => Task.CompletedTask);
        await vm.InitializeAsync();
        var app = Application.Current!;
        using var themes = new ThemeService(app, new ThemeCatalogue(isAvailable: _ => true));
        vm.AttachThemeService(themes);
        var search = new LocationSearchViewModel(new FakeLocationSearchProvider(), NullLogger<LocationSearchViewModel>.Instance);
        var window = new MainWindow(vm, new DesktopDialogService(search)) { Width = 1280, Height = 820 };
        TestMaps.OfflinePip(window);
        window.Show();
        ((TabControl)((Grid)window.Content!).Children[1]).SelectedIndex = 1;
        try
        {
            await vm.WaitForPlannerRefreshAsync();
            vm.CameraBearingDegrees = 0;
            Settle(window);
            Assert.Equal(["Critical", "Good", "Unknown", "Warning"],
                vm.ShotViabilityRows.Select(row => row.SeverityLabel).Distinct().Order());
            var button = window.FindControl<Button>("ShotViabilityButton")!;
            var flyout = (Flyout)button.Flyout!;
            flyout.ShowAt(button);
            Settle(window);
            var details = Assert.IsType<ScrollViewer>(flyout.Content);
            foreach (var variant in AppearanceTests.Variants)
            {
                var (id, mode, appearance) = ((string)variant[0], (ColourVisionMode)variant[1], (AppearanceMode)variant[2]);
                themes.Apply(new(id, mode, AppearanceMode: appearance));
                Dispatcher.UIThread.RunJobs();
                Settle(window);
                var label = $"{id}/{appearance}/{mode}";
                Color Resource(string key) => app.TryFindResource(key, window.ActualThemeVariant, out var value)
                    ? Assert.IsAssignableFrom<ISolidColorBrush>(value).Color : throw new InvalidOperationException(key);
                var presenter = details.GetVisualAncestors().OfType<FlyoutPresenter>().First();
                var surface = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color;
                Assert.Equal(Resource("ElevatedSurface"), surface);
                foreach (var status in details.GetVisualDescendants().OfType<StatusText>())
                {
                    var expected = Resource(status.Kind.ToString());
                    Assert.Equal(expected, Assert.IsAssignableFrom<ISolidColorBrush>(status.IconForTesting.Foreground).Color);
                    Assert.Equal(expected, Assert.IsAssignableFrom<ISolidColorBrush>(status.TextBlockForTesting.Foreground).Color);
                    Assert.True(ColourScience.Contrast(expected, surface) >= 4.5, $"{label}: {status.Kind} on the details");
                }
            }
            // The header names what is wrong, not just how many things are.
            Assert.Contains("1 critical issue and 1 warning", AutomationProperties.GetName(button));
            flyout.Hide();
        }
        finally { window.Close(); ((App)app).Themes!.Apply(new()); }
    }
    /// <summary>
    /// Captures of the open details for review, with M31 focused and an atlas value, in representative themes,
    /// window sizes and text scales. Opt-in: set NOCTAXIS_VIABILITY_CAPTURES to a folder.
    /// </summary>
    [AvaloniaFact]
    public async Task ShotViabilityDetailsCaptures()
    {
        var output = System.Environment.GetEnvironmentVariable("NOCTAXIS_VIABILITY_CAPTURES");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue) { HorizontalFor = _ => new HorizontalCoordinate(180, 50) };
        var vm = await FocusedDeepSkyViewModelAsync(planning, catalogue);
        vm.UseLightPollutionReader(new FakeLightPollutionReader { Answer = (_, _) => Task.FromResult<double?>(2.2) });
        await vm.LightPollutionReadForTesting;
        var app = Application.Current!;
        using var themes = new ThemeService(app, new ThemeCatalogue(isAvailable: _ => true));
        vm.AttachThemeService(themes);
        var search = new LocationSearchViewModel(new FakeLocationSearchProvider(), NullLogger<LocationSearchViewModel>.Instance);
        var window = new MainWindow(vm, new DesktopDialogService(search)) { Width = 1280, Height = 820 };
        TestMaps.OfflinePip(window);
        window.Show();
        ((TabControl)((Grid)window.Content!).Children[1]).SelectedIndex = 1;
        try
        {
            vm.CameraBearingDegrees = 180;
            vm.CameraPitchDegrees = 50;
            var button = window.FindControl<Button>("ShotViabilityButton")!;
            var flyout = (Flyout)button.Flyout!;
            foreach (var (theme, appearance, vision, scale, width, height) in new (string, AppearanceMode, ColourVisionMode, double, double, double)[]
                     {
                         ("builtin.noctaxis", AppearanceMode.Dark, ColourVisionMode.None, 1, 1280, 820),
                         ("builtin.noctaxis", AppearanceMode.Light, ColourVisionMode.None, 1, 1280, 820),
                         ("builtin.highcontrast", AppearanceMode.Dark, ColourVisionMode.None, 1, 1280, 820),
                         ("supporter.pigeon", AppearanceMode.Light, ColourVisionMode.Deuteranopia, 1, 1280, 820),
                         ("builtin.noctaxis", AppearanceMode.Dark, ColourVisionMode.Tritanopia, 1, 760, 560),
                         ("builtin.noctaxis", AppearanceMode.Dark, ColourVisionMode.None, 2, 1280, 820),
                     })
            {
                window.Width = width; window.Height = height;
                themes.Apply(new(theme, vision, scale, appearance));
                Dispatcher.UIThread.RunJobs();
                Settle(window);
                flyout.ShowAt(button);
                Settle(window);
                Settle(window);
                var details = Assert.IsType<ScrollViewer>(flyout.Content);
                var root = (Visual)TopLevel.GetTopLevel(details)!;
                var size = root.Bounds.Size;
                using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height))))
                {
                    bitmap.Render(root);
                    bitmap.Save(Path.Combine(output, $"details-{theme}-{appearance}-{vision}-{scale * 100:0}-{width:0}x{height:0}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
                using (var whole = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)width, (int)height)))
                {
                    whole.Render(window);
                    whole.Save(Path.Combine(output, $"planner-{theme}-{appearance}-{vision}-{scale * 100:0}-{width:0}x{height:0}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
                flyout.Hide();
                Settle(window);
            }
        }
        finally { window.Close(); ((App)app).Themes!.Apply(new()); }
    }
}
