using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using NodaTime;

namespace Noctaxis.Core.Tests;

/// <summary>The map instruments' preferred widths: shares of the map's width, saved, and kept within bounds on load.</summary>
public sealed class PlannerOverlaySizesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "noctaxis-overlays-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void ThePreferencesDefaultAndClampSafely()
    {
        Assert.Equal(new PlannerOverlaySizes(.221, .222), new AppSettings().EffectivePlannerOverlays);
        Assert.Equal(new PlannerOverlaySizes(PlannerOverlaySizes.MinimumFraction, PlannerOverlaySizes.MaximumFraction),
            new AppSettings(PlannerOverlays: new(-2, 9)).EffectivePlannerOverlays);
        Assert.Equal(new PlannerOverlaySizes(), new AppSettings(PlannerOverlays: new(double.NaN, double.PositiveInfinity)).EffectivePlannerOverlays);
    }

    [Fact]
    public async Task ThePreferencesAreSavedAndAFileWithoutThemLoadsWhole()
    {
        var now = Instant.FromUtc(2026, 10, 3, 21, 0);
        var store = new JsonUserDataStore(new Paths(_directory), NullLogger<JsonUserDataStore>.Instance, () => now);
        await store.SaveAsync(new PersistedState(4, new AppSettings(TimeSnapMinutes: 10, PlannerOverlays: new(.22, .18)), [],
            PlanningSession.Default(now, "UTC"), null), CancellationToken.None);
        var loaded = (await store.LoadAsync(CancellationToken.None)).Settings;
        Assert.Equal(new PlannerOverlaySizes(.22, .18), loaded.PlannerOverlays);

        await store.SaveAsync(new PersistedState(4, new AppSettings(TimeSnapMinutes: 10), [],
            PlanningSession.Default(now, "UTC"), null), CancellationToken.None);
        loaded = (await store.LoadAsync(CancellationToken.None)).Settings;
        Assert.Equal(10, loaded.TimeSnapMinutes);
        Assert.Equal(new PlannerOverlaySizes(), loaded.EffectivePlannerOverlays);
    }

    private sealed record Paths(string Path) : IUserDataPathProvider { public string GetApplicationDataDirectory() => Path; }
}
