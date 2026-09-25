using System.Text.Json;
using System.Text.Json.Serialization;

namespace Noctaxis.Core.Domain;

public enum ColourVisionMode { None, Protanopia, Deuteranopia, Tritanopia }
public enum AppearanceMode { System, Dark, Light }

[JsonConverter(typeof(AppearancePreferencesConverter))]
public sealed record AppearancePreferences(
    string ThemeFamilyId = "builtin.noctaxis",
    ColourVisionMode ColourVisionMode = ColourVisionMode.None,
    double TextScale = 1,
    AppearanceMode AppearanceMode = AppearanceMode.Dark)
{
    public AppearancePreferences Normalised()
    {
        var (family, mode) = ThemeFamilyId switch
        {
            "builtin.system" => ("builtin.noctaxis", AppearanceMode.System),
            "builtin.dark" => ("builtin.noctaxis", AppearanceMode.Dark),
            "builtin.light" => ("builtin.noctaxis", AppearanceMode.Light),
            _ => (string.IsNullOrWhiteSpace(ThemeFamilyId) ? "builtin.noctaxis" : ThemeFamilyId, AppearanceMode)
        };
        return this with { ThemeFamilyId = family,
            AppearanceMode = Enum.IsDefined(mode) ? mode : AppearanceMode.System,
            ColourVisionMode = Enum.IsDefined(ColourVisionMode) ? ColourVisionMode : ColourVisionMode.None,
            TextScale = Math.Clamp(double.IsFinite(TextScale) ? TextScale : 1, 1, 2) };
    }
}

/// <summary>Reads the former SelectedThemeId shape; writes only independent family/mode preferences.</summary>
public sealed class AppearancePreferencesConverter : JsonConverter<AppearancePreferences>
{
    public override AppearancePreferences Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        bool Property(string name, out JsonElement value)
        {
            if (root.TryGetProperty(name, out value)) return true;
            if (options.PropertyNameCaseInsensitive)
                foreach (var property in root.EnumerateObject())
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
            return false;
        }
        string? Text(string name) => Property(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        T EnumValue<T>(string name, T fallback) where T : struct, Enum =>
            Property(name, out var v) && Enum.TryParse<T>(v.ToString(), true, out var parsed) ? parsed : fallback;
        var family = Text("ThemeFamilyId");
        var id = family ?? Text("SelectedThemeId") ?? "builtin.noctaxis";
        var scale = Property("TextScale", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetDouble(out var value) ? value : 1;
        return new AppearancePreferences(id, EnumValue("ColourVisionMode", ColourVisionMode.None), scale,
            EnumValue("AppearanceMode", AppearanceMode.Dark)).Normalised();
    }
    public override void Write(Utf8JsonWriter writer, AppearancePreferences value, JsonSerializerOptions options)
    {
        var p = value.Normalised();
        writer.WriteStartObject();
        writer.WriteString("ThemeFamilyId", p.ThemeFamilyId);
        writer.WritePropertyName("AppearanceMode");
        JsonSerializer.Serialize(writer, p.AppearanceMode, options);
        writer.WritePropertyName("ColourVisionMode");
        JsonSerializer.Serialize(writer, p.ColourVisionMode, options);
        writer.WriteNumber("TextScale", p.TextScale);
        writer.WriteEndObject();
    }
}
