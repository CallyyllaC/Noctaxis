using System.Security.Cryptography;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Desktop.Services;
using SkiaSharp;

namespace Noctaxis.Desktop.Mapping.Preview;

public enum PreviewLineClass { Major, Minor, River, Stream }

public readonly record struct PreviewLine(PreviewLineClass Class, SKPoint[] Points);

/// <summary>A settlement light: position in scene pixels and relative brightness (0..1).</summary>
public readonly record struct PreviewLight(float X, float Y, float Intensity);

/// <summary>
/// Theme-independent content of a saved-location preview, built once per location from its cached
/// map assets. Coordinates are scene pixels of the source Web-Mercator viewport; area layers and the
/// settlement field are stored at half that resolution. <see cref="LocationPreviewRenderer"/> paints
/// it with any theme, so a theme change never rebuilds the scene.
/// </summary>
public sealed class LocationPreviewScene
{
    public const int MaskScale = 2;

    internal LocationPreviewScene(Guid locationId, int width, int height, SKPoint pin,
        byte[] water, byte[] vegetation, byte[] builtUp, byte[] settlement,
        IReadOnlyList<PreviewLine> lines, IReadOnlyList<PreviewLight> lights, string fingerprint)
    {
        LocationId = locationId; Width = width; Height = height; Pin = pin;
        Water = water; Vegetation = vegetation; BuiltUp = builtUp; Settlement = settlement;
        Lines = lines; Lights = lights; Fingerprint = fingerprint;
    }

    public Guid LocationId { get; }
    public int Width { get; }
    public int Height { get; }
    public int MaskWidth => Math.Max(1, Width / MaskScale);
    public int MaskHeight => Math.Max(1, Height / MaskScale);
    public SKPoint Pin { get; }
    /// <summary>Area coverage masks (0..255), MaskWidth × MaskHeight.</summary>
    public byte[] Water { get; }
    public byte[] Vegetation { get; }
    public byte[] BuiltUp { get; }
    /// <summary>Smoothed settlement density (0..255), MaskWidth × MaskHeight.</summary>
    public byte[] Settlement { get; }
    public IReadOnlyList<PreviewLine> Lines { get; }
    public IReadOnlyList<PreviewLight> Lights { get; }
    /// <summary>Stable content identity: equal inputs give an equal fingerprint and identical renders.</summary>
    public string Fingerprint { get; }
    public bool HasMapContent => Lines.Count > 0 || Water.Any(v => v > 0) || Vegetation.Any(v => v > 0) || BuiltUp.Any(v => v > 0);

    /// <summary>The settlement field widely blurred, computed once per scene so themed renders need no blur.</summary>
    public byte[] SettlementBloom
    {
        get { lock (_bloomGate) return _settlementBloom ??= Bloom(Settlement, MaskWidth, MaskHeight); }
    }
    private readonly object _bloomGate = new();
    private byte[]? _settlementBloom;

    /// <summary>Nebula, starfield and light bearing derived from this scene, computed once on first use.</summary>
    public LocationPreviewAtmosphere Atmosphere
    {
        get { lock (_atmosphereGate) return _atmosphere ??= LocationPreviewAtmosphere.Create(this); }
    }
    private readonly object _atmosphereGate = new();
    private LocationPreviewAtmosphere? _atmosphere;

    private static byte[] Bloom(byte[] field, int width, int height)
    {
        var blurred = SettlementDensityBuilder.GaussianBlur(field.Select(v => (float)v).ToArray(), width, height, 6);
        // Spreading lowers the peak; lift it back so a town still glows at its core.
        return blurred.Select(v => (byte)Math.Clamp(Math.Round(v * 1.7f), 0, 255)).ToArray();
    }

    /// <summary>A preview with no map data yet: the themed ground, reticle and pin only.</summary>
    public static LocationPreviewScene Empty(Guid locationId, int width = LocationPreviewSceneBuilder.DefaultWidth,
        int height = LocationPreviewSceneBuilder.DefaultHeight)
    {
        var count = Math.Max(1, width / MaskScale) * Math.Max(1, height / MaskScale);
        return new(locationId, width, height, new SKPoint(width / 2f, height / 2f),
            new byte[count], new byte[count], new byte[count], new byte[count], [], [], "empty:" + locationId.ToString("N"));
    }
}

/// <summary>Builds <see cref="LocationPreviewScene"/>s from saved-location map assets. Pure and deterministic.</summary>
public static class LocationPreviewSceneBuilder
{
    public const int DefaultWidth = 896;
    public const int DefaultHeight = 504;
    public const int MaximumLights = 420;

