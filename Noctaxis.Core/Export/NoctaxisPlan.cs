using System.Text;
using System.Text.Json;
using NodaTime;
using NodaTime.Text;

namespace Noctaxis.Core.Export;

/// <summary>The focused target of a plan, by its stable catalogue identifier, and whether the camera follows it.</summary>
public sealed record NoctaxisPlanFocus(string TargetId, bool Follow);

/// <summary>
/// The minimal planning state a plan image carries, so it can be opened again: the moment (UTC), the place, the
/// camera's direction and the focused target. Nothing derived is kept — no elevation, terrain, weather,
/// astronomy, equipment, map or interface state — so an imported plan is always recalculated and fetched fresh.
/// </summary>
public sealed record NoctaxisPlan(Instant TimeUtc, double Latitude, double Longitude, double BearingDegrees, double PitchDegrees,
    NoctaxisPlanFocus? Focus)
{
    public const string Format = "noctaxis-plan";
    /// <summary>The schema this version writes.</summary>
    public const int SchemaVersion = 1;
    /// <summary>
    /// Every schema this version reads, each by its own reader in <see cref="Parse"/>. A schema is never dropped from
    /// here to simplify the code; a newer one is refused, since nothing promises an older reader can interpret it.
    /// </summary>
    public static IReadOnlyList<int> SupportedSchemaVersions { get; } = [1];

    /// <summary>
    /// The earliest and latest moments a plan may name: the whole calendar NodaTime handles, less a day at each end,
    /// so the moment can be shown in any time zone without running off the calendar.
    /// </summary>
    public static readonly Instant EarliestTime = Instant.FromUtc(1, 1, 2, 0, 0);
    public static readonly Instant LatestTime = Instant.FromUtc(9999, 12, 30, 0, 0);
    /// <summary>A camera pitch is a direction only between straight down and straight up.</summary>
    public const double MaximumPitchMagnitude = 90;

    /// <summary>The document, schema version 1, as compact JSON.</summary>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("format", Format);
            json.WriteNumber("schemaVersion", SchemaVersion);
            json.WriteString("timeUtc", InstantPattern.ExtendedIso.Format(TimeUtc));
            json.WriteStartObject("location");
            json.WriteNumber("latitude", Latitude);
            json.WriteNumber("longitude", Longitude);
            json.WriteEndObject();
            json.WriteStartObject("camera");
            json.WriteNumber("bearingDegrees", BearingDegrees);
            json.WriteNumber("pitchDegrees", PitchDegrees);
            json.WriteEndObject();
            if (Focus is { } focus)
            {
                json.WriteStartObject("focus");
                json.WriteString("target", focus.TargetId);
                json.WriteBoolean("follow", focus.Follow);
                json.WriteEndObject();
            }
            else json.WriteNull("focus");
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Reads a plan document of any supported schema. Unknown additional fields are ignored. Anything that would
    /// have to be guessed is refused with its reason, and nothing is returned:
    /// - a missing or wrongly typed required value;
    /// - a property named twice;
    /// - a schema this version does not know;
    /// - coordinates off the globe;
    /// - a time that is not UTC or is beyond the calendar;
    /// - a pitch that is not a direction.
    /// </summary>
    public static NoctaxisPlanReadResult Parse(string json)
    {
        JsonDocument document;
        // A property given twice could be read either way: refused, never resolved silently.
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false }); }
        catch (JsonException) { return NoctaxisPlanReadResult.Failed(NoctaxisPlanError.Malformed); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != Format)
                return NoctaxisPlanReadResult.Failed(NoctaxisPlanError.Malformed);
            if (!root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var schema) || schema < 1)
                return NoctaxisPlanReadResult.Failed(NoctaxisPlanError.Malformed);
            return schema switch
            {
                1 => ParseVersion1(root),
                // Newer than this version knows: its fields may mean something this reader would get wrong.
                _ => NoctaxisPlanReadResult.Failed(NoctaxisPlanError.UnsupportedSchema)
            };
        }
    }

    /// <summary>
    /// Schema 1, as first written. Every field is required. <c>focus</c> is present as null when nothing is
    /// focused; when something is, it has both <c>target</c> and <c>follow</c>.
    /// Units: degrees for coordinates, bearing and pitch; an ISO instant ending in Z for the time.
    /// </summary>
    private static NoctaxisPlanReadResult ParseVersion1(JsonElement root)
    {
        if (!root.TryGetProperty("timeUtc", out var time) || time.ValueKind != JsonValueKind.String)
            return NoctaxisPlanReadResult.Failed(NoctaxisPlanError.InvalidTime);
        var parsedTime = InstantPattern.ExtendedIso.Parse(time.GetString()!);
        if (!parsedTime.Success || parsedTime.Value < EarliestTime || parsedTime.Value > LatestTime)
            return NoctaxisPlanReadResult.Failed(NoctaxisPlanError.InvalidTime);

        if (!TryObject(root, "location", out var location) ||
            !TryNumber(location, "latitude", out var latitude) || !TryNumber(location, "longitude", out var longitude) ||
            Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180)
            return NoctaxisPlanReadResult.Failed(NoctaxisPlanError.InvalidCoordinates);

        if (!TryObject(root, "camera", out var camera) ||
            !TryNumber(camera, "bearingDegrees", out var bearing) || !TryNumber(camera, "pitchDegrees", out var pitch))
            return NoctaxisPlanReadResult.Failed(NoctaxisPlanError.Malformed);
        // Any bearing is a direction (370° is 10°), but a pitch beyond straight up or down is not one.
        if (Math.Abs(pitch) > MaximumPitchMagnitude) return NoctaxisPlanReadResult.Failed(NoctaxisPlanError.InvalidCamera);

        // Required, as null or the focus: an absent focus is not taken to mean "none".
        if (!root.TryGetProperty("focus", out var focusElement)) return NoctaxisPlanReadResult.Failed(NoctaxisPlanError.Malformed);
        NoctaxisPlanFocus? focus = null;
        if (focusElement.ValueKind != JsonValueKind.Null)
        {
            if (focusElement.ValueKind != JsonValueKind.Object ||
                !focusElement.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(target.GetString()) ||
                // Follow is true or false, never a value taken to mean one of them.
                !focusElement.TryGetProperty("follow", out var follow) || follow.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return NoctaxisPlanReadResult.Failed(NoctaxisPlanError.Malformed);
            focus = new(target.GetString()!.Trim(), follow.GetBoolean());
        }
        return new(new NoctaxisPlan(parsedTime.Value, latitude, longitude, bearing, pitch, focus), NoctaxisPlanError.None);
    }

    private static bool TryObject(JsonElement parent, string name, out JsonElement value) =>
        parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;

    private static bool TryNumber(JsonElement parent, string name, out double value)
    {
        value = double.NaN;
        return parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number &&
               element.TryGetDouble(out value) && double.IsFinite(value);
    }
}

