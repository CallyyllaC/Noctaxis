using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.VisualTree;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.ViewModels;

namespace Noctaxis.Desktop.Controls.Docking;

/// <summary>
/// The chrome every Planner panel wears: a title, the controls that pin, unpin, float and dock it, the
/// grip that resizes it, and the drag that moves it. The content inside is a view of one feature and
/// never learns which of those states it is in; it is placed by <see cref="DockWorkspace"/> and
/// described by <see cref="PlannerPanelViewModel"/>.
/// </summary>
/// <remarks>
/// <para>Dragging and the resize grip are accelerators. Everything they do is also a command on the
/// panel menu in the title bar, so a panel can be moved, docked, pinned and unpinned entirely by
/// tapping or by keyboard, and no state is reachable only by hovering or only by dragging.</para>
/// <para>A panel whose content cannot usefully be narrower or shorter than some size says so with the
/// ordinary <see cref="Avalonia.Layout.Layoutable.MinWidth"/> and <see cref="Avalonia.Layout.Layoutable.MinHeight"/>,
/// in the panel's own outer pixels, where it is declared. The workspace treats them as the floor for
/// the dock it is in, for its share of a dock it shares, for its floating size and for both grips.
/// Panels that set neither behave as they always have.</para>
/// <para>The host keeps its place in the visual tree for the window's lifetime. Changing a panel's
/// state moves where the workspace arranges it, never which parent it belongs to, so the content is
/// never torn down and rebuilt and never loses what the user typed into it.</para>
/// </remarks>
public class PlannerPanelHost : ContentControl
{
    /// <summary>How far the pointer must travel before a press on the title bar becomes a drag.</summary>
    private const double DragThreshold = 10;

    /// <summary>The grip's thickness. Narrow to look at, and deliberately wide enough to hit with a finger.</summary>
    public const double GripThickness = 14;

    private Control? _titleBar;
    private Control? _dockGrip;
    private Control? _floatGrip;
    private Point _dragOrigin;
    private Rect _dragStartBounds;
    private double _dragStartSize;
    private bool _dragging;
    private bool _resizing;

    /// <summary>Names the panel in the saved layout; it is matched against the layout's panel ids.</summary>
    public static readonly StyledProperty<string> PanelIdProperty =
        AvaloniaProperty.Register<PlannerPanelHost, string>(nameof(PanelId), string.Empty);

    /// <summary>The panel's state. Set by the workspace from its layout, never in markup.</summary>
    public static readonly StyledProperty<PlannerPanelViewModel?> PanelProperty =
        AvaloniaProperty.Register<PlannerPanelHost, PlannerPanelViewModel?>(nameof(Panel));

    public string PanelId
    {
        get => GetValue(PanelIdProperty);
        set => SetValue(PanelIdProperty, value);
    }

    public PlannerPanelViewModel? Panel
    {
        get => GetValue(PanelProperty);
        set => SetValue(PanelProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(PlannerPanelHost);

    internal DockWorkspace? Workspace => this.FindAncestorOfType<DockWorkspace>();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != PanelProperty) return;
        // Presentation follows the panel's state, so a hidden or collapsed panel costs no layout and
        // the workspace never has to reach in and switch its children off.
        if (change.GetNewValue<PlannerPanelViewModel?>() is { } panel)
            Bind(IsVisibleProperty, new Binding(nameof(PlannerPanelViewModel.IsPresented)) { Source = panel });
        else IsVisible = false;
        UpdateGrips();
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        Detach(_titleBar, TitlePressed, TitleMoved, TitleReleased);
        Detach(_dockGrip, GripPressed, GripMoved, GripReleased);
        Detach(_floatGrip, GripPressed, GripMoved, GripReleased);
        _titleBar = e.NameScope.Find<Control>("PART_TitleBar");
        _dockGrip = e.NameScope.Find<Control>("PART_DockGrip");
        _floatGrip = e.NameScope.Find<Control>("PART_FloatGrip");
        Attach(_titleBar, TitlePressed, TitleMoved, TitleReleased);
        Attach(_dockGrip, GripPressed, GripMoved, GripReleased);
        Attach(_floatGrip, GripPressed, GripMoved, GripReleased);
        UpdateGrips();
    }

    private static void Attach(Control? control, EventHandler<PointerPressedEventArgs> pressed,
        EventHandler<PointerEventArgs> moved, EventHandler<PointerReleasedEventArgs> released)
    {
        if (control is null) return;
        control.PointerPressed += pressed;
        control.PointerMoved += moved;
        control.PointerReleased += released;
    }

    private static void Detach(Control? control, EventHandler<PointerPressedEventArgs> pressed,
        EventHandler<PointerEventArgs> moved, EventHandler<PointerReleasedEventArgs> released)
    {
        if (control is null) return;
        control.PointerPressed -= pressed;
        control.PointerMoved -= moved;
        control.PointerReleased -= released;
    }

