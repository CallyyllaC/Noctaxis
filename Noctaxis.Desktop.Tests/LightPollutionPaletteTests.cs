using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using BruTile;
using Mapsui;
using Mapsui.Fetcher;
using Mapsui.Layers;
using Noctaxis.Core.Domain;
using Noctaxis.Core.LightPollution;
using Noctaxis.Desktop.Mapping;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

public sealed class LightPollutionPaletteTests
{
    [Theory]
    [InlineData("grayscale", 0,0,0, 128,128,128, 255,255,255)]
    [InlineData("turbo", 48,18,59, 164,252,60, 122,4,3)]
    [InlineData("viridis", 68,1,84, 33,145,140, 253,231,37)]
    [InlineData("inferno", 0,0,4, 188,55,84, 252,255,164)]
    [InlineData("magma", 0,0,4, 183,55,121, 252,253,191)]
    [InlineData("cividis", 0,34,78, 125,124,120, 254,232,56)]
    public void CanonicalCheckpointsClampingAndDeterminism(string id, byte r0, byte g0, byte b0,
        byte rm, byte gm, byte bm, byte r1, byte g1, byte b1)
    {
        var p = LightPollutionColorMaps.Resolve(id);
        Assert.Equal(new SKColor(r0,g0,b0), p.Map(0));
        Assert.Equal(new SKColor(rm,gm,bm), p.Map(128d/255));
        Assert.Equal(new SKColor(r1,g1,b1), p.Map(1));
        Assert.Equal(p.Map(0), p.Map(-1)); Assert.Equal(p.Map(1), p.Map(2));
        Assert.Equal(p.Map(0), p.Map(double.NaN));
        for (var i=0;i<=1024;i++) { var c=p.Map(i/1024d); Assert.Equal(255,c.Alpha); Assert.Equal(c,p.Map(i/1024d)); }
    }

    [Fact]
    public void RegistryUsesStableSingletonsAndLutInterpolationHasNoPixelAllocations()
    {
        Assert.Equal(new[] {"grayscale","turbo","viridis","inferno","magma","cividis"}, LightPollutionColorMaps.All.Select(p=>p.Id));
        Assert.Equal(new[] {"Grayscale","Turbo","Viridis","Inferno","Magma","Cividis"}, LightPollutionColorMaps.All.Select(p=>p.DisplayName));
        foreach(var p in LightPollutionColorMaps.All) Assert.Same(p,LightPollutionColorMaps.Resolve(p.Id));
        foreach(var id in new string?[] {null,"","unknown","VIRIDIS"," "}) Assert.Same(LightPollutionColorMaps.Grayscale,LightPollutionColorMaps.Resolve(id));
        // Turbo rows 0 and 1 are [0.18995,0.07176,0.23217], [0.19483,0.08339,0.26149].
        var turbo=LightPollutionColorMaps.Resolve("turbo");
        Assert.Equal(new SKColor(49,20,63),turbo.Map(.5/255));
        Assert.NotEqual(turbo.Map(0),turbo.Map(.5/255));
        foreach(var p in LightPollutionColorMaps.All)
        {
            var a=p.Map(20d/255); var b=p.Map(21d/255); var between=p.Map(20.5/255);
            Assert.InRange(Math.Abs(between.Red-(a.Red+b.Red)/2d),0,1);
            Assert.InRange(Math.Abs(between.Green-(a.Green+b.Green)/2d),0,1);
            Assert.InRange(Math.Abs(between.Blue-(a.Blue+b.Blue)/2d),0,1);
            for(var i=0;i<1000;i++) p.Map(i/1000d);
            var before=GC.GetAllocatedBytesForCurrentThread();
            for(var i=0;i<10000;i++) p.Map(i/10000d);
            Assert.Equal(before,GC.GetAllocatedBytesForCurrentThread());
        }
    }

    [Theory]
    [InlineData("{}")] [InlineData("{\"PaletteId\":null}")] [InlineData("{\"PaletteId\":\"\"}")]
    [InlineData("{\"PaletteId\":\"future\"}")] [InlineData("{\"PaletteId\":42}")]
    [InlineData("{\"PaletteId\":{\"bad\":true}}")] [InlineData("{\"PaletteId\":[]}")]
    public void MalformedPaletteCannotBreakPreferences(string json)
    {
        var p=JsonSerializer.Deserialize<LightPollutionPreferences>(json)!;
        Assert.Equal("grayscale",LightPollutionColorMaps.Resolve(p.PaletteId).Id);
    }