/// <summary>Why a plan image could not be read. <see cref="None"/>: it was.</summary>
public enum NoctaxisPlanError
{
    None,
    /// <summary>The data is not an image Noctaxis recognises.</summary>
    NotAnImage,
    /// <summary>An image, but not a PNG: only PNG plan images carry a plan.</summary>
    UnsupportedImageFormat,
    /// <summary>A PNG with no Noctaxis plan in it.</summary>
    NoPlan,
    /// <summary>The plan is not a well-formed Noctaxis plan.</summary>
    Malformed,
    /// <summary>A plan from a newer version of Noctaxis.</summary>
    UnsupportedSchema,
    InvalidCoordinates,
    InvalidTime,
    /// <summary>The camera's pitch is not a direction (beyond straight up or straight down).</summary>
    InvalidCamera,
    /// <summary>
    /// A scouting card from an earlier Noctaxis (its <c>Noctaxis.ExportData</c> metadata). It records what the card showed,
    /// not a plan: it has no camera pitch or focus, and its bearing is the target's, not the camera's.
    /// </summary>
    LegacyScoutingCard
}

public sealed record NoctaxisPlanReadResult(NoctaxisPlan? Plan, NoctaxisPlanError Error)
{
    public static NoctaxisPlanReadResult Failed(NoctaxisPlanError error) => new(null, error);
}
