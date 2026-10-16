using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Inspector → Equipment: the camera and lens first, then focal length, orientation, where the camera
/// points as one Camera direction instrument, and the field of view — explanations behind info buttons.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static System.Xml.Linq.XElement EquipmentMarkup() => TestMarkup.LoadMainWindow().Descendants()
        .Single(element => NameOf(element) == "PlannerInspectorTabs").Elements()
        .Single(element => element.Name.LocalName == "TabItem" && element.Attribute("Header")?.Value == "Equipment");

    /// <summary>
    /// Equipment is the camera and what is on it: camera, lens, focal length, orientation and the field of
    /// view they make. Where the camera points belongs to the Terrain frame, which shows what it sees.
    /// </summary>
    [Fact]
    public void EquipmentIsTheCameraLensFocalLengthOrientationAndFieldOfView()
    {
        var equipment = EquipmentMarkup();
        var labels = equipment.Descendants()
            .Where(element => (string?)element.Attribute("Classes") == "fieldLabel")
            .Select(element => element.Attribute("Text")?.Value ?? "").ToArray();
        Assert.Equal(["Camera", "Lens", "Focal length", "Orientation", "Field of view"], labels);
        Assert.Equal(["FocalLengthSlider"], equipment.Descendants().Where(element => element.Name.LocalName == "Slider").Select(NameOf));
        foreach (var gone in new[] { "CameraDirectionPad", "RepeatButton", "NumericUpDown" })
            Assert.DoesNotContain(equipment.Descendants(), element => element.Name.LocalName == gone);
        Assert.DoesNotContain(equipment.Descendants(), element => NameOf(element) == "FollowFocusedTargetToggle");
        Assert.DoesNotContain(equipment.Descendants(), element => element.Attribute("Text")?.Value == "Camera direction");
        var frame = TerrainFrameMarkup();
        Assert.Single(frame.Descendants(), element => element.Name.LocalName == "CameraDirectionPad");

        // Each label sits with its control, a group apiece; the selectors lead, full width.
        Assert.Equal(["EquipmentCamera", "EquipmentLens", "EquipmentFocalLength", "EquipmentOrientation", "EquipmentFieldOfView"],
            equipment.Descendants().Single(element => NameOf(element) == "PlannerEquipment").Elements()
                .Where(element => element.Name.LocalName == "StackPanel").Select(NameOf));
        Assert.All(equipment.Descendants().Where(element => element.Name.LocalName == "ComboBox"),
            combo => Assert.Equal("Stretch", combo.Attribute("HorizontalAlignment")?.Value));
        // The field of view stands out, but less than the camera and lens it comes from.
        Assert.Equal("{DynamicResource TextBodyStrong}",
            equipment.Descendants().Single(element => NameOf(element) == "FieldOfViewValue").Attribute("FontSize")?.Value);
    }

    /// <summary>The two explanations are not on the page: each is behind the info button beside what it explains.</summary>
    [Fact]
    public void ExplanationsAreBehindInfoButtonsNotOnThePage()
    {
        var equipment = EquipmentMarkup();
        var composition = Noctaxis.Core.Localisation.Strings.Get("Planner_Camera_AngularCompositionGuide_Description");
        Assert.DoesNotContain("0° horizontal · positive looks upward", equipment.ToString());
        var frame = TerrainFrameMarkup();
        foreach (var (scope, button, text) in new[] { (frame, "PlannerTerrainLegendButton", "CameraDirectionHelp"), (equipment, "FieldOfViewHelpButton", "FieldOfViewHelp") })
        {
            var info = scope.Descendants().Single(element => NameOf(element) == button);
            var help = info.Descendants().Single(element => NameOf(element) == text);
            Assert.Contains(help.Ancestors(), element => element.Name.LocalName == "Flyout");
        }
        // Anything with the explanation's wording is inside a flyout.
        Assert.All(equipment.Descendants().Where(element => element.Attribute("Text")?.Value == composition),
            element => Assert.Contains(element.Ancestors(), ancestor => ancestor.Name.LocalName == "Flyout"));
        Assert.Contains("0° at the horizon", Noctaxis.Core.Localisation.Strings.Get("Planner_Camera_Direction_Help"));
    }

    /// <summary>Bearing has no ends: it turns through north either way. Pitch stops at the camera's limits.</summary>
    [AvaloniaFact]
    public void BearingWrapsThroughNorthAndPitchStopsAtItsLimits()
    {
        var pad = new CameraDirectionPad { Bearing = 2 };
        pad.Turn(-5);
        Assert.Equal(357, pad.Bearing, 6);
        pad.Bearing = 358;
        pad.Turn(5);
        Assert.Equal(3, pad.Bearing, 6);
        pad.Turn(360);
        Assert.Equal(3, pad.Bearing, 6);
        pad.Turn(-723);
        Assert.Equal(0, pad.Bearing, 6);
        Assert.Equal(0, CameraDirectionPad.WrapBearing(360));
        Assert.Equal(359.5, CameraDirectionPad.WrapBearing(-.5), 6);

        pad.Pitch = 85;
        pad.Tilt(10);
        Assert.Equal(CameraFramingSettings.MaximumCameraPitchDegrees, pad.Pitch);
        pad.Pitch = -40;
        pad.Tilt(-10);
        Assert.Equal(CameraFramingSettings.MinimumCameraPitchDegrees, pad.Pitch);
        Assert.Equal(-45, CameraDirectionPad.ClampPitch(-90));
        Assert.Equal(90, CameraDirectionPad.ClampPitch(120));
    }

    /// <summary>
    /// A drag across the instrument is relative: across turns the camera, up tilts it up, wherever the
    /// drag starts. It writes the camera's own bearing and pitch, and is saved like any other change.
    /// The field of view does not depend on where the camera points.
    /// </summary>
    [AvaloniaFact]
    public async Task DraggingTheInstrumentTurnsAndTiltsTheCamera()
    {
        var store = CreatePlannerStore(new AppSettings());
        var (vm, window, _) = await CreatePlannerAsync(store: store);
        try
        {
            var pad = OpenCameraDirection(window);
            var fieldOfView = vm.FieldOfViewText;
            vm.CameraBearingDegrees = 2;
            vm.CameraPitchDegrees = 0;
            Settle(window);
            Assert.Equal(2, pad.Bearing, 6);

            // 20 px left, 40 px up, from wherever the pointer went down.
            // Anywhere on the pad clear of its arrows (turn at the ends, tilt just inside them).
            var start = pad.TranslatePoint(new Point(pad.Bounds.Width * .6, pad.Bounds.Height * .7), window)!.Value;
            Drag(window, start, start + new Vector(-20, -40));
            Assert.Equal(357, vm.CameraBearingDegrees, 6);
            Assert.Equal(10, vm.CameraPitchDegrees, 6);
            Assert.Equal(357, pad.Bearing, 6);

            // A long drag down stops at the lower limit; a long drag right goes all the way round.
            Drag(window, start, start + new Vector(0, 400));
            Assert.Equal(CameraFramingSettings.MinimumCameraPitchDegrees, vm.CameraPitchDegrees);
            Drag(window, start, start + new Vector(360 / CameraDirectionPad.DegreesPerPixel, 0));
            Assert.Equal(357, vm.CameraBearingDegrees, 3);

            Assert.Equal(fieldOfView, vm.FieldOfViewText);
            await vm.WaitForCameraFramingPersistenceAsync();
            Assert.Equal(357, store.State.Settings.EffectiveCameraFraming.ManualBearingDegrees, 3);
            Assert.Equal(CameraFramingSettings.MinimumCameraPitchDegrees, store.State.Settings.EffectiveCameraFraming.CameraPitchDegrees);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// The drag is never the only way: the arrows beside the values nudge by tap or click, and the
    /// instrument takes the arrow keys, finer with Shift. Bearing still wraps; pitch still stops.
    /// </summary>
    [AvaloniaFact]
    public async Task TheArrowsAndTheArrowKeysNudgeTheCamera()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var pad = OpenCameraDirection(window);
            vm.CameraBearingDegrees = 0;
            vm.CameraPitchDegrees = 89.5;
            Settle(window);
            void Tap(string name)
            {
                var button = window.FindControl<RepeatButton>(name)!;
                Assert.True(button.Bounds.Width >= 28 && button.Bounds.Height >= 28, $"{name} is {button.Bounds.Size}");
                Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(button)));
                var at = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                Settle(window);
            }
            Tap("CameraTurnLeftButton");
            Assert.Equal(359, vm.CameraBearingDegrees, 6);
            Tap("CameraTurnRightButton");
            Tap("CameraTurnRightButton");
            Assert.Equal(1, vm.CameraBearingDegrees, 6);
            Tap("CameraTiltUpButton");
            Assert.Equal(90, vm.CameraPitchDegrees, 6);
            Tap("CameraTiltUpButton");
            Assert.Equal(90, vm.CameraPitchDegrees, 6);
            Tap("CameraTiltDownButton");
            Assert.Equal(89, vm.CameraPitchDegrees, 6);

            // The keyboard: the instrument takes focus and the arrow keys; Shift is a tenth of a step.
            Assert.True(pad.Focusable);
            Assert.Equal("Camera direction", Avalonia.Automation.AutomationProperties.GetName(pad));
            Assert.True(pad.Focus(NavigationMethod.Tab));
            void Key(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
            {
                window.KeyPressQwerty(key, modifiers);
                window.KeyReleaseQwerty(key, modifiers);
                Settle(window);
            }
            Key(PhysicalKey.ArrowLeft);
            Key(PhysicalKey.ArrowLeft);
            Assert.Equal(359, vm.CameraBearingDegrees, 6);
            Key(PhysicalKey.ArrowRight, RawInputModifiers.Shift);
            Assert.Equal(359.1, vm.CameraBearingDegrees, 6);
            Key(PhysicalKey.ArrowDown);
            Assert.Equal(88, vm.CameraPitchDegrees, 6);
            Key(PhysicalKey.ArrowUp);
            Assert.Equal(89, vm.CameraPitchDegrees, 6);

            // And the values themselves still take a typed number.
            Assert.Equal(359.1m, window.FindControl<NumericUpDown>("CameraBearingInput")!.Value);
            Assert.Equal(89m, window.FindControl<NumericUpDown>("CameraPitchInput")!.Value);
        }
        finally { Close(window); }
    }

    /// <summary>Focal length's value sits at the end of the slider's own row, on one line.</summary>
    [AvaloniaFact]
    public async Task TheFocalLengthValueSitsOnTheSlidersRow()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            OpenCameraDirection(window);
            var slider = window.FindControl<Slider>("FocalLengthSlider")!;
            var value = window.FindControl<TextBlock>("FocalLengthValue")!;
            var sliderAt = slider.TranslatePoint(default, window)!.Value;
            var valueAt = value.TranslatePoint(default, window)!.Value;
            Assert.InRange(valueAt.Y + value.Bounds.Height / 2, sliderAt.Y, sliderAt.Y + slider.Bounds.Height);
            Assert.True(valueAt.X >= sliderAt.X + slider.Bounds.Width, "the value should follow the slider");
            Assert.Equal(Avalonia.Media.TextWrapping.NoWrap, value.TextWrapping);
            Assert.EndsWith("mm", value.Text);
            Assert.True(value.Bounds.Height < value.FontSize * 2, "the value should be one line");
        }
        finally { Close(window); }
    }

    /// <summary>Each explanation opens from its info button by a click or tap, or from the keyboard.</summary>
    [AvaloniaFact]
    public async Task TheExplanationsOpenFromTheirInfoButtons()
    {
        var (_, window, _) = await CreatePlannerAsync();
        try
        {
            OpenCameraDirection(window);
            // How to aim the camera is in the Terrain frame's info, with the legend of what it shows.
            var direction = window.FindControl<Button>("PlannerTerrainLegendButton")!;
            var fieldOfView = window.FindControl<Button>("FieldOfViewHelpButton")!;
            Assert.Equal("Camera frame legend", Avalonia.Automation.AutomationProperties.GetName(direction));
            Assert.Equal("About field of view", Avalonia.Automation.AutomationProperties.GetName(fieldOfView));
            foreach (var button in new[] { direction, fieldOfView })
                Assert.True(button.Bounds.Width >= 32 && button.Bounds.Height >= 28 && button.Focusable);

            direction.BringIntoView();
            Settle(window);
            var at = direction.TranslatePoint(new Point(direction.Bounds.Width / 2, direction.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Settle(window);
            var flyout = Assert.IsType<Flyout>(direction.Flyout);
            Assert.True(flyout.IsOpen, "a click should open the camera direction explanation");
            Assert.Contains(Assert.IsType<StackPanel>(flyout.Content).Children.OfType<TextBlock>(),
                text => text.Text?.Contains("0° at the horizon") == true);
            flyout.Hide();
            Settle(window);

            Assert.True(fieldOfView.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Settle(window);
            var second = Assert.IsType<Flyout>(fieldOfView.Flyout);
            Assert.True(second.IsOpen, "Enter should open the field of view explanation");
            Assert.Equal(Noctaxis.Core.Localisation.Strings.Get("Planner_Camera_AngularCompositionGuide_Description"),
                Assert.IsType<TextBlock>(second.Content).Text);
            second.Hide();
        }
        finally { Close(window); }
    }

    private static CameraDirectionPad OpenCameraDirection(MainWindow window)
    {
        window.FindControl<TabControl>("PlannerInspectorTabs")!.SelectedIndex = 2;
        Settle(window);
        var pad = window.FindControl<CameraDirectionPad>("CameraDirection")!;
        pad.BringIntoView();
        Settle(window);
        return pad;
    }

    private static void Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left);
        Settle(window);
    }
}
