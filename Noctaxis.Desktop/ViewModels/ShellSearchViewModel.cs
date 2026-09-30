using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctaxis.Core.Localisation;

namespace Noctaxis.Desktop.ViewModels;

/// <summary>What the shared search field searches. One per page that has a search.</summary>
public enum ShellSearchScope
{
    /// <summary>Filters the saved-location cards as the user types.</summary>
    SavedLocations,
    /// <summary>Hands the typed text to the existing place and coordinate search when it is submitted.</summary>
    Places,
    /// <summary>Settings: no search yet. See docs/settings.md and the note on <see cref="IsAvailable"/>.</summary>
    Settings
}

/// <summary>
/// The one search field in the application header. It is a single control and a single piece of state;
/// what it does and what it invites the user to type follow the page that is open. Pages do not each
/// own a search box that happens to look the same.
/// </summary>
/// <remarks>
/// <para>A scope that filters (saved locations) does so as the text changes. A scope that searches
/// elsewhere (places) does so when the text is submitted, and reuses the workflow that already exists
/// rather than a second copy of it.</para>
/// <para>Text does not carry between pages: moving to another page clears the field, so a filter can
/// never be left applied to a page the user cannot see it on.</para>
/// </remarks>
public sealed partial class ShellSearchViewModel : ObservableObject
{
    private readonly Action<string> _filterSavedLocations;
    private readonly Func<string, Task> _searchPlaces;
    private bool _changingScope;

    public ShellSearchViewModel(Action<string> filterSavedLocations, Func<string, Task> searchPlaces)
    {
        _filterSavedLocations = filterSavedLocations;
        _searchPlaces = searchPlaces;
    }

    [ObservableProperty] private string _text = string.Empty;

    /// <summary>The page the field is serving. Setting it clears whatever was typed for the last one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Placeholder)), NotifyPropertyChangedFor(nameof(AccessibleName))]
    [NotifyPropertyChangedFor(nameof(IsAvailable))]
    private ShellSearchScope _scope = ShellSearchScope.SavedLocations;

    /// <summary>
    /// Whether this page has a search. Settings does not yet: rather than show a field that looks as
    /// though it works and does nothing, the field is not shown there at all.
    /// </summary>
    public bool IsAvailable => Scope is not ShellSearchScope.Settings;

    public bool HasText => Text.Length > 0;

    /// <summary>What to type, in the words of the page that is open.</summary>
    public string Placeholder => Strings.Get(Scope switch
    {
        ShellSearchScope.Places => "Shell_Search_Places_Placeholder",
        ShellSearchScope.Settings => "Shell_Search_Settings_Placeholder",
        _ => "Shell_Search_SavedLocations_Placeholder"
    });

    /// <summary>The field's name for assistive technology. It says the same thing as the placeholder.</summary>
    public string AccessibleName => Placeholder;

    /// <summary>The page the field is on, by index, as the navigation orders them.</summary>
    public void FollowPage(int pageIndex) => Scope = pageIndex switch
    {
        1 => ShellSearchScope.Places,
        2 => ShellSearchScope.Settings,
        _ => ShellSearchScope.SavedLocations
    };

    /// <summary>Enter: hands the text to a scope that searches rather than filters.</summary>
    [RelayCommand]
    private async Task Submit()
    {
        if (Scope != ShellSearchScope.Places || Text.Trim().Length == 0) return;
        var query = Text.Trim();
        // The place search opens its own surface; the field has done its part and goes back to empty.
        Text = string.Empty;
        await _searchPlaces(query);
    }

    /// <summary>Escape, or the clear button: empties the field and undoes whatever it was doing.</summary>
    [RelayCommand]
    private void Clear() => Text = string.Empty;

    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasText));
        if (_changingScope) return;
        if (Scope == ShellSearchScope.SavedLocations) _filterSavedLocations(value);
    }

    partial void OnScopeChanged(ShellSearchScope value)
    {
        _changingScope = true;
        try { Text = string.Empty; }
        finally { _changingScope = false; }
        // Leaving the Locations page lifts its filter, so the list is whole when the user returns.
        _filterSavedLocations(string.Empty);
    }
}
