using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using BruTile;
using Noctaxis.Core.LightPollution;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Mapping;
using SkiaSharp;

internal static class PaletteProbe
{
    public static async Task RunAsync(string root)
    {
        using var http = new HttpClient(new OfflineHandler());
        var installation = new LorenzInstallation(new Paths(root), http);
        await installation.DetectAsync();
        if (!installation.IsAvailable) throw new InvalidOperationException("An existing installed atlas is required; this mode never downloads/imports.");
        var directory = installation.CurrentDirectory!;
        var before = Hashes(directory);
        var output = Path.Combine(root, "phase4b"); Directory.CreateDirectory(output);
        await using var provider = new LightPollutionDataProvider(installation);
        using var source = new LightPollutionRasterSource(provider, installation, LightPollutionColorMaps.Grayscale);
        var cases = new[] { ("world",0,0d,0d), ("london",8,51.5074,-.1278), ("rural-wales",8,52.25,-3.75), ("mixed-uk",6,52.5,-2.5) };
        // Warm runtime/JIT and numerical caches equally before comparative measurements.
        foreach(var p in LightPollutionColorMaps.All)
        {
            source.SetColorMap(p);
            foreach(var c in cases) await source.GetTileAsync(Tile(c.Item2,c.Item3,c.Item4));
        }
        var measurements = new List<object>();
        foreach(var c in cases)
        {
            var tile = Tile(c.Item2,c.Item3,c.Item4);
            using var sheet = new SKBitmap(3*256,2*288);
            using var canvas = new SKCanvas(sheet); canvas.Clear(new SKColor(32,32,32));
            using var text = new SKPaint { Color=SKColors.White, IsAntialias=true };
            using var font = new SKFont(SKTypeface.Default,16);
            var paletteIndex=0;
            foreach(var palette in LightPollutionColorMaps.All)
            {
                var cold = new List<double>(); var cached = new List<double>(); byte[]? png=null;
                for(var repeat=0;repeat<5;repeat++)
                {
                    source.SetColorMap(palette.Id=="grayscale" ? LightPollutionColorMaps.Resolve("turbo") : LightPollutionColorMaps.Grayscale);
                    source.SetColorMap(palette);
                    var timer=Stopwatch.StartNew(); png=await source.GetTileAsync(tile); cold.Add(timer.Elapsed.TotalMilliseconds);
                    timer.Restart(); var hit=await source.GetTileAsync(tile); cached.Add(timer.Elapsed.TotalMilliseconds);
                    if(!ReferenceEquals(png,hit)) throw new InvalidOperationException("Rendered cache miss");
                }
                await File.WriteAllBytesAsync(Path.Combine(output,$"{c.Item1}-{palette.Id}.png"),png!);
                using var image=SKBitmap.Decode(png);
                // Separate mapping cost from PNG entropy/compression cost when comparing palettes.
                var scalars=Enumerable.Range(0,65536).Select(i=>i/65535d).ToArray();
                var mapped=new SKColor[scalars.Length]; var mapping=new List<double>(); var encoding=new List<double>();
                for(var repeat=0;repeat<7;repeat++)
                {
                    var timer=Stopwatch.StartNew();
                    for(var pixel=0;pixel<scalars.Length;pixel++) mapped[pixel]=palette.Map(scalars[pixel]);
                    mapping.Add(timer.Elapsed.TotalMilliseconds);
                    using var encodeImage=SKImage.FromBitmap(image); timer.Restart();
                    using var encodeData=encodeImage.Encode(SKEncodedImageFormat.Png,100); encoding.Add(timer.Elapsed.TotalMilliseconds);
                }
                var x=paletteIndex%3*256; var y=paletteIndex/3*288;
                canvas.DrawText(palette.DisplayName,x+8,y+21,font,text); canvas.DrawBitmap(image,x,y+32); paletteIndex++;
                var measure=new { location=c.Item1,palette=palette.Id,z=c.Item2,x=tile.Index.Col,y=tile.Index.Row,
                    coldMedianMs=cold.Order().ElementAt(2),cachedMedianMs=cached.Order().ElementAt(2),coldMs=cold,cachedMs=cached,
                    mapping65536MedianMs=mapping.Order().ElementAt(3),pngEncodeMedianMs=encoding.Order().ElementAt(3),pngBytes=png!.Length };
                measurements.Add(measure); Console.WriteLine(JsonSerializer.Serialize(measure));
            }
            using var encoded=SKImage.FromBitmap(sheet); using var data=encoded.Encode(SKEncodedImageFormat.Png,100);
            await File.WriteAllBytesAsync(Path.Combine(output,$"{c.Item1}-comparison.png"),data.ToArray());
        }
        // Adjacent native tiles around the Greenwich boundary, joined without resampling.
        foreach(var palette in LightPollutionColorMaps.All)
        {
            source.SetColorMap(palette); using var join=new SKBitmap(512,256); using var canvas=new SKCanvas(join);
            for(var i=0;i<2;i++)
            { using var tile=SKBitmap.Decode(await source.GetTileAsync(new TileInfo {Index=new TileIndex(127+i,85,8)})); canvas.DrawBitmap(tile,i*256,0); }
            using var image=SKImage.FromBitmap(join); using var data=image.Encode(SKEncodedImageFormat.Png,100);
            await File.WriteAllBytesAsync(Path.Combine(output,$"boundary-{palette.Id}.png"),data.ToArray());
        }
        if(!before.OrderBy(p=>p.Key).SequenceEqual(Hashes(directory).OrderBy(p=>p.Key))) throw new InvalidOperationException("Source data changed");
        await File.WriteAllTextAsync(Path.Combine(output,"timings.json"),JsonSerializer.Serialize(measurements,new JsonSerializerOptions {WriteIndented=true}));
        Console.WriteLine($"Verified unchanged SHA-256 for {before.Count} installed files. No HTTP requests.");
    }
    private static Dictionary<string,string> Hashes(string path)=>Directory.GetFiles(path,"*",SearchOption.AllDirectories)
        .ToDictionary(p=>Path.GetRelativePath(path,p),p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
    private static TileInfo Tile(int z,double lat,double lon)
    {
        var n=1<<z; return new() {Index=new TileIndex((int)Math.Floor((lon+180)/360*n),
            (int)Math.Floor((1-Math.Asinh(Math.Tan(lat*Math.PI/180))/Math.PI)/2*n),z)};
    }
    private sealed record Paths(string Root):IUserDataPathProvider { public string GetApplicationDataDirectory()=>Root; }
    private sealed class OfflineHandler:HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>throw new InvalidOperationException("Palette probe must never access HTTP"); }
}
