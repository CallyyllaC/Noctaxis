using System.Net;
using System.Text.Json;
using Noctaxis.Core.Persistence;

namespace Noctaxis.Core.LightPollution;

public sealed record LorenzManifest(string ProviderId, string DatasetName, int Year, string SourceFormat,
    int ImportVersion, double ResolutionDegrees, double South, double North, int ExpectedTiles,
    Dictionary<string, long> Tiles, long InstalledBytes, bool Complete);
public sealed record LorenzInstallProgress(int CompletedTiles, int TotalTiles, long DownloadedBytes);

/// <summary>Optional local dataset store. Only InstallAsync contacts the publisher. DataGate protects
/// read leases and commit/remove; network work stages independently of the live installation.</summary>
public sealed class LorenzInstallation(IUserDataPathProvider paths, HttpClient http)
{
    private readonly string _root = Path.Combine(paths.GetApplicationDataDirectory(), "EnvironmentalData", "LightPollution", "Lorenz", "2025");
    private readonly SemaphoreSlim _operation = new(1, 1);
    internal readonly SemaphoreSlim DataGate = new(1, 1);
    private string? _current;
    public string? CurrentDirectory => Volatile.Read(ref _current);
    public bool IsAvailable => CurrentDirectory is not null;
    public string StorageDirectory => _root;
    public long Generation { get; private set; }
    public string? ReadError { get; private set; }
    // Raised under DataGate. Subscribers must only clear caches or post UI work; never await a read lease.
    public event EventHandler? Changed;

