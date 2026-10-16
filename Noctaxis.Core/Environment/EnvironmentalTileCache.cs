using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Persistence;

namespace Noctaxis.Core.Environment;

public sealed record EnvironmentalTileDescriptor(
    string SourceId,
    string SourceVersion,
    string Layer,
    string TileId,
    string Extension);

public sealed record EnvironmentalCacheResult(
    EnvironmentalDataState State,
    string? Path,
    bool CacheHit,
    string Message,
    int? HttpStatusCode = null)
{
    public bool IsAvailable => Path is not null && State is EnvironmentalDataState.Available
        or EnvironmentalDataState.Cached;
}

public sealed record EnvironmentalAcquisitionResult(
    EnvironmentalDataState State,
    byte[]? Bytes,
    string Message,
    int? HttpStatusCode = null)
{
    public static EnvironmentalAcquisitionResult Available(byte[] bytes, string message = "Source acquired.") =>
        new(EnvironmentalDataState.Available, bytes, message);
}

public interface IEnvironmentalTileCache
{
    long TerrainGeneration => 0;
    void RegisterTerrainInvalidation(Action callback) { }
    Task<IDisposable?> LeaseTerrainAsync(EnvironmentalTileDescriptor descriptor, long generation,
        CancellationToken token) => Task.FromResult<IDisposable?>(null);
    void TouchTerrain(EnvironmentalTileDescriptor descriptor) { }
    string RootDirectory { get; }
    Task<EnvironmentalCacheResult> GetOrCreateAsync(
        EnvironmentalTileDescriptor descriptor,
        Func<CancellationToken, Task<byte[]?>> acquire,
        Func<string, bool> validate,
        CancellationToken cancellationToken);
    Task<EnvironmentalCacheResult> GetOrCreateDetailedAsync(
        EnvironmentalTileDescriptor descriptor,
        Func<CancellationToken, Task<EnvironmentalAcquisitionResult>> acquire,
        Func<string, bool> validate,
        CancellationToken cancellationToken);

    /// <summary>
    /// As <see cref="GetOrCreateDetailedAsync"/>, but the source writes its payload straight into the
    /// staging file instead of returning it as an array. Large sources (a WorldCover tile is tens of
    /// megabytes) are therefore never held in memory. Validation and the atomic install are identical.
    /// The default implementation buffers, so an implementation that does not stream still works.
    /// </summary>
    Task<EnvironmentalCacheResult> GetOrCreateStreamedAsync(
        EnvironmentalTileDescriptor descriptor,
        Func<Stream, CancellationToken, Task<EnvironmentalAcquisitionResult>> acquire,
        Func<string, bool> validate,
        CancellationToken cancellationToken) =>
        GetOrCreateDetailedAsync(descriptor, async token =>
        {
            using var buffer = new MemoryStream();
            var acquisition = await acquire(buffer, token).ConfigureAwait(false);
            return acquisition.State == EnvironmentalDataState.Available && buffer.Length > 0
                ? EnvironmentalAcquisitionResult.Available(buffer.ToArray(), acquisition.Message)
                : acquisition;
        }, validate, cancellationToken);
}

public sealed class EnvironmentalTileCache : IEnvironmentalTileCache
{
    private readonly ILogger<EnvironmentalTileCache> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly TerrainDiskCache? _terrain;

    public EnvironmentalTileCache(IUserDataPathProvider paths, ILogger<EnvironmentalTileCache> logger,
        TerrainDiskCache? terrain = null)
    {
        _logger = logger;
        _terrain = terrain;
        // Regenerable tiles live in the cache folder (NX-38); the user-installed atlas stays in the data folder.
        RootDirectory = Path.Combine(paths.GetCacheDirectory(), "EnvironmentalData");
    }

    public string RootDirectory { get; }
    public long TerrainGeneration => _terrain?.Generation ?? 0;
    public void RegisterTerrainInvalidation(Action callback)
    { if (_terrain is not null) _terrain.Invalidated += callback; }
    public async Task<IDisposable?> LeaseTerrainAsync(EnvironmentalTileDescriptor descriptor,
        long generation, CancellationToken token) => _terrain is not null && TerrainDiskCache.Governs(descriptor)
        ? await _terrain.LeaseAsync(descriptor, token, generation).ConfigureAwait(false) : null;
    public void TouchTerrain(EnvironmentalTileDescriptor descriptor)
    { if (TerrainDiskCache.Governs(descriptor)) _terrain?.Touch(descriptor); }

    public async Task<EnvironmentalCacheResult> GetOrCreateAsync(
        EnvironmentalTileDescriptor descriptor,
        Func<CancellationToken, Task<byte[]?>> acquire,
        Func<string, bool> validate,
        CancellationToken cancellationToken) => await GetOrCreateDetailedAsync(descriptor, async token =>
        {
            var bytes = await acquire(token).ConfigureAwait(false);
            return bytes is null || bytes.Length == 0
                ? new EnvironmentalAcquisitionResult(EnvironmentalDataState.Unavailable, null,
                    "Environmental source tile is unavailable.")
                : EnvironmentalAcquisitionResult.Available(bytes);
        }, validate, cancellationToken).ConfigureAwait(false);

