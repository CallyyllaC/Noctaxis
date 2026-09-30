using Noctaxis.Core.Environment;

namespace Noctaxis.Core.Terrain;

/// <summary>
/// Hillshade for presenting terrain on the map. It shows the shape of the land and is never used
/// to decide what the observer can see: the horizon and obstruction calculations sample the same
/// Terrarium elevations through <see cref="ITerrainElevationProvider"/> and are unaffected by it.
/// </summary>
/// <remarks>
/// Horn's (1981) 3 x 3 gradient over the elevation grid, lit by a conventional synthetic sun in the
/// north-west at 45°. The result is relative to flat ground: 0 where the land is level, positive on
/// slopes facing the light, negative on slopes facing away. Flat land therefore gains no texture.
/// Each tile is shaded from a grid padded with one sample from each of its eight neighbours, so the
/// gradient at a tile's edge is the gradient of the land there, and tile boundaries do not show.
/// </remarks>
public static class TerrainRelief
{
    /// <summary>Changes whenever the output for the same elevations would change.</summary>
    public const int AlgorithmVersion = 1;
    public const int TileSize = TerrariumTerrainOptions.TileSize;
    public const int PaddedSize = TileSize + 2;
    public const double SunAzimuthDegrees = 315;
    public const double SunAltitudeDegrees = 45;

    private static readonly double Zenith = (90 - SunAltitudeDegrees) * Math.PI / 180;
    // Horn's aspect is measured anticlockwise from east, so the compass azimuth is converted.
    private static readonly double SunAzimuth = ((360 - SunAzimuthDegrees + 90) % 360) * Math.PI / 180;
    private static readonly double FlatShade = Math.Cos(Zenith);

    /// <summary>
    /// Vertical exaggeration for a Terrarium zoom. One map pixel covers ever more ground as the map
    /// zooms out, which flattens real slopes towards nothing; a moderate, capped exaggeration keeps
    /// hills recognisable at regional scale without turning lowland undulation into mountains.
    /// </summary>
    public static double ExaggerationForZoom(int zoom) => Math.Clamp(Math.Pow(1.3, 12 - zoom), 1, 4);

    /// <summary>
    /// The padded elevation grid for <paramref name="centre"/>, <see cref="PaddedSize"/> square.
    /// <paramref name="neighbour"/> returns the adjacent tile at (dx, dy), each -1, 0 or +1 with y
    /// increasing southwards, or null when it is unavailable (not downloaded, failed, or beyond a
    /// pole). A missing neighbour's samples are extrapolated linearly from the tile's own edge, which
    /// continues the edge gradient instead of inventing a cliff or a flat rim.
    /// Elevations below sea level are raised to it: Terrarium includes bathymetry, and relief is about
    /// the land, so the sea (and the rare dry land below sea level) is drawn flat.
    /// </summary>
    public static float[] Pad(TerrariumTile centre, Func<int, int, TerrariumTile?> neighbour)
    {
        ArgumentNullException.ThrowIfNull(centre);
        ArgumentNullException.ThrowIfNull(neighbour);
        var grid = new float[PaddedSize * PaddedSize];
        // Math.Max keeps NaN, so an invalid sample still disables shading around it.
        void Set(int column, int row, double value) => grid[(row + 1) * PaddedSize + column + 1] = (float)Math.Max(0, value);
        double Get(int column, int row) => grid[(row + 1) * PaddedSize + column + 1];

        for (var row = 0; row < TileSize; row++)
            for (var column = 0; column < TileSize; column++)
                Set(column, row, centre[column, row]);

        var west = neighbour(-1, 0);
        var east = neighbour(1, 0);
        var north = neighbour(0, -1);
        var south = neighbour(0, 1);
        const int last = TileSize - 1;
        for (var i = 0; i < TileSize; i++)
        {
            Set(-1, i, west?[last, i] ?? 2 * Get(0, i) - Get(1, i));
            Set(TileSize, i, east?[0, i] ?? 2 * Get(last, i) - Get(last - 1, i));
            Set(i, -1, north?[i, last] ?? 2 * Get(i, 0) - Get(i, 1));
            Set(i, TileSize, south?[i, 0] ?? 2 * Get(i, last) - Get(i, last - 1));
        }
        // Corners come from the diagonal tiles, or continue the padded edge rows beside them.
        Set(-1, -1, neighbour(-1, -1)?[last, last] ?? 2 * Get(0, -1) - Get(1, -1));
        Set(TileSize, -1, neighbour(1, -1)?[0, last] ?? 2 * Get(last, -1) - Get(last - 1, -1));
        Set(-1, TileSize, neighbour(-1, 1)?[last, 0] ?? 2 * Get(0, TileSize) - Get(1, TileSize));
        Set(TileSize, TileSize, neighbour(1, 1)?[0, 0] ?? 2 * Get(last, TileSize) - Get(last - 1, TileSize));
        return grid;
    }

    /// <summary>
    /// Relief for the 256 x 256 tile (<paramref name="zoom"/>, <paramref name="tileY"/>) from its
    /// padded grid, row-major, each value in [-1, 1] and 0 on flat ground. A cell whose neighbourhood
    /// holds a non-finite elevation is left flat rather than shaded from invalid data.
    /// </summary>
    public static float[] Shade(float[] padded, int zoom, int tileY, double? exaggeration = null)
    {
        ArgumentNullException.ThrowIfNull(padded);
        if (padded.Length != PaddedSize * PaddedSize)
            throw new ArgumentException("The padded grid must be 258 x 258.", nameof(padded));
        if (zoom is < 0 or > 30) throw new ArgumentOutOfRangeException(nameof(zoom));
        var factor = exaggeration ?? ExaggerationForZoom(zoom);
        var relief = new float[TileSize * TileSize];
        var worldPixels = (double)TileSize * (1L << zoom);
        var sinZenith = Math.Sin(Zenith);
        for (var row = 0; row < TileSize; row++)
        {
            // Web Mercator is conformal: one pixel spans the same ground distance east-west and
            // north-south at a given latitude, and that distance shrinks with the cosine of latitude.
            var mercatorY = (tileY * (double)TileSize + row + .5) / worldPixels;
            var latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * mercatorY))) * 180 / Math.PI;
            var cell = TerrariumTerrainProvider.ResolutionMetres(latitude, zoom);
            var scale = factor / (8 * cell);
            var top = row * PaddedSize;
            var middle = top + PaddedSize;
            var bottom = middle + PaddedSize;
            for (var column = 0; column < TileSize; column++)
            {
                double a = padded[top + column], b = padded[top + column + 1], c = padded[top + column + 2];
                double d = padded[middle + column], f = padded[middle + column + 2];
                double g = padded[bottom + column], h = padded[bottom + column + 1], i = padded[bottom + column + 2];
                var dzdx = (c + 2 * f + i - (a + 2 * d + g)) * scale;
                var dzdy = (g + 2 * h + i - (a + 2 * b + c)) * scale;
                if (!double.IsFinite(dzdx) || !double.IsFinite(dzdy)) continue;
                var gradient = Math.Sqrt(dzdx * dzdx + dzdy * dzdy);
                if (gradient == 0) continue;
                var slope = Math.Atan(gradient);
                var aspect = Math.Atan2(dzdy, -dzdx);
                var shade = Math.Max(0, FlatShade * Math.Cos(slope) + sinZenith * Math.Sin(slope) * Math.Cos(SunAzimuth - aspect));
                relief[row * TileSize + column] = (float)Math.Clamp(shade - FlatShade, -1, 1);
            }
        }
        return relief;
    }
}
