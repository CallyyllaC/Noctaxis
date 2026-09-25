using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using BruTile;
using BruTile.Cache;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Tiling.Fetcher;
using Mapsui.Tiling.Layers;
using Noctaxis.Core.Diagnostics;
using Noctaxis.Core.LightPollution;
using Noctaxis.Desktop.Diagnostics;
using Noctaxis.Desktop.Mapping;
using SkiaSharp;

internal static class ViewportProbe
{
    public static void Info()
    {
        foreach (var t in new[] { typeof(MemoryCache<IFeature>), typeof(DataFetchStrategy) })
        {
            Console.WriteLine(t.FullName);
            foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)) Console.WriteLine(m);
        }
    }
    public static async Task RunAsync(string root, string output, string? capacityEvidence = null)
    {
        Directory.CreateDirectory(output);
        using var http = new HttpClient(new Offline());
        var store = new LorenzInstallation(new Paths(root), http);
        await store.DetectAsync();
        if (!store.IsAvailable) throw new InvalidOperationException("Existing local Lorenz installation required.");
        foreach (var (width, height) in new[] { (1920,1080), (2560,1440), (3440,1440), (3840,2160) })
        {
            await using var provider = new LightPollutionDataProvider(store);
            var numerical = new LightPollutionCacheMetrics(); provider.Diagnostics = numerical;
            using var source = new LightPollutionRasterSource(provider, store, LightPollutionColorMaps.Resolve("turbo"));
            var trace = new LightPollutionRasterDiagnostics(); source.Diagnostics = trace;
            var strategy = new RecordingStrategy();
            using var layer = new LightPollutionTileLayer(source, strategy) { Opacity = .5 };
            var requests = new ConcurrentDictionary<TileIndex,int>();
            var timings = new ConcurrentBag<Timing>();
            var jobContext = new AsyncLocal<Timing?>();
            var stageName = "";
            var active = 0; var peak = 0; long generated = 0;
            trace.Requested = index => { var t=jobContext.Value!; t.Index=index; t.Started=Stopwatch.GetTimestamp(); requests.AddOrUpdate(index,1,(_,n)=>n+1); var n=Interlocked.Increment(ref active); int p; do { p=peak; } while(n>p && Interlocked.CompareExchange(ref peak,n,p)!=p); };
            trace.Completed = (index,hit,total,wait,png,bytes) => { Interlocked.Decrement(ref active); if(!hit) Interlocked.Increment(ref generated); var t=jobContext.Value!; t.Hit=hit; t.TotalMs=total; t.WaitMs=wait; t.PngMs=png; t.Bytes=bytes; t.SourceCompleted=Stopwatch.GetTimestamp(); };
            var centerX = -.1278 / 180 * 20037508.342789244;
            var centerY = Math.Log(Math.Tan(Math.PI/4+51.5074*Math.PI/360))*6378137;
            var steps = new List<object>();
            var nativeCache = (MemoryCache<IFeature>)typeof(TileLayer).GetProperty("MemoryCache",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(layer)!;
            var nativeField = nativeCache.GetType().GetField("_bitmaps",BindingFlags.NonPublic|BindingFlags.Instance)!;
            // Ablation only: use the measured complete path union, never a guessed constant.
            if(capacityEvidence is not null)
            {
                var union=new HashSet<string>();
                foreach(var file in Directory.GetFiles(capacityEvidence,$"{width}x{height}-*-detail.json"))
                {
                    using var evidence=JsonDocument.Parse(await File.ReadAllTextAsync(file));
                    foreach(var tile in evidence.RootElement.GetProperty("requested").EnumerateArray())
                        union.Add($"{tile.GetProperty("Level")}/{tile.GetProperty("Col")}/{tile.GetProperty("Row")}");
                }
                if(union.Count==0) throw new InvalidOperationException("Actual strategy tile lists are required for the ablation.");
                var capacity=union.Count;
                nativeCache.MaxTiles=capacity; nativeCache.MinTiles=capacity;
            }
            HashSet<TileIndex> NativeKeys() => ((Dictionary<TileIndex,IFeature>)nativeField.GetValue(nativeCache)!).Keys.ToHashSet();
            var previousNative = NativeKeys(); long nativeEvictions=0, nativeVisibleEvictions=0;
            var nativePeak=0; long renderedVisibleEvictions=0, nativeRelevantEvictions=0;
            // Same tile-relative path at every viewport; each stage drains actual fetch tasks.
            foreach (var (name, zoom, dx, dy) in new[] { ("initial",8d,0d,0d), ("pan-east",8d,512d,0d), ("pan-south",8d,512d,-384d), ("zoom-out",7d,512d,-384d), ("zoom-mid",7.5,512d,-384d), ("return",8d,0d,0d) })
            {
                stageName=name;
                peak=0;
                var viewportStarted=Stopwatch.GetTimestamp();
                var resolution = 156543.03392804097/Math.Pow(2,zoom);
                var cx=centerX+dx*611.49622628141; var cy=centerY+dy*611.49622628141;
                var extent=new MRect(cx-width*resolution/2,cy-height*resolution/2,cx+width*resolution/2,cy+height*resolution/2);
                var info=new FetchInfo(new MSection(extent,resolution));
                layer.ViewportChanged(info);
                var firstJobs=layer.GetFetchJobs(0,4);
                var level=strategy.Level;
                var visible=source.Schema.GetTileInfos(new Extent(extent.MinX,extent.MinY,extent.MaxX,extent.MaxY),level).ToArray();
                var fetched = strategy.Tiles;
                var visibleIds=visible.Select(t=>t.Index).ToHashSet();
                var relevantIds=fetched.Select(t=>t.Index).ToHashSet();
                var expectedHits=fetched.Count(t=>previousNative.Contains(t.Index));
                trace.Cache.Evicted = key => { var tuple=(System.Runtime.CompilerServices.ITuple)key; if(visibleIds.Contains(new TileIndex((int)tuple[2]!, (int)tuple[3]!, (int)tuple[1]!))) renderedVisibleEvictions++; };
                var batches=0; var jobsTotal=0; var watch=Stopwatch.StartNew();
                while(true)
                {
                    var jobs=firstJobs;
                    firstJobs=[];
                    if(jobs.Length==0) jobs=layer.GetFetchJobs(0,4);
                    if(jobs.Length==0) break;
                    jobsTotal+=jobs.Length;
                    var published = await Task.WhenAll(jobs.Select(async j => {
                        var t=new Timing { Stage=stageName }; jobContext.Value=t;
                        await j.FetchFunc();
                        t.PublicationMs=Stopwatch.GetElapsedTime(t.Started).TotalMilliseconds;
                        t.ViewportPublicationMs=Stopwatch.GetElapsedTime(viewportStarted).TotalMilliseconds;
                        t.AfterSourceMs=Stopwatch.GetElapsedTime(t.SourceCompleted).TotalMilliseconds;
                        timings.Add(t); return t.Index;
                    })).WaitAsync(TimeSpan.FromSeconds(60));
                    _=layer.GetFeatures(extent,resolution).ToArray();
                    var keys=NativeKeys(); var removed=previousNative.Union(published).Except(keys).ToArray();
                    nativeEvictions+=removed.Length; nativeVisibleEvictions+=removed.Count(visibleIds.Contains);
                    nativeRelevantEvictions+=removed.Count(relevantIds.Contains);
                    if(removed.Length>0) nativePeak=Math.Max(nativePeak,nativeCache.MaxTiles);
                    previousNative=keys; nativePeak=Math.Max(nativePeak,keys.Count);
                    if(++batches>500) throw new InvalidOperationException("Unbounded fetch churn");
                }
                var features=layer.GetFeatures(extent,resolution).ToArray();
                watch.Stop();
                SaveMosaic(features,extent,width,height,Path.Combine(output,$"{width}x{height}-{name}.png"));
                SaveNative(layer, new Viewport(cx,cy,resolution,0,width,height), Path.Combine(output,$"{width}x{height}-{name}-native.png"));
                var missing=visible.Count(t=>!features.Any(f=>f.Extent is {} e && e.MinX<=t.Extent.CenterX && e.MaxX>=t.Extent.CenterX && e.MinY<=t.Extent.CenterY && e.MaxY>=t.Extent.CenterY));
                var cache=typeof(TileLayer).GetProperty("MemoryCache",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(layer)!;
                var nativeCount=cache.GetType().GetProperty("TileCount")?.GetValue(cache);
                var stageTimings=timings.Where(t=>t.Stage==name).ToArray();
                var row=new {width,height,name,zoom,resolution,level,levels=fetched.GroupBy(t=>t.Index.Level).OrderByDescending(g=>g.Key).ToDictionary(g=>g.Key,g=>g.Count()),currentLevel=fetched.Count(t=>t.Index.Level==level),fallback=fetched.Count(t=>t.Index.Level!=level),columns=visible.Select(t=>t.Index.Col).Distinct().Count(),rows=visible.Select(t=>t.Index.Row).Distinct().Count(),visible=visible.Length,fetchWorkingSet=fetched.Count,expectedHits,expectedMisses=fetched.Count-expectedHits,jobsTotal,missing,currentLevelMissing=visibleIds.Except(previousNative).Count(),features=features.Length,nativeCount,nativePeak,nativeEvictions,nativeVisibleEvictions,nativeRelevantEvictions,renderedVisibleEvictions,renderedCache=source.CachedTileCount,renderedHits=trace.Cache.Hits,renderedMisses=trace.Cache.Misses,renderedEvictions=trace.Cache.Evictions,numericalHits=numerical.Hits,numericalMisses=numerical.Misses,numericalEvictions=numerical.Evictions,decoded=provider.DecodedTileCount,unique=requests.Count,requests=requests.Values.Sum(),repeated=requests.Values.Sum()-requests.Count,generated,peak,active,elapsedMs=watch.Elapsed.TotalMilliseconds,waitMs=Stats(stageTimings.Select(t=>t.WaitMs)),generationMs=Stats(stageTimings.Where(t=>!t.Hit).Select(t=>t.TotalMs-t.WaitMs-t.PngMs)),pngMs=Stats(stageTimings.Where(t=>!t.Hit).Select(t=>t.PngMs)),publicationMs=Stats(stageTimings.Select(t=>t.PublicationMs)),afterSourceMs=Stats(stageTimings.Select(t=>t.AfterSourceMs)),encodedBytes=Stats(stageTimings.Select(t=>(double)t.Bytes))};
                steps.Add(row); Console.WriteLine(JsonSerializer.Serialize(row));
                var sourceCache=typeof(LightPollutionRasterSource).GetField("_cache",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(source)!;
                var items=(IDictionary)sourceCache.GetType().GetField("_items",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(sourceCache)!;
                var encoded=new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
                foreach(DictionaryEntry item in items)
                {
                    var pair=(System.Runtime.CompilerServices.ITuple)item.Value!.GetType().GetProperty("Value")!.GetValue(item.Value)!;
                    encoded.Add((byte[])pair[1]!);
                }
                var sourceBytes=encoded.Sum(b=>(long)b.Length);
                foreach(var feature in ((Dictionary<TileIndex,IFeature>)nativeField.GetValue(nativeCache)!).Values.OfType<RasterFeature>()) encoded.Add(feature.Raster.Data);
                var memory=new { sourceBytes, distinctEncodedBytes=encoded.Sum(b=>(long)b.Length), numericalBytes=provider.DecodedTileCount*600L*600*4+(provider.HasOverview?4320L*1680*4:0), conservativeNativeDecodedBytes=nativeCache.TileCount*256L*256*4, nativeCapacity=nativeCache.MaxTiles };
                await File.WriteAllTextAsync(Path.Combine(output,$"{width}x{height}-{name}-detail.json"),JsonSerializer.Serialize(new {requested=fetched.Select(t=>new {t.Index.Level,t.Index.Col,t.Index.Row}),actualFetchLevels=stageTimings.GroupBy(t=>t.Index.Level).OrderByDescending(g=>g.Key).ToDictionary(g=>g.Key,g=>g.Count()),notScheduledByNativePlanner=fetched.Count-expectedHits-jobsTotal,viewportPublicationMs=Stats(stageTimings.Select(t=>t.ViewportPublicationMs)),memory},new JsonSerializerOptions { WriteIndented=true }));
            }
            await File.WriteAllTextAsync(Path.Combine(output,$"{width}x{height}.json"),JsonSerializer.Serialize(new {steps,timings,trace.RejectedRevisions,trace.EmptyResults,trace.Failures},new JsonSerializerOptions{WriteIndented=true}));
        }
    }
    private static void SaveNative(ILayer layer, Viewport viewport, string path)
    {
        var renderer=new Mapsui.Rendering.Skia.MapRenderer();
        using var service=new Mapsui.Rendering.RenderService();
        using var image=renderer.RenderToBitmapStream(viewport,[layer],service,new Mapsui.Styles.Color(30,30,30));
        using var file=File.Create(path); image.Position=0; image.CopyTo(file);
    }
    private static void SaveMosaic(IFeature[] features, MRect extent, int width, int height, string path)
    {
        using var bitmap=new SKBitmap(width,height);
        using var canvas=new SKCanvas(bitmap);
        canvas.Clear(new SKColor(30,30,30));
        foreach(var feature in features.OfType<RasterFeature>().OrderByDescending(f=>f.Extent!.Width))
        {
            using var tile=SKBitmap.Decode(feature.Raster.Data);
            var e=feature.Extent!;
            canvas.DrawBitmap(tile,new SKRect((float)((e.MinX-extent.MinX)/extent.Width*width),(float)((extent.MaxY-e.MaxY)/extent.Height*height),(float)((e.MaxX-extent.MinX)/extent.Width*width),(float)((extent.MaxY-e.MinY)/extent.Height*height)));
        }
        using var image=SKImage.FromBitmap(bitmap);
        using var data=image.Encode(SKEncodedImageFormat.Png,100);
        using var stream=File.Create(path); data.SaveTo(stream);
    }
    private static object Stats(IEnumerable<double> values)
    {
        var a=values.Order().ToArray();
        return new { count=a.Length, sum=a.Sum(), mean=a.Length==0?0:a.Average(), p95=a.Length==0?0:a[(int)Math.Floor((a.Length-1)*.95)], max=a.Length==0?0:a[^1] };
    }
    private sealed class Timing
    {
        public string Stage {get;set;}="";
        public TileIndex Index {get;set;}
        public bool Hit {get;set;}
        public double TotalMs {get;set;}
        public double WaitMs {get;set;}
        public double PngMs {get;set;}
        public int Bytes {get;set;}
        public double PublicationMs {get;set;}
        public double ViewportPublicationMs {get;set;}
        public double AfterSourceMs {get;set;}
        public long Started, SourceCompleted;
    }
    private sealed class RecordingStrategy : IDataFetchStrategy
    {
        private readonly DataFetchStrategy _inner = new(3);
        public int Level { get; private set; }
        public IList<TileInfo> Tiles { get; private set; } = [];
        public IList<TileInfo> Get(ITileSchema schema, Extent extent, int level)
        {
            Level = level;
            Tiles = _inner.Get(schema, extent, level);
            return Tiles;
        }
    }
    private sealed class Offline : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>throw new InvalidOperationException("Viewport probe cannot use HTTP."); }
}
