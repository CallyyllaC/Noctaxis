using Noctaxis.Core.Environment;
using Noctaxis.Core.Terrain;

namespace Noctaxis.Core.Tests;

public sealed class TerrainReliefTests
{
    private const int Size = TerrainRelief.TileSize;
    // A mid-latitude zoom-12 tile (northern England), about 23 m per pixel.
    private const int Zoom = 12;
    private const int TileY = 1323;

    [Fact]
    public void FlatGroundHasNoRelief()
    {
        foreach (var height in new[] { -30d, 0d, 850d })
        {
            var relief = Shade(Tile((_, _) => height), _ => null);
            Assert.All(relief, value => Assert.Equal(0, value));
        }
    }

    [Theory]
    // Rising to the east the ground faces west, towards the north-west sun: lit. And so on.
    [InlineData(1, 0, true)]
    [InlineData(-1, 0, false)]
    [InlineData(0, 1, true)]    // rising to the south, facing north
    [InlineData(0, -1, false)]  // rising to the north, facing south
    [InlineData(1, 1, true)]    // facing north-west, straight at the sun
    [InlineData(-1, -1, false)] // facing south-east, straight away from it
    public void SlopesFacingTheLightAreLitAndSlopesFacingAwayAreShaded(int east, int south, bool lit)
    {
        var relief = Shade(Plane(east * 2, south * 2), _ => null);
        var centre = relief[128 * Size + 128];
        Assert.True(lit ? centre > .02 : centre < -.02, $"relief {centre}");
        // A plane has one slope and aspect, so it is shaded uniformly, edges included.
        Assert.All(relief, value => Assert.Equal(centre, value, 3));
    }

    [Fact]
    public void SteeperSlopesAreShadedMoreStrongly()
    {
        double Centre(double metresPerPixel) => Shade(Plane(0, -metresPerPixel), _ => null)[128 * Size + 128];
        Assert.True(Centre(1) < 0);
        Assert.True(Centre(8) < Centre(1));
        Assert.True(Centre(40) < Centre(8));
        Assert.InRange(Centre(400), -1, Centre(40));
    }

    [Fact]
    public void TheSeabedIsDrawnFlatAndACoastIsShadedFromSeaLevel()
    {
        // Terrarium's bathymetry: the sea floor falls away to the west, the land rises to the east.
        var coast = Shade(Tile((column, _) => (column - 128) * 5d), _ => null);
        for (var column = 0; column < 127; column++) Assert.Equal(0, coast[100 * Size + column]);
        Assert.True(coast[100 * Size + 200] > 0);
    }

    [Fact]
    public void ReliefIsBoundedAndNeverNotANumberEvenFromInvalidElevations()
    {
        var elevations = new float[Size * Size];
        var random = new Random(7);
        for (var i = 0; i < elevations.Length; i++) elevations[i] = (float)(random.NextDouble() * 4000 - 500);
        elevations[1000] = float.NaN;
        elevations[5000] = float.PositiveInfinity;
        var relief = TerrainRelief.Shade(TerrainRelief.Pad(TerrariumTile.FromElevations(elevations), (_, _) => null), Zoom, TileY);
        Assert.All(relief, value => Assert.InRange(value, -1, 1));
        // Cells beside an invalid elevation are left flat rather than shaded from it.
        Assert.Equal(0, relief[1000 + 1]);
        Assert.Equal(0, relief[5000 + 1]);
    }

    [Fact]
    public void PaddingTakesOneSampleFromEachNeighbourAndExtrapolatesWhereOneIsMissing()
    {
        var centre = Tile((column, row) => 100 + column);
        var west = Tile((_, _) => 7);
        var padded = TerrainRelief.Pad(centre, (dx, dy) => (dx, dy) == (-1, 0) ? west : null);
        float At(int column, int row) => padded[(row + 1) * TerrainRelief.PaddedSize + column + 1];
        Assert.Equal(7, At(-1, 10));            // from the western neighbour
        Assert.Equal(100 + 256, At(256, 10));    // east missing: the edge gradient continues
        Assert.Equal(100 + 10, At(10, -1));      // north missing: a flat north-south gradient continues
        Assert.Equal(100 + 256, At(256, 256));   // corner continues the padded edge
    }

    [Fact]
    public void AdjacentTilesShadedFromTheirNeighboursJoinWithoutASeam()
    {
        // One continuous landscape spanning two tiles side by side (and the rows above and below).
        static double Land(long x, long y) => 400 * Math.Sin(x / 37d) * Math.Cos(y / 23d) + 3 * (x - 511_000);
        const long originX = 2000L * Size, originY = TileY * (long)Size;
        TerrariumTile At(int tileX, int tileY) => Tile((column, row) =>
            Land(originX + tileX * Size + column, originY + tileY * Size + row));
        float[] Relief(int tileX) => TerrainRelief.Shade(TerrainRelief.Pad(At(tileX, 0), (dx, dy) => At(tileX + dx, dy)), Zoom, TileY);
        var left = Relief(0);
        var right = Relief(1);

        // The step across the tile boundary is no larger than steps between neighbouring columns inside a tile.
        var largestInteriorStep = 0d;
        foreach (var relief in new[] { left, right })
            for (var row = 0; row < Size; row++)
                for (var column = 1; column < Size; column++)
                    largestInteriorStep = Math.Max(largestInteriorStep, Math.Abs(relief[row * Size + column] - relief[row * Size + column - 1]));
        Assert.True(largestInteriorStep > 0);
        for (var row = 0; row < Size; row++)
            Assert.True(Math.Abs(right[row * Size] - left[row * Size + Size - 1]) <= largestInteriorStep + 1e-6);
    }

    [Fact]
    public void ExaggerationGrowsAsTheMapZoomsOutAndIsCapped()
    {
        Assert.Equal(1, TerrainRelief.ExaggerationForZoom(12));
        Assert.Equal(1, TerrainRelief.ExaggerationForZoom(15));
        Assert.True(TerrainRelief.ExaggerationForZoom(8) > TerrainRelief.ExaggerationForZoom(10));
        Assert.Equal(4, TerrainRelief.ExaggerationForZoom(0));
    }

    private static float[] Shade(TerrariumTile tile, Func<(int, int), TerrariumTile?> neighbour) =>
        TerrainRelief.Shade(TerrainRelief.Pad(tile, (dx, dy) => neighbour((dx, dy))), Zoom, TileY, exaggeration: 1);

    private static TerrariumTile Plane(double eastPerPixel, double southPerPixel) =>
        Tile((column, row) => 200_000 + column * eastPerPixel + row * southPerPixel); // well above sea level

    private static TerrariumTile Tile(Func<int, int, double> elevation)
    {
        var values = new float[Size * Size];
        for (var row = 0; row < Size; row++)
            for (var column = 0; column < Size; column++)
                values[row * Size + column] = (float)elevation(column, row);
        return TerrariumTile.FromElevations(values);
    }
}
