using System.Text.Json;
using System.Text.Json.Serialization;
using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Persistence;

/// <summary>
/// Reads a Planner docking value that the file does not recognise as its default instead of refusing
/// the file. A panel side or mode is where a window happened to be when it last closed; a name this
/// version does not know — a hand edit, or a layout written by a newer build — must cost the user that
/// panel's position and nothing else, never their locations and session as a quarantined file would.
/// </summary>
internal sealed class TolerantEnumJsonConverter<T>(T fallback) : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String &&
            Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            return parsed;
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number) &&
            Enum.IsDefined((T)(object)number))
            return (T)(object)number;
        return fallback;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
