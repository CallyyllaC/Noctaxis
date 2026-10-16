using System.Globalization;
using Avalonia.Data.Converters;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Localisation;

namespace Noctaxis.Desktop.Controls;

/// <summary>
/// Names a planning time zone choice: an IANA identifier as itself, and the saved "system" choice as
/// "System". The saved identifier is unchanged; only its name is.
/// </summary>
public sealed class TimeZoneLabelConverter : IValueConverter
{
    public static TimeZoneLabelConverter Instance { get; } = new();

    public static string Label(string? id) =>
        string.Equals(id, AppSettings.UseSystemTimeZoneId, StringComparison.OrdinalIgnoreCase)
            ? Strings.Get("Settings_General_SystemTimeZone_Option") : id ?? string.Empty;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Label(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
