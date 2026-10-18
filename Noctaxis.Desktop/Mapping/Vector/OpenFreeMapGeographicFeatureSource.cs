using System.Globalization;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Locations.Descriptions;
using Noctaxis.Desktop.Services;

namespace Noctaxis.Desktop.Mapping.Vector;

/// <summary>
/// Feature discovery for location descriptions from OpenFreeMap vector tiles (OpenMapTiles schema), read through the
/// shared <see cref="VectorTileRepository"/>: the tiles the Planner draws, from the same disk cache, so a place already
/// viewed is described offline. Replaces reverse geocoding with the public Nominatim service.
/// </summary>
/// <remarks>
/// <para>Each query reads a fixed, small set of tiles: the 3×3 block of zoom-13 tiles around the position (settlements,
/// watercourses, lakes, peaks, reserves, built-up land) and the 2×2 zoom-14 tiles nearest it (landmarks, which the
/// tiles hold only from zoom 14). Moving the pin within those tiles reads nothing new.</para>
/// <para>What the tiles do not hold is not invented: only named features are described, and nothing beyond the block
/// is claimed. Vector tiles are generalised for display; their geometry is good to roughly a display pixel, which the
/// features carry as <see cref="GeographicFeature.DetailMetres"/>.</para>
/// </remarks>
public sealed class OpenFreeMapGeographicFeatureSource(VectorTileRepository tiles) : IGeographicFeatureSource
{
    public const string SourceId = "openfreemap-openmaptiles";
    public const string AttributionText = "Place data: © OpenStreetMap contributors, via OpenMapTiles and OpenFreeMap";
    public const int ContextZoom = 13;
    public const int LandmarkZoom = 14;
    /// <summary>Evidence polygons (built-up land, water) are only kept this close to the position.</summary>
    private const double EvidenceRadiusMetres = 250;

    public async Task<GeographicFeatureSet> GetFeaturesAsync(GeoCoordinate centre, CancellationToken cancellationToken)
    {
        var context = Block(centre, ContextZoom, 3);
        var landmarks = Block(centre, LandmarkZoom, 2);
        var loaded = await LoadAsync(context.Concat(landmarks).ToArray(), cancellationToken).ConfigureAwait(false);
        var contextTiles = loaded.Where(tile => tile.Key.Zoom == ContextZoom).ToArray();
        var landmarkTiles = loaded.Where(tile => tile.Key.Zoom == LandmarkZoom).ToArray();
        if (contextTiles.All(tile => tile.Tile is null))
            return GeographicFeatureSet.Unavailable(AttributionText);
        var language = CultureInfo.CurrentUICulture.Name;
        var features = new List<GeographicFeature>();
        var observer = World(centre, ContextZoom);
        foreach (var (key, tile) in contextTiles)
        {
            // Reading is synchronous work per tile; a caller that has given up (the time limit) stops it here.
            cancellationToken.ThrowIfCancellationRequested();
            if (tile is not null) ReadContext(new PlacedVectorTile(key.Zoom, key.RawColumn, key.Row, tile), centre, observer, language, features);
        }
        cancellationToken.ThrowIfCancellationRequested();
        features.AddRange(Watercourses(contextTiles, language));
        cancellationToken.ThrowIfCancellationRequested();
        var landmarkCoverage = CoverageRadius(centre, landmarkTiles, LandmarkZoom);
        foreach (var (key, tile) in landmarkTiles)
            if (tile is not null) ReadLandmarks(new PlacedVectorTile(key.Zoom, key.RawColumn, key.Row, tile), centre, language,
                landmarkCoverage, features);
        var coverage = loaded.Any(tile => tile.Tile is null) ? GeographicCoverage.Partial : GeographicCoverage.Complete;
        return new GeographicFeatureSet(features, coverage, CoverageRadius(centre, contextTiles, ContextZoom), AttributionText);
    }

    private readonly record struct TileKey(int Zoom, int RawColumn, int Row);

