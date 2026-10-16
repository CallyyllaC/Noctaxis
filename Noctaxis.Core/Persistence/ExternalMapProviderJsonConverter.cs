using System.Text.Json;
using System.Text.Json.Serialization;
using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Persistence;

// Preserve existing state when loading the removed Street View preference. A provider this version
// does not know (written by a newer build) falls back to the default and is reported, never fatal.
internal sealed class ExternalMapProviderJsonConverter(PersistedStateReadReport? report = null)
    : JsonConverter<ExternalMapProvider>
{
    private static readonly ExternalMapProvider Fallback = new AppSettings().ExternalMapProvider;

    public override ExternalMapProvider Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string raw;
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            raw = value ?? "";
            if (string.Equals(value, "GoogleStreetView", StringComparison.OrdinalIgnoreCase) || value == "2")
                return ExternalMapProvider.GoogleMaps;
            if (Enum.TryParse<ExternalMapProvider>(value, true, out var provider) && Enum.IsDefined(provider))
                return provider;
        }
        else if (reader.TokenType == JsonTokenType.Number)
        {
            raw = reader.TryGetInt64(out var whole) ? whole.ToString(System.Globalization.CultureInfo.InvariantCulture) : "number";
            if (reader.TryGetInt32(out var value))
            {
                if (value == 2) return ExternalMapProvider.GoogleMaps;
                if (Enum.IsDefined((ExternalMapProvider)value)) return (ExternalMapProvider)value;
            }
        }
        else throw new JsonException("Invalid external map provider.");
        report?.Unrecognised(typeof(ExternalMapProvider), raw);
        return Fallback;
    }

    public override void Write(Utf8JsonWriter writer, ExternalMapProvider value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
