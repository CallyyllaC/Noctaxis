using Noctaxis.Core.Localisation;
using System.Globalization;

namespace Noctaxis.Core.Measurements;

public enum MeasurementSystem
{
    Metric,
    Imperial,
    /// <summary>UK Mixed: contemporary British usage, not historical Imperial.</summary>
    Uk
}

public readonly record struct MeasurementValue(double Value, string Unit, int DecimalPlaces = 0)
{
    /// <summary>
    /// The number with its unit. The unit comes from the measurement profile; how the number is
    /// written (decimal separator, digits) comes from the regional culture. Neither decides the other.
    /// </summary>
    /// <param name="numberFormat">A numeric format in place of the fixed decimal places, for example <c>0.#</c>.</param>
    public string Format(string? numberFormat = null) => Strings.Format("Units_Value_Format",
        Value.ToString(numberFormat ?? $"F{DecimalPlaces}", CultureInfo.CurrentCulture), Unit);
}

/// <summary>
/// A unit a quantity is shown in: its symbol and spoken name (resource keys) and the linear
/// conversion from the unit the quantity is stored in.
/// </summary>
/// <param name="CoarseStep">The step an approximate value is rounded to, in this unit.</param>
public sealed record MeasurementUnit(string SymbolKey, string NameKey, double Scale, double Offset = 0,
    int DecimalPlaces = 0, double CoarseStep = 1)
{
    public string Symbol => Strings.Get(SymbolKey);
    /// <summary>The unit in words, for accessible names: "metres", "feet".</summary>
    public string Name => Strings.Get(NameKey);
    public double FromBase(double value) => value * Scale + Offset;
    public double ToBase(double value) => (value - Offset) / Scale;
    public MeasurementValue Of(double baseValue) => new(FromBase(baseValue), Symbol, DecimalPlaces);
    public MeasurementValue Of(double baseValue, int decimalPlaces) => new(FromBase(baseValue), Symbol, decimalPlaces);
}

/// <summary>
/// What a measurement system means, in one place: for every physical quantity Noctaxis shows, the
/// unit and conversion under that system. Screens ask the profile; none decides for itself what
/// "Imperial" or "UK Mixed" is. Values are stored in SI units and converted only for display.
/// </summary>
/// <remarks>
/// The profile is an explicit user choice. It does not follow the application language or the
/// regional culture, and it covers terrestrial and environmental quantities only: focal length,
/// sensor size, angles, magnitudes and times keep their conventional units in every profile.
/// See docs/localisation.md for the full mapping.
/// </remarks>
public sealed class MeasurementProfile
{
    private const double MetresPerMile = 1_609.344;
    private const double FeetPerMetre = 1 / 0.3048;

    private static readonly MeasurementUnit Celsius = new("Units_Celsius", "Units_Celsius_Name", 1);
    private static readonly MeasurementUnit Fahrenheit = new("Units_Fahrenheit", "Units_Fahrenheit_Name", 9d / 5, 32);
    private static readonly MeasurementUnit KilometresPerHour = new("Units_KilometresPerHour", "Units_KilometresPerHour_Name", 3.6);
    private static readonly MeasurementUnit MilesPerHour = new("Units_MilesPerHour", "Units_MilesPerHour_Name", 3_600 / MetresPerMile);
    private static readonly MeasurementUnit Kilometres = new("Units_Kilometres", "Units_Kilometres_Name", 1, DecimalPlaces: 1);
    private static readonly MeasurementUnit Miles = new("Units_Miles", "Units_Miles_Name", 1_000 / MetresPerMile, DecimalPlaces: 1);
    private static readonly MeasurementUnit Metres = new("Units_Metres", "Units_Metres_Name", 1, CoarseStep: 10);
    private static readonly MeasurementUnit Feet = new("Units_Feet", "Units_Feet_Name", FeetPerMetre, CoarseStep: 50);
    private static readonly MeasurementUnit Millimetres = new("Units_Millimetres", "Units_Millimetres_Name", 1, DecimalPlaces: 1);
    private static readonly MeasurementUnit Inches = new("Units_Inches", "Units_Inches_Name", 1 / 25.4, DecimalPlaces: 2);
    private static readonly MeasurementUnit Hectopascals = new("Units_Hectopascals", "Units_Hectopascals_Name", 1);
    private static readonly MeasurementUnit InchesOfMercury = new("Units_InchesOfMercury", "Units_InchesOfMercury_Name", 1 / 33.8638866667, DecimalPlaces: 2);

