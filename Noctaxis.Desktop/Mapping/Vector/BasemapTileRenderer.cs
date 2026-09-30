using SkiaSharp;

namespace Noctaxis.Desktop.Mapping.Vector;

/// <summary>
/// One displayed web-mercator tile and the vector tile that covers it. Vector data stops at
/// <see cref="BasemapTileRenderer.MaximumSourceZoom"/>; deeper zooms draw an enlarged part of it.
/// </summary>
public readonly record struct BasemapTileRequest(int Zoom, int Column, int Row)
{
    public int SourceZoom => Math.Min(Zoom, BasemapTileRenderer.MaximumSourceZoom);
    public int SubScale => 1 << (Zoom - SourceZoom);
    public int SourceColumn => Column >> (Zoom - SourceZoom);
    public int SourceRow => Row >> (Zoom - SourceZoom);
    public int SubColumn => Column - SourceColumn * SubScale;
    public int SubRow => Row - SourceRow * SubScale;
}

/// <summary>
/// Which part of the basemap a tile holds. The Planner draws <see cref="Geography"/> at the bottom
/// of the map and <see cref="Reference"/> above its environmental layers (terrain relief, light
/// pollution), so those change the look of the land without covering roads, boundaries or names.
/// Together the two passes draw exactly what <see cref="Full"/> draws, each feature once.
/// </summary>
public enum BasemapPass
{
    /// <summary>Everything, on an opaque background: for a map with no environmental layers.</summary>
    Full,
    /// <summary>Land, land cover, water and buildings, on an opaque background.</summary>
    Geography,
    /// <summary>Boundaries, roads, railways and labels, on a transparent background.</summary>
    Reference
}

/// <summary>
/// Draws OpenMapTiles-schema vector tiles (the OpenFreeMap schema) with a <see cref="BasemapStyle"/>.
/// Only semantic roles reach this class; it knows nothing about theme families. Labels are placed
/// only where they fit entirely inside the tile, so no label is ever cut at a tile edge.
/// </summary>
public sealed class BasemapTileRenderer
{
    public const int MaximumSourceZoom = 14;
    public const int LogicalTileSize = 256;
    private static readonly SKTypeface Regular = SKTypeface.FromFamilyName(null, SKFontStyle.Normal) ?? SKTypeface.Default;
    private static readonly SKTypeface Bold = SKTypeface.FromFamilyName(null, SKFontStyle.Bold) ?? SKTypeface.Default;
    private static readonly SKTypeface Italic = SKTypeface.FromFamilyName(null, SKFontStyle.Italic) ?? SKTypeface.Default;

    public BasemapTileRenderer(int pixelRatio = 2) => PixelRatio = Math.Clamp(pixelRatio, 1, 4);

    public int PixelRatio { get; }
    public int PixelSize => LogicalTileSize * PixelRatio;

