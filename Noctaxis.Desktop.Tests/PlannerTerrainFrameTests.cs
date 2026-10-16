using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Controls.Docking;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The Terrain frame: the camera's view of the terrain, where the camera points and the two readings it
/// gives, laid over the map's bottom-left as a static instrument rather than docked as a panel.
/// </summary>
public sealed partial class MainViewModelTests
{
    /// <summary>
    /// What the Terrain frame does not show: the pin's coordinates (the map overlay has them), the source
    /// line, the data state and the observer datum (support information, in Settings → Diagnostics).
    /// </summary>
    private static readonly string[] RetiredTerrainBindings =
    [
        "{Binding TerrainStatus}", "{Binding TerrainCurrentLocationText}", "{Binding GroundHorizonState}", "{Binding TerrainDatumText}",
        "{Binding TerrainDebugMap}", "{Binding SettingsTerrainMinimapContext, Mode=TwoWay}"
    ];

    private static System.Xml.Linq.XElement TerrainFrameMarkup() => TestMarkup.LoadMainWindow().Descendants()
        .Single(element => NameOf(element) == "PlannerTerrainFrame");

    private static string NameOf(System.Xml.Linq.XElement element) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Name")?.Value ?? "";

    /// <summary>
    /// The Terrain frame is laid over the map's bottom-left beside the pin's own place: not a panel, so it
    /// cannot be docked, pinned or floated, and the Planner has no Terrain panel or minimap left.
    /// </summary>
    [Fact]
    public void TheTerrainFrameIsAStaticInstrumentOnTheMap()
    {
        var window = TestMarkup.LoadMainWindow();
        var frame = TerrainFrameMarkup();
        Assert.Equal("PlannerMapBottomLeft", NameOf(frame.Parent!));
        Assert.DoesNotContain(frame.Ancestors(), element => element.Name.LocalName == "PlannerPanelHost");
        Assert.DoesNotContain(window.Descendants(), element => element.Name.LocalName == "PlannerPanelHost" && element.Attribute("PanelId")?.Value == "terrain");
        Assert.DoesNotContain(window.Descendants(), element => element.Name.LocalName == "LocalTerrainMap");
        Assert.DoesNotContain(Noctaxis.Desktop.ViewModels.PlannerDockViewModel.DefaultPanels, panel => panel.Layout.PanelId == "terrain");
        var planner = window.Descendants().Single(element => element.Name.LocalName == "TabItem" && element.Attribute("Header")?.Value == "Planner");
        foreach (var binding in RetiredTerrainBindings)
            Assert.DoesNotContain(planner.Descendants(), element => element.Attributes().Any(attribute => attribute.Value == binding));
        // Its camera direction and readings came with it, unchanged.
        foreach (var part in new[] { "CameraDirectionInstrument", "CameraFrameReadings", "PlannerTerrainLegendButton" })
            Assert.Single(frame.Descendants(), element => NameOf(element) == part);
    }

    /// <summary>
    /// The frame's readings fit beside it on the map, the frame as wide as before and still tall enough
    /// to read the skyline across.
    /// </summary>
    [AvaloniaFact]
    public async Task TheTerrainFrameStaysReadableOnTheMap()
    {
        var (_, window, _) = await CreatePlannerAsync();
        try
        {
            var frame = window.GetVisualDescendants().OfType<TerrainFrameView>().Single();
            frame.Depth = new CameraTerrainDepth([345, 0, 15], -10, 10,
                System.Collections.Immutable.ImmutableArray.CreateRange(Enumerable.Repeat(1000d, 3 * 72)));
            Settle(window);
            // The picture itself, in the camera's 3:2 shape, as large as fits in the frame's 208:128 shape at the
            // width the card gives it: here as tall as that shape, and nearly as wide.
            var space = window.FindControl<StackPanel>("PlannerCameraFrameContent")!.Bounds.Width;
            Assert.True(space >= MapInstrumentLayout.MinimumCameraFrameWidth - 18, $"the card gives the frame {space}");
            Assert.InRange(frame.Bounds.Height, TerrainFrameView.HeightFor(space) - 1, TerrainFrameView.HeightFor(space) + 1);
            Assert.InRange(frame.Bounds.Width, space * .9, space + .5);
            var map = window.FindControl<PlannerMapView>("PlannerMap")!;
            var card = window.FindControl<Border>("PlannerTerrainFrame")!;
            var at = card.TranslatePoint(default, map)!.Value;
            Assert.True(at.X >= 0 && at.Y >= 0 && at.Y + card.Bounds.Height <= map.Bounds.Height + 0.5, $"the Terrain frame should be on the map at {at}");
        }
        finally { Close(window); }
    }

