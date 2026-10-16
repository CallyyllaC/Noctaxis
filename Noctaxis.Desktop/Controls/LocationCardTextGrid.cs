using Avalonia;
using Avalonia.Controls;

namespace Noctaxis.Desktop.Controls;

/// <summary>
/// Location card layout: details beside a smaller map preview. Enlarged text takes a larger share of
/// the card, never all of it, so the preview is squeezed rather than covered; each card owns its own
/// column definitions rather than sharing mutable ones.
/// </summary>
public sealed class LocationCardTextGrid : Grid
{
    /// <summary>How much of the card's width the details take, from 0 to 1. Half by default.</summary>
    public static readonly StyledProperty<double> TextShareProperty =
        AvaloniaProperty.Register<LocationCardTextGrid, double>(nameof(TextShare), .5);
    public double TextShare { get => GetValue(TextShareProperty); set => SetValue(TextShareProperty, value); }
    public LocationCardTextGrid() => ColumnDefinitions = new("*,*");
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TextShareProperty) return;
        // Always two real columns: the details can never take the whole card and leave none for the map.
        var share = Math.Clamp(TextShare, .3, .8);
        ColumnDefinitions = new(FormattableString.Invariant($"{share}*,{1 - share}*"));
    }
}
