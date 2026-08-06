using Noctaxis.Desktop.Controls;

namespace Noctaxis.Desktop.Tests;

public sealed class ResponsiveCardGridLayoutTests
{
    [Theory]
    [InlineData(480, 1, 480)]
    [InlineData(1180, 2, 583)]
    [InlineData(1800, 3, 590.6666666667)]
    [InlineData(2400, 4, 589.5)]
    public void Calculate_UsesAdditionalColumnsWithoutOverstretchingCards(
        double availableWidth, int expectedColumns, double expectedCardWidth)
    {
        var result = ResponsiveCardGridLayout.Calculate(availableWidth);

        Assert.Equal(expectedColumns, result.ColumnCount);
        Assert.Equal(expectedCardWidth, result.CardWidth, precision: 6);
        Assert.InRange(result.CardWidth, 0, ResponsiveCardGridLayout.DefaultMaximumCardWidth);
    }

    [Fact]
    public void Calculate_NeverReturnsFewerThanOneColumn()
    {
        var result = ResponsiveCardGridLayout.Calculate(0);

        Assert.Equal(1, result.ColumnCount);
        Assert.Equal(0, result.CardWidth);
    }

    [Fact]
    public void Calculate_CapsCardWidthAndKeepsIncompleteFinalRowAtTheSameWidth()
    {
        var result = ResponsiveCardGridLayout.Calculate(1400);

        Assert.Equal(2, result.ColumnCount);
        Assert.Equal(ResponsiveCardGridLayout.DefaultMaximumCardWidth, result.CardWidth);
        Assert.Equal(2, result.RowCount(3));
        Assert.Equal(2, result.RowCount(4));
    }

    [Fact]
    public void Calculate_UsesPreferredWidthWhenMeasureWidthIsUnbounded()
    {
        var result = ResponsiveCardGridLayout.Calculate(double.PositiveInfinity);

        Assert.Equal(1, result.ColumnCount);
        Assert.Equal(ResponsiveCardGridLayout.DefaultPreferredCardWidth, result.CardWidth);
    }
}
