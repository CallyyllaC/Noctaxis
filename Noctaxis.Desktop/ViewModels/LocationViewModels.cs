using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Locations;
using Noctaxis.Desktop.Services;
using Avalonia.Media.Imaging;

namespace Noctaxis.Desktop.ViewModels;

public sealed record SavedLocationEdit(string Name, string? Description);

public interface IPlannerDialogService
{
    Task<LocationSearchResult?> ShowLocationSearchAsync(CancellationToken cancellationToken = default);
    Task<SavedLocationEdit?> ShowSavedLocationEditAsync(SavedLocation location, bool isCreateMode = false,
        CancellationToken cancellationToken = default);
    Task<bool> ConfirmDeleteSavedLocationAsync(SavedLocation location, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
    Task<bool> ConfirmRefreshSavedLocationThumbnailsAsync(int locationCount, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
    Task<bool> ConfirmRefreshSavedLocationBuildingCachesAsync(int locationCount,
        CancellationToken cancellationToken = default) => Task.FromResult(true);
    Task<string?> ChooseDemDirectoryAsync(CancellationToken cancellationToken = default);
}

public partial class SavedLocationEditorViewModel : ObservableObject
{
    private readonly SavedLocation _original;
    public SavedLocationEditorViewModel(SavedLocation location)
    {
        _original = location;
        _name = location.Name;
        _description = location.Notes ?? string.Empty;
    }
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string? _validationMessage;
    public string Coordinates => $"{_original.Coordinate.Latitude:F5}°, {_original.Coordinate.Longitude:F5}°";
    public SavedLocationEdit? ValidateAndCreateResult()
    {
        var name = Name.Trim();
        if (name.Length == 0) { ValidationMessage = "Location name is required."; return null; }
        ValidationMessage = null;
        return new SavedLocationEdit(name, string.IsNullOrWhiteSpace(Description) ? null : Description.Trim());
    }
}

public partial class LocationSearchViewModel(ILocationSearchProvider provider, ILogger<LocationSearchViewModel> logger) : ObservableObject
{
    private CancellationTokenSource? _searchCancellation;
    public ObservableCollection<LocationSearchResult> Results { get; } = [];
    public string Attribution => provider.Attribution;
    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string? _errorMessage;

    partial void OnQueryChanged(string value)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        _ = SearchDebouncedAsync(value, _searchCancellation.Token);
    }

    public void Reset()
    {
        _searchCancellation?.Cancel();
        Query = string.Empty;
        Results.Clear();
        ErrorMessage = null;
    }

    private async Task SearchDebouncedAsync(string query, CancellationToken cancellationToken)
    {
        Results.Clear(); ErrorMessage = null;
        if (query.Trim().Length < 2) return;
        try
        {
            await Task.Delay(350, cancellationToken);
            IsSearching = true;
            var results = await provider.SearchAsync(query, cancellationToken);
            foreach (var result in results) Results.Add(result);
            if (results.Count == 0) ErrorMessage = "No matching locations found.";
        }
        catch (OperationCanceledException) { logger.LogDebug("Obsolete location search cancelled"); }
        catch (Exception ex) { ErrorMessage = ex.Message; logger.LogWarning(ex, "Location search failed"); }
        finally { if (!cancellationToken.IsCancellationRequested) IsSearching = false; }
    }
}

public interface ILocationGridItem;

public sealed class AddLocationCardViewModel(Func<Task> addLocation) : ILocationGridItem
{
    public IAsyncRelayCommand AddCommand { get; } = new AsyncRelayCommand(addLocation);
}

public partial class LocationCardViewModel : ObservableObject, ILocationGridItem
{
    private readonly Func<LocationCardViewModel, Task> _open;
    private readonly Func<LocationCardViewModel, Task> _edit;
    private readonly Func<LocationCardViewModel, Task> _delete;
    private readonly Func<LocationCardViewModel, Task> _toggleFavourite;
    private readonly ILocationMapThumbnailService _mapThumbnails;
    private readonly Func<DateTimeOffset> _now;
    private CancellationTokenSource? _thumbnailCancellation;

