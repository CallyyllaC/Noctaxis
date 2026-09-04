using Noctaxis.Core.Environment;

namespace Noctaxis.Desktop.Services;

public enum SettlementStarClass { Faint, Common, Medium, Bright }

public readonly record struct SettlementStar(
    float X, float Y, float Radius, float Brightness, byte Red, byte Green, byte Blue,
    SettlementStarClass Class, ulong StableSeed);

/// <summary>
/// Creates deterministic illustrative stars from WSF settlement mass. These are visual samples,
/// not claims that a cell or star represents one literal building.
/// </summary>
public sealed class SettlementStarGenerator
{
    public IReadOnlyList<SettlementStar> Generate(SettlementRaster settlement, int outputWidth, int outputHeight)
    {
        var centre = settlement.Grid.Bounds;
        var viewport = new WebMercatorViewport(0, 256, outputWidth, outputHeight,
            (centre.North + centre.South) / 2, (centre.East + centre.West) / 2);
        return Generate(settlement, outputWidth, outputHeight,
            SettlementGalaxyRenderContext.Anonymous(viewport), SettlementGalaxyStyle.DefaultV1);
    }

    public IReadOnlyList<SettlementStar> Generate(SettlementRaster settlement, int outputWidth, int outputHeight,
        SettlementGalaxyRenderContext context, SettlementGalaxyStyle style)
    {
        var width = settlement.Grid.Width;
        var height = settlement.Grid.Height;
        var transform = SettlementGlowGeometryCalculator.ThumbnailTransform.Create(
            width / SettlementDensityBuilder.Supersampling, height / SettlementDensityBuilder.Supersampling,
            outputWidth, outputHeight);
        var stars = new List<SettlementStar>();
        var masses = new float[settlement.BuildingFraction.Length];
        double totalMass = 0;
        for (var index = 0; index < masses.Length; index++)
        {
            masses[index] = SettlementDensityBuilder.SettlementMass(settlement.BuildingFraction[index],
                settlement.BuildingHeightMetres[index]);
            totalMass += masses[index];
        }
        var targetCount = totalMass <= 0 ? 0 : Math.Min(style.Stars.MaxSettlementStars,
            Math.Max(1, (int)Math.Round(totalMass * style.Stars.TargetSettlementStarDensity)));
        for (var index = 0; index < settlement.BuildingFraction.Length; index++)
        {
            var mass = masses[index];
            if (mass <= 0) continue;
            var xCell = index % width;
            var yCell = index / width;
            var stableId = $"{settlement.DatasetId}:{settlement.DatasetVersion}:{xCell}:{yCell}";
            var seed = SettlementGalaxyDeterminism.DeriveSeed(stableId, context, style);
            var selectionProbability = Math.Min(1, targetCount * mass / Math.Max(totalMass, 1e-12));
            if (SettlementGalaxyDeterminism.Unit(seed, 0) > selectionProbability) continue;

            var x = xCell + SettlementGalaxyDeterminism.Unit(seed, 1) - .5;
            var y = yCell + SettlementGalaxyDeterminism.Unit(seed, 2) - .5;
            var point = transform.MapHighResolution(x, y);
            if (point.X < -8 || point.X >= outputWidth + 8 || point.Y < -8 || point.Y >= outputHeight + 8) continue;

            var classUnit = SettlementGalaxyDeterminism.Unit(
                SettlementGalaxyDeterminism.DeriveSeed("pass07-class:" + stableId, context, style));
            var starClass = classUnit < style.Stars.ClassThresholds.FaintMax ? SettlementStarClass.Faint
                : classUnit < style.Stars.ClassThresholds.CommonMax ? SettlementStarClass.Common
                : classUnit < style.Stars.ClassThresholds.MediumMax ? SettlementStarClass.Medium
                : SettlementStarClass.Bright;
            var baseRadius = style.Stars.BaseRadius;
            var size = Lerp(style.Stars.SizeMinPercent, style.Stars.SizeMaxPercent,
                SettlementGalaxyDeterminism.Unit(
                    SettlementGalaxyDeterminism.DeriveSeed("pass07-size:" + stableId, context, style))) / 100;
            var classGain = starClass switch
            {
                SettlementStarClass.Faint => style.Stars.ClassGains.Faint,
                SettlementStarClass.Common => style.Stars.ClassGains.Common,
                SettlementStarClass.Medium => style.Stars.ClassGains.Medium,
                _ => style.Stars.ClassGains.Bright
            };
            // The reference assigns a fixed brightness to each stable class. WSF height already has a
            // bounded secondary effect on selection mass and must not inflate Pass 7 luminosity.
            var brightness = classGain;
            var colour = SelectColour(SettlementGalaxyDeterminism.DeriveSeed(
                "pass08-family:" + stableId, context, style), style.Stars.ColourVariation);
            stars.Add(new SettlementStar((float)point.X, (float)point.Y, (float)(baseRadius * size),
                (float)brightness, colour.R, colour.G, colour.B, starClass, seed));
        }
        return stars.OrderBy(value => value.StableSeed).ThenBy(value => value.Y).ThenBy(value => value.X)
            .Take(style.Stars.MaxSettlementStars).ToArray();
    }

    private static (byte R, byte G, byte B) SelectColour(ulong seed, StarColourStyle style)
    {
        var family = SettlementGalaxyDeterminism.Unit(seed);
        if (style.Families.Length == 0)
            throw new InvalidDataException("The settlement star colour-family preset is empty.");
        var selected = style.Families[^1];
        foreach (var candidate in style.Families)
        {
            if (family > candidate.Ceiling) continue;
            selected = candidate;
            break;
        }
        return ((byte)selected.Colour[0], (byte)selected.Colour[1], (byte)selected.Colour[2]);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
