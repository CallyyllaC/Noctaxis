using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Locations.Descriptions;

/// <summary>A geographic position without elevation, for feature geometry.</summary>
public readonly record struct GeographicPoint(double Latitude, double Longitude);

/// <summary>
/// What a named (or evidence) feature is. The description engine ranks and phrases features by kind; a source maps its
/// own classes onto these. <see cref="BuiltUpArea"/> and <see cref="WaterArea"/> are anonymous evidence (built-up land,
/// open water) used to decide containment and banks; they are never named in a description.
/// </summary>
public enum GeographicFeatureKind
{
    City,
    Town,
    Village,
    Hamlet,
    Suburb,
    Neighbourhood,
    River,
    Canal,
    Stream,
    Lake,
    Peak,
    Ridge,
    ProtectedArea,
    Landmark,
    PlaceOfWorship,
    BuiltUpArea,
    WaterArea
}

public enum GeographicGeometryKind
{
    /// <summary>Each part is one point.</summary>
    Point,
    /// <summary>Each part is a polyline.</summary>
    Line,
    /// <summary>Parts are rings, exterior and holes alike, combined by the even-odd rule.</summary>
    Area
}

/// <summary>
/// One feature from a provider-neutral source. Geometry may be clipped to the area the source covers, so a line's
/// ends are not assumed to be the feature's real ends.
/// </summary>
/// <param name="Id">Stable within one source and one query: equal inputs give equal identifiers.</param>
/// <param name="DetailMetres">The geometry's approximate generalisation tolerance; claims finer than this are not made.</param>
/// <param name="Prominence">The source's own weighting within the kind (0..1), e.g. a castle above a museum.</param>
public sealed record GeographicFeature(
    string Id,
    string? Name,
    GeographicFeatureKind Kind,
    GeographicGeometryKind Geometry,
    IReadOnlyList<GeographicPoint[]> Parts,
    string SourceId,
    double DetailMetres,
    double Prominence = 1)
{
    public bool IsEvidence => Kind is GeographicFeatureKind.BuiltUpArea or GeographicFeatureKind.WaterArea;
    public bool IsSettlement => Kind is GeographicFeatureKind.City or GeographicFeatureKind.Town or
        GeographicFeatureKind.Village or GeographicFeatureKind.Hamlet or GeographicFeatureKind.Suburb or
        GeographicFeatureKind.Neighbourhood;
}

public enum GeographicCoverage
{
    /// <summary>Every part of the requested area was available.</summary>
    Complete,
    /// <summary>Some of the area was unavailable; what is present is real, but absences mean nothing.</summary>
    Partial,
    /// <summary>Nothing could be read.</summary>
    Unavailable
}

/// <summary>
/// Features around a position. <paramref name="CoverageRadiusMetres"/> is how far from the position the source holds
/// data in every direction; nothing farther is described. An absent feature is never proof that none exists.
/// </summary>
public sealed record GeographicFeatureSet(
    IReadOnlyList<GeographicFeature> Features,
    GeographicCoverage Coverage,
    double CoverageRadiusMetres,
    string Attribution)
{
    public static GeographicFeatureSet Unavailable(string attribution) => new([], GeographicCoverage.Unavailable, 0, attribution);
}

/// <summary>Feature discovery: candidate features around a position, from whatever data a source holds.</summary>
public interface IGeographicFeatureSource
{
    Task<GeographicFeatureSet> GetFeaturesAsync(GeoCoordinate centre, CancellationToken cancellationToken);
}

/// <summary>How the position relates to a feature.</summary>
public enum GeographicRelation
{
    /// <summary>Inside the feature (a settlement's built-up area, a reserve).</summary>
    In,
    /// <summary>Beside a watercourse or water body.</summary>
    By,
    /// <summary>On one bank of a watercourse, where the geometry supports the side.</summary>
    OnBank,
    /// <summary>Just beyond a settlement's edge.</summary>
    Outside,
    Near,
    /// <summary>Farther away, in a compass direction from the feature.</summary>
    DirectionOf
}

public enum CompassPoint { North, NorthEast, East, SouthEast, South, SouthWest, West, NorthWest }

public enum DescriptionConfidence { Low, Medium, High }

/// <summary>A structured relationship between the position and one feature, kept beside the composed text.</summary>
/// <param name="Direction">For <see cref="GeographicRelation.DirectionOf"/>, where the position lies from the feature;
/// for <see cref="GeographicRelation.OnBank"/>, the bank.</param>
/// <param name="BearingFromFeatureDegrees">Compass bearing from the feature's nearest point to the position.</param>
public sealed record FeatureRelationship(
    string FeatureId,
    string Name,
    GeographicFeatureKind Kind,
    GeographicRelation Relation,
    CompassPoint? Direction,
    double DistanceMetres,
    double BearingFromFeatureDegrees,
    double Score,
    DescriptionConfidence Confidence,
    string Evidence);

/// <summary>
/// A contextual, human-readable description of a position. It is a label, never a measurement: the exact coordinate
/// stays authoritative for every calculation.
/// </summary>
public sealed record LocationDescription(
    GeoCoordinate Coordinate,
    string Text,
    FeatureRelationship Primary,
    FeatureRelationship? Secondary,
    GeographicCoverage Coverage,
    string Attribution);