    // OpenStreetMap Standard land-use fills, for locations whose map data predates the vector landscape: their source
    // raster is always this style, so its areas can be read back as semantic coverage and repainted with the theme's
    // own map roles.
    private static readonly (byte R, byte G, byte B)[] WaterFills = [(170, 211, 223), (181, 208, 208)];
    private static readonly (byte R, byte G, byte B)[] VegetationFills =
        [(173, 209, 158), (205, 235, 176), (200, 250, 204), (214, 217, 159), (223, 240, 206), (174, 223, 163), (199, 229, 179)];
    private static readonly (byte R, byte G, byte B)[] BuiltUpFills =
        [(224, 223, 223), (217, 208, 201), (235, 219, 232), (242, 218, 217), (255, 214, 209), (197, 195, 195), (222, 208, 213)];

    public static LocationPreviewScene Build(Guid locationId, GeoCoordinate location, WebMercatorViewport viewport,
        byte[]? sourcePng, MapFeatureDataDocument? features, SettlementRaster? settlement) =>
        Build(locationId, location, viewport, sourcePng, null, features, settlement);

    /// <param name="landscape">Area coverage from vector polygons; when present the raster is not read.</param>
    /// <param name="legacySourcePng">An earlier OpenStreetMap Standard capture, classified by colour.</param>
    public static LocationPreviewScene Build(Guid locationId, GeoCoordinate location, WebMercatorViewport viewport,
        byte[]? legacySourcePng, SavedLocationLandscape? landscape, MapFeatureDataDocument? features,
        SettlementRaster? settlement)
    {
        var width = viewport.Width;
        var height = viewport.Height;
        var maskWidth = Math.Max(1, width / LocationPreviewScene.MaskScale);
        var maskHeight = Math.Max(1, height / LocationPreviewScene.MaskScale);
        var count = maskWidth * maskHeight;
        var water = new byte[count];
        var vegetation = new byte[count];
        var builtUp = new byte[count];
        if (landscape is not null && landscape.Width == maskWidth && landscape.Height == maskHeight)
            UseLandscape(landscape, maskWidth, maskHeight, water, vegetation, builtUp);
        else if (legacySourcePng is not null) ClassifyAreas(legacySourcePng, maskWidth, maskHeight, water, vegetation, builtUp);

        var lines = new List<PreviewLine>();
        if (features is not null)
        {
            foreach (var waterway in features.Waterways.OrderBy(w => w.Id))
                if (Project(waterway.Geometry, viewport) is { } points)
                    lines.Add(new(waterway.Classification == MapWaterwayClassification.Stream ? PreviewLineClass.Stream : PreviewLineClass.River, points));
            foreach (var road in features.Roads.OrderByDescending(r => r.Classification).ThenBy(r => r.Id))
                if (Project(road.Geometry, viewport) is { } points)
                    lines.Add(new(road.Classification is MapRoadClassification.Motorway or MapRoadClassification.ARoad
                        ? PreviewLineClass.Major : PreviewLineClass.Minor, points));
        }

        var settlementField = new byte[count];
        var lights = Array.Empty<PreviewLight>();
        if (settlement is not null && settlement.CellCount > 0)
        {
            var density = new SettlementDensityBuilder().Build(settlement, maskWidth, maskHeight, SettlementGalaxyStyle.DefaultV1).Density;
            for (var i = 0; i < count && i < density.Length; i++)
                settlementField[i] = (byte)Math.Round(Math.Clamp(density[i], 0, 1) * 255);
            lights = SampleLights(locationId, density, maskWidth, maskHeight);
        }

        var projectedPin = viewport.Project(location.Latitude, location.Longitude);
        var pin = new SKPoint((float)projectedPin.X, (float)projectedPin.Y);
        return new LocationPreviewScene(locationId, width, height, pin, water, vegetation, builtUp, settlementField,
            lines, lights, Fingerprint(locationId, width, height, pin, water, vegetation, builtUp, settlementField, lines, lights));
    }

    private static SKPoint[]? Project(MapFeatureCoordinate[] geometry, WebMercatorViewport viewport)
    {
        if (geometry.Length < 2) return null;
        var points = new SKPoint[geometry.Length];
        for (var i = 0; i < geometry.Length; i++)
        {
            var p = viewport.Project(geometry[i].Latitude, geometry[i].Longitude);
            points[i] = new SKPoint((float)p.X, (float)p.Y);
        }
        return points;
    }

