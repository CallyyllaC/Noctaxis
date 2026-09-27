using Avalonia.Media;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Mapping.Preview;
using Noctaxis.Desktop.Themes;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

/// <summary>Light-concentration detection and the scan-ring light sectors that show it.</summary>
public sealed class LocationPreviewLightSourceTests
{
    // A scene-sized settlement field (mask resolution) seen from its centre.
    private const int Width = 448, Height = 252;
    private const float SiteX = Width / 2f, SiteY = Height / 2f;

    private static byte[] Field() => new byte[Width * Height];

    private static (double X, double Y) At(float bearing, float distanceFraction)
    {
        var radians = bearing * Math.PI / 180;
        var distance = distanceFraction * Height;
        return (SiteX + Math.Sin(radians) * distance, SiteY - Math.Cos(radians) * distance);
    }

    /// <summary>A compact settlement: a lit disc.</summary>
    private static byte[] Blob(byte[] field, float bearing, float distanceFraction, int radius, byte value = 220)
    {
        var (cx, cy) = At(bearing, distanceFraction);
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
            if ((x + .5 - cx) * (x + .5 - cx) + (y + .5 - cy) * (y + .5 - cy) <= radius * radius) field[y * Width + x] = value;
        return field;
    }

    /// <summary>A broad glow: a lit band spanning ±<paramref name="halfSpan"/> degrees at one distance.</summary>
    private static byte[] Band(byte[] field, float bearing, float distanceFraction, float halfSpan, float thickness, byte value = 220)
    {
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            double dx = x + .5 - SiteX, dy = y + .5 - SiteY;
            var b = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360;
            var offset = Math.Abs((b - bearing + 540) % 360 - 180);
            var d = Math.Sqrt(dx * dx + dy * dy) / Height;
            if (offset <= halfSpan && Math.Abs(d - distanceFraction) <= thickness) field[y * Width + x] = value;
        }
        return field;
    }

    private static IReadOnlyList<LocationPreviewLightSource> Find(byte[] field) =>
        LocationPreviewLightSources.Find(field, Width, Height, SiteX, SiteY, Height);

    [Fact]
    public void OneDominantCompactSourceGivesOneSectorAtItsBearing()
    {
        var source = Assert.Single(Find(Blob(Field(), 60, .35f, 10)));
        Assert.InRange(source.BearingDegrees, 58, 62);
        Assert.Equal(1, source.Strength);
        Assert.InRange(source.DistanceFraction, .33f, .37f);
    }

    [Fact]
    public void TwoStrongSourcesMoreThan90DegreesApartGiveTwoSectors()
    {
        var sources = Find(Blob(Blob(Field(), 30, .35f, 12), 200, .35f, 10));
        Assert.Equal(2, sources.Count);
        Assert.InRange(sources[0].BearingDegrees, 28, 32); // The larger town first.
        Assert.InRange(sources[1].BearingDegrees, 198, 202);
        Assert.True(sources[1].Strength < 1);
    }

    [Fact]
    public void ThreeQualifyingSourcesAreAllShownAndAFourthIsIgnored()
    {
        var three = Find(Blob(Blob(Blob(Field(), 0, .35f, 12), 120, .35f, 11), 240, .35f, 10));
        Assert.Equal(3, three.Count);
        // Four mutually separated sources: only the three most relevant are kept.
        var four = Find(Blob(Blob(Blob(Blob(Field(), 0, .35f, 13), 90, .35f, 12), 180, .35f, 11), 270, .35f, 10));
        Assert.Equal(LocationPreviewLightSources.MaximumSources, four.Count);
        Assert.DoesNotContain(four, s => Math.Abs(s.BearingDegrees - 270) < 5);
    }

    [Fact]
    public void SourcesLessThan90DegreesApartShowOnlyTheDominantOne()
    {
        // Two towns 60° apart are one direction of light, not two; and they are never averaged.
        var source = Assert.Single(Find(Blob(Blob(Field(), 30, .35f, 12), 90, .35f, 9)));
        Assert.InRange(source.BearingDegrees, 28, 32);
    }

    [Fact]
    public void LightAtTheSiteIsSuppressed()
    {
        Assert.Empty(Find(Blob(Field(), 0, 0, 20)));                          // The site sits inside the light.
        // A sizeable town centred just beside the site (but not covering it) is still the site's own light.
        Assert.Empty(Find(Blob(Field(), 45, LocationPreviewLightSources.AtSiteFraction * .8f, 7)));
        // The same town farther out is a light source.
        Assert.Single(Find(Blob(Field(), 45, .3f, 7)));
    }

    [Fact]
    public void LightTheSiteSitsInOutshinesLesserLightElsewhere()
    {
        // A village at the site with scattered fragments around it (the Tomintoul case): no indicator.
        var village = Blob(Blob(Blob(Field(), 0, 0, 22), 250, .16f, 8), 100, .25f, 6);
        Assert.Empty(Find(village));
        // A town brighter than the site's own light still shows from inside a small village.
        var town = Assert.Single(Find(Blob(Blob(Field(), 0, 0, 10), 140, .35f, 20)));
        Assert.InRange(town.BearingDegrees, 138, 142);
    }

    [Fact]
    public void ConnectedSprawlPointsAtItsDensestPartNotAnAverage()
    {
        // A large town at 60° joined by a thin lit arc, round through 130°, to a smaller one at 200°.
        // Its light-weighted centre lies between them; the indicator must point at the town.
        var field = Band(Blob(Blob(Field(), 60, .35f, 16), 200, .35f, 11), 130, .35f, 70, .008f);
        var source = Assert.Single(Find(field));
        Assert.InRange(source.BearingDegrees, 56, 64);
        Assert.True(source.SpreadDegrees > 60, $"{source.SpreadDegrees:F0}");
    }

    [Fact]
    public void WeakSourcesAreSuppressed()
    {
        Assert.Empty(Find(Blob(Field(), 60, .35f, 3)));                       // A hamlet: below the meaningful mass.
        Assert.Empty(Find(Blob(Field(), 60, .35f, 12, LocationPreviewLightSources.LitThreshold - 1))); // Below lit.
        // A minor cluster far weaker than the main source adds nothing, even when well separated.
        var sources = Find(Blob(Blob(Field(), 0, .3f, 16), 180, .6f, 5));
        Assert.Single(sources);
    }

    [Fact]
    public void AngularSpreadFollowsHowBroadTheLightIs()
    {
        var compact = Assert.Single(Find(Blob(Field(), 90, .4f, 8)));
        var broad = Assert.Single(Find(Band(Field(), 90, .4f, 30, .03f)));
        Assert.True(broad.SpreadDegrees > compact.SpreadDegrees * 2, $"{broad.SpreadDegrees:F1} vs {compact.SpreadDegrees:F1}");
        Assert.InRange(broad.SpreadDegrees, 40, 60);   // 10th–90th percentile of a uniform ±30° band.
        // The drawn sector follows the spread, within its readable limits.
        var site = new SKPoint(200, 120);
        var narrow = LocationPreviewRenderer.LightSector(site, 72, compact, 1);
        var wide = LocationPreviewRenderer.LightSector(site, 72, broad, 1);
        Assert.True(wide.SweepDegrees > narrow.SweepDegrees);
        Assert.InRange(narrow.SweepDegrees, LocationPreviewRenderer.MinimumSectorDegrees, LocationPreviewRenderer.MaximumSectorDegrees);
        Assert.Equal(LocationPreviewRenderer.MaximumSectorDegrees,
            LocationPreviewRenderer.LightSector(site, 72, broad with { SpreadDegrees = 170 }, 1).SweepDegrees);
        Assert.Equal(LocationPreviewRenderer.MinimumSectorDegrees,
            LocationPreviewRenderer.LightSector(site, 72, broad with { SpreadDegrees = 1 }, 1).SweepDegrees);
    }

    [Fact]
    public void StrengthDoesNotChangeTheMeasuredSpread()
    {
        var dim = Assert.Single(Find(Band(Field(), 150, .4f, 20, .03f, 120)));
        var bright = Assert.Single(Find(Band(Field(), 150, .4f, 20, .03f, 240)));
        Assert.Equal(dim.SpreadDegrees, bright.SpreadDegrees, 1);
        Assert.Equal(dim.BearingDegrees, bright.BearingDegrees, 1);
        Assert.True(bright.Relevance > dim.Relevance * 1.8);
        // Nor does the sector length: only opacity and weight follow strength.
        var site = new SKPoint(200, 120);
        Assert.Equal(LocationPreviewRenderer.LightSector(site, 72, dim with { Strength = .2f }, 1).SweepDegrees,
            LocationPreviewRenderer.LightSector(site, 72, dim with { Strength = 1 }, 1).SweepDegrees);
    }

    [Theory]
    [InlineData(0f)] [InlineData(37f)] [InlineData(90f)] [InlineData(200f)] [InlineData(315f)]
    public void CaretSitsOnTheBearingAndPointsOutward(float bearing)
    {
        var site = new SKPoint(200, 120);
        foreach (var unit in new[] { 1f, 2f })
        {
            var source = new LocationPreviewLightSource(bearing, 30, 1, 100, .3f);
            var g = LocationPreviewRenderer.LightSector(site, 72 * unit, source, unit);
            var radians = bearing * MathF.PI / 180;
            // Apex just outside the ring on the bearing; the sector is centred on it.
            Assert.Equal(site.X + MathF.Sin(radians) * (72 + 10) * unit, g.Apex.X, 3);
            Assert.Equal(site.Y - MathF.Cos(radians) * (72 + 10) * unit, g.Apex.Y, 3);
            Assert.Equal(bearing, g.StartDegrees + g.SweepDegrees / 2, 3);
            // Outward: from the base towards the apex, away from the site, along the bearing.
            var baseCentre = new SKPoint((g.BaseLeft.X + g.BaseRight.X) / 2, (g.BaseLeft.Y + g.BaseRight.Y) / 2);
            var pointing = new SKPoint(g.Apex.X - baseCentre.X, g.Apex.Y - baseCentre.Y);
            Assert.Equal(5 * unit, pointing.Length, 3);
            Assert.Equal(1, (pointing.X * g.Outward.X + pointing.Y * g.Outward.Y) / pointing.Length, 4);
            Assert.True(SKPoint.Distance(g.Apex, site) > SKPoint.Distance(baseCentre, site));
            // A compact, symmetric caret that scales with the preview.
            Assert.Equal(7.2f * unit, SKPoint.Distance(g.BaseLeft, g.BaseRight), 3);
        }
    }

    [Fact]
    public void DetectionIsDeterministic()
    {
        var field = Blob(Blob(Band(Field(), 20, .4f, 25, .03f), 140, .35f, 9), 260, .3f, 11);
        Assert.Equal(Find(field), Find((byte[])field.Clone()));
    }

    private static LocationPreviewScene SceneWith(byte[] settlement)
    {
        var count = settlement.Length;
        return new LocationPreviewScene(Guid.Parse("015cb354-786a-45ab-acd4-6f8502813731"), Width * 2, Height * 2, new SKPoint(Width, Height),
            new byte[count], new byte[count], new byte[count], settlement, [], [], "light-test:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(settlement)));
    }

    [Theory]
    [InlineData("supporter.noctaxis-neon", AppearanceMode.Dark)]
    [InlineData("builtin.highcontrast", AppearanceMode.Dark)] [InlineData("builtin.highcontrast", AppearanceMode.Light)]
    [InlineData("supporter.foxfire", AppearanceMode.Light)]
    public void SectorsAndCaretsAreDrawnAtEachLightBearing(string id, AppearanceMode appearance)
    {
        var scene = SceneWith(Blob(Blob(Field(), 60, .35f, 12), 220, .35f, 10));
        Assert.Equal(2, scene.Atmosphere.LightSources.Count);
        var style = LocationPreviewExport.StyleFor(new ThemeCatalogue(isAvailable: _ => true), id, appearance, ColourVisionMode.None);
        using var bitmap = LocationPreviewRenderer.Render(scene, style, 400, 240);
        var unit = 240 / LocationPreviewRenderer.ReferenceHeight;
        var light = style.IsHighContrast ? style.Instrument : style.Glow;
        Color Pixel(SKPoint p) { var c = bitmap.GetPixel((int)Math.Round(p.X), (int)Math.Round(p.Y)); return Color.FromRgb(c.Red, c.Green, c.Blue); }
        var lightColour = Color.FromRgb(light.Red, light.Green, light.Blue);
        foreach (var source in scene.Atmosphere.LightSources)
        {
            var g = LocationPreviewRenderer.LightSector(new SKPoint(200, 120), 240 * .3f, source, unit);
            // On the caret's stroke, a little in from the apex.
            var onCaret = new SKPoint(g.Apex.X * .8f + g.BaseLeft.X * .2f, g.Apex.Y * .8f + g.BaseLeft.Y * .2f);
            Assert.True(ColourScience.DeltaE(Pixel(onCaret), lightColour) < 25, $"{id}/{appearance}: caret at {source.BearingDegrees:F0}°");
        }
        if (id == "supporter.noctaxis-neon")
        {
            // The lit sector takes the light colour; the rest of the ring keeps the instrument colour.
            SKPoint Ring(float bearing) => new(200 + MathF.Sin(bearing * MathF.PI / 180) * 72, 120 - MathF.Cos(bearing * MathF.PI / 180) * 72);
            var bearing = scene.Atmosphere.LightSources[0].BearingDegrees;
            // The ring is a hairline at this size: take the brightest pixel under it.
            Color Brightest(SKPoint p) => Enumerable.Range(-1, 3).SelectMany(dy => Enumerable.Range(-1, 3).Select(dx => Pixel(new(p.X + dx, p.Y + dy))))
                .MaxBy(ColourScience.Luminance);
            var instrument = Color.FromRgb(style.Instrument.Red, style.Instrument.Green, style.Instrument.Blue);
            var inSector = Brightest(Ring(bearing + 3));
            var elsewhere = Brightest(Ring(bearing + 70));
            Assert.True(ColourScience.DeltaE(inSector, lightColour) < ColourScience.DeltaE(inSector, instrument), $"sector {inSector}");
            Assert.True(ColourScience.DeltaE(elsewhere, instrument) < ColourScience.DeltaE(elsewhere, lightColour), $"ring {elsewhere}");
        }
    }

    [Fact]
    public void RenderingWithSectorsIsDeterministic()
    {
        var field = Blob(Blob(Field(), 60, .35f, 12), 220, .35f, 10);
        var style = LocationPreviewExport.StyleFor(new ThemeCatalogue(isAvailable: _ => true), ThemeCatalogue.DefaultId, AppearanceMode.Dark, ColourVisionMode.None);
        Assert.Equal(LocationPreviewRenderer.RenderPng(SceneWith(field), style, 320, 180),
            LocationPreviewRenderer.RenderPng(SceneWith((byte[])field.Clone()), style, 320, 180));
    }
}
