using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Astronomy;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Locations;
using Noctaxis.Core.Time;
using Noctaxis.Core.Weather;
using NodaTime;

namespace Noctaxis.Core.Tests;

public sealed class ApplicationHardeningTests
{
    [Fact]
    public async Task SearchCacheBoundsDistinctQueriesAndReusesRecentResults()
    {
        var handler = new SearchHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/") };
        var provider = new OpenMeteoLocationSearchProvider(http, SystemClock.Instance,
            NullLogger<OpenMeteoLocationSearchProvider>.Instance);
        for (var i = 0; i < 129; i++) await provider.SearchAsync("place" + i, CancellationToken.None);
        await provider.SearchAsync("place128", CancellationToken.None);
        Assert.Equal(129, handler.Requests);
        await provider.SearchAsync("place0", CancellationToken.None);
        Assert.Equal(130, handler.Requests);
    }
    [Fact]
    public async Task WeatherTimeoutReturnsUnavailableButCallerCancellationPropagates()
    {
        using var client = new HttpClient(new TimeoutHandler()) { BaseAddress = new Uri("https://test.invalid/") };
        var provider = new OpenMeteoWeatherProvider(client, new GeographicWeatherCache(SystemClock.Instance),
            SystemClock.Instance, NullLogger<OpenMeteoWeatherProvider>.Instance);
        var request = new WeatherRequest(new(51, 0), SystemClock.Instance.GetCurrentInstant(), [], 0);
        Assert.Equal(DataState.Error, (await provider.GetWeatherAsync(request, CancellationToken.None)).State);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetWeatherAsync(request, cancelled.Token));
    }

    [Fact]
    public async Task LocationTimeoutReportsFailureButCallerCancellationPropagates()
    {
        using var client = new HttpClient(new TimeoutHandler()) { BaseAddress = new Uri("https://test.invalid/") };
        var provider = new OpenMeteoLocationSearchProvider(client, SystemClock.Instance,
            NullLogger<OpenMeteoLocationSearchProvider>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SearchAsync("London", CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SearchAsync("London", cancelled.Token));
    }

    [Fact]
    public async Task ProfileProductionAstronomyAndSearchRetention()
    {
        var output = System.Environment.GetEnvironmentVariable("NOCTAXIS_REVIEW_PROFILE");
        if (string.IsNullOrEmpty(output)) return;
        var measurements = new List<object>();
        var catalogue = new OpenNgcTargetCatalogue();
        var service = new AstronomyEngineService(new TimeZoneResolver());
        var instant = Instant.FromUtc(2026, 9, 9, 21, 0);
        foreach (var target in new[] { "sun", "moon", "M31" })
        {
            var allocated = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            for (var i = 0; i < 30; i++)
            {
                var observer = new GeoCoordinate(51 + i * .001, -1);
                service.Calculate(catalogue.Get(target), observer, instant + Duration.FromMinutes(i),
                    new LocalDate(2026, 9, 9), "Europe/London");
                await service.CalculatePathAsync(catalogue.Get(target), observer, new LocalDate(2026, 9, 9),
                    "Europe/London", instant, Duration.FromMinutes(10), CancellationToken.None);
            }
            measurements.Add(new { Target = target, Iterations = 30, timer.Elapsed.TotalMilliseconds,
                AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated });
        }
        using var http = new HttpClient(new SearchHandler()) { BaseAddress = new Uri("https://test.invalid/") };
        var search = new OpenMeteoLocationSearchProvider(http, SystemClock.Instance,
            NullLogger<OpenMeteoLocationSearchProvider>.Instance);
        var retained = GC.GetTotalMemory(true);
        for (var i = 0; i < 2000; i++) await search.SearchAsync("place" + i, CancellationToken.None);
        measurements.Add(new { SearchQueries = 2000, RetainedBytes = GC.GetTotalMemory(true) - retained });
        GC.KeepAlive(search);
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "core.json"), JsonSerializer.Serialize(measurements));
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new TaskCanceledException("Simulated HTTP timeout");
    }
    private sealed class SearchHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"results\":[{\"id\":1,\"name\":\"Example\",\"latitude\":51,\"longitude\":0}]}") });
        }
    }
}