    private async Task<(TileKey Key, VectorTile? Tile)[]> LoadAsync(TileKey[] keys, CancellationToken cancellationToken)
    {
        var results = new (TileKey, VectorTile?)[keys.Length];
        await Parallel.ForAsync(0, keys.Length, new ParallelOptions
        {
            MaxDegreeOfParallelism = MapFrameVectorTiles.MaximumParallelRequests, CancellationToken = cancellationToken
        }, async (index, token) =>
        {
            var key = keys[index];
            var scale = 1 << key.Zoom;
            var column = ((key.RawColumn % scale) + scale) % scale;
            results[index] = (key, await tiles.GetAsync(key.Zoom, column, key.Row, token).ConfigureAwait(false));
        }).ConfigureAwait(false);
        return results;
    }

    /// <summary>A size×size block of tiles around the position: centred for odd sizes, towards the nearer edges for even.</summary>
    private static TileKey[] Block(GeoCoordinate centre, int zoom, int size)
    {
        var (x, y) = World(centre, zoom);
        var column = (int)Math.Floor(x / MapFrameVectorTiles.TileSize);
        var row = (int)Math.Floor(y / MapFrameVectorTiles.TileSize);
        var firstColumn = column - (size - 1) / 2;
        var firstRow = row - (size - 1) / 2;
        if (size % 2 == 0)
        {
            if (x / MapFrameVectorTiles.TileSize - column < .5) firstColumn--;
            if (y / MapFrameVectorTiles.TileSize - row < .5) firstRow--;
        }
        var scale = 1 << zoom;
        var keys = new List<TileKey>();
        for (var r = firstRow; r < firstRow + size; r++)
        for (var c = firstColumn; c < firstColumn + size; c++)
            if (r >= 0 && r < scale) keys.Add(new TileKey(zoom, c, r));
        return [.. keys];
    }

    /// <summary>How far the available tiles reach from the position in every direction, in metres.</summary>
    private static double CoverageRadius(GeoCoordinate centre, (TileKey Key, VectorTile? Tile)[] block, int zoom)
    {
        if (block.Length == 0) return 0;
        var (x, y) = World(centre, zoom);
        var metresPerPixel = MetresPerPixel(centre.Latitude, zoom);
        var size = MapFrameVectorTiles.TileSize;
        var minColumn = block.Min(t => t.Key.RawColumn) * size;
        var maxColumn = (block.Max(t => t.Key.RawColumn) + 1) * size;
        var minRow = block.Min(t => t.Key.Row) * size;
        var maxRow = (block.Max(t => t.Key.Row) + 1) * size;
        var reach = new[] { x - minColumn, maxColumn - x, y - minRow, maxRow - y }.Min();
        // A tile that could not be read is a hole: nothing beyond its nearest edge is complete.
        foreach (var (key, tile) in block.Where(t => t.Tile is null))
        {
            var dx = Math.Max(Math.Max(key.RawColumn * size - x, 0), x - (key.RawColumn + 1) * size);
            var dy = Math.Max(Math.Max(key.Row * size - y, 0), y - (key.Row + 1) * size);
            reach = Math.Min(reach, Math.Sqrt(dx * dx + dy * dy));
        }
        return Math.Max(0, reach) * metresPerPixel;
    }

    private static double MetresPerPixel(double latitude, int zoom) =>
        40_075_016.686 * Math.Cos(latitude * Math.PI / 180) / (MapFrameVectorTiles.TileSize * Math.Pow(2, zoom));

    private static (double X, double Y) World(GeoCoordinate coordinate, int zoom)
    {
        var size = MapFrameVectorTiles.TileSize * Math.Pow(2, zoom);
        var latitude = Math.Clamp(coordinate.Latitude, -WebMercatorViewport.MaximumLatitude, WebMercatorViewport.MaximumLatitude);
        var x = (coordinate.Longitude + 180) / 360 * size;
        var y = (1 - Math.Asinh(Math.Tan(latitude * Math.PI / 180)) / Math.PI) / 2 * size;
        return (x, y);
    }

