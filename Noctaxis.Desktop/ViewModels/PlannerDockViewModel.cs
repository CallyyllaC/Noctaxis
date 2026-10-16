using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Localisation;

namespace Noctaxis.Desktop.ViewModels;

/// <summary>One view of a panel that has several, named by a stable id and a short label.</summary>
public sealed record PlannerPanelTab(string Id, string TitleKey)
{
    public string Title => Strings.Get(TitleKey);
}

/// <summary>A panel the Planner declares: where it starts, what it is called, and the views it holds.</summary>
public sealed record PlannerPanelDefinition(PlannerPanelLayout Layout, string TitleKey, IReadOnlyList<PlannerPanelTab>? Tabs = null)
{
    public IReadOnlyList<PlannerPanelTab> EffectiveTabs => Tabs ?? [];
}

/// <summary>
/// One Planner panel's state, and every way the user has of changing it. The panel's content knows
/// none of this: whether it is docked, floating or unpinned belongs here and to the dock host that
/// reads it, never to the view inside.
/// </summary>
/// <remarks>
/// <para>Every state change is a command, so each one has a visible control and a keyboard route, and
/// none of them needs a pointer gesture. Dragging a panel does what the commands do and nothing more.</para>
/// <para>A panel is pinned, unpinned or floating, and nothing else. The Planner's panels are its
/// permanent tools rather than documents to close: unpinning is how one is put out of the way, and an
/// unpinned panel is still named on its edge and one tap from open.</para>
/// </remarks>
public sealed partial class PlannerPanelViewModel : ObservableObject
{
    private readonly PlannerDockViewModel _dock;

    internal PlannerPanelViewModel(PlannerDockViewModel dock, PlannerPanelLayout layout, PlannerPanelDefinition definition)
    {
        _dock = dock;
        PanelId = layout.PanelId;
        TitleKey = definition.TitleKey;
        Tabs = definition.EffectiveTabs;
        _mode = layout.Mode;
        _side = layout.Side;
        _dockedSize = layout.DockedSize;
        _floatingX = layout.FloatingX;
        _floatingY = layout.FloatingY;
        _floatingWidth = layout.FloatingWidth;
        _floatingHeight = layout.FloatingHeight;
        _dockOrder = layout.DockOrder;
        _selectedTab = ValidTab(layout.SelectedTab);
    }

    /// <summary>Stable and never translated: it names the panel in the saved layout.</summary>
    public string PanelId { get; }

    /// <summary>The resource key of the panel's name.</summary>
    public string TitleKey { get; }

    public string Title => Strings.Get(TitleKey);

    [ObservableProperty] private PanelDockMode _mode;
    [ObservableProperty] private PanelDockSide _side;
    [ObservableProperty] private double _dockedSize;
    [ObservableProperty] private int _dockOrder;

    // Written by hand rather than generated, because one caller sets them without notifying: the
    // workspace clamps a saved rectangle while it is measuring itself, and that is not an edit.
    private double _floatingX, _floatingY, _floatingWidth, _floatingHeight;

    public double FloatingX { get => _floatingX; private set => SetProperty(ref _floatingX, value); }
    public double FloatingY { get => _floatingY; private set => SetProperty(ref _floatingY, value); }
    public double FloatingWidth { get => _floatingWidth; private set => SetProperty(ref _floatingWidth, value); }
    public double FloatingHeight { get => _floatingHeight; private set => SetProperty(ref _floatingHeight, value); }

    /// <summary>
    /// An unpinned panel the user has opened from its edge tab. It is shown over the map, which keeps
    /// its size, so revealing a panel never reflows the workspace. This is a moment in a session
    /// rather than a preference, and it is not saved.
    /// </summary>
    [ObservableProperty] private bool _isRevealed;

    /// <summary>Pinned means docked: the panel holds its own space beside the map until the user says otherwise.</summary>
    public bool IsPinned => Mode == PanelDockMode.Docked;
    public bool IsDocked => Mode == PanelDockMode.Docked;
    public bool IsFloating => Mode == PanelDockMode.Floating;
    public bool IsAutoHidden => Mode == PanelDockMode.AutoHidden;
    /// <summary>Laid out somewhere: docked, floating, or unpinned and currently opened from its tab.</summary>
    public bool IsPresented => Mode != PanelDockMode.AutoHidden || IsRevealed;

    // ---- Views ---------------------------------------------------------------------------------------

    /// <summary>The views this panel holds, in order; empty for a panel with one view.</summary>
    public IReadOnlyList<PlannerPanelTab> Tabs { get; }

    private string? _selectedTab;

    /// <summary>The id of the view the panel shows, saved with the layout so it opens where it was left.</summary>
    public string? SelectedTab => _selectedTab;

