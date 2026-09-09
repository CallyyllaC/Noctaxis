using Avalonia.Headless.XUnit;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Services;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    [AvaloniaFact]
    public async Task ResetSearchRejectsLateResultsFromProviderIgnoringCancellation()
    {
        var provider = new PendingReviewSearch();
        var search = new Noctaxis.Desktop.ViewModels.LocationSearchViewModel(provider,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Noctaxis.Desktop.ViewModels.LocationSearchViewModel>.Instance);
        search.Query = "London";
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = search.WaitForSearchAsync();
        search.Reset();
        provider.Result.SetResult([new("old", "Old", new(51, 0), null, null, "UTC", "Test")]);
        await pending;
        Assert.Empty(search.Results);
        Assert.False(search.IsSearching);
        Assert.Null(search.ErrorMessage);
    }

    private sealed class PendingReviewSearch : Noctaxis.Core.Locations.ILocationSearchProvider
    {
        public string Attribution => "Test";
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<LocationSearchResult>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<LocationSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            return Result.Task;
        }
    }

    [AvaloniaFact]
    public async Task LocationReloadReusesUnchangedCardsAndCancelsRemovedCards()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var thumbnails = new PendingReviewThumbnails();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(4, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2026, 9, 9, 20, 0), "UTC"), null)),
            new FakeExporter(), thumbnails: thumbnails);
        await vm.InitializeAsync();
        var location = new SavedLocation(Guid.NewGuid(), "Example", new GeoCoordinate(51, 0), "UTC");
        vm.Locations.Load([location], null);
        var original = Assert.Single(vm.Locations.Saved);
        vm.Locations.Load([location with { Name = "Renamed" }], null, location.Id);
        Assert.Same(original, Assert.Single(vm.Locations.Saved));
        Assert.Equal("Renamed", original.Name);
        Assert.True(original.IsSelected);
        Assert.Single(thumbnails.Tokens);
        vm.Locations.Load([], null);
        Assert.True(thumbnails.Tokens[0].IsCancellationRequested);
        thumbnails.Complete();
    }

    private sealed class PendingReviewThumbnails : ILocationMapThumbnailService
    {
        public string StorageDirectory => "";
        public List<CancellationToken> Tokens { get; } = [];
        private readonly TaskCompletionSource<SavedLocationThumbnailResult?> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SavedLocationThumbnailResult?> GetThumbnailAsync(SavedLocation location, bool forceRefresh, CancellationToken cancellationToken)
            => GetThumbnailAsync(location, SavedLocationMapRefreshMode.UseCache, cancellationToken);
        public Task<SavedLocationThumbnailResult?> GetThumbnailAsync(SavedLocation location, SavedLocationMapRefreshMode mode, CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            return _pending.Task;
        }
        public void Complete() => _pending.TrySetResult(null);
    }
}
