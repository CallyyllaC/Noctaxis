using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Noctaxis.Core.Astronomy;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Export;
using Noctaxis.Core.Planning;
using Noctaxis.Core.Time;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Export;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// A plan image is about one target, the focused one: its pitch and bearing over the window, drawn so the direction
/// to point the camera can be read off the printed sheet. No focus, no sheet; no other target on it.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static readonly GeoCoordinate Wales = new(52.59302, -3.64697);
    private static readonly Instant OctoberEvening = Instant.FromUtc(2026, 10, 7, 17, 0);

    /// <summary>A target's real path over the 24-hour window, at the Planner's own 10-minute step.</summary>
    private static async Task<TimelinePath> RealPathAsync(string id, GeoCoordinate observer, Instant start)
    {
        var target = new OpenNgcTargetCatalogue().Get(id);
        var path = await new AstronomyEngineService(new TimeZoneResolver()).CalculatePathAsync(target, observer, start,
            start + IPlanningService.TimelineLength, "Europe/London", start, Duration.FromMinutes(10), CancellationToken.None);
        return new TimelinePath(target, path);
    }

    private static ExportTargetSample Sample(int minutes, double bearing, double pitch, double terrain = double.NaN) =>
        new(OctoberEvening + Duration.FromMinutes(minutes), bearing, pitch, terrain);

    // ---- The one target --------------------------------------------------------------------------------------

    /// <summary>The focused target, and only it, is the sheet's subject, its samples the Timeline's own.</summary>
    [AvaloniaFact]
    public async Task TheFocusedTargetIsTheSheetsOneTarget()
    {
        var exporter = new FakeExporter();
        var (vm, window, _) = await CreatePlannerAsync(exporter: exporter);
        try
        {
            var focused = vm.CelestialObjects.Last(item => item.IsVisible);
            focused.FocusCommand.Execute(null);
            Settle(window);
            await vm.WaitForPlannerRefreshAsync();
            Assert.True(vm.TimelinePaths.Count > 1, "the Planner shows more than one target");

            await vm.CreateExportPngAsync(CancellationToken.None);
            var sheet = exporter.LastExport!;
            Assert.Equal(focused.Target.DisplayName, sheet.Target.Name);
            Assert.StartsWith(focused.Target.DisplayName + " · ", sheet.Subject);
            var path = vm.TimelinePaths.Single(item => item.Target.Id == focused.TargetId).Path;
            Assert.Equal(path.Samples.Select(sample => (sample.Instant, sample.Horizontal.AltitudeDegrees, Angles.NormaliseDegrees(sample.Horizontal.AzimuthDegrees))),
                sheet.Target.Samples.Select(sample => (sample.Time, sample.Pitch, sample.Bearing)));
            // Every other shown target is absent: the sheet carries one series.
            Assert.DoesNotContain(vm.TimelinePaths.Where(item => item.Target.Id != focused.TargetId),
                item => sheet.Target.Name == item.Target.DisplayName);
            Assert.Equal(focused.TargetId, sheet.Plan.Focus!.TargetId);
        }
        finally { Close(window); }
    }

    /// <summary>With nothing focused there is no sheet: the export says why, renders nothing and changes nothing.</summary>
    [AvaloniaFact]
    public async Task ExportWithoutAFocusedTargetSaysSoAndDrawsNothing()
    {
        var exporter = new FakeExporter();
        var (vm, window, _) = await CreatePlannerAsync(exporter: exporter);
        try
        {
            vm.ImportPlanImage(PlanImageSource.Of(PlanPng(vm.CurrentPlan() with { Focus = null })));
            Settle(window);
            await vm.WaitForPlannerRefreshAsync();
            Assert.DoesNotContain(vm.CelestialObjects, item => item.IsPrimary);

            await Assert.ThrowsAsync<Noctaxis.Desktop.ViewModels.ExportNotPossibleException>(() => vm.CreateExportPngAsync(CancellationToken.None));
            Assert.Equal("Focus a target to export a plan.", vm.StatusMessage);
            Assert.Equal(0, exporter.RenderCount);
        }
        finally { Close(window); }
    }

    /// <summary>A focused target that is then hidden cannot be exported: it is no longer drawn, so there is no plan of it.</summary>
    [AvaloniaFact]
    public async Task AHiddenFocusedTargetCannotBeExported()
    {
        var exporter = new FakeExporter();
        var (vm, window, _) = await CreatePlannerAsync(exporter: exporter);
        try
        {
            var focused = vm.CelestialObjects.Single(item => item.IsPrimary);
            vm.CelestialObjects.First(item => !item.IsPrimary).IsVisible = true;
            focused.IsVisible = false;
            Settle(window);
            await vm.WaitForPlannerRefreshAsync();

            await Assert.ThrowsAsync<Noctaxis.Desktop.ViewModels.ExportNotPossibleException>(() => vm.CreateExportPngAsync(CancellationToken.None));
            Assert.Contains(vm.StatusMessage, new[] { "Focus a target to export a plan.",
                "The focused target is still being calculated. Try again in a moment." });
            Assert.Equal(0, exporter.RenderCount);
        }
        finally { Close(window); }
    }

    // ---- Pitch -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The pitch is the planning calculation's altitude; the target rises and sets where its altitude crosses 0°, at the
    /// moments the astronomy gives; its highest point is the window's highest sample, at its transit.
    /// </summary>
    [Fact]
    public async Task PitchFollowsTheTargetsAltitudeRiseSetAndCulmination()
    {
        var path = await RealPathAsync("M42", Wales, OctoberEvening);
        var series = ExportTargetSeries.From(path, null, CelestialRole.DeepSky1);
        Assert.Equal(path.Path.Samples.Select(sample => sample.Horizontal.AltitudeDegrees), series.Samples.Select(sample => sample.Pitch));
        Assert.All(series.Samples, sample => Assert.False(double.IsFinite(sample.Terrain)));

        var crossings = series.Crossings();
        Assert.Contains(crossings, crossing => crossing.Kind == ExportCrossingKind.Rises);
        Assert.Contains(crossings, crossing => crossing.Kind == ExportCrossingKind.Sets);
        Assert.DoesNotContain(crossings, crossing => crossing.Kind is ExportCrossingKind.ClearsTerrain or ExportCrossingKind.BehindTerrain);
        foreach (var crossing in crossings) Assert.Equal(0, crossing.Pitch, 6);
        var rises = crossings.First(crossing => crossing.Kind == ExportCrossingKind.Rises).Time;
        if (path.Path.Events.Rise is { } rise && rise > OctoberEvening && rise < OctoberEvening + IPlanningService.TimelineLength)
            Assert.True(Math.Abs((rises - rise).TotalMinutes) < 6, $"rises {rises}, astronomy {rise}");

        var top = series.Culmination!;
        Assert.Equal(path.Path.Samples.Max(sample => sample.Horizontal.AltitudeDegrees), top.Pitch);
        Assert.InRange(top.Bearing, 170, 190);
        if (path.Path.Events.Transit is { } transit && transit > OctoberEvening && transit < OctoberEvening + IPlanningService.TimelineLength)
            Assert.True(Math.Abs((top.Time - transit).TotalMinutes) <= 10, $"highest {top.Time}, transit {transit}");
    }

    /// <summary>Behind the terrain, the target is hidden however high it is: the crossings are where it clears and drops behind it.</summary>
    [Fact]
    public void TerrainHidesTheTargetUntilItClearsIt()
    {
        var series = new ExportTargetSeries("Test", CelestialRole.DeepSky1,
            [Sample(0, 90, -4, 10), Sample(10, 95, 4, 10), Sample(20, 100, 12, 10), Sample(30, 110, 20, 10), Sample(40, 120, 8, 10)]);
        Assert.Equal([false, false, true, true, false], series.Samples.Select(sample => sample.IsVisible));
        var crossings = series.Crossings();
        Assert.Equal([ExportCrossingKind.Rises, ExportCrossingKind.ClearsTerrain, ExportCrossingKind.BehindTerrain],
            crossings.Select(crossing => crossing.Kind));
        Assert.Equal(OctoberEvening + Duration.FromMinutes(5), crossings[0].Time);
        Assert.Equal(OctoberEvening + Duration.FromMinutes(17.5), crossings[1].Time);
        Assert.Equal(10, crossings[1].Pitch, 6);
        // Without terrain, the geometric horizon is the one to clear.
        Assert.True(Sample(0, 0, 1).IsVisible);
        Assert.False(Sample(0, 0, -1).IsVisible);
    }

    // ---- Terrain over time ------------------------------------------------------------------------------------

    /// <summary>The bearing is the target's azimuth, on 0°–360°.</summary>
    [Fact]
    public async Task BearingFollowsTheTargetsAzimuth()
    {
        var path = await RealPathAsync("M31", Wales, OctoberEvening);
        var series = ExportTargetSeries.From(path, null, CelestialRole.DeepSky1);
        Assert.Equal(path.Path.Samples.Select(sample => Angles.NormaliseDegrees(sample.Horizontal.AzimuthDegrees)),
            series.Samples.Select(sample => sample.Bearing));
        Assert.All(series.Samples, sample => Assert.InRange(sample.Bearing, 0, 360));
    }

    /// <summary>A terrain horizon that rises steadily with bearing, from 0° due North to almost 18° just west of it.</summary>
    private static TerrainHorizonProfile RisingWithBearing(GeoCoordinate observer) => new(observer,
        Enumerable.Range(0, 360).Select(bearing => new TerrainHorizonSample(bearing, bearing / 20.0, 2000)).ToArray(),
        HasDemCoverage: true, Status: "Test ridge", GeneratedAt: OctoberEvening);

    /// <summary>
    /// Each moment's terrain is the horizon in the target's direction at that moment: as the target moves round the
    /// sky the terrain is read where it now is — not at one fixed bearing, nor the camera's.
    /// </summary>
    [Fact]
    public async Task TerrainIsReadInTheTargetsOwnDirectionAtEachMoment()
    {
        var path = await RealPathAsync("M31", Wales, OctoberEvening);
        var terrain = RisingWithBearing(Wales);
        var series = ExportTargetSeries.From(path, terrain, CelestialRole.DeepSky1);
        foreach (var sample in series.Samples)
            Assert.Equal(terrain.EffectiveAltitudeAt(sample.Bearing)!.Value, sample.Terrain, 9);
        // The target sweeps round, and the terrain under it changes with it.
        Assert.True(series.Samples.Max(sample => sample.Bearing) - series.Samples.Min(sample => sample.Bearing) > 180);
        Assert.True(series.Samples.Select(sample => Math.Round(sample.Terrain, 1)).Distinct().Count() > 20, "terrain read at one bearing only");
        var fixedBearing = terrain.EffectiveAltitudeAt(series.Samples[0].Bearing)!.Value;
        Assert.Contains(series.Samples, sample => Math.Abs(sample.Terrain - fixedBearing) > 5);
        // The same moment and bearing gives the same terrain, whatever the camera does: the camera is not an input.
        Assert.Equal(series.Samples, ExportTargetSeries.From(path, terrain, CelestialRole.DeepSky1).Samples);
    }

    /// <summary>
    /// Above the terrain curve the target is in view; below it, obstructed. The crossings are exactly where the two
    /// drawn curves meet: both straight between samples, crossing at the same interpolated moment and altitude.
    /// </summary>
    [Fact]
    public void ClearsAndBehindTerrainAreWhereTheDrawnCurvesMeet()
    {
        var series = new ExportTargetSeries("Test", CelestialRole.DeepSky1,
            [Sample(0, 100, 2, 8), Sample(10, 110, 10, 6), Sample(20, 120, 14, 9), Sample(30, 130, 8, 12)]);
        Assert.Equal([false, true, true, false], series.Samples.Select(sample => sample.IsVisible));
        var crossings = series.Crossings().Where(crossing => crossing.Kind is ExportCrossingKind.ClearsTerrain or ExportCrossingKind.BehindTerrain).ToArray();
        Assert.Equal([ExportCrossingKind.ClearsTerrain, ExportCrossingKind.BehindTerrain], crossings.Select(crossing => crossing.Kind));
        foreach (var crossing in crossings)
        {
            var i = series.Samples.Count(sample => sample.Time <= crossing.Time);
            var (a, b) = (series.Samples[i - 1], series.Samples[i]);
            var fraction = (crossing.Time - a.Time).TotalMinutes / (b.Time - a.Time).TotalMinutes;
            var pitch = a.Pitch + (b.Pitch - a.Pitch) * fraction;
            var horizon = a.Terrain + (b.Terrain - a.Terrain) * fraction;
            Assert.Equal(pitch, horizon, 9);
            Assert.Equal(pitch, crossing.Pitch, 9);
        }
        Assert.Equal(OctoberEvening + Duration.FromMinutes(6), crossings[0].Time);
    }

    // ---- Direction on the time axis --------------------------------------------------------------------------

    /// <summary>
    /// Every three hours the time axis gives the time and, under it, the target's bearing at that moment as a compass
    /// point and degrees — the astronomy's own azimuth then, to the degree shown.
    /// </summary>
    [AvaloniaFact]
    public async Task TheTimeAxisSaysWhichWayToFaceAtEachTick()
    {
        var exporter = new FakeExporter();
        var (vm, window, _) = await CreatePlannerAsync(exporter: exporter);
        try
        {
            var path = await RealPathAsync("M31", Wales, OctoberEvening);
            var zone = new TimeZoneResolver().Resolve("Europe/London");
            var snapshot = await CapturedSheetAsync(vm, exporter) with
            {
                Target = ExportTargetSeries.From(path, null, CelestialRole.DeepSky1),
                WindowStart = OctoberEvening, WindowEnd = OctoberEvening + IPlanningService.TimelineLength, Zone = zone
            };
            var chart = RenderedChart(snapshot);
            var ticks = chart.Ticks;
            Assert.Equal(7, ticks.Count); // 21:00 to 15:00: the window ends at 18:00, its own edge.
            Assert.Equal(new[] { "21:00", "Thu 8 Oct", "03:00", "06:00", "09:00", "12:00", "15:00" }, ticks.Take(7).Select(tick => tick.Clock));
            var astronomy = new AstronomyEngineService(new TimeZoneResolver());
            foreach (var tick in ticks)
            {
                Assert.Equal(0, (tick.Time - OctoberEvening).TotalMinutes % 180 % 60);
                var exact = astronomy.Calculate(path.Target, Wales, tick.Time, tick.Time.InZone(zone).Date, "Europe/London").Horizontal.AzimuthDegrees;
                Assert.Equal(ExportTargetSeries.Compass(exact), tick.Bearing);
            }
        }
        finally { Close(window); }
    }

    /// <summary>On a narrow chart the bearings give way first, every other one left out; every time stays.</summary>
    [AvaloniaFact]
    public async Task BearingsThinBeforeTheyCrowdAndTimesAlwaysStay()
    {
        var exporter = new FakeExporter();
        var (vm, window, _) = await CreatePlannerAsync(exporter: exporter);
        try
        {
            var snapshot = await CapturedSheetAsync(vm, exporter) with
            {
                Target = ExportTargetSeries.From(await RealPathAsync("M31", Wales, OctoberEvening), null, CelestialRole.DeepSky1),
                WindowStart = OctoberEvening, WindowEnd = OctoberEvening + IPlanningService.TimelineLength
            };
            var wide = new ExportDirectionChart(snapshot);
            wide.Measure(new Size(1500, 400)); wide.Arrange(new Rect(0, 0, 1500, 400));
            Assert.All(wide.LayOutTicks(), tick => Assert.NotNull(tick.Bearing));
            var narrow = new ExportDirectionChart(snapshot);
            narrow.Measure(new Size(420, 400)); narrow.Arrange(new Rect(0, 0, 420, 400));
            var ticks = narrow.LayOutTicks();
            Assert.Equal(wide.LayOutTicks().Select(tick => tick.Clock), ticks.Select(tick => tick.Clock));
            Assert.Contains(ticks, tick => tick.Bearing is null);
            Assert.Contains(ticks, tick => tick.Bearing is not null);
        }
        finally { Close(window); }
    }

    /// <summary>The compass point is the nearest of eight, always with the degrees, and agrees with them at every boundary.</summary>
    [Theory]
    [InlineData(0, "N 0°")]
    [InlineData(359.6, "N 0°")]
    [InlineData(360, "N 0°")]
    [InlineData(-45, "NW 315°")]
    [InlineData(22.4, "N 22°")]
    [InlineData(22.5, "NE 23°")]
    [InlineData(67.4, "NE 67°")]
    [InlineData(67.5, "E 68°")]
    [InlineData(135, "SE 135°")]
    [InlineData(177, "S 177°")]
    [InlineData(224, "SW 224°")]
    [InlineData(268, "W 268°")]
    [InlineData(337.4, "NW 337°")]
    [InlineData(337.6, "N 338°")]
    public void TheCompassPointAgreesWithItsDegrees(double bearing, string expected) =>
        Assert.Equal(expected, ExportTargetSeries.Compass(bearing));

    /// <summary>Between samples the bearing is taken the short way round, so across North it stays near North.</summary>
    [Fact]
    public void BearingBetweenSamplesTakesTheShortWayRound()
    {
        var series = new ExportTargetSeries("Test", CelestialRole.DeepSky1, [Sample(0, 350, 10), Sample(10, 10, 10)]);
        Assert.Equal(0, series.BearingAt(OctoberEvening + Duration.FromMinutes(5))!.Value, 9);
        Assert.Equal(355, series.BearingAt(OctoberEvening + Duration.FromMinutes(2.5))!.Value, 9);
        Assert.Null(series.BearingAt(OctoberEvening - Duration.FromMinutes(1)));
    }

    /// <summary>One axis: pitch. Nothing plots the bearing, so nothing is left that could draw a line across North.</summary>
    [Fact]
    public void TheChartHasOnePitchAxisAndNoBearingLine()
    {
        var members = typeof(ExportDirectionChart).GetMembers(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(members, member => member.Name is "BearingPen" or "BearingDash" or "BearingY" or "BearingLines" or "BearingScale");
        Assert.Null(typeof(ExportTargetSeries).GetMethod("BearingSegments"));
        Assert.DoesNotContain("Export_Chart_Bearing", File.ReadAllText(Path.Combine(TestPaths.DesktopProject, "Export", "ExportDirectionChart.cs")));
    }

    // ---- The sheet, for people -------------------------------------------------------------------------------

    private static ExportDirectionChart RenderedChart(PlannerExportSnapshot snapshot)
    {
        var sheet = PlannerExportRenderer.Compose(snapshot);
        sheet.Measure(new Size(PlannerExportRenderer.Width, PlannerExportRenderer.Height));
        sheet.Arrange(new Rect(0, 0, PlannerExportRenderer.Width, PlannerExportRenderer.Height));
        using var bitmap = new RenderTargetBitmap(new PixelSize(PlannerExportRenderer.Width, PlannerExportRenderer.Height));
        bitmap.Render(sheet);
        return Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(sheet).OfType<ExportDirectionChart>().Single();
    }

    /// <summary>Hills to the south-east, about 14° at their highest, and a low 3° horizon everywhere else.</summary>
    private static TerrainHorizonProfile HillsToTheSouthEast(GeoCoordinate observer) => new(observer,
        Enumerable.Range(0, 360).Select(bearing => new TerrainHorizonSample(bearing,
            bearing is >= 100 and <= 170 ? 14 - Math.Abs(bearing - 135) / 8.0 : 3, 2000)).ToArray(),
        HasDemCoverage: true, Status: "Test hills", GeneratedAt: OctoberEvening);

    /// <summary>A valley open to the south: a low horizon south, a high ridge to the north (up to 22° due North).</summary>
    private static TerrainHorizonProfile ValleyOpenToTheSouth(GeoCoordinate observer) => new(observer,
        Enumerable.Range(0, 360).Select(bearing => new TerrainHorizonSample(bearing,
            2 + 20 * Math.Pow(Math.Cos(bearing * Math.PI / 360), 2), 2000)).ToArray(),
        HasDemCoverage: true, Status: "Test valley", GeneratedAt: OctoberEvening);

    /// <summary>
    /// The manual review cases, rendered with real astronomy over a real 24-hour window: A, M31 overnight with no
    /// terrain; B, a circumpolar target whose bearing passes North; C, M31 in a valley whose horizon changes strongly as
    /// the target's bearing changes; D, M42 behind hills for part of the window; E, the dark theme; F, a colour-vision
    /// mode. Written to artifacts/plan-images.
    /// </summary>
    [AvaloniaFact]
    public async Task TheReviewCasesAreRendered()
    {
        var exporter = new FakeExporter();
        var service = ((App)Application.Current!).Themes!;
        var (vm, window, _) = await CreatePlannerAsync(exporter: exporter);
        var output = Path.Combine(Path.GetDirectoryName(AppearancePolishTests.Output)!, "plan-images");
        Directory.CreateDirectory(output);
        foreach (var old in Directory.GetFiles(output, "*.png")) File.Delete(old);
        try
        {
            var catalogue = new OpenNgcTargetCatalogue();
            var zones = new TimeZoneResolver();
            var planning = new PlanningService(catalogue, new AstronomyEngineService(zones), new LensCalculator(), null!, null!, zones);
            var session = PlanningSession.Default(OctoberEvening, "Europe/London") with { Observer = Wales };
            var events = await planning.CalculateTimelineEventsAsync(session, OctoberEvening, CancellationToken.None);
            var zone = zones.Resolve("Europe/London");
            async Task Render(string name, string id, TerrainHorizonProfile? terrain, AppearanceMode appearance, ColourVisionMode mode)
            {
                service.Apply(new(ColourVisionMode: mode, AppearanceMode: appearance));
                var captured = await CapturedSheetAsync(vm, exporter);
                var target = ExportTargetSeries.From(await RealPathAsync(id, Wales, OctoberEvening), terrain, CelestialRole.DeepSky1);
                var snapshot = captured with
                {
                    LocationName = "Near Llanbrynmair", Coordinates = Wales.ToString(), Target = target, Events = events,
                    WindowStart = OctoberEvening, WindowEnd = OctoberEvening + IPlanningService.TimelineLength, Zone = zone,
                    Subject = Noctaxis.Core.Localisation.Strings.Format("Export_Sheet_Subject_Format", target.Name, OctoberEvening.InZone(zone).ToDateTimeUnspecified(),
                        (OctoberEvening + IPlanningService.TimelineLength).InZone(zone).ToDateTimeUnspecified(),
                        TimeZoneDisplay.Describe(zone, OctoberEvening).ShortName),
                    Theme = ExportTheme.Current()
                };
                Assert.Contains("BST", snapshot.Subject);
                var png = new PlannerExportRenderer().RenderPng(snapshot);
                await File.WriteAllBytesAsync(Path.Combine(output, name + ".png"), NoctaxisPlanImage.Embed(png, snapshot.Plan));
            }
            await Render("A-m31-overnight", "M31", null, AppearanceMode.Light, ColourVisionMode.None);
            await Render("B-ngc869-bearing-crosses-north", "NGC 869", null, AppearanceMode.Dark, ColourVisionMode.None);
            await Render("C-m31-valley-horizon-follows-bearing", "M31", ValleyOpenToTheSouth(Wales), AppearanceMode.Dark, ColourVisionMode.None);
            await Render("D-m42-behind-hills", "M42", HillsToTheSouthEast(Wales), AppearanceMode.Dark, ColourVisionMode.None);
            await Render("E-m31-dark-theme", "M31", HillsToTheSouthEast(Wales), AppearanceMode.Dark, ColourVisionMode.None);
            await Render("F-m31-deuteranopia", "M31", ValleyOpenToTheSouth(Wales), AppearanceMode.Dark, ColourVisionMode.Deuteranopia);
            Assert.Equal(6, Directory.GetFiles(output, "*.png").Length);
        }
        finally { service.Apply(new()); Close(window); }
    }
}
