using System.Net;
using System.Net.Http.Headers;
using BitMiracle.LibTiff.Classic;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Persistence;

namespace Noctaxis.Core.Tests;

public sealed class WsfProviderRegressionTests
{
    private static readonly GeoBounds UkChunkBounds = new(53, -1, 54, 0);
    private static readonly WsfCoverageChunk UkChunk = new("w001_n54_e000_n53", UkChunkBounds);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LiveDlrWcs_ReturnsRawScientificCoverage_WhenExplicitlyEnabled()
    {
        if (!string.Equals(System.Environment.GetEnvironmentVariable("NOCTAXIS_RUN_LIVE_WSF_TESTS"), "1",
                StringComparison.Ordinal)) return;

        using var files = new TemporaryDirectory();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Noctaxis-tests/1.0 (DLR WSF scientific validation)");
        var cache = new EnvironmentalTileCache(new TestPaths(files.Path),
            NullLogger<EnvironmentalTileCache>.Instance);
        var source = new DlrWsfCoverageSource(client, cache, NullLogger<DlrWsfCoverageSource>.Instance);
        var london = new WsfCoverageChunk("w001_n52_e000_n51", new GeoBounds(51, -1, 52, 0));

        var fraction = await source.GetCoverageAsync(london, WsfCoverageLayer.BuildingFraction, default);
        var height = await source.GetCoverageAsync(london, WsfCoverageLayer.BuildingHeight, default);

        Assert.True(fraction.HasRaster, fraction.Message);
        Assert.True(height.HasRaster, height.Message);
        Assert.Equal(Photometric.MINISBLACK, fraction.Raster!.Metadata.Photometric);
        Assert.Equal(8, fraction.Raster.Metadata.BitsPerSample);
        Assert.Equal(SampleFormat.UINT, fraction.Raster.Metadata.SampleFormat);
        Assert.Equal(64, height.Raster!.Metadata.BitsPerSample);
        Assert.Equal(SampleFormat.IEEEFP, height.Raster.Metadata.SampleFormat);
        var fractionStats = fraction.Raster.GetStatistics(value => value == 255);
        var heightStats = height.Raster.GetStatistics(value => value == -32767);
        Assert.InRange(fractionStats.Minimum!.Value, 0, 100);
        Assert.InRange(fractionStats.Maximum!.Value, 1, 100);
        Assert.True(fractionStats.NonZeroCount > 0);
        Assert.InRange(heightStats.Minimum!.Value, 0, 5000);
        Assert.InRange(heightStats.Maximum!.Value, 1, 5000);
        Assert.Equal(14.2, WsfSettlementDataProvider.NormalizeBuildingHeight(142), 12);
    }