    [Fact]
    public async Task PaletteInvalidatesOnlyVisualCachesAndPreservesNumericalObjectsAndFiles()
    {
        await using var paths=new AtlasTestData(); using var http=Offline();
        var store=new LorenzInstallation(paths,http); var directory=await paths.SeedAsync(store);
        var generation=store.Generation; var manifest=await File.ReadAllBytesAsync(Path.Combine(directory,"manifest.json"));
        await using var provider=new LightPollutionDataProvider(store);
        using var raster=new LightPollutionRasterSource(provider,store,LightPollutionColorMaps.Grayscale);
        var tile=new TileInfo {Index=new TileIndex(0,0,0)};
        var gray=await raster.GetTileAsync(tile);
        Assert.Same(gray,await raster.GetTileAsync(tile)); Assert.True(provider.HasOverview);
        int[] decoded; using(var session=await provider.OpenAsync()) decoded=await session!.GetSourceTileAsync(36,24);
        var count=provider.DecodedTileCount;
        foreach(var p in LightPollutionColorMaps.All.Skip(1))
        {
            Assert.True(raster.SetColorMap(p)); Assert.Equal(0,raster.CachedTileCount);
            Assert.True(provider.HasOverview); Assert.Equal(count,provider.DecodedTileCount);
            using(var session=await provider.OpenAsync()) Assert.Same(decoded,await session!.GetSourceTileAsync(36,24));
            var png=await raster.GetTileAsync(tile); Assert.False(gray!.SequenceEqual(png!));
            Assert.Same(png,await raster.GetTileAsync(tile));
            Assert.False(raster.SetColorMap(p)); Assert.Same(png,await raster.GetTileAsync(tile));
            using var image=SKBitmap.Decode(png); Assert.Equal(0,image.GetPixel(128,0).Alpha); Assert.Equal(255,image.GetPixel(128,128).Alpha);
        }
        Assert.Equal(generation,store.Generation); Assert.Equal(directory,store.CurrentDirectory);
        Assert.Equal(manifest,await File.ReadAllBytesAsync(Path.Combine(directory,"manifest.json")));
        Assert.Equal(2016,Directory.GetFiles(Path.Combine(directory,"binary")).Length);
    }

    [Fact]
    public async Task PaletteChangeDuringPixelWorkRetriesWithOneConsistentPalette()
    {
        await using var paths=new AtlasTestData(); using var http=Offline(); var store=new LorenzInstallation(paths,http);
        await paths.SeedAsync(store); await using var provider=new LightPollutionDataProvider(store);
        using var blocking=new BlockingPalette(); using var raster=new LightPollutionRasterSource(provider,store,blocking);
        var task=raster.GetTileAsync(new TileInfo {Index=new TileIndex(0,0,0)});
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { raster.SetColorMap(LightPollutionColorMaps.Resolve("viridis")); }
        finally { blocking.Release.Set(); }
        using var image=SKBitmap.Decode(await task);
        Assert.Equal(LightPollutionColorMaps.Resolve("viridis").Map(new LightPollutionDisplayScale().Normalize((float)LorenzAtlas.ToLpi(100))),image.GetPixel(128,128));
        Assert.Equal(1,raster.CachedTileCount);
    }

    [AvaloniaFact]
    public async Task NativeLayerKeepsIdentityAttributionOtherLayersAndSelectedPaletteAcrossAttachments()
    {
        await using var paths=new AtlasTestData(); using var http=Offline(); var store=new LorenzInstallation(paths,http);
        await paths.SeedAsync(store);
        var map=LightPollutionMapBinding.CreateComposition(); map.Map.Layers.First().Enabled=false;
        await using var binding=new LightPollutionMapBinding(map,store);
        binding.Attach(); binding.Attach();
        var composition=map.Composition; var layers=map.Map.Layers.ToArray();
        LightPollutionMapBinding.ApplyPreferences(map,new(true,.4));
        var attribution=map.Runtime.AttributionText;
        var states=map.Composition.Layers.Where(l=>l.Id!=LightPollutionMapBinding.LayerId).Select(l=>map.Runtime[l.Id]).ToArray();
        var redraws=0; map.Map.RefreshGraphicsRequest+=(s,e)=>redraws++;
        binding.SetPalette("turbo"); Assert.Equal(1,redraws);
        binding.SetPalette("turbo"); Assert.Equal(1,redraws);
        Assert.Same(composition,map.Composition); Assert.Equal(layers,map.Map.Layers.ToArray());
        Assert.Equal(attribution,map.Runtime.AttributionText);
        Assert.Equal(states,map.Composition.Layers.Where(l=>l.Id!=LightPollutionMapBinding.LayerId).Select(l=>map.Runtime[l.Id]));
        binding.Detach(); binding.SetPalette("viridis"); Assert.Equal(1,redraws);
        binding.Attach(); binding.Attach(); Assert.Equal("viridis",binding.PaletteId);
        LightPollutionMapBinding.ApplyPreferences(map,new(false,.4)); var before=redraws;
        binding.SetPalette("magma"); Assert.Equal(before,redraws);
        await store.RemoveAsync(); Dispatcher.UIThread.RunJobs(); before=redraws;
        binding.SetPalette("cividis"); Assert.Equal(before,redraws);
        await binding.DisposeAsync(); map.Map.Dispose();
    }