    /// <summary>
    /// Puts the dock grip on the edge the panel shares with the map, and the floating grip in the
    /// corner, so the thing the user grabs is always where the panel can actually give way.
    /// </summary>
    internal void UpdateGrips()
    {
        if (_dockGrip is not null)
        {
            var side = Panel?.Side ?? PanelDockSide.Right;
            var across = side is PanelDockSide.Top or PanelDockSide.Bottom;
            _dockGrip.Width = across ? double.NaN : GripThickness;
            _dockGrip.Height = across ? GripThickness : double.NaN;
            _dockGrip.HorizontalAlignment = side switch
            {
                PanelDockSide.Left => Avalonia.Layout.HorizontalAlignment.Right,
                PanelDockSide.Right => Avalonia.Layout.HorizontalAlignment.Left,
                _ => Avalonia.Layout.HorizontalAlignment.Stretch
            };
            _dockGrip.VerticalAlignment = side switch
            {
                PanelDockSide.Top => Avalonia.Layout.VerticalAlignment.Bottom,
                PanelDockSide.Bottom => Avalonia.Layout.VerticalAlignment.Top,
                _ => Avalonia.Layout.VerticalAlignment.Stretch
            };
            _dockGrip.Cursor = new Cursor(across ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast);
        }
        if (_floatGrip is not null) _floatGrip.Cursor = new Cursor(StandardCursorType.BottomRightCorner);
    }

    // ---- Moving -------------------------------------------------------------------------------------

    private void TitlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (Workspace is not { } workspace || Panel is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragOrigin = e.GetPosition(workspace);
        _dragStartBounds = new Rect(Panel.FloatingX, Panel.FloatingY, Panel.FloatingWidth, Panel.FloatingHeight);
        _dragging = false;
        e.Pointer.Capture(sender as IInputElement);
    }

    private void TitleMoved(object? sender, PointerEventArgs e)
    {
        if (Workspace is not { } workspace || Panel is not { } panel) return;
        if (!ReferenceEquals(e.Pointer.Captured, sender)) return;
        var position = e.GetPosition(workspace);
        var delta = position - _dragOrigin;
        if (!_dragging)
        {
            if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;
            _dragging = true;
            // A docked or collapsed panel the user drags comes loose first, under the pointer, so what
            // happens next is the same single gesture whatever state the panel started in.
            if (!panel.IsFloating)
            {
                _dragStartBounds = workspace.TearOutToFloating(panel, position);
                _dragOrigin = position;
                delta = default;
            }
        }
        panel.SetFloatingBounds(_dragStartBounds.X + delta.X, _dragStartBounds.Y + delta.Y,
            _dragStartBounds.Width, _dragStartBounds.Height);
        workspace.ShowDockHint(position);
    }

    private void TitleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer.Captured, sender)) return;
        e.Pointer.Capture(null);
        if (Workspace is not { } workspace || Panel is not { } panel) return;
        var dragged = _dragging;
        _dragging = false;
        workspace.HideDockHint();
        if (dragged) workspace.DropAt(panel, e.GetPosition(workspace));
    }

    // ---- Resizing -----------------------------------------------------------------------------------

    private void GripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Workspace is not { } workspace || Panel is not { } panel) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragOrigin = e.GetPosition(workspace);
        _dragStartSize = panel.DockedSize;
        _dragStartBounds = new Rect(panel.FloatingX, panel.FloatingY, panel.FloatingWidth, panel.FloatingHeight);
        _resizing = true;
        e.Pointer.Capture(sender as IInputElement);
        e.Handled = true;
    }

    private void GripMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizing || Workspace is not { } workspace || Panel is not { } panel) return;
        if (!ReferenceEquals(e.Pointer.Captured, sender)) return;
        var delta = e.GetPosition(workspace) - _dragOrigin;
        if (panel.IsFloating)
        {
            // A floating panel stops shrinking where its content would start to clip, as a docked one does.
            panel.SetFloatingBounds(_dragStartBounds.X, _dragStartBounds.Y,
                Math.Max(MinWidth, _dragStartBounds.Width + delta.X), Math.Max(MinHeight, _dragStartBounds.Height + delta.Y));
            return;
        }
        // A dock grows towards the map, so which way the pointer must travel depends on the side it is on.
        var grown = panel.Side switch
        {
            PanelDockSide.Left => _dragStartSize + delta.X,
            PanelDockSide.Right => _dragStartSize - delta.X,
            PanelDockSide.Top => _dragStartSize + delta.Y,
            _ => _dragStartSize - delta.Y
        };
        workspace.ResizeDock(panel, grown);
    }

    private void GripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer.Captured, sender)) return;
        e.Pointer.Capture(null);
        _resizing = false;
    }
}
