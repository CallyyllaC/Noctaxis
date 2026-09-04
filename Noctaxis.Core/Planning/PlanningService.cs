using Noctaxis.Core.Astronomy;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Terrain;
using Noctaxis.Core.Time;
using Noctaxis.Core.Weather;
using NodaTime;

namespace Noctaxis.Core.Planning;

public interface IPlanningService
{
    TargetPosition CalculateCurrent(PlanningSession session);
    Task<PlanningSnapshot> CalculateCoreSnapshotAsync(PlanningSession session,
        CancellationToken cancellationToken);
    Task<PlannerEnvironmentSnapshot> LoadEnvironmentAsync(PlanningSession session,
        CancellationToken cancellationToken);
    Task<PlannerEnvironmentSnapshot> LoadEnvironmentAsync(PlanningSession session,
        double cameraHeightAboveGroundMetres, CancellationToken cancellationToken) =>
        LoadEnvironmentAsync(session, cancellationToken);
    async Task<TerrainHorizonProfile> PrioritiseTerrainAsync(PlanningSession session,
        IReadOnlyList<double> bearings, CancellationToken cancellationToken) =>
        (await LoadEnvironmentAsync(session, cancellationToken).ConfigureAwait(false)).HorizonProfile;
    Task<TerrainHorizonProfile> PrioritiseTerrainAsync(PlanningSession session,
        double cameraHeightAboveGroundMetres, IReadOnlyList<double> bearings,
        CancellationToken cancellationToken) => PrioritiseTerrainAsync(session, bearings, cancellationToken);
    Task<WeatherResult> LoadWeatherAsync(PlanningSession session, WeatherSettings weatherSettings,
        CancellationToken cancellationToken);
    PlanningRefreshWork StartRefresh(PlanningSession session, WeatherSettings weatherSettings,
        CancellationToken cancellationToken);
    PlanningRefreshWork StartRefresh(PlanningSession session, WeatherSettings weatherSettings,
        double cameraHeightAboveGroundMetres, CancellationToken cancellationToken) =>
        StartRefresh(session, weatherSettings, cancellationToken);
    Task<PlanningSnapshot> CalculateSnapshotAsync(PlanningSession session, WeatherSettings weatherSettings, CancellationToken cancellationToken);
    Task<WeatherResult> RefreshWeatherAsync(PlanningSession session, WeatherSettings weatherSettings, CancellationToken cancellationToken);
}

/// <summary>
/// The independently useful pieces of a Planner refresh. The UI may commit the core astronomy
/// snapshot before optional static-environment and weather enrichment have finished.
/// </summary>
public sealed record PlanningRefreshWork(
    Task<PlanningSnapshot> Core,
    Task<PlannerEnvironmentSnapshot> Environment,
    Task<WeatherResult> Weather,
    Func<IReadOnlyList<double>, CancellationToken, Task<TerrainHorizonProfile>>? PriorityTerrain = null);

