using System.Security.Cryptography;
using System.Text.Json;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Desktop.Services;

namespace Noctaxis.Desktop.Mapping.Preview;

/// <summary>
/// Reads a saved location's cached map assets (landscape or earlier source raster, road and water features,
/// settlement field)
/// and builds its preview scene. Files that are missing or fail their recorded content hash are
/// skipped, so the preview degrades to fewer layers rather than failing. Built scenes are cached in
/// <see cref="LocationPreviewSceneCodec.FileName"/> beside those assets.
/// </summary>
public static class LocationPreviewSceneLoader
{
    private static readonly JsonSerializerOptions FeatureJson = new(JsonSerializerDefaults.Web);

    public static async Task<LocationPreviewScene> LoadAsync(string directory, SavedLocationThumbnailMetadata metadata,
        GeoCoordinate location, CancellationToken cancellationToken)
    {
        if (metadata.SourceImageWidth <= 0 || metadata.SourceImageHeight <= 0)
            return LocationPreviewScene.Empty(metadata.LocationId);
        var viewport = new WebMercatorViewport(metadata.SourceZoom, LocationMapThumbnailService.TileSize,
            metadata.SourceImageWidth, metadata.SourceImageHeight, metadata.SourceCentreLatitude, metadata.SourceCentreLongitude);
        // A scene depends only on these inputs, so a cached build is reused until one of them changes.
        var key = InputKey(metadata, location);
        var cachePath = Path.Combine(directory, LocationPreviewSceneCodec.FileName);
        if (await ReadAsync(directory, LocationPreviewSceneCodec.FileName, null, cancellationToken).ConfigureAwait(false) is { } cached &&
            LocationPreviewSceneCodec.TryDecode(cached, key) is { } scene)
            return scene;
        var source = await ReadAsync(directory, metadata.SourceFileName, metadata.SourceContentHash, cancellationToken).ConfigureAwait(false);
        var featureBytes = await ReadAsync(directory, metadata.FeatureFileName, metadata.FeatureContentHash, cancellationToken).ConfigureAwait(false);
        var settlementBytes = await ReadAsync(directory, metadata.SettlementFileName, metadata.SettlementContentHash, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            MapFeatureDataDocument? features = null;
            SettlementRaster? settlement = null;
            // A vector landscape, or for map data saved before it an OpenStreetMap raster capture.
            var landscape = metadata.HasVectorLandscape && source is not null ? SavedLocationLandscapeCodec.TryDecode(source) : null;
            var legacyRaster = metadata.HasVectorLandscape ? null : source;
            try
            {
                features = featureBytes is null ? null : JsonSerializer.Deserialize<MapFeatureDataDocument>(featureBytes, FeatureJson);
                if (features?.LocationId != metadata.LocationId) features = null;
            }
            catch (JsonException) { features = null; }
            try
            {
                settlement = settlementBytes is null ? null : SettlementRasterCodec.Decode(settlementBytes);
                // The field must cover exactly this viewport, as the thumbnail service stores it.
                if (settlement is not null && (settlement.Grid.Width != viewport.Width * SettlementDensityBuilder.Supersampling ||
                        settlement.Grid.Height != viewport.Height * SettlementDensityBuilder.Supersampling))
                    settlement = null;
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException) { settlement = null; }
            var built = LocationPreviewSceneBuilder.Build(metadata.LocationId, location, viewport, legacyRaster, landscape,
                features, settlement);
            TryWrite(cachePath, LocationPreviewSceneCodec.Encode(built, key));
            return built;
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static string InputKey(SavedLocationThumbnailMetadata metadata, GeoCoordinate location) => string.Join("|",
        LocationPreviewSceneCodec.Version, metadata.LocationId.ToString("N"), metadata.SourceZoom, metadata.SourceImageWidth, metadata.SourceImageHeight,
        BitConverter.DoubleToInt64Bits(metadata.SourceCentreLatitude), BitConverter.DoubleToInt64Bits(metadata.SourceCentreLongitude),
        BitConverter.DoubleToInt64Bits(location.Latitude), BitConverter.DoubleToInt64Bits(location.Longitude),
        metadata.SourceContentHash, metadata.FeatureContentHash, metadata.SettlementContentHash);

    private static void TryWrite(string path, byte[] bytes)
    {
        // Written beside the assets it came from; a failed write only means the next load rebuilds.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<byte[]?> ReadAsync(string directory, string? fileName, string? expectedHash, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var path = Path.Combine(directory, Path.GetFileName(fileName));
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(expectedHash) &&
                !Convert.ToHexString(SHA256.HashData(bytes)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                return null;
            return bytes;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
