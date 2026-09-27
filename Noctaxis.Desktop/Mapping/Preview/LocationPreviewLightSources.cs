namespace Noctaxis.Desktop.Mapping.Preview;

/// <summary>
/// A meaningful concentration of off-site light, as seen from the saved site.
/// </summary>
/// <param name="BearingDegrees">Compass bearing (0 = north, clockwise) from the site to where the concentration's light is most relevant (its densest, nearest part).</param>
/// <param name="SpreadDegrees">Angular width the concentration covers as seen from the site; geometry only, independent of brightness.</param>
/// <param name="Strength">Relevance relative to the strongest selected source (1 for the strongest).</param>
/// <param name="Relevance">Absolute relevance: the concentration's light, each part weighted by its proximity to the site.</param>
/// <param name="DistanceFraction">Distance of the concentration's light-weighted centre from the site, as a fraction of the scene height.</param>
public readonly record struct LocationPreviewLightSource(float BearingDegrees, float SpreadDegrees, float Strength,
    float Relevance, float DistanceFraction);

/// <summary>
/// Finds up to three meaningful light concentrations around a site from its settlement field. Pure and
/// deterministic. Concentrations are connected regions of the field; each is measured separately, so two
/// towns are never averaged into a direction between them, and light the site itself sits in outshines
/// lesser light elsewhere.
/// </summary>
public static class LocationPreviewLightSources
{
    public const int MaximumSources = 3;
    /// <summary>Selected sources are at least this far apart in bearing.</summary>
    public const float MinimumSeparationDegrees = 90;
    /// <summary>Field level (0..255) that counts as lit when tracing a concentration.</summary>
    public const byte LitThreshold = 56;
    /// <summary>Minimum light mass (sum of lit density over mask cells) for a concentration to count at all.</summary>
    public const float MinimumMass = 60;
    /// <summary>Concentrations centred closer than this fraction of the scene height are at the site: no direction.</summary>
    public const float AtSiteFraction = .12f;
    /// <summary>Proximity weighting: relevance halves at this fraction of the scene height.</summary>
    public const float ProximityFraction = .45f;
    /// <summary>Candidates weaker than this share of the strongest are clutter.</summary>
    public const float MinimumRelativeRelevance = .15f;
    /// <summary>
    /// Light at the site outshines lesser light elsewhere: an off-site source needs at least this share of
    /// the relevance of the light the site sits in (none when the site is dark).
    /// </summary>
    public const float SiteDominance = .5f;
    /// <summary>Width of the bearing window used to find the direction of a concentration's light.</summary>
    public const int PeakWindowDegrees = 30;

    public static IReadOnlyList<LocationPreviewLightSource> Find(LocationPreviewScene scene) =>
        Find(scene.Settlement, scene.MaskWidth, scene.MaskHeight, scene.Pin.X / LocationPreviewScene.MaskScale,
            scene.Pin.Y / LocationPreviewScene.MaskScale, scene.MaskHeight);

