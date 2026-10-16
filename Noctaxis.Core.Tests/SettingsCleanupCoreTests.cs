using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Weather;
using NodaTime;

namespace Noctaxis.Core.Tests;

/// <summary>
/// The Settings cleanup's model changes: a camera's optional resolution and the pixel pitch derived from it, and
/// the visibility the map's limits depend on always being requested, whatever the weather panel shows.
/// </summary>
public sealed class SettingsCleanupCoreTests
{
    /// <summary>
    /// The resolution is optional: a profile without it, or with only one dimension, is as valid as before and has
    /// no pixel pitch; with both, the pitch is derived from the sensor's size, never stored.
    /// </summary>
    [Fact]
    public void ACamerasResolutionIsOptionalAndGivesItsPixelPitch()
    {
        var fullFrame = new CameraProfile("ff", "Full frame", 36, 24);
        Assert.True(fullFrame.IsValid);
        Assert.False(fullFrame.HasResolution);
        Assert.Null(fullFrame.PixelPitchMicrometres);

        var halfDescribed = fullFrame with { ResolutionWidthPixels = 6000 };
        Assert.True(halfDescribed.IsValid);
        Assert.False(halfDescribed.HasResolution);
        Assert.Null(halfDescribed.PixelPitchMicrometres);

        var described = fullFrame with { ResolutionWidthPixels = 6000, ResolutionHeightPixels = 4000 };
        Assert.True(described.HasResolution);
        Assert.Equal(6, described.PixelPitchMicrometres!.Value, 9);
        Assert.False((fullFrame with { ResolutionWidthPixels = 0, ResolutionHeightPixels = 4000 }).HasResolution);

        // Saved and loaded with it; an older profile without it loads as it was.
        var json = JsonSerializer.Serialize(described);
        Assert.DoesNotContain("PixelPitch", json);
        Assert.Equal(described, JsonSerializer.Deserialize<CameraProfile>(json));
        Assert.Equal(fullFrame, JsonSerializer.Deserialize<CameraProfile>("""{"Id":"ff","DisplayName":"Full frame","SensorWidthMillimetres":36,"SensorHeightMillimetres":24}"""));
    }

    /// <summary>
    /// Visibility is always requested, so hiding it from the weather panel never takes away the data the map's
    /// visibility limits are drawn from; values that are shown are requested as before.
    /// </summary>
    [Fact]
    public async Task VisibilityIsAlwaysRequested()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/") };
        var provider = new OpenMeteoWeatherProvider(client, new GeographicWeatherCache(SystemClock.Instance),
            SystemClock.Instance, NullLogger<OpenMeteoWeatherProvider>.Instance);
        await provider.GetWeatherAsync(new WeatherRequest(new(51, 0), SystemClock.Instance.GetCurrentInstant(),
            [WeatherField.TotalCloudCover], 0), CancellationToken.None);
        var hourly = Uri.UnescapeDataString(handler.Requested!.Query.Split('&').Single(part => part.StartsWith("hourly=")))["hourly=".Length..].Split(',');
        Assert.Contains("visibility", hourly);
        Assert.Contains("cloud_cover", hourly);
        Assert.DoesNotContain("temperature_2m", hourly);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? Requested { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
