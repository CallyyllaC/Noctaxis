using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using NodaTime;

namespace Noctaxis.Desktop.Export;

/// <summary>
/// One moment of the exported target: where it is (bearing and pitch, the planning calculations' own azimuth and
/// altitude) and the terrain horizon in that same direction at that same moment — the terrain's altitude at the
/// target's bearing then, or NaN with no terrain. As the target moves round the sky the terrain is read where it is.
/// </summary>
public sealed record ExportTargetSample(Instant Time, double Bearing, double Pitch, double Terrain)
{
    /// <summary>The horizon the target must clear here: the terrain where there is terrain, else the geometric 0°.</summary>
    public double LocalHorizon => double.IsFinite(Terrain) ? Terrain : 0;

    /// <summary>Above its local horizon, and so in view.</summary>
    public bool IsVisible => Pitch > LocalHorizon;
}

/// <summary>What happens to the target at a moment: it rises over or sets below the geometric horizon or the terrain.</summary>
public enum ExportCrossingKind { Rises, Sets, ClearsTerrain, BehindTerrain }

public sealed record ExportCrossing(ExportCrossingKind Kind, Instant Time, double Pitch);

/// <summary>
/// The exported sheet's subject: the focused target's direction over the planning window, as the Planner
/// calculated it. The sheet's chart draws this alone; no other target is part of it.
/// </summary>
public sealed record ExportTargetSeries(string Name, CelestialRole Role, IReadOnlyList<ExportTargetSample> Samples)
{
    /// <summary>
    /// The focused target's series from its Timeline path and the plan's terrain: each sample's terrain is the horizon
    /// at that sample's own bearing, never at one fixed direction or the camera's.
    /// </summary>
    public static ExportTargetSeries From(TimelinePath path, TerrainHorizonProfile? terrain, CelestialRole role)
    {
        var hasTerrain = terrain is { TerrainCalculationsEnabled: true, Samples.Count: > 1 };
        return new(path.Target.DisplayName, role, path.Path.Samples
            .Select(sample => new ExportTargetSample(sample.Instant, Angles.NormaliseDegrees(sample.Horizontal.AzimuthDegrees),
                sample.Horizontal.AltitudeDegrees,
                hasTerrain ? terrain!.EffectiveAltitudeAt(sample.Horizontal.AzimuthDegrees) ?? double.NaN : double.NaN))
            .ToArray());
    }

    /// <summary>The window's highest point, or none when there are no samples.</summary>
    public ExportTargetSample? Culmination => Samples.Count == 0 ? null : Samples.MaxBy(sample => sample.Pitch);

    /// <summary>
    /// Where the target crosses the geometric horizon (rises, sets) and where it clears or drops behind the terrain,
    /// each at the moment found between the two samples either side, in time order.
    /// </summary>
    public IReadOnlyList<ExportCrossing> Crossings()
    {
        var crossings = new List<ExportCrossing>();
        for (var i = 1; i < Samples.Count; i++)
        {
            var (a, b) = (Samples[i - 1], Samples[i]);
            void Add(double fromHeight, double toHeight, ExportCrossingKind up, ExportCrossingKind down)
            {
                if ((fromHeight > 0) == (toHeight > 0)) return;
                var fraction = fromHeight / (fromHeight - toHeight);
                var time = a.Time + (b.Time - a.Time) * fraction;
                crossings.Add(new(toHeight > 0 ? up : down, time, a.Pitch + (b.Pitch - a.Pitch) * fraction));
            }
            Add(a.Pitch, b.Pitch, ExportCrossingKind.Rises, ExportCrossingKind.Sets);
            if (double.IsFinite(a.Terrain) && double.IsFinite(b.Terrain))
                Add(a.Pitch - a.Terrain, b.Pitch - b.Terrain, ExportCrossingKind.ClearsTerrain, ExportCrossingKind.BehindTerrain);
        }
        return [.. crossings.OrderBy(crossing => crossing.Time)];
    }

    /// <summary>
    /// The target's bearing at a moment inside the samples, between the two either side the short way round (so
    /// between 359° and 1° it passes North); null outside them.
    /// </summary>
    public double? BearingAt(Instant time)
    {
        if (Samples.Count == 0 || time < Samples[0].Time || time > Samples[^1].Time) return null;
        for (var i = 1; i < Samples.Count; i++)
        {
            var (a, b) = (Samples[i - 1], Samples[i]);
            if (time > b.Time) continue;
            var span = (b.Time - a.Time).TotalMinutes;
            var fraction = span <= 0 ? 0 : (time - a.Time).TotalMinutes / span;
            return Angles.NormaliseDegrees(a.Bearing + Angles.NormaliseSignedDegrees(b.Bearing - a.Bearing) * fraction);
        }
        return Samples[0].Bearing;
    }

    /// <summary>
    /// A bearing as the compass reads it — the nearest of the eight points and the whole degrees: "SW 224°". The
    /// degrees are rounded once and the point is taken from them, so 359.6° is "N 0°", never "N 360°", and the point
    /// always agrees with the number beside it (22° is N, 23° is NE).
    /// </summary>
    public static string Compass(double bearing)
    {
        string[] points = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        var degrees = (int)Math.Round(Angles.NormaliseDegrees(bearing), MidpointRounding.AwayFromZero) % 360;
        return $"{points[(int)Math.Round(degrees / 45.0, MidpointRounding.AwayFromZero) % 8]} {degrees}°";
    }
}