    /// <summary>
    /// The shown view by position, for a tab strip to bind to. Switching views is a preference like
    /// moving a panel, and it is saved the same way. A position the panel does not have — a tab strip
    /// briefly reporting no selection while it builds itself — is ignored rather than recorded.
    /// </summary>
    public int SelectedTabIndex
    {
        get => Tabs.Count == 0 ? -1 : Math.Max(0, IndexOf(_selectedTab));
        set
        {
            if (value < 0 || value >= Tabs.Count || Tabs[value].Id == _selectedTab) return;
            SelectTab(Tabs[value].Id);
            Changed();
        }
    }

    private void SelectTab(string? id)
    {
        var next = ValidTab(id);
        if (next == _selectedTab) return;
        _selectedTab = next;
        OnPropertyChanged(nameof(SelectedTab));
        OnPropertyChanged(nameof(SelectedTabIndex));
    }

    private int IndexOf(string? id)
    {
        for (var index = 0; index < Tabs.Count; index++)
            if (string.Equals(Tabs[index].Id, id, StringComparison.Ordinal)) return index;
        return -1;
    }

    /// <summary>A view this panel has, or its first: an id from another version is not a reason to show nothing.</summary>
    private string? ValidTab(string? id) => Tabs.Count == 0 ? null : IndexOf(id) >= 0 ? id : Tabs[0].Id;

    // ---- Names for assistive technology --------------------------------------------------------------

    public string PinAccessibleName => Strings.Format("Planner_Panel_Pin_AccessibleName_Format", Title);
    public string UnpinAccessibleName => Strings.Format("Planner_Panel_Unpin_AccessibleName_Format", Title);
    public string MenuAccessibleName => Strings.Format("Planner_Panel_Menu_AccessibleName_Format", Title);
    public string MoveAccessibleName => Strings.Format("Planner_Panel_Move_AccessibleName_Format", Title);
    public string ResizeAccessibleName => Strings.Format("Planner_Panel_Resize_AccessibleName_Format", Title);
    public string RevealAccessibleName => Strings.Format("Planner_Panel_Reveal_AccessibleName_Format", Title);
    // ---- Corners -------------------------------------------------------------------------------------

    /// <summary>True when the dock this panel sits in runs the whole way, past the docks beside it.</summary>
    public bool DockSpans => _dock.Corners.Spans(Side);

    /// <summary>
    /// What the panel menu offers next, said as the result rather than as a setting: a dock either runs
    /// the whole way or stops at the docks beside it, and the label names whichever it is not doing.
    /// </summary>
    public string SpanActionLabel => Strings.Get((Side is PanelDockSide.Top or PanelDockSide.Bottom, DockSpans) switch
    {
        (true, false) => "Planner_Panel_RunFullWidth_MenuItem",
        (true, true) => "Planner_Panel_StopAtSides_MenuItem",
        (false, false) => "Planner_Panel_RunFullHeight_MenuItem",
        _ => "Planner_Panel_StopAtEnds_MenuItem"
    });

    /// <summary>
    /// Runs this panel's dock the whole way, or stops it at the docks beside it. It is the dock that
    /// moves, not the panel: every panel docked on that side runs as far as the dock does.
    /// </summary>
    [RelayCommand]
    public void ToggleDockSpan() => _dock.SetSpan(Side, !DockSpans);

    internal void NotifySpan()
    {
        OnPropertyChanged(nameof(DockSpans));
        OnPropertyChanged(nameof(SpanActionLabel));
    }

    // ---- Placement -----------------------------------------------------------------------------------

    /// <summary>Docks the panel to the side it remembers, which is what pinning an unpinned panel means.</summary>
    [RelayCommand]
    public void Pin() => Apply(PanelDockMode.Docked, Side);

    /// <summary>Collapses the panel to a tab on its dock's edge. It is still there, and still one tap away.</summary>
    [RelayCommand]
    public void Unpin() => Apply(PanelDockMode.AutoHidden, Side);

    [RelayCommand]
    public void Float() => Apply(PanelDockMode.Floating, Side);

    [RelayCommand]
    public void DockTo(PanelDockSide side) => Apply(PanelDockMode.Docked, side);

    /// <summary>Opens an unpinned panel over the map, or closes it again.</summary>
    [RelayCommand]
    public void ToggleReveal()
    {
        if (Mode != PanelDockMode.AutoHidden) return;
        if (IsRevealed) IsRevealed = false;
        else _dock.Reveal(this);
    }

    private void Apply(PanelDockMode mode, PanelDockSide side)
    {
        var changed = Mode != mode || Side != side;
        Mode = mode;
        Side = side;
        if (mode != PanelDockMode.AutoHidden) IsRevealed = false;
        if (changed) Changed();
    }

