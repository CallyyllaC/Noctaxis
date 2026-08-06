using System.Globalization;

namespace Noctaxis.Core.Measurements;

public enum MeasurementSystem
{
    Metric,
    Imperial,
    Uk
}

public readonly record struct MeasurementValue(double Value, string Unit, int DecimalPlaces = 0)
{
    public string Format() => $"{Value.ToString($"F{DecimalPlaces}", CultureInfo.CurrentCulture)} {Unit}";
}

/// <summary>
/// Defines the deliberately distinct metric, US-style imperial, and UK mixed-unit profiles.
/// Domain values remain stored in SI units; conversion happens only at presentation boundaries.
/// </summary>
public static class MeasurementUnits
{
    public const string MetricId = "Metric";
    public const string ImperialId = "Imperial";
    public const string UkId = "UK";

    public static IReadOnlyList<string> Options { get; } = [MetricId, ImperialId, UkId];

    public static string NormaliseId(string? value) => Parse(value) switch
    {
        MeasurementSystem.Imperial => ImperialId,
        MeasurementSystem.Uk => UkId,
        _ => MetricId
    };

    public static MeasurementSystem Parse(string? value)
    {
        if (string.Equals(value, ImperialId, StringComparison.OrdinalIgnoreCase))
            return MeasurementSystem.Imperial;
        if (string.Equals(value, UkId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "United Kingdom", StringComparison.OrdinalIgnoreCase))
            return MeasurementSystem.Uk;
        return MeasurementSystem.Metric;
    }

    public static MeasurementValue Temperature(double celsius, MeasurementSystem system) =>
        system == MeasurementSystem.Imperial
            ? new(celsius * 9 / 5 + 32, "°F")
            : new(celsius, "°C");

    public static MeasurementValue WindSpeed(double metresPerSecond, MeasurementSystem system) =>
        system == MeasurementSystem.Metric
            ? new(metresPerSecond, "m/s")
            : new(metresPerSecond * 2.2369362920544, "mph");

    public static MeasurementValue Visibility(double kilometres, MeasurementSystem system) =>
        system == MeasurementSystem.Metric
            ? new(kilometres, "km", 1)
            : new(kilometres * 0.62137119223733, "mi", 1);

    public static MeasurementValue Precipitation(double millimetres, MeasurementSystem system) =>
        system == MeasurementSystem.Imperial
            ? new(millimetres / 25.4, "in", 2)
            : new(millimetres, "mm", 1);

    public static string FormatTemperature(double? value, MeasurementSystem system) =>
        value.HasValue ? Temperature(value.Value, system).Format() : "—";

    public static string FormatWindSpeed(double? value, MeasurementSystem system) =>
        value.HasValue ? WindSpeed(value.Value, system).Format() : "—";

    public static string FormatVisibility(double? value, MeasurementSystem system) =>
        value.HasValue ? Visibility(value.Value, system).Format() : "—";

    public static string FormatPrecipitation(double? value, MeasurementSystem system) =>
        value.HasValue ? Precipitation(value.Value, system).Format() : "—";
}