    private static HttpClient Offline()=>new(new LightPollutionInstallationTests.Handler((_,_)=>throw new InvalidOperationException("No network")));
    [AvaloniaFact]
    public async Task NativeTileFetchPaintsNewPaletteWithoutReplacingLayer()
    {
        await using var paths=new AtlasTestData(); using var http=Offline(); var store=new LorenzInstallation(paths,http);
        await paths.SeedAsync(store);
        var composition=LightPollutionMapBinding.CreateComposition();
        composition.Map.Layers.First().Enabled=false;
        await using var binding=new LightPollutionMapBinding(composition,store);
        binding.Attach(); LightPollutionMapBinding.ApplyPreferences(composition,new(true,.5));
        var layer=Assert.IsType<LightPollutionTileLayer>(composition.Map.Layers.Last());
        var map=composition.Map;
        map.Navigator.SetSize(256,256); map.Navigator.CenterOnAndZoomTo(new MPoint(0,0),156543.03392804097,0);
        try
        {
            var extent=new MRect(-20037508,-20037508,20037508,20037508);
            var features=await PublishedNativeFeaturesAsync(map,layer,extent); Assert.NotEmpty(features);
            var raster=Assert.IsAssignableFrom<RasterFeature>(features[0]);
            Assert.NotNull(raster.Raster);
            using var gray=SKBitmap.Decode(raster.Raster.Data);
            Assert.Equal(gray.GetPixel(128,128).Red,gray.GetPixel(128,128).Green);
            features=await PublishedNativeFeaturesAsync(map,layer,extent,()=>binding.SetPalette("turbo")); Assert.NotEmpty(features);
            var colored=Assert.IsAssignableFrom<RasterFeature>(features[0]); Assert.NotNull(colored.Raster);
            using var turbo=SKBitmap.Decode(colored.Raster.Data);
            Assert.NotEqual(gray.GetPixel(128,128),turbo.GetPixel(128,128));
            Assert.Same(layer,map.Layers.Last());
        }
        finally { await binding.DisposeAsync(); map.Dispose(); }
    }

    private static async Task<IFeature[]> PublishedNativeFeaturesAsync(Map map, LightPollutionTileLayer layer,
        MRect extent, Action? changePalette = null)
    {
        // RefreshDataAsync/fetch-idle is not a cache-publication barrier: Mapsui's
        // tracker marks a tile done before adding its feature to the cache. Wait
        // for the actual native features, which also exclude stale palette revisions.
        // Subscribe before refresh and retain signals to avoid a check/wait race.
        var changed = new SemaphoreSlim(0);
        var gate = new object();
        var stopped = false;
        void Published(object? sender, DataChangedEventArgs args)
        {
            lock (gate) { if (!stopped) changed.Release(); }
        }
        layer.DataChanged += Published;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            changePalette?.Invoke();
            map.RefreshData();
            while (true)
            {
                Dispatcher.UIThread.RunJobs();
                var features = layer.GetFeatures(extent, 156543.03392804097).ToArray();
                if (features.Length > 0) return features;
                await changed.WaitAsync(deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Native Light Pollution features were not published within five seconds.");
        }
        finally
        {
            layer.DataChanged -= Published;
            // An already-dispatched callback must not release a disposed semaphore.
            lock (gate) { stopped = true; changed.Dispose(); }
        }
    }
    private sealed class BlockingPalette : ILightPollutionColorMap, IDisposable
    {
        public string Id=>"blocking"; public string DisplayName=>"Blocking";
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release {get;}=new();
        public SKColor Map(double value) { Started.TrySetResult(); Release.Wait(); return SKColors.Red; }
        public void Dispose()=>Release.Dispose();
    }
}