    /// <summary>Records a new dock extent, in the one place that clamps it.</summary>
    internal void SetDockedSize(double size)
    {
        var clamped = Math.Clamp(double.IsFinite(size) ? size : PlannerPanelLayout.DefaultDockedSize,
            PlannerPanelLayout.MinimumDockedSize, PlannerPanelLayout.MaximumDockedSize);
        if (Math.Abs(clamped - DockedSize) < .5) return;
        DockedSize = clamped;
        Changed();
    }

    internal void SetFloatingBounds(double x, double y, double width, double height)
    {
        var next = (Layout() with { FloatingX = x, FloatingY = y, FloatingWidth = width, FloatingHeight = height }).Normalised();
        if (Math.Abs(next.FloatingX - FloatingX) < .5 && Math.Abs(next.FloatingY - FloatingY) < .5 &&
            Math.Abs(next.FloatingWidth - FloatingWidth) < .5 && Math.Abs(next.FloatingHeight - FloatingHeight) < .5) return;
        FloatingX = next.FloatingX;
        FloatingY = next.FloatingY;
        FloatingWidth = next.FloatingWidth;
        FloatingHeight = next.FloatingHeight;
        Changed();
    }

    /// <summary>
    /// Brings a saved rectangle back inside a workspace of this size without reporting a change:
    /// opening Noctaxis on a smaller display must not rewrite the layout the user arranged.
    /// </summary>
    internal void ClampTo(double workspaceWidth, double workspaceHeight)
    {
        var clamped = Layout().ClampedTo(workspaceWidth, workspaceHeight);
        // The fields directly, and deliberately: this runs while the workspace is measuring itself, so
        // it must not notify, invalidate that measure again, or mark the saved layout as edited.
        _floatingX = clamped.FloatingX;
        _floatingY = clamped.FloatingY;
        _floatingWidth = clamped.FloatingWidth;
        _floatingHeight = clamped.FloatingHeight;
    }

    /// <summary>Takes every value back from a layout, without reporting each one: the caller reports once.</summary>
    internal void Restore(PlannerPanelLayout layout)
    {
        IsRevealed = false;
        Mode = layout.Mode;
        Side = layout.Side;
        DockedSize = layout.DockedSize;
        DockOrder = layout.DockOrder;
        SetFloatingBounds(layout.FloatingX, layout.FloatingY, layout.FloatingWidth, layout.FloatingHeight);
        SelectTab(layout.SelectedTab);
    }

    public PlannerPanelLayout Layout() => new(PanelId, Mode, Side, DockedSize,
        FloatingX, FloatingY, FloatingWidth, FloatingHeight, DockOrder, SelectedTab);

    private void Changed() => _dock.PanelChanged();

    partial void OnModeChanged(PanelDockMode value) => NotifyState();
    partial void OnSideChanged(PanelDockSide value) => NotifyState();
    partial void OnIsRevealedChanged(bool value) => NotifyState();

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(IsDocked));
        OnPropertyChanged(nameof(IsFloating));
        OnPropertyChanged(nameof(IsAutoHidden));
        OnPropertyChanged(nameof(IsPresented));
        NotifySpan();
    }
}

/// <summary>
/// The Planner's panel layout: which panels exist, where each one is, and the reset that puts them all
/// back. The panel set is declared here rather than in the saved file, so the application always
/// decides what panels there are and the file only says where the user put them.
/// </summary>
public sealed partial class PlannerDockViewModel : ObservableObject
{
    /// <summary>The Inspector's views, in the order its tab strip shows them.</summary>
    public static IReadOnlyList<PlannerPanelTab> InspectorTabs { get; } =
    [
        new("targets", "Planner_Inspector_Targets_Tab"),
        new("weather", "Planner_Inspector_Weather_Tab"),
        new("equipment", "Planner_Inspector_Equipment_Tab")
    ];

    /// <summary>
    /// The panels the Planner has, and the layout a new installation starts with: one Inspector holding
    /// the planning detail beside the map, and the timeline under it, running the full width beneath the
    /// map and the Inspector (<see cref="DockCornerOwnership.Default"/>). The sizes are the arrangement tuned
    /// on a 1920 × 1080 display; a smaller window keeps them within its own limits. Everything that reads off the map
    /// itself — the pin's place, the Picture-in-Picture map, the Terrain frame — is laid over the map
    /// rather than docked, so a saved layout that still names a planning-location or terrain panel simply
    /// drops it.
    /// </summary>
    public static IReadOnlyList<PlannerPanelDefinition> DefaultPanels { get; } =
    [
        new(new("inspector", Mode: PanelDockMode.Docked, Side: PanelDockSide.Right, DockedSize: 342, DockOrder: 0,
            SelectedTab: InspectorTabs[0].Id), "Planner_Inspector_Title", InspectorTabs),
        new(new("timeline", Mode: PanelDockMode.Docked, Side: PanelDockSide.Bottom, DockedSize: 275), "Planner_Panel_Timeline_Title")
    ];

