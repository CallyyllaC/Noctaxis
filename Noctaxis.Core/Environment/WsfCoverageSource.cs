using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BitMiracle.LibTiff.Classic;
using Microsoft.Extensions.Logging;

namespace Noctaxis.Core.Environment;

public enum WsfCoverageLayer { BuildingFraction, BuildingHeight }

public sealed record WsfCoverageChunk(string Id, GeoBounds Bounds);

public sealed record WsfRasterValidationResult(
    bool IsValid,
    string Message,
    GeoTiffRasterMetadata? Metadata = null,
    RasterValueStatistics? Statistics = null);

public sealed record WsfCoverageResult(
    EnvironmentalDataState State,
    WsfCoverageLayer Layer,
    WsfCoverageChunk Chunk,
    GeoTiffRaster? Raster,
    string Message,
    bool CacheHit = false,
    int? HttpStatusCode = null)
{
    public bool HasRaster => Raster is not null && State is EnvironmentalDataState.Available
        or EnvironmentalDataState.Cached;
}

public interface IWsfCoverageSource
{
    Task<WsfCoverageResult> GetCoverageAsync(WsfCoverageChunk chunk, WsfCoverageLayer layer,
        CancellationToken cancellationToken);
}

/// <summary>
/// Retrieves raw WSF scientific coverages through DLR's catalogued WCS. WMS portrayals and the
/// incomplete direct-download directory are deliberately not used.
/// </summary>
public sealed class DlrWsfCoverageSource(
    HttpClient http,
    IEnvironmentalTileCache cache,
    ILogger<DlrWsfCoverageSource> logger) : IWsfCoverageSource
{
    public const string ServiceEndpoint = "https://geoservice.dlr.de/eoc/land/wcs";
    public const string FractionCoverageId = "land__WSF3D_V02_BUILDINGFRACTION";
    public const string HeightCoverageId = "land__WSF3D_V02_BUILDINGHEIGHT";
    public const string CacheEncodingVersion = "v02-wcs-scientific-v1";
    private const int MaximumCoverageBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions CacheMetadataJson = new(JsonSerializerDefaults.Web)
        { WriteIndented = true };
    private readonly BoundedDecodedRasterCache<string, GeoTiffRaster> _decoded = new(8);
    private readonly ConcurrentDictionary<string, Lazy<Task<WsfCoverageResult>>> _inflight =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, WsfCoverageResult Result)> _recentFailures =
        new(StringComparer.Ordinal);

    public async Task<WsfCoverageResult> GetCoverageAsync(WsfCoverageChunk chunk, WsfCoverageLayer layer,
        CancellationToken cancellationToken)
    {
        var key = $"{chunk.Id}:{layer}";
        if (_recentFailures.TryGetValue(key, out var recent))
        {
            if (DateTimeOffset.UtcNow - recent.At < TimeSpan.FromSeconds(30)) return recent.Result;
            _recentFailures.TryRemove(key, out _);
        }
        WsfCoverageResult? acquired = null;
        var raster = await _decoded.GetOrCreateAsync(key, async _ =>
        {
            acquired = await AcquireSingleFlightAsync(key, chunk, layer, cancellationToken)
                .ConfigureAwait(false);
            if (!acquired.HasRaster) _recentFailures[key] = (DateTimeOffset.UtcNow, acquired);
            return acquired.HasRaster ? acquired.Raster : null;
        }, cancellationToken).ConfigureAwait(false);
        if (raster is not null)
        {
            _recentFailures.TryRemove(key, out _);
            return acquired ?? new WsfCoverageResult(EnvironmentalDataState.Cached, layer, chunk, raster,
                "Decoded WSF coverage reused from memory.", true);
        }
        var failure = acquired ?? (_recentFailures.TryGetValue(key, out recent)
            ? recent.Result
            : new WsfCoverageResult(EnvironmentalDataState.SourceUnavailable, layer, chunk, null,
                "The shared WSF coverage request did not produce a raster."));
        _recentFailures[key] = (DateTimeOffset.UtcNow, failure);
        return failure;
    }

    public static Uri BuildCoverageUri(WsfCoverageChunk chunk, WsfCoverageLayer layer)
    {
        var coverage = layer == WsfCoverageLayer.BuildingFraction ? FractionCoverageId : HeightCoverageId;
        var bounds = chunk.Bounds;
        var query = string.Create(CultureInfo.InvariantCulture,
            $"SERVICE=WCS&VERSION=2.0.1&REQUEST=GetCoverage&COVERAGEID={coverage}" +
            $"&SUBSET=Lat({bounds.South:R},{bounds.North:R})" +
            $"&SUBSET=Long({bounds.West:R},{bounds.East:R})&FORMAT=image/tiff");
        return new Uri(ServiceEndpoint + "?" + query);
    }

    public static WsfRasterValidationResult ValidateScientificRaster(GeoTiffRaster raster,
        WsfCoverageChunk chunk, WsfCoverageLayer layer)
    {
        var metadata = raster.Metadata;
        if (metadata.Width < 2 || metadata.Height < 2 || metadata.Width > 4096 || metadata.Height > 4096)
            return Invalid("Raster dimensions are outside the supported scientific coverage range.");
        if (metadata.SamplesPerPixel != 1)
            return Invalid("WSF coverage must contain exactly one scientific sample band.");
        if (metadata.Photometric == Photometric.PALETTE)
            return Invalid("The response is a paletted portrayal raster, not scientific WSF samples.");
        if (metadata.Photometric is not (Photometric.MINISBLACK or Photometric.MINISWHITE))
            return Invalid("WSF coverage has an unsupported photometric interpretation.");
        if (metadata.EpsgCode != 4326)
            return Invalid("WSF coverage is missing the required EPSG:4326 geographic CRS.");
        if (metadata.Bounds is not { } rasterBounds || !BoundsMatch(rasterBounds, chunk.Bounds,
                metadata.Width, metadata.Height))
            return Invalid("WSF coverage georeferencing does not match the requested source chunk.");

        var expectedNoData = layer == WsfCoverageLayer.BuildingFraction ? 255d : -32767d;
        if (!metadata.NoDataValue.HasValue || Math.Abs(metadata.NoDataValue.Value - expectedNoData) > 1e-6)
            return Invalid($"WSF {layer} coverage has missing or unexpected nodata metadata.");

        if (layer == WsfCoverageLayer.BuildingFraction &&
            (metadata.BitsPerSample != 8 || metadata.SampleFormat != SampleFormat.UINT))
            return Invalid("Building Fraction must be an unsigned 8-bit scientific coverage.");
        if (layer == WsfCoverageLayer.BuildingHeight &&
            !((metadata.BitsPerSample == 16 && metadata.SampleFormat == SampleFormat.INT) ||
              (metadata.BitsPerSample == 64 && metadata.SampleFormat == SampleFormat.IEEEFP)))
            return Invalid("Building Height must contain raw signed Int16 or WCS Float64 samples.");

        var statistics = raster.GetStatistics(value => Math.Abs(value - expectedNoData) <= 1e-6);
        if (statistics.ValidCount == 0 && layer == WsfCoverageLayer.BuildingFraction)
            return Invalid("WSF coverage contains no valid scientific samples.", statistics);
        if (layer == WsfCoverageLayer.BuildingFraction &&
            (statistics.Minimum < 0 || statistics.Maximum > 100))
            return Invalid("Building Fraction contains values outside the documented 0..100 percent range.",
                statistics);
        if (layer == WsfCoverageLayer.BuildingHeight &&
            (statistics.Minimum < 0 || statistics.Maximum > 5000))
            return Invalid("Building Height contains impossible raw values outside 0..5000.", statistics);

        return new WsfRasterValidationResult(true,
            "Raw single-band WSF scientific coverage validated.", metadata, statistics);

        WsfRasterValidationResult Invalid(string message, RasterValueStatistics? statistics = null) =>
            new(false, message, metadata, statistics);
    }

    private async Task<WsfCoverageResult> AcquireSingleFlightAsync(string key, WsfCoverageChunk chunk,
        WsfCoverageLayer layer, CancellationToken cancellationToken)
    {
        var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task<WsfCoverageResult>>(
            () => LoadCoverageAsync(chunk, layer, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try { return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompleted)
                _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<WsfCoverageResult>>>(key, lazy));
        }
    }

    private async Task<WsfCoverageResult> LoadCoverageAsync(WsfCoverageChunk chunk, WsfCoverageLayer layer,
        CancellationToken cancellationToken)
    {
        var layerName = layer == WsfCoverageLayer.BuildingFraction ? "fraction" : "height";
        GeoTiffRaster? validatedRaster = null;
        WsfRasterValidationResult? validation = null;
        var descriptor = new EnvironmentalTileDescriptor(WsfSettlementDataProvider.SourceId,
            CacheEncodingVersion, layerName, chunk.Id, "tif");
        var result = await cache.GetOrCreateDetailedAsync(descriptor,
            token => DownloadCoverageAsync(BuildCoverageUri(chunk, layer), chunk, layer, token), path =>
            {
                try
                {
                    var raster = GeoTiffRaster.Load(path);
                    validation = ValidateScientificRaster(raster, chunk, layer);
                    if (!validation.IsValid) return false;
                    validatedRaster = raster;
                    return true;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException)
                {
                    validation = new WsfRasterValidationResult(false, exception.Message);
                    return false;
                }
            }, cancellationToken).ConfigureAwait(false);

        if (!result.IsAvailable)
        {
            var state = result.State == EnvironmentalDataState.InvalidData
                ? EnvironmentalDataState.InvalidRaster : result.State;
            logger.LogWarning(
                "WSF chunk unavailable: {Chunk} {Layer}, state={State}, HTTP={HttpStatus}, validation={Validation}",
                chunk.Id, layer, state, result.HttpStatusCode, validation?.Message ?? result.Message);
            return new WsfCoverageResult(state, layer, chunk, null,
                validation?.Message ?? result.Message, result.CacheHit, result.HttpStatusCode);
        }

        validatedRaster ??= GeoTiffRaster.Load(result.Path!);
        validation ??= ValidateScientificRaster(validatedRaster, chunk, layer);
        if (!validation.IsValid)
            return new WsfCoverageResult(EnvironmentalDataState.InvalidRaster, layer, chunk, null,
                validation.Message, result.CacheHit, result.HttpStatusCode);
        await EnsureCacheMetadataAsync(result.Path!, chunk, layer, validatedRaster.Metadata,
            cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "WSF chunk ready: {Chunk} {Layer}, cache={CacheHit}, {Width}x{Height}, {Bits}-bit {SampleFormat}, EPSG:{Epsg}, nodata={NoData}, values={Minimum}..{Maximum}",
            chunk.Id, layer, result.CacheHit, validatedRaster.Width, validatedRaster.Height,
            validatedRaster.Metadata.BitsPerSample, validatedRaster.Metadata.SampleFormat,
            validatedRaster.Metadata.EpsgCode, validatedRaster.Metadata.NoDataValue,
            validation.Statistics?.Minimum, validation.Statistics?.Maximum);
        return new WsfCoverageResult(result.CacheHit ? EnvironmentalDataState.Cached : EnvironmentalDataState.Available,
            layer, chunk, validatedRaster, validation.Message, result.CacheHit, result.HttpStatusCode);
    }

    private async Task<EnvironmentalAcquisitionResult> DownloadCoverageAsync(Uri uri, WsfCoverageChunk chunk,
        WsfCoverageLayer layer, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return new EnvironmentalAcquisitionResult(EnvironmentalDataState.TileAbsent, null,
                        "DLR WCS explicitly reported that the requested WSF chunk is absent.", status);
                if (!response.IsSuccessStatusCode)
                {
                    if (attempt < 3 && (status == 429 || status >= 500))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }
                    return new EnvironmentalAcquisitionResult(EnvironmentalDataState.SourceUnavailable, null,
                        $"DLR WCS returned HTTP {status}.", status);
                }
                if (!IsTiff(response.Content.Headers.ContentType))
                    return new EnvironmentalAcquisitionResult(EnvironmentalDataState.InvalidRaster, null,
                        "DLR WCS returned HTTP 200 with a non-TIFF response.", status);
                var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("DLR WCS acquired {Chunk} {Layer}: HTTP {Status}, {Bytes} bytes",
                    chunk.Id, layer, status, bytes.Length);
                return new EnvironmentalAcquisitionResult(EnvironmentalDataState.Available, bytes,
                    "Raw DLR WCS coverage acquired.", status);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (InvalidDataException exception)
            {
                return new EnvironmentalAcquisitionResult(EnvironmentalDataState.InvalidRaster, null,
                    $"DLR WCS response was not a usable bounded raster: {exception.Message}");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
            {
                if (attempt < 3) continue;
                return new EnvironmentalAcquisitionResult(EnvironmentalDataState.SourceUnavailable, null,
                    $"DLR WCS could not be reached: {exception.GetBaseException().Message}");
            }
        }
        return new EnvironmentalAcquisitionResult(EnvironmentalDataState.SourceUnavailable, null,
            "DLR WCS acquisition exhausted its retry budget.");
    }

    private static bool IsTiff(MediaTypeHeaderValue? contentType) =>
        contentType?.MediaType is "image/tiff" or "image/geotiff" or "application/geotiff";

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumCoverageBytes)
            throw new InvalidDataException("WSF coverage exceeds its configured size limit.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumCoverageBytes)
                throw new InvalidDataException("WSF coverage exceeds its configured size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static bool BoundsMatch(GeoBounds actual, GeoBounds expected, int width, int height)
    {
        var xTolerance = Math.Abs(actual.East - actual.West) / Math.Max(1, width) * 2 + 1e-5;
        var yTolerance = Math.Abs(actual.North - actual.South) / Math.Max(1, height) * 2 + 1e-5;
        return Math.Abs(actual.West - expected.West) <= xTolerance &&
               Math.Abs(actual.East - expected.East) <= xTolerance &&
               Math.Abs(actual.South - expected.South) <= yTolerance &&
               Math.Abs(actual.North - expected.North) <= yTolerance;
    }

    private async Task EnsureCacheMetadataAsync(string rasterPath, WsfCoverageChunk chunk,
        WsfCoverageLayer layer, GeoTiffRasterMetadata raster, CancellationToken cancellationToken)
    {
        var path = rasterPath + ".metadata.json";
        try
        {
            if (File.Exists(path))
            {
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    8 * 1024, FileOptions.Asynchronous);
                var existing = await JsonSerializer.DeserializeAsync<WsfCoverageCacheMetadata>(input,
                    CacheMetadataJson, cancellationToken).ConfigureAwait(false);
                if (existing is not null && existing.CacheEncodingVersion == CacheEncodingVersion &&
                    existing.ChunkId == chunk.Id && existing.Layer == layer.ToString() &&
                    existing.Width == raster.Width && existing.Height == raster.Height &&
                    existing.Bounds == chunk.Bounds) return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "WSF cache metadata was unreadable and will be replaced for {Chunk} {Layer}",
                chunk.Id, layer);
        }

        var metadata = new WsfCoverageCacheMetadata(WsfSettlementDataProvider.SourceId,
            WsfSettlementDataProvider.SourceVersion, CacheEncodingVersion, "DLR WCS 2.0.1", chunk.Id,
            layer.ToString(), chunk.Bounds, raster.EpsgCode, raster.Width, raster.Height,
            raster.BitsPerSample, raster.SampleFormat.ToString(), raster.Photometric.ToString(),
            raster.NoDataValue, layer == WsfCoverageLayer.BuildingFraction ? .01 : .1,
            layer == WsfCoverageLayer.BuildingFraction ? "fraction01" : "metres",
            File.GetLastWriteTimeUtc(rasterPath));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata, CacheMetadataJson);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "WSF cache metadata could not be persisted for {Chunk} {Layer}",
                chunk.Id, layer);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record WsfCoverageCacheMetadata(
        string SourceId,
        string DatasetVersion,
        string CacheEncodingVersion,
        string AcquisitionSource,
        string ChunkId,
        string Layer,
        GeoBounds Bounds,
        int? EpsgCode,
        int Width,
        int Height,
        int BitsPerSample,
        string SampleFormat,
        string Photometric,
        double? NoData,
        double Scale,
        string NormalizedUnit,
        DateTimeOffset RetrievedAtUtc);
}
