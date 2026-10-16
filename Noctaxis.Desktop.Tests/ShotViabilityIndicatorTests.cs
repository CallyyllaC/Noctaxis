using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Viability;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Export;
using Noctaxis.Desktop.Views;
using Noctaxis.Desktop.ViewModels;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The Camera frame's shot-viability indicator (docs/shot-viability-scoring.md): a compact score in the frame's
/// header, its warnings, and the details behind it. Advisory only, and never describing a previous place, time
/// or target.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static ShotViabilityIndicatorParts ViabilityParts(Window window) => new(
        window.FindControl<Button>("ShotViabilityButton")!,
        window.FindControl<StatusText>("ShotViabilityIndicator")!,
        window.FindControl<FrameHeaderPanel>("PlannerCameraFrameHeader")!);

    private sealed record ShotViabilityIndicatorParts(Button Button, StatusText Indicator, FrameHeaderPanel Header)
    {
        public Flyout Flyout => (Flyout)Button.Flyout!;
    }

    /// <summary>Points the camera straight at the plan's target, as Focus does.</summary>
    private static void AimAtTarget(MainViewModel vm)
    {
        var target = vm.Snapshot!.Position.Horizontal;
        vm.CameraBearingDegrees = target.AzimuthDegrees;
        vm.CameraPitchDegrees = target.AltitudeDegrees;
    }

    // ---- Where it is ---------------------------------------------------------------------------------------

    /// <summary>
    /// The indicator is in the Camera frame's own header, after the heading and before the legend and the
    /// resize grip, and its details are behind it, not on screen. Nothing anywhere is enabled or disabled by
    /// the score: it is advisory.
    /// </summary>
    [Fact]
    public void TheShotViabilityIndicatorIsInTheCameraFramesHeader()
    {
        var frame = TerrainFrameMarkup();
        var header = frame.Descendants().Single(element => NameOf(element) == "PlannerCameraFrameHeader");
        Assert.Equal("FrameHeaderPanel", header.Name.LocalName);
        Assert.Equal(["", "ShotViabilityButton", "PlannerTerrainLegendButton", "PlannerCameraFrameResizeGrip"],
            header.Elements().Select(NameOf));
        var button = header.Elements().ElementAt(1);
        Assert.Equal("{Binding ShotViabilityAccessibleName}", button.Attributes().Single(attribute => attribute.Name.LocalName == "AutomationProperties.Name").Value);
        var flyout = button.Descendants().Single(element => element.Name.LocalName == "Flyout");
        Assert.Single(flyout.Descendants(), element => NameOf(element) == "ShotViabilityMetricList");
        var window = TestMarkup.LoadMainWindow();
        Assert.DoesNotContain(window.Descendants().SelectMany(element => element.Attributes()), attribute =>
            attribute.Name.LocalName is "IsEnabled" or "IsHitTestVisible" or "IsReadOnly" && attribute.Value.Contains("ShotViability"));
    }

    // ---- What it says --------------------------------------------------------------------------------------

    /// <summary>
    /// The header shows the score and its rating with the icon of its most severe warning, and follows the
    /// camera: pointing away from the target turns it critical at once, pointing back clears it.
    /// </summary>
    [AvaloniaFact]
    public async Task TheIndicatorShowsTheScoreAndFollowsTheCamera()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            await vm.WaitForPlannerRefreshAsync();
            AimAtTarget(vm);
            Settle(window);
            var parts = ViabilityParts(window);
            var assessment = Assert.IsType<ShotViabilityAssessment>(vm.ShotViability);
            Assert.Equal(vm.Snapshot!.Position.Target.Id, assessment.Target.Id);
            Assert.Equal(assessment.RoundedScore.ToString(), vm.ShotViabilityScoreText);
            Assert.Equal($"· {Noctaxis.Core.Localisation.Strings.Get("Planning_Viability_Rating_" + assessment.Rating)}", vm.ShotViabilityRatingText);
            Assert.Equal(vm.ShotViabilityScoreText, parts.Indicator.TextBlockForTesting.Text);
            Assert.Equal(vm.ShotViabilityRatingText, window.FindControl<TextBlock>("ShotViabilityRating")!.Text);
            Assert.Equal(ShotViabilitySeverity.Good, assessment[ShotViabilityMetric.FramePosition].Severity);
            Assert.Equal(vm.ShotViabilityAccessibleName, AutomationProperties.GetName(parts.Button));
            Assert.StartsWith($"Shot viability {assessment.RoundedScore} out of 100", vm.ShotViabilityAccessibleName);
            Assert.StartsWith(vm.ShotViabilityTitle, (string)ToolTip.GetTip(parts.Button)!);
            var warningsBefore = vm.ShotViabilityWarningCount;

            // Turned right round: the target has left the frame.
            vm.CameraBearingDegrees = Angles.NormaliseDegrees(vm.CameraBearingDegrees + 180);
            Settle(window);
            var away = vm.ShotViability!;
            Assert.NotSame(assessment, away);
            Assert.Equal(ShotViabilitySeverity.Critical, away[ShotViabilityMetric.FramePosition].Severity);
            Assert.Equal(warningsBefore + 1, vm.ShotViabilityWarningCount);
            Assert.Equal(StatusKind.Error, vm.ShotViabilityStatusKind);
            Assert.Equal(StatusKind.Error, parts.Indicator.Kind);
            Assert.Contains("1 critical issue", AutomationProperties.GetName(parts.Button));

            AimAtTarget(vm);
            Settle(window);
            Assert.Equal(ShotViabilitySeverity.Good, vm.ShotViability![ShotViabilityMetric.FramePosition].Severity);
            Assert.Equal(warningsBefore, vm.ShotViabilityWarningCount);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// The details open from a tap or click and from the keyboard. They list every metric that applies, each as its
    /// name and one explanation with its status icon, each a stop for the keyboard and a reader; no points and no
    /// repeated raw values. The advisory note is behind an info button, not a paragraph. Escape closes them.
    /// </summary>
    [AvaloniaFact]
    public async Task TheDetailsOpenFromATapOrTheKeyboardAndListEveryMetric()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            await vm.WaitForPlannerRefreshAsync();
            AimAtTarget(vm);
            Settle(window);
            var parts = ViabilityParts(window);
            Assert.True(parts.Button.IsEffectivelyVisible && parts.Button.IsEffectivelyEnabled && parts.Button.Focusable);
            Assert.True(parts.Button.Bounds.Height >= 28, $"the indicator is {parts.Button.Bounds.Size}");
            Assert.False(parts.Flyout.IsOpen);

            var centre = Centre(parts.Button, window);
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
            Settle(window);
            Assert.True(parts.Flyout.IsOpen, "a click should open the details");
            var details = Assert.IsType<ScrollViewer>(parts.Flyout.Content);
            Assert.Equal("ShotViabilityDetails", details.Name);
            var list = details.GetVisualDescendants().OfType<ItemsControl>().Single(control => control.Name == "ShotViabilityMetricList");
            var rows = list.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("viabilityRow")).ToArray();

            // Every metric that applies, in order, and none that does not (the plan's target here is the Sun).
            var applicable = vm.ShotViability!.Metrics.Where(metric => metric.Status != ShotViabilityMetricStatus.NotApplicable).ToArray();
            Assert.Contains(vm.ShotViability.Metrics, metric => metric.Status == ShotViabilityMetricStatus.NotApplicable);
            Assert.Equal(applicable.Select(metric => metric.Id), vm.ShotViabilityRows.Select(row => row.Id));
            Assert.Equal(applicable.Length, rows.Length);
            for (var index = 0; index < rows.Length; index++)
            {
                var row = vm.ShotViabilityRows[index];
                Assert.True(rows[index].Focusable, $"{row.Name} should be a keyboard stop");
                Assert.Equal(row.AccessibleText, AutomationProperties.GetName(rows[index]));
                Assert.StartsWith(row.Name + ": " + row.SeverityLabel + ". ", row.AccessibleText);
                // Its name and its explanation (with the status icon), and nothing else: no points, no raw value line.
                var texts = rows[index].GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text))
                    .Select(text => text.Text!).ToArray();
                Assert.Equal([row.Name, row.Explanation], texts);
                var status = Assert.Single(rows[index].GetVisualDescendants().OfType<StatusText>());
                Assert.Equal(row.Kind, status.Kind);
            }
            var shown = details.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? "").ToArray();
            Assert.DoesNotContain(shown, text => text.Contains(" pts", StringComparison.Ordinal) || text.Contains("of 100.0", StringComparison.Ordinal));
            // The title repeats the score, out of 100, with its rating; the policy is named.
            var title = details.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "ShotViabilityDetailsTitle");
            Assert.Matches(@"^Shot viability · \d{1,3}/100 · \S", title.Text);
            Assert.Contains("Scoring policy shot-viability-v2", shown);

            // The advisory note: no paragraph, but an info button beside the title, and the indicator's help text.
            const string advisory = "Viability scores are advisory estimates. They do not restrict planning or exporting.";
            Assert.DoesNotContain(shown, text => text.Contains("advisory", StringComparison.OrdinalIgnoreCase));
            var info = details.GetVisualDescendants().OfType<InfoButton>().Single(button => button.Name == "ShotViabilityAdvisory");
            Assert.Equal(advisory, info.Text);
            Assert.Equal("About shot viability", AutomationProperties.GetName(info));
            Assert.True(info.Focusable && info.IsEffectivelyEnabled);
            Assert.Equal(advisory, AutomationProperties.GetHelpText(parts.Button));
            Assert.EndsWith(advisory, (string)ToolTip.GetTip(parts.Button)!);

            // Escape closes it.
            rows[0].Focus(NavigationMethod.Tab);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Settle(window);
            Assert.False(parts.Flyout.IsOpen, "Escape should close the details");

            // The keyboard opens it too.
            Assert.True(parts.Button.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Settle(window);
            Assert.True(parts.Flyout.IsOpen, "Enter on the focused indicator should open the details");
            parts.Flyout.Hide();
        }
        finally { Close(window); }
    }

    /// <summary>
    /// The details take their preferred size in an ordinary window, with every explanation inside them, and shrink
    /// to a small window, scrolling rather than running off it; at the largest text nothing is clipped sideways.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 820, 1.0)]
    [InlineData(760, 560, 1.0)]
    [InlineData(720, 520, 2.0)]
    public async Task TheDetailsFitTheWindowAndScrollWhenItIsSmall(double width, double height, double textScale)
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            window.Width = width; window.Height = height;
            if (textScale != 1) await vm.ApplyAppearanceAsync(new("builtin.noctaxis", TextScale: textScale));
            await vm.WaitForPlannerRefreshAsync();
            Settle(window);
            var parts = ViabilityParts(window);
            parts.Flyout.ShowAt(parts.Button);
            Settle(window);
            Settle(window);
            var details = Assert.IsType<ScrollViewer>(parts.Flyout.Content);
            var expectedWidth = Math.Max(240, Math.Min(MainWindow.ShotViabilityDetailsWidth, window.ClientSize.Width - 48));
            var expectedHeight = Math.Max(160, Math.Min(MainWindow.ShotViabilityDetailsHeight, window.ClientSize.Height - 96));
            Assert.Equal(expectedWidth, details.Bounds.Width, 0);
            Assert.True(details.Bounds.Height <= expectedHeight + 0.5, $"the details are {details.Bounds.Height:F0} tall in {expectedHeight:F0}");
            if (width >= 1000) Assert.Equal(MainWindow.ShotViabilityDetailsWidth, details.Bounds.Width, 0); // Its preferred width.
            // Everything is inside the details' width: wrapping, never clipped sideways.
            var content = details.GetVisualDescendants().OfType<StackPanel>().First(panel => panel.Name == "ShotViabilityDetailsContent");
            foreach (var text in content.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && text.Bounds.Width > 0))
            {
                var at = text.TranslatePoint(default, details)!.Value;
                Assert.True(at.X >= -0.5 && at.X + text.Bounds.Width <= details.Bounds.Width + 0.5,
                    $"'{text.Text}' runs from {at.X:F0} to {at.X + text.Bounds.Width:F0} in {details.Bounds.Width:F0}");
            }
            // Taller than the room: it scrolls, and the last metric can be reached.
            if (details.Extent.Height > details.Viewport.Height + 0.5)
            {
                details.Offset = new Vector(0, details.Extent.Height);
                Settle(window);
                Assert.True(details.Offset.Y > 0, "a small window's details should scroll");
            }
            parts.Flyout.Hide();
        }
        finally { Close(window); }
    }

    /// <summary>
    /// The header gives up the rating before anything else, then moves the indicator to its own line: at the
    /// widths a card can have, with stand-in parts of fixed size.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(300, false, 2)] // Room for everything.
    [InlineData(192, false, 1)] // The default card: the score stays beside the heading, the rating goes.
    [InlineData(140, true, 2)]  // Not even the score fits: its own line, where the rating fits again.
    public void TheHeaderDropsTheRatingBeforeItWraps(double width, bool wrapped, int shown)
    {
        // Sized by their content, as text is, so a part left out can be given no size.
        static Border Part(double partWidth) => new() { Child = new Border { Width = partWidth, Height = 16 } };
        var tail = new OptionalTailPanel { Spacing = 3, Children = { Part(40), Part(50) } };
        var indicator = new Button { Content = tail, Padding = new Thickness(4, 2) };
        var header = new FrameHeaderPanel { Width = width, Children = { Part(71), indicator, Part(32), Part(21) } };
        var window = new Window { Content = header, Width = 400, Height = 200 };
        window.Show();
        try
        {
            Settle(window);
            Assert.Equal(wrapped, header.IsIndicatorWrapped);
            Assert.Equal(shown, tail.ShownCount);
            // Whatever is left out is placed past the clipped row, never over the score; whatever is shown stays inside the header.
            Assert.True(tail.ClipToBounds);
            Assert.Equal(shown == 2, tail.Children[1].Bounds.Width > 0 && tail.Children[1].Bounds.Left < tail.Bounds.Width);
            foreach (var child in header.Children)
                Assert.True(child.Bounds.Right <= width + 0.5, $"{child.GetType().Name} ends at {child.Bounds.Right:F1} of {width}");
            Assert.Equal(width - 53, header.Children[2].Bounds.Left, 1);
            if (wrapped) Assert.True(indicator.Bounds.Top >= header.Children[0].Bounds.Bottom - 0.5);
            else Assert.Equal(header.Children[2].Bounds.Left, indicator.Bounds.Right, 1);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// In the Planner: at the default size the score shares the heading's line; at the largest text the
    /// indicator takes its own line under the heading. The heading, the legend and the grip keep their room, and
    /// nothing leaves the card.
    /// </summary>
    [AvaloniaFact]
    public async Task OnANarrowCardWithLargeTextTheIndicatorTakesItsOwnLine()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            await vm.WaitForPlannerRefreshAsync();
            Settle(window);
            var parts = ViabilityParts(window);
            var legend = window.FindControl<Button>("PlannerTerrainLegendButton")!;
            var card = window.FindControl<Border>("PlannerTerrainFrame")!;
            Assert.False(parts.Header.IsIndicatorWrapped, "at the default size and text the score shares the heading's line: " +
                string.Join(", ", parts.Header.Children.Select(child => $"{child.GetType().Name} {child.DesiredSize.Width:F1}")) +
                $" in {parts.Header.Bounds.Width:F1}");
            Assert.Equal(parts.Button.Bounds.Center.Y, legend.Bounds.Center.Y, 0);
            Assert.True(parts.Indicator.Bounds.Width > 0);

            foreach (var scale in new[] { 1.5, 2.0 })
            {
                await vm.ApplyAppearanceAsync(new("builtin.noctaxis", TextScale: scale));
                Settle(window);
                Settle(window);
                foreach (var control in new Control[] { parts.Button, legend })
                {
                    var at = control.TranslatePoint(default, card)!.Value;
                    Assert.True(at.X >= -0.5 && at.X + control.Bounds.Width <= card.Bounds.Width + 0.5,
                        $"{control.Name} at {scale}× runs from {at.X:F1} to {at.X + control.Bounds.Width:F1} in a card {card.Bounds.Width:F1} wide");
                }
                Assert.True(legend.Bounds.Width >= 32 && legend.Bounds.Height >= 28, $"the legend button is {legend.Bounds.Size}");
                if (parts.Header.IsIndicatorWrapped)
                    Assert.True(parts.Button.Bounds.Top >= legend.Bounds.Bottom - 8, "a wrapped indicator sits under the heading row");
            }
            // At twice the text size the line cannot hold the rating as well: it is given up first, or the
            // indicator moves to its own line; the score itself is always shown.
            var tail = window.FindControl<OptionalTailPanel>("ShotViabilityTail")!;
            Assert.True(parts.Header.IsIndicatorWrapped || tail.ShownCount == 1, "at twice the text size something must give");
            Assert.True(parts.Indicator.Bounds.Width > 0 && parts.Indicator.IsEffectivelyVisible);
        }
        finally { Close(window); }
    }

    /// <summary>The indicator's icon and text take the theme's own status colours, and follow a theme change at once.</summary>
    [AvaloniaFact]
    public async Task TheIndicatorTakesTheThemesStatusColours()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            await vm.WaitForPlannerRefreshAsync();
            vm.CameraBearingDegrees = Angles.NormaliseDegrees(vm.Snapshot!.Position.Horizontal.AzimuthDegrees + 180);
            Settle(window);
            var indicator = ViabilityParts(window).Indicator;
            Assert.Equal(StatusKind.Error, indicator.Kind);
            foreach (var (theme, mode) in new[] { ("builtin.noctaxis", AppearanceMode.Dark), ("builtin.highcontrast", AppearanceMode.Light),
                         ("builtin.noctaxis", AppearanceMode.Light) })
            {
                await vm.ApplyAppearanceAsync(new(theme, AppearanceMode: mode));
                Dispatcher.UIThread.RunJobs();
                Settle(window);
                Assert.True(Application.Current!.TryFindResource("Error", window.ActualThemeVariant, out var error));
                var expected = Assert.IsAssignableFrom<ISolidColorBrush>(error).Color;
                Assert.Equal(expected, Assert.IsAssignableFrom<ISolidColorBrush>(indicator.IconForTesting.Foreground).Color);
                Assert.Equal(expected, Assert.IsAssignableFrom<ISolidColorBrush>(indicator.TextBlockForTesting.Foreground).Color);
            }
            // Not colour alone: each severity has its own icon shape.
            Assert.Equal(4, Enum.GetValues<StatusKind>().Select(kind => new StatusText { Kind = kind }.IconForTesting.Data).Distinct().Count());
        }
        finally { Close(window); }
    }

    // ---- Never stale ---------------------------------------------------------------------------------------

    private static ShotWindowPaths ConstantPaths(PlanningSession session, Instant start, Instant end) => new(session.TargetId, session.Observer,
        start, end,
        Enumerable.Range(0, (int)((end - start).TotalMinutes / 10) + 1).Select(step => new AstralPathSample(start + Duration.FromMinutes(10 * step), new(180, 60))).ToArray(),
        Enumerable.Range(0, (int)((end - start).TotalMinutes / 10) + 1).Select(step => new AstralPathSample(start + Duration.FromMinutes(10 * step), new(0, -30))).ToArray());

    /// <summary>
    /// The window paths are asked for once per place and target, while the moment stays in their span; a reply
    /// for a place the plan has left is discarded, and the assessment always describes the place, time and
    /// target on screen, however quickly they change. Runs on the headless UI dispatcher: it pumps
    /// Dispatcher.UIThread, which only its owning thread may do (as a plain [Fact] it failed whenever an earlier
    /// test in the same process had bound the dispatcher to another thread).
    /// </summary>
    [AvaloniaFact]
    public async Task RapidChangesNeverShowAStaleAssessment()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var replies = new List<(PlanningSession Session, Instant Start, Instant End, TaskCompletionSource<ShotWindowPaths?> Reply)>();
        planning.ShotWindowPathsFor = (session, start, end, _) =>
        {
            var reply = new TaskCompletionSource<ShotWindowPaths?>(TaskCreationOptions.RunContinuationsAsynchronously);
            replies.Add((session, start, end, reply));
            return reply.Task;
        };
        var vm = CreateViewModel(planning, catalogue, CreatePlannerStore(new AppSettings()), new FakeExporter());
        await vm.InitializeAsync();
        await vm.WaitForPlannerRefreshAsync();
        var m31 = catalogue.Get("M31");
        vm.AddCelestialObjectCommand.Execute(m31);
        vm.CelestialObjects.Single(item => item.TargetId == m31.Id).FocusCommand.Execute(null);
        await vm.WaitForPlannerRefreshAsync();
        Assert.Equal(m31.Id, vm.ShotViability!.Target.Id);
        var first = Assert.Single(replies);
        Assert.Equal(m31.Id, first.Session.TargetId);
        Assert.Contains("Calculating", vm.ShotViability[ShotViabilityMetric.UsefulWindow].Explanation);

        // Moved before the first reply: a second request, for the new place.
        var elsewhere = new GeoCoordinate(57.1, -4.2, 300);
        vm.MoveObserver(elsewhere);
        await vm.WaitForPlannerRefreshAsync();
        Assert.Equal(2, replies.Count);
        Assert.Equal(elsewhere.Latitude, replies[1].Session.Observer.Latitude);

        // The first place's reply lands late: discarded.
        first.Reply.SetResult(ConstantPaths(first.Session, first.Start, first.End));
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ShotViabilityMetricStatus.Unavailable, vm.ShotViability![ShotViabilityMetric.UsefulWindow].Status);

        // The current place's reply is used.
        replies[1].Reply.SetResult(ConstantPaths(replies[1].Session, replies[1].Start, replies[1].End));
        await vm.ShotWindowCalculationForTesting;
        var window = vm.ShotViability![ShotViabilityMetric.UsefulWindow];
        Assert.True(window.IsEvaluated, window.Explanation);
        Assert.Contains("At least 12.0 h", window.Explanation);

        // Moving the time within the paths' span asks for nothing new and still scores the window.
        vm.MinutesOfDay = (vm.MinutesOfDay + 120) % (24 * 60);
        await vm.WaitForPlannerRefreshAsync();
        Assert.Equal(2, replies.Count);
        Assert.True(vm.ShotViability![ShotViabilityMetric.UsefulWindow].IsEvaluated);

        // Rapid camera changes: each assessment is the camera's as it is now.
        for (var bearing = 0; bearing < 360; bearing += 7)
        {
            vm.CameraBearingDegrees = bearing;
            var expected = ShotViabilityScorer.Assess(ShotViabilityPlanning.FromSnapshot(vm.Snapshot!,
                new ShotViabilityCamera(bearing, vm.CameraPitchDegrees, vm.Snapshot!.FieldOfView.HorizontalDegrees, vm.Snapshot.FieldOfView.VerticalDegrees),
                null, vm.SelectedCamera, null, false));
            Assert.Equal(expected[ShotViabilityMetric.FramePosition].Score, vm.ShotViability![ShotViabilityMetric.FramePosition].Score);
        }

        // A new focus is the assessment's subject at once: the Moon, with no window to wait for.
        vm.CelestialObjects.Single(item => item.TargetId == "moon").FocusCommand.Execute(null);
        await vm.WaitForPlannerRefreshAsync();
        Assert.Equal("moon", vm.ShotViability!.Target.Id);
        Assert.Equal(ShotViabilityMetricStatus.NotApplicable, vm.ShotViability[ShotViabilityMetric.UsefulWindow].Status);
        Assert.Equal(2, replies.Count);
    }

    /// <summary>With nothing focused there is nothing to score, and the indicator says so rather than guessing.</summary>
    [AvaloniaFact]
    public async Task WithNothingFocusedThereIsNoScore()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            await vm.WaitForPlannerRefreshAsync();
            Assert.NotNull(vm.ShotViability);
            vm.ImportPlanImage(PlanImageSource.Of(PlanPng(vm.CurrentPlan() with { Focus = null })));
            Settle(window);
            await vm.WaitForPlannerRefreshAsync();
            Settle(window);
            Assert.DoesNotContain(vm.CelestialObjects, item => item.IsPrimary);
            Assert.Null(vm.ShotViability);
            Assert.Equal("—", vm.ShotViabilityScoreText);
            Assert.Equal(string.Empty, vm.ShotViabilityRatingText);
            Assert.Equal("Focus a target to assess the shot.", vm.ShotViabilitySubject);
            Assert.Equal(StatusKind.Information, vm.ShotViabilityStatusKind);
            Assert.Empty(vm.ShotViabilityRows);
            var parts = ViabilityParts(window);
            Assert.Equal("—", parts.Indicator.TextBlockForTesting.Text);
            Assert.Equal("Shot viability not assessed. Show details", AutomationProperties.GetName(parts.Button));
            // The indicator still opens, to say why.
            Assert.True(parts.Button.IsEffectivelyEnabled);
        }
        finally { Close(window); }
    }
}