    private static GeographicPoint Point(PlacedVectorTile tile, int extent, double x, double y)
    {
        var (worldX, worldY) = MapFrameVectorTiles.WorldPixel(tile, extent, x, y);
        var coordinate = MapFrameVectorTiles.Unproject(worldX, worldY, tile.Zoom);
        return new GeographicPoint(coordinate.Latitude, coordinate.Longitude);
    }

    private static GeographicPoint[][] Rings(PlacedVectorTile tile, VectorTileFeature feature) =>
        feature.Parts.Where(ring => ring.Length >= 3)
            .Select(ring => ring.Select(p => Point(tile, feature.Extent, p.X, p.Y)).ToArray()).ToArray();

    private static string Id(PlacedVectorTile tile, string layer, int index) =>
        FormattableString.Invariant($"omt/{tile.Zoom}/{tile.RawColumn}/{tile.Row}/{layer}/{index}");

    private static double Detail(PlacedVectorTile tile, GeoCoordinate centre) => MetresPerPixel(centre.Latitude, tile.Zoom);

    private static void ReadContext(PlacedVectorTile tile, GeoCoordinate centre, (double X, double Y) observer,
        string language, List<GeographicFeature> into)
    {
        var detail = Detail(tile, centre);
        var places = tile.Tile["place"]?.Features ?? [];
        for (var i = 0; i < places.Count; i++)
        {
            var feature = places[i];
            GeographicFeatureKind? kind = feature.Text("class") switch
            {
                "city" => GeographicFeatureKind.City,
                "town" => GeographicFeatureKind.Town,
                "village" => GeographicFeatureKind.Village,
                "hamlet" => GeographicFeatureKind.Hamlet,
                "suburb" or "quarter" => GeographicFeatureKind.Suburb,
                "neighbourhood" => GeographicFeatureKind.Neighbourhood,
                _ => null
            };
            if (kind is null || feature.Type != VectorTileGeometryType.Point || feature.Parts.Count == 0) continue;
            var point = feature.Parts[0][0];
            // Labels are repeated in each tile's buffer; the tile that contains the point owns it.
            if (!InsideTile(point, feature.Extent)) continue;
            into.Add(new GeographicFeature(Id(tile, "place", i), BasemapLabelNames.Resolve(feature, language), kind.Value,
                GeographicGeometryKind.Point, [[Point(tile, feature.Extent, point.X, point.Y)]], SourceId, detail));
        }

        var peaks = tile.Tile["mountain_peak"]?.Features ?? [];
        for (var i = 0; i < peaks.Count; i++)
        {
            var feature = peaks[i];
            var name = BasemapLabelNames.Resolve(feature, language);
            if (name is null || feature.Parts.Count == 0) continue;
            if (feature.Text("class") == "peak" && feature.Type == VectorTileGeometryType.Point && InsideTile(feature.Parts[0][0], feature.Extent))
                into.Add(new GeographicFeature(Id(tile, "peak", i), name, GeographicFeatureKind.Peak, GeographicGeometryKind.Point,
                    [[Point(tile, feature.Extent, feature.Parts[0][0].X, feature.Parts[0][0].Y)]], SourceId, detail));
            else if (feature.Text("class") == "ridge" && feature.Type == VectorTileGeometryType.LineString)
                into.Add(new GeographicFeature(Id(tile, "ridge", i), name, GeographicFeatureKind.Ridge, GeographicGeometryKind.Line,
                    feature.Parts.SelectMany(part => MapFrameVectorTiles.ClipToTile(part, feature.Extent))
                        .Select(run => run.Select(p => Point(tile, feature.Extent, p.X, p.Y)).ToArray()).ToArray(),
                    SourceId, detail));
        }

        var parks = tile.Tile["park"]?.Features ?? [];
        for (var i = 0; i < parks.Count; i++)
        {
            var feature = parks[i];
            var name = BasemapLabelNames.Resolve(feature, language);
            if (name is null || feature.Type != VectorTileGeometryType.Polygon) continue;
            // A reserve cut at the tile edge is larger than this tile shows: it counts as a large area.
            var clipped = feature.Parts.Any(ring => ring.Any(p => p.X <= 0 || p.Y <= 0 || p.X >= feature.Extent || p.Y >= feature.Extent));
            into.Add(new GeographicFeature(Id(tile, "park", i), name, GeographicFeatureKind.ProtectedArea, GeographicGeometryKind.Area,
                Rings(tile, feature), SourceId, detail, clipped ? .5 : 1));
        }

        // Lakes: the name is a label point; the shape is the water polygon that contains it.
        var water = tile.Tile["water"]?.Features ?? [];
        var waterNames = tile.Tile["water_name"]?.Features ?? [];
        for (var i = 0; i < waterNames.Count; i++)
        {
            var label = waterNames[i];
            var name = BasemapLabelNames.Resolve(label, language);
            if (name is null || label.Type != VectorTileGeometryType.Point || label.Parts.Count == 0 ||
                label.Text("class") is not ("lake" or "reservoir" or "pond")) continue;
            var at = label.Parts[0][0];
            var shape = water.FirstOrDefault(polygon => polygon.Type == VectorTileGeometryType.Polygon && polygon.Extent == label.Extent &&
                ContainsTilePoint(polygon, at));
            if (shape is null) continue;
            into.Add(new GeographicFeature(Id(tile, "lake", i), name, GeographicFeatureKind.Lake, GeographicGeometryKind.Area,
                Rings(tile, shape), SourceId, detail));
        }

        // Anonymous evidence near the position: built-up land (containment) and open water (banks).
        var nearPixels = EvidenceRadiusMetres / Detail(tile, centre);
        foreach (var (layer, kind, include) in new (string, GeographicFeatureKind, Func<VectorTileFeature, bool>)[]
                 {
                     ("landuse", GeographicFeatureKind.BuiltUpArea, f => f.Text("class") is "residential" or "suburb" or
                         "quarter" or "neighbourhood" or "commercial" or "industrial" or "retail"),
                     ("building", GeographicFeatureKind.BuiltUpArea, _ => true),
                     ("water", GeographicFeatureKind.WaterArea, f => f.Text("brunnel") != "tunnel")
                 })
        {
            var features = tile.Tile[layer]?.Features ?? [];
            for (var i = 0; i < features.Count; i++)
            {
                var feature = features[i];
                if (feature.Type != VectorTileGeometryType.Polygon || !include(feature) ||
                    !NearObserver(tile, feature, observer, nearPixels)) continue;
                into.Add(new GeographicFeature(Id(tile, layer, i), null, kind, GeographicGeometryKind.Area, Rings(tile, feature),
                    SourceId, detail));
            }
        }
    }