    public LocationCardViewModel(SavedLocation location, bool isSelected,
        ILocationMapThumbnailService mapThumbnails, Func<DateTimeOffset> now,
        Func<LocationCardViewModel, Task> open,
        Func<LocationCardViewModel, Task> edit, Func<LocationCardViewModel, Task> delete,
        Func<LocationCardViewModel, Task> toggleFavourite)
    {
        Location = location; _isFavourite = location.IsFavourite; _isSelected = isSelected;
        _mapThumbnails = mapThumbnails; _now = now;
        _open = open; _edit = edit; _delete = delete; _toggleFavourite = toggleFavourite;
        _ = LoadMapThumbnailAsync();
    }

    public SavedLocation Location { get; private set; }
    public Guid Id => Location.Id;
    public string Name => Location.Name;
    public string Coordinates => $"{Location.Coordinate.Latitude:F4}°, {Location.Coordinate.Longitude:F4}°";
    public string? RegionDescription => Location.RegionDescription;
    public bool HasRegionDescription => !string.IsNullOrWhiteSpace(Location.RegionDescription);
    public string? UserDescription => Location.Notes;
    public bool HasUserDescription => !string.IsNullOrWhiteSpace(Location.Notes);
    public string Eyebrow => IsFavourite ? "FAVOURITE LOCATION" : "SAVED LOCATION";
    public bool IsNotFavourite => !IsFavourite;
    public string FavouriteAccessibleName => IsFavourite ? "Remove from favourites" : "Add to favourites";
    public string? LastUsedText => FormatLastUsed(Location.LastUsedUtc, _now());
    public bool HasLastUsed => LastUsedText is not null;
    [ObservableProperty] private bool _isFavourite;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private Bitmap? _mapThumbnail;
    [ObservableProperty] private bool _isMapThumbnailLoading;
    [ObservableProperty] private SavedLocationThumbnailMetadata? _thumbnailMetadata;
    [ObservableProperty] private SavedLocationThumbnailResult? _lastThumbnailResult;
    public string FavouriteLabel => IsFavourite ? "Unfavourite" : "Favourite";
    public bool HasSemanticWarning => ThumbnailMetadata?.FeatureFetchStatus is
        nameof(MapFeatureFetchStatus.Unavailable) or nameof(MapFeatureFetchStatus.CachedPrevious) or
        nameof(MapFeatureFetchStatus.PartialWithoutBuildings) or "BuildingsOmitted";
    public string SemanticStatusSummary
    {
        get
        {
            if (ThumbnailMetadata is null) return "Semantic overlay status unavailable";
            var reason = ThumbnailMetadata.FeatureFailureReason;
            return ThumbnailMetadata.FeatureFetchStatus switch
            {
                nameof(MapFeatureFetchStatus.PartialWithoutBuildings) =>
                    reason ?? "Roads and waterways loaded; buildings were skipped.",
                "BuildingsOmitted" => reason ?? "Roads and waterways loaded; buildings were skipped.",
                nameof(MapFeatureFetchStatus.CachedPrevious) =>
                    "Previous semantic overlay retained" + (string.IsNullOrWhiteSpace(reason) ? "." : $": {reason}"),
                nameof(MapFeatureFetchStatus.Unavailable) =>
                    "Semantic overlay unavailable" + (string.IsNullOrWhiteSpace(reason) ? "." : $": {reason}"),
                _ => "Semantic overlay available"
            };
        }
    }
    public bool HasBuildingWarning => ThumbnailMetadata?.BuildingStarStatus is
        nameof(BuildingStarStatus.Partial) or nameof(BuildingStarStatus.Unavailable) or
        nameof(BuildingStarStatus.Cached);
    public string BuildingStatusSummary
    {
        get
        {
            if (ThumbnailMetadata is null) return "Building-star status unavailable";
            var reason = ThumbnailMetadata.BuildingFailureReason;
            return ThumbnailMetadata.BuildingStarStatus switch
            {
                nameof(BuildingStarStatus.Complete) =>
                    $"Building stars available ({ThumbnailMetadata.BuildingCount ?? 0:N0})",
                nameof(BuildingStarStatus.Cached) => "Previous building stars retained" +
                    (string.IsNullOrWhiteSpace(reason) ? "." : $": {reason}"),
                nameof(BuildingStarStatus.Partial) => reason ??
                    $"Partial building stars: {ThumbnailMetadata.BuildingCompletedRegionCount} of {ThumbnailMetadata.BuildingRegionCount} regions loaded.",
                _ => "Building stars unavailable" +
                    (string.IsNullOrWhiteSpace(reason) ? "." : $": {reason}")
            };
        }
    }

