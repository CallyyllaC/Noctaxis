using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Noctaxis.Desktop.Controls;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The Layers flyout is a quick map control: it opens straight onto the layers, one compact row each —
/// switch, name and opacity on a line, the slider beneath — and a layer that is off keeps its slider,
/// dimmed and holding its value, so nothing moves.
/// </summary>
public sealed class PlannerLayersFlyoutTests
{
    private static System.Xml.Linq.XElement Markup() => System.Xml.Linq.XElement.Load(
        Path.Combine(TestPaths.DesktopProject, "Controls", "PlannerLayersControl.axaml"));

    [Fact]
    public void TheFlyoutHasNoHeadingsAndNoTooltipsThatRepeatWhatIsVisible()
    {
        var flyout = Markup().Descendants().Single(element => element.Name.LocalName == "Flyout");
        var text = flyout.ToString();
        foreach (var retired in new[] { "Planner_Layers_MapLayers_Heading", "Planner_Layers_Overlays_Label", "Planner_Layers_Opacity_Label" })
            Assert.DoesNotContain(retired, text);
        Assert.DoesNotContain(flyout.Descendants(), element => (string?)element.Attribute("Classes") == "section");
        // Nothing in the flyout needs hovering over to be understood.
        Assert.DoesNotContain(flyout.Descendants(), element => element.Attributes().Any(attribute => attribute.Name.LocalName == "ToolTip.Tip"));
    }

    [AvaloniaFact]
    public void EachLayerIsOneRowWithItsOpacityBesideItsNameAndItsSliderBeneath()
    {
        using var flyout = Open();
        foreach (var (toggleName, valueName, sliderName) in Rows)
        {
            var toggle = flyout.Controls.FindControl<CheckBox>(toggleName)!;
            var value = flyout.Controls.FindControl<TextBlock>(valueName)!;
            var slider = flyout.Controls.FindControl<Slider>(sliderName)!;
            var toggleAt = Position(toggle, flyout.Window);
            var valueAt = Position(value, flyout.Window);
            var sliderAt = Position(slider, flyout.Window);
            // The value is on the name's line, at the far side.
            Assert.InRange(valueAt.Y + value.Bounds.Height / 2, toggleAt.Y, toggleAt.Y + toggle.Bounds.Height);
            Assert.True(valueAt.X > toggleAt.X + toggle.Bounds.Width, $"{valueName} should sit opposite the name");
            Assert.Equal($"{slider.Value:F0}%", value.Text);
            // The slider is directly beneath, as wide as the row allows, and the whole row is compact.
            Assert.InRange(sliderAt.Y - (toggleAt.Y + toggle.Bounds.Height), -8, 2);
            Assert.True(slider.Bounds.Width >= 240, $"{sliderName} is {slider.Bounds.Width} wide");
            Assert.True(slider.Bounds.Height >= 32, $"{sliderName} is {slider.Bounds.Height} tall to grab");
        }
        // Two layers in well under the height the headed, labelled layout took (285 px).
        Assert.True(flyout.Content.Bounds.Height <= 170, $"the flyout is {flyout.Content.Bounds.Height} tall");
    }

    [AvaloniaFact]
    public void ALayerThatIsOffKeepsItsSliderInPlaceDimmedAndHoldingItsValue()
    {
        using var flyout = Open();
        foreach (var (toggleName, valueName, sliderName) in Rows)
        {
            var toggle = flyout.Controls.FindControl<CheckBox>(toggleName)!;
            var value = flyout.Controls.FindControl<TextBlock>(valueName)!;
            var slider = flyout.Controls.FindControl<Slider>(sliderName)!;
            slider.Value = 42;
            var height = flyout.Content.Bounds.Height;
            var at = Position(slider, flyout.Window);

            toggle.IsChecked = false;
            Settle(flyout.Window);
            Assert.True(slider.IsVisible);
            Assert.False(slider.IsEffectivelyEnabled);
            Assert.False(value.IsEffectivelyEnabled);
            Assert.Equal(42, slider.Value, 6);
            Assert.Equal("42%", value.Text);
            Assert.Equal(height, flyout.Content.Bounds.Height, 3);
            Assert.Equal(at, Position(slider, flyout.Window));

            toggle.IsChecked = true;
            Settle(flyout.Window);
            Assert.True(slider.IsEffectivelyEnabled);
            Assert.Equal(42, slider.Value, 6);
        }
    }