    [Fact]
    public void CoverageUri_UsesCataloguedWcsLayersAndGeographicBounds_NotDirectTileDirectory()
    {
        var fraction = DlrWsfCoverageSource.BuildCoverageUri(UkChunk, WsfCoverageLayer.BuildingFraction);
        var height = DlrWsfCoverageSource.BuildCoverageUri(UkChunk, WsfCoverageLayer.BuildingHeight);

        Assert.Contains("/eoc/land/wcs?", fraction.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(DlrWsfCoverageSource.FractionCoverageId, fraction.Query, StringComparison.Ordinal);
        Assert.Contains(DlrWsfCoverageSource.HeightCoverageId, height.Query, StringComparison.Ordinal);
        Assert.Contains("SUBSET=Lat(53,54)", Uri.UnescapeDataString(fraction.Query), StringComparison.Ordinal);
        Assert.Contains("SUBSET=Long(-1,0)", Uri.UnescapeDataString(fraction.Query), StringComparison.Ordinal);
        Assert.DoesNotContain("/WSF3D/files/tiles/", fraction.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(25, .25)]
    [InlineData(50, .5)]
    [InlineData(100, 1)]
    public void BuildingFraction_IsNormalizedAtProviderBoundary(double raw, double expected) =>
        Assert.Equal(expected, WsfSettlementDataProvider.NormalizeBuildingFraction(raw), 12);

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void BuildingFraction_RejectsImpossibleScientificValues(double raw) =>
        Assert.Throws<InvalidDataException>(() => WsfSettlementDataProvider.NormalizeBuildingFraction(raw));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(73, 7.3)]
    [InlineData(142, 14.2)]
    [InlineData(5000, 500)]
    public void BuildingHeight_RawGainIsConvertedToMetres(double raw, double expected) =>
        Assert.Equal(expected, WsfSettlementDataProvider.NormalizeBuildingHeight(raw), 12);

    [Theory]
    [InlineData(-1)]
    [InlineData(5001)]
    public void BuildingHeight_RejectsImpossibleScientificValues(double raw) =>
        Assert.Throws<InvalidDataException>(() => WsfSettlementDataProvider.NormalizeBuildingHeight(raw));

    [Fact]
    public async Task Provider_ConvertsRawHeightGainAndFractionBeforePublishingSettlementRaster()
    {
        using var fixture = CreateProvider(Fraction([50, 50, 50, 50]), Height([142, 142, 142, 142]));

        var result = await fixture.Provider.GetSettlementAsync(Request(), default);

        Assert.True(result.HasValue);
        Assert.All(result.Value!.BuildingFraction, value => Assert.Equal(.5f, value));
        Assert.All(result.Value.BuildingHeightMetres, value => Assert.Equal(14.2f, value, .001f));
    }

    [Fact]
    public async Task Provider_ConvertsStoredInt16HeightAndHandlesNoData()
    {
        using var fixture = CreateProvider(Fraction([50, 50, 50, 50]),
            HeightInt16([142, -32767, 73, 0]));

        var result = await fixture.Provider.GetSettlementAsync(Request(), default);

        Assert.True(result.HasValue);
        Assert.Equal([14.2f, 0f, 7.3f, 0f], result.Value!.BuildingHeightMetres);
    }

    [Fact]
    public async Task ValidAllZeroFractionRaster_ProducesEmptyWithAUsableRaster()
    {
        using var fixture = CreateProvider(Fraction([0, 0, 0, 0]), Height([-32767, -32767, -32767, -32767]));

        var result = await fixture.Provider.GetSettlementAsync(Request(), default);

        Assert.Equal(EnvironmentalDataState.Empty, result.State);
        Assert.True(result.HasValue);
        Assert.All(result.Value!.BuildingFraction, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task PalettedPortrayalRaster_IsRejectedAndNeverCommittedToCache()
    {
        using var fixture = CreateProvider(Fraction([0, 25, 50, 100], Photometric.PALETTE),
            Height([0, 0, 0, 0]));

        var result = await fixture.Provider.GetSettlementAsync(Request(), default);

        Assert.Equal(EnvironmentalDataState.InvalidRaster, result.State);
        Assert.False(result.HasValue);
        Assert.DoesNotContain(ExistingCacheFiles(fixture.Cache), path =>
            path.Contains($"{Path.DirectorySeparatorChar}fraction{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MalformedTiff_IsInvalidRasterAndNeverCommittedToCache()
    {
        using var fixture = CreateProvider([0x49, 0x49, 0x2a, 0x00, 0x01, 0x02, 0x03],
            Height([0, 0, 0, 0]));

        var result = await fixture.Provider.GetSettlementAsync(Request(), default);

        Assert.Equal(EnvironmentalDataState.InvalidRaster, result.State);
        Assert.DoesNotContain(ExistingCacheFiles(fixture.Cache), path =>
            path.Contains($"{Path.DirectorySeparatorChar}fraction{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SourceFailure_IsDistinctAndDoesNotPoisonCache()
    {
        using var fixture = CreateProvider([], [], HttpStatusCode.ServiceUnavailable);

        var result = await fixture.Provider.GetSettlementAsync(Request(), default);

        Assert.Equal(EnvironmentalDataState.SourceUnavailable, result.State);
        Assert.Empty(ExistingCacheFiles(fixture.Cache));
    }

    [Fact]
    public async Task ExplicitNotFound_IsTileAbsentRatherThanTransientUnavailable()
    {
        using var fixture = CreateProvider([], [], HttpStatusCode.NotFound);

        var result = await fixture.Provider.GetSettlementAsync(Request(), default);

        Assert.Equal(EnvironmentalDataState.TileAbsent, result.State);
    }

    [Fact]
    public async Task ConcurrentCoverageRequests_AreAcquiredOnceAndThenReusedFromChunkCache()
    {
        using var fixture = CreateProvider(Fraction([0, 25, 50, 100]), Height([0, 10, 20, 30]));

        var firstPair = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            fixture.Source.GetCoverageAsync(UkChunk, WsfCoverageLayer.BuildingFraction, default)));
        var later = await fixture.Source.GetCoverageAsync(UkChunk, WsfCoverageLayer.BuildingFraction, default);

        Assert.All(firstPair, result => Assert.True(result.HasRaster));
        Assert.True(later.HasRaster);
        Assert.Equal(1, fixture.Handler.FractionRequests);
        var metadata = Assert.Single(Directory.EnumerateFiles(fixture.Cache.RootDirectory,
            "*.metadata.json", SearchOption.AllDirectories), path =>
            path.Contains($"{Path.DirectorySeparatorChar}fraction{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase));
        var metadataJson = await File.ReadAllTextAsync(metadata);
        Assert.Contains("v02-wcs-scientific-v1", metadataJson, StringComparison.Ordinal);
        Assert.Contains("fraction01", metadataJson, StringComparison.Ordinal);
        Assert.Contains("\"scale\": 0.01", metadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentFailedCoverageRequests_ShareRetryBudgetAndFailureBackoff()
    {
        using var fixture = CreateProvider([], [], HttpStatusCode.ServiceUnavailable);

        var failures = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            fixture.Source.GetCoverageAsync(UkChunk, WsfCoverageLayer.BuildingFraction, default)));
        var immediateRetry = await fixture.Source.GetCoverageAsync(UkChunk,
            WsfCoverageLayer.BuildingFraction, default);

        Assert.All(failures, result => Assert.Equal(EnvironmentalDataState.SourceUnavailable, result.State));
        Assert.Equal(EnvironmentalDataState.SourceUnavailable, immediateRetry.State);
        Assert.Equal(3, fixture.Handler.FractionRequests);
    }

    [Fact]
    public void GeoTiffRaster_DecodesWcsFloat64ScientificSamplesAndMetadata()
    {
        using var files = new TemporaryDirectory();
        var path = Path.Combine(files.Path, "height.tif");
        File.WriteAllBytes(path, Height([142, 73, 0, -32767]));

        var raster = GeoTiffRaster.Load(path);

        Assert.Equal(64, raster.Metadata.BitsPerSample);
        Assert.Equal(SampleFormat.IEEEFP, raster.Metadata.SampleFormat);
        Assert.Equal(4326, raster.Metadata.EpsgCode);
        Assert.Equal(-32767, raster.Metadata.NoDataValue);
        Assert.Equal(142, raster.Nearest(UkChunkBounds, 53.75, -.75));
    }

    private static ProviderFixture CreateProvider(byte[] fraction, byte[] height,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var files = new TemporaryDirectory();
        var handler = new RasterHandler(fraction, height, status);
        var client = new HttpClient(handler);
        var cache = new EnvironmentalTileCache(new TestPaths(files.Path),
            NullLogger<EnvironmentalTileCache>.Instance);
        var source = new DlrWsfCoverageSource(client, cache, NullLogger<DlrWsfCoverageSource>.Instance);
        var provider = new WsfSettlementDataProvider(source, NullLogger<WsfSettlementDataProvider>.Instance);
        return new ProviderFixture(files, handler, client, cache, source, provider);
    }

    private static GeoRasterRequest Request() => new(UkChunkBounds, 2, 2);

    private static IEnumerable<string> ExistingCacheFiles(EnvironmentalTileCache cache) =>
        Directory.Exists(cache.RootDirectory)
            ? Directory.EnumerateFiles(cache.RootDirectory, "*.tif", SearchOption.AllDirectories)
            : [];

    private static byte[] Fraction(double[] values, Photometric photometric = Photometric.MINISBLACK) =>
        CreateRaster(8, SampleFormat.UINT, values, 255, photometric);

    private static byte[] Height(double[] values) =>
        CreateRaster(64, SampleFormat.IEEEFP, values, -32767, Photometric.MINISBLACK);

    private static byte[] HeightInt16(double[] values) =>
        CreateRaster(16, SampleFormat.INT, values, -32767, Photometric.MINISBLACK);

    private static byte[] CreateRaster(int bits, SampleFormat format, double[] values, double noData,
        Photometric photometric)
    {
        using var files = new TemporaryDirectory();
        var path = Path.Combine(files.Path, "fixture.tif");
        GeoTiffRaster.EnsureGeoTiffTagsRegistered();
        using (var tiff = Tiff.Open(path, "w"))
        {
            tiff.SetField(TiffTag.IMAGEWIDTH, 2);
            tiff.SetField(TiffTag.IMAGELENGTH, 2);
            tiff.SetField(TiffTag.BITSPERSAMPLE, bits);
            tiff.SetField(TiffTag.SAMPLESPERPIXEL, 1);
            tiff.SetField(TiffTag.ROWSPERSTRIP, 2);
            tiff.SetField(TiffTag.COMPRESSION, Compression.NONE);
            tiff.SetField(TiffTag.PHOTOMETRIC, photometric);
            if (photometric == Photometric.PALETTE)
            {
                var palette = Enumerable.Range(0, 256).Select(value => (ushort)(value * 257)).ToArray();
                tiff.SetField(TiffTag.COLORMAP, palette, palette, palette);
            }
            tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            tiff.SetField(TiffTag.SAMPLEFORMAT, format);
            tiff.SetField(TiffTag.GEOTIFF_MODELPIXELSCALETAG, 3, new[] { .5, .5, 0d });
            tiff.SetField(TiffTag.GEOTIFF_MODELTIEPOINTTAG, 6,
                new[] { 0d, 0d, 0d, -1d, 54d, 0d });
            tiff.SetField(TiffTag.GEOTIFF_GEOKEYDIRECTORYTAG, 16, new ushort[]
            {
                1, 1, 0, 3,
                1024, 0, 1, 2,
                1025, 0, 1, 1,
                2048, 0, 1, 4326
            });
            tiff.SetField(TiffTag.GDAL_NODATA, noData.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var bytesPerSample = bits / 8;
            for (var rowIndex = 0; rowIndex < 2; rowIndex++)
            {
                var row = new byte[2 * bytesPerSample];
                for (var column = 0; column < 2; column++)
                {
                    var value = values[rowIndex * 2 + column];
                    var offset = column * bytesPerSample;
                    if (bits == 8) row[offset] = (byte)value;
                    else if (bits == 16)
                        BitConverter.TryWriteBytes(row.AsSpan(offset, 2), (short)value);
                    else
                        BitConverter.TryWriteBytes(row.AsSpan(offset, 8), value);
                }
                tiff.WriteScanline(row, rowIndex);
            }
            tiff.WriteDirectory();
        }
        return File.ReadAllBytes(path);
    }

    private sealed class RasterHandler(byte[] fraction, byte[] height, HttpStatusCode status)
        : HttpMessageHandler
    {
        private int _fractionRequests;
        private int _heightRequests;
        public int FractionRequests => Volatile.Read(ref _fractionRequests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var isHeight = request.RequestUri!.Query.Contains(DlrWsfCoverageSource.HeightCoverageId,
                StringComparison.Ordinal);
            if (isHeight) Interlocked.Increment(ref _heightRequests);
            else Interlocked.Increment(ref _fractionRequests);
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.OK)
            {
                response.Content = new ByteArrayContent(isHeight ? height : fraction);
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/tiff");
            }
            return Task.FromResult(response);
        }
    }

    private sealed record ProviderFixture(TemporaryDirectory Files, RasterHandler Handler,
        HttpClient Client, EnvironmentalTileCache Cache, DlrWsfCoverageSource Source,
        WsfSettlementDataProvider Provider) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Handler.Dispose();
            Files.Dispose();
        }
    }

    private sealed class TestPaths(string path) : IUserDataPathProvider
    {
        public string GetApplicationDataDirectory() => path;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Noctaxis.Wsf.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
