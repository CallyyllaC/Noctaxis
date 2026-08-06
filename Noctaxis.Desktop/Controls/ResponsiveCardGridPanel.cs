using Avalonia;
using Avalonia.Controls;

namespace Noctaxis.Desktop.Controls;

public readonly record struct ResponsiveCardGridMetrics(
    int ColumnCount,
    double CardWidth,
    double CardHeight,
    double HorizontalSpacing,
    double VerticalSpacing)
{
    public int RowCount(int itemCount) => itemCount <= 0 ? 0 : (itemCount + ColumnCount - 1) / ColumnCount;
}

public static class ResponsiveCardGridLayout
{
    public const double DefaultMinimumCardWidth = 530;
    public const double DefaultPreferredCardWidth = 580;
    public const double DefaultMaximumCardWidth = 640;
    public const double DefaultCardHeight = 240;
    public const double DefaultHorizontalSpacing = 14;
    public const double DefaultVerticalSpacing = 14;

    public static ResponsiveCardGridMetrics Calculate(
        double availableWidth,
        double minimumCardWidth = DefaultMinimumCardWidth,
        double preferredCardWidth = DefaultPreferredCardWidth,
        double maximumCardWidth = DefaultMaximumCardWidth,
        double cardHeight = DefaultCardHeight,
        double horizontalSpacing = DefaultHorizontalSpacing,
        double verticalSpacing = DefaultVerticalSpacing)
    {
        if (minimumCardWidth <= 0) throw new ArgumentOutOfRangeException(nameof(minimumCardWidth));
        if (preferredCardWidth < minimumCardWidth || preferredCardWidth > maximumCardWidth)
            throw new ArgumentOutOfRangeException(nameof(preferredCardWidth));
        if (maximumCardWidth < minimumCardWidth) throw new ArgumentOutOfRangeException(nameof(maximumCardWidth));
        if (cardHeight <= 0) throw new ArgumentOutOfRangeException(nameof(cardHeight));
        if (horizontalSpacing < 0) throw new ArgumentOutOfRangeException(nameof(horizontalSpacing));
        if (verticalSpacing < 0) throw new ArgumentOutOfRangeException(nameof(verticalSpacing));

        if (!double.IsFinite(availableWidth))
            return new(1, preferredCardWidth, cardHeight, horizontalSpacing, verticalSpacing);

        var usableWidth = Math.Max(0, availableWidth);
        var columns = Math.Max(1, (int)Math.Floor((usableWidth + horizontalSpacing) /
                                                  (minimumCardWidth + horizontalSpacing)));
        var widthForColumns = columns == 1
            ? usableWidth
            : (usableWidth - horizontalSpacing * (columns - 1)) / columns;
        var cardWidth = Math.Min(maximumCardWidth, Math.Max(0, widthForColumns));
        return new(columns, cardWidth, cardHeight, horizontalSpacing, verticalSpacing);
    }
}

/// <summary>
/// Lays out equal-width cards using as many columns as fit at the configured minimum width.
/// An incomplete final row remains left aligned and uses the same card width as every other row.
/// </summary>
public sealed class ResponsiveCardGridPanel : Panel
{
    public static readonly StyledProperty<double> MinimumCardWidthProperty =
        AvaloniaProperty.Register<ResponsiveCardGridPanel, double>(
            nameof(MinimumCardWidth), ResponsiveCardGridLayout.DefaultMinimumCardWidth);

    public static readonly StyledProperty<double> PreferredCardWidthProperty =
        AvaloniaProperty.Register<ResponsiveCardGridPanel, double>(
            nameof(PreferredCardWidth), ResponsiveCardGridLayout.DefaultPreferredCardWidth);

    public static readonly StyledProperty<double> MaximumCardWidthProperty =
        AvaloniaProperty.Register<ResponsiveCardGridPanel, double>(
            nameof(MaximumCardWidth), ResponsiveCardGridLayout.DefaultMaximumCardWidth);

    public static readonly StyledProperty<double> CardHeightProperty =
        AvaloniaProperty.Register<ResponsiveCardGridPanel, double>(
            nameof(CardHeight), ResponsiveCardGridLayout.DefaultCardHeight);

    public static readonly StyledProperty<double> HorizontalSpacingProperty =
        AvaloniaProperty.Register<ResponsiveCardGridPanel, double>(
            nameof(HorizontalSpacing), ResponsiveCardGridLayout.DefaultHorizontalSpacing);

    public static readonly StyledProperty<double> VerticalSpacingProperty =
        AvaloniaProperty.Register<ResponsiveCardGridPanel, double>(
            nameof(VerticalSpacing), ResponsiveCardGridLayout.DefaultVerticalSpacing);

    public double MinimumCardWidth
    {
        get => GetValue(MinimumCardWidthProperty);
        set => SetValue(MinimumCardWidthProperty, value);
    }

    public double PreferredCardWidth
    {
        get => GetValue(PreferredCardWidthProperty);
        set => SetValue(PreferredCardWidthProperty, value);
    }

    public double MaximumCardWidth
    {
        get => GetValue(MaximumCardWidthProperty);
        set => SetValue(MaximumCardWidthProperty, value);
    }

    public double CardHeight
    {
        get => GetValue(CardHeightProperty);
        set => SetValue(CardHeightProperty, value);
    }

    public double HorizontalSpacing
    {
        get => GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var metrics = Calculate(availableSize.Width);
        foreach (var child in Children)
            child.Measure(new Size(metrics.CardWidth, metrics.CardHeight));

        var rows = metrics.RowCount(Children.Count);
        var desiredHeight = rows == 0
            ? 0
            : rows * metrics.CardHeight + (rows - 1) * metrics.VerticalSpacing;
        var desiredWidth = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : metrics.CardWidth;
        return new Size(desiredWidth, desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var metrics = Calculate(finalSize.Width);
        for (var index = 0; index < Children.Count; index++)
        {
            var column = index % metrics.ColumnCount;
            var row = index / metrics.ColumnCount;
            var x = column * (metrics.CardWidth + metrics.HorizontalSpacing);
            var y = row * (metrics.CardHeight + metrics.VerticalSpacing);
            Children[index].Arrange(new Rect(x, y, metrics.CardWidth, metrics.CardHeight));
        }

        var rows = metrics.RowCount(Children.Count);
        var usedHeight = rows == 0
            ? 0
            : rows * metrics.CardHeight + (rows - 1) * metrics.VerticalSpacing;
        return new Size(finalSize.Width, usedHeight);
    }

    private ResponsiveCardGridMetrics Calculate(double availableWidth) => ResponsiveCardGridLayout.Calculate(
        availableWidth, MinimumCardWidth, PreferredCardWidth, MaximumCardWidth, CardHeight,
        HorizontalSpacing, VerticalSpacing);
}
