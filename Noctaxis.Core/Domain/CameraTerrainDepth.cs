using System.Collections.Immutable;

namespace Noctaxis.Core.Domain;

/// <summary>Camera-space first intersections; zero distance is sky. No renderer or provider ownership.</summary>
/// <param name="HorizonAltitudes">
/// Per bearing, the terrain horizon's exact altitude (absolute, in degrees; NaN where there is none): the skyline
/// itself, which the rows only approximate to their height.
/// </param>
public sealed record CameraTerrainDepth(
    ImmutableArray<double> Bearings, double LowerAltitude, double UpperAltitude,
    ImmutableArray<double> DistancesMetres, ImmutableArray<double> HorizonAltitudes = default)
{
    /// <summary>The terrain horizon's altitude at a column, or NaN without one.</summary>
    public double HorizonAt(int column) =>
        HorizonAltitudes.IsDefaultOrEmpty || column < 0 || column >= HorizonAltitudes.Length ? double.NaN : HorizonAltitudes[column];
    public const int Rows = 72;
    public int Width => Bearings.Length;
    public double AltitudeAtRow(int row) => UpperAltitude - (row + .5) * (UpperAltitude - LowerAltitude) / Rows;
    public double DistanceAt(int column, int row) => DistancesMetres[row * Width + column];
}