    public Task<EnvironmentalCacheResult> GetOrCreateDetailedAsync(
        EnvironmentalTileDescriptor descriptor,
        Func<CancellationToken, Task<EnvironmentalAcquisitionResult>> acquire,
        Func<string, bool> validate,
        CancellationToken cancellationToken) =>
        GetOrCreateCoreAsync(descriptor, async (destination, token) =>
        {
            var acquisition = await EnvironmentalPerformanceDiagnostics.MeasureAsync("network-acquisition",
                () => acquire(token)).ConfigureAwait(false);
            if (acquisition.Bytes is null || acquisition.Bytes.Length == 0) return acquisition;
            var writeTimer = System.Diagnostics.Stopwatch.StartNew();
            await destination.WriteAsync(acquisition.Bytes, token).ConfigureAwait(false);
            writeTimer.Stop();
            EnvironmentalPerformanceDiagnostics.Add("disk-write", writeTimer.Elapsed.TotalMilliseconds);
            return acquisition;
        }, validate, cancellationToken);

    public Task<EnvironmentalCacheResult> GetOrCreateStreamedAsync(
        EnvironmentalTileDescriptor descriptor,
        Func<Stream, CancellationToken, Task<EnvironmentalAcquisitionResult>> acquire,
        Func<string, bool> validate,
        CancellationToken cancellationToken) =>
        GetOrCreateCoreAsync(descriptor, (destination, token) =>
            EnvironmentalPerformanceDiagnostics.MeasureAsync("network-acquisition",
                () => acquire(destination, token)), validate, cancellationToken);