    /// <summary>
    /// Horizon and Obstruction describe what the frame shows, so they are in the frame's section, on one
    /// line under the frame, each once in the whole Planner.
    /// </summary>
    [Fact]
    public void HorizonAndObstructionAreOneLineOfReadingsUnderTheTerrainFrame()
    {
        var frame = TerrainFrameMarkup();
        var markup = TestMarkup.LoadMainWindow().ToString();
        foreach (var binding in new[] { "{Binding GroundHorizonAngleText}", "{Binding GroundObstructionText}" })
        {
            Assert.Contains(frame.Descendants(), element => element.Attribute("Text")?.Value == binding);
            Assert.Single(markup.Split(binding).Skip(1));
        }
        var children = frame.Elements().Single().Elements().ToArray();
        int Index(Func<System.Xml.Linq.XElement, bool> match) => Array.FindIndex(children, element => match(element));
        // The frame and the direction control laid along its bottom share one stage.
        var view = Index(element => NameOf(element) == "CameraFrameStage" &&
            element.Elements().Any(child => child.Name.LocalName == "TerrainFrameView"));
        var readings = Index(element => NameOf(element) == "CameraFrameReadings");
        Assert.True(view >= 0 && view < readings, $"frame {view}, readings {readings}");
        // The readings are the section's last line: the legend is no longer under them.
        Assert.Equal(children.Length - 1, readings);
        Assert.Equal(["CameraBearingValue", "CameraPitchValue", "PlannerTerrainHorizon", "PlannerTerrainObstruction"],
            children[readings].Elements().Where(element => element.Name.LocalName == "StackPanel").Select(NameOf));
        Assert.Equal(["Bearing", "Pitch", "Horizon", "Obstruction"], children[readings].Descendants()
            .Where(element => (string?)element.Attribute("Classes") == "terrainReadingLabel" && element.Attribute("Text")?.Value != "·")
            .Select(element => element.Attribute("Text")?.Value));
        // Their tooltips explain what the numbers are; neither repeats its label.
        Assert.Contains("along the camera's bearing", Noctaxis.Core.Localisation.Strings.Get("Planner_Terrain_Horizon_Tooltip"));
        Assert.Contains("along the camera's bearing", Noctaxis.Core.Localisation.Strings.Get("Planner_Terrain_Obstruction_Tooltip"));
    }

    /// <summary>
    /// At the normal text size the two readings share one line, at their widest ordinary values too, and
    /// both stay inside the section.
    /// </summary>
    [AvaloniaFact]
    public async Task HorizonAndObstructionShareOneLine()
    {
        var (_, window, _) = await CreatePlannerAsync();
        try
        {
            var horizon = window.FindControl<StackPanel>("PlannerTerrainHorizon")!;
            var obstruction = window.FindControl<StackPanel>("PlannerTerrainObstruction")!;
            // Wide values as the profile formats them, standing in for a live terrain profile.
            horizon.Children.OfType<TextBlock>().Last().Text = "+12.5°";
            obstruction.Children.OfType<TextBlock>().Last().Text = "12.3 mi";
            Settle(window);
            Assert.Equal(horizon.Bounds.Y, obstruction.Bounds.Y, 3);
            Assert.True(obstruction.Bounds.X > horizon.Bounds.Right, "Obstruction should follow Horizon on the same line");
            var row = window.FindControl<ReadingsPanel>("CameraFrameReadings")!;
            Assert.True(obstruction.Bounds.Right <= row.Bounds.Width + 0.5, $"the line is {obstruction.Bounds.Right:F1} wide in {row.Bounds.Width:F1}");
        }
        finally { Close(window); }
    }

    // ---- The legend ----------------------------------------------------------------------------------

    private static readonly string[] TerrainLegendLabels = ["Near", "Far", "Sky", "Centre", "Threshold"];

    /// <summary>
    /// What the frame's tones and lines mean is reference, not something to keep on screen: the panel shows
    /// none of it until it is asked for, from a named button beside the frame's heading.
    /// </summary>
    [Fact]
    public void TheTerrainFrameLegendIsBehindItsOwnButton()
    {
        var frame = TerrainFrameMarkup();
        var button = frame.Descendants().Single(element => NameOf(element) == "PlannerTerrainLegendButton");
        var flyout = button.Descendants().Single(element => element.Name.LocalName == "Flyout");
        var legend = flyout.Descendants().Single(element => NameOf(element) == "PlannerTerrainLegend");
        var keys = TerrainLegendLabels;
        Assert.Equal(keys, legend.Elements().Where(element => element.Name.LocalName == "TextBlock").Select(element => element.Attribute("Text")?.Value));
        // Nowhere in the panel outside that flyout: not as a line under the frame, not as a tooltip.
        foreach (var key in keys)
            Assert.Single(TerrainFrameMarkup().Descendants(), element => element.Attributes().Any(attribute => attribute.Value == key));
        Assert.Equal("Camera frame legend", Noctaxis.Core.Localisation.Strings.Get("Planner_Terrain_Legend_AccessibleName"));
    }

