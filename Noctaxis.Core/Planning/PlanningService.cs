using Noctaxis.Core.Astronomy;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Terrain;
using Noctaxis.Core.Time;
using Noctaxis.Core.Weather;
using NodaTime;

namespace Noctaxis.Core.Planning;

public interface IPlanningService
{
    TargetPosition CalculateCurrent(PlanningSession session);
    Task<PlanningSnapshot> CalculateSnapshotAsync(PlanningSession session, WeatherSettings weatherSettings, CancellationToken cancellationToken);
    Task<WeatherResult> RefreshWeatherAsync(PlanningSession session, WeatherSettings weatherSettings, CancellationToken cancellationToken);
}

public sealed class PlanningService(
    ITargetCatalogue catalogue,
    IAstronomyService astronomy,
    ILensCalculator lenses,
    ITerrainHorizonProvider terrain,
    IWeatherProvider weather,
    ITimeZoneResolver timeZones) : IPlanningService
{
    public TargetPosition CalculateCurrent(PlanningSession session)
    {
        var target = catalogue.Get(session.TargetId);
        var localDate = timeZones.InZone(session.Instant, session.TimeZoneId).Date;
        return astronomy.Calculate(target, session.Observer, session.Instant, localDate, session.TimeZoneId);
    }

    public async Task<PlanningSnapshot> CalculateSnapshotAsync(
        PlanningSession session,
        WeatherSettings weatherSettings,
        CancellationToken cancellationToken)
    {
        var localDate = timeZones.InZone(session.Instant, session.TimeZoneId).Date;
        var primaryTarget = catalogue.Get(session.TargetId);
        var selections = session.EffectiveVisibleObjects
            .Where(item => item.IsVisible)
            .Select(item => catalogue.ResolveId(item.TargetId))
            .Where(id => id is not null)
            .Select(id => new CelestialObjectSelection(id!))
            .Append(new CelestialObjectSelection(primaryTarget.Id))
            .DistinctBy(item => item.TargetId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Order)
            .ToArray();
        var targets = selections.Select(item => catalogue.Get(item.TargetId)).ToArray();
        var positionTasks = targets.ToDictionary(target => target.Id,
            target => target.HasEquatorialCoordinates
                ? astronomy.CalculateCatalogueAsync(target, session.Observer, session.Instant, localDate, session.TimeZoneId, cancellationToken)
                : Task.FromResult(astronomy.Calculate(target, session.Observer, session.Instant, localDate, session.TimeZoneId)),
            StringComparer.OrdinalIgnoreCase);
        var pathTasks = targets.ToDictionary(target => target.Id,
            target => astronomy.CalculatePathAsync(target, session.Observer, localDate, session.TimeZoneId,
                session.Instant, Duration.FromMinutes(10), cancellationToken), StringComparer.OrdinalIgnoreCase);
        var terrainTask = terrain.GetProfileAsync(session.Observer, new TerrainProfileRequest(), cancellationToken);
        var weatherTask = weather.GetWeatherAsync(CreateWeatherRequest(session, weatherSettings, false), cancellationToken);
        await Task.WhenAll(positionTasks.Values.Cast<Task>().Concat(pathTasks.Values).Append(terrainTask).Append(weatherTask)).ConfigureAwait(false);
        var positions = positionTasks.ToDictionary(pair => pair.Key, pair => pair.Value.Result, StringComparer.OrdinalIgnoreCase);
        var position = positions[primaryTarget.Id];
        var path = await pathTasks[primaryTarget.Id].ConfigureAwait(false);
        var horizon = await terrainTask.ConfigureAwait(false);
        var weatherResult = await weatherTask.ConfigureAwait(false);
        var sun = positions.GetValueOrDefault("sun") ?? astronomy.Calculate(catalogue.Get("sun"), session.Observer, session.Instant, localDate, session.TimeZoneId);
        var moon = positions.GetValueOrDefault("moon") ?? astronomy.Calculate(catalogue.Get("moon"), session.Observer, session.Instant, localDate, session.TimeZoneId);
        var objectPlans = new List<CelestialObjectPlan>(targets.Length);
        foreach (var target in targets)
            objectPlans.Add(new CelestialObjectPlan(positions[target.Id], await pathTasks[target.Id].ConfigureAwait(false)));
        return new PlanningSnapshot(session, position, path, lenses.Calculate(session.Lens), horizon,
            TerrainCrossingCalculator.Calculate(path, horizon), weatherResult, new AstronomyContext(sun, moon), objectPlans);
    }

    public Task<WeatherResult> RefreshWeatherAsync(
        PlanningSession session,
        WeatherSettings weatherSettings,
        CancellationToken cancellationToken) =>
        weather.GetWeatherAsync(CreateWeatherRequest(session, weatherSettings, true), cancellationToken);

    private static WeatherRequest CreateWeatherRequest(PlanningSession session, WeatherSettings settings, bool force) =>
        new(session.Observer, session.Instant, settings.EffectiveFields,
            Math.Clamp(settings.CacheDistanceKilometres, 0, 100), force);
}
