using System.Text.Json;
using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Locations;

public sealed record LocationExportDocument(
    int SchemaVersion,
    DateTimeOffset ExportedAtUtc,
    IReadOnlyList<SavedLocation> Locations,
    string Product = "Noctaxis");

public interface ILocationTransferService
{
    byte[] Export(IReadOnlyList<SavedLocation> locations);
    LocationExportDocument Import(ReadOnlySpan<byte> json);
    Task ExportToFileAsync(string path, IReadOnlyList<SavedLocation> locations,
        CancellationToken cancellationToken);
}

/// <summary>Versioned location metadata only; environmental tiles and thumbnails are excluded by design.</summary>
public sealed class LocationTransferService : ILocationTransferService
{
    /// <summary>The schema this version writes, and the newest it reads. Schema 1 is the only one so far.</summary>
    public const int CurrentSchemaVersion = 1;
    // A property given twice could be read either way, and a missing required value (one with no default, such as a
    // location's coordinate) or a null where none is allowed would be filled in by guesswork: all are refused.
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
    };

    public byte[] Export(IReadOnlyList<SavedLocation> locations) => JsonSerializer.SerializeToUtf8Bytes(
        new LocationExportDocument(CurrentSchemaVersion, DateTimeOffset.UtcNow, locations), _json);

    /// <summary>
    /// Reads a location export, entirely, before anything uses it. Unknown additional fields are ignored. These are
    /// refused, each with a reason, rather than guessed:
    /// - malformed JSON;
    /// - a schema newer than this version reads;
    /// - a missing location list;
    /// - a location with no identifier or name, coordinates off the globe, or an unknown sensor.
    /// </summary>
    public LocationExportDocument Import(ReadOnlySpan<byte> json)
    {
        LocationExportDocument? document;
        try
        {
            RequireCoordinates(json);
            document = JsonSerializer.Deserialize<LocationExportDocument>(json, _json);
        }
        catch (JsonException ex) { throw new InvalidDataException("The file is not a readable Noctaxis location export.", ex); }
        if (document is null) throw new InvalidDataException("Location export is empty or invalid.");
        if (document.SchemaVersion < 1)
            throw new InvalidDataException("Location export schema is invalid.");
        if (document.SchemaVersion > CurrentSchemaVersion)
            throw new InvalidDataException("These locations were exported by a newer version of Noctaxis. Update Noctaxis to import them.");
        if (!string.Equals(document.Product, "Noctaxis", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The file is not a Noctaxis location export.");
        if (document.Locations is null)
            throw new InvalidDataException("The location export has no locations.");
        if (document.Locations.Any(location => location is null || location.Id == Guid.Empty ||
                                               string.IsNullOrWhiteSpace(location.Name) ||
                                               !double.IsFinite(location.Coordinate.Latitude) || Math.Abs(location.Coordinate.Latitude) > 90 ||
                                               !double.IsFinite(location.Coordinate.Longitude) || Math.Abs(location.Coordinate.Longitude) > 180 ||
                                               location.PreferredSensor is { } sensor && !Enum.IsDefined(sensor)))
            throw new InvalidDataException("One or more imported locations are invalid.");
        return document;
    }

    /// <summary>
    /// Each location's coordinate has its latitude and longitude. A coordinate is a struct, which the serializer fills
    /// in member by member, so a missing one would otherwise read as 0° rather than be refused.
    /// </summary>
    private static void RequireCoordinates(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { AllowDuplicateProperties = false });
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("locations", out var locations) || locations.ValueKind != JsonValueKind.Array)
            return; // Reported below, with the rest of the document's shape.
        foreach (var location in locations.EnumerateArray())
            if (location.ValueKind != JsonValueKind.Object || !location.TryGetProperty("coordinate", out var coordinate) ||
                coordinate.ValueKind != JsonValueKind.Object ||
                !coordinate.TryGetProperty("latitude", out var latitude) || latitude.ValueKind != JsonValueKind.Number ||
                !coordinate.TryGetProperty("longitude", out var longitude) || longitude.ValueKind != JsonValueKind.Number)
                throw new InvalidDataException("One or more imported locations are invalid.");
    }

    public async Task ExportToFileAsync(string path, IReadOnlyList<SavedLocation> locations,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("Export path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, Export(locations), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }
}