    public static MeasurementProfile Metric { get; } = new(MeasurementSystem.Metric, MeasurementUnits.MetricId,
        Celsius, KilometresPerHour, Kilometres, Metres, Metres, Millimetres, Hectopascals, 1_000);

    /// <summary>Miles and miles per hour; everything else metric, as in everyday British use.</summary>
    public static MeasurementProfile UkMixed { get; } = new(MeasurementSystem.Uk, MeasurementUnits.UkId,
        Celsius, MilesPerHour, Miles, Metres, Metres, Millimetres, Hectopascals, MetresPerMile);

    public static MeasurementProfile Imperial { get; } = new(MeasurementSystem.Imperial, MeasurementUnits.ImperialId,
        Fahrenheit, MilesPerHour, Miles, Feet, Feet, Inches, InchesOfMercury, MetresPerMile);

    public static IReadOnlyList<MeasurementProfile> All { get; } = [Metric, UkMixed, Imperial];

    public static MeasurementProfile For(MeasurementSystem system) => system switch
    {
        MeasurementSystem.Metric => Metric,
        MeasurementSystem.Imperial => Imperial,
        _ => UkMixed
    };

    /// <summary>The profile for a stored identifier; see <see cref="MeasurementUnits.Parse"/>.</summary>
    public static MeasurementProfile For(string? id) => For(MeasurementUnits.Parse(id));

    private MeasurementProfile(MeasurementSystem system, string id, MeasurementUnit temperature, MeasurementUnit windSpeed,
        MeasurementUnit longDistance, MeasurementUnit shortDistance, MeasurementUnit height, MeasurementUnit precipitation,
        MeasurementUnit pressure, double longDistanceFromMetres)
    {
        System = system;
        Id = id;
        Temperature = temperature;
        WindSpeed = windSpeed;
        LongDistance = longDistance;
        ShortDistance = shortDistance;
        Height = height;
        Precipitation = precipitation;
        Pressure = pressure;
        LongDistanceFromMetres = longDistanceFromMetres;
    }

    public MeasurementSystem System { get; }
    /// <summary>The identifier saved in settings. Technical: never translated.</summary>
    public string Id { get; }

    /// <summary>Air temperature and dew point. Stored in degrees Celsius.</summary>
    public MeasurementUnit Temperature { get; }
    /// <summary>Wind speed and gusts. Stored in metres per second.</summary>
    public MeasurementUnit WindSpeed { get; }
    /// <summary>Visibility and other long distances over the ground. Stored in kilometres.</summary>
    public MeasurementUnit LongDistance { get; }
    /// <summary>Distances over the ground shorter than <see cref="LongDistanceFromMetres"/>. Stored in metres.</summary>
    public MeasurementUnit ShortDistance { get; }
    /// <summary>Ground elevation above sea level and heights above the ground. Stored in metres.</summary>
    public MeasurementUnit Height { get; }
    /// <summary>Precipitation amount. Stored in millimetres.</summary>
    public MeasurementUnit Precipitation { get; }
    /// <summary>Atmospheric pressure. Stored in hectopascals. No screen shows pressure yet; the rule is fixed here for when one does.</summary>
    public MeasurementUnit Pressure { get; }
    /// <summary>Where a distance stops being short: one kilometre, or one mile.</summary>
    public double LongDistanceFromMetres { get; }

    /// <summary>A distance over the ground, in the short or the long unit as its size requires.</summary>
    public MeasurementValue Distance(double metres) => metres < LongDistanceFromMetres
        ? ShortDistance.Of(metres)
        : LongDistance.Of(metres / 1_000);

