using System.Diagnostics;
using Noctaxis.Desktop.Controls;

// Diagnostic replay of the retained indexed implementation. Runs after the native
// interaction samples, never inside the measured callback.
internal static class ContrastPreparationMeasurements
{
    internal static object Measure(PlanTerrainFan fan)
    {
        var rows = new List<object>();
        double[]? baseline = null;
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            var p = fan.Patches;
            var absolute = new double[p.Length];
            var result = new double[p.Length];
            for (var i = 0; i < p.Length; i++) absolute[i] = TerrainFanLocalContrast.Absolute(p[i]);
            var absoluteMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var clock = Stopwatch.GetTimestamp();
            int Bucket(int i) => (int)Math.Floor(((p[i].LeftOffset + p[i].RightOffset) / 2) / 2.1);
            var buckets = new Dictionary<int, List<int>>();
            for (var i = 0; i < p.Length; i++)
            {
                var key = Bucket(i);
                if (!buckets.TryGetValue(key, out var list)) buckets.Add(key, list = []);
                list.Add(i);
            }
            var bucketMs = Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
            var neighbourhoods = new Dictionary<int, int[]>();
            var candidates = new List<int>();
            double indexMs = 0, sortMs = 0;
            foreach (var key in buckets.Keys)
            {
                clock = Stopwatch.GetTimestamp();
                candidates.Clear();
                for (var k = key - 2; k <= key + 2; k++)
                    if (buckets.TryGetValue(k, out var list)) candidates.AddRange(list);
                indexMs += Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
                clock = Stopwatch.GetTimestamp(); candidates.Sort();
                sortMs += Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
                clock = Stopwatch.GetTimestamp(); neighbourhoods.Add(key, candidates.ToArray());
                indexMs += Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
            }
            long visits = 0, radialChecks = 0, accepted = 0;
            clock = Stopwatch.GetTimestamp();
            for (var i = 0; i < p.Length; i++)
            {
                var sum = 0d; var count = 0;
                foreach (var j in neighbourhoods[Bucket(i)])
                {
                    if (i == j) continue;
                    visits++;
                    if (Math.Abs((p[i].LeftOffset + p[i].RightOffset) / 2 - (p[j].LeftOffset + p[j].RightOffset) / 2) > 2.1) continue;
                    radialChecks++;
                    var overlap = Math.Max(0, Math.Min(p[i].LeftEnd, p[j].LeftEnd) - Math.Max(p[i].LeftStart, p[j].LeftStart));
                    var small = Math.Max(1, Math.Min(p[i].LeftEnd - p[i].LeftStart, p[j].LeftEnd - p[j].LeftStart));
                    var large = Math.Max(1, Math.Max(p[i].LeftEnd - p[i].LeftStart, p[j].LeftEnd - p[j].LeftStart));
                    var radial = overlap / small > .05 || Math.Abs(p[i].LeftStart - p[j].LeftEnd) <= large * .035 ||
                        Math.Abs(p[j].LeftStart - p[i].LeftEnd) <= large * .035;
                    if (!(overlap > 0 || radial) || !double.IsFinite(absolute[j])) continue;
                    sum += absolute[j]; count++; accepted++;
                }
                result[i] = count == 0 ? absolute[i] : Math.Clamp(absolute[i] + .18 * Math.Clamp(absolute[i] - sum / count, -.25, .25), 0, 1);
            }
            var predicateAndAccumulationMs = Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
            var totalMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            baseline = result;
            allocated = GC.GetAllocatedBytesForCurrentThread(); clock = Stopwatch.GetTimestamp();
            var optimized = TerrainFanLocalContrast.Prepare(fan, out var optimizedComparisons);
            var optimizedMs = Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
            var optimizedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            var maxError = optimized.Zip(result).Select(pair => Math.Abs(pair.First - pair.Second)).DefaultIfEmpty().Max();
            if (maxError > 1e-14) throw new InvalidOperationException($"Contrast equivalence failed: {maxError}");
            rows.Add(new { iteration, totalMs, absoluteMs, bucketMs, indexMs, sortMs, predicateAndAccumulationMs,
                bytes, visits, radialChecks, accepted, optimizedMs, optimizedBytes, optimizedComparisons, maxError });
        }
        return new { patches = fan.Patches.Length, rows,
            note = "Indexed baseline replay after native sampling. Predicate and accumulation timed together to avoid per-candidate clock overhead; no LINQ in baseline hot loop, no fingerprint or cross-fan cache." };
    }
}
