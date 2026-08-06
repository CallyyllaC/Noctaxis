using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Astronomy;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Locations;
using Noctaxis.Core.Planning;
using Noctaxis.Core.Terrain;
using Noctaxis.Core.Time;
using Noctaxis.Core.Weather;
using NodaTime;

namespace Noctaxis.Core.Tests;

public sealed class LocationAndCatalogueTests
{
    [Fact]
    public async Task DefaultResolution_PrefersLastCustomPosition()
    {
        var last = new GeoCoordinate(54.2, -2.4);
        var resolver = new LocationResolver(new FakeDevice(null), NullLogger<LocationResolver>.Instance);
        var result = await resolver.ResolveDefaultAsync(last, CancellationToken.None);
        Assert.Equal(LocationResolutionSource.LastCustomPosition, result.Source);
        Assert.Equal(last, result.Coordinate);
    }

    [Fact]
    public async Task DeviceResolution_UsesDeviceAndFailureFallsBack()
    {
        var deviceResult = new LocationResolution(new GeoCoordinate(51, -1), LocationResolutionSource.OperatingSystemLocation, 25);
        var resolver = new LocationResolver(new FakeDevice(deviceResult), NullLogger<LocationResolver>.Instance);
        Assert.Equal(deviceResult, await resolver.ResolveDeviceOrFallbackAsync(null, CancellationToken.None));

        var last = new GeoCoordinate(55, -3);
        var failure = new LocationResolver(new ThrowingDevice(), NullLogger<LocationResolver>.Instance);
        var fallback = await failure.ResolveDeviceOrFallbackAsync(last, CancellationToken.None);
        Assert.Equal(LocationResolutionSource.LastCustomPosition, fallback.Source);
        Assert.Equal(last, fallback.Coordinate);
    }

    [Fact]
    public async Task RegionFallback_IsApproximateOrUsesNeutralFallback()
    {
        var resolver = new LocationResolver(new FakeDevice(null), NullLogger<LocationResolver>.Instance);
        var result = await resolver.ResolveDefaultAsync(null, CancellationToken.None);
        Assert.True(result.IsApproximate);
        Assert.True(result.Source is LocationResolutionSource.SystemRegion or LocationResolutionSource.ApplicationFallback);
    }

    [Fact]
    public async Task OpenMeteoGeocoding_MapsRegionalContextAndCachesResults()
    {
        const string json = """{"results":[{"id":2643743,"name":"London","latitude":51.5085,"longitude":-0.1257,"elevation":25,"country":"United Kingdom","admin1":"England","timezone":"Europe/London"}]}""";
        var handler = new CountingHandler(json);
        var clock = new TestClock(Instant.FromUtc(2024, 1, 1, 0, 0));
        var provider = new OpenMeteoLocationSearchProvider(new HttpClient(handler) { BaseAddress = new Uri("https://geocoding-api.open-meteo.com/v1/") }, clock, NullLogger<OpenMeteoLocationSearchProvider>.Instance);
        var first = Assert.Single(await provider.SearchAsync("London", CancellationToken.None));
        var second = Assert.Single(await provider.SearchAsync("london", CancellationToken.None));
        Assert.Equal("England, United Kingdom", first.RegionDescription);
        Assert.Equal("Europe/London", first.TimeZoneId);
        Assert.Equal(first, second);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task NominatimReverseGeocoding_PrefersLocalityAndCachesNearbyCoordinates()
    {
        const string json = """{"display_name":"High Street, Testville, England","address":{"road":"High Street","village":"Testville","town":"Larger Town","county":"Test County","state":"England","country":"United Kingdom"}}""";
        var handler = new CountingHandler(json);
        var provider = new NominatimReverseGeocodingProvider(
            new HttpClient(handler) { BaseAddress = new Uri("https://nominatim.openstreetmap.org/") },
            new TestClock(Instant.FromUtc(2024, 1, 1, 0, 0)),
            NullLogger<NominatimReverseGeocodingProvider>.Instance);

        var first = await provider.ResolveAsync(new GeoCoordinate(51.50000, -0.10000), CancellationToken.None);
        var nearby = await provider.ResolveAsync(new GeoCoordinate(51.50010, -0.10010), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal("Testville", first.PlaceName);
        Assert.Equal("England", first.RegionDescription);
        Assert.Equal(first, nearby);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData("Andromeda", "openngc:NGC0224")]
    [InlineData("M31", "openngc:NGC0224")]
    [InlineData("NGC 224", "openngc:NGC0224")]
    [InlineData("IC 1805", "openngc:IC1805")]
    [InlineData("Orion Nebula", "openngc:NGC1976")]
    public async Task LocalCatalogueSearch_MatchesNamesIdentifiersConstellationsAndAliases(string query, string expectedId)
    {
        var service = new LocalTargetSearchService(new OpenNgcTargetCatalogue());
        Assert.Contains(await service.SearchAsync(query, 8, CancellationToken.None), target => target.Id == expectedId);
    }

    [Fact]
    public void OpenNgcCatalogue_LoadsValidatedEntries()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        Assert.True(catalogue.Targets.Count >= 13_000);
        Assert.Equal(catalogue.Targets.Count, catalogue.Targets.Select(target => target.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(catalogue.Targets.Where(target => !target.IsSun && !target.IsMoon), target =>
        {
            Assert.InRange(target.RightAscensionHours!.Value, 0, 23.999999);
            Assert.InRange(target.DeclinationDegrees!.Value, -90, 90);
            Assert.Equal("J2000", target.CoordinateEpoch);
            Assert.False(string.IsNullOrWhiteSpace(target.Source));
        });
        Assert.Contains(AstralTargetCategory.Galaxy, catalogue.ObjectTypes);
        Assert.Contains("Orion", catalogue.Constellations);
        var andromeda = catalogue.Get("M31");
        Assert.Equal("openngc:NGC0224", andromeda.Id);
        Assert.Contains("Andromeda", andromeda.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(andromeda.RightAscensionHours!.Value, 0.70, 0.72);
        Assert.Equal(andromeda.Id, catalogue.ResolveId("andromeda"));
        Assert.Equal("openngc:Mel022", catalogue.Get("M45").Id); // OpenNGC addendum.
    }

    [Fact]
    public async Task CatalogueFilters_CombineTypeConstellationAndIdentifierFamily()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var service = new LocalTargetSearchService(catalogue);
        var nebulae = await service.SearchAsync(new CatalogueSearchQuery("Orion Nebula", AstralTargetCategory.Nebula, "Orion"), 20, CancellationToken.None);
        Assert.Contains(nebulae, target => target.Id == "openngc:NGC1976");
        var ngc = await service.SearchAsync(new CatalogueSearchQuery("Galaxy", AstralTargetCategory.Galaxy, CatalogueFamily: "NGC"), 20, CancellationToken.None);
        Assert.Contains(ngc, target => target.Id == "openngc:NGC0224");
        Assert.All(ngc, target => Assert.Equal(AstralTargetCategory.Galaxy, target.Category));
    }

    [Fact]
    public void VisibilityPolicy_AllowsEightAndRetainsButHidesAdditionalObjects()
    {
        var selections = Enumerable.Range(0, 11).Select(index => new CelestialObjectSelection($"object-{index}", index != 9, index)).ToArray();
        var normalised = CelestialVisibilityPolicy.Normalise(selections);
        Assert.Equal(11, normalised.Count);
        Assert.Equal(8, normalised.Count(item => item.IsVisible));
        Assert.False(normalised[8].IsVisible);
        Assert.False(normalised[9].IsVisible);
        Assert.False(normalised[10].IsVisible);
    }

    [Theory]
    [InlineData(DeviceLocationAvailabilityState.Available, true)]
    [InlineData(DeviceLocationAvailabilityState.PermissionRequestable, true)]
    [InlineData(DeviceLocationAvailabilityState.PermissionPermanentlyDenied, false)]
    [InlineData(DeviceLocationAvailabilityState.Unsupported, false)]
    public void DeviceAvailability_SeparatesRequestability(DeviceLocationAvailabilityState state, bool expected)
    {
        Assert.Equal(expected, new DeviceLocationAvailability(state).CanRequest);
    }

    [Fact]
    public async Task Planning_DoesNotCalculateDisabledCatalogueObject()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var astronomy = new CountingAstronomy();
        var service = new PlanningService(catalogue, astronomy, new LensCalculator(), new FlatTerrainHorizonProvider(), new FakeWeather(), new TimeZoneResolver());
        var session = PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 12, 0), "UTC") with
        {
            TargetId = "sun",
            VisibleObjects = [new("sun", true, 0), new("moon", true, 1), new("andromeda", false, 2)]
        };
        var snapshot = await service.CalculateSnapshotAsync(session, new WeatherSettings(), CancellationToken.None);
        Assert.DoesNotContain("andromeda", astronomy.CalculatedIds);
        Assert.Equal(2, snapshot.EffectiveObjectPlans.Count);
    }

