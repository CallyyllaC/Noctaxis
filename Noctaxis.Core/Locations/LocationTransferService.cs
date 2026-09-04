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
    public const int CurrentSchemaVersion = 1;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public byte[] Export(IReadOnlyList<SavedLocation> locations) => JsonSerializer.SerializeToUtf8Bytes(
        new LocationExportDocument(CurrentSchemaVersion, DateTimeOffset.UtcNow, locations), _json);

    public LocationExportDocument Import(ReadOnlySpan<byte> json)
    {
        var document = JsonSerializer.Deserialize<LocationExportDocument>(json, _json)
                       ?? throw new InvalidDataException("Location export is empty or invalid.");
        if (document.SchemaVersion < 1)
            throw new InvalidDataException("Location export schema is invalid.");
        if (!string.Equals(document.Product, "Noctaxis", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The file is not a Noctaxis location export.");
        if (document.Locations.Any(location => location.Id == Guid.Empty ||
                                               string.IsNullOrWhiteSpace(location.Name)))
            throw new InvalidDataException("One or more imported locations are invalid.");
        return document;
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
