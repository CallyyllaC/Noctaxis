using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text.Json;

namespace Noctaxis.Desktop.Mapping.Vector;

/// <summary>
/// Downloads OpenFreeMap vector tiles (OpenMapTiles schema). The tile URL is versioned, so it is
/// read from the TileJSON document; the last document and every downloaded tile are kept on disk,
/// which lets a previously viewed area draw with no network, at a dark site for example.
/// </summary>
public sealed class OpenFreeMapTileFetcher : IVectorTileFetcher
{
    public static readonly Uri DefaultTileJson = new("https://tiles.openfreemap.org/planet");
    private static readonly TimeSpan TileJsonMaximumAge = TimeSpan.FromDays(1);
    private readonly HttpClient _http;
    private readonly string? _cacheDirectory;
    private readonly Uri _tileJson;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _templateGate = new(1, 1);
    private readonly ConcurrentDictionary<(int, int, int), Lazy<Task<byte[]?>>> _inFlight = new();
    private (string Template, DateTimeOffset Retrieved)? _template;
    private int _prunedVersions;

    public OpenFreeMapTileFetcher(HttpClient http, string? cacheDirectory, Uri? tileJson = null,
        Func<DateTimeOffset>? now = null)
    {
        _http = http;
        _cacheDirectory = cacheDirectory;
        _tileJson = tileJson ?? DefaultTileJson;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The production fetcher: one shared client and a cache in the user data directory.</summary>
    public static OpenFreeMapTileFetcher CreateDefault()
    {
        string? directory = null;
        try
        {
            directory = Path.Combine(new Noctaxis.Core.Persistence.PlatformUserDataPathProvider()
                .GetApplicationDataDirectory(), "BasemapCache", "openfreemap");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return new OpenFreeMapTileFetcher(SharedClient.Value, directory);
    }

    private static readonly Lazy<HttpClient> SharedClient = new(() =>
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        }) { Timeout = TimeSpan.FromSeconds(20) };
        var version = typeof(OpenFreeMapTileFetcher).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Noctaxis/{version}");
        return client;
    });

    public Task<byte[]?> FetchAsync(int zoom, int column, int row, CancellationToken cancellationToken)
    {
        var key = (zoom, column, row);
        // Concurrent requests for one tile share a single download.
        var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<Task<byte[]?>>(() => FetchCoreAsync(zoom, column, row)));
        return lazy.Value.ContinueWith(task =>
        {
            _inFlight.TryRemove(new KeyValuePair<(int, int, int), Lazy<Task<byte[]?>>>(key, lazy));
            return task.Result;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
            .WaitAsync(cancellationToken);
    }

    public void Discard(int zoom, int column, int row)
    {
        if (_template is { } template && TilePath(template.Template, zoom, column, row) is { } path) TryDelete(path);
    }

    private async Task<byte[]?> FetchCoreAsync(int zoom, int column, int row)
    {
        var template = await ResolveTemplateAsync().ConfigureAwait(false);
        if (template is null) return null;
        var path = TilePath(template, zoom, column, row);
        if (path is not null && File.Exists(path))
        {
            try { return await File.ReadAllBytesAsync(path).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        try
        {
            var uri = new Uri(template.Replace("{z}", zoom.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("{x}", column.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("{y}", row.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
            {
                if (path is not null) TryWrite(path, []);
                return [];
            }
            if (!response.IsSuccessStatusCode) return null;
            var bytes = Decompress(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            if (path is not null) TryWrite(path, bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The current tile URL template; a stored copy is used when the network is unavailable.</summary>
    private async Task<string?> ResolveTemplateAsync()
    {
        if (_template is { } known && _now() - known.Retrieved < TileJsonMaximumAge) return known.Template;
        await _templateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_template is { } current && _now() - current.Retrieved < TileJsonMaximumAge) return current.Template;
            var stored = _cacheDirectory is null ? null : Path.Combine(_cacheDirectory, "tilejson.json");
            if (_template is null && stored is not null && File.Exists(stored))
            {
                try
                {
                    var age = _now() - File.GetLastWriteTimeUtc(stored);
                    if (ParseTemplate(await File.ReadAllTextAsync(stored).ConfigureAwait(false)) is { } cached)
                    {
                        _template = (cached, _now() - age);
                        if (age < TileJsonMaximumAge) return cached;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            }
            try
            {
                var json = await _http.GetStringAsync(_tileJson).ConfigureAwait(false);
                if (ParseTemplate(json) is { } fresh)
                {
                    _template = (fresh, _now());
                    if (stored is not null) TryWrite(stored, System.Text.Encoding.UTF8.GetBytes(json));
                    PruneOtherVersions(fresh);
                    return fresh;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
            return _template?.Template;
        }
        finally { _templateGate.Release(); }
    }

    internal static string? ParseTemplate(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("tiles", out var tiles) || tiles.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var tile in tiles.EnumerateArray())
            if (tile.GetString() is { } template && template.Contains("{z}") && template.Contains("{x}") &&
                template.Contains("{y}") && Uri.TryCreate(template.Replace("{z}", "0").Replace("{x}", "0").Replace("{y}", "0"),
                    UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                return template;
        return null;
    }

    /// <summary>The tileset version is the path segment before "{z}", e.g. "20260913_164504_pt".</summary>
    internal static string VersionOf(string template)
    {
        var segments = new Uri(template.Replace("{z}", "0").Replace("{x}", "0").Replace("{y}", "0")).Segments;
        var version = segments.Length >= 4 ? segments[^4].TrimEnd('/') : "current";
        return string.Concat(version.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
    }

    private string? TilePath(string template, int zoom, int column, int row) => _cacheDirectory is null
        ? null
        : Path.Combine(_cacheDirectory, VersionOf(template), zoom.ToString(System.Globalization.CultureInfo.InvariantCulture),
            column.ToString(System.Globalization.CultureInfo.InvariantCulture),
            row.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".pbf");

    /// <summary>Tiles are served gzip-encoded; accept them whether or not the transport decoded them.</summary>
    internal static byte[] Decompress(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes[0] != 0x1F || bytes[1] != 0x8B) return bytes;
        using var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>A newer tileset replaces the stored one; older versions are removed once per session.</summary>
    private void PruneOtherVersions(string template)
    {
        if (_cacheDirectory is null || Interlocked.Exchange(ref _prunedVersions, 1) == 1) return;
        var current = VersionOf(template);
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(_cacheDirectory))
                    if (!string.Equals(Path.GetFileName(directory), current, StringComparison.Ordinal))
                        Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
    }

    private static void TryWrite(string path, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