    /// <summary>Finds sources in a <paramref name="width"/> × <paramref name="height"/> field seen from (<paramref name="siteX"/>, <paramref name="siteY"/>).</summary>
    public static IReadOnlyList<LocationPreviewLightSource> Find(byte[] field, int width, int height, float siteX, float siteY, float sceneHeight)
    {
        var candidates = new List<LocationPreviewLightSource>();
        double siteRelevance = 0;
        var label = new int[field.Length];
        var stack = new Stack<int>();
        var cells = new List<int>();
        var siteCell = (int)Math.Clamp(siteY, 0, height - 1) * width + (int)Math.Clamp(siteX, 0, width - 1);
        for (var start = 0; start < field.Length; start++)
        {
            if (label[start] != 0 || field[start] < LitThreshold) continue;
            // Trace one connected concentration.
            cells.Clear();
            label[start] = 1;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                cells.Add(i);
                int x = i % width, y = i / width;
                foreach (var n in new[] { x > 0 ? i - 1 : -1, x < width - 1 ? i + 1 : -1, y > 0 ? i - width : -1, y < height - 1 ? i + width : -1 })
                    if (n >= 0 && label[n] == 0 && field[n] >= LitThreshold) { label[n] = 1; stack.Push(n); }
            }
            double mass = 0, cx = 0, cy = 0, relevance = 0;
            var profile = new double[360];
            foreach (var i in cells)
            {
                double d = field[i], px = i % width + .5 - siteX, py = i / width + .5 - siteY;
                mass += d / 255; cx += d * px; cy += d * py;
                // Nearer light matters more: each cell's contribution falls away with its distance.
                var w = d / 255 / (1 + (px * px + py * py) / Math.Pow(ProximityFraction * sceneHeight, 2));
                relevance += w;
                profile[(int)Bearing(px, py) % 360] += w;
            }
            cx /= mass * 255; cy /= mass * 255;
            var distance = Math.Sqrt(cx * cx + cy * cy);
            if (cells.Contains(siteCell) || distance < AtSiteFraction * sceneHeight)
            {
                // The site sits in or at this light: it has no direction, but it outshines lesser light elsewhere.
                siteRelevance += relevance;
                continue;
            }
            if (mass < MinimumMass) continue;
            var bearing = PeakBearing(profile);
            candidates.Add(new((float)bearing, (float)Spread(field, cells, width, siteX, siteY, bearing), 0, (float)relevance,
                (float)(distance / sceneHeight)));
        }

        // Strongest first; each later source must stand at least 90° from every one already chosen.
        var selected = new List<LocationPreviewLightSource>();
        foreach (var candidate in candidates.OrderByDescending(c => c.Relevance).ThenBy(c => c.BearingDegrees))
        {
            if (selected.Count == MaximumSources) break;
            if (candidate.Relevance < SiteDominance * siteRelevance) break;
            if (selected.Count > 0 && candidate.Relevance < MinimumRelativeRelevance * selected[0].Relevance) break;
            if (selected.All(s => Separation(s.BearingDegrees, candidate.BearingDegrees) >= MinimumSeparationDegrees))
                selected.Add(candidate);
        }
        var strongest = selected.Count > 0 ? selected[0].Relevance : 1;
        return selected.Select(s => s with { Strength = s.Relevance / strongest }).ToArray();
    }

    /// <summary>Smallest angle between two bearings, 0..180.</summary>
    public static float Separation(float a, float b)
    {
        var d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    private static double Bearing(double dx, double dy) => (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360;

    /// <summary>
    /// Direction in which a concentration's light is most relevant: the centre of the 30° window with the
    /// most proximity-weighted light, refined to the weighted mean bearing within it. A concentration that
    /// wraps around the site points at its densest part, never at an average between its arms.
    /// </summary>
    private static double PeakBearing(double[] profile)
    {
        const int half = PeakWindowDegrees / 2;
        int best = 0; double bestSum = -1;
        for (var b = 0; b < 360; b++)
        {
            double sum = 0;
            for (var o = -half; o <= half; o++) sum += profile[(b + o + 360) % 360];
            if (sum > bestSum + 1e-9) { bestSum = sum; best = b; }
        }
        double offset = 0;
        for (var o = -half; o <= half; o++) offset += profile[(best + o + 360) % 360] * (o + .5);
        return (best + offset / bestSum + 360) % 360;
    }

    /// <summary>
    /// Angular width of a concentration: the light-weighted 10th to 90th percentile of its cells' bearings
    /// about its centre bearing. Scaling every cell's brightness equally leaves it unchanged.
    /// </summary>
    private static double Spread(byte[] field, List<int> cells, int width, double siteX, double siteY, double centre)
    {
        var offsets = cells.Select(i =>
        {
            var b = Bearing(i % width + .5 - siteX, i / width + .5 - siteY);
            var offset = (b - centre + 540) % 360 - 180;
            return (Offset: offset, Weight: (double)field[i]);
        }).OrderBy(o => o.Offset).ToArray();
        var total = offsets.Sum(o => o.Weight);
        double Percentile(double p)
        {
            var target = p * total;
            double running = 0;
            foreach (var o in offsets) { running += o.Weight; if (running >= target) return o.Offset; }
            return offsets[^1].Offset;
        }
        return Math.Max(0, Percentile(.9) - Percentile(.1));
    }
}
