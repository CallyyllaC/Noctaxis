using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Locations.Descriptions;

/// <summary>
/// How far a kind of feature stays relevant, on a human scale rather than a single universal threshold: a named river
/// several hundred metres away is still "near", a stream is not, and a town reaches farther than a hamlet.
/// </summary>
/// <param name="ByMetres">Watercourses and water: close enough to be "by" it.</param>
/// <param name="CoreMetres">Settlements: the distance from the place point within which a position can be in it or
/// just outside it.</param>
/// <param name="DirectionMetres">How far a compass direction from the feature still means something; 0 for never.</param>
public readonly record struct RelevanceProfile(
    double Significance, double ByMetres, double CoreMetres, double NearMetres, double DirectionMetres);

/// <summary>
/// Candidate ranking and selection for location descriptions. Deterministic and explicit: the same features and
/// position always give the same description, built only from features that exist in the data.
/// </summary>
public static class LocationDescriptionEngine
{
    /// <summary>Below this score nothing is worth saying, and the caller keeps showing coordinates.</summary>
    public const double MinimumScore = .12;
    /// <summary>A second feature is added only while it is at least this share of the first one's relevance.</summary>
    public const double SecondaryShare = .3;
    /// <summary>A position this close to built-up land counts as being on it (a road between two blocks).</summary>
    public const double BuiltUpToleranceMetres = 30;

    public static RelevanceProfile Profile(GeographicFeatureKind kind) => kind switch
    {
        GeographicFeatureKind.City => new(1.0, 0, 3000, 8000, 15000),
        GeographicFeatureKind.Town => new(.85, 0, 1200, 3500, 7000),
        GeographicFeatureKind.Village => new(.6, 0, 450, 1500, 3000),
        GeographicFeatureKind.Hamlet => new(.4, 0, 200, 700, 1200),
        GeographicFeatureKind.Suburb => new(.5, 0, 500, 900, 0),
        GeographicFeatureKind.Neighbourhood => new(.35, 0, 250, 500, 0),
        GeographicFeatureKind.River => new(.85, 120, 0, 700, 2000),
        GeographicFeatureKind.Canal => new(.75, 100, 0, 500, 1500),
        GeographicFeatureKind.Stream => new(.35, 50, 0, 200, 0),
        GeographicFeatureKind.Lake => new(.6, 100, 0, 500, 1500),
        GeographicFeatureKind.Peak => new(.65, 0, 0, 800, 2500),
        GeographicFeatureKind.Ridge => new(.55, 0, 0, 600, 1500),
        GeographicFeatureKind.ProtectedArea => new(.45, 0, 0, 300, 0),
        GeographicFeatureKind.Landmark => new(.7, 0, 0, 500, 1500),
        GeographicFeatureKind.PlaceOfWorship => new(.35, 0, 0, 250, 0),
        _ => default
    };

    /// <summary>The description of <paramref name="observer"/>, or null when the data supports none.</summary>
    public static LocationDescription? Describe(GeoCoordinate observer, GeographicFeatureSet set,
        CancellationToken cancellationToken = default)
    {
        var relationships = Relate(observer, set, cancellationToken);
        var (primary, secondary) = Select(relationships);
        if (primary is null) return null;
        // Features come first and settlement context after: "Near the River Ancholme, outside Saxby".
        if (secondary is not null && IsSettlement(primary.Kind) && !IsSettlement(secondary.Kind))
            (primary, secondary) = (secondary, primary);
        return new LocationDescription(observer, LocationDescriptionComposer.Compose(primary, secondary), primary,
            secondary, set.Coverage, set.Attribution);
    }

