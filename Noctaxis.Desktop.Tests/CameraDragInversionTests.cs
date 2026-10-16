using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Inverting the Camera frame's drag is a preference of the hand: it reverses which way a drag turns or tilts the
/// camera, each axis on its own, and nothing else — not the nudge arrows, not the keys, not Follow, not the values.
/// </summary>
public sealed partial class MainViewModelTests
{
    private const double DragStep = CameraDirectionPad.DegreesPerPixel;

    [Theory]
    [InlineData(false, false, 1, 1)]
    [InlineData(true, false, -1, 1)]
    [InlineData(false, true, 1, -1)]
    [InlineData(true, true, -1, -1)]
    public void EachInversionReversesOnlyItsOwnAxis(bool horizontal, bool vertical, int turnSign, int tiltSign)
    {
        // Right 40 px and up 8 px: normally a clockwise turn and a look up.
        var (turn, tilt) = CameraDirectionPad.DragDelta(new Vector(40, -8), fine: false, horizontal, vertical);
        Assert.Equal(turnSign * 40 * DragStep, turn, 9);
        Assert.Equal(tiltSign * 8 * DragStep, tilt, 9);
        var (fineTurn, fineTilt) = CameraDirectionPad.DragDelta(new Vector(40, -8), fine: true, horizontal, vertical);
        Assert.Equal(turn * CameraDirectionPad.FineFactor, fineTurn, 9);
        Assert.Equal(tilt * CameraDirectionPad.FineFactor, fineTilt, 9);
    }

    [AvaloniaFact]
    public void BothInversionsAreOffByDefault()
    {
        Assert.False(new CameraFramingSettings().InvertHorizontalDrag);
        Assert.False(new CameraFramingSettings().InvertVerticalDrag);
        Assert.False(new AppSettings().EffectiveCameraFraming.InvertHorizontalDrag);
        Assert.False(new AppSettings().EffectiveCameraFraming.InvertVerticalDrag);
        Assert.False(new CameraDirectionPad().InvertHorizontalDrag);
        Assert.False(new CameraDirectionPad().InvertVerticalDrag);
    }

    /// <summary>A real drag on the Planner's Camera frame, in each of the four combinations, from the saved settings.</summary>
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DraggingTheCameraFrameFollowsTheSavedInversions(bool horizontal, bool vertical)
    {
        var (vm, window, _) = await CreatePlannerAsync(new AppSettings(
            CameraFraming: new CameraFramingSettings(InvertHorizontalDrag: horizontal, InvertVerticalDrag: vertical)));
        try
        {
            var pad = AimAt(vm, window, bearing: 90, pitch: 10);
            Assert.Equal((horizontal, vertical), (pad.InvertHorizontalDrag, pad.InvertVerticalDrag));
            DragPad(window, pad, new Vector(40, -8));
            Assert.Equal(90 + (horizontal ? -1 : 1) * 40 * DragStep, vm.CameraBearingDegrees, 6);
            Assert.Equal(10 + (vertical ? -1 : 1) * 8 * DragStep, vm.CameraPitchDegrees, 6);
        }
        finally { Close(window); }
    }

    /// <summary>Changing either toggle applies to the next drag at once, is saved at once, and needs no restart.</summary>
    [AvaloniaFact]
    [CoversSettingsInput("SettingsInvertHorizontalDrag")]
    [CoversSettingsInput("SettingsInvertVerticalDrag")]
    public async Task TogglingAnInversionAppliesAtOnceAndIsSaved()
    {
        var store = CreatePlannerStore(new AppSettings());
        var (vm, window, _) = await CreatePlannerAsync(store: store);
        try
        {
            var pad = AimAt(vm, window, bearing: 90, pitch: 10);
            vm.SettingsInvertHorizontalDrag = true;
            await vm.SettleSettingsAsync();
            Settle(window);
            Assert.True(store.State.Settings.EffectiveCameraFraming.InvertHorizontalDrag);
            Assert.False(store.State.Settings.EffectiveCameraFraming.InvertVerticalDrag);
            Assert.True(pad.InvertHorizontalDrag);
            DragPad(window, pad, new Vector(40, -8));
            Assert.Equal(90 - 40 * DragStep, vm.CameraBearingDegrees, 6);
            Assert.Equal(10 + 8 * DragStep, vm.CameraPitchDegrees, 6);

            vm.SettingsInvertHorizontalDrag = false;
            vm.SettingsInvertVerticalDrag = true;
            await vm.SettleSettingsAsync();
            Settle(window);
            Assert.False(store.State.Settings.EffectiveCameraFraming.InvertHorizontalDrag);
            Assert.True(store.State.Settings.EffectiveCameraFraming.InvertVerticalDrag);
            AimAt(vm, window, bearing: 90, pitch: 10);
            DragPad(window, pad, new Vector(40, -8));
            Assert.Equal(90 + 40 * DragStep, vm.CameraBearingDegrees, 6);
            Assert.Equal(10 - 8 * DragStep, vm.CameraPitchDegrees, 6);

            // The toggles live with the Camera framing settings.
            Assert.NotNull(window.FindControl<CheckBox>("InvertHorizontalDragToggle"));
            Assert.NotNull(window.FindControl<CheckBox>("InvertVerticalDragToggle"));
        }
        finally { Close(window); }

        // Restarted from what was saved: the same preferences.
        var (reloaded, again, _) = await CreatePlannerAsync(store: store);
        try
        {
            Assert.False(reloaded.SettingsInvertHorizontalDrag);
            Assert.True(reloaded.SettingsInvertVerticalDrag);
            var pad = again.FindControl<CameraDirectionPad>("CameraDirection")!;
            Assert.Equal((false, true), (pad.InvertHorizontalDrag, pad.InvertVerticalDrag));
        }
        finally { Close(again); }
    }