    private static void ReadLandmarks(PlacedVectorTile tile, GeoCoordinate centre, string language, double coverageMetres,
        List<GeographicFeature> into)
    {
        var detail = Detail(tile, centre);
        var pois = tile.Tile["poi"]?.Features ?? [];
        for (var i = 0; i < pois.Count; i++)
        {
            var feature = pois[i];
            (GeographicFeatureKind Kind, double Prominence)? kind = feature.Text("class") switch
            {
                "castle" => (GeographicFeatureKind.Landmark, 1),
                "monument" or "attraction" or "museum" => (GeographicFeatureKind.Landmark, .75),
                "place_of_worship" => (GeographicFeatureKind.PlaceOfWorship, 1),
                _ => null
            };
            var name = BasemapLabelNames.Resolve(feature, language);
            if (kind is null || name is null || feature.Type != VectorTileGeometryType.Point || feature.Parts.Count == 0 ||
                !InsideTile(feature.Parts[0][0], feature.Extent)) continue;
            var point = Point(tile, feature.Extent, feature.Parts[0][0].X, feature.Parts[0][0].Y);
            // Landmarks are only held close in: none is described from beyond the landmark tiles' reach.
            if (GeographicSpatialAnalysis.Project(centre, point).Length > coverageMetres) continue;
            into.Add(new GeographicFeature(Id(tile, "poi", i), name, kind.Value.Kind, GeographicGeometryKind.Point, [[point]],
                SourceId, detail, kind.Value.Prominence));
        }
    }

