using BruTile;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Mapping.Vector;
using SkiaSharp;

namespace Noctaxis.Desktop.Export;

/// <summary>
/// The sheet's clean map: the Planner's own themed vector basemap (roads, water, land and place names, in the
/// user's theme and language) and nothing laid over it — no camera cone, rays, terrain, weather or light
/// pollution. It is composed from the basemap's tiles around the planning location, so it does not depend on
/// where the Planner map happens to be panned.
/// </summary>
public static class ExportMapComposer
{
    /// <summary>About 25 km across the sheet's map at mid latitudes: the location and its surroundings.</summary>
    public const int Zoom = 12;
    public const int PixelRatio = 2;
    private const int TileSize = BasemapTileRenderer.LogicalTileSize;

    /// <summary>The credits the basemap owes, worded as the Planner map words them.</summary>
    public static string Credits { get; } = Mapping.MapAttributions.Line(
        [Mapping.MapAttributions.OpenStreetMap, Mapping.MapAttributions.OpenFreeMap, Mapping.MapAttributions.OpenMapTiles]);

    /// <summary>
    /// The map, <paramref name="width"/> × <paramref name="height"/> sheet pixels, centred on
    /// <paramref name="centre"/>; null when no tile could be drawn (offline, or the source is unavailable).
    /// </summary>
    public static async Task<ExportMapImage?> ComposeAsync(ThemedVectorTileSource source, GeoCoordinate centre, int width, int height,
        CancellationToken cancellationToken)
    {
        var scale = 1 << Zoom;
        var latitude = Math.Clamp(centre.Latitude, -85.05112878, 85.05112878) * Math.PI / 180;
        var worldX = (centre.Longitude + 180) / 360 * scale * TileSize;
        var worldY = (1 - Math.Log(Math.Tan(latitude) + 1 / Math.Cos(latitude)) / Math.PI) / 2 * scale * TileSize;
        var left = worldX - width / 2.0;
        var top = worldY - height / 2.0;
        var tiles = new List<(int Column, int Row, double X, double Y)>();
        for (var row = (int)Math.Floor(top / TileSize); row <= (int)Math.Floor((top + height) / TileSize); row++)
        {
            if (row < 0 || row >= scale) continue;
            for (var column = (int)Math.Floor(left / TileSize); column <= (int)Math.Floor((left + width) / TileSize); column++)
                tiles.Add((((column % scale) + scale) % scale, row, column * TileSize - left, row * TileSize - top));
        }

        var rendered = await Task.WhenAll(tiles.Select(async tile =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (bytes, _, _) = await source.RenderAsync(new TileInfo { Index = new TileIndex(tile.Column, tile.Row, Zoom) })
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return (tile, bytes);
        })).ConfigureAwait(false);
        if (rendered.All(item => item.bytes is null)) return null;

        using var bitmap = new SKBitmap(width * PixelRatio, height * PixelRatio);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { IsAntialias = true };
            foreach (var (tile, bytes) in rendered)
            {
                if (bytes is null) continue;
                using var image = SKBitmap.Decode(bytes);
                if (image is null) continue;
                var destination = SKRect.Create((float)(tile.X * PixelRatio), (float)(tile.Y * PixelRatio), TileSize * PixelRatio, TileSize * PixelRatio);
                canvas.DrawBitmap(image, destination, paint);
            }
        }
        using var composed = SKImage.FromBitmap(bitmap);
        using var encoded = composed.Encode(SKEncodedImageFormat.Png, 100);
        return new ExportMapImage(encoded.ToArray(), PixelRatio, Credits);
    }
}
