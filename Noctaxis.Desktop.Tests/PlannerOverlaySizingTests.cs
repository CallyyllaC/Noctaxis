using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The Planner map's two fixed instruments, sized from the user's shares of the map's width: the PiP at the
/// top-left and the Camera frame at the bottom-left never overlap and stay on the map, follow the map as the
/// window changes, and are resized by their grips — by drag or by arrow keys — saving only the resized one's
/// preference, and only when the resize ends.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static Rect OnMap(MainWindow window, Control control) =>
        new(control.TranslatePoint(default, MainMap(window))!.Value, control.Bounds.Size);

    private static void AssertInstrumentsFit(MainWindow window)
    {
        var map = new Rect(MainMap(window).Bounds.Size);
        var row = OnMap(window, window.FindControl<StackPanel>("PlannerMapBottomLeft")!);
        var card = OnMap(window, window.FindControl<Border>("PlannerTerrainFrame")!);
        Assert.True(map.Contains(card), $"the Camera frame {card} should be on the map {map}");
        var pip = window.FindControl<Border>("PlannerPip")!;
        if (!pip.IsVisible) return;
        var box = OnMap(window, pip);
        Assert.True(map.Contains(box), $"the PiP {box} should be on the map {map}");
        Assert.True(row.Top - box.Bottom >= MapInstrumentLayout.Gap - .5, $"the PiP {box} should stay {MapInstrumentLayout.Gap} px above {row}");
    }

    // Tall enough that both instruments fit at their preferred sizes, as in a full-HD window.
    private static void Roomy(Window window)
    {
        window.Height = 1100;
        Settle(window);
    }

    private static void Drag(Window window, Control grip, double across)
    {
        var at = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        for (var step = 1; step <= 4; step++)
        {
            window.MouseMove(at + new Vector(across * step / 4, 0), RawInputModifiers.LeftMouseButton);
            Settle(window);
        }
        window.MouseUp(at + new Vector(across, 0), MouseButton.Left);
        Settle(window);
    }

    /// <summary>By default each is its share of the map's own width, the room the docked panels leave.</summary>
    [AvaloniaFact]
    public async Task TheInstrumentsTakeTheirShareOfTheMapsWidth()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            Roomy(window);
            var map = MainMap(window).Bounds.Size;
            var (pip, frame) = MapInstrumentLayout.Preferred(map.Width, vm.PlannerOverlaySizes);
            Assert.Equal(new PlannerOverlaySizes(), vm.PlannerOverlaySizes);
            Assert.True(window.OverlayLayoutForTesting.PipShown);
            Assert.Equal(Math.Round(pip), window.FindControl<Border>("PlannerPip")!.Bounds.Width, 0);
            Assert.Equal(window.FindControl<Border>("PlannerPip")!.Bounds.Width, window.FindControl<Border>("PlannerPip")!.Bounds.Height, 0);
            Assert.Equal(Math.Round(frame), window.FindControl<Border>("PlannerTerrainFrame")!.Bounds.Width, 0);
            AssertInstrumentsFit(window);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// A saved preference is a share of the map, so it is the same share however wide the map is; a value
    /// out of bounds is drawn within them.
    /// </summary>
    [AvaloniaFact]
    public async Task ASavedPreferenceIsDrawnAsItsShareOfTheMap()
    {
        var (_, window, _) = await CreatePlannerAsync(new AppSettings(PlannerOverlays: new(.25, double.NaN)));
        try
        {
            Roomy(window);
            var width = MainMap(window).Bounds.Width;
            Assert.Equal(Math.Round(width * .25), window.FindControl<Border>("PlannerPip")!.Bounds.Width, 0);
            Assert.Equal(Math.Round(Math.Max(window.CameraFrameMinimumWidthForTesting, width * PlannerOverlaySizes.DefaultCameraFrameWidthFraction)),
                window.FindControl<Border>("PlannerTerrainFrame")!.Bounds.Width, 0);
            AssertInstrumentsFit(window);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// A shorter window shrinks them to fit, never overlapping and never saving the smaller size; a larger one
    /// brings them back to the preference.
    /// </summary>
    [AvaloniaFact]
    public async Task AShorterWindowShrinksThemForNowAndALargerOneRestoresThem()
    {
        var (vm, window, _) = await CreatePlannerAsync(new AppSettings(PlannerOverlays: new(.24, .24)));
        try
        {
            Roomy(window);
            var preferred = window.FindControl<Border>("PlannerPip")!.Bounds.Width;
            var saved = vm.PlannerOverlaySizes;
            foreach (var height in new[] { 700d, 600, 520, 460 })
            {
                window.Height = height;
                Settle(window);
                AssertInstrumentsFit(window);
                Assert.Equal(saved, vm.PlannerOverlaySizes);
            }
            Assert.True(!window.OverlayLayoutForTesting.PipShown || window.OverlayLayoutForTesting.PipSide < preferred);

            Roomy(window);
            Settle(window);
            Assert.True(window.FindControl<Border>("PlannerPip")!.IsVisible);
            Assert.Equal(preferred, window.FindControl<Border>("PlannerPip")!.Bounds.Width, 0);
            AssertInstrumentsFit(window);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// Dragging the PiP's grip resizes the PiP and saves its preference when the drag ends, leaving the Camera
    /// frame's preference, the map's view inside the PiP, the main map, the pin and the camera as they were.
    /// </summary>
    [AvaloniaFact]
    public async Task DraggingThePipsGripResizesOnlyThePip()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            Roomy(window);
            var pip = Pip(window);
            var (pipView, mainView) = (pip.CurrentView!.Value, MainMap(window).CurrentView!.Value);
            var shared = (vm.Observer, vm.CameraBearingDegrees, vm.CameraPitchDegrees, vm.FocalLength);
            var before = vm.PlannerOverlaySizes;
            var box = window.FindControl<Border>("PlannerPip")!;
            var width = box.Bounds.Width;
            var grip = window.FindControl<OverlayResizeGrip>("PlannerPipResizeGrip")!;
            Assert.Equal("Resize Picture-in-Picture map", Avalonia.Automation.AutomationProperties.GetName(grip));
            // The grip is clear of the PiP's own buttons.
            foreach (var name in new[] { "PlannerPipSwitchButton", "PlannerPipFollowButton" })
            {
                var button = window.FindControl<Control>(name)!;
                Assert.False(OnMap(window, button).Intersects(OnMap(window, grip)), $"{name} overlaps the grip");
            }

            Drag(window, grip, 40);
            Assert.Equal(width + 40, box.Bounds.Width, 0);
            Assert.Equal(box.Bounds.Width, box.Bounds.Height, 0);
            await vm.WaitForPendingSavesAsync();
            Assert.Equal((width + 40) / MainMap(window).Bounds.Width, vm.PlannerOverlaySizes.PipWidthFraction, 1 / MainMap(window).Bounds.Width);
            Assert.Equal(before.CameraFrameWidthFraction, vm.PlannerOverlaySizes.CameraFrameWidthFraction);
            // The PiP's map keeps its centre and zoom; nothing else moved.
            Assert.Equal(pipView.Resolution, pip.CurrentView!.Value.Resolution, 6);
            AssertNear(pipView.Centre, pip.CurrentView!.Value.Centre, "the PiP's centre");
            Assert.Equal(mainView, MainMap(window).CurrentView!.Value);
            Assert.Equal(shared, (vm.Observer, vm.CameraBearingDegrees, vm.CameraPitchDegrees, vm.FocalLength));
            AssertInstrumentsFit(window);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// The Camera frame's grip resizes the frame card, by drag or by the arrow keys while it has focus, and
    /// saves only the frame's preference. Its keys and pointer never reach the camera direction or the focal
    /// length; the frame keeps its shape at the new width.
    /// </summary>
    [AvaloniaFact]
    public async Task TheCameraFramesGripResizesOnlyTheCameraFrame()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            Roomy(window);
            var shared = (vm.CameraBearingDegrees, vm.CameraPitchDegrees, vm.FocalLength);
            var before = vm.PlannerOverlaySizes;
            var card = window.FindControl<Border>("PlannerTerrainFrame")!;
            var width = card.Bounds.Width;
            var grip = window.FindControl<OverlayResizeGrip>("PlannerCameraFrameResizeGrip")!;
            Assert.Equal("Resize Camera frame", Avalonia.Automation.AutomationProperties.GetName(grip));
            Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetHelpText(grip)));

            Drag(window, grip, 30);
            Assert.Equal(width + 30, card.Bounds.Width, 0);
            var view = window.FindControl<TerrainFrameView>("PlannerCameraFrameView")!;
            Assert.Equal(card.Bounds.Width - 18, view.Bounds.Width, 0);

            // The keyboard: focus the grip; the arrows step its width.
            Assert.True(grip.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Settle(window);
            Assert.Equal(width + 30 + OverlayResizeGrip.KeyboardStep, card.Bounds.Width, 0);
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Settle(window);
            Assert.Equal(width + 30, card.Bounds.Width, 0);

            await vm.WaitForPendingSavesAsync();
            Assert.Equal((width + 30) / MainMap(window).Bounds.Width, vm.PlannerOverlaySizes.CameraFrameWidthFraction, 3);
            Assert.Equal(before.PipWidthFraction, vm.PlannerOverlaySizes.PipWidthFraction);
            Assert.Equal(shared, (vm.CameraBearingDegrees, vm.CameraPitchDegrees, vm.FocalLength));
            AssertInstrumentsFit(window);

            // Nothing in the card is clipped at its smallest.
            for (var i = 0; i < 40; i++) grip.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left });
            Settle(window);
            Assert.Equal(window.CameraFrameMinimumWidthForTesting, card.Bounds.Width, 0);
            var inside = new Rect(card.Bounds.Size);
            foreach (var name in new[] { "CameraDirectionInstrument", "CameraFrameReadings", "PlannerTerrainLegendButton" })
            {
                var part = window.FindControl<Control>(name)!;
                var at = new Rect(part.TranslatePoint(default, card)!.Value, part.Bounds.Size);
                Assert.True(at.Right <= inside.Right + .5 && at.Bottom <= inside.Bottom + .5, $"{name} {at} is clipped in {inside}");
            }
        }
        finally { Close(window); }
    }
}
