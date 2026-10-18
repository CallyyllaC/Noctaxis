using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using Noctaxis.Desktop.Mapping.Vector;
using SkiaSharp;

namespace Noctaxis.Desktop.Services;

/// <summary>A decoded vector tile placed in the world-pixel grid of its zoom (256-pixel tiles).</summary>
/// <param name="RawColumn">The column before wrapping across the antimeridian, so geometry lands beside its neighbours.</param>
public sealed record PlacedVectorTile(int Zoom, int RawColumn, int Row, VectorTile Tile)
{
    public bool IsEmpty => Tile.Layers.Count == 0;
}

/// <summary>
/// The vector tiles covering one saved-location map frame, read through the shared <see cref="VectorTileRepository"/>.
/// Bounded to the frame (never an area download), at most <see cref="MaximumParallelRequests"/> at a time, and
/// complete or nothing: a frame with any tile unavailable is not used, so partial data never replaces saved data.
/// </summary>
public static class MapFrameVectorTiles
{
    /// <summary>The frame's own zoom: one vector tile per 256 map pixels, generalised for exactly this scale.</summary>
    public const int Zoom = LocationMapThumbnailService.TileZoom;
    public const int TileSize = 256;
    /// <summary>A 896×504 frame needs at most 5×3 tiles; anything more is refused, not downloaded.</summary>
    public const int MaximumTiles = 24;
    /// <summary>OpenFreeMap's terms allow at most four parallel requests for a download.</summary>
    public const int MaximumParallelRequests = 4;

    public static async Task<IReadOnlyList<PlacedVectorTile>> LoadAsync(VectorTileRepository repository,
        WebMercatorViewport viewport, CancellationToken cancellationToken)
    {
        var scale = 1 << Zoom;
        var factor = Math.Pow(2, Zoom - viewport.Zoom) * TileSize / viewport.TileSize;
        var left = viewport.WorldPixelLeft * factor;
        var top = viewport.WorldPixelTop * factor;
        var firstX = (int)Math.Floor(left / TileSize);
        var firstY = (int)Math.Floor(top / TileSize);
        var lastX = (int)Math.Floor((left + viewport.Width * factor - 1e-9) / TileSize);
        var lastY = (int)Math.Floor((top + viewport.Height * factor - 1e-9) / TileSize);
        var keys = new List<(int RawX, int Y)>();
        for (var y = Math.Max(0, firstY); y <= Math.Min(scale - 1, lastY); y++)
        for (var x = firstX; x <= lastX; x++)
            keys.Add((x, y));
        if (keys.Count > MaximumTiles)
            throw new MapFeatureRequestException("area_too_large", "the map frame needs more vector tiles than allowed");
        var tiles = new PlacedVectorTile?[keys.Count];
        await Parallel.ForAsync(0, keys.Count, new ParallelOptions
        {
            MaxDegreeOfParallelism = MaximumParallelRequests, CancellationToken = cancellationToken
        }, async (index, token) =>
        {
            var (rawX, y) = keys[index];
            var column = ((rawX % scale) + scale) % scale;
            var tile = await repository.GetAsync(Zoom, column, y, token).ConfigureAwait(false);
            if (tile is not null) tiles[index] = new PlacedVectorTile(Zoom, rawX, y, tile);
        }).ConfigureAwait(false);
        if (tiles.Any(tile => tile is null))
            throw new MapFeatureRequestException("tile_unavailable",
                "an OpenFreeMap tile covering the map frame is unavailable (offline and not cached, or a server error)");
        return tiles!;
    }

    /// <summary>A tile coordinate (0..extent) as world pixels at the tile's zoom.</summary>
    public static (double X, double Y) WorldPixel(PlacedVectorTile tile, int extent, double x, double y) =>
        ((tile.RawColumn + x / extent) * TileSize, (tile.Row + y / extent) * TileSize);