    /// <summary>
    /// Avalonia's slider ignores the wheel, so the opacity sliders take it themselves: only while the
    /// pointer is over a slider, a notch at a time, within the slider's range, and never while the
    /// layer is off. The keyboard still moves the slider in its own steps.
    /// </summary>
    [AvaloniaFact]
    public void TheWheelOverAnOpacitySliderMovesItAndNowhereElse()
    {
        using var flyout = Open();
        var slider = flyout.Controls.FindControl<Slider>("OpacitySlider")!;
        var value = flyout.Controls.FindControl<TextBlock>("LightPollutionOpacityValue")!;
        var toggle = flyout.Controls.FindControl<CheckBox>("LightPollutionToggle")!;
        var writes = 0;
        flyout.View.Layers.PersistAsync = () => { writes++; return Task.CompletedTask; };
        slider.Value = 50;
        void Wheel(Control over, double delta) { flyout.Window.MouseWheel(Centre(over, flyout.Window), new Vector(0, delta)); Settle(flyout.Window); }

        Wheel(slider, 1);
        Assert.Equal(50 + PlannerLayersControl.OpacityPerWheelNotch, slider.Value, 6);
        Assert.Equal(.55, flyout.View.CompositionForTesting.Runtime["light-pollution"].Opacity, 6);
        Wheel(slider, -2);
        Assert.Equal(45, slider.Value, 6);
        for (var i = 0; i < 4; i++) Wheel(slider, .25); // A trackpad's quarters make one notch.
        Assert.Equal(50, slider.Value, 6);

        // Not over the slider: nothing moves.
        Wheel(value, 1);
        Wheel(toggle, 1);
        Assert.Equal(50, slider.Value, 6);

        // Within the range, whatever the wheel does.
        for (var i = 0; i < 30; i++) Wheel(slider, 1);
        Assert.Equal(slider.Maximum, slider.Value, 6);

        // A wheel turn commits once the pointer leaves the slider.
        var before = writes;
        flyout.Window.MouseMove(Centre(toggle, flyout.Window));
        Settle(flyout.Window);
        Assert.Equal(before + 1, writes);

        // A layer that is off is not adjusted.
        toggle.IsChecked = false;
        Settle(flyout.Window);
        Wheel(slider, -1);
        Assert.Equal(slider.Maximum, slider.Value, 6);
        toggle.IsChecked = true;
        Settle(flyout.Window);

        // The keyboard still steps the slider.
        slider.Focus(NavigationMethod.Tab);
        flyout.Window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        flyout.Window.KeyReleaseQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Assert.Equal(slider.Maximum - slider.SmallChange, slider.Value, 6);
    }

    // ---- Harness -------------------------------------------------------------------------------------

    private static readonly (string Toggle, string Value, string Slider)[] Rows =
    [
        ("TerrainReliefToggle", "TerrainReliefOpacityValue", "TerrainReliefOpacitySlider"),
        ("LightPollutionToggle", "LightPollutionOpacityValue", "OpacitySlider")
    ];

    private sealed record OpenFlyout(PlannerMapView View, PlannerLayersControl Controls, Window Window, Control Content) : IDisposable
    {
        public void Dispose() => Window.Close();
    }

    private static OpenFlyout Open()
    {
        var view = new PlannerMapView { LightPollutionPreferences = new(true, .35, "grayscale") };
        var controls = new PlannerLayersControl { DataContext = view.Layers };
        var window = new Window { Content = controls, Width = 700, Height = 700 };
        window.Show();
        var button = controls.FindControl<Button>("LayersButton")!;
        button.Flyout!.ShowAt(button);
        Settle(window);
        return new(view, controls, window, (Control)((Flyout)button.Flyout).Content!);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static Point Position(Control control, Window window) => control.TranslatePoint(default, window)!.Value;

    private static Point Centre(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
}
