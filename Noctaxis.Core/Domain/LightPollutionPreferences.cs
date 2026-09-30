namespace Noctaxis.Core.Domain;

/// <remarks>
/// A stored opacity is always kept as saved: it cannot be told apart from one the user chose, so it
/// is never rewritten. <see cref="DefaultOpacity"/> applies only where no preference was stored.
/// </remarks>
public sealed record LightPollutionPreferences(bool IsVisible = false, double Opacity = LightPollutionPreferences.DefaultOpacity,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(PaletteIdJsonConverter))] string PaletteId = "grayscale")
{
    /// <summary>
    /// Visible at regional and national scale while leaving the map readable and room for terrain
    /// relief beneath it; users inspecting light pollution closely raise it themselves.
    /// </summary>
    public const double DefaultOpacity = .1;

    public LightPollutionPreferences Normalised() =>
        this with { Opacity = Math.Clamp(double.IsFinite(Opacity) ? Opacity : DefaultOpacity, 0, 1) };
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