    partial void OnThumbnailMetadataChanged(SavedLocationThumbnailMetadata? value)
    {
        OnPropertyChanged(nameof(HasSemanticWarning));
        OnPropertyChanged(nameof(SemanticStatusSummary));
        OnPropertyChanged(nameof(HasBuildingWarning));
        OnPropertyChanged(nameof(BuildingStatusSummary));
    }

    public void Update(SavedLocation location)
    {
        var coordinateChanged = location.Coordinate != Location.Coordinate;
        Location = location; IsFavourite = location.IsFavourite;
        OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(Coordinates));
        OnPropertyChanged(nameof(RegionDescription)); OnPropertyChanged(nameof(HasRegionDescription));
        OnPropertyChanged(nameof(UserDescription)); OnPropertyChanged(nameof(HasUserDescription));
        OnPropertyChanged(nameof(FavouriteLabel));
        OnPropertyChanged(nameof(Eyebrow)); OnPropertyChanged(nameof(IsNotFavourite));
        OnPropertyChanged(nameof(FavouriteAccessibleName)); OnPropertyChanged(nameof(LastUsedText));
        OnPropertyChanged(nameof(HasLastUsed));
        if (coordinateChanged) _ = LoadMapThumbnailAsync();
    }

    [RelayCommand] private Task Open() => _open(this);
    [RelayCommand] private Task Edit() => _edit(this);
    [RelayCommand] private Task Delete() => _delete(this);
    [RelayCommand] private Task ToggleFavourite() => _toggleFavourite(this);
    partial void OnIsFavouriteChanged(bool value)
    {
        OnPropertyChanged(nameof(FavouriteLabel)); OnPropertyChanged(nameof(Eyebrow));
        OnPropertyChanged(nameof(IsNotFavourite)); OnPropertyChanged(nameof(FavouriteAccessibleName));
    }

    [RelayCommand]
    private Task RefreshMapThumbnail() => LoadMapThumbnailAsync(SavedLocationMapRefreshMode.RefreshSource);

    [RelayCommand]
    private Task RefreshBuildingCache() => LoadMapThumbnailAsync(SavedLocationMapRefreshMode.RefreshBuildings);

    public Task<bool> RefreshMapThumbnailAsync(bool forceRefresh) => LoadMapThumbnailAsync(
        forceRefresh ? SavedLocationMapRefreshMode.RefreshSource : SavedLocationMapRefreshMode.UseCache);
    public Task<bool> RefreshMapThumbnailAsync(SavedLocationMapRefreshMode mode) => LoadMapThumbnailAsync(mode);
    public async Task<SavedLocationThumbnailResult?> RefreshMapThumbnailWithResultAsync(
        SavedLocationMapRefreshMode mode)
    {
        await LoadMapThumbnailAsync(mode);
        return LastThumbnailResult;
    }

    private Task<bool> LoadMapThumbnailAsync() => LoadMapThumbnailAsync(SavedLocationMapRefreshMode.UseCache);

    private async Task<bool> LoadMapThumbnailAsync(SavedLocationMapRefreshMode mode)
    {
        LastThumbnailResult = null;
        _thumbnailCancellation?.Cancel();
        _thumbnailCancellation?.Dispose();
        _thumbnailCancellation = new CancellationTokenSource();
        var cancellationToken = _thumbnailCancellation.Token;
        var requestedLocation = Location;
        IsMapThumbnailLoading = true;
        try
        {
            var result = await _mapThumbnails.GetThumbnailAsync(requestedLocation, mode, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is null) return false;
            if (Location.Id != result.Metadata.LocationId ||
                BitConverter.DoubleToInt64Bits(Location.Coordinate.Latitude) != BitConverter.DoubleToInt64Bits(result.Metadata.Latitude) ||
                BitConverter.DoubleToInt64Bits(Location.Coordinate.Longitude) != BitConverter.DoubleToInt64Bits(result.Metadata.Longitude))
                return false;
            LastThumbnailResult = result;
            ThumbnailMetadata = result.Metadata;
            try
            {
                var bitmap = new Bitmap(result.ImagePath);
                var previous = MapThumbnail;
                MapThumbnail = bitmap;
                previous?.Dispose();
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The generated disk asset remains valid even when this UI process cannot decode it yet.
                // Preserve the currently displayed bitmap; a later page load can retry the disk asset.
            }
            return result.RefreshSucceeded;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return false; }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) IsMapThumbnailLoading = false;
        }
    }

    internal static string? FormatLastUsed(DateTimeOffset? value, DateTimeOffset now)
    {
        if (!value.HasValue) return null;
        var elapsed = now - value.Value;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed < TimeSpan.FromMinutes(2)) return "Used just now";
        if (elapsed < TimeSpan.FromHours(1)) return $"Used {(int)elapsed.TotalMinutes} min ago";
        if (elapsed < TimeSpan.FromHours(24)) return $"Used {(int)elapsed.TotalHours} hr ago";
        if (elapsed < TimeSpan.FromDays(14)) return $"Used {(int)elapsed.TotalDays} days ago";
        return $"Used {value.Value.ToLocalTime():dd MMM yyyy}";
    }
}