public sealed class PlanningService(
    ITargetCatalogue catalogue,
    IAstronomyService astronomy,
    ILensCalculator lenses,
    IPlannerEnvironmentService environment,
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
        var work = StartRefresh(session, weatherSettings, cancellationToken);
        await Task.WhenAll(work.Core, work.Environment, work.Weather).ConfigureAwait(false);
        var core = await work.Core.ConfigureAwait(false);
        var environmentSnapshot = await work.Environment.ConfigureAwait(false);
        var weatherResult = await work.Weather.ConfigureAwait(false);
        var horizon = environmentSnapshot.HorizonProfile;
        return core with
        {
            Terrain = horizon,
            TerrainCrossings = TerrainCrossingCalculator.Calculate(core.Path, horizon),
            Weather = weatherResult,
            Environment = environmentSnapshot
        };
    }

    public PlanningRefreshWork StartRefresh(
        PlanningSession session,
        WeatherSettings weatherSettings,
        CancellationToken cancellationToken) =>
        StartRefresh(session, weatherSettings, AppSettings.DefaultCameraHeightAboveGroundMetres,
            cancellationToken);

    public PlanningRefreshWork StartRefresh(
        PlanningSession session,
        WeatherSettings weatherSettings,
        double cameraHeightAboveGroundMetres,
        CancellationToken cancellationToken) => new(
        CalculateCoreSnapshotAsync(session, cancellationToken),
        LoadEnvironmentAsync(session, cameraHeightAboveGroundMetres, cancellationToken),
        LoadWeatherAsync(session, weatherSettings, cancellationToken),
        (bearings, token) => environment.GetPriorityHorizonAsync(session.Observer,
            CreateTerrainRequest(session, cameraHeightAboveGroundMetres), bearings, token));

    public async Task<PlanningSnapshot> CalculateCoreSnapshotAsync(
        PlanningSession session,
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
        await Task.WhenAll(positionTasks.Values.Cast<Task>().Concat(pathTasks.Values)).ConfigureAwait(false);
        var positions = positionTasks.ToDictionary(pair => pair.Key, pair => pair.Value.Result, StringComparer.OrdinalIgnoreCase);
        var position = positions[primaryTarget.Id];
        var path = await pathTasks[primaryTarget.Id].ConfigureAwait(false);
        var sun = positions.GetValueOrDefault("sun") ?? astronomy.Calculate(catalogue.Get("sun"), session.Observer, session.Instant, localDate, session.TimeZoneId);
        var moon = positions.GetValueOrDefault("moon") ?? astronomy.Calculate(catalogue.Get("moon"), session.Observer, session.Instant, localDate, session.TimeZoneId);
        var objectPlans = new List<CelestialObjectPlan>(targets.Length);
        foreach (var target in targets)
            objectPlans.Add(new CelestialObjectPlan(positions[target.Id], await pathTasks[target.Id].ConfigureAwait(false)));
        var pendingHorizon = new TerrainHorizonProfile(session.Observer, [], false,
            "Environmental horizon loading", session.Instant);
        return new PlanningSnapshot(session, position, path, lenses.Calculate(session.Lens), pendingHorizon,
            new TerrainCrossings(null, null), new WeatherResult(DataState.Loading, null, "Loading weather…"),
            new AstronomyContext(sun, moon), objectPlans);
    }

    public Task<PlannerEnvironmentSnapshot> LoadEnvironmentAsync(PlanningSession session,
        CancellationToken cancellationToken) => LoadEnvironmentAsync(session,
        AppSettings.DefaultCameraHeightAboveGroundMetres, cancellationToken);

    public Task<PlannerEnvironmentSnapshot> LoadEnvironmentAsync(PlanningSession session,
        double cameraHeightAboveGroundMetres, CancellationToken cancellationToken) =>
        environment.GetSnapshotAsync(session.Observer,
            CreateTerrainRequest(session, cameraHeightAboveGroundMetres), cancellationToken);

    public Task<TerrainHorizonProfile> PrioritiseTerrainAsync(PlanningSession session,
        IReadOnlyList<double> bearings, CancellationToken cancellationToken) =>
        PrioritiseTerrainAsync(session, AppSettings.DefaultCameraHeightAboveGroundMetres, bearings,
            cancellationToken);

    public Task<TerrainHorizonProfile> PrioritiseTerrainAsync(PlanningSession session,
        double cameraHeightAboveGroundMetres, IReadOnlyList<double> bearings,
        CancellationToken cancellationToken) =>
        environment.GetPriorityHorizonAsync(session.Observer,
            CreateTerrainRequest(session, cameraHeightAboveGroundMetres), bearings, cancellationToken);

    public Task<WeatherResult> LoadWeatherAsync(PlanningSession session, WeatherSettings weatherSettings,
        CancellationToken cancellationToken) =>
        weather.GetWeatherAsync(CreateWeatherRequest(session, weatherSettings, false), cancellationToken);

    public Task<WeatherResult> RefreshWeatherAsync(
        PlanningSession session,
        WeatherSettings weatherSettings,
        CancellationToken cancellationToken) =>
        weather.GetWeatherAsync(CreateWeatherRequest(session, weatherSettings, true), cancellationToken);

    private static WeatherRequest CreateWeatherRequest(PlanningSession session, WeatherSettings settings, bool force) =>
        new(session.Observer, session.Instant, settings.EffectiveFields,
            Math.Clamp(settings.CacheDistanceKilometres, 0, 100), force);

    private static TerrainProfileRequest CreateTerrainRequest(PlanningSession session,
        double cameraHeightAboveGroundMetres) => PlannerEnvironmentService.DefaultHorizonRequest with
    {
        ObserverHeightAboveGroundMetres = AppSettings.NormaliseCameraHeight(cameraHeightAboveGroundMetres),
        ManualGroundElevationOverrideMetres =
            session.EffectiveObserverElevation.ManualGroundElevationOverrideAslMetres
    };
}
