using Avalonia;
using Avalonia.Controls;

namespace Noctaxis.Desktop.Controls;

/// <summary>
/// The Camera frame's heading row: its first child is the heading, its second an indicator placed after it, and
/// the rest are buttons kept at the row's right end. When the row is too narrow for all of them at their ease (a
/// small card, or large text), the indicator moves to its own line under the heading instead of squeezing the
/// heading or the buttons.
/// </summary>
public sealed class FrameHeaderPanel : Panel
{
    private double _headingWidth, _rowHeight, _trailingWidth;

    /// <summary>Whether the indicator is on its own line.</summary>
    public bool IsIndicatorWrapped { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count < 2) return base.MeasureOverride(availableSize);
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        var heading = Children[0];
        var indicator = Children[1];
        var trailing = Children.Skip(2).ToArray();
        _trailingWidth = 0;
        var trailingHeight = 0d;
        foreach (var child in trailing)
        {
            child.Measure(unbounded);
            _trailingWidth += child.DesiredSize.Width;
            trailingHeight = Math.Max(trailingHeight, child.DesiredSize.Height);
        }
        indicator.Measure(unbounded);
        heading.Measure(unbounded);
        var full = indicator.DesiredSize.Width;
        // An indicator whose content is an OptionalTailPanel can give up its tail (the rating) before it wraps.
        var minimal = indicator is ContentControl { Content: OptionalTailPanel tail } ? full - tail.OptionalWidth : full;
        var natural = heading.DesiredSize.Width + full + _trailingWidth;
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : natural;
        var room = width - heading.DesiredSize.Width - _trailingWidth;
        IsIndicatorWrapped = indicator.IsVisible && minimal > room + 0.5;
        // On the heading's line the indicator takes the room left (dropping its tail if it must); on its own
        // line it has the whole width.
        indicator.Measure(new Size(IsIndicatorWrapped ? width : Math.Max(0, room), double.PositiveInfinity));
        _headingWidth = Math.Max(0, width - _trailingWidth - (IsIndicatorWrapped ? 0 : indicator.DesiredSize.Width));
        heading.Measure(new Size(_headingWidth, double.PositiveInfinity));
        _rowHeight = Math.Max(Math.Max(heading.DesiredSize.Height, trailingHeight), IsIndicatorWrapped ? 0 : indicator.DesiredSize.Height);
        return IsIndicatorWrapped ? new Size(width, _rowHeight + indicator.DesiredSize.Height) : new Size(width, _rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count < 2) return base.ArrangeOverride(finalSize);
        var heading = Children[0];
        var indicator = Children[1];
        Rect InRow(double x, Control child, double width) =>
            new(x, (_rowHeight - child.DesiredSize.Height) / 2, width, child.DesiredSize.Height);
        heading.Arrange(InRow(0, heading, _headingWidth));
        var x = finalSize.Width - _trailingWidth;
        foreach (var child in Children.Skip(2))
        {
            child.Arrange(InRow(x, child, child.DesiredSize.Width));
            x += child.DesiredSize.Width;
        }
        if (IsIndicatorWrapped)
            indicator.Arrange(new Rect(0, _rowHeight, Math.Min(finalSize.Width, indicator.DesiredSize.Width), indicator.DesiredSize.Height));
        else
            indicator.Arrange(InRow(finalSize.Width - _trailingWidth - indicator.DesiredSize.Width, indicator, indicator.DesiredSize.Width));
        return finalSize;
    }
}
