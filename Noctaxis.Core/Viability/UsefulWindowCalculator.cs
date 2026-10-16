using Noctaxis.Core.Domain;
using NodaTime;

namespace Noctaxis.Core.Viability;

/// <summary>What ends (or, when it is closed, prevents) a useful time window.</summary>
public enum UsefulWindowLimit { None, Darkness, Altitude, Terrain }

/// <summary>
/// The favourable time from the planned moment: one unbroken stretch in which every condition holds at once,
/// never the sum of separate stretches. When the moment itself is not favourable, the window is closed and
/// the next one (if any, within the horizon) is described.
/// </summary>
public sealed record UsefulWindowResult(
    bool IsOpen,
    Duration Remaining,
    bool IsAtLeast,
    UsefulWindowLimit Limit,
    Duration? NextOpensIn,
    Duration? NextLasts,
    bool TerrainConsidered);

/// <summary>
/// Follows the useful window along precomputed paths: the target at least
/// <see cref="ShotViabilityPolicy.AcceptableAltitudeDegrees"/> up, the Sun at or below
/// <see cref="ShotViabilityPolicy.NauticalDarknessDegrees"/>, and the target above the terrain horizon at its
/// azimuth when terrain is known. Each condition is a margin in degrees; the window is where the smallest
/// margin is not negative, with its ends interpolated between samples.
/// </summary>
public static class UsefulWindowCalculator
{
    public static UsefulWindowResult? Evaluate(ShotWindowPaths paths, Instant instant, TerrainHorizonProfile? terrain)
    {
        var target = paths.Target;
        var sun = paths.Sun;
        if (target.Count < 2 || target.Count != sun.Count) return null;
        for (var index = 0; index < target.Count; index++)
            if (target[index].Instant != sun[index].Instant || (index > 0 && target[index].Instant <= target[index - 1].Instant))
                return null;
        if (instant < target[0].Instant || instant > target[^1].Instant) return null;

        var terrainConsidered = ShotViabilityScorer.HasUsableTerrain(terrain);
        var margins = new double[target.Count];
        var limits = new UsefulWindowLimit[target.Count];
        for (var index = 0; index < target.Count; index++)
            (margins[index], limits[index]) = Margin(target[index].Horizontal, sun[index].Horizontal.AltitudeDegrees,
                terrainConsidered ? terrain : null);

        var segment = 0;
        while (segment < target.Count - 2 && target[segment + 1].Instant <= instant) segment++;
        var now = Interpolate(target[segment].Instant, margins[segment], target[segment + 1].Instant, margins[segment + 1], instant);
        var horizonEnd = instant + Duration.FromTimeSpan(ShotViabilityPolicy.UsefulWindowHorizon);
        var dataEnd = target[^1].Instant;
        var end = horizonEnd < dataEnd ? horizonEnd : dataEnd;

        if (now >= 0)
        {
            var (closes, limit) = FollowOpen(target, margins, limits, segment + 1, instant, now, end);
            return closes is { } at
                ? new UsefulWindowResult(true, at - instant, false, limit, null, null, terrainConsidered)
                : new UsefulWindowResult(true, end - instant, true, UsefulWindowLimit.None, null, null, terrainConsidered);
        }

        // Closed now: the condition failing worst at the moment says why; then look for the next opening.
        var nowLimit = target[segment + 1].Instant - instant < instant - target[segment].Instant ? limits[segment + 1] : limits[segment];
        var previous = (Instant: instant, Margin: now);
        for (var index = segment + 1; index < target.Count && target[index].Instant <= end; index++)
        {
            if (margins[index] >= 0)
            {
                var opens = Crossing(previous.Instant, previous.Margin, target[index].Instant, margins[index]);
                var (closes, _) = FollowOpen(target, margins, limits, index, opens, 0, end);
                return new UsefulWindowResult(false, Duration.Zero, false, nowLimit, opens - instant,
                    (closes ?? end) - opens, terrainConsidered);
            }
            previous = (target[index].Instant, margins[index]);
        }
        return new UsefulWindowResult(false, Duration.Zero, false, nowLimit, null, null, terrainConsidered);
    }

    private static (Instant? Closes, UsefulWindowLimit Limit) FollowOpen(IReadOnlyList<AstralPathSample> target,
        double[] margins, UsefulWindowLimit[] limits, int from, Instant start, double startMargin, Instant end)
    {
        var previous = (Instant: start, Margin: startMargin);
        for (var index = from; index < target.Count; index++)
        {
            var at = target[index].Instant;
            if (margins[index] < 0)
            {
                var closes = Crossing(previous.Instant, previous.Margin, at, margins[index]);
                return closes <= end ? (closes, limits[index]) : (null, UsefulWindowLimit.None);
            }
            if (at >= end) break;
            previous = (at, margins[index]);
        }
        return (null, UsefulWindowLimit.None);
    }

    private static (double Margin, UsefulWindowLimit Limit) Margin(HorizontalCoordinate target, double sunAltitude,
        TerrainHorizonProfile? terrain)
    {
        var margin = ShotViabilityPolicy.NauticalDarknessDegrees - sunAltitude;
        var limit = UsefulWindowLimit.Darkness;
        var altitude = target.AltitudeDegrees - ShotViabilityPolicy.AcceptableAltitudeDegrees;
        if (altitude < margin) (margin, limit) = (altitude, UsefulWindowLimit.Altitude);
        if (terrain?.EffectiveAltitudeAt(target.AzimuthDegrees) is double horizon &&
            target.AltitudeDegrees - horizon is var clearance && clearance < margin)
            (margin, limit) = (clearance, UsefulWindowLimit.Terrain);
        return (margin, limit);
    }

    private static double Interpolate(Instant a, double ma, Instant b, double mb, Instant at) =>
        b == a ? ma : ma + (mb - ma) * ((at - a).TotalTicks / (b - a).TotalTicks);

    /// <summary>Where the margin, linear between two samples, reaches zero.</summary>
    private static Instant Crossing(Instant a, double ma, Instant b, double mb)
    {
        var fraction = Math.Abs(mb - ma) < 1e-12 ? 0 : Math.Clamp(ma / (ma - mb), 0, 1);
        return a + (b - a) * fraction;
    }
}
