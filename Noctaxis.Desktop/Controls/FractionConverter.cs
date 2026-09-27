using System.Globalization;
using Avalonia.Data.Converters;

namespace Noctaxis.Desktop.Controls;

/// <summary>Multiplies a length by the fraction given as the converter parameter (invariant culture).</summary>
public sealed class FractionConverter : IValueConverter
{
    public static FractionConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double length && double.IsFinite(length) &&
        double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction)
            ? length * fraction
            : double.PositiveInfinity;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
