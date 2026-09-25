using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
namespace Noctaxis.Desktop.Controls;

/// <summary>Two-way radio selection without writing the unchecked transition back to the model.</summary>
public sealed class RadioChoiceConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string choice
            ? targetType.IsEnum ? Enum.Parse(targetType, choice) : choice
            : Avalonia.Data.BindingOperations.DoNothing;
}
