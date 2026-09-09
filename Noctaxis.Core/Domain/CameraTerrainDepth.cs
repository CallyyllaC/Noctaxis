using System.Collections.Immutable;

namespace Noctaxis.Core.Domain;

/// <summary>Camera-space first intersections; zero distance is sky. No renderer or provider ownership.</summary>
public sealed record CameraTerrainDepth(
    ImmutableArray<double> Bearings, double LowerAltitude, double UpperAltitude,
    ImmutableArray<double> DistancesMetres)
{
    public const int Rows = 72;
    public int Width => Bearings.Length;
    public double AltitudeAtRow(int row) => UpperAltitude - (row + .5) * (UpperAltitude - LowerAltitude) / Rows;
    public double DistanceAt(int column, int row) => DistancesMetres[row * Width + column];
}
