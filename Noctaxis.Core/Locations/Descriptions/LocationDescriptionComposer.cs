using Noctaxis.Core.Localisation;

namespace Noctaxis.Core.Locations.Descriptions;

/// <summary>
/// Description composition: localised text from structured relationships. Every phrase is a resource
/// (<c>Places_Description_*</c>), so a language decides its own word order, articles and compass words.
/// </summary>
public static class LocationDescriptionComposer
{
    public static string Compose(FeatureRelationship primary, FeatureRelationship? secondary)
    {
        var first = Phrase(primary, "Places_Description_Primary_");
        return secondary is null
            ? first
            : Strings.Format("Places_Description_Combined_Format", first, Phrase(secondary, "Places_Description_Secondary_"));
    }

    private static string Phrase(FeatureRelationship relationship, string family)
    {
        var name = NameOf(relationship);
        return relationship.Relation switch
        {
            GeographicRelation.DirectionOf => Strings.Format(family + "DirectionOf_Format",
                Compass(relationship.Direction ?? CompassPoint.North, family == "Places_Description_Primary_"), name),
            GeographicRelation.OnBank => Strings.Format(family + "OnBank_Format",
                Compass(relationship.Direction ?? CompassPoint.North, capitalised: false), name),
            _ => Strings.Format(family + relationship.Relation + "_Format", name)
        };
    }

    /// <summary>Rivers and canals take the definite article in English ("the River Ancholme"); other names stand alone.</summary>
    private static string NameOf(FeatureRelationship relationship) =>
        relationship.Kind is GeographicFeatureKind.River or GeographicFeatureKind.Canal &&
        !relationship.Name.StartsWith("The ", StringComparison.OrdinalIgnoreCase)
            ? Strings.Format("Places_Description_WatercourseName_Format", relationship.Name)
            : relationship.Name;

    private static string Compass(CompassPoint point, bool capitalised) =>
        Strings.Get((capitalised ? "Places_Description_Compass_" : "Places_Description_CompassLower_") + point);
}
