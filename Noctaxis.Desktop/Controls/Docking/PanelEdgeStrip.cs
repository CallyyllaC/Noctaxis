using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.Controls.Docking;

/// <summary>
/// The tabs for the unpinned panels on one edge of the workspace. An unpinned panel is not gone: its
/// name stays on the edge it was docked to, and one tap opens it over the map.
/// </summary>
/// <remarks>
/// The tabs carry the panel's own name at the page's text size, so they grow with the application's text
/// scale. On the top and bottom edges the name reads across; on the right edge it is turned to read down
/// the edge (top to bottom), so the strip stays a narrow column. The strip carries its edge as a class
/// (<see cref="EdgeClass"/>) for the styles to select on; a left edge can be turned the other way the same
/// way, and is left reading across for now. The strip is
/// created and placed by <see cref="DockWorkspace"/>; it is empty, and takes no space at all, when
/// nothing on its edge is unpinned.
/// </remarks>
public sealed class PanelEdgeStrip : ItemsControl
{
    /// <summary>The widest a left or right strip may become, however long the panel names are.</summary>
    public const double MaximumSideExtent = 168;

    public static readonly StyledProperty<PanelDockSide> SideProperty =
        AvaloniaProperty.Register<PanelEdgeStrip, PanelDockSide>(nameof(Side));

    public PanelDockSide Side
    {
        get => GetValue(SideProperty);
        set => SetValue(SideProperty, value);
    }

    /// <summary>
    /// The strip is an items list and is templated as one. Without this it would have no template at
    /// all — a control's style key is its own type — and the tabs would never be built.
    /// </summary>
    protected override Type StyleKeyOverride => typeof(ItemsControl);

    /// <summary>The class the styles in DockingStyles select on, since the style key is ItemsControl.</summary>
    public const string StyleClass = "panelEdgeStrip";

    /// <summary>The class naming the strip's edge: <c>rightEdge</c>, <c>leftEdge</c>, <c>topEdge</c> or <c>bottomEdge</c>.</summary>
    public static string EdgeClass(PanelDockSide side) => side.ToString().ToLowerInvariant() + "Edge";

    public PanelEdgeStrip()
    {
        Classes.Add(StyleClass);
        ApplyOrientation(Side);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SideProperty) ApplyOrientation(change.GetNewValue<PanelDockSide>());
    }

    private void ApplyOrientation(PanelDockSide side)
    {
        var across = side is PanelDockSide.Top or PanelDockSide.Bottom;
        foreach (var edge in Enum.GetValues<PanelDockSide>()) Classes.Set(EdgeClass(edge), edge == side);
        ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel
        {
            Orientation = across ? Orientation.Horizontal : Orientation.Vertical,
            Spacing = 4
        });
    }
}