    public async Task DetectAsync(CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken);
        try
        {
            string? valid = null;
            try
            {
                var name = (await File.ReadAllTextAsync(Path.Combine(_root, "current"), cancellationToken)).Trim();
                if (Guid.TryParseExact(name, "N", out _))
                {
                    var directory = Path.Combine(_root, name);
                    var manifest = JsonSerializer.Deserialize<LorenzManifest>(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json"), cancellationToken));
                    if (manifest is { Complete: true, ProviderId: LorenzAtlas.ProviderId, Year: 2025, ImportVersion: 1,
                        SourceFormat: "signed-int8-delta-v1", ExpectedTiles: LorenzAtlas.TileCount, ResolutionDegrees: 1d / 120, South: -65, North: 75 } &&
                        manifest.Tiles is { Count: LorenzAtlas.TileCount } &&
                        new FileInfo(Path.Combine(directory, "overview.f32")).Length == (long)LorenzAtlas.OverviewWidth * LorenzAtlas.OverviewHeight * 4 &&
                        ExpectedTiles().All(tile => manifest.Tiles.TryGetValue(LorenzAtlas.FileName(tile.X, tile.Y), out var size) && size > 0 &&
                            new FileInfo(Path.Combine(directory, "binary", LorenzAtlas.FileName(tile.X, tile.Y))) is { Exists: true } file && file.Length == size))
                        valid = directory;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            await DataGate.WaitAsync(cancellationToken);
            try
            {
                _current = valid;
                ReadError = valid is null && File.Exists(Path.Combine(_root, "current")) ? "Installation incomplete · repair or reinstall" : null;
                Generation++; Changed?.Invoke(this, EventArgs.Empty);
            }
            finally { DataGate.Release(); }
        }
        finally { _operation.Release(); }
    }

    public async Task InstallAsync(IProgress<LorenzInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken);
        var staging = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var committed = false;
        try
        {
            Directory.CreateDirectory(Path.Combine(staging, "binary"));
            var overview = new float[LorenzAtlas.OverviewWidth * LorenzAtlas.OverviewHeight];
            var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
            var completed = 0;
            long bytes = 0;
            var progressGate = new object();
            await Parallel.ForEachAsync(ExpectedTiles(), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (tile, ct) =>
            {
                var filename = LorenzAtlas.FileName(tile.X, tile.Y);
                var temporary = Path.Combine(staging, "binary", filename + ".part");
                await DownloadAsync(LorenzAtlas.Source(tile.X, tile.Y), temporary, ct);
                int[] values;
                await using (var file = File.OpenRead(temporary)) values = await LorenzDecoder.DecodeGzipAsync(file, ct);
                for (var oy = 0; oy < 60; oy++)
                for (var ox = 0; ox < 60; ox++)
                {
                    double sum = 0;
                    for (var y = 0; y < 10; y++)
                    for (var x = 0; x < 10; x++) sum += LorenzAtlas.ToLpi(values[(oy * 10 + y) * 600 + ox * 10 + x]);
                    var average = sum / 100;
                    if (!double.IsFinite(average) || average < 0 || average > float.MaxValue)
                        throw new InvalidDataException("Invalid numerical brightness in Lorenz tile.");
                    overview[((tile.Y - 1) * 60 + oy) * LorenzAtlas.OverviewWidth + (tile.X - 1) * 60 + ox] = (float)average;
                }
                File.Move(temporary, Path.Combine(staging, "binary", filename));
                var length = new FileInfo(Path.Combine(staging, "binary", filename)).Length;
                lock (progressGate)
                {
                    sizes.Add(filename, length); bytes += length; completed++;
                    progress?.Report(new(completed, LorenzAtlas.TileCount, bytes));
                }
            });
            // Numerical, palette-independent overview prevents world views from decoding every full source tile.
            await using (var file = File.Create(Path.Combine(staging, "overview.f32")))
            {
                var data = new byte[overview.Length * 4];
                for (var i = 0; i < overview.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4, 4), overview[i]);
                await file.WriteAsync(data, cancellationToken);
            }
            var manifest = new LorenzManifest(LorenzAtlas.ProviderId, LorenzAtlas.Name, 2025, "signed-int8-delta-v1", 1,
                1d / 120, -65, 75, LorenzAtlas.TileCount, sizes, bytes, true);
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(manifest), cancellationToken);
            await DataGate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pointer = Path.Combine(_root, "current.part");
                await File.WriteAllTextAsync(pointer, Path.GetFileName(staging), cancellationToken);
                File.Move(pointer, Path.Combine(_root, "current"), overwrite: true);
                _current = staging; ReadError = null; committed = true; Generation++; Changed?.Invoke(this, EventArgs.Empty);
            }
            finally { DataGate.Release(); }
            CleanupExcept(staging);
        }
        finally
        {
            try { if (!committed && Directory.Exists(staging)) DeleteGeneration(staging); }
            finally { _operation.Release(); }
        }
    }

    private async Task DownloadAsync(Uri uri, string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidDataException($"Missing required source tile: {uri.AbsolutePath}");
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(path);
                var buffer = new byte[32768];
                int count; long total = 0;
                while ((count = await input.ReadAsync(buffer, ct)) != 0)
                {
                    total += count;
                    if (total > 2 * 1024 * 1024) throw new InvalidDataException("Oversized source tile.");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                return;
            }
            catch (Exception ex) when (attempt < 2 && !ct.IsCancellationRequested &&
                (ex is HttpRequestException { StatusCode: null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests } ||
                 ex is HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError } || ex is TaskCanceledException))
            { await Task.Delay(TimeSpan.FromMilliseconds(500 * (1 << attempt)), ct); }
        }
    }

    public async Task RemoveAsync(CancellationToken ct = default)
    {
        await _operation.WaitAsync(ct);
        try
        {
            await DataGate.WaitAsync(ct);
            try
            {
                _current = null; ReadError = null; Generation++; Changed?.Invoke(this, EventArgs.Empty);
                File.Delete(Path.Combine(_root, "current"));
                CleanupExcept(null);
            }
            finally { DataGate.Release(); }
        }
        finally { _operation.Release(); }
    }

    // Called by a reader holding DataGate. A broken installed file is an error, not zero brightness.
    internal void InvalidateRead()
    {
        _current = null; ReadError = "Local atlas data could not be read · repair or reinstall";
        Generation++; Changed?.Invoke(this, EventArgs.Empty);
    }

    private void CleanupExcept(string? keep)
    {
        if (!Directory.Exists(_root)) return;
        foreach (var directory in Directory.EnumerateDirectories(_root))
            if (directory != keep && Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) DeleteGeneration(directory);
    }
    private void DeleteGeneration(string directory)
    {
        // Never recurse outside this dataset's direct, generated children (including via symlinks).
        if (Path.GetDirectoryName(Path.GetFullPath(directory)) != Path.GetFullPath(_root) ||
            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Unsafe dataset directory.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unexpected link in dataset.");
        Directory.Delete(directory, recursive: true);
    }
    public static IEnumerable<(int X, int Y)> ExpectedTiles()
    {
        for (var y = 1; y <= LorenzAtlas.Rows; y++) for (var x = 1; x <= LorenzAtlas.Columns; x++) yield return (x, y);
    }
}
