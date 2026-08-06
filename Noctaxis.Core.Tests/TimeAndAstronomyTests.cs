using Noctaxis.Core.Astronomy;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Time;
using NodaTime;

namespace Noctaxis.Core.Tests;

public sealed class TimeAndAstronomyTests
{
    private readonly TimeZoneResolver _zones = new();

    [Fact]
    public void LocalDayAcrossSpringDstBoundary_Is23Hours()
    {
        var day = _zones.GetLocalDay(new LocalDate(2024, 3, 31), "Europe/London");
        Assert.Equal(23, (day.End - day.Start).TotalHours);
        Assert.Equal(Instant.FromUtc(2024, 3, 31, 0, 0), day.Start);
        Assert.Equal(Instant.FromUtc(2024, 3, 31, 23, 0), day.End);
    }

    [Fact]
    public void NonexistentDstLocalTime_IsResolvedLenientlyAndExplicitly()
    {
        var instant = _zones.ResolveLocal(new LocalDate(2024, 3, 31), new LocalTime(1, 30), "Europe/London");
        var local = _zones.InZone(instant, "Europe/London");
        Assert.Equal(new LocalTime(2, 30), local.TimeOfDay);
        Assert.Equal(Instant.FromUtc(2024, 3, 31, 1, 30), instant);
    }

    [Fact]
    public void SummerSolsticeSunPositionInLondon_IsSane()
    {
        var service = new AstronomyEngineService(_zones);
        var target = new OpenNgcTargetCatalogue().Get("sun");
        var instant = Instant.FromUtc(2024, 6, 21, 12, 0);
        var result = service.Calculate(target, new GeoCoordinate(51.5074, -0.1278), instant, new LocalDate(2024, 6, 21), "Europe/London");
        Assert.InRange(result.Horizontal.AltitudeDegrees, 60, 63);
        Assert.InRange(result.Horizontal.AzimuthDegrees, 175, 185);
        Assert.NotNull(result.Events.Rise);
        Assert.NotNull(result.Events.Set);
        Assert.NotNull(result.Twilight?.CivilDusk);
        Assert.Null(result.Twilight?.AstronomicalDusk); // London never reaches -18° near midsummer.
    }

    [Fact]
    public void OpenNgcTarget_ProducesValidHorizontalPosition()
    {
        var service = new AstronomyEngineService(_zones);
        var target = new OpenNgcTargetCatalogue().Get("NGC 869");
        var result = service.Calculate(target, new GeoCoordinate(51.5, -0.1), Instant.FromUtc(2024, 1, 15, 22, 0), new LocalDate(2024, 1, 15), "Europe/London");
        Assert.InRange(result.Horizontal.AltitudeDegrees, -90, 90);
        Assert.InRange(result.Horizontal.AzimuthDegrees, 0, 360);
    }

    [Fact]
    public async Task DeepSkyPath_ContainsRiseTransitAndSetForOrion()
    {
        var service = new AstronomyEngineService(_zones);
        var target = new OpenNgcTargetCatalogue().Get("NGC 1976");
        var selected = Instant.FromUtc(2024, 1, 15, 22, 0);
        var path = await service.CalculatePathAsync(target, new GeoCoordinate(51.5, -0.1), new LocalDate(2024, 1, 15), "Europe/London", selected, Duration.FromMinutes(10), CancellationToken.None);
        Assert.NotNull(path.Events.Rise);
        Assert.NotNull(path.Events.Transit);
        Assert.NotNull(path.Events.Set);
        Assert.Contains(path.Samples, sample => sample.Horizontal.AltitudeDegrees > 30);
        Assert.Contains(path.Samples, sample => sample.Horizontal.AltitudeDegrees < 0);
    }

    [Fact]
    public async Task CatalogueJ2000Calculations_AreStatelessConcurrentAndRetainIdentity()
    {
        var service = new AstronomyEngineService(_zones);
        var catalogue = new OpenNgcTargetCatalogue();
        var observer = new GeoCoordinate(51.5, -0.1, 25);
        var instant = Instant.FromUtc(2025, 2, 1, 22, 0);
        var date = new LocalDate(2025, 2, 1);
        var targets = new[] { catalogue.Get("M31"), catalogue.Get("M42"), catalogue.Get("M45") };
        var results = await Task.WhenAll(targets.Select(target => service.CalculateCatalogueAsync(target, observer, instant, date, "UTC", CancellationToken.None)));
        Assert.Equal(targets.Select(target => target.Id), results.Select(result => result.Target.Id));
        Assert.All(results, result =>
        {
            Assert.InRange(result.Horizontal.AzimuthDegrees, 0, 360);
            Assert.InRange(result.Horizontal.AltitudeDegrees, -90, 90);
        });
        var repeated = await service.CalculateCatalogueAsync(targets[0], observer, instant, date, "UTC", CancellationToken.None);
        Assert.Equal(results[0].Horizontal, repeated.Horizontal);
    }

    [Fact]
    public async Task CancelledCatalogueCalculation_LeavesServiceUsable()
    {
        var service = new AstronomyEngineService(_zones);
        var target = new OpenNgcTargetCatalogue().Get("M31");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CalculateCatalogueAsync(target,
            new GeoCoordinate(51.5, -0.1), Instant.FromUtc(2025, 1, 1, 0, 0), new LocalDate(2025, 1, 1), "UTC", cancellation.Token));
        var result = await service.CalculateCatalogueAsync(target, new GeoCoordinate(51.5, -0.1),
            Instant.FromUtc(2025, 1, 1, 0, 0), new LocalDate(2025, 1, 1), "UTC", CancellationToken.None);
        Assert.Equal(target.Id, result.Target.Id);
    }
}