    public static MapFeatureCoordinate Unproject(double worldX, double worldY, int zoom)
    {
        var size = TileSize * Math.Pow(2, zoom);
        var longitude = worldX / size * 360d - 180d;
        longitude = ((longitude + 180) % 360 + 360) % 360 - 180;
        var latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * Math.Clamp(worldY, 0, size) / size))) * 180 / Math.PI;
        return new MapFeatureCoordinate(latitude, longitude);
    }

    /// <summary>
    /// Clips a polyline to the tile square, so a feature crossing tiles is held once: the parts each tile carries in its
    /// buffer beyond its edge belong to the neighbour, which holds them too.
    /// </summary>
    public static List<(double X, double Y)[]> ClipToTile(VectorTilePoint[] line, int extent)
    {
        var runs = new List<(double X, double Y)[]>();
        var current = new List<(double X, double Y)>();
        for (var i = 1; i < line.Length; i++)
        {
            double x0 = line[i - 1].X, y0 = line[i - 1].Y, x1 = line[i].X, y1 = line[i].Y;
            if (!ClipSegment(ref x0, ref y0, ref x1, ref y1, extent))
            {
                Flush();
                continue;
            }
            if (current.Count == 0 || current[^1] != (x0, y0))
            {
                Flush();
                current.Add((x0, y0));
            }
            current.Add((x1, y1));
            // The segment left the square: the run ends here.
            if (x1 != line[i].X || y1 != line[i].Y) Flush();
        }
        Flush();
        return runs;

        void Flush()
        {
            if (current.Count >= 2) runs.Add([.. current]);
            current.Clear();
        }
    }

    /// <summary>Liang–Barsky clipping of one segment to [0, extent]².</summary>
    private static bool ClipSegment(ref double x0, ref double y0, ref double x1, ref double y1, int extent)
    {
        double t0 = 0, t1 = 1, dx = x1 - x0, dy = y1 - y0;
        foreach (var (p, q) in new[] { (-dx, x0), (dx, extent - x0), (-dy, y0), (dy, extent - y0) })
        {
            if (p == 0) { if (q < 0) return false; continue; }
            var r = q / p;
            if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
            else { if (r < t0) return false; if (r < t1) t1 = r; }
        }
        var (sx, sy) = (x0, y0);
        (x0, y0) = (sx + t0 * dx, sy + t0 * dy);
        (x1, y1) = (sx + t1 * dx, sy + t1 * dy);
        return true;
    }

    /// <summary>
    /// Joins runs whose ends meet (within <paramref name="tolerance"/> world pixels), so a line that crossed tile
    /// edges becomes one continuous line again. Deterministic: runs are joined in the order given.
    /// </summary>
    public static List<(double X, double Y)[]> Stitch(List<(double X, double Y)[]> runs, double tolerance)
    {
        var lines = runs.Select(run => run.ToList()).ToList();
        bool Meet((double X, double Y) a, (double X, double Y) b) =>
            Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance;
        for (var i = 0; i < lines.Count; i++)
        {
            for (var j = 0; j < lines.Count; j++)
            {
                if (i == j) continue;
                var a = lines[i];
                var b = lines[j];
                if (Meet(a[^1], b[0])) a.AddRange(b.Skip(1));
                else if (Meet(a[^1], b[^1])) a.AddRange(Enumerable.Reverse(b).Skip(1));
                else if (Meet(a[0], b[^1])) a.InsertRange(0, b.Take(b.Count - 1));
                else if (Meet(a[0], b[0])) a.InsertRange(0, Enumerable.Reverse(b).Take(b.Count - 1));
                else continue;
                lines.RemoveAt(j);
                if (j < i) i--;
                // Line i grew: look again from the start for a run that now meets one of its ends.
                j = -1;
            }
        }
        return lines.Select(line => line.ToArray()).ToList();
    }

    /// <summary>A deterministic identifier for a derived feature (vector tiles carry no stable OSM identifiers).</summary>
    public static long StableId(string layer, PlacedVectorTile tile, int feature, int part)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes($"{layer}/{tile.Zoom}/{tile.RawColumn}/{tile.Row}/{feature}/{part}"))
            hash = (hash ^ b) * 1099511628211UL;
        return (long)(hash & long.MaxValue);
    }
}

