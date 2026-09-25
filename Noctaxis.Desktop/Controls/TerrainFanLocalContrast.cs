namespace Noctaxis.Desktop.Controls;

public enum TerrainFanLocalContrastMode
{
    None,
    Bearing,
    Radial,
    Combined
}

/// <summary>
/// Small terrain-space presentation adjustment. It consumes only the existing fan
/// patches; it does not inspect map pixels or change the fan topology.
/// </summary>
public static class TerrainFanLocalContrast
{
    public const double DefaultAmount = .18;
    public const double BearingNeighbourDegrees = 2.1;
    public const double RadialNeighbourFraction = .035;

    /// <summary>Prepares presentation tones once per immutable fan. Candidate indices remain
    /// in patch order so the neighbourhood mean has the same accumulation order.</summary>
    public static double[] Prepare(PlanTerrainFan fan, out long comparisons,
        TerrainFanLocalContrastMode mode = TerrainFanLocalContrastMode.Combined,
        double amount = DefaultAmount)
    {
        comparisons = 0;
        var patches = fan.Patches;
        var absolute = new double[patches.Length];
        var result = new double[patches.Length];
        var centres = new double[patches.Length];
        for (var i = 0; i < patches.Length; i++)
        {
            absolute[i] = Absolute(patches[i]);
            centres[i] = Centre(patches[i]);
        }
        if (mode == TerrainFanLocalContrastMode.None) return absolute;
        // PlanTerrainFan.Connect emits patches in monotonically increasing bearing
        // order (levels within an interval have the same centre). Walk that ordered
        // structure directly instead of rebuilding/sorting a general spatial index.
        // Keep the old indexed evaluator for hand-built or otherwise unordered fans;
        // this preserves the reference semantics for every caller.
        for (var i = 1; i < centres.Length; i++)
            if (centres[i] < centres[i - 1])
                return PrepareIndexed(fan, absolute, centres, out comparisons, mode, amount);

        var lower = 0;
        var upper = 0;
        var gain = Math.Clamp(amount, 0, .3);
        for (var i = 0; i < patches.Length; i++)
        {
            while (lower < i && centres[i] - centres[lower] > BearingNeighbourDegrees) lower++;
            while (upper < patches.Length && centres[upper] - centres[i] <= BearingNeighbourDegrees) upper++;
            var patch = patches[i];
            var start = patch.LeftStart;
            var end = patch.LeftEnd;
            var width = end - start;
            var sum = 0d;
            var count = 0;
            for (var j = lower; j < upper; j++)
            {
                if (i == j) continue;
                comparisons++;
                var other = patches[j];
                var overlap = Math.Max(0, Math.Min(end, other.LeftEnd) - Math.Max(start, other.LeftStart));
                // Combined's overlap branch already accepts every positive overlap;
                // the fractional overlap test can only add work, never a neighbour.
                var neighbour = mode == TerrainFanLocalContrastMode.Bearing ? overlap > 0 :
                    (mode == TerrainFanLocalContrastMode.Combined && overlap > 0) ||
                    (mode == TerrainFanLocalContrastMode.Radial && overlap / Math.Max(1, Math.Min(width, other.LeftEnd - other.LeftStart)) > .05) ||
                    (mode is TerrainFanLocalContrastMode.Radial or TerrainFanLocalContrastMode.Combined &&
                        (Math.Abs(start - other.LeftEnd) <= Math.Max(1, Math.Max(width, other.LeftEnd - other.LeftStart)) * RadialNeighbourFraction ||
                         Math.Abs(other.LeftStart - end) <= Math.Max(1, Math.Max(width, other.LeftEnd - other.LeftStart)) * RadialNeighbourFraction));
                if (!neighbour || !double.IsFinite(absolute[j])) continue;
                sum += absolute[j]; count++;
            }
            result[i] = count == 0 ? absolute[i] : Math.Clamp(absolute[i] +
                gain * Math.Clamp(absolute[i] - sum / count, -.25, .25), 0, 1);
        }
        return result;
    }

