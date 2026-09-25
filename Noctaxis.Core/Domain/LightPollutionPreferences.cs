namespace Noctaxis.Core.Domain;

public sealed record LightPollutionPreferences(bool IsVisible = false, double Opacity = .5,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(PaletteIdJsonConverter))] string PaletteId = "grayscale")
{
    public LightPollutionPreferences Normalised() => this with { Opacity = Math.Clamp(double.IsFinite(Opacity) ? Opacity : .5, 0, 1) };
}

/// <summary>A malformed optional palette value must not discard the rest of the user's settings.</summary>
public sealed class PaletteIdJsonConverter : System.Text.Json.Serialization.JsonConverter<string>
{
    public override string Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.String) return reader.GetString() ?? "grayscale";
        reader.Skip();
        return "grayscale";
    }
    public override void Write(System.Text.Json.Utf8JsonWriter writer, string value, System.Text.Json.JsonSerializerOptions options) => writer.WriteStringValue(value);
}
