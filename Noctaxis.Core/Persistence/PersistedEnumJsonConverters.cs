using System.Text.Json;
using System.Text.Json.Serialization;
using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Persistence;

/// <summary>
/// What one read of the state file could not represent: values this build does not know, typically
/// written by a newer build. A read that records anything here cannot be saved back without losing
/// them, so the caller must not write over the file it came from.
/// </summary>
internal sealed class PersistedStateReadReport
{
    private readonly List<string> _unrecognised = [];
    public IReadOnlyList<string> UnrecognisedValues => _unrecognised;
    public void Unrecognised(Type type, string value) => _unrecognised.Add($"{type.Name} '{value}'");
}

/// <summary>
/// Reads every persisted enum tolerantly. A value this build does not define never fails the file:
/// in a list it is dropped, a nullable value becomes "not set", and a required value takes the
/// semantically safe fallback for its type. Each such value is recorded in the read's report, so the
/// file is never silently replaced by a state that has lost it. Writing is unchanged (enum names).
/// </summary>
internal sealed class PersistedEnumConverterFactory(PersistedStateReadReport? report) : JsonConverterFactory
{
    private static readonly Dictionary<Type, object> Fallbacks = new()
    {
        [typeof(AppearanceMode)] = AppearanceMode.System,
        [typeof(ColourVisionMode)] = ColourVisionMode.None,
        [typeof(CameraOrientation)] = CameraOrientation.Landscape,
        // A lens preset is reset to Custom on every load in any case: the camera profile owns the sensor.
        [typeof(SensorPreset)] = SensorPreset.Custom,
        [typeof(ExternalMapProvider)] = new AppSettings().ExternalMapProvider,
    };

    // Persisted only as list members, where an unknown member is dropped and no fallback is needed.
    private static readonly Type[] ListOnly = [typeof(WeatherField)];

    /// <summary>
    /// The enums whose unknown values have been reviewed: a fallback, or list-only. Tests require this to
    /// cover every enum in the persisted state, so a new persisted enum needs a deliberate choice.
    /// </summary>
    internal static IReadOnlyCollection<Type> ReviewedEnums => [.. Fallbacks.Keys, .. ListOnly];

    public override bool CanConvert(Type typeToConvert) => EnumType(typeToConvert) is not null;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (typeToConvert.IsEnum)
            return Create(typeof(ScalarConverter<>), typeToConvert, typeToConvert);
        if (Nullable.GetUnderlyingType(typeToConvert) is { IsEnum: true } nullable)
            return (JsonConverter)Activator.CreateInstance(typeof(NullableConverter<>).MakeGenericType(nullable), report)!;
        var element = ListElement(typeToConvert)!;
        return (JsonConverter)Activator.CreateInstance(
            typeof(ListConverter<,>).MakeGenericType(typeToConvert, element), report, typeToConvert.IsArray)!;
    }

    private JsonConverter Create(Type definition, Type enumType, Type fallbackKey) =>
        (JsonConverter)Activator.CreateInstance(definition.MakeGenericType(enumType), report,
            Fallbacks.TryGetValue(fallbackKey, out var fallback) ? fallback : null)!;

    private static Type? EnumType(Type type) =>
        type.IsEnum ? type : Nullable.GetUnderlyingType(type) is { IsEnum: true } nullable ? nullable : ListElement(type);

    private static Type? ListElement(Type type)
    {
        if (type.IsArray) return type.GetElementType() is { IsEnum: true } element ? element : null;
        if (!type.IsGenericType) return null;
        var definition = type.GetGenericTypeDefinition();
        if (definition != typeof(IReadOnlyList<>) && definition != typeof(IReadOnlyCollection<>) &&
            definition != typeof(IEnumerable<>) && definition != typeof(IList<>) &&
            definition != typeof(ICollection<>) && definition != typeof(List<>)) return null;
        var argument = type.GetGenericArguments()[0];
        return argument.IsEnum ? argument : null;
    }

    private static bool TryRead<T>(ref Utf8JsonReader reader, out T value, out string raw) where T : struct, Enum
    {
        value = default;
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                raw = reader.GetString() ?? "";
                return Enum.TryParse(raw, ignoreCase: true, out value) && Enum.IsDefined(value);
            case JsonTokenType.Number:
                raw = reader.TryGetInt64(out var whole) ? whole.ToString(System.Globalization.CultureInfo.InvariantCulture) : "number";
                if (!reader.TryGetInt32(out var number)) return false;
                value = (T)Enum.ToObject(typeof(T), number);
                return Enum.IsDefined(value);
            default:
                throw new JsonException($"Expected a {typeof(T).Name} name.");
        }
    }

    private sealed class ScalarConverter<T>(PersistedStateReadReport? report, object? fallback) : JsonConverter<T>
        where T : struct, Enum
    {
        // Without a reviewed fallback the first defined value is used; the report still blocks saving.
        private readonly T _fallback = fallback is T known ? known : Enum.GetValues<T>()[0];

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (TryRead<T>(ref reader, out var value, out var raw)) return value;
            report?.Unrecognised(typeof(T), raw);
            return _fallback;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    private sealed class NullableConverter<T>(PersistedStateReadReport? report) : JsonConverter<T?>
        where T : struct, Enum
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (TryRead<T>(ref reader, out var value, out var raw)) return value;
            report?.Unrecognised(typeof(T), raw);
            return null;
        }

        public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
        {
            if (value is { } known) writer.WriteStringValue(known.ToString());
            else writer.WriteNullValue();
        }
    }

    private sealed class ListConverter<TCollection, T>(PersistedStateReadReport? report, bool isArray)
        : JsonConverter<TCollection> where T : struct, Enum
    {
        public override TCollection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException($"Expected a list of {typeof(T).Name}.");
            var values = new List<T>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (TryRead<T>(ref reader, out var value, out var raw)) values.Add(value);
                else report?.Unrecognised(typeof(T), raw);
            }
            if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Unterminated list.");
            return (TCollection)(object)(isArray || typeToConvert.IsInterface ? values.ToArray() : values);
        }

        public override void Write(Utf8JsonWriter writer, TCollection value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in (IEnumerable<T>)value!) writer.WriteStringValue(item.ToString());
            writer.WriteEndArray();
        }
    }
}
