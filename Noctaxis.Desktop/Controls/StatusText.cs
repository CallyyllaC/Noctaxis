using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Noctaxis.Desktop.Controls;

public enum StatusKind { Information, Success, Warning, Error }

/// <summary>
/// A status message in its theme status colour with a shape-coded icon (circled i, circled tick,
/// triangle, octagon), so the kind of message never rests on colour alone. Hidden when empty.
/// </summary>
public sealed class StatusText : Grid
{
    public static readonly StyledProperty<StatusKind> KindProperty =
        AvaloniaProperty.Register<StatusText, StatusKind>(nameof(Kind), StatusKind.Information);
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<StatusText, string?>(nameof(Text));
    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<StatusText>();

    // 16×16 icons; the even-odd rule cuts each glyph out of its shape.
    private static readonly Geometry[] Icons =
    [
        Geometry.Parse("F0 M8,0.5 A7.5,7.5 0 1 1 7.99,0.5 Z M7.1,6.4 H8.9 V12.2 H7.1 Z M7.1,3.4 H8.9 V5.2 H7.1 Z"),
        Geometry.Parse("F0 M8,0.5 A7.5,7.5 0 1 1 7.99,0.5 Z M3.9,8.3 L5.2,7 L7,8.8 L10.8,5 L12.1,6.3 L7,11.4 Z"),
        Geometry.Parse("F0 M8,0.6 L15.6,14.6 H0.4 Z M7.1,5.4 H8.9 V10.2 H7.1 Z M7.1,11.3 H8.9 V13.1 H7.1 Z"),
        Geometry.Parse("F0 M5,0.5 H11 L15.5,5 V11 L11,15.5 H5 L0.5,11 V5 Z M5.4,4.1 L8,6.7 L10.6,4.1 L11.9,5.4 L9.3,8 L11.9,10.6 L10.6,11.9 L8,9.3 L5.4,11.9 L4.1,10.6 L6.7,8 L4.1,5.4 Z")
    ];

    private readonly PathIcon _icon = new() { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 7, 0) };
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    public StatusText()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*");
        SetColumn(_text, 1);
        Children.Add(_icon);
        Children.Add(_text);
        IsVisible = false;
        Apply();
    }

    public StatusKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    internal PathIcon IconForTesting => _icon;
    internal TextBlock TextBlockForTesting => _text;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty) Apply();
        else if (change.Property == TextProperty)
        {
            _text.Text = Text;
            IsVisible = !string.IsNullOrWhiteSpace(Text);
        }
    }

    private void Apply()
    {
        _icon.Data = Icons[(int)Kind];
        var colour = new DynamicResourceExtension(Kind.ToString());
        _icon.Bind(PathIcon.ForegroundProperty, colour);
        _text.Bind(TextBlock.ForegroundProperty, colour);
        Avalonia.Automation.AutomationProperties.SetName(_icon, Kind.ToString());
    }
}