    private static void UseLandscape(SavedLocationLandscape landscape, int maskWidth, int maskHeight, byte[] water,
        byte[] vegetation, byte[] builtUp)
    {
        landscape.Water.CopyTo(water, 0);
        landscape.Vegetation.CopyTo(vegetation, 0);
        landscape.BuiltUp.CopyTo(builtUp, 0);
        // The same softening the raster classification gets, so both sources read alike on the card.
        foreach (var mask in new[] { water, vegetation, builtUp }) Smooth(mask, maskWidth, maskHeight);
    }

    private static void ClassifyAreas(byte[] sourcePng, int maskWidth, int maskHeight, byte[] water, byte[] vegetation, byte[] builtUp)
    {
        using var decoded = SKBitmap.Decode(sourcePng);
        if (decoded is null) return;
        using var scaled = decoded.Resize(new SKImageInfo(maskWidth, maskHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul),
            new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        if (scaled is null) return;
        static bool Near((byte R, byte G, byte B)[] fills, SKColor c, int tolerance) =>
            fills.Any(f => Math.Abs(f.R - c.Red) + Math.Abs(f.G - c.Green) + Math.Abs(f.B - c.Blue) <= tolerance);
        var pixels = scaled.GetPixelSpan();
        for (var y = 0; y < maskHeight; y++)
        for (var x = 0; x < maskWidth; x++)
        {
            var i = y * maskWidth + x;
            var o = y * scaled.RowBytes + x * 4;
            var c = new SKColor(pixels[o], pixels[o + 1], pixels[o + 2]);
            if (Near(WaterFills, c, 24)) water[i] = 255;
            else if (Near(VegetationFills, c, 22)) vegetation[i] = 255;
            else if (Near(BuiltUpFills, c, 16)) builtUp[i] = 255;
        }
        // Labels, icons and line work leave speckle in the classification; a small blur turns coverage
        // into soft areas without inventing shapes.
        foreach (var mask in new[] { water, vegetation, builtUp }) Smooth(mask, maskWidth, maskHeight);
    }

    private static void Smooth(byte[] mask, int width, int height)
    {
        var source = mask.Select(v => (float)v).ToArray();
        var blurred = SettlementDensityBuilder.GaussianBlur(source, width, height, 1.2);
        for (var i = 0; i < mask.Length; i++) mask[i] = (byte)Math.Clamp(Math.Round(blurred[i]), 0, 255);
    }

    private static PreviewLight[] SampleLights(Guid locationId, float[] density, int width, int height)
    {
        // Seeded by the location, so a location always shows the same lights in every theme and export.
        var random = new Random(BitConverter.ToInt32(locationId.ToByteArray(), 0));
        var lights = new List<PreviewLight>(MaximumLights);
        var total = density.Sum(v => Math.Max(0, v));
        if (total <= 0) return [];
        var target = (int)Math.Min(MaximumLights, 40 + total / 6);
        for (var attempt = 0; attempt < target * 12 && lights.Count < target; attempt++)
        {
            var x = random.Next(width);
            var y = random.Next(height);
            var d = density[y * width + x];
            if (d <= .12f || random.NextDouble() > d * d) continue;
            lights.Add(new((x + (float)random.NextDouble()) * LocationPreviewScene.MaskScale,
                (y + (float)random.NextDouble()) * LocationPreviewScene.MaskScale,
                (float)Math.Clamp(.35 + .65 * d * random.NextDouble(), 0, 1)));
        }
        return [.. lights];
    }

    private static string Fingerprint(Guid id, int width, int height, SKPoint pin, byte[] water, byte[] vegetation,
        byte[] builtUp, byte[] settlement, IReadOnlyList<PreviewLine> lines, IReadOnlyList<PreviewLight> lights)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(id.ToByteArray());
        hash.AppendData(BitConverter.GetBytes(width)); hash.AppendData(BitConverter.GetBytes(height));
        hash.AppendData(BitConverter.GetBytes(pin.X)); hash.AppendData(BitConverter.GetBytes(pin.Y));
        foreach (var mask in new[] { water, vegetation, builtUp, settlement }) hash.AppendData(mask);
        foreach (var line in lines)
        {
            hash.AppendData(BitConverter.GetBytes((int)line.Class));
            foreach (var p in line.Points) { hash.AppendData(BitConverter.GetBytes(p.X)); hash.AppendData(BitConverter.GetBytes(p.Y)); }
        }
        foreach (var l in lights) { hash.AppendData(BitConverter.GetBytes(l.X)); hash.AppendData(BitConverter.GetBytes(l.Y)); hash.AppendData(BitConverter.GetBytes(l.Intensity)); }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