    /// <summary>Named rivers, canals and streams across the block, clipped per tile and joined across tile edges.</summary>
    private static IEnumerable<GeographicFeature> Watercourses((TileKey Key, VectorTile? Tile)[] block, string language)
    {
        var runs = new SortedDictionary<(GeographicFeatureKind, string), List<(double X, double Y)[]>>();
        foreach (var (key, tile) in block)
        {
            if (tile is null) continue;
            var placed = new PlacedVectorTile(key.Zoom, key.RawColumn, key.Row, tile);
            foreach (var feature in tile["waterway"]?.Features ?? [])
            {
                GeographicFeatureKind? kind = feature.Text("class") switch
                {
                    "river" => GeographicFeatureKind.River,
                    "canal" => GeographicFeatureKind.Canal,
                    "stream" => GeographicFeatureKind.Stream,
                    _ => null
                };
                var name = BasemapLabelNames.Resolve(feature, language);
                if (kind is null || name is null || feature.Type != VectorTileGeometryType.LineString) continue;
                if (!runs.TryGetValue((kind.Value, name), out var list)) runs[(kind.Value, name)] = list = [];
                foreach (var part in feature.Parts)
                foreach (var run in MapFrameVectorTiles.ClipToTile(part, feature.Extent))
                    list.Add(run.Select(p => MapFrameVectorTiles.WorldPixel(placed, feature.Extent, p.X, p.Y)).ToArray());
            }
        }
        foreach (var ((kind, name), list) in runs)
        {
            var lines = MapFrameVectorTiles.Stitch(list, .25);
            var parts = lines.Select(line => line.Select(p =>
            {
                var coordinate = MapFrameVectorTiles.Unproject(p.X, p.Y, ContextZoom);
                return new GeographicPoint(coordinate.Latitude, coordinate.Longitude);
            }).ToArray()).ToArray();
            var latitude = parts.Length > 0 && parts[0].Length > 0 ? parts[0][0].Latitude : 0;
            yield return new GeographicFeature(FormattableString.Invariant($"omt/{ContextZoom}/waterway/{kind}/{name}"), name, kind,
                GeographicGeometryKind.Line, parts, SourceId, MetresPerPixel(latitude, ContextZoom));
        }
    }

    private static bool InsideTile(VectorTilePoint point, int extent) =>
        point.X >= 0 && point.Y >= 0 && point.X < extent && point.Y < extent;

    private static bool ContainsTilePoint(VectorTileFeature polygon, VectorTilePoint point)
    {
        var inside = false;
        foreach (var ring in polygon.Parts)
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
                if ((ring[i].Y > point.Y) != (ring[j].Y > point.Y) &&
                    point.X < (double)(ring[j].X - ring[i].X) * (point.Y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X)
                    inside = !inside;
        return inside;
    }

    private static bool NearObserver(PlacedVectorTile tile, VectorTileFeature feature, (double X, double Y) observer,
        double radiusPixels)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var ring in feature.Parts)
        foreach (var p in ring)
        {
            var (x, y) = MapFrameVectorTiles.WorldPixel(tile, feature.Extent, p.X, p.Y);
            minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        return observer.X >= minX - radiusPixels && observer.X <= maxX + radiusPixels &&
               observer.Y >= minY - radiusPixels && observer.Y <= maxY + radiusPixels;
    }
}
