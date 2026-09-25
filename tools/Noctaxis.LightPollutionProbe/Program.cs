using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using BruTile;
using Noctaxis.Core.LightPollution;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Mapping;

// Explicit opt-in only. --install downloads all source files; --parity reads existing sample files.
if (args.Length == 2 && args[0] == "--viewport-info") { ViewportProbe.Info(); return; }
if (args.Length == 3 && args[0] == "--viewports") { await ViewportProbe.RunAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2])); return; }
if (args.Length == 4 && args[0] == "--viewports-native-ablation") { await ViewportProbe.RunAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3])); return; }
if (args.Length != 2 || args[0] is not ("--install" or "--parity" or "--palettes"))
    throw new ArgumentException("Usage: --install <isolated-data-directory> | --parity <directory-containing-lorenz-*.dat.gz>");
if (args[0] == "--palettes") { await PaletteProbe.RunAsync(Path.GetFullPath(args[1])); return; }
if (args[0] == "--parity")
{
    foreach (var tile in new[] { (1, 1), (36, 24), (72, 28) })
    {
        var path = Path.Combine(args[1], $"lorenz-{tile.Item1}_{tile.Item2}.dat.gz");
        await using var file = File.OpenRead(path);
        var decoded = await LorenzDecoder.DecodeGzipAsync(file);
        file.Position = 0;
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var output = new MemoryStream(); await gzip.CopyToAsync(output);
        var data = output.ToArray().Select(b => (int)unchecked((sbyte)b)).ToArray();
        foreach (var cell in new[] { (0, 0), (299, 299), (599, 599), (0, 599), (599, 0) })
        {
            // Literal publisher loop, with ix/iy being one-based cell indices.
            var ix = cell.Item1 + 1; var iy = cell.Item2 + 1;
            var reference = 128 * data[0] + data[1];
            for (var i = 1; i < iy; i++) reference += data[600 * i + 1];
            for (var i = 1; i < ix; i++) reference += data[600 * (iy - 1) + 1 + i];
            if (reference != decoded[cell.Item2 * 600 + cell.Item1]) throw new InvalidDataException("Reference mismatch");
            Console.WriteLine(JsonSerializer.Serialize(new { tile = $"{tile.Item1}_{tile.Item2}", latitude = -65 + (tile.Item2 - 1) * 5 + (cell.Item2 + .5) / 120,
                longitude = -180 + (tile.Item1 - 1) * 5 + (cell.Item1 + .5) / 120, compressed = reference, lpi = LorenzAtlas.ToLpi(reference) }));
        }
    }
    return;
}
var root = Path.GetFullPath(args[1]);
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Noctaxis/1.0 (explicit numerical atlas validation)");
var store = new LorenzInstallation(new Paths(root), http);
await store.DetectAsync();
if (!store.IsAvailable) await store.InstallAsync(new Progress<LorenzInstallProgress>(p => { if (p.CompletedTiles % 100 == 0) Console.WriteLine($"{p.CompletedTiles}/2016 {p.DownloadedBytes} bytes"); }));
await using var provider = new LightPollutionDataProvider(store);
using var raster = new LightPollutionRasterSource(provider, store, new GrayscaleLightPollutionColorMap());
Directory.CreateDirectory(Path.Combine(root, "smoke"));
foreach (var location in new[] { ("London", 51.5074, -.1278), ("Rural-Wales", 52.25, -3.75), ("York", 53.96, -1.08), ("Boundary-west", 51.5, -.00001), ("Boundary-east", 51.5, .00001) })
    Console.WriteLine(JsonSerializer.Serialize(new { name = location.Item1, latitude = location.Item2, longitude = location.Item3,
        nearest = await provider.SampleAsync(location.Item2, location.Item3), bilinear = await provider.SampleAsync(location.Item2, location.Item3, true) }));
foreach (var z in new[] { 0, 4, 8 })
{
    var n = 1 << z; var x = (int)Math.Floor((180 - .1278) / 360 * n);
    var y = (int)Math.Floor((1 - Math.Asinh(Math.Tan(51.5074 * Math.PI / 180)) / Math.PI) / 2 * n);
    var info = new TileInfo { Index = new TileIndex(x, y, z) };
    var timer = Stopwatch.StartNew(); var png = await raster.GetTileAsync(info); var cold = timer.Elapsed.TotalMilliseconds;
    timer.Restart(); await raster.GetTileAsync(info); var warm = timer.Elapsed.TotalMilliseconds;
    await File.WriteAllBytesAsync(Path.Combine(root, "smoke", $"london-z{z}.png"), png!);
    Console.WriteLine(JsonSerializer.Serialize(new { z, x, y, coldMs = cold, cachedMs = warm, decodedTiles = provider.DecodedTileCount }));
}
sealed record Paths(string Root) : IUserDataPathProvider { public string GetApplicationDataDirectory() => Root; }