public enum LocationSortMode { RecentlyUsed, Name, DateAdded }
public sealed record LocationSortOption(LocationSortMode Mode, string Label);

public partial class LocationsViewModel : ObservableObject
{
    private readonly ILocationResolver _resolver;
    private readonly IDeviceLocationAvailabilityService _deviceAvailability;
    private readonly Func<LocationResolution, Task> _openCustom;
    private readonly Func<LocationSearchResult, bool, Task> _useSearchResult;
    private readonly Func<Task<LocationSearchResult?>> _showSearch;
    private readonly Func<LocationCardViewModel, Task> _openSaved;
    private readonly Func<LocationCardViewModel, Task> _edit;
    private readonly Func<LocationCardViewModel, Task> _delete;
    private readonly Func<LocationCardViewModel, Task> _toggleFavourite;
    private readonly ILocationMapThumbnailService _mapThumbnails;
    private readonly Func<DateTimeOffset> _now;
    private GeoCoordinate? _lastCustomCoordinate;
    private readonly AddLocationCardViewModel _addLocationCard;

    public LocationsViewModel(ILocationResolver resolver, IDeviceLocationAvailabilityService deviceAvailability,
        Func<LocationResolution, Task> openCustom, Func<LocationSearchResult, bool, Task> useSearchResult,
        Func<Task<LocationSearchResult?>> showSearch,
        Func<LocationCardViewModel, Task> openSaved, Func<LocationCardViewModel, Task> edit,
        Func<LocationCardViewModel, Task> delete, Func<LocationCardViewModel, Task> toggleFavourite,
        Func<Task> addLocation, ILocationMapThumbnailService mapThumbnails, Func<DateTimeOffset> now)
    {
        _resolver = resolver; _deviceAvailability = deviceAvailability; _openCustom = openCustom;
        _useSearchResult = useSearchResult; _showSearch = showSearch;
        _openSaved = openSaved; _edit = edit; _delete = delete; _toggleFavourite = toggleFavourite;
        _mapThumbnails = mapThumbnails; _now = now;
        _addLocationCard = new AddLocationCardViewModel(addLocation);
        _selectedSortOption = SortOptions[0];
        GridItems.Add(_addLocationCard);
    }

    public ObservableCollection<LocationCardViewModel> Saved { get; } = [];
    public ObservableCollection<ILocationGridItem> GridItems { get; } = [];
    public IReadOnlyList<LocationSortOption> SortOptions { get; } =
    [
        new(LocationSortMode.RecentlyUsed, "Recently used"),
        new(LocationSortMode.Name, "Name"),
        new(LocationSortMode.DateAdded, "Date added")
    ];
    public bool HasSavedLocations => Saved.Count > 0;
    public bool IsFirstRun => Saved.Count == 0;
    [ObservableProperty] private LocationSortOption _selectedSortOption;
    [ObservableProperty] private string? _resolutionMessage;
    [ObservableProperty] private bool _canUseDeviceLocation;
    [ObservableProperty] private string? _deviceLocationUnavailableReason;
    [ObservableProperty] private string? _thumbnailAttributionSummary;
    public bool HasThumbnailAttributions => !string.IsNullOrWhiteSpace(ThumbnailAttributionSummary);

    public void Load(IEnumerable<SavedLocation> locations, GeoCoordinate? lastCustomCoordinate, Guid? selectedLocationId = null)
    {
        _lastCustomCoordinate = lastCustomCoordinate;
        Saved.Clear();
        foreach (var location in locations)
            Saved.Add(CreateCard(location, location.Id == selectedLocationId));
        ApplySort();
        RefreshThumbnailAttributions();
        OnPropertyChanged(nameof(HasSavedLocations)); OnPropertyChanged(nameof(IsFirstRun));
    }