    /// <summary>
    /// The legend opens from a tap or click and from the keyboard, with no hover anywhere, from a button
    /// that is small to look at and a full-size target. Its samples are the frame's own tones.
    /// </summary>
    [AvaloniaFact]
    public async Task TheTerrainFrameLegendOpensFromATapOrTheKeyboard()
    {
        var (_, window, _) = await CreatePlannerAsync();
        try
        {
            var button = window.FindControl<Button>("PlannerTerrainLegendButton")!;
            var flyout = Assert.IsType<Flyout>(button.Flyout);
            Assert.Equal("Camera frame legend", Avalonia.Automation.AutomationProperties.GetName(button));
            Assert.True(button.IsEffectivelyVisible && button.IsEffectivelyEnabled && button.Focusable);
            Assert.True(button.Bounds.Width >= 32 && button.Bounds.Height >= 28, $"the button is {button.Bounds.Size}");
            // Until it is asked for, none of the legend is on screen.
            Assert.False(flyout.IsOpen);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), text =>
                TerrainLegendLabels.Contains(text.Text) && text.IsEffectivelyVisible);

            // A tap or click opens it.
            var centre = Centre(button, window);
            window.MouseDown(centre, Avalonia.Input.MouseButton.Left);
            window.MouseUp(centre, Avalonia.Input.MouseButton.Left);
            Settle(window);
            Assert.True(flyout.IsOpen, "a click should open the legend");
            var legend = Assert.IsType<Grid>(Assert.IsType<StackPanel>(flyout.Content).Children[0]);
            Assert.Equal("PlannerTerrainLegend", legend.Name);
            Assert.Equal(TerrainLegendLabels, legend.Children.OfType<TextBlock>().Select(text => text.Text));
            Assert.True(legend.Bounds.Height > 0, "the open legend should be laid out");
            var samples = legend.Children.OfType<Border>().ToArray();
            Assert.Same(TerrainFrameView.NearBrush, samples[0].Background);
            Assert.Same(TerrainFrameView.FarBrush, samples[1].Background);
            Assert.Same(TerrainFrameView.SkyBrush, samples[2].Background);
            Assert.Same(TerrainFrameView.CentreLineBrush, Assert.IsType<Avalonia.Controls.Shapes.Line>(samples[3].Child).Stroke);
            Assert.Same(TerrainFrameView.ThresholdLineBrush, Assert.IsType<Avalonia.Controls.Shapes.Line>(samples[4].Child).Stroke);
            flyout.Hide();
            Settle(window);
            Assert.False(flyout.IsOpen);

            // So does the keyboard: the button takes focus, and Enter opens it.
            Assert.True(button.Focus(Avalonia.Input.NavigationMethod.Tab));
            window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);
            window.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);
            Settle(window);
            Assert.True(flyout.IsOpen, "Enter on the focused button should open the legend");
            flyout.Hide();
        }
        finally { Close(window); }
    }

    /// <summary>The data state the panel no longer shows is still available to whoever is diagnosing terrain.</summary>
    [Fact]
    public async Task TheTerrainDataStateAndSourceStayInTheDiagnostics()
    {
        var catalogue = new Noctaxis.Core.Catalogues.OpenNgcTargetCatalogue();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue,
            CreatePlannerStore(new AppSettings(TerrainDebugOverlay: true)), new FakeExporter());
        await vm.InitializeAsync();
        Assert.Contains($"State: {vm.TerrainStatus}", vm.TerrainDiagnosticsStatus);
        Assert.Contains($"Terrain data: {vm.GroundHorizonState}", vm.TerrainDiagnosticsStatus);
        Assert.Contains("Observer: ", vm.TerrainDiagnosticsStatus);
    }

    /// <summary>How a dock's length is shared: equally, unless a panel needs more, and never past the dock.</summary>
    [Theory]
    [InlineData(new double[] { 0, 0 }, 400, new double[] { 200, 200 })]
    [InlineData(new double[] { 254, 0 }, 400, new double[] { 254, 146 })]
    [InlineData(new double[] { 254, 0 }, 600, new double[] { 300, 300 })]
    [InlineData(new double[] { 0, 254, 0 }, 600, new double[] { 173, 254, 173 })]
    [InlineData(new double[] { 300, 300 }, 400, new double[] { 200, 200 })]
    public void ADocksLengthIsSharedEquallyUnlessAPanelNeedsMore(double[] minimums, double length, double[] expected)
    {
        var shares = DockWorkspace.ShareLength(minimums, length);
        Assert.Equal(expected.Length, shares.Length);
        for (var index = 0; index < shares.Length; index++) Assert.Equal(expected[index], shares[index], 0);
        Assert.Equal(length, shares.Sum(), 6);
    }

    // ---- Harness -------------------------------------------------------------------------------------

    private static Point Centre(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

}