/// <summary>
/// Roads and waterways for a saved-location preview, from the OpenFreeMap vector tiles (OpenMapTiles schema) that the
/// Planner basemap draws. Replaces the public Overpass API. The classes are those the preview has always drawn:
/// motorways, trunk and primary roads (major), secondary roads (minor), and rivers, canals and streams.
/// </summary>
public sealed class OpenFreeMapMapFeatureDataService(VectorTileRepository tiles,
    ILogger<OpenFreeMapMapFeatureDataService> logger) : IMapFeatureDataService
{
    public const string ProviderId = "openfreemap-openmaptiles";
    /// <summary>Overpass documents were schema 1; vector-tile documents are 2.</summary>
    public const int FeatureSchemaVersion = 2;
    /// <summary>Bump when the derivation from tiles changes.</summary>
    public const int FeatureQueryVersion = 1;

    public async Task<MapFeatureFetchResult> FetchAsync(Guid locationId, WebMercatorViewport viewport,
        CancellationToken cancellationToken)
    {
        try
        {
            var loaded = await MapFrameVectorTiles.LoadAsync(tiles, viewport, cancellationToken).ConfigureAwait(false);
            var document = Derive(locationId, viewport, loaded, DateTimeOffset.UtcNow);
            logger.LogInformation(
                "Vector map features derived for {LocationId}: {RoadCount} roads, {WaterwayCount} waterways from {TileCount} tiles",
                locationId, document.Roads.Length, document.Waterways.Length, loaded.Count);
            return new MapFeatureFetchResult(document, MapFeatureFetchOutcome.Success(document, MapFeatureFetchStatus.Complete));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (MapFeatureRequestException ex)
        {
            logger.LogWarning("Vector map features unavailable for {LocationId}: {Code}", locationId, ex.Code);
            return new MapFeatureFetchResult(null, MapFeatureFetchOutcome.Failure(ex.Code, ex.SafeReason));
        }
    }

    public static MapFeatureSourceMetadata Source(MapGeographicBounds bounds, DateTimeOffset fetchedAtUtc) => new(
        ProviderId, "OpenFreeMap", OpenFreeMapBasemap.AttributionText, "https://www.openstreetmap.org/copyright",
        "Open Database License (ODbL)", "https://opendatacommons.org/licenses/odbl/", "openfreemap-planet",
        FeatureQueryVersion, fetchedAtUtc, bounds);

    public static MapFeatureDataDocument Derive(Guid locationId, WebMercatorViewport viewport,
        IReadOnlyList<PlacedVectorTile> loaded, DateTimeOffset fetchedAtUtc)
    {
        var roads = new Dictionary<(MapRoadClassification, string, string?), List<(double X, double Y)[]>>();
        var waterways = new Dictionary<(MapWaterwayClassification, string, string?, string?, bool), List<(double X, double Y)[]>>();
        foreach (var tile in loaded)
        {
            foreach (var feature in tile.Tile["transportation"]?.Features ?? [])
            {
                if (feature.Type != VectorTileGeometryType.LineString || feature.Number("ramp") == 1) continue;
                MapRoadClassification? classification = feature.Text("class") switch
                {
                    "motorway" => MapRoadClassification.Motorway,
                    "trunk" or "primary" => MapRoadClassification.ARoad,
                    "secondary" => MapRoadClassification.BRoad,
                    _ => null
                };
                if (classification is null) continue;
                var key = (classification.Value, feature.Text("class")!, feature.Text("brunnel"));
                Add(roads, key, tile, feature);
            }
            foreach (var feature in tile.Tile["waterway"]?.Features ?? [])
            {
                if (feature.Type != VectorTileGeometryType.LineString) continue;
                MapWaterwayClassification? classification = feature.Text("class") switch
                {
                    "river" => MapWaterwayClassification.River,
                    "canal" => MapWaterwayClassification.Canal,
                    "stream" => MapWaterwayClassification.Stream,
                    _ => null
                };
                if (classification is null) continue;
                var key = (classification.Value, feature.Text("class")!, Name(feature), feature.Text("brunnel"),
                    feature.Number("intermittent") == 1);
                Add(waterways, key, tile, feature);
            }
        }

        // World pixels at the tiles' zoom; a quarter pixel (about 5 m at the equator) joins edge-clipped runs.
        const double tolerance = .25;
        var roadFeatures = new List<MapRoadFeature>();
        foreach (var ((classification, highway, brunnel), runs) in roads.OrderBy(r => r.Key.Item1).ThenBy(r => r.Key.Item2, StringComparer.Ordinal)
                     .ThenBy(r => r.Key.Item3, StringComparer.Ordinal))
            foreach (var line in MapFrameVectorTiles.Stitch(runs, tolerance))
                roadFeatures.Add(new MapRoadFeature(Id("road", highway, line), "vector-tile", classification,
                    Geographic(line), highway, null, null, brunnel == "bridge" ? "yes" : null,
                    brunnel == "tunnel" ? "yes" : null, null));
        var waterFeatures = new List<MapWaterwayFeature>();
        foreach (var ((classification, waterway, name, brunnel, intermittent), runs) in waterways.OrderBy(w => w.Key.Item1)
                     .ThenBy(w => w.Key.Item3 ?? "", StringComparer.Ordinal).ThenBy(w => w.Key.Item4 ?? "", StringComparer.Ordinal)
                     .ThenBy(w => w.Key.Item5))
            foreach (var line in MapFrameVectorTiles.Stitch(runs, tolerance))
                waterFeatures.Add(new MapWaterwayFeature(Id("waterway", waterway + name, line), "vector-tile",
                    classification, Geographic(line), waterway, name, intermittent ? "yes" : null,
                    brunnel == "tunnel" ? "yes" : null, null));
        return new MapFeatureDataDocument(FeatureSchemaVersion, locationId, Source(viewport.Bounds, fetchedAtUtc),
            [.. roadFeatures], [.. waterFeatures]);
    }

    private static void Add<TKey>(Dictionary<TKey, List<(double X, double Y)[]>> into, TKey key, PlacedVectorTile tile,
        VectorTileFeature feature) where TKey : notnull
    {
        if (!into.TryGetValue(key, out var runs)) into[key] = runs = [];
        foreach (var part in feature.Parts)
            foreach (var run in MapFrameVectorTiles.ClipToTile(part, feature.Extent))
                runs.Add(run.Select(point => MapFrameVectorTiles.WorldPixel(tile, feature.Extent, point.X, point.Y)).ToArray());
    }

    private static MapFeatureCoordinate[] Geographic((double X, double Y)[] line) =>
        line.Select(point => MapFrameVectorTiles.Unproject(point.X, point.Y, MapFrameVectorTiles.Zoom)).ToArray();

    private static long Id(string layer, string key, (double X, double Y)[] line)
    {
        var hash = 14695981039346656037UL;
        void Mix(string text) { foreach (var b in Encoding.UTF8.GetBytes(text)) hash = (hash ^ b) * 1099511628211UL; }
        Mix(layer); Mix(key);
        Mix(FormattableString.Invariant($"{line[0].X:F2},{line[0].Y:F2},{line[^1].X:F2},{line[^1].Y:F2},{line.Length}"));
        return (long)(hash & long.MaxValue);
    }

    private static string? Name(VectorTileFeature feature) =>
        feature.Text("name") is { } name && !string.IsNullOrWhiteSpace(name) ? name : null;
}

/// <summary>
/// Area coverage for a saved-location preview: water, vegetation and built-up land, rasterised from OpenFreeMap vector
/// polygons at the preview's mask resolution. Replaces the colour classification of OpenStreetMap raster tiles.
/// </summary>
public sealed record SavedLocationLandscape(int Width, int Height, int SourceZoom, MapGeographicBounds Bounds,
    int TileCount, int EmptyTileCount, byte[] Water, byte[] Vegetation, byte[] BuiltUp)
{
    /// <summary>Every covering tile reported no data: the frame's surface is unknown, not empty land.</summary>
    public bool HasNoData => TileCount > 0 && EmptyTileCount == TileCount;
}

public static class SavedLocationLandscapeCodec
{
    public const string FileName = "landscape.bin.gz";
    /// <summary>The <see cref="SavedLocationThumbnailMetadata.SourceFormat"/> of a vector-derived landscape.</summary>
    public const string Format = "openfreemap-landscape-v1";
    public const int SchemaVersion = 1;
    private static readonly byte[] Magic = "NXLAND"u8.ToArray();

    public static byte[] Encode(SavedLocationLandscape landscape)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new BinaryWriter(gzip, Encoding.UTF8))
        {
            writer.Write(Magic); writer.Write(SchemaVersion);
            writer.Write(landscape.Width); writer.Write(landscape.Height); writer.Write(landscape.SourceZoom);
            writer.Write(landscape.Bounds.South); writer.Write(landscape.Bounds.West);
            writer.Write(landscape.Bounds.North); writer.Write(landscape.Bounds.East);
            writer.Write(landscape.TileCount); writer.Write(landscape.EmptyTileCount);
            writer.Write(landscape.Water); writer.Write(landscape.Vegetation); writer.Write(landscape.BuiltUp);
        }
        return output.ToArray();
    }

    /// <summary>The landscape, or null when the bytes are not a complete, readable landscape.</summary>
    public static SavedLocationLandscape? TryDecode(byte[] bytes)
    {
        try
        {
            using var reader = new BinaryReader(new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress), Encoding.UTF8);
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) || reader.ReadInt32() != SchemaVersion) return null;
            var width = reader.ReadInt32(); var height = reader.ReadInt32(); var zoom = reader.ReadInt32();
            if (width is < 1 or > 4096 || height is < 1 or > 4096 || zoom is < 0 or > 22) return null;
            var bounds = new MapGeographicBounds(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
            var tiles = reader.ReadInt32(); var empty = reader.ReadInt32();
            if (tiles < 0 || empty < 0 || empty > tiles) return null;
            var count = width * height;
            byte[] Mask() { var m = reader.ReadBytes(count); return m.Length == count ? m : throw new EndOfStreamException(); }
            var water = Mask(); var vegetation = Mask(); var builtUp = Mask();
            if (reader.BaseStream.ReadByte() != -1) return null;
            return new SavedLocationLandscape(width, height, zoom, bounds, tiles, empty, water, vegetation, builtUp);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Acquires a saved location's landscape for one map frame.</summary>
public interface ISavedLocationLandscapeSource
{
    MapTileSourceDefinition Definition { get; }

    /// <summary>The frame's landscape. Throws <see cref="MapFeatureRequestException"/> when it cannot be complete.</summary>
    Task<SavedLocationLandscape> AcquireAsync(WebMercatorViewport viewport, int maskScale, CancellationToken cancellationToken);
}

public sealed class OpenFreeMapLandscapeSource(VectorTileRepository tiles) : ISavedLocationLandscapeSource
{
    public static MapTileSourceDefinition OpenFreeMapDefinition { get; } = new(
        "openfreemap", "OpenFreeMap", "openmaptiles-landscape", OpenFreeMapTileFetcher.DefaultTileJson.AbsoluteUri,
        OpenFreeMapTileFetcher.DefaultTileJson.AbsoluteUri, OpenFreeMapBasemap.AttributionText,
        "https://www.openstreetmap.org/copyright", "Open Database License (ODbL)",
        "https://opendatacommons.org/licenses/odbl/");

    public MapTileSourceDefinition Definition => OpenFreeMapDefinition;

    public async Task<SavedLocationLandscape> AcquireAsync(WebMercatorViewport viewport, int maskScale,
        CancellationToken cancellationToken)
    {
        var loaded = await MapFrameVectorTiles.LoadAsync(tiles, viewport, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Rasterise(viewport, maskScale, loaded), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fills each class's polygons into its mask. Classes are kept apart as the earlier map colours were: water over
    /// everything, buildings over vegetation, vegetation over built-up land use.
    /// </summary>
    public static SavedLocationLandscape Rasterise(WebMercatorViewport viewport, int maskScale,
        IReadOnlyList<PlacedVectorTile> loaded)
    {
        var width = Math.Max(1, viewport.Width / maskScale);
        var height = Math.Max(1, viewport.Height / maskScale);
        var water = Fill(viewport, maskScale, width, height, loaded, ("water", f => f.Text("brunnel") != "tunnel"));
        var vegetation = Fill(viewport, maskScale, width, height, loaded,
            ("landcover", f => f.Text("class") is "wood" or "grass"));
        var buildings = Fill(viewport, maskScale, width, height, loaded, ("building", _ => true));
        var landUse = Fill(viewport, maskScale, width, height, loaded, ("landuse", f => f.Text("class") is
            "residential" or "suburb" or "quarter" or "neighbourhood" or "commercial" or "industrial" or "retail" or
            "railway" or "garages"));
        var builtUp = new byte[water.Length];
        for (var i = 0; i < water.Length; i++)
        {
            var open = 255 - water[i];
            var veg = vegetation[i] * (255 - buildings[i]) / 255 * open / 255;
            var built = Math.Max(buildings[i], landUse[i] * (255 - vegetation[i]) / 255) * open / 255;
            vegetation[i] = (byte)veg;
            builtUp[i] = (byte)built;
        }
        return new SavedLocationLandscape(width, height, MapFrameVectorTiles.Zoom, viewport.Bounds, loaded.Count,
            loaded.Count(tile => tile.IsEmpty), water, vegetation, builtUp);
    }

    private static byte[] Fill(WebMercatorViewport viewport, int maskScale, int width, int height,
        IReadOnlyList<PlacedVectorTile> loaded, (string Layer, Func<VectorTileFeature, bool> Include) selection)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill })
        {
            canvas.Clear(SKColors.Transparent);
            var factor = viewport.TileSize / (double)MapFrameVectorTiles.TileSize * Math.Pow(2, viewport.Zoom - MapFrameVectorTiles.Zoom);
            foreach (var tile in loaded)
            {
                if (tile.Tile[selection.Layer] is not { } layer) continue;
                foreach (var feature in layer.Features)
                {
                    if (feature.Type != VectorTileGeometryType.Polygon || !selection.Include(feature)) continue;
                    // Polygons are not clipped: each tile's buffer overlaps its neighbour's, so edges meet without seams.
                    using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
                    foreach (var ring in feature.Parts)
                    {
                        if (ring.Length < 3) continue;
                        for (var i = 0; i < ring.Length; i++)
                        {
                            var (worldX, worldY) = MapFrameVectorTiles.WorldPixel(tile, feature.Extent, ring[i].X, ring[i].Y);
                            var point = new SKPoint((float)((worldX * factor - viewport.WorldPixelLeft) / maskScale),
                                (float)((worldY * factor - viewport.WorldPixelTop) / maskScale));
                            if (i == 0) path.MoveTo(point); else path.LineTo(point);
                        }
                        path.Close();
                    }
                    canvas.DrawPath(path, paint);
                }
            }
        }
        var pixels = bitmap.GetPixelSpan();
        var mask = new byte[width * height];
        for (var y = 0; y < height; y++) pixels.Slice(y * bitmap.RowBytes, width).CopyTo(mask.AsSpan(y * width, width));
        return mask;
    }
}