    /// <summary>Every named feature's best relationship to the position, strongest first, one per feature name.</summary>
    public static IReadOnlyList<FeatureRelationship> Relate(GeoCoordinate observer, GeographicFeatureSet set,
        CancellationToken cancellationToken = default)
    {
        if (set.Coverage == GeographicCoverage.Unavailable) return [];
        var builtUp = false;
        var inWater = false;
        foreach (var evidence in set.Features.Where(feature => feature.IsEvidence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Each answer is a yes/no: once known, no further polygon of that kind needs measuring (open sea can
            // bring several very large water polygons).
            if (evidence.Kind == GeographicFeatureKind.BuiltUpArea ? builtUp : inWater) continue;
            var proximity = GeographicSpatialAnalysis.Measure(evidence, observer);
            if (evidence.Kind == GeographicFeatureKind.BuiltUpArea && proximity.DistanceMetres <= BuiltUpToleranceMetres)
                builtUp = true;
            if (evidence.Kind == GeographicFeatureKind.WaterArea && proximity.Inside) inWater = true;
        }

        var best = new Dictionary<(GeographicFeatureKind, string), FeatureRelationship>();
        foreach (var feature in set.Features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (feature.IsEvidence || string.IsNullOrWhiteSpace(feature.Name) || feature.Parts.Count == 0) continue;
            var relationship = RelateOne(observer, feature, set, builtUp, inWater);
            if (relationship is null) continue;
            var key = (feature.Kind, feature.Name.Trim().ToUpperInvariant());
            if (!best.TryGetValue(key, out var existing) || Better(relationship, existing)) best[key] = relationship;
        }
        return best.Values.OrderByDescending(r => r.Score).ThenBy(r => r.DistanceMetres)
            .ThenBy(r => r.FeatureId, StringComparer.Ordinal).ToArray();
    }

    private static bool Better(FeatureRelationship candidate, FeatureRelationship existing) =>
        candidate.Score > existing.Score ||
        (candidate.Score == existing.Score && (candidate.DistanceMetres < existing.DistanceMetres ||
            (candidate.DistanceMetres == existing.DistanceMetres &&
             string.CompareOrdinal(candidate.FeatureId, existing.FeatureId) < 0)));

    private static (FeatureRelationship? Primary, FeatureRelationship? Secondary) Select(
        IReadOnlyList<FeatureRelationship> ranked)
    {
        var primary = ranked.FirstOrDefault(r => r.Score >= MinimumScore);
        if (primary is null) return (null, null);
        // The second part adds the other kind of context, settlement beside landmark, never a repetition of the first.
        var wantSettlement = !IsSettlement(primary.Kind);
        var secondary = ranked.FirstOrDefault(r => r != primary && IsSettlement(r.Kind) == wantSettlement &&
            r.Score >= MinimumScore && r.Score >= primary.Score * SecondaryShare &&
            r.Relation != GeographicRelation.DirectionOf &&
            !string.Equals(r.Name, primary.Name, StringComparison.OrdinalIgnoreCase));
        return (primary, secondary);
    }

    private static FeatureRelationship? RelateOne(GeoCoordinate observer, GeographicFeature feature,
        GeographicFeatureSet set, bool builtUp, bool inWater)
    {
        var profile = Profile(feature.Kind);
        if (profile.Significance <= 0) return null;
        var proximity = GeographicSpatialAnalysis.Measure(feature, observer);
        var distance = proximity.DistanceMetres;
        if (!double.IsFinite(distance)) return null;
        var bearing = proximity.Inside ? 0 : GeographicSpatialAnalysis.Bearing(proximity.Nearest * -1);
        var significance = profile.Significance * Math.Clamp(feature.Prominence, 0, 1);
        var partialPenalty = set.Coverage == GeographicCoverage.Partial ? .85 : 1;
        // A line's closest point at a part's end may be where the data was clipped, not where the feature ends:
        // no direction is claimed from there.
        var atClippedEnd = feature.Geometry == GeographicGeometryKind.Line &&
            (proximity.AlongPartMetres < 1 || proximity.PartLengthMetres - proximity.AlongPartMetres < 1);

        GeographicRelation relation;
        CompassPoint? direction = null;
        double weight;
        string evidence;
        if (feature.IsSettlement)
        {
            if (distance <= profile.CoreMetres && builtUp)
                (relation, weight, evidence) = (GeographicRelation.In, 1, "within the place's core distance, on built-up land");
            else if (distance <= profile.CoreMetres)
                (relation, weight, evidence) = (GeographicRelation.Outside, .8, "within the place's core distance, not on built-up land");
            else if (distance <= profile.NearMetres)
                (relation, weight, evidence) = (GeographicRelation.Near,
                    Interpolate(distance, profile.CoreMetres, profile.NearMetres, .8, .5), "within the place's near distance");
            else if (profile.DirectionMetres > 0 && distance <= profile.DirectionMetres)
                (relation, weight, evidence) = (GeographicRelation.DirectionOf,
                    Interpolate(distance, profile.NearMetres, profile.DirectionMetres, .5, .15), "beyond the place's near distance");
            else return null;
        }
        else if (proximity.Inside)
        {
            // Inside an area feature: a reserve contains the position; a lake does not describe it.
            if (feature.Kind != GeographicFeatureKind.ProtectedArea) return null;
            relation = GeographicRelation.In;
            weight = 1;
            // Containment in a large designated area says little; a meaningful landmark should outrank it.
            significance *= AreaSignificance(feature, observer);
            evidence = "inside the area";
        }
        else if (profile.ByMetres > 0 && distance <= profile.ByMetres)
        {
            weight = 1;
            var bank = inWater ? null : GeographicSpatialAnalysis.Bank(feature, observer, proximity);
            if (bank is not null && feature.Kind is GeographicFeatureKind.River or GeographicFeatureKind.Canal)
            {
                relation = GeographicRelation.OnBank;
                direction = bank;
                evidence = "beside a straight run of the course, clear of clipped ends";
            }
            else
            {
                relation = GeographicRelation.By;
                evidence = "within the feature's 'by' distance";
            }
        }
        else if (distance <= profile.NearMetres)
        {
            relation = GeographicRelation.Near;
            weight = Interpolate(distance, profile.ByMetres, profile.NearMetres, 1, .55);
            evidence = "within the feature's near distance";
        }
        else if (profile.DirectionMetres > 0 && distance <= profile.DirectionMetres && !atClippedEnd)
        {
            relation = GeographicRelation.DirectionOf;
            weight = Interpolate(distance, profile.NearMetres, profile.DirectionMetres, .55, .15);
            evidence = "beyond the near distance, in a clear direction";
        }
        else return null;

        // Nothing is claimed beyond the area the source holds in every direction.
        if (relation is GeographicRelation.DirectionOf or GeographicRelation.Near && distance > set.CoverageRadiusMetres)
            return null;
        if (relation == GeographicRelation.DirectionOf) direction = GeographicSpatialAnalysis.ToCompass(bearing);

        var confidence = relation switch
        {
            GeographicRelation.DirectionOf => DescriptionConfidence.Low,
            _ when feature.Geometry == GeographicGeometryKind.Point => DescriptionConfidence.Medium,
            _ => DescriptionConfidence.High
        };
        if (set.Coverage == GeographicCoverage.Partial && confidence > DescriptionConfidence.Low) confidence--;
        return new FeatureRelationship(feature.Id, feature.Name!.Trim(), feature.Kind, relation, direction, distance,
            bearing, significance * weight * partialPenalty, confidence, evidence);
    }

    private static double AreaSignificance(GeographicFeature feature, GeoCoordinate observer)
    {
        var area = 0d;
        foreach (var ring in feature.Parts)
        {
            var points = ring.Select(point => GeographicSpatialAnalysis.Project(observer, point)).ToArray();
            var sum = 0d;
            for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
                sum += points[j].East * points[i].North - points[i].East * points[j].North;
            area += Math.Abs(sum) / 2;
        }
        var squareKilometres = area / 1e6;
        return squareKilometres switch { < 5 => 1, < 50 => .6, _ => .3 };
    }

    private static double Interpolate(double value, double from, double to, double atFrom, double atTo) =>
        to <= from ? atTo : atFrom + (atTo - atFrom) * Math.Clamp((value - from) / (to - from), 0, 1);

    private static bool IsSettlement(GeographicFeatureKind kind) => kind is GeographicFeatureKind.City or
        GeographicFeatureKind.Town or GeographicFeatureKind.Village or GeographicFeatureKind.Hamlet or
        GeographicFeatureKind.Suburb or GeographicFeatureKind.Neighbourhood;
}
