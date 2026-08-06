using Noctaxis.Core.Measurements;

namespace Noctaxis.Core.Tests;

public sealed class MeasurementUnitsTests
{
    [Theory]
    [InlineData("Metric", MeasurementSystem.Metric)]
    [InlineData("Imperial", MeasurementSystem.Imperial)]
    [InlineData("UK", MeasurementSystem.Uk)]
    [InlineData("uk", MeasurementSystem.Uk)]
    [InlineData("unknown", MeasurementSystem.Metric)]
    public void SavedUnitIdentifiers_AreParsedTolerantly(string value, MeasurementSystem expected) =>
        Assert.Equal(expected, MeasurementUnits.Parse(value));

    [Fact]
    public void Imperial_UsesFahrenheitMilesMphAndInches()
    {
        Assert.Equal(new MeasurementValue(32, "°F"), MeasurementUnits.Temperature(0, MeasurementSystem.Imperial));
        Assert.Equal("mph", MeasurementUnits.WindSpeed(1, MeasurementSystem.Imperial).Unit);
        Assert.Equal("mi", MeasurementUnits.Visibility(1, MeasurementSystem.Imperial).Unit);
        var precipitation = MeasurementUnits.Precipitation(25.4, MeasurementSystem.Imperial);
        Assert.Equal("in", precipitation.Unit);
        Assert.Equal(1, precipitation.Value, 10);
    }

    [Fact]
    public void Uk_UsesCelsiusMilesMphAndMillimetres()
    {
        Assert.Equal("°C", MeasurementUnits.Temperature(0, MeasurementSystem.Uk).Unit);
        Assert.Equal("mph", MeasurementUnits.WindSpeed(1, MeasurementSystem.Uk).Unit);
        Assert.Equal("mi", MeasurementUnits.Visibility(1, MeasurementSystem.Uk).Unit);
        Assert.Equal("mm", MeasurementUnits.Precipitation(1, MeasurementSystem.Uk).Unit);
    }

    [Fact]
    public void Metric_UsesSiWeatherUnits()
    {
        Assert.Equal("°C", MeasurementUnits.Temperature(0, MeasurementSystem.Metric).Unit);
        Assert.Equal("m/s", MeasurementUnits.WindSpeed(1, MeasurementSystem.Metric).Unit);
        Assert.Equal("km", MeasurementUnits.Visibility(1, MeasurementSystem.Metric).Unit);
        Assert.Equal("mm", MeasurementUnits.Precipitation(1, MeasurementSystem.Metric).Unit);
    }
}
