namespace Noctaxis.Core.Viability;

/// <summary>
/// A scoring function: a measurement in, a score from 0 to 100 out. Every curve is continuous, deterministic and
/// bounded; each is described in docs/shot-viability-scoring.md with its parameters and representative values.
/// </summary>
public interface IScoreCurve
{
    /// <summary>The score for <paramref name="x"/>, from 0 to 100. A value that is not finite scores 0.</summary>
    double Evaluate(double x);
}

/// <summary>
/// A monotone curve through a table of anchor points, interpolated by a monotone cubic (Fritsch–Butland tangents),
/// so it passes exactly through every anchor, never overshoots between them and has no steps. Below the first
/// anchor it holds the first score, above the last the last. Tuning it is editing the table.
/// </summary>
public sealed class AnchoredCurve : IScoreCurve
{
    private readonly double[] _x, _y, _m;

    public AnchoredCurve(params (double X, double Score)[] anchors)
    {
        if (anchors.Length < 2) throw new ArgumentException("A curve needs at least two anchors.", nameof(anchors));
        _x = anchors.Select(anchor => anchor.X).ToArray();
        _y = anchors.Select(anchor => anchor.Score).ToArray();
        for (var index = 0; index < anchors.Length; index++)
        {
            if (!double.IsFinite(_x[index]) || !double.IsFinite(_y[index]))
                throw new ArgumentException("Anchors must be finite.", nameof(anchors));
            if (index > 0 && _x[index] <= _x[index - 1])
                throw new ArgumentException("Anchors must be in strictly increasing order.", nameof(anchors));
        }
        Anchors = anchors;
        var count = anchors.Length;
        var h = new double[count - 1];
        var d = new double[count - 1];
        for (var index = 0; index < count - 1; index++)
        {
            h[index] = _x[index + 1] - _x[index];
            d[index] = (_y[index + 1] - _y[index]) / h[index];
        }
        _m = new double[count];
        _m[0] = d[0];
        _m[count - 1] = d[count - 2];
        for (var index = 1; index < count - 1; index++)
            _m[index] = d[index - 1] * d[index] <= 0 ? 0
                : 3 * (h[index - 1] + h[index]) /
                  ((2 * h[index] + h[index - 1]) / d[index - 1] + (h[index] + 2 * h[index - 1]) / d[index]);
    }

    /// <summary>The anchor table, as given.</summary>
    public IReadOnlyList<(double X, double Score)> Anchors { get; }

    public double Evaluate(double x)
    {
        if (!double.IsFinite(x)) return 0;
        if (x <= _x[0]) return Clamp(_y[0]);
        if (x >= _x[^1]) return Clamp(_y[^1]);
        var index = Array.BinarySearch(_x, x);
        if (index >= 0) return Clamp(_y[index]);
        index = ~index - 1;
        var width = _x[index + 1] - _x[index];
        var t = (x - _x[index]) / width;
        var t2 = t * t;
        var t3 = t2 * t;
        var value = (2 * t3 - 3 * t2 + 1) * _y[index] + (t3 - 2 * t2 + t) * width * _m[index] +
                    (-2 * t3 + 3 * t2) * _y[index + 1] + (t3 - t2) * width * _m[index + 1];
        return Clamp(value);
    }

    private static double Clamp(double value) => Math.Clamp(value, 0, 100);
}

/// <summary>
/// A logistic (sigmoid) step from 0 to 100, rescaled so it reaches exactly 0 at <see cref="From"/> and exactly 100
/// at <see cref="To"/> (or the reverse when <see cref="Decreasing"/>), and holds those scores beyond them. The
/// midpoint is where the score is about half way; the scale is how wide the transition is (about 4.4 scales
/// cover 10% to 90% of a plain logistic).
/// </summary>
public sealed record LogisticCurve(double Midpoint, double Scale, double From, double To, bool Decreasing = false) : IScoreCurve
{
    public double Evaluate(double x)
    {
        if (!double.IsFinite(x)) return 0;
        var low = Raw(From);
        var high = Raw(To);
        var fraction = Math.Clamp((Raw(Math.Clamp(x, From, To)) - low) / (high - low), 0, 1);
        return 100 * (Decreasing ? 1 - fraction : fraction);
    }

    private double Raw(double x) => 1 / (1 + Math.Exp(-(x - Midpoint) / Scale));
}

/// <summary>
/// A saturating exponential, 100 × (1 − e^(−x / τ)): each extra unit adds less than the one before. Zero and
/// below score 0.
/// </summary>
public sealed record SaturatingCurve(double TimeConstant) : IScoreCurve
{
    public double Evaluate(double x) =>
        !double.IsFinite(x) || x <= 0 ? 0 : Math.Clamp(100 * (1 - Math.Exp(-x / TimeConstant)), 0, 100);
}

/// <summary>Hermite smoothstep from 0 at <paramref name="edge0"/> to 1 at <paramref name="edge1"/>: a factor, not a score.</summary>
public static class Smoothstep
{
    public static double Evaluate(double edge0, double edge1, double x)
    {
        if (!double.IsFinite(x)) return 0;
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