    /// <summary>
    /// A distance that is an estimate: a short one is rounded to the unit's coarse step (10 m, 50 ft)
    /// and never shown as less than one step.
    /// </summary>
    public MeasurementValue ApproximateDistance(double metres)
    {
        if (metres >= LongDistanceFromMetres) return LongDistance.Of(metres / 1_000);
        var step = ShortDistance.CoarseStep;
        var rounded = Math.Round(ShortDistance.FromBase(metres) / step, MidpointRounding.AwayFromZero) * step;
        return new MeasurementValue(Math.Max(step, rounded), ShortDistance.Symbol);
    }

    /// <summary>
    /// A round length for a scale bar, no longer than <paramref name="maximumMetres"/>: 1, 2 or 5
    /// times a power of ten in the unit the length is shown in.
    /// </summary>
    public (double Metres, MeasurementValue Label) ScaleBar(double maximumMetres)
    {
        var isLong = maximumMetres >= LongDistanceFromMetres;
        var unit = isLong ? LongDistance : ShortDistance;
        var maximum = unit.FromBase(isLong ? maximumMetres / 1_000 : maximumMetres);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(maximum)));
        var leading = maximum / magnitude;
        var length = (leading >= 5 ? 5 : leading >= 2 ? 2 : 1) * magnitude;
        return (unit.ToBase(length) * (isLong ? 1_000 : 1), new MeasurementValue(length, unit.Symbol));
    }
}

/// <summary>
/// The measurement systems the user can choose, their stored identifiers, and formatting shortcuts.
/// What each system means is defined by <see cref="MeasurementProfile"/> alone.
/// </summary>
public static class MeasurementUnits
{
    public const string MetricId = "Metric";
    public const string ImperialId = "Imperial";
    /// <summary>UK Mixed. The identifier predates the name and is kept so saved settings still load.</summary>
    public const string UkId = "UK";
    /// <summary>The system a new installation starts with.</summary>
    public const string DefaultId = UkId;

    /// <summary>The identifiers in the order they are offered.</summary>
    public static IReadOnlyList<string> Options { get; } = [MetricId, UkId, ImperialId];

    public static string NormaliseId(string? value) => MeasurementProfile.For(Parse(value)).Id;

    /// <summary>
    /// The system for a stored identifier, compared without regard to case or culture. A missing or
    /// unrecognised value is the default, UK Mixed; every identifier Noctaxis has ever saved
    /// ("Metric", "Imperial", "UK") is recognised and keeps its meaning.
    /// </summary>
    public static MeasurementSystem Parse(string? value)
    {
        var id = value?.Trim();
        if (string.Equals(id, MetricId, StringComparison.OrdinalIgnoreCase)) return MeasurementSystem.Metric;
        if (string.Equals(id, ImperialId, StringComparison.OrdinalIgnoreCase)) return MeasurementSystem.Imperial;
        return MeasurementSystem.Uk;
    }

    public static MeasurementValue Temperature(double celsius, MeasurementSystem system) =>
        MeasurementProfile.For(system).Temperature.Of(celsius);

    public static MeasurementValue WindSpeed(double metresPerSecond, MeasurementSystem system) =>
        MeasurementProfile.For(system).WindSpeed.Of(metresPerSecond);

    public static MeasurementValue Visibility(double kilometres, MeasurementSystem system) =>
        MeasurementProfile.For(system).LongDistance.Of(kilometres);

    public static MeasurementValue Precipitation(double millimetres, MeasurementSystem system) =>
        MeasurementProfile.For(system).Precipitation.Of(millimetres);

    public static MeasurementValue Pressure(double hectopascals, MeasurementSystem system) =>
        MeasurementProfile.For(system).Pressure.Of(hectopascals);

    public static string FormatTemperature(double? value, MeasurementSystem system) =>
        value.HasValue ? Temperature(value.Value, system).Format() : "—";

    public static string FormatWindSpeed(double? value, MeasurementSystem system) =>
        value.HasValue ? WindSpeed(value.Value, system).Format() : "—";

    public static string FormatVisibility(double? value, MeasurementSystem system) =>
        value.HasValue ? Visibility(value.Value, system).Format() : "—";

    public static string FormatPrecipitation(double? value, MeasurementSystem system) =>
        value.HasValue ? Precipitation(value.Value, system).Format() : "—";
}
