using Avalonia;
using Avalonia.Controls;

namespace Noctaxis.Desktop.Controls;

/// <summary>
/// One status line of label–value pairs with a separator between each two, wrapping a whole pair at a time when
/// the line is too narrow. A separator is shown only between pairs that share a line, so no line starts or ends
/// with one. Separators are the children with the <see cref="SeparatorClass"/> class; every other child is a pair.
/// </summary>
public sealed class ReadingsPanel : Panel
{
    public const string SeparatorClass = "readingSeparator";
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<ReadingsPanel, double>(nameof(Spacing), 4);

    /// <summary>The space either side of a separator.</summary>
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    static ReadingsPanel() => AffectsMeasure<ReadingsPanel>(SpacingProperty);

    private static bool IsSeparator(Control child) => child.Classes.Contains(SeparatorClass);

    /// <summary>The separators shown, each between two pairs on the same line.</summary>
    internal IEnumerable<Control> ShownSeparators => Children.Where(child => IsSeparator(child) && child.Bounds.Width > 0);

    /// <summary>The pairs line by line, each but a line's first with the separator before it.</summary>
    private List<List<(Control? Separator, Control Pair)>> Lines(double width)
    {
        var lines = new List<List<(Control? Separator, Control Pair)>>();
        var x = 0d;
        Control? separator = null;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            if (IsSeparator(child)) { separator = child; continue; }
            var joined = 2 * Spacing + (separator?.DesiredSize.Width ?? 0) + child.DesiredSize.Width;
            if (lines.Count > 0 && separator is not null && x + joined <= width + .5)
            {
                lines[^1].Add((separator, child));
                x += joined;
            }
            else
            {
                lines.Add([(null, child)]);
                x = child.DesiredSize.Width;
            }
            separator = null;
        }
        return lines;
    }

    /// <summary>
    /// The narrowest width at which the pairs, as they measure now, take no more than two lines: the best place to
    /// break the line, as the wrapping above would at that width.
    /// </summary>
    internal double TwoLineWidth
    {
        get
        {
            var items = new List<(double Joined, double Alone)>();
            Control? separator = null;
            foreach (var child in Children)
            {
                if (!child.IsVisible) continue;
                if (IsSeparator(child)) { separator = child; continue; }
                items.Add((2 * Spacing + (separator?.DesiredSize.Width ?? 0) + child.DesiredSize.Width, child.DesiredSize.Width));
                separator = null;
            }
            double Line(int from, int to) => from >= to ? 0 : items[from].Alone + items.Skip(from + 1).Take(to - from - 1).Sum(item => item.Joined);
            var narrowest = Line(0, items.Count);
            for (var split = 1; split < items.Count; split++)
                narrowest = Math.Min(narrowest, Math.Max(Line(0, split), Line(split, items.Count)));
            return narrowest;
        }
    }

    private static double LineWidth(List<(Control? Separator, Control Pair)> line, double spacing) =>
        line.Sum(item => item.Pair.DesiredSize.Width + (item.Separator is { } separator ? separator.DesiredSize.Width + 2 * spacing : 0));

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var lines = Lines(availableSize.Width);
        return new(lines.Count == 0 ? 0 : lines.Max(line => LineWidth(line, Spacing)),
            lines.Sum(line => line.Max(item => item.Pair.DesiredSize.Height)));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var shown = new HashSet<Control>();
        var y = 0d;
        foreach (var line in Lines(finalSize.Width))
        {
            var height = line.Max(item => item.Pair.DesiredSize.Height);
            var x = 0d;
            foreach (var (separator, pair) in line)
            {
                if (separator is not null)
                {
                    x += Spacing;
                    separator.Arrange(new Rect(x, y, separator.DesiredSize.Width, height));
                    shown.Add(separator);
                    x += separator.DesiredSize.Width + Spacing;
                }
                pair.Arrange(new Rect(x, y, pair.DesiredSize.Width, height));
                x += pair.DesiredSize.Width;
            }
            y += height;
        }
        foreach (var child in Children)
            if (IsSeparator(child) && !shown.Contains(child)) child.Arrange(default);
        return finalSize;
    }
}