    public static IReadOnlyList<PlannerPanelLayout> DefaultLayout { get; } =
        DefaultPanels.Select(panel => panel.Layout).ToArray();

    /// <summary>
    /// The one definition of the layout Noctaxis ships with: the panels, the Inspector's view, and the
    /// corners. Both the first run and every reset come from here, so there is nowhere else to keep in step.
    /// </summary>
    public static PlannerDockLayout ShippedLayout { get; } = new(DefaultLayout, DockCornerOwnership.Default);

    private bool _restoring;

    public PlannerDockViewModel(PlannerDockLayout? saved = null)
    {
        var definitions = DefaultPanels.ToDictionary(panel => panel.Layout.PanelId, StringComparer.Ordinal);
        var layout = saved ?? PlannerDockLayout.Empty;
        Panels = new(layout.MergedOver(DefaultLayout)
            .Select(panel => new PlannerPanelViewModel(this, panel, definitions[panel.PanelId])));
        _corners = layout.EffectiveCorners;
    }

    private DockCornerOwnership _corners;

    /// <summary>
    /// Which dock reaches into each corner. Changing it is a layout change like any other: it is saved,
    /// and the workspace re-arranges itself around it.
    /// </summary>
    public DockCornerOwnership Corners
    {
        get => _corners;
        private set
        {
            var next = value.Normalised();
            if (_corners == next) return;
            _corners = next;
            OnPropertyChanged();
            foreach (var panel in Panels) panel.NotifySpan();
            PanelChanged();
        }
    }

    /// <summary>Hands one corner to one of the two docks that touch it.</summary>
    public void SetCornerOwner(DockCorner corner, PanelDockSide owner) => Corners = Corners.With(corner, owner);

    /// <summary>Makes a dock run the whole way, or stop at the docks beside it.</summary>
    public void SetSpan(PanelDockSide side, bool spans) => Corners = Corners.WithSpan(side, spans);

    public ObservableCollection<PlannerPanelViewModel> Panels { get; }

    /// <summary>The panel holding the Planner's planning detail, for its tab strip to bind to.</summary>
    public PlannerPanelViewModel Inspector => this["inspector"]!;

    /// <summary>Raised whenever a panel's saved placement changes, so the layout can be persisted.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised by <see cref="ResetLayout"/>, after the panels, for the rest of the shipped arrangement the
    /// Planner keeps outside the docks: the map's instrument sizes and the Picture-in-Picture map's zoom.
    /// </summary>
    public event EventHandler? LayoutReset;

    public PlannerPanelViewModel? this[string panelId] =>
        Panels.FirstOrDefault(panel => string.Equals(panel.PanelId, panelId, StringComparison.Ordinal));

    /// <summary>The layout as it should be written to the user's state file.</summary>
    public PlannerDockLayout Capture() => new(Panels.Select(panel => panel.Layout()).ToArray(), Corners);

    /// <summary>
    /// Opens one unpinned panel over the map. Only one is open at a time on a side, because two
    /// overlays on one edge would cover each other.
    /// </summary>
    public void Reveal(PlannerPanelViewModel panel)
    {
        foreach (var other in Panels)
            if (!ReferenceEquals(other, panel) && other.Side == panel.Side) other.IsRevealed = false;
        panel.IsRevealed = true;
    }

    /// <summary>Puts away whichever unpinned panel is open, for a press on the map.</summary>
    public void CloseRevealed()
    {
        foreach (var panel in Panels) panel.IsRevealed = false;
    }

    /// <summary>
    /// Puts the whole Planner layout back to what Noctaxis ships with: every panel's dock, pinning,
    /// size, floating rectangle and order, the Inspector's view, and which dock owns each corner; and,
    /// through <see cref="LayoutReset"/>, the map's instrument sizes and the Picture-in-Picture zoom. This
    /// is the one implementation, behind Settings → Planner map → Reset Planner layout, and it takes
    /// everything it restores from <see cref="ShippedLayout"/> rather than repeating any of it.
    /// </summary>
    [RelayCommand]
    public void ResetLayout()
    {
        _restoring = true;
        try
        {
            _corners = ShippedLayout.EffectiveCorners;
            foreach (var layout in ShippedLayout.EffectivePanels)
                this[layout.PanelId]?.Restore(layout);
        }
        finally { _restoring = false; }
        OnPropertyChanged(nameof(Corners));
        foreach (var panel in Panels) panel.NotifySpan();
        Changed?.Invoke(this, EventArgs.Empty);
        LayoutReset?.Invoke(this, EventArgs.Empty);
    }

    internal void PanelChanged()
    {
        if (!_restoring) Changed?.Invoke(this, EventArgs.Empty);
    }
}
