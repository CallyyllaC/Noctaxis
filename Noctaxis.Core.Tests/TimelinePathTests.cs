using Noctaxis.Core.Astronomy;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Time;
using NodaTime;

namespace Noctaxis.Core.Tests;

/// <summary>The Timeline's paths are the same astronomy as the day's, sampled across a window that may cross midnight.</summary>
public sealed class TimelinePathTests
{
    [Fact]
    public async Task AWindowedPathIsTheDayPathsSamplesAcrossItsWindow()
    {
        var astronomy = new AstronomyEngineService(new TimeZoneResolver());
        var observer = new GeoCoordinate(53.5, -0.5);
        var catalogue = new OpenNgcTargetCatalogue();
        foreach (var id in new[] { "sun", "moon", catalogue.Get("m31").Id })
        {
            var target = catalogue.Get(id);
            var start = Instant.FromUtc(2026, 10, 3, 10, 0);
            var window = await astronomy.CalculatePathAsync(target, observer, start, start + Duration.FromHours(24), "UTC", start,
                Duration.FromMinutes(10), CancellationToken.None);
            Assert.Equal(145, window.Samples.Count);
            Assert.Equal(start, window.Samples[0].Instant);
            Assert.Equal(start + Duration.FromHours(24), window.Samples[^1].Instant);
            // Where the window and the next day overlap, the samples are the day path's own, exactly.
            var day = await astronomy.CalculatePathAsync(target, observer, new LocalDate(2026, 10, 4), "UTC", start,
                Duration.FromMinutes(10), CancellationToken.None);
            var byInstant = day.Samples.ToDictionary(sample => sample.Instant);
            var shared = window.Samples.Where(sample => byInstant.ContainsKey(sample.Instant)).ToArray();
            Assert.True(shared.Length > 50);
            Assert.All(shared, sample => Assert.Equal(byInstant[sample.Instant].Horizontal, sample.Horizontal));
        }
    }
}
