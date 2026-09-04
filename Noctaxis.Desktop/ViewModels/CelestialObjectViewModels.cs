using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.ViewModels;

public sealed record ObjectTypeOption(string Label, AstralTargetCategory? Value);

public partial class CelestialObjectItemViewModel : ObservableObject
{
    private readonly Action<CelestialObjectItemViewModel> _changed;
    private readonly Action<CelestialObjectItemViewModel> _makePrimary;
    private readonly Action<CelestialObjectItemViewModel> _remove;
    private readonly Action<CelestialObjectItemViewModel, int> _move;
    private bool _suppressVisibility;

    public CelestialObjectItemViewModel(AstralTarget target, bool isVisible, int order, bool isPrimary,
        string colour, Action<CelestialObjectItemViewModel> changed,
        Action<CelestialObjectItemViewModel> makePrimary, Action<CelestialObjectItemViewModel> remove,
        Action<CelestialObjectItemViewModel, int>? move = null)
    {
        Target = target; _isVisible = isVisible; Order = order; _isPrimary = isPrimary; Colour = colour;
        _changed = changed; _makePrimary = makePrimary; _remove = remove; _move = move ?? ((_, _) => { });
    }

    public AstralTarget Target { get; }
    public string TargetId => Target.Id;
    public string DisplayName => Target.DisplayName;
    public string Detail => string.Join(" · ", new[] { Target.PrimaryIdentifier, Target.Constellation }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public int Order { get; set; }
    public string Colour { get; }
    public bool CanRemove => !Target.IsSun && !Target.IsMoon;
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _isPrimary;
    public string PrimaryLabel => IsPrimary ? "Primary" : "Set primary";

    public void SetVisibilitySilently(bool value)
    {
        _suppressVisibility = true;
        IsVisible = value;
        _suppressVisibility = false;
    }

    partial void OnIsVisibleChanged(bool value) { if (!_suppressVisibility) _changed(this); }
    partial void OnIsPrimaryChanged(bool value) => OnPropertyChanged(nameof(PrimaryLabel));
    [RelayCommand] private void MakePrimary() => _makePrimary(this);
    [RelayCommand] private void Remove() { if (CanRemove) _remove(this); }
    [RelayCommand] private void MoveUp() => _move(this, -1);
    [RelayCommand] private void MoveDown() => _move(this, 1);
}

public partial class CelestialSearchViewModel : ObservableObject
{
    private readonly ITargetSearchService _search;
    private CancellationTokenSource? _cancellation;
    private bool _suppressSearch;

    public CelestialSearchViewModel(ITargetSearchService search, ITargetCatalogue catalogue)
    {
        _search = search;
        ObjectTypes = [new ObjectTypeOption("All object types", null),
            .. catalogue.ObjectTypes.Select(type => new ObjectTypeOption(FormatType(type), type))];
        Constellations = ["All constellations", .. catalogue.Constellations];
        DesignationFamilies = ["All designations", .. catalogue.DesignationFamilies];
        _selectedObjectType = ObjectTypes[0];
        _selectedConstellation = Constellations[0];
        _selectedDesignationFamily = DesignationFamilies[0];
        Results.CollectionChanged += (_, _) => NotifyResultState();
    }

    public ObservableCollection<AstralTarget> Results { get; } = [];
    public string Attribution => OpenNgcTargetCatalogue.Attribution;
    public IReadOnlyList<ObjectTypeOption> ObjectTypes { get; }
    public IReadOnlyList<string> Constellations { get; }
    public IReadOnlyList<string> DesignationFamilies { get; }
    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private ObjectTypeOption _selectedObjectType;
    [ObservableProperty] private string _selectedConstellation;
    [ObservableProperty] private string _selectedDesignationFamily;
    [ObservableProperty] private bool _isSearching;

    public bool HasActiveFilters => SelectedObjectType.Value.HasValue ||
                                    SelectedConstellation != Constellations[0] ||
                                    SelectedDesignationFamily != DesignationFamilies[0];
    public bool HasResults => Results.Count > 0;
    public bool ShowEmptyState => !IsSearching && !HasResults && (Query.Trim().Length >= 2 || HasActiveFilters);
    public string EmptyStateMessage => "No celestial objects match the current search and filters.";

    partial void OnQueryChanged(string value) { NotifyResultState(); if (!_suppressSearch) QueueSearch(); }
    partial void OnSelectedObjectTypeChanged(ObjectTypeOption value) => FilterChanged();
    partial void OnSelectedConstellationChanged(string value) => FilterChanged();
    partial void OnSelectedDesignationFamilyChanged(string value) => FilterChanged();
    partial void OnIsSearchingChanged(bool value) => NotifyResultState();

    public void ClearResults()
    {
        _cancellation?.Cancel();
        _suppressSearch = true;
        Query = string.Empty;
        _suppressSearch = false;
        Results.Clear();
        if (HasActiveFilters) QueueSearch();
    }

    [RelayCommand]
    private void ResetFilters()
    {
        _suppressSearch = true;
        SelectedObjectType = ObjectTypes[0];
        SelectedConstellation = Constellations[0];
        SelectedDesignationFamily = DesignationFamilies[0];
        _suppressSearch = false;
        OnPropertyChanged(nameof(HasActiveFilters));
        QueueSearch();
    }

    private void FilterChanged()
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        NotifyResultState();
        if (!_suppressSearch) QueueSearch();
    }

    private void QueueSearch()
    {
        _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = new CancellationTokenSource();
        _ = SearchAsync(_cancellation.Token);
    }

    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        var hasFilter = HasActiveFilters;
        if (Query.Trim().Length < 2 && !hasFilter)
        {
            Results.Clear();
            IsSearching = false;
            return;
        }
        try
        {
            await Task.Delay(150, cancellationToken);
            IsSearching = true;
            var query = new CatalogueSearchQuery(Query, SelectedObjectType.Value,
                SelectedConstellation == Constellations[0] ? null : SelectedConstellation,
                SelectedDesignationFamily == DesignationFamilies[0] ? null : SelectedDesignationFamily);
            var results = await _search.SearchAsync(query, 12, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Results.Clear();
            foreach (var target in results) Results.Add(target);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) IsSearching = false;
        }
    }

    private void NotifyResultState()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private static string FormatType(AstralTargetCategory type)
    {
        var text = type.ToString();
        return string.Concat(text.Select((character, index) => index > 0 && char.IsUpper(character) ? " " + character : character.ToString()));
    }
}
