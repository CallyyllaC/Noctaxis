using System.Text.Json;
using Noctaxis.Core.Export;
using NodaTime;
using SkiaSharp;

namespace Noctaxis.Core.Tests;

/// <summary>The plan a plan image carries: schema 1, written and read through one path, and nothing derived.</summary>
public sealed class NoctaxisPlanTests
{
    private static readonly NoctaxisPlan Focused = new(Instant.FromUtc(2026, 10, 7, 21, 0), 52.59302, -3.64697, 293.3, 7.2,
        new NoctaxisPlanFocus("m31", Follow: true));

    private static byte[] OrdinaryPng(SKColor colour)
    {
        using var bitmap = new SKBitmap(4, 3);
        bitmap.Erase(colour);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Exactly the agreed fields, in UTC, and nothing else at any level.</summary>
    [Fact]
    public void ThePlanIsTheMinimalVersionOneDocument()
    {
        using var document = JsonDocument.Parse(Focused.ToJson());
        var root = document.RootElement;
        Assert.Equal(["format", "schemaVersion", "timeUtc", "location", "camera", "focus"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("noctaxis-plan", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("2026-10-07T21:00:00Z", root.GetProperty("timeUtc").GetString());
        Assert.Equal(["latitude", "longitude"], root.GetProperty("location").EnumerateObject().Select(p => p.Name));
        Assert.Equal(52.59302, root.GetProperty("location").GetProperty("latitude").GetDouble());
        Assert.Equal(["bearingDegrees", "pitchDegrees"], root.GetProperty("camera").EnumerateObject().Select(p => p.Name));
        Assert.Equal(293.3, root.GetProperty("camera").GetProperty("bearingDegrees").GetDouble());
        Assert.Equal(["target", "follow"], root.GetProperty("focus").EnumerateObject().Select(p => p.Name));
        Assert.Equal("m31", root.GetProperty("focus").GetProperty("target").GetString());
        Assert.True(root.GetProperty("focus").GetProperty("follow").GetBoolean());
        // No derived or interface state, by name, anywhere in it.
        foreach (var forbidden in new[] { "elevation", "altitude", "terrain", "weather", "theme", "layout", "dock", "pip", "lens", "camera\":{\"profile", "provider", "lightPollution" })
            Assert.DoesNotContain(forbidden, Focused.ToJson(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoFocusIsWrittenAsNull()
    {
        var plan = Focused with { Focus = null };
        using var document = JsonDocument.Parse(plan.ToJson());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("focus").ValueKind);
        Assert.Equal(plan, NoctaxisPlan.Parse(plan.ToJson()).Plan);
    }

    [Fact]
    public void APlanReadsBackAsWritten() => Assert.Equal(Focused, NoctaxisPlan.Parse(Focused.ToJson()).Plan);

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        const string json = """
            {"format":"noctaxis-plan","schemaVersion":1,"timeUtc":"2026-10-07T21:00:00Z","extra":{"anything":[1,2]},
             "location":{"latitude":52.5,"longitude":-3.6,"elevation":400},"camera":{"bearingDegrees":10,"pitchDegrees":-2,"lens":"x"},
             "focus":{"target":"moon","follow":false,"note":"hi"}}
            """;
        var read = NoctaxisPlan.Parse(json);
        Assert.Equal(NoctaxisPlanError.None, read.Error);
        Assert.Equal(new NoctaxisPlan(Instant.FromUtc(2026, 10, 7, 21, 0), 52.5, -3.6, 10, -2, new("moon", false)), read.Plan);
    }

    [Theory]
    [InlineData("not json", NoctaxisPlanError.Malformed)]
    [InlineData("""{"format":"something-else","schemaVersion":1}""", NoctaxisPlanError.Malformed)]
    [InlineData("""{"format":"noctaxis-plan","schemaVersion":2,"whatever":true}""", NoctaxisPlanError.UnsupportedSchema)]
    [InlineData("""{"format":"noctaxis-plan","schemaVersion":"1"}""", NoctaxisPlanError.Malformed)]
    [InlineData("""{"format":"noctaxis-plan","schemaVersion":1,"timeUtc":"2026-10-07T22:00:00+01:00","location":{"latitude":1,"longitude":1},"camera":{"bearingDegrees":0,"pitchDegrees":0},"focus":null}""", NoctaxisPlanError.InvalidTime)]
    [InlineData("""{"format":"noctaxis-plan","schemaVersion":1,"timeUtc":"yesterday","location":{"latitude":1,"longitude":1},"camera":{"bearingDegrees":0,"pitchDegrees":0},"focus":null}""", NoctaxisPlanError.InvalidTime)]
    [InlineData("""{"format":"noctaxis-plan","schemaVersion":1,"timeUtc":"2026-10-07T21:00:00Z","location":{"latitude":91,"longitude":1},"camera":{"bearingDegrees":0,"pitchDegrees":0},"focus":null}""", NoctaxisPlanError.InvalidCoordinates)]
    [InlineData("""{"format":"noctaxis-plan","schemaVersion":1,"timeUtc":"2026-10-07T21:00:00Z","location":{"latitude":1},"camera":{"bearingDegrees":0,"pitchDegrees":0},"focus":null}""", NoctaxisPlanError.InvalidCoordinates)]
    [InlineData("""{"format":"noctaxis-plan","schemaVersion":1,"timeUtc":"2026-10-07T21:00:00Z","location":{"latitude":1,"longitude":1},"focus":null}""", NoctaxisPlanError.Malformed)]
    [InlineData("""{"format":"noctaxis-plan","schemaVersion":1,"timeUtc":"2026-10-07T21:00:00Z","location":{"latitude":1,"longitude":1},"camera":{"bearingDegrees":0,"pitchDegrees":0},"focus":{"follow":true}}""", NoctaxisPlanError.Malformed)]
    public void AnInvalidPlanIsRejectedWithItsReason(string json, NoctaxisPlanError error)
    {
        var read = NoctaxisPlan.Parse(json);
        Assert.Null(read.Plan);
        Assert.Equal(error, read.Error);
    }

    /// <summary>The plan travels in the PNG's own text chunk; the pixels are untouched.</summary>
    [Fact]
    public void APngCarriesThePlanAndKeepsItsPixels()
    {
        var png = OrdinaryPng(SKColors.CornflowerBlue);
        var withPlan = NoctaxisPlanImage.Embed(png, Focused);
        Assert.True(NoctaxisPlanImage.IsPng(withPlan));
        Assert.Equal(Focused.ToJson(), PngMetadataWriter.ReadText(withPlan, NoctaxisPlanImage.MetadataKey));
        Assert.Equal(Focused, NoctaxisPlanImage.Read(withPlan).Plan);
        using var before = SKBitmap.Decode(png);
        using var after = SKBitmap.Decode(withPlan);
        Assert.Equal(before.Pixels, after.Pixels);
    }

    [Fact]
    public void ImagesWithoutAPlanAreRecognisedForWhatTheyAre()
    {
        Assert.Equal(NoctaxisPlanError.NoPlan, NoctaxisPlanImage.Read(OrdinaryPng(SKColors.Red)).Error);
        Assert.Equal(NoctaxisPlanError.UnsupportedImageFormat, NoctaxisPlanImage.Read([0xFF, 0xD8, 0xFF, 0xE0, 0, 0]).Error);
        Assert.Equal(NoctaxisPlanError.UnsupportedImageFormat, NoctaxisPlanImage.Read("GIF89a......"u8.ToArray()).Error);
        Assert.Equal(NoctaxisPlanError.NotAnImage, NoctaxisPlanImage.Read("plain text"u8.ToArray()).Error);
        Assert.Equal(NoctaxisPlanError.NotAnImage, NoctaxisPlanImage.Read([]).Error);
        Assert.Equal(NoctaxisPlanError.NotAnImage, NoctaxisPlanImage.Read(null).Error);
        // A PNG cut short is damaged, not a plan.
        var cut = NoctaxisPlanImage.Embed(OrdinaryPng(SKColors.Red), Focused)[..40];
        Assert.Equal(NoctaxisPlanError.NotAnImage, NoctaxisPlanImage.Read(cut).Error);
        // A plan chunk that is not a plan.
        var damaged = PngMetadataWriter.AddText(OrdinaryPng(SKColors.Red), NoctaxisPlanImage.MetadataKey, "{\"format\":\"noctaxis-plan\"");
        Assert.Equal(NoctaxisPlanError.Malformed, NoctaxisPlanImage.Read(damaged).Error);
    }
}