    private static double[] PrepareIndexed(PlanTerrainFan fan, double[] absolute, double[] centres,
        out long comparisons, TerrainFanLocalContrastMode mode, double amount)
    {
        comparisons = 0;
        var patches = fan.Patches;
        var result = new double[patches.Length];
        var buckets = new Dictionary<int, List<int>>();
        for (var i = 0; i < patches.Length; i++)
        {
            var key = (int)Math.Floor(centres[i] / BearingNeighbourDegrees);
            if (!buckets.TryGetValue(key, out var entries)) buckets.Add(key, entries = []);
            entries.Add(i);
        }
        var candidates = new List<int>();
        var neighbourhoods = new Dictionary<int, int[]>();
        foreach (var key in buckets.Keys)
        {
            candidates.Clear();
            for (var k = key - 2; k <= key + 2; k++)
                if (buckets.TryGetValue(k, out var entries)) candidates.AddRange(entries);
            candidates.Sort();
            neighbourhoods.Add(key, candidates.ToArray());
        }
        for (var i = 0; i < patches.Length; i++)
        {
            var sum = 0d;
            var count = 0;
            foreach (var j in neighbourhoods[(int)Math.Floor(centres[i] / BearingNeighbourDegrees)])
            {
                if (i == j) continue;
                comparisons++;
                if (Math.Abs(centres[i] - centres[j]) > BearingNeighbourDegrees) continue;
                if (!IsNeighbour(patches[i], patches[j], centres[i], centres[j], mode) || !double.IsFinite(absolute[j])) continue;
                sum += absolute[j]; count++;
            }
            result[i] = count == 0 ? absolute[i] : Math.Clamp(absolute[i] +
                Math.Clamp(amount, 0, .3) * Math.Clamp(absolute[i] - sum / count, -.25, .25), 0, 1);
        }
        return result;
    }

    public static double Enhance(PlanTerrainFan fan, int patchIndex,
        TerrainFanLocalContrastMode mode = TerrainFanLocalContrastMode.Combined,
        double amount = DefaultAmount)
    {
        if ((uint)patchIndex >= (uint)fan.Patches.Length || mode == TerrainFanLocalContrastMode.None)
            return Absolute(fan.Patches[patchIndex]);
        var patch = fan.Patches[patchIndex];
        var absolute = Absolute(patch);
        var neighbours = fan.Patches
            .Select((candidate, index) => (candidate, index))
            .Where(item => item.index != patchIndex)
            .Where(item => IsNeighbour(patch, item.candidate, mode))
            .Select(item => Absolute(item.candidate))
            .Where(double.IsFinite)
            .ToArray();
        if (neighbours.Length == 0) return absolute;

        var reference = neighbours.Average();
        // Keep the absolute field authoritative: the local term is a restrained
        // residual, bounded to the same contribution as the requested amount.
        var local = Math.Clamp(absolute - reference, -.25, .25);
        return Math.Clamp(absolute + Math.Clamp(amount, 0, .3) * local, 0, 1);
    }

    public static double Absolute(PlanTerrainPatch patch) =>
        TerrainFanTone.Strength(patch.ApparentAltitudeDegrees);

    private static bool IsNeighbour(PlanTerrainPatch a, PlanTerrainPatch b,
        TerrainFanLocalContrastMode mode)
    {
        return IsNeighbour(a, b, Centre(a), Centre(b), mode);
    }

    private static bool IsNeighbour(PlanTerrainPatch a, PlanTerrainPatch b,
        double aCentre, double bCentre, TerrainFanLocalContrastMode mode)
    {
        var angular = Math.Abs(aCentre - bCentre) <= BearingNeighbourDegrees;
        var radialOverlap = Math.Max(0, Math.Min(a.LeftEnd, b.LeftEnd) - Math.Max(a.LeftStart, b.LeftStart));
        var radialScale = Math.Max(1, Math.Min(a.LeftEnd - a.LeftStart, b.LeftEnd - b.LeftStart));
        var scale = Math.Max(1, Math.Max(a.LeftEnd - a.LeftStart, b.LeftEnd - b.LeftStart));
        var radial = radialOverlap / radialScale > .05 ||
            Math.Abs(a.LeftStart - b.LeftEnd) <= scale * RadialNeighbourFraction ||
            Math.Abs(b.LeftStart - a.LeftEnd) <= scale * RadialNeighbourFraction;
        return mode switch
        {
            TerrainFanLocalContrastMode.Bearing => angular && radialOverlap > 0,
            TerrainFanLocalContrastMode.Radial => angular && radial,
            TerrainFanLocalContrastMode.Combined => angular && (radialOverlap > 0 || radial),
            _ => false
        };
    }

    private static double Centre(PlanTerrainPatch patch) => (patch.LeftOffset + patch.RightOffset) / 2;
}
