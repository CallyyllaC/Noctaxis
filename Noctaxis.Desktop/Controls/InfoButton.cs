using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace Noctaxis.Desktop.Controls;

/// <summary>
/// An explicit ⓘ beside a setting that opens its explanation: one click, tap, Enter or Space, never a hover.
/// The explanation is secondary — what the setting is must be clear from its own label — so it waits here
/// rather than sitting on the page. Name each one for what it explains ("About units").
/// </summary>
public sealed class InfoButton : Button
{
    /// <summary>The ⓘ glyph, shared with the Planner's own info buttons.</summary>
    public static readonly Geometry Glyph = Geometry.Parse(
        "F0 M12,2 A10,10 0 1 1 12,22 A10,10 0 1 1 12,2 Z M12,3.8 A8.2,8.2 0 1 0 12,20.2 A8.2,8.2 0 1 0 12,3.8 Z " +
        "M10.9,10 L13.1,10 L13.1,17.5 L10.9,17.5 Z M10.9,6.2 L13.1,6.2 L13.1,8.4 L10.9,8.4 Z");

    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<InfoButton, string?>(nameof(Text));

    /// <summary>The explanation; line breaks start new lines, so a short list reads as one.</summary>
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    private readonly TextBlock _explanation = new() { Classes = { "infoText" } };

    protected override Type StyleKeyOverride => typeof(Button);

    public InfoButton()
    {
        Classes.Add("settingInfo");
        var glyph = new Avalonia.Controls.Shapes.Path { Data = Glyph, Width = 14, Height = 14, Stretch = Stretch.Uniform };
        glyph.Bind(Shape.FillProperty, this.GetObservable(ForegroundProperty));
        Content = glyph;
        Flyout = new Flyout { Content = _explanation, Placement = PlacementMode.BottomEdgeAlignedLeft };
    }

    /// <summary>The explanation as shown, for tests.</summary>
    internal TextBlock Explanation => _explanation;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty) _explanation.Text = Text;
    }
}
