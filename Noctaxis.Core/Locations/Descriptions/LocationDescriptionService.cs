using System.Globalization;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Locations.Descriptions;

/// <summary>Automatic, human-readable descriptions of a position, from geographic data the application already holds.</summary>
public interface ILocationDescriptionService
{
    /// <summary>The description, or null when the available data supports none (the caller keeps coordinates).</summary>
    Task<LocationDescription?> DescribeAsync(GeoCoordinate coordinate, CancellationToken cancellationToken);
}

/// <summary>
/// The local geographic description engine: feature discovery (<see cref="IGeographicFeatureSource"/>), spatial
/// reasoning (<see cref="GeographicSpatialAnalysis"/>), ranking (<see cref="LocationDescriptionEngine"/>) and
/// composition (<see cref="LocationDescriptionComposer"/>). It sends no request of its own: the source decides what
/// data it reads. Results are kept for nearby repeated queries; a query whose data was unavailable is not kept, so a
/// later one (back online) can succeed.
/// </summary>
public sealed class LocationDescriptionService(IGeographicFeatureSource source, ILogger<LocationDescriptionService> logger)
    : ILocationDescriptionService
{
    /// <summary>A query this close to a remembered one reuses its answer.</summary>
    public const double ReuseDistanceMetres = 15;
    public const int MaximumRemembered = 64;
    /// <summary>
    /// The most one description may take, reading tiles and reasoning included. A label is never worth holding
    /// anything up for: past this the query gives up (no description, coordinates stay) and is not remembered.
    /// </summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(5);
    private sealed record Entry(GeoCoordinate Coordinate, string Culture, LocationDescription? Description);
    private readonly List<Entry> _remembered = [];
    private readonly object _gate = new();

    public async Task<LocationDescription?> DescribeAsync(GeoCoordinate coordinate, CancellationToken cancellationToken)
    {
        coordinate = coordinate.Normalised();
        var culture = CultureInfo.CurrentUICulture.Name;
        lock (_gate)
        {
            var known = _remembered.LastOrDefault(entry => entry.Culture == culture &&
                Angles.GreatCircleDistanceMetres(entry.Coordinate, coordinate) <= ReuseDistanceMetres);
            if (known is not null) return known.Description is null ? null : known.Description with { Coordinate = coordinate };
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeLimit);
        GeographicFeatureSet features;
        LocationDescription? description;
        try
        {
            // Off the caller's thread from the start: with every tile already in memory a source can finish its
            // reading synchronously, and the caller is the UI.
            features = await Task.Run(() => source.GetFeaturesAsync(coordinate, limit.Token), limit.Token).ConfigureAwait(false);
            limit.Token.ThrowIfCancellationRequested();
            description = await Task.Run(() => LocationDescriptionEngine.Describe(coordinate, features, limit.Token), limit.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Location description gave up after {TimeLimit}; the position keeps its coordinates", TimeLimit);
            return null;
        }
        logger.LogDebug("Location description: coverage {Coverage}, {FeatureCount} features, primary {Relation} {Kind}",
            features.Coverage, features.Features.Count, description?.Primary.Relation, description?.Primary.Kind);
        if (features.Coverage != GeographicCoverage.Unavailable)
            lock (_gate)
            {
                _remembered.Add(new Entry(coordinate, culture, description));
                if (_remembered.Count > MaximumRemembered) _remembered.RemoveRange(0, _remembered.Count - MaximumRemembered);
            }
        return description;
    }
}