    public async Task RefreshDeviceAvailabilityAsync(CancellationToken cancellationToken)
    {
        var availability = await _deviceAvailability.GetAvailabilityAsync(cancellationToken);
        CanUseDeviceLocation = availability.CanRequest;
        DeviceLocationUnavailableReason = availability.CanRequest ? null : availability.Reason ?? "Device location is unavailable.";
        UseDeviceLocationCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUseDeviceLocation))]
    private async Task UseDeviceLocation()
    {
        var result = await _resolver.ResolveDeviceOrFallbackAsync(_lastCustomCoordinate, CancellationToken.None);
        if (result.Source != LocationResolutionSource.OperatingSystemLocation)
        {
            ResolutionMessage = "Device location could not be resolved. The current planning location was not changed.";
            return;
        }
        _lastCustomCoordinate = result.Coordinate;
        ResolutionMessage = "Device location selected.";
        await _openCustom(result);
    }

    [RelayCommand]
    private async Task SearchForPlace()
    {
        var result = await _showSearch();
        if (result is null) return;
        _lastCustomCoordinate = result.Coordinate;
        await _useSearchResult(result, false);
    }

    private LocationCardViewModel CreateCard(SavedLocation location, bool isSelected)
    {
        var card = new LocationCardViewModel(location, isSelected, _mapThumbnails, _now,
            _openSaved, _edit, _delete, _toggleFavourite);
        card.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LocationCardViewModel.ThumbnailMetadata)) RefreshThumbnailAttributions();
        };
        return card;
    }

    public void RefreshThumbnailAttributions()
    {
        var values = Saved.SelectMany(card => AttributionEntries(card.ThumbnailMetadata))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Text))
            .DistinctBy(entry => ($"{entry.Text.Trim()}|{entry.Url?.Trim()}"),
                StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Text)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase);
        ThumbnailAttributionSummary = string.Join("  |  ", values);
        OnPropertyChanged(nameof(HasThumbnailAttributions));
    }

    private static IEnumerable<(string Provider, string? Url, string Text)> AttributionEntries(
        SavedLocationThumbnailMetadata? metadata)
    {
        if (metadata is null) yield break;
        if (!string.IsNullOrWhiteSpace(metadata.AttributionText))
            yield return (metadata.ProviderId, metadata.AttributionUrl, metadata.AttributionText);
        if (!string.IsNullOrWhiteSpace(metadata.FeatureAttributionText))
            yield return (metadata.FeatureProviderId ?? "openstreetmap-overpass",
                metadata.FeatureAttributionUrl, metadata.FeatureAttributionText);
        if (!string.IsNullOrWhiteSpace(metadata.BuildingAttributionText))
            yield return (metadata.BuildingProviderId ?? "openstreetmap-overpass-buildings",
                metadata.BuildingAttributionUrl, metadata.BuildingAttributionText);
    }

    partial void OnSelectedSortOptionChanged(LocationSortOption value) => ApplySort();

    private void ApplySort()
    {
        if (Saved.Count < 2)
        {
            RebuildGridItems();
            return;
        }
        var mode = SelectedSortOption?.Mode ?? LocationSortMode.RecentlyUsed;
        IOrderedEnumerable<LocationCardViewModel> ordered = Saved.OrderByDescending(card => card.IsFavourite);
        ordered = mode switch
        {
            LocationSortMode.Name => ordered.ThenBy(card => card.Name, StringComparer.CurrentCultureIgnoreCase),
            LocationSortMode.DateAdded => ordered
                .ThenByDescending(card => card.Location.DateAddedUtc ?? DateTimeOffset.MinValue)
                .ThenByDescending(card => card.Location.SortOrder),
            _ => ordered.ThenByDescending(card => card.Location.LastUsedUtc ?? DateTimeOffset.MinValue)
        };
        Reorder(ordered.ThenBy(card => card.Name, StringComparer.CurrentCultureIgnoreCase));
    }
    private void Reorder(IEnumerable<LocationCardViewModel> ordered)
    {
        var values = ordered.ToArray(); Saved.Clear(); foreach (var value in values) Saved.Add(value);
        RebuildGridItems();
    }

    private void RebuildGridItems()
    {
        GridItems.Clear();
        foreach (var card in Saved) GridItems.Add(card);
        GridItems.Add(_addLocationCard);
    }
}
