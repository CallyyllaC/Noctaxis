using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.ViewModels;

namespace Noctaxis.Desktop.Controls.Docking;

/// <summary>
/// The Planner's workspace: one permanent central surface — the map — with panels docked around it,
/// floating over it, or collapsed to tabs on its edges. It is the only place that decides where a panel
/// is drawn, and it decides it from the state in <see cref="Layout"/>.
/// </summary>
/// <remarks>
/// <para>The first child that is not part of the framework is the central surface; every
/// <see cref="PlannerPanelHost"/> child is a panel, matched to the layout by its
/// <see cref="PlannerPanelHost.PanelId"/>. Panels keep their place among these children whatever state
/// they are in, so moving one re-arranges it and never reparents it: no panel's content is torn down
/// and rebuilt, and nothing the user typed into it is lost, by docking, floating or unpinning it.</para>
/// <para>The map never disappears. Docks that together ask for more than the workspace can give are
/// scaled down until the central surface still has <see cref="MinimumCentralWidth"/> by
/// <see cref="MinimumCentralHeight"/>, and floating panels are brought back inside the workspace, so a
/// layout saved on a large display opens safely on a small one.</para>
/// </remarks>
public sealed class DockWorkspace : Panel
{
    /// <summary>The map always keeps at least this much room, whatever the docks ask for.</summary>
    public const double MinimumCentralWidth = 240;
    public const double MinimumCentralHeight = 160;

    /// <summary>How close to an edge a dragged panel must be dropped to dock there.</summary>
    public const double EdgeDockDistance = 56;

    /// <summary>The share of the central surface an opened unpinned panel may cover.</summary>
    private const double RevealShare = .8;

    private static readonly PanelDockSide[] Sides =
        [PanelDockSide.Left, PanelDockSide.Right, PanelDockSide.Top, PanelDockSide.Bottom];

    private readonly Dictionary<PanelDockSide, PanelEdgeStrip> _strips = [];
    private readonly Border _dockHint;
    private Dictionary<Control, Rect> _slots = [];
    private Rect _central;
    private Rect? _hint;
    // The room the docks take on each side, from the last layout pass; the drop hint shows the shape a
    // dock would have beside the ones already there.
    private double _leading, _trailing, _above, _below;
    private PlannerDockViewModel? _bound;
    private PlannerDockViewModel? _fallback;

    public static readonly StyledProperty<PlannerDockViewModel?> LayoutProperty =
        AvaloniaProperty.Register<DockWorkspace, PlannerDockViewModel?>(nameof(Layout));

    public PlannerDockViewModel? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public DockWorkspace()
    {
        foreach (var side in Sides)
        {
            var strip = new PanelEdgeStrip { Side = side, ZIndex = 2, IsVisible = false };
            _strips[side] = strip;
            Children.Add(strip);
        }
        // Shown while a panel is dragged near an edge, so dropping it there is a decision and not a surprise.
        _dockHint = new Border { ZIndex = 30, IsVisible = false, IsHitTestVisible = false };
        _dockHint.Classes.Add("dockHint");
        Children.Add(_dockHint);
        // A press that is not on an opened panel puts that panel away again, which is what a user who
        // has finished reading it does next. Nothing is handled here: the press still reaches the map.
        AddHandler(PointerPressedEvent, WorkspacePressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>The map's rectangle, once the docks and edge tabs have taken their room.</summary>
    public Rect CentralBounds => _central;

    /// <summary>The central surface: the first child that is not part of the docking framework.</summary>
    public Control? Central => Children.FirstOrDefault(child =>
        child is not PlannerPanelHost && child is not PanelEdgeStrip && !ReferenceEquals(child, _dockHint));

    public IReadOnlyList<PlannerPanelHost> Hosts => Children.OfType<PlannerPanelHost>().ToArray();

    /// <summary>The tabs for the unpinned panels on one edge.</summary>
    public PanelEdgeStrip StripFor(PanelDockSide side) => _strips[side];

    /// <summary>
    /// The layout in force: the one bound to <see cref="Layout"/>, or the default set. The Planner
    /// always has its panels, so a workspace that has not been given a layout shows the arrangement a
    /// new installation starts with rather than an empty map with its panels missing.
    /// </summary>
    public PlannerDockViewModel EffectiveLayout => Layout ?? (_fallback ??= new PlannerDockViewModel());

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LayoutProperty) BindPanels();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BindPanels();
    }

