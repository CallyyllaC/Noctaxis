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
/// The camera-direction control is drawn over the Camera frame's picture itself — a bearing tape along its
/// bottom edge, a pitch ladder down its left — with the same behaviour as before; and the four values under
/// the picture are one status line.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static Rect In(Control control, Visual relativeTo) =>
        new(control.TranslatePoint(default, relativeTo)!.Value, control.Bounds.Size);

    /// <summary>A 3:2 picture, as the default camera sees, standing in for a live terrain profile.</summary>
    private static CameraTerrainDepth PictureDepth(double halfWidth = 15, double halfHeight = 10) =>
        new([360 - halfWidth, 0, halfWidth], -halfHeight, halfHeight,
            System.Collections.Immutable.ImmutableArray.CreateRange(Enumerable.Repeat(1000d, 3 * CameraTerrainDepth.Rows)));

    /// <summary>
    /// The control lies on the picture, edge to edge, in whatever shape the camera gives it: the view is the
    /// picture, with no band beside it, and the control covers exactly that. Its scales are on the picture's
    /// bottom and left edges, and nothing of it lies below the picture.
    /// </summary>
    [AvaloniaFact]
    public async Task TheDirectionControlIsDrawnOverThePicture()
    {
        var (_, window, _) = await CreatePlannerAsync();
        try
        {
            var view = window.FindControl<TerrainFrameView>("PlannerCameraFrameView")!;
            var card = window.FindControl<Border>("PlannerTerrainFrame")!;
            foreach (var depth in new[] { PictureDepth(), PictureDepth(30, 10) })
            {
                view.Depth = depth;
                Settle(window);
                var picture = view.FrameRectangle;
                Assert.InRange(view.Bounds.Width - picture.Width, 0, 1);
                Assert.InRange(view.Bounds.Height - picture.Height, 0, 1);
                var instrument = window.FindControl<Grid>("CameraDirectionInstrument")!;
                var pad = window.FindControl<CameraDirectionPad>("CameraDirection")!;
                Assert.Equal(new Rect(view.Bounds.Size), In(instrument, view));
                Assert.Equal(new Rect(view.Bounds.Size), In(pad, view));
                Assert.Equal(view.Bounds.Height, pad.TapeBounds.Bottom, 0);
                Assert.Equal(0, pad.LadderBounds.Left);
                foreach (var name in new[] { "CameraTurnLeftButton", "CameraTurnRightButton", "CameraTiltUpButton", "CameraTiltDownButton" })
                    Assert.True(new Rect(view.Bounds.Size).Contains(In(window.FindControl<RepeatButton>(name)!, view)), $"{name} should be on the picture");
                // Directly under the picture is the status line: no strip of its own between them.
                var below = In(window.FindControl<ReadingsPanel>("CameraFrameReadings")!, card).Top - In(view, card).Bottom;
                Assert.InRange(below, 0, 4);
            }
            // The scales are drawn light on the picture's own fixed tones, over a fade, not an opaque panel.
            var direction = window.FindControl<CameraDirectionPad>("CameraDirection")!;
            Assert.Equal(Avalonia.Media.Brushes.Transparent, direction.ChromeBackground);
            Assert.Equal(CameraDirectionPad.OverlayForeground, direction.ChromeForeground);

            var stage = TestMarkup.LoadMainWindow().Descendants().Single(element => NameOf(element) == "CameraFrameStage");
            Assert.Equal(["PlannerCameraFrameView", "CameraDirectionInstrument"], stage.Elements().Select(NameOf));
            Assert.DoesNotContain(TestMarkup.LoadMainWindow().Descendants(), element => NameOf(element) == "CameraDirectionBacking");
        }
        finally { Close(window); }
    }

    /// <summary>
    /// The frame's own picture shows through the control. Its middle is exactly as the frame draws it, the
    /// control adding nothing there. Along the two edges the scales' backing only darkens what is beneath:
    /// with the ticks and figures included, each edge band is darker on average than the bare picture, never
    /// greyed over. The picture keeps a visible edge.
    /// </summary>
    [AvaloniaFact]
    public async Task TheFramePictureShowsThroughTheControl()
    {
        var (_, window, _) = await CreatePlannerAsync();
        try
        {
            var view = window.FindControl<TerrainFrameView>("PlannerCameraFrameView")!;
            // Mid-grey terrain across the whole picture, so a veil of any colour would show.
            view.Depth = new CameraTerrainDepth([345, 0, 15], -10, 10,
                System.Collections.Immutable.ImmutableArray.CreateRange(Enumerable.Repeat(3000d, 3 * CameraTerrainDepth.Rows)));
            Settle(window);
            var stage = window.FindControl<Grid>("CameraFrameStage")!;
            var instrument = window.FindControl<Grid>("CameraDirectionInstrument")!;
            var size = new PixelSize((int)stage.Bounds.Width, (int)stage.Bounds.Height);
            byte[] Pixels()
            {
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
                bitmap.Render(stage);
                return RenderedPixels.Bgra(bitmap);
            }
            double Luminance(byte[] pixels, Rect area)
            {
                double sum = 0; var count = 0;
                for (var y = (int)area.Top; y < (int)area.Bottom; y++)
                    for (var x = (int)area.Left; x < (int)area.Right; x++)
                    {
                        var i = (y * size.Width + x) * 4;
                        sum += .0722 * pixels[i] + .7152 * pixels[i + 1] + .2126 * pixels[i + 2];
                        count++;
                    }
                return sum / count;
            }
            var overlaid = Pixels();
            instrument.IsVisible = false;
            Settle(window);
            var bare = Pixels();
            instrument.IsVisible = true;
            Settle(window);

            var middle = new Rect(size.Width * .45, size.Height * .25, size.Width * .3, size.Height * .2);
            Assert.Equal(Luminance(bare, middle), Luminance(overlaid, middle), 3);
            Assert.True(Luminance(bare, middle) > 60, "the frame's picture is drawn, not an empty box");
            var tape = new Rect(CameraDirectionPad.LadderFadeDepth, size.Height - CameraDirectionPad.TapeFadeDepth,
                size.Width - CameraDirectionPad.LadderFadeDepth, CameraDirectionPad.TapeFadeDepth);
            var ladder = new Rect(0, CameraDirectionPad.EdgeBand, CameraDirectionPad.LadderFadeDepth, size.Height - 2 * CameraDirectionPad.EdgeBand);
            foreach (var (name, band) in new[] { ("bearing tape", tape), ("pitch ladder", ladder) })
                Assert.True(Luminance(overlaid, band) < Luminance(bare, band), $"the {name}'s backing should darken the picture, not grey it over");
            // The backing reaches only its own edge: just inside it, the picture is as the frame draws it.
            var inside = new Rect(CameraDirectionPad.LadderFadeDepth + 30, size.Height - CameraDirectionPad.TapeFadeDepth - 12, 30, 6);
            Assert.Equal(Luminance(bare, inside), Luminance(overlaid, inside), 3);
            Assert.NotNull(view.Outline);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// The frame is the camera's own perspective: its centre is the camera's bearing and pitch, its edges half
    /// the field of view each way, and an angle in between falls at its tangent, as a rectilinear lens images it.
    /// A pitched camera's corners are exact, not approximated.
    /// </summary>
    [Fact]
    public void TheFrameIsTheCamerasOwnPerspective()
    {
        const double h = 73.7, v = 53.1;
        Assert.Equal((0, 4), CameraFrameProjection.Direction(0, 0, h, v, 4));
        var (edge, level) = CameraFrameProjection.Direction(1, 0, h, v, 0);
        Assert.Equal(h / 2, edge, 6);
        Assert.Equal(0, level, 6);
        Assert.Equal(v / 2 + 10, CameraFrameProjection.Direction(0, 1, h, v, 10).Altitude, 6);
        Assert.Equal(1, CameraFrameProjection.Fraction(h / 2, h), 9);
        // Halfway out in angle is less than halfway across: the tangent, not the angle, is linear on the sensor.
        Assert.Equal(Math.Tan(h / 4 * Math.PI / 180) / Math.Tan(h / 2 * Math.PI / 180), CameraFrameProjection.Fraction(h / 4, h), 9);
        Assert.True(CameraFrameProjection.Fraction(h / 4, h) < .5);
        // Looking up, a top corner's altitude is below the top centre's: the frame is a tilted plane, not a box of angles.
        Assert.True(CameraFrameProjection.Direction(1, 1, h, v, 30).Altitude < CameraFrameProjection.Direction(0, 1, h, v, 30).Altitude);
    }

    /// <summary>
    /// The scales are drawn to the picture: the pitch scale's level (0°) tick is where the true horizon is drawn,
    /// the camera's pitch mark is level with the frame's centre line, and the bearing scale's ends are the
    /// picture's edges, half the field of view either side of the camera's bearing. The spans are the frame's own.
    /// </summary>
    [AvaloniaFact]
    public async Task TheScalesStandWhereTheirAnglesAreDrawn()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var view = window.FindControl<TerrainFrameView>("PlannerCameraFrameView")!;
            var pad = window.FindControl<CameraDirectionPad>("CameraDirection")!;
            // Without a terrain picture, the spans are the camera's own field of view.
            Assert.Null(vm.TerrainFrameDepth);
            Assert.Equal(vm.Snapshot!.FieldOfView.HorizontalDegrees, vm.CameraFrameHorizontalFieldOfView, 9);
            Assert.Equal(vm.Snapshot.FieldOfView.VerticalDegrees, vm.CameraFrameVerticalFieldOfView, 9);
            Assert.Equal(vm.CameraFrameHorizontalFieldOfView, pad.HorizontalFieldOfView);

            const double h = 60, v = 40, pitch = 8;
            pad.Pitch = pitch;
            Settle(window);
            pad.HorizontalFieldOfView = h;
            pad.VerticalFieldOfView = v;
            view.Depth = new CameraTerrainDepth([180 - h / 2, 180, 180 + h / 2], pitch - v / 2, pitch + v / 2,
                System.Collections.Immutable.ImmutableArray.CreateRange(Enumerable.Repeat(2000d, 3 * CameraTerrainDepth.Rows)));
            Settle(window);
            // Within half a pixel: the pad's bounds are layout-rounded, the picture's rectangle exact.
            Assert.InRange(pad.HorizonY - view.AltitudeY(0)!.Value, -.5, .5);
            Assert.Equal(pad.Bounds.Height / 2, pad.AltitudeY(pitch), 3);
            Assert.True(pad.HorizonY > pad.Bounds.Height / 2, "looking up, the horizon is below the aim");
            Assert.Equal(view.FrameRectangle.Bottom, view.AltitudeY(pitch - v / 2)!.Value, 3);
            // Level, the bearing scale's ends are the picture's edges, half the field of view either side; pitched, a
            // bearing is placed along the camera's own altitude, so the edges hold only for the centre row.
            pad.Pitch = 0;
            Settle(window);
            Assert.Equal(0, pad.BearingX(-h / 2), 3);
            Assert.Equal(pad.Bounds.Width, pad.BearingX(h / 2), 3);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// Dragging across the picture turns the camera and dragging up tilts it, the arrows nudge it, and the
    /// wheel over the picture — the control included — is still the focal length, never the direction.
    /// </summary>
    [AvaloniaFact]
    public async Task TheOverlaidControlStillAimsAndTheWheelIsStillTheFocalLength()
    {
        var (vm, window, _) = await CreatePlannerAsync(new AppSettings(Equipment: WheelEquipment));
        try
        {
            vm.SelectedLens = vm.Lenses.Single(lens => lens.Id == "zoom");
            vm.FocalLength = 100;
            vm.CameraBearingDegrees = 90;
            vm.CameraPitchDegrees = 0;
            window.FindControl<TerrainFrameView>("PlannerCameraFrameView")!.Depth = PictureDepth();
            Settle(window);
            var pad = window.FindControl<CameraDirectionPad>("CameraDirection")!;
            var centre = pad.TranslatePoint(new Point(pad.Bounds.Width / 2, pad.Bounds.Height / 2), window)!.Value;
            window.MouseDown(centre, MouseButton.Left);
            window.MouseMove(centre + new Vector(40, -8), RawInputModifiers.LeftMouseButton);
            window.MouseUp(centre + new Vector(40, -8), MouseButton.Left);
            Settle(window);
            Assert.Equal(90 + 40 * CameraDirectionPad.DegreesPerPixel, vm.CameraBearingDegrees, 6);
            Assert.Equal(8 * CameraDirectionPad.DegreesPerPixel, vm.CameraPitchDegrees, 6);
            Assert.Equal(100, vm.FocalLength);

            var bearing = vm.CameraBearingDegrees;
            window.FindControl<RepeatButton>("CameraTurnLeftButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Settle(window);
            Assert.Equal(bearing - CameraDirectionPad.NudgeDegrees, vm.CameraBearingDegrees, 6);
            var pitch = vm.CameraPitchDegrees;
            window.FindControl<RepeatButton>("CameraTiltUpButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Settle(window);
            Assert.Equal(pitch + CameraDirectionPad.NudgeDegrees, vm.CameraPitchDegrees, 6);

            // The wheel over the control, scales included, is the frame's: the focal length moves, the direction does not.
            var direction = (vm.CameraBearingDegrees, vm.CameraPitchDegrees);
            window.MouseWheel(centre, new Vector(0, 1));
            Settle(window);
            Assert.Equal(105, vm.FocalLength);
            var tape = pad.TranslatePoint(pad.TapeBounds.Center, window)!.Value;
            window.MouseWheel(tape, new Vector(0, 1));
            Settle(window);
            Assert.Equal(110, vm.FocalLength);
            Assert.Equal(direction, (vm.CameraBearingDegrees, vm.CameraPitchDegrees));
        }
        finally { Close(window); }
    }

    /// <summary>Following the focused target, the control and its arrows give way, still showing the direction.</summary>
    [AvaloniaFact]
    public async Task FollowingTheFocusedTargetStillDisablesTheOverlaidControl()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            Target(vm, "sun").FocusCommand.Execute(null);
            vm.FollowFocusedTarget = true;
            Settle(window);
            Assert.True(vm.IsFollowingFocusedTarget);
            Assert.False(window.FindControl<CameraDirectionPad>("CameraDirection")!.IsEffectivelyEnabled);
            foreach (var name in new[] { "CameraTurnLeftButton", "CameraTurnRightButton", "CameraTiltUpButton", "CameraTiltDownButton" })
                Assert.False(window.FindControl<RepeatButton>(name)!.IsEffectivelyEnabled, $"{name} should give way");
            Assert.True(window.FindControl<CameraDirectionPad>("CameraDirection")!.IsEffectivelyVisible);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// The four values are one status line, in order, each value beside its label, the pairs joined by a dot
    /// between them and on one baseline, wherever the card has room for them; where it has not, a whole pair
    /// moves to the next line, and no line starts or ends with a dot.
    /// </summary>
    [AvaloniaFact]
    public async Task TheFourValuesAreOneStatusLine()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            vm.CameraBearingDegrees = 359.9;
            vm.CameraPitchDegrees = -45;
            window.FindControl<TerrainFrameView>("PlannerCameraFrameView")!.Depth = PictureDepth();
            window.FindControl<StackPanel>("PlannerTerrainHorizon")!.Children.OfType<TextBlock>().Last().Text = "+12.5°";
            window.FindControl<StackPanel>("PlannerTerrainObstruction")!.Children.OfType<TextBlock>().Last().Text = "12.3 mi";
            Settle(window);
            var row = window.FindControl<ReadingsPanel>("CameraFrameReadings")!;
            var pairs = row.Children.Where(child => !child.Classes.Contains(ReadingsPanel.SeparatorClass)).ToArray();
            Assert.Equal(["CameraBearingValue", "CameraPitchValue", "PlannerTerrainHorizon", "PlannerTerrainObstruction"], pairs.Select(child => child.Name));
            Assert.DoesNotContain(TestMarkup.LoadMainWindow().Descendants(), element => NameOf(element) is "CameraDirectionValues" or "PlannerTerrainReadings");
            var card = window.FindControl<Border>("PlannerTerrainFrame")!;
            var label = window.FindControl<StackPanel>("CameraBearingValue")!.Children[0];
            var value = window.FindControl<NumericUpDown>("CameraBearingInput")!;

            // A card with room for the line: all four on it, a dot between each two, one line tall.
            vm.SetPlannerOverlaySizes(cameraFrameWidthFraction: WideFraction);
            var width = window.Width;
            window.Width = 1900;
            window.Height = 1100;
            Settle(window);
            Assert.True(card.Bounds.Width >= 330, $"the card is {card.Bounds.Width} wide");
            Assert.All(pairs, pair => Assert.Equal(0, pair.Bounds.Y));
            Assert.Equal(pairs[0].Bounds.Height, row.Bounds.Height, 0);
            Assert.Equal(3, row.ShownSeparators.Count());
            Assert.All(row.ShownSeparators, dot => Assert.Equal(0, dot.Bounds.Y));
            Assert.InRange(In(value, card).Left - In(label, card).Right, 2, 6);
            // Each unit follows its value directly ("Pitch 0.0°"), however short the value.
            vm.CameraPitchDegrees = 0;
            Settle(window);
            foreach (var input in new[] { "CameraBearingInput", "CameraPitchInput" })
            {
                var box = window.FindControl<NumericUpDown>(input)!;
                var unit = ((StackPanel)box.Parent!).Children.OfType<TextBlock>().Single();
                Assert.InRange(In(unit, card).Left - In(box, card).Right, -.5, 1.5);
            }
            Assert.True(pairs[^1].Bounds.Right <= row.Bounds.Width + .5);

            // At the smallest card: whole pairs wrap, onto no more than two lines, with a dot only between pairs
            // on the same line.
            vm.SetPlannerOverlaySizes(cameraFrameWidthFraction: PlannerOverlaySizes.MinimumFraction);
            window.Width = width;
            Settle(window);
            Assert.Equal(MapInstrumentLayout.MinimumCameraFrameWidth, card.Bounds.Width, 0);
            var lines = pairs.Select(pair => pair.Bounds.Y).Distinct().Count();
            Assert.InRange(lines, 1, 2);
            Assert.Equal(pairs.Length - lines, row.ShownSeparators.Count());
            foreach (var dot in row.ShownSeparators)
            {
                var before = pairs.Last(pair => pair.Bounds.Right <= dot.Bounds.Left && pair.Bounds.Y == dot.Bounds.Y);
                Assert.Contains(pairs, pair => pair.Bounds.Left >= dot.Bounds.Right && pair.Bounds.Y == dot.Bounds.Y);
                Assert.NotNull(before);
            }
            Assert.All(pairs.GroupBy(pair => pair.Bounds.Y), line => Assert.Equal(0, line.Min(pair => pair.Bounds.X)));
        }
        finally { Close(window); }
    }

    /// <summary>A Camera frame share wide enough for the whole status line in a 1900-wide window.</summary>
    private const double WideFraction = .3;

    /// <summary>
    /// At the Camera frame's smallest width the control stays usable: every arrow a full touch target on the
    /// picture, clear of the other arrows and of both scales' centre marks, and the header's grip and legend
    /// clear of all of it.
    /// </summary>
    [AvaloniaFact]
    public async Task TheOverlaidControlStaysUsableAtTheSmallestFrame()
    {
        var (_, window, _) = await CreatePlannerAsync();
        try
        {
            window.FindControl<TerrainFrameView>("PlannerCameraFrameView")!.Depth = PictureDepth();
            Settle(window);
            var card = window.FindControl<Border>("PlannerTerrainFrame")!;
            Assert.Equal(MapInstrumentLayout.MinimumCameraFrameWidth, card.Bounds.Width, 0);
            var pad = window.FindControl<CameraDirectionPad>("CameraDirection")!;
            Assert.True(pad.TapeBounds.Width >= 60, $"the tape is {pad.TapeBounds.Width} wide");
            Assert.True(pad.LadderBounds.Height >= 40, $"the ladder is {pad.LadderBounds.Height} tall");
            var marks = new[]
            {
                new Rect(pad.TapeBounds.Center.X - 4, pad.TapeBounds.Top, 8, pad.TapeBounds.Height),
                new Rect(pad.LadderBounds.Left, pad.Bounds.Height / 2 - 4, pad.LadderBounds.Width, 8)
            };
            var names = new[] { "CameraTurnLeftButton", "CameraTurnRightButton", "CameraTiltUpButton", "CameraTiltDownButton" };
            var nudges = names.Select(name => In(window.FindControl<RepeatButton>(name)!, pad)).ToArray();
            for (var i = 0; i < nudges.Length; i++)
            {
                Assert.True(nudges[i].Width >= 28 && nudges[i].Height >= 28, $"{names[i]} is {nudges[i].Size}");
                Assert.DoesNotContain(marks, mark => mark.Intersects(nudges[i]));
                for (var j = i + 1; j < nudges.Length; j++)
                    Assert.False(nudges[i].Intersects(nudges[j]), $"{names[i]} and {names[j]} overlap");
            }
            // The header's grip and legend button keep clear of the arrows, and where they reach over the
            // picture's top edge they, not the control, take the pointer.
            foreach (var name in new[] { "PlannerCameraFrameResizeGrip", "PlannerTerrainLegendButton" })
            {
                var header = window.FindControl<Control>(name)!;
                foreach (var nudge in names)
                    Assert.False(In(header, card).Intersects(In(window.FindControl<RepeatButton>(nudge)!, card)), $"{name} should stay clear of {nudge}");
                var reach = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height - 1), window)!.Value;
                var hit = window.InputHitTest(reach) as Visual;
                Assert.True(hit == header || hit?.FindAncestorOfType<Control>() is { } owner && (owner == header || owner.GetVisualAncestors().Contains(header)),
                    $"{name} should own its bottom edge, not {hit}");
            }
        }
        finally { Close(window); }
    }
}