    /// <summary>
    /// Shared install path: serve a valid cached file, otherwise let the source fill a staging file,
    /// validate that file and move it into place. The staging file is always removed.
    /// </summary>
    private async Task<EnvironmentalCacheResult> GetOrCreateCoreAsync(
        EnvironmentalTileDescriptor descriptor,
        Func<Stream, CancellationToken, Task<EnvironmentalAcquisitionResult>> acquire,
        Func<string, bool> validate,
        CancellationToken cancellationToken)
    {
        var path = GetPath(descriptor);
        using var lease = await LeaseTerrainAsync(descriptor, TerrainGeneration, cancellationToken).ConfigureAwait(false);
        var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lookupTimer = System.Diagnostics.Stopwatch.StartNew();
            var cacheHit = File.Exists(path) && SafeValidate(path, validate);
            lookupTimer.Stop();
            EnvironmentalPerformanceDiagnostics.Add("cache-lookup", lookupTimer.Elapsed.TotalMilliseconds);
            if (cacheHit)
            {
                _logger.LogDebug("Environmental cache hit for {Source}/{Layer}/{Tile}",
                    descriptor.SourceId, descriptor.Layer, descriptor.TileId);
                return new EnvironmentalCacheResult(EnvironmentalDataState.Cached, path, true,
                    "Environmental source tile loaded from cache.");
            }

            _logger.LogInformation("Environmental cache miss for {Source}/{Layer}/{Tile}",
                descriptor.SourceId, descriptor.Layer, descriptor.TileId);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                EnvironmentalAcquisitionResult acquisition;
                long acquiredBytes;
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                                 FileShare.None, 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    try
                    {
                        acquisition = await acquire(stream, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    // Cancellation the caller did not ask for is the source giving up, typically
                    // HttpClient.Timeout. It is a transient source failure for this tile, not a reason to
                    // abandon everything that requested it.
                    catch (OperationCanceledException ex)
                    {
                        _logger.LogWarning(ex, "Environmental tile acquisition timed out for {Source}/{Layer}/{Tile}",
                            descriptor.SourceId, descriptor.Layer, descriptor.TileId);
                        return new EnvironmentalCacheResult(EnvironmentalDataState.SourceUnavailable, null, false,
                            "Environmental source did not respond in time.");
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
                    {
                        _logger.LogWarning(ex, "Environmental tile acquisition failed for {Source}/{Layer}/{Tile}",
                            descriptor.SourceId, descriptor.Layer, descriptor.TileId);
                        return new EnvironmentalCacheResult(EnvironmentalDataState.Unavailable, null, false,
                            "Environmental source tile is unavailable.");
                    }
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    acquiredBytes = stream.Length;
                }
                // No payload: the tile is absent, or the source reported a failure instead of data.
                if (acquisition.State != EnvironmentalDataState.Available || acquiredBytes == 0)
                    return new EnvironmentalCacheResult(acquisition.State, null, false,
                        acquisition.Message, acquisition.HttpStatusCode);
                if (!SafeValidate(temporary, validate))
                {
                    _logger.LogWarning("Rejected invalid environmental download for {Source}/{Layer}/{Tile}",
                        descriptor.SourceId, descriptor.Layer, descriptor.TileId);
                    return new EnvironmentalCacheResult(EnvironmentalDataState.InvalidData, null, false,
                        "Downloaded environmental tile failed validation.");
                }
                File.Move(temporary, path, true);
                if (_terrain is not null && TerrainDiskCache.Governs(descriptor))
                    await _terrain.InsertedAsync(descriptor).ConfigureAwait(false);
                _logger.LogInformation("Environmental tile committed for {Source}/{Layer}/{Tile} at {Path}",
                    descriptor.SourceId, descriptor.Layer, descriptor.TileId, path);
                return new EnvironmentalCacheResult(EnvironmentalDataState.Available, path, false,
                    "Environmental source tile downloaded and cached.");
            }
            finally
            {
                TryDelete(temporary);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private string GetPath(EnvironmentalTileDescriptor descriptor)
    {
        static string Safe(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
        }
        var extension = descriptor.Extension.TrimStart('.');
        return Path.Combine(RootDirectory, Safe(descriptor.SourceId), Safe(descriptor.SourceVersion),
            Safe(descriptor.Layer), Safe(descriptor.TileId) + "." + Safe(extension));
    }

    private bool SafeValidate(string path, Func<string, bool> validate)
    {
        try { return validate(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Environmental cache validation failed for {Path}", path);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public static class EnvironmentalHttpDownloader
{
    public static async Task<byte[]?> DownloadAsync(HttpClient client, Uri uri, int maximumBytes,
        CancellationToken cancellationToken, int maximumAttempts = 3) =>
        (await DownloadDetailedAsync(client, uri, maximumBytes, cancellationToken, maximumAttempts)
            .ConfigureAwait(false)).Bytes;

    public static async Task<EnvironmentalAcquisitionResult> DownloadDetailedAsync(HttpClient client, Uri uri,
        int maximumBytes, CancellationToken cancellationToken, int maximumAttempts = 3)
    {
        using var buffer = new MemoryStream();
        var result = await DownloadToStreamAsync(client, uri, maximumBytes, buffer, cancellationToken, maximumAttempts)
            .ConfigureAwait(false);
        return result.State == EnvironmentalDataState.Available
            ? EnvironmentalAcquisitionResult.Available(buffer.ToArray())
            : result;
    }

    /// <summary>
    /// Downloads straight into <paramref name="destination"/>, so a large source is never held in
    /// memory. Retries, absent-tile reporting, the size limit and cancellation behave exactly as they
    /// do for the buffered overload; the returned result never carries bytes.
    /// </summary>
    public static async Task<EnvironmentalAcquisitionResult> DownloadToStreamAsync(HttpClient client, Uri uri,
        int maximumBytes, Stream destination, CancellationToken cancellationToken, int maximumAttempts = 3)
    {
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            // Discard anything a previous attempt may have written before starting again.
            if (destination.CanSeek && destination.Length > 0)
            {
                destination.SetLength(0);
                destination.Seek(0, SeekOrigin.Begin);
            }
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new EnvironmentalAcquisitionResult(EnvironmentalDataState.TileAbsent, null,
                    "The source explicitly reports that this geographic tile is absent.", 404);
            if (!response.IsSuccessStatusCode)
            {
                if (attempt < maximumAttempts && ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }
                response.EnsureSuccessStatusCode();
            }
            if (response.Content.Headers.ContentLength > maximumBytes)
                throw new InvalidDataException("Environmental tile exceeds its configured size limit.");
            // HttpClient.Timeout ends at the headers when the body is streamed: a source that then stops
            // sending would hold the shared tile load, its lock and its disk lease indefinitely. Each read
            // must make progress within the client's own timeout (a slow but steady body is never cut off),
            // and a stall ends as that timeout would have: a TaskCanceledException the caller did not ask for.
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var window = BodyStallWindow(client);
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                int read;
                stall.CancelAfter(window);
                try { read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    throw new TaskCanceledException($"The source sent no data for {window.TotalSeconds:0.#} s.",
                        new TimeoutException(ex.Message, ex));
                }
                // Only the source's silence counts, never the time spent writing what it sent.
                stall.CancelAfter(Timeout.InfiniteTimeSpan);
                if (read == 0) break;
                if (total + read > maximumBytes)
                    throw new InvalidDataException("Environmental tile exceeds its configured size limit.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total += read;
            }
            return new EnvironmentalAcquisitionResult(EnvironmentalDataState.Available, null, "Source acquired.");
        }
        return new EnvironmentalAcquisitionResult(EnvironmentalDataState.SourceUnavailable, null,
            "Environmental source acquisition attempts were exhausted.");
    }

    /// <summary>How long one body read may wait for data: the client's own timeout, or 30 s when it has none.</summary>
    internal static TimeSpan BodyStallWindow(HttpClient client) =>
        client.Timeout > TimeSpan.Zero && client.Timeout != Timeout.InfiniteTimeSpan ? client.Timeout : TimeSpan.FromSeconds(30);
}
