using System.Text.Json;
using System.Text.Json.Serialization;
using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Persistence;

// Preserve existing state when loading the removed Street View preference.
internal sealed class ExternalMapProviderJsonConverter : JsonConverter<ExternalMapProvider>
{
    public override ExternalMapProvider Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (string.Equals(value, "GoogleStreetView", StringComparison.OrdinalIgnoreCase) || value == "2")
                return ExternalMapProvider.GoogleMaps;
            if (Enum.TryParse<ExternalMapProvider>(value, true, out var provider) && Enum.IsDefined(provider))
                return provider;
        }
        else if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value))
        {
            if (value == 2) return ExternalMapProvider.GoogleMaps;
            if (Enum.IsDefined((ExternalMapProvider)value)) return (ExternalMapProvider)value;
        }
        throw new JsonException("Invalid external map provider.");
    }

    public override void Write(Utf8JsonWriter writer, ExternalMapProvider value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