    /// <summary>With both inverted, the nudge arrows and the arrow keys still turn and tilt the way they name.</summary>
    [AvaloniaFact]
    public async Task NudgesAndKeysAreNeverInverted()
    {
        var (vm, window, _) = await CreatePlannerAsync(new AppSettings(
            CameraFraming: new CameraFramingSettings(InvertHorizontalDrag: true, InvertVerticalDrag: true)));
        try
        {
            var pad = AimAt(vm, window, bearing: 90, pitch: 10);
            void Click(string name)
            {
                window.FindControl<RepeatButton>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Settle(window);
            }
            Click("CameraTurnRightButton");
            Assert.Equal(90 + CameraDirectionPad.NudgeDegrees, vm.CameraBearingDegrees, 6);
            Click("CameraTurnLeftButton");
            Assert.Equal(90, vm.CameraBearingDegrees, 6);
            Click("CameraTiltUpButton");
            Assert.Equal(10 + CameraDirectionPad.NudgeDegrees, vm.CameraPitchDegrees, 6);
            Click("CameraTiltDownButton");
            Assert.Equal(10, vm.CameraPitchDegrees, 6);

            void Press(Key key)
            {
                pad.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
                Settle(window);
            }
            Press(Key.Right);
            Assert.Equal(90 + CameraDirectionPad.NudgeDegrees, vm.CameraBearingDegrees, 6);
            Press(Key.Left);
            Assert.Equal(90, vm.CameraBearingDegrees, 6);
            Press(Key.Up);
            Assert.Equal(10 + CameraDirectionPad.NudgeDegrees, vm.CameraPitchDegrees, 6);
            Press(Key.Down);
            Assert.Equal(10, vm.CameraPitchDegrees, 6);
        }
        finally { Close(window); }
    }

    /// <summary>Following the focused target aims the camera exactly as it does with no inversion.</summary>
    [AvaloniaFact]
    public async Task FollowingIsTheSameWithTheDragInverted()
    {
        async Task<(double Bearing, double Pitch)> Followed(bool inverted)
        {
            var (vm, window, _) = await CreatePlannerAsync(new AppSettings(
                CameraFraming: new CameraFramingSettings(InvertHorizontalDrag: inverted, InvertVerticalDrag: inverted)));
            try
            {
                Target(vm, "sun").FocusCommand.Execute(null);
                vm.FollowFocusedTarget = true;
                Settle(window);
                Assert.True(vm.IsFollowingFocusedTarget);
                Assert.False(window.FindControl<CameraDirectionPad>("CameraDirection")!.IsEffectivelyEnabled);
                return (vm.CameraBearingDegrees, vm.CameraPitchDegrees);
            }
            finally { Close(window); }
        }
        var plain = await Followed(false);
        var inverted = await Followed(true);
        Assert.Equal(plain.Bearing, inverted.Bearing, 9);
        Assert.Equal(plain.Pitch, inverted.Pitch, 9);
    }

    private static CameraDirectionPad AimAt(MainViewModel vm, MainWindow window, double bearing, double pitch)
    {
        vm.CameraBearingDegrees = bearing;
        vm.CameraPitchDegrees = pitch;
        window.FindControl<TerrainFrameView>("PlannerCameraFrameView")!.Depth = PictureDepth();
        Settle(window);
        return window.FindControl<CameraDirectionPad>("CameraDirection")!;
    }

    private static void DragPad(MainWindow window, CameraDirectionPad pad, Vector by)
    {
        var centre = pad.TranslatePoint(new Point(pad.Bounds.Width / 2, pad.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseMove(centre + by, RawInputModifiers.LeftMouseButton);
        window.MouseUp(centre + by, MouseButton.Left);
        Settle(window);
    }
}