    [Fact]
    public void ObserverElevation_IsClampedToSupportedRange()
    {
        Assert.Equal(9_000, new GeoCoordinate(0, 0, 20_000).Normalised().ElevationMetres);
        Assert.Equal(-500, new GeoCoordinate(0, 0, -1_000).Normalised().ElevationMetres);
    }

    private sealed record FakeDevice(LocationResolution? Result) : IDeviceLocationProvider
    {
        public Task<LocationResolution?> TryGetLocationAsync(CancellationToken cancellationToken) => Task.FromResult(Result);
    }
    private sealed class ThrowingDevice : IDeviceLocationProvider
    {
        public Task<LocationResolution?> TryGetLocationAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Denied");
    }
    private sealed class TestClock(Instant now) : IClock { public Instant GetCurrentInstant() => now; }
    private sealed class CountingHandler(string json) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
    private sealed class FakeWeather : IWeatherProvider
    {
        public Task<WeatherResult> GetWeatherAsync(WeatherRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new WeatherResult(DataState.Error, null, "Offline"));
    }
    private sealed class CountingAstronomy : IAstronomyService
    {
        public List<string> CalculatedIds { get; } = [];
        public TargetPosition Calculate(AstralTarget target, GeoCoordinate observer, Instant instant, LocalDate localDate, string timeZoneId)
        {
            CalculatedIds.Add(target.Id);
            return new TargetPosition(target, instant, new HorizontalCoordinate(180, 30), new TargetEvents(null, null, null),
                target.IsMoon ? .5 : null, target.IsMoon ? 90 : null,
                target.IsSun ? new TwilightEvents(null, null, null, null, null, null, null, null) : null);
        }
        public Task<AstralPath> CalculatePathAsync(AstralTarget target, GeoCoordinate observer, LocalDate localDate, string timeZoneId, Instant selectedInstant, Duration interval, CancellationToken cancellationToken)
        {
            CalculatedIds.Add(target.Id);
            return Task.FromResult(new AstralPath(localDate, timeZoneId, interval,
                [new AstralPathSample(selectedInstant, new HorizontalCoordinate(180, 30)), new AstralPathSample(selectedInstant + interval, new HorizontalCoordinate(181, 31))],
                new TargetEvents(null, null, null), selectedInstant));
        }
    }
}
