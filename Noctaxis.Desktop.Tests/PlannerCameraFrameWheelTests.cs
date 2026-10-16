using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The Camera frame (formerly the Terrain frame): its name wherever a user reads it, and the wheel over it,
/// which turns the camera's focal length — the same focal length the Equipment slider sets — and nothing else.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static readonly EquipmentSettings WheelEquipment = new(
        [new CameraProfile("camera", "Full Frame", 36, 24)],
        [new LensProfile("zoom", "70-200 mm", 70, 200), new LensProfile("prime", "50 mm", 50, 50)]);

    /// <summary>A point over the frame itself, away from its buttons and values.</summary>
    private static Point OverCameraFrame(Window window)
    {
        var view = window.FindControl<Border>("PlannerTerrainFrame")!.GetVisualDescendants().OfType<TerrainFrameView>().Single();
        return view.TranslatePoint(new Point(view.Bounds.Width / 2, view.Bounds.Height / 2), window)!.Value;
    }

    [Fact]
    public void TheInstrumentIsCalledTheCameraFrameWhereverItIsRead()
    {
        Assert.Equal("Camera frame", Noctaxis.Core.Localisation.Strings.Get("Planner_Terrain_CameraFrame_Label"));
        Assert.Equal("Camera frame legend", Noctaxis.Core.Localisation.Strings.Get("Planner_Terrain_Legend_AccessibleName"));
        var frame = TerrainFrameMarkup();
        Assert.Contains(frame.Descendants(), element => element.Attribute("Text")?.Value == "Camera frame");
        // How to use the wheel is in the frame's help and its accessible help text, not on screen.
        Assert.Equal("Scroll over the frame to adjust the focal length.", frame.Attributes().Single(attribute => attribute.Name.LocalName == "AutomationProperties.HelpText").Value);
        Assert.Single(frame.Descendants(), element => NameOf(element) == "CameraFrameWheelHelp" && element.Ancestors().Any(a => a.Name.LocalName == "Flyout"));
        // No user-facing text names a Terrain frame any more.
        foreach (var resources in new[] { "Planner.resx", "Settings.resx" })
            Assert.DoesNotContain("terrain frame", File.ReadAllText(Path.Combine(TestPaths.DesktopProject, "Resources", resources)),
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The wheel over the Camera frame turns the focal length up and down, a whole millimetre step at a
    /// time; the Equipment slider and value, the field of view and the map's framing follow at once, and
    /// neither map moves.
    /// </summary>
    [AvaloniaFact]
    public async Task TheWheelOverTheCameraFrameTurnsTheFocalLength()
    {
        var (vm, window, _) = await CreatePlannerAsync(new AppSettings(Equipment: WheelEquipment));
        try
        {
            vm.SelectedLens = vm.Lenses.Single(lens => lens.Id == "zoom");
            vm.FocalLength = 100;
            Settle(window);
            var over = OverCameraFrame(window);
            var (main, pip) = (MainMap(window), Pip(window));
            var (mainView, pipView) = (main.CurrentView!.Value, pip.CurrentView!.Value);
            var fov = vm.Snapshot!.FieldOfView.HorizontalDegrees;

            window.MouseWheel(over, new Vector(0, 1));
            Settle(window);
            Assert.Equal(105, vm.FocalLength);
            Assert.Equal(105, window.FindControl<Slider>("FocalLengthSlider")!.Value);
            Assert.StartsWith("105", window.FindControl<TextBlock>("FocalLengthValue")!.Text);
            Assert.True(vm.Snapshot!.FieldOfView.HorizontalDegrees < fov, "a longer lens narrows the view");
            if (vm.CameraFramingGuide is { } guide)
                Assert.Equal(vm.Snapshot.FieldOfView.HorizontalDegrees, guide.HorizontalFieldOfViewDegrees, 6);

            window.MouseWheel(over, new Vector(0, -1));
            Settle(window);
            Assert.Equal(100, vm.FocalLength);
            Assert.Equal(fov, vm.Snapshot!.FieldOfView.HorizontalDegrees, 6);

            // A trackpad's small deltas add up to a notch.
            window.MouseWheel(over, new Vector(0, .5));
            Assert.Equal(100, vm.FocalLength);
            window.MouseWheel(over, new Vector(0, .5));
            Settle(window);
            Assert.Equal(105, vm.FocalLength);

            // Only the focal length: neither map zoomed or moved.
            Assert.Equal(mainView, main.CurrentView!.Value);
            Assert.Equal(pipView, pip.CurrentView!.Value);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// Within the lens's range, however far the wheel turns; a prime lens does not turn at all, and the
    /// wheel still goes no further than the frame.
    /// </summary>
    [AvaloniaFact]
    public async Task TheCameraFrameWheelKeepsToTheLens()
    {
        var (vm, window, _) = await CreatePlannerAsync(new AppSettings(Equipment: WheelEquipment));
        try
        {
            vm.SelectedLens = vm.Lenses.Single(lens => lens.Id == "zoom");
            vm.FocalLength = 198;
            Settle(window);
            var over = OverCameraFrame(window);
            window.MouseWheel(over, new Vector(0, 1));
            Settle(window);
            Assert.Equal(200, vm.FocalLength);
            window.MouseWheel(over, new Vector(0, 3));
            Settle(window);
            Assert.Equal(200, vm.FocalLength);
            for (var i = 0; i < 40; i++) window.MouseWheel(over, new Vector(0, -1));
            Settle(window);
            Assert.Equal(70, vm.FocalLength);
            Assert.Equal(70, window.FindControl<Slider>("FocalLengthSlider")!.Value);

            vm.SelectedLens = vm.Lenses.Single(lens => lens.Id == "prime");
            Settle(window);
            var mainView = MainMap(window).CurrentView!.Value;
            window.MouseWheel(over, new Vector(0, 1));
            window.MouseWheel(over, new Vector(0, -2));
            Settle(window);
            Assert.Equal(50, vm.FocalLength);
            Assert.False(vm.StepFocalLength(1));
            Assert.Equal(mainView, MainMap(window).CurrentView!.Value);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// The frame's own controls keep their behaviour, and the wheel stays the frame's: over the bearing value
    /// it turns the focal length, not the bearing, and the nudges still aim the camera. The wheel over the Picture-in-Picture map stays its zoom.
    /// </summary>
    [AvaloniaFact]
    public async Task TheCameraFrameWheelCoexistsWithAimingAndThePictureInPictureMap()
    {
        var (vm, window, _) = await CreatePlannerAsync(new AppSettings(Equipment: WheelEquipment));
        try
        {
            vm.SelectedLens = vm.Lenses.Single(lens => lens.Id == "zoom");
            vm.FocalLength = 100;
            Settle(window);
            var bearing = vm.CameraBearingDegrees;
            var input = window.FindControl<NumericUpDown>("CameraBearingInput")!;
            var overInput = input.TranslatePoint(new Point(input.Bounds.Width / 2, input.Bounds.Height / 2), window)!.Value;
            window.MouseWheel(overInput, new Vector(0, 1));
            Settle(window);
            Assert.Equal(105, vm.FocalLength);
            Assert.Equal(bearing, vm.CameraBearingDegrees);

            // The camera's direction is still the frame's to set.
            window.FindControl<RepeatButton>("CameraTurnRightButton")!.RaiseEvent(
                new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Settle(window);
            Assert.NotEqual(bearing, vm.CameraBearingDegrees);

            // Over the Picture-in-Picture map, the wheel zooms that map and leaves the lens alone.
            var pip = Pip(window);
            var zoom = ZoomOf(pip);
            var frame = window.FindControl<Border>("PlannerPip")!;
            window.MouseWheel(frame.TranslatePoint(new Point(frame.Bounds.Width / 3, frame.Bounds.Height / 3), window)!.Value, new Vector(0, 1));
            Settle(window);
            Assert.Equal(zoom / 2, ZoomOf(pip), 6);
            Assert.Equal(105, vm.FocalLength);
        }
        finally { Close(window); }
    }
}