    public byte[] RenderPng(VectorTile tile, BasemapTileRequest request, BasemapStyle style,
        BasemapPass pass = BasemapPass.Full)
    {
        using var bitmap = RenderBitmap(tile, request, style, pass);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public SKBitmap RenderBitmap(VectorTile tile, BasemapTileRequest request, BasemapStyle style,
        BasemapPass pass = BasemapPass.Full)
    {
        var bitmap = new SKBitmap(PixelSize, PixelSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        new Pass(this, canvas, tile, request, style).Draw(pass);
        return bitmap;
    }

    private sealed class Pass(BasemapTileRenderer owner, SKCanvas canvas, VectorTile tile,
        BasemapTileRequest request, BasemapStyle style)
    {
        private readonly float _ratio = owner.PixelRatio;
        private readonly float _size = owner.PixelSize;
        private readonly int _zoom = request.Zoom;
        private readonly List<SKRect> _placed = [];

        public void Draw(BasemapPass pass)
        {
            if (pass == BasemapPass.Reference) canvas.Clear(SKColors.Transparent);
            else
            {
                canvas.Clear(style.Land);
                DrawAreas();
                DrawWater();
                // Buildings are area fills: drawn above the environmental layers they would hide
                // them across every town, so they belong to the geography.
                if (_zoom >= 13) Fill("building", _ => true, style.Building, 1);
                if (pass == BasemapPass.Geography) return;
            }
            DrawBoundaries();
            DrawTransportation();
            DrawLabels();
        }

        private void DrawAreas()
        {
            Fill("landcover", f => f.Text("class") is "wood" or "forest", style.Vegetation, 1);
            Fill("landcover", f => f.Text("class") is "grass", style.Vegetation, .5f);
            Fill("landcover", f => f.Text("class") is "wetland", style.Water, .3f);
            Fill("park", _ => true, style.Vegetation, .35f);
            Fill("landuse", f => f.Text("class") is "residential" or "suburb" or "quarter" or "neighbourhood",
                style.BuiltUp, 1);
            Fill("landuse", f => f.Text("class") is "commercial" or "industrial" or "retail" or "railway" or
                "hospital" or "school" or "university" or "college" or "military" or "stadium" or "garages",
                style.BuiltUp, .8f);
            Fill("landuse", f => f.Text("class") is "cemetery" or "pitch" or "playground", style.Vegetation, .5f);
            Fill("aeroway", f => f.Type == VectorTileGeometryType.Polygon, style.BuiltUp, .8f);
        }

        private void DrawWater()
        {
            Fill("water", f => f.Text("brunnel") != "tunnel" && f.Number("intermittent") != 1, style.Water, 1);
            Fill("water", f => f.Number("intermittent") == 1, style.Water, .6f);
            foreach (var feature in Features("waterway"))
            {
                var kind = feature.Text("class");
                var width = kind switch
                {
                    "river" or "canal" => Interpolate([(8, .6), (12, 1.4), (14, 2.4), (18, 6)]),
                    "stream" when _zoom >= 13 => Interpolate([(13, .5), (16, 1.2), (18, 2)]),
                    _ => 0
                };
                if (width > 0) Stroke(feature, style.Water, width, feature.Text("brunnel") == "tunnel" ? .5f : 1);
            }
        }

        private void DrawBoundaries()
        {
            foreach (var feature in Features("boundary"))
            {
                if (feature.Number("maritime") == 1 || feature.Number("admin_level") is not double level || level > 4)
                    continue;
                var country = level <= 2;
                var dash = country ? new[] { 6f, 3f } : [3f, 3f];
                Stroke(feature, style.Boundary, country ? 1.3 : .8, .9f, dash);
            }
        }

        private enum RoadRank { None, Path, Service, Minor, Tertiary, Secondary, Primary, Major }

        private static RoadRank Rank(VectorTileFeature feature) => feature.Text("class") switch
        {
            "motorway" or "trunk" => RoadRank.Major,
            "primary" => RoadRank.Primary,
            "secondary" => RoadRank.Secondary,
            "tertiary" => RoadRank.Tertiary,
            "minor" or "busway" => RoadRank.Minor,
            "service" => RoadRank.Service,
            "track" or "path" => RoadRank.Path,
            _ => RoadRank.None
        };

        private double RoadWidth(RoadRank rank) => rank switch
        {
            RoadRank.Major or RoadRank.Primary => Interpolate([(5, .6), (8, 1), (10, 1.6), (12, 2.4), (14, 4), (16, 7), (18, 12)]),
            RoadRank.Secondary when _zoom >= 8 => .8 * Interpolate([(8, 1), (10, 1.6), (12, 2.4), (14, 4), (16, 7), (18, 12)]),
            RoadRank.Tertiary when _zoom >= 10 => 1.2 * Interpolate([(10, .6), (12, .8), (14, 1.8), (16, 4), (18, 8)]),
            RoadRank.Minor when _zoom >= 12 => Interpolate([(12, .8), (14, 1.8), (16, 4), (18, 8)]),
            RoadRank.Service when _zoom >= 14 => .6 * Interpolate([(14, 1.8), (16, 4), (18, 8)]),
            RoadRank.Path when _zoom >= 14 => Interpolate([(14, .6), (18, 1.4)]),
            _ => 0
        };

        private bool HasCasing(RoadRank rank) => rank switch
        {
            RoadRank.Major or RoadRank.Primary or RoadRank.Secondary => _zoom >= 10,
            RoadRank.Tertiary or RoadRank.Minor => _zoom >= 13,
            RoadRank.Service => _zoom >= 15,
            _ => false
        };

        private void DrawTransportation()
        {
            var roads = new List<(VectorTileFeature Feature, RoadRank Rank, double Width, float Alpha)>();
            foreach (var feature in Features("transportation"))
            {
                var kind = feature.Text("class") ?? "";
                if (kind.EndsWith("_construction", StringComparison.Ordinal)) continue;
                var alpha = feature.Text("brunnel") == "tunnel" ? .55f : 1f;
                if (kind is "rail" or "transit")
                {
                    if (_zoom >= 10)
                        Stroke(feature, style.Rail, Interpolate([(10, .8), (14, 1.4), (18, 2.4)]), alpha, [4f, 3f],
                            SKStrokeCap.Butt);
                    continue;
                }
                var rank = Rank(feature);
                var width = RoadWidth(rank);
                if (width > 0) roads.Add((feature, rank, width, alpha));
            }
            // Casings first, then fills from least to most important, so junctions read correctly.
            foreach (var road in roads.Where(road => HasCasing(road.Rank)))
                Stroke(road.Feature, style.RoadCasing, road.Width + 1.6, road.Alpha);
            // Only the trunk network carries the major-road colour, so the theme hue marks the
            // hierarchy instead of flooding the map; primary roads lean towards minor, the rest are minor.
            var primary = Mix(style.RoadMinor, style.RoadMajor, .35f);
            foreach (var road in roads.OrderBy(road => road.Rank))
            {
                var colour = road.Rank switch
                {
                    RoadRank.Major => style.RoadMajor,
                    RoadRank.Primary => primary,
                    _ => style.RoadMinor
                };
                Stroke(road.Feature, colour, road.Width, road.Alpha * (road.Rank == RoadRank.Path ? .8f : 1),
                    road.Rank == RoadRank.Path ? [2f, 2f] : null);
            }
        }

        private void DrawLabels()
        {
            var candidates = new List<(int Priority, double Rank, VectorTileFeature Feature, LabelKind Kind)>();
            foreach (var feature in Features("place"))
            {
                var kind = feature.Text("class") switch
                {
                    "country" when _zoom is >= 2 and <= 7 => LabelKind.Country,
                    "state" or "province" when _zoom is >= 5 and <= 8 => LabelKind.State,
                    "city" when _zoom >= 4 => LabelKind.City,
                    "town" when _zoom >= 8 => LabelKind.Town,
                    "village" when _zoom >= 11 => LabelKind.Village,
                    "island" when _zoom >= 10 => LabelKind.Island,
                    "hamlet" or "suburb" or "quarter" or "neighbourhood" when _zoom >= 13 => LabelKind.Locality,
                    _ => LabelKind.None
                };
                if (kind != LabelKind.None) candidates.Add(((int)kind, feature.Number("rank") ?? 99, feature, kind));
            }
            foreach (var feature in Features("water_name"))
                candidates.Add(((int)LabelKind.Water, feature.Number("rank") ?? 99, feature, LabelKind.Water));
            if (_zoom >= 12)
                foreach (var feature in Features("mountain_peak"))
                    candidates.Add(((int)LabelKind.Peak, feature.Number("rank") ?? 99, feature, LabelKind.Peak));
            // Points of interest only close in, and only the most notable: they are context, not the plan.
            if (_zoom >= 16)
                foreach (var feature in Features("poi").Where(f => (f.Number("rank") ?? 99) <= 12))
                    candidates.Add(((int)LabelKind.Poi, feature.Number("rank") ?? 99, feature, LabelKind.Poi));

            var names = new HashSet<string>(StringComparer.Ordinal);
            var pois = 0;
            foreach (var (_, _, feature, kind) in candidates.OrderBy(c => c.Priority).ThenBy(c => c.Rank))
            {
                if (kind == LabelKind.Poi && pois >= 12) continue;
                // One label per name per tile: stations and landmarks appear as several points.
                var name = LabelText(feature);
                if (name is null || !names.Add(name)) continue;
                if (PlaceLabel(feature, kind, name) && kind == LabelKind.Poi) pois++;
            }
        }

        private static string? LabelText(VectorTileFeature feature) =>
            (feature.Text("name:latin") ?? feature.Text("name")) is { } name && !string.IsNullOrWhiteSpace(name) ? name : null;

        private static SKColor Mix(SKColor from, SKColor to, float amount)
        {
            byte Channel(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
            return new SKColor(Channel(from.Red, to.Red), Channel(from.Green, to.Green),
                Channel(from.Blue, to.Blue), Channel(from.Alpha, to.Alpha));
        }

        private enum LabelKind { None, Country, State, City, Town, Water, Island, Village, Peak, Locality, Poi }

        private bool PlaceLabel(VectorTileFeature feature, LabelKind kind, string name)
        {
            if (feature.Type != VectorTileGeometryType.Point || feature.Parts.Count == 0) return false;
            var anchor = Project(feature, feature.Parts[0][0]);
            var (size, typeface) = kind switch
            {
                LabelKind.Country => (12.5, Bold), LabelKind.State => (11, Italic),
                LabelKind.City => (_zoom >= 10 ? 15 : 13, Bold), LabelKind.Town => (12, Regular),
                LabelKind.Village => (11, Regular), LabelKind.Water => (11, Italic), LabelKind.Island => (10.5, Italic),
                _ => (10, Regular)
            };
            using var font = new SKFont(typeface, (float)(size * _ratio)) { Edging = SKFontEdging.Antialias, Subpixel = true };
            var width = font.MeasureText(name);
            var marker = kind is LabelKind.Poi or LabelKind.Peak;
            var textX = marker ? anchor.X + 5 * _ratio : anchor.X - width / 2;
            var baseline = anchor.Y + font.Size * .35f;
            var bounds = new SKRect(marker ? anchor.X - 4 * _ratio : textX, baseline - font.Size,
                textX + width, baseline + font.Size * .3f);
            var inset = 2 * _ratio;
            if (bounds.Left < inset || bounds.Top < inset || bounds.Right > _size - inset || bounds.Bottom > _size - inset)
                return false;
            var padded = SKRect.Inflate(bounds, 3 * _ratio, 3 * _ratio);
            if (_placed.Any(placed => placed.IntersectsWith(padded))) return false;
            _placed.Add(padded);

            var textColour = kind == LabelKind.Water ? style.WaterLabel : style.Label;
            if (marker)
            {
                using var markerPaint = new SKPaint { IsAntialias = true, Color = style.Poi };
                using var markerHalo = new SKPaint { IsAntialias = true, Color = style.LabelHalo, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * _ratio };
                if (kind == LabelKind.Peak)
                {
                    using var path = new SKPath();
                    var r = 3.5f * _ratio;
                    path.MoveTo(anchor.X, anchor.Y - r);
                    path.LineTo(anchor.X + r, anchor.Y + r * .8f);
                    path.LineTo(anchor.X - r, anchor.Y + r * .8f);
                    path.Close();
                    canvas.DrawPath(path, markerHalo);
                    canvas.DrawPath(path, markerPaint);
                }
                else
                {
                    canvas.DrawCircle(anchor, 2.5f * _ratio, markerHalo);
                    canvas.DrawCircle(anchor, 2.5f * _ratio, markerPaint);
                }
            }
            using var halo = new SKPaint
            {
                IsAntialias = true, Color = style.LabelHalo, Style = SKPaintStyle.Stroke,
                StrokeWidth = 2.6f * _ratio, StrokeJoin = SKStrokeJoin.Round
            };
            using var fill = new SKPaint { IsAntialias = true, Color = textColour };
            canvas.DrawText(name, textX, baseline, SKTextAlign.Left, font, halo);
            canvas.DrawText(name, textX, baseline, SKTextAlign.Left, font, fill);
            return true;
        }

        private IEnumerable<VectorTileFeature> Features(string layer) =>
            tile[layer]?.Features ?? (IEnumerable<VectorTileFeature>)[];

        private void Fill(string layerName, Func<VectorTileFeature, bool> predicate, SKColor colour, float alpha)
        {
            if (tile[layerName] is not { } layer) return;
            using var paint = new SKPaint { IsAntialias = true, Color = colour.WithAlpha((byte)(colour.Alpha * alpha)) };
            foreach (var feature in layer.Features)
            {
                if (feature.Type != VectorTileGeometryType.Polygon || !predicate(feature)) continue;
                using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
                foreach (var ring in feature.Parts)
                {
                    if (ring.Length < 3) continue;
                    path.MoveTo(Project(feature, ring[0]));
                    for (var index = 1; index < ring.Length; index++) path.LineTo(Project(feature, ring[index]));
                    path.Close();
                }
                canvas.DrawPath(path, paint);
            }
        }

        private void Stroke(VectorTileFeature feature, SKColor colour, double logicalWidth, float alpha,
            float[]? dash = null, SKStrokeCap cap = SKStrokeCap.Round)
        {
            if (feature.Type != VectorTileGeometryType.LineString && feature.Type != VectorTileGeometryType.Polygon) return;
            using var paint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(logicalWidth * _ratio),
                StrokeCap = cap, StrokeJoin = SKStrokeJoin.Round, Color = colour.WithAlpha((byte)(colour.Alpha * alpha))
            };
            using var effect = dash is null ? null : SKPathEffect.CreateDash(dash.Select(d => d * _ratio).ToArray(), 0);
            paint.PathEffect = effect;
            using var path = new SKPath();
            foreach (var part in feature.Parts)
            {
                if (part.Length < 2) continue;
                path.MoveTo(Project(feature, part[0]));
                for (var index = 1; index < part.Length; index++) path.LineTo(Project(feature, part[index]));
            }
            canvas.DrawPath(path, paint);
        }

        private SKPoint Project(VectorTileFeature feature, VectorTilePoint point)
        {
            var scale = _size * request.SubScale / feature.Extent;
            return new SKPoint(point.X * scale - request.SubColumn * _size, point.Y * scale - request.SubRow * _size);
        }

        /// <summary>Width in logical pixels at this zoom, interpolated linearly between stops.</summary>
        private double Interpolate((double Zoom, double Width)[] stops)
        {
            if (_zoom <= stops[0].Zoom) return stops[0].Width;
            for (var index = 1; index < stops.Length; index++)
            {
                if (_zoom > stops[index].Zoom) continue;
                var (z0, w0) = stops[index - 1];
                var (z1, w1) = stops[index];
                return w0 + (w1 - w0) * (_zoom - z0) / (z1 - z0);
            }
            return stops[^1].Width;
        }
    }
}
