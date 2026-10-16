using Avalonia;
using Avalonia.Controls;

namespace Noctaxis.Desktop.Controls;

/// <summary>
/// A row whose first child always shows and whose later children show only while they fit the width it is
/// given, left out from the end otherwise. It changes no property of its children to do so, so it can never
/// set off another layout pass: a child left out is simply arranged with no size.
/// </summary>
public sealed class OptionalTailPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<OptionalTailPanel, double>(nameof(Spacing), 4);

    private int _shown;

    public OptionalTailPanel() => ClipToBounds = true;

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    /// <summary>How many children are shown, counting from the first.</summary>
    public int ShownCount => _shown;

    /// <summary>
    /// The width the children after the first add at their ease, spacing included: what the row gives up when
    /// it shows only its first child. Known after a measure.
    /// </summary>
    public double OptionalWidth { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        double width = 0, height = 0, optional = 0;
        var fitting = true;
        _shown = 0;
        for (var index = 0; index < Children.Count; index++)
        {
            var child = Children[index];
            child.Measure(unbounded);
            if (!child.IsVisible) { if (fitting) _shown++; continue; }
            var added = (width > 0 || index > 0 ? Spacing : 0) + child.DesiredSize.Width;
            if (index > 0) optional += added;
            if (!fitting) continue;
            if (index > 0 && width + added > availableSize.Width + 0.5) { fitting = false; continue; }
            width += index == 0 ? child.DesiredSize.Width : added;
            height = Math.Max(height, child.DesiredSize.Height);
            _shown++;
        }
        OptionalWidth = optional;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0d;
        for (var index = 0; index < Children.Count; index++)
        {
            var child = Children[index];
            // Left out: given no size, past the panel's edge, where its clip keeps it from drawing or taking a pointer.
            if (index >= _shown || !child.IsVisible)
            {
                child.Arrange(new Rect(finalSize.Width + 1, 0, 0, 0));
                continue;
            }
            child.Arrange(new Rect(x, (finalSize.Height - child.DesiredSize.Height) / 2, child.DesiredSize.Width, child.DesiredSize.Height));
            x += child.DesiredSize.Width + Spacing;
        }
        return finalSize;
    }
}
