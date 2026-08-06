using Noctaxis.Core.Domain;
using NodaTime;

namespace Noctaxis.Core.Astronomy;

public static class TerrainCrossingCalculator
{
    public static TerrainCrossings Calculate(AstralPath path, TerrainHorizonProfile terrain)
    {
        Instant? clears = null;
        Instant? drops = null;
        for (var i = 1; i < path.Samples.Count; i++)
        {
            var a = path.Samples[i - 1];
            var b = path.Samples[i];
            var da = a.Horizontal.AltitudeDegrees - terrain.AltitudeAt(a.Horizontal.AzimuthDegrees);
            var db = b.Horizontal.AltitudeDegrees - terrain.AltitudeAt(b.Horizontal.AzimuthDegrees);
            if (da < 0 && db >= 0 && clears is null) clears = Interpolate(a.Instant, b.Instant, da, db);
            if (da >= 0 && db < 0) drops = Interpolate(a.Instant, b.Instant, da, db);
        }
        return new TerrainCrossings(clears, drops);
    }

    private static Instant Interpolate(Instant a, Instant b, double va, double vb)
    {
        var fraction = Math.Abs(va) / (Math.Abs(va) + Math.Abs(vb));
        return a + (b - a) * fraction;
    }
}