    /// <summary>
    /// Gives each declared host the state of the panel it names. A host whose id the layout does not
    /// know stays switched off, rather than appearing as an unnamed panel nothing can move.
    /// </summary>
    private void BindPanels()
    {
        var layout = EffectiveLayout;
        foreach (var host in Hosts)
            if (!ReferenceEquals(host.Panel, layout[host.PanelId])) host.Panel = layout[host.PanelId];
        if (!ReferenceEquals(_bound, layout))
        {
            if (_bound is not null)
            {
                _bound.Changed -= LayoutChanged;
                foreach (var panel in _bound.Panels) panel.PropertyChanged -= PanelStateChanged;
            }
            _bound = layout;
            layout.Changed += LayoutChanged;
            foreach (var panel in layout.Panels) panel.PropertyChanged += PanelStateChanged;
        }
        Reorganise();
    }

    private void LayoutChanged(object? sender, EventArgs e) => Reorganise();

    private void PanelStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlannerPanelViewModel.Side) or nameof(PlannerPanelViewModel.Mode)
            or nameof(PlannerPanelViewModel.IsRevealed))
        {
            Reorganise();
            return;
        }
        if (e.PropertyName is nameof(PlannerPanelViewModel.DockedSize) or nameof(PlannerPanelViewModel.DockOrder)
            or nameof(PlannerPanelViewModel.FloatingX) or nameof(PlannerPanelViewModel.FloatingY)
            or nameof(PlannerPanelViewModel.FloatingWidth) or nameof(PlannerPanelViewModel.FloatingHeight))
            InvalidateMeasure();
    }

    /// <summary>Re-reads every panel's state: the edge tabs, the stacking order and the arrangement.</summary>
    private void Reorganise()
    {
        foreach (var side in Sides)
        {
            var panels = EffectiveLayout.Panels
                .Where(panel => panel.IsAutoHidden && panel.Side == side)
                .OrderBy(panel => panel.DockOrder)
                .ToArray();
            var strip = _strips[side];
            strip.ItemsSource = panels;
            strip.IsVisible = panels.Length > 0;
        }
        foreach (var host in Hosts)
        {
            host.UpdateGrips();
            // Docked panels sit under the map's own chrome; opened and floating ones sit over everything,
            // and a panel the user is holding is the one on top.
            host.ZIndex = host.Panel switch
            {
                { IsFloating: true } => 20,
                { IsDocked: true } => 1,
                _ => 10
            };
            host.Classes.Set("floating", host.Panel is { IsFloating: true });
        }
        InvalidateMeasure();
    }

    private void WorkspacePressed(object? sender, PointerPressedEventArgs e)
    {
        var layout = EffectiveLayout;
        if (!layout.Panels.Any(panel => panel.IsRevealed)) return;
        // A press inside the opened panel, or on the strip that opened it, is not a dismissal.
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors()
                .Any(visual => visual is PlannerPanelHost { Panel.IsRevealed: true } or PanelEdgeStrip)) return;
        layout.CloseRevealed();
    }

    // ---- Layout -------------------------------------------------------------------------------------

    protected override Size MeasureOverride(Size availableSize)
    {
        // An unbounded workspace is a measurement rather than a window: it falls back to the size the
        // Planner opens at, so the arrangement below is always computed against real numbers.
        var size = new Size(
            double.IsFinite(availableSize.Width) && availableSize.Width > 0 ? availableSize.Width : 1280,
            double.IsFinite(availableSize.Height) && availableSize.Height > 0 ? availableSize.Height : 800);
        _slots = Compute(size);
        foreach (var child in Children)
            if (child.IsVisible) child.Measure(_slots.TryGetValue(child, out var slot) ? slot.Size : default);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _slots = Compute(finalSize);
        foreach (var child in Children)
            if (child.IsVisible) child.Arrange(_slots.TryGetValue(child, out var slot) ? slot : default);
        return finalSize;
    }

    private Dictionary<Control, Rect> Compute(Size size)
    {
        var slots = new Dictionary<Control, Rect>();
        var hosts = Hosts;
        var docked = Sides.ToDictionary(side => side, side => DockedHosts(hosts, side));
        var extents = Sides.ToDictionary(side => side, side => DockExtent(docked[side], side));
        var minimums = Sides.ToDictionary(side => side, side => SideMinimum(docked[side], side));
        var strips = Sides.ToDictionary(side => side, side => StripExtent(side, size));

        // The map keeps its minimum: two docks that together ask for more than is left give up the room
        // they have above their own minimums, in proportion, so neither wins outright and neither is
        // squeezed narrower than its panels can use.
        Fit(extents, minimums, strips, PanelDockSide.Left, PanelDockSide.Right, size.Width, MinimumCentralWidth);
        Fit(extents, minimums, strips, PanelDockSide.Top, PanelDockSide.Bottom, size.Height, MinimumCentralHeight);

        var leading = _leading = strips[PanelDockSide.Left] + extents[PanelDockSide.Left];
        var trailing = _trailing = strips[PanelDockSide.Right] + extents[PanelDockSide.Right];
        var above = _above = strips[PanelDockSide.Top] + extents[PanelDockSide.Top];
        var below = _below = strips[PanelDockSide.Bottom] + extents[PanelDockSide.Bottom];
        var middleWidth = Math.Max(0, size.Width - leading - trailing);
        _central = new Rect(leading, above, middleWidth, Math.Max(0, size.Height - above - below));

        // How far each dock runs is the corners it owns: a dock reaches the workspace edge at a corner
        // it owns, and stops at its neighbour's edge at one it does not. Every corner belongs to exactly
        // one of the two docks that touch it, so the four rectangles can never overlap, and none of this
        // touches the map's rectangle above — ownership moves the docks around the map, not into it.
        var corners = EffectiveLayout.Corners;
        double Top(DockCorner corner, PanelDockSide side) => corners.Owns(side, corner) ? 0 : above;
        double Bottom(DockCorner corner, PanelDockSide side) => corners.Owns(side, corner) ? size.Height : size.Height - below;
        double Start(DockCorner corner, PanelDockSide side) => corners.Owns(side, corner) ? 0 : leading;
        double End(DockCorner corner, PanelDockSide side) => corners.Owns(side, corner) ? size.Width : size.Width - trailing;

        // The edge tabs belong to their dock and run exactly as far as it does, so a side reads as one
        // column or one row however its corners are owned.
        Rect Column(PanelDockSide side, double x, double width)
        {
            var (upper, lower) = side == PanelDockSide.Left
                ? (Top(DockCorner.TopLeft, side), Bottom(DockCorner.BottomLeft, side))
                : (Top(DockCorner.TopRight, side), Bottom(DockCorner.BottomRight, side));
            return new Rect(x, upper, width, Math.Max(0, lower - upper));
        }
        Rect Row(PanelDockSide side, double y, double height)
        {
            var (start, end) = side == PanelDockSide.Top
                ? (Start(DockCorner.TopLeft, side), End(DockCorner.TopRight, side))
                : (Start(DockCorner.BottomLeft, side), End(DockCorner.BottomRight, side));
            return new Rect(start, y, Math.Max(0, end - start), height);
        }

        slots[_strips[PanelDockSide.Left]] = Column(PanelDockSide.Left, 0, strips[PanelDockSide.Left]);
        slots[_strips[PanelDockSide.Right]] = Column(PanelDockSide.Right, size.Width - strips[PanelDockSide.Right], strips[PanelDockSide.Right]);
        slots[_strips[PanelDockSide.Top]] = Row(PanelDockSide.Top, 0, strips[PanelDockSide.Top]);
        slots[_strips[PanelDockSide.Bottom]] = Row(PanelDockSide.Bottom, size.Height - strips[PanelDockSide.Bottom], strips[PanelDockSide.Bottom]);

        var docks = new Dictionary<PanelDockSide, Rect>
        {
            [PanelDockSide.Left] = Column(PanelDockSide.Left, strips[PanelDockSide.Left], extents[PanelDockSide.Left]),
            [PanelDockSide.Right] = Column(PanelDockSide.Right, size.Width - trailing, extents[PanelDockSide.Right]),
            [PanelDockSide.Top] = Row(PanelDockSide.Top, strips[PanelDockSide.Top], extents[PanelDockSide.Top]),
            [PanelDockSide.Bottom] = Row(PanelDockSide.Bottom, size.Height - below, extents[PanelDockSide.Bottom])
        };

        if (Central is { } central) slots[central] = _central;

        foreach (var side in Sides)
        {
            var stack = docked[side];
            if (stack.Count == 0) continue;
            // Panels docked on one side share its length and each scrolls its own content. The share is
            // equal unless a panel needs more along the dock than that, in which case it has its minimum
            // and the others share the rest. Splitters between them are a refinement for later.
            var dock = docks[side];
            var across = side is PanelDockSide.Top or PanelDockSide.Bottom;
            var lengths = ShareLength(stack.Select(host => LengthMinimum(host, side)).ToArray(), across ? dock.Width : dock.Height);
            var offset = 0d;
            for (var index = 0; index < stack.Count; index++)
            {
                slots[stack[index]] = across
                    ? new Rect(dock.X + offset, dock.Y, lengths[index], dock.Height)
                    : new Rect(dock.X, dock.Y + offset, dock.Width, lengths[index]);
                offset += lengths[index];
            }
        }

        foreach (var host in hosts)
        {
            if (host.Panel is not { } panel || !panel.IsPresented || slots.ContainsKey(host)) continue;
            if (panel.IsFloating)
            {
                panel.ClampTo(size.Width, size.Height);
                // Never narrower than the panel can use, as long as the workspace has the room.
                var width = Math.Min(Math.Max(panel.FloatingWidth, host.MinWidth), size.Width);
                var height = Math.Min(Math.Max(panel.FloatingHeight, host.MinHeight), size.Height);
                slots[host] = new Rect(Math.Clamp(panel.FloatingX, 0, Math.Max(0, size.Width - width)),
                    Math.Clamp(panel.FloatingY, 0, Math.Max(0, size.Height - height)), width, height);
                continue;
            }
            // Opened from an edge tab: over the map, at the size it would have if it were docked, so it
            // reads as the same panel and the map behind it does not move.
            slots[host] = RevealBounds(host, panel);
        }
        if (_hint is { } hint) slots[_dockHint] = hint;
        return slots;
    }

    private Rect RevealBounds(PlannerPanelHost host, PlannerPanelViewModel panel)
    {
        var across = panel.Side is PanelDockSide.Top or PanelDockSide.Bottom;
        var room = across ? _central.Height : _central.Width;
        // At the size it would have docked, within the map's own room, and never narrower than it can use.
        var extent = Math.Clamp(Math.Max(CrossMinimum(host, panel.Side), Math.Min(panel.DockedSize, room * RevealShare)), 0, Math.Max(0, room));
        return panel.Side switch
        {
            PanelDockSide.Left => new Rect(_central.X, _central.Y, extent, _central.Height),
            PanelDockSide.Right => new Rect(_central.Right - extent, _central.Y, extent, _central.Height),
            PanelDockSide.Top => new Rect(_central.X, _central.Y, _central.Width, extent),
            _ => new Rect(_central.X, _central.Bottom - extent, _central.Width, extent)
        };
    }

    private static List<PlannerPanelHost> DockedHosts(IReadOnlyList<PlannerPanelHost> hosts, PanelDockSide side) => hosts
        .Where(host => host.Panel is { IsDocked: true } panel && panel.Side == side)
        .OrderBy(host => host.Panel!.DockOrder)
        .ToList();

    /// <summary>
    /// A dock has one extent across it, and the panel in it that wants the most decides — but never less
    /// than the most any panel in it needs to show its content without clipping.
    /// </summary>
    private static double DockExtent(List<PlannerPanelHost> hosts, PanelDockSide side) => hosts.Count == 0
        ? 0
        : Math.Clamp(hosts.Max(host => host.Panel!.DockedSize),
            SideMinimum(hosts, side), Math.Max(SideMinimum(hosts, side), PlannerPanelLayout.MaximumDockedSize));

    /// <summary>
    /// The least a dock may be across: the framework's floor, or more where a panel in it says it needs
    /// more. A panel says so with its own <see cref="Layoutable.MinWidth"/> and
    /// <see cref="Layoutable.MinHeight"/>, the ordinary Avalonia properties, set where it is declared.
    /// </summary>
    private static double SideMinimum(List<PlannerPanelHost> hosts, PanelDockSide side) => hosts.Count == 0
        ? 0
        : Math.Max(PlannerPanelLayout.MinimumDockedSize, hosts.Max(host => CrossMinimum(host, side)));

    /// <summary>A panel's own minimum across its dock: its width beside the map, its height above or below it.</summary>
    private static double CrossMinimum(PlannerPanelHost host, PanelDockSide side) =>
        side is PanelDockSide.Top or PanelDockSide.Bottom ? host.MinHeight : host.MinWidth;

    /// <summary>A panel's own minimum along its dock, where it shares the dock's length with others.</summary>
    private static double LengthMinimum(PlannerPanelHost host, PanelDockSide side) =>
        side is PanelDockSide.Top or PanelDockSide.Bottom ? host.MinWidth : host.MinHeight;

    /// <summary>
    /// Shares a dock's length between the panels in it: equally, except that a panel needing more than an
    /// equal share has its minimum and the others divide what is left. If the minimums together are more
    /// than the dock has, each is scaled down in proportion — the one case where a panel can be given
    /// less than it asked for, because there is simply not the room.
    /// </summary>
    internal static double[] ShareLength(IReadOnlyList<double> minimums, double length)
    {
        var shares = new double[minimums.Count];
        if (minimums.Count == 0) return shares;
        var floor = minimums.Sum();
        if (floor >= length)
        {
            for (var index = 0; index < shares.Length; index++)
                shares[index] = floor > 0 ? length * minimums[index] / floor : length / shares.Length;
            return shares;
        }
        var fixedAt = new bool[shares.Length];
        var remaining = length;
        var open = shares.Length;
        // Settle the panels whose minimum is more than an equal share of what is left, until none are.
        for (var changed = true; changed && open > 0;)
        {
            changed = false;
            var equal = remaining / open;
            for (var index = 0; index < shares.Length; index++)
            {
                if (fixedAt[index] || minimums[index] <= equal) continue;
                shares[index] = minimums[index];
                fixedAt[index] = true;
                remaining -= minimums[index];
                open--;
                changed = true;
            }
        }
        for (var index = 0; index < shares.Length; index++)
            if (!fixedAt[index]) shares[index] = remaining / open;
        return shares;
    }

    /// <summary>
    /// Brings two facing docks back within the room the map leaves them. Each gives up the room it has
    /// above its own minimum, in proportion, until the map has its minimum. Neither goes below its
    /// minimum: in a window too small for both docks and the map's minimum at once, the map has what is
    /// left, because a panel squeezed past its minimum clips its content and helps nobody.
    /// </summary>
    private static void Fit(Dictionary<PanelDockSide, double> extents, Dictionary<PanelDockSide, double> minimums,
        Dictionary<PanelDockSide, double> strips, PanelDockSide first, PanelDockSide second, double total, double minimumCentral)
    {
        var room = total - minimumCentral - strips[first] - strips[second];
        var needed = extents[first] + extents[second] - room;
        if (needed <= 0) return;
        var spareFirst = Math.Max(0, extents[first] - minimums[first]);
        var spareSecond = Math.Max(0, extents[second] - minimums[second]);
        var spare = spareFirst + spareSecond;
        if (spare <= 0) return;
        var given = Math.Min(1, needed / spare);
        extents[first] -= spareFirst * given;
        extents[second] -= spareSecond * given;
    }

    private double StripExtent(PanelDockSide side, Size size)
    {
        var strip = _strips[side];
        if (!strip.IsVisible || strip.ItemCount == 0) return 0;
        var across = side is PanelDockSide.Top or PanelDockSide.Bottom;
        strip.Measure(across ? size : new Size(PanelEdgeStrip.MaximumSideExtent, size.Height));
        return across
            ? Math.Min(strip.DesiredSize.Height, size.Height / 4)
            : Math.Min(Math.Max(strip.DesiredSize.Width, 1), PanelEdgeStrip.MaximumSideExtent);
    }

    // ---- Moving, docking and resizing ---------------------------------------------------------------

    /// <summary>
    /// Brings a docked or collapsed panel loose under the pointer, at the size it already had, and
    /// returns the rectangle the drag continues from.
    /// </summary>
    internal Rect TearOutToFloating(PlannerPanelViewModel panel, Point pointer)
    {
        var host = HostFor(panel);
        var current = host is not null && _slots.TryGetValue(host, out var slot) && slot.Width > 0
            ? slot.Size
            : new Size(panel.FloatingWidth, panel.FloatingHeight);
        var width = Math.Min(current.Width, Math.Max(PlannerPanelLayout.MinimumFloatingWidth, Bounds.Width));
        var height = Math.Min(current.Height, Math.Max(PlannerPanelLayout.MinimumFloatingHeight, Bounds.Height));
        // Held where it was grabbed, so the title bar stays under the pointer for the rest of the drag.
        panel.SetFloatingBounds(pointer.X - Math.Min(width / 2, 120), pointer.Y - 14, width, height);
        panel.Float();
        return new Rect(panel.FloatingX, panel.FloatingY, panel.FloatingWidth, panel.FloatingHeight);
    }

    /// <summary>
    /// Docks the panel if it was dropped against an edge, and otherwise leaves it floating. Dropped
    /// into a corner, the dock it lands in takes that corner and runs into it, which is how corner
    /// ownership is chosen: by putting a panel there, not by reading a setting about it.
    /// </summary>
    internal void DropAt(PlannerPanelViewModel panel, Point pointer)
    {
        if (TargetAt(pointer) is not { } target) return;
        if (target.Corner is { } corner) EffectiveLayout.SetCornerOwner(corner, target.Side);
        panel.DockTo(target.Side);
    }

    internal void ShowDockHint(Point pointer)
    {
        if (TargetAt(pointer) is not { } target)
        {
            HideDockHint();
            return;
        }
        // The hint is the shape the dock would actually take, corners included, so dropping into a
        // corner shows the dock reaching into it before the user commits to it.
        var corners = EffectiveLayout.Corners;
        if (target.Corner is { } corner) corners = corners.With(corner, target.Side);
        _hint = HintBounds(target.Side, PlannerPanelLayout.DefaultDockedSize, corners);
        _dockHint.IsVisible = true;
        InvalidateMeasure();
    }

    private Rect HintBounds(PanelDockSide side, double extent, DockCornerOwnership corners)
    {
        double width = Bounds.Width, height = Bounds.Height;
        bool Owns(DockCorner corner) => corners.Owns(side, corner);
        static Rect Between(double start, double end, double across, double extent, bool vertical) => vertical
            ? new Rect(across, start, extent, Math.Max(0, end - start))
            : new Rect(start, across, Math.Max(0, end - start), extent);
        return side switch
        {
            PanelDockSide.Left => Between(Owns(DockCorner.TopLeft) ? 0 : _above,
                Owns(DockCorner.BottomLeft) ? height : height - _below, 0, extent, vertical: true),
            PanelDockSide.Right => Between(Owns(DockCorner.TopRight) ? 0 : _above,
                Owns(DockCorner.BottomRight) ? height : height - _below, Math.Max(0, width - extent), extent, vertical: true),
            PanelDockSide.Top => Between(Owns(DockCorner.TopLeft) ? 0 : _leading,
                Owns(DockCorner.TopRight) ? width : width - _trailing, 0, extent, vertical: false),
            _ => Between(Owns(DockCorner.BottomLeft) ? 0 : _leading,
                Owns(DockCorner.BottomRight) ? width : width - _trailing, Math.Max(0, height - extent), extent, vertical: false)
        };
    }

    internal void HideDockHint()
    {
        if (_hint is null) return;
        _hint = null;
        _dockHint.IsVisible = false;
        InvalidateMeasure();
    }

    /// <summary>
    /// The dock a drop at this point belongs to, and the corner it claims when the drop is in one.
    /// A point near two edges at once is a corner: the dock it is dropped into reaches into it.
    /// </summary>
    internal (PanelDockSide Side, DockCorner? Corner)? TargetAt(Point pointer)
    {
        if (EdgeAt(pointer) is not { } side) return null;
        var horizontal = pointer.X <= EdgeDockDistance ? PanelDockSide.Left
            : pointer.X >= Bounds.Width - EdgeDockDistance ? PanelDockSide.Right : (PanelDockSide?)null;
        var vertical = pointer.Y <= EdgeDockDistance ? PanelDockSide.Top
            : pointer.Y >= Bounds.Height - EdgeDockDistance ? PanelDockSide.Bottom : (PanelDockSide?)null;
        if (horizontal is null || vertical is null) return (side, null);
        return (side, (horizontal, vertical) switch
        {
            (PanelDockSide.Left, PanelDockSide.Top) => DockCorner.TopLeft,
            (PanelDockSide.Right, PanelDockSide.Top) => DockCorner.TopRight,
            (PanelDockSide.Left, _) => DockCorner.BottomLeft,
            _ => DockCorner.BottomRight
        });
    }

    /// <summary>The edge a drop at this point belongs to, or null for the middle of the workspace.</summary>
    public PanelDockSide? EdgeAt(Point pointer)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return null;
        var nearest = new (PanelDockSide Side, double Distance)[]
        {
            (PanelDockSide.Left, pointer.X),
            (PanelDockSide.Right, Bounds.Width - pointer.X),
            (PanelDockSide.Top, pointer.Y),
            (PanelDockSide.Bottom, Bounds.Height - pointer.Y)
        }.MinBy(candidate => Math.Abs(candidate.Distance));
        return Math.Abs(nearest.Distance) <= EdgeDockDistance ? nearest.Side : null;
    }

    /// <summary>
    /// Resizes the dock this panel belongs to. Every panel docked on that side takes the new extent,
    /// because a dock has one extent; the map's minimum is the limit it stops at.
    /// </summary>
    internal void ResizeDock(PlannerPanelViewModel panel, double extent)
    {
        var layout = EffectiveLayout;
        var across = panel.Side is PanelDockSide.Top or PanelDockSide.Bottom;
        var opposite = panel.Side switch
        {
            PanelDockSide.Left => PanelDockSide.Right,
            PanelDockSide.Right => PanelDockSide.Left,
            PanelDockSide.Top => PanelDockSide.Bottom,
            _ => PanelDockSide.Top
        };
        var taken = DockExtent(DockedHosts(Hosts, opposite), opposite) + StripExtent(opposite, Bounds.Size) + StripExtent(panel.Side, Bounds.Size);
        var room = (across ? Bounds.Height - MinimumCentralHeight : Bounds.Width - MinimumCentralWidth) - taken;
        // The grip stops where the narrowest panel in the dock would start to clip, and where the map
        // would lose its minimum; of the two, the panel's limit holds.
        var minimum = SideMinimum(DockedHosts(Hosts, panel.Side), panel.Side);
        var clamped = Math.Clamp(extent, minimum, Math.Max(minimum, room));
        foreach (var other in layout.Panels)
            if (other.IsDocked && other.Side == panel.Side) other.SetDockedSize(clamped);
    }

    private PlannerPanelHost? HostFor(PlannerPanelViewModel panel) =>
        Hosts.FirstOrDefault(host => ReferenceEquals(host.Panel, panel));
}
