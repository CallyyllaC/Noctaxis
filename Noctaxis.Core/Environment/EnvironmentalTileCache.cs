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
}

public sealed class EnvironmentalTileCache : IEnvironmentalTileCache
{
    private readonly ILogger<EnvironmentalTileCache> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public EnvironmentalTileCache(IUserDataPathProvider paths, ILogger<EnvironmentalTileCache> logger)
    {
        _logger = logger;
        RootDirectory = Path.Combine(paths.GetApplicationDataDirectory(), "EnvironmentalData");
    }

    public string RootDirectory { get; }

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

    public async Task<EnvironmentalCacheResult> GetOrCreateDetailedAsync(
        EnvironmentalTileDescriptor descriptor,
        Func<CancellationToken, Task<EnvironmentalAcquisitionResult>> acquire,
        Func<string, bool> validate,
        CancellationToken cancellationToken)
    {
        var path = GetPath(descriptor);
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
            EnvironmentalAcquisitionResult acquisition;
            try
            {
                acquisition = await EnvironmentalPerformanceDiagnostics.MeasureAsync("network-acquisition",
                    () => acquire(cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                _logger.LogWarning(ex, "Environmental tile acquisition failed for {Source}/{Layer}/{Tile}",
                    descriptor.SourceId, descriptor.Layer, descriptor.TileId);
                return new EnvironmentalCacheResult(EnvironmentalDataState.Unavailable, null, false,
                    "Environmental source tile is unavailable.");
            }
            if (acquisition.Bytes is null || acquisition.Bytes.Length == 0)
                return new EnvironmentalCacheResult(acquisition.State, null, false,
                    acquisition.Message, acquisition.HttpStatusCode);
            var bytes = acquisition.Bytes;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                                 FileShare.None, 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    var writeTimer = System.Diagnostics.Stopwatch.StartNew();
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    writeTimer.Stop();
                    EnvironmentalPerformanceDiagnostics.Add("disk-write", writeTimer.Elapsed.TotalMilliseconds);
                }
                if (!SafeValidate(temporary, validate))
                {
                    _logger.LogWarning("Rejected invalid environmental download for {Source}/{Layer}/{Tile}",
                        descriptor.SourceId, descriptor.Layer, descriptor.TileId);
                    return new EnvironmentalCacheResult(EnvironmentalDataState.InvalidData, null, false,
                        "Downloaded environmental tile failed validation.");
                }
                File.Move(temporary, path, true);
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
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
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
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > maximumBytes)
                    throw new InvalidDataException("Environmental tile exceeds its configured size limit.");
                output.Write(buffer, 0, read);
            }
            return EnvironmentalAcquisitionResult.Available(output.ToArray());
        }
        return new EnvironmentalAcquisitionResult(EnvironmentalDataState.SourceUnavailable, null,
            "Environmental source acquisition attempts were exhausted.");
    }
}
