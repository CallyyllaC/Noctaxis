using System.Text.Json.Serialization;

namespace Noctaxis.Core.Domain;

/// <summary>Where a Planner panel is: in a dock, loose over the map, or collapsed to an edge tab.</summary>
public enum PanelDockMode
{
    /// <summary>Pinned into one of the four docks, holding its own space beside the map.</summary>
    Docked,
    /// <summary>Loose over the map, inside the workspace, with its own position and size.</summary>
    Floating,
    /// <summary>Unpinned: collapsed to a tab on its dock's edge, shown over the map on demand.</summary>
    AutoHidden
}

public enum PanelDockSide { Left, Right, Top, Bottom }

/// <summary>One of the four places where two docks meet.</summary>
public enum DockCorner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>
/// Which dock reaches into each corner. Where a side dock and an end dock meet, one of them runs the
/// full way and the other stops short, and that is a choice the layout records rather than a rule
/// built into it: a user may want the timeline to run under the side docks, or between them, and the
/// two bottom corners need not agree.
/// </summary>
/// <remarks>
/// Each corner belongs to one of its own two neighbours — the top-left corner to the left dock or the
/// top dock, and never to the right or bottom one — so the two docks can never both claim it and no
/// arrangement can overlap. A value naming a dock that does not touch the corner is not a layout this
/// can express; <see cref="Normalised"/> returns that corner to its default rather than honour it.
/// </remarks>
public sealed record DockCornerOwnership(
    PanelDockSide TopLeft = PanelDockSide.Left,
    PanelDockSide TopRight = PanelDockSide.Right,
    PanelDockSide BottomLeft = PanelDockSide.Bottom,
    PanelDockSide BottomRight = PanelDockSide.Bottom)
{
    /// <summary>
    /// The shipped arrangement: the side docks reach the top corners and the bottom dock runs the full
    /// width beneath them, so the timeline spans the whole window under the map and the Inspector.
    /// </summary>
    public static DockCornerOwnership Default { get; } = new();

    /// <summary>The two docks that touch a corner: the side one first, then the end one.</summary>
    public static (PanelDockSide Side, PanelDockSide End) Neighbours(DockCorner corner) => corner switch
    {
        DockCorner.TopLeft => (PanelDockSide.Left, PanelDockSide.Top),
        DockCorner.TopRight => (PanelDockSide.Right, PanelDockSide.Top),
        DockCorner.BottomLeft => (PanelDockSide.Left, PanelDockSide.Bottom),
        _ => (PanelDockSide.Right, PanelDockSide.Bottom)
    };

    public static IReadOnlyList<DockCorner> All { get; } =
        [DockCorner.TopLeft, DockCorner.TopRight, DockCorner.BottomLeft, DockCorner.BottomRight];

    /// <summary>The corners a dock reaches into, which is how a dock knows how far it runs.</summary>
    public static IReadOnlyList<DockCorner> CornersOf(PanelDockSide side) => side switch
    {
        PanelDockSide.Left => [DockCorner.TopLeft, DockCorner.BottomLeft],
        PanelDockSide.Right => [DockCorner.TopRight, DockCorner.BottomRight],
        PanelDockSide.Top => [DockCorner.TopLeft, DockCorner.TopRight],
        _ => [DockCorner.BottomLeft, DockCorner.BottomRight]
    };

    public PanelDockSide Owner(DockCorner corner) => corner switch
    {
        DockCorner.TopLeft => TopLeft,
        DockCorner.TopRight => TopRight,
        DockCorner.BottomLeft => BottomLeft,
        _ => BottomRight
    };

    public bool Owns(PanelDockSide side, DockCorner corner) => Owner(corner) == side;

    public DockCornerOwnership With(DockCorner corner, PanelDockSide owner)
    {
        var (sideDock, endDock) = Neighbours(corner);
        if (owner != sideDock && owner != endDock) return this;
        return corner switch
        {
            DockCorner.TopLeft => this with { TopLeft = owner },
            DockCorner.TopRight => this with { TopRight = owner },
            DockCorner.BottomLeft => this with { BottomLeft = owner },
            _ => this with { BottomRight = owner }
        };
    }

    /// <summary>Gives a dock both of its corners, or hands both back to the docks beside it.</summary>
    public DockCornerOwnership WithSpan(PanelDockSide side, bool spans)
    {
        var ownership = this;
        foreach (var corner in CornersOf(side))
        {
            var (sideDock, endDock) = Neighbours(corner);
            ownership = ownership.With(corner, spans ? side : side == sideDock ? endDock : sideDock);
        }
        return ownership;
    }

    /// <summary>True when this dock reaches into both of its corners, and so runs the whole way.</summary>
    public bool Spans(PanelDockSide side) => CornersOf(side).All(corner => Owns(side, corner));

    /// <summary>A corner claimed by a dock that does not touch it returns to its default.</summary>
    public DockCornerOwnership Normalised()
    {
        var ownership = Default;
        foreach (var corner in All) ownership = ownership.With(corner, Owner(corner));
        return ownership;
    }
}

/// <summary>
/// One Planner panel's placement. The panel set itself belongs to the application (the panels the
/// Planner declares); this records only where the user put each one, so an id this version does not
/// know is dropped and a panel the file does not mention keeps its default.
/// </summary>
/// <remarks>
/// <para><see cref="DockedSize"/> is the extent across the dock: the width of a left or right dock,
/// the height of a top or bottom one. The length along the dock is shared between the panels docked
/// there and is not a stored preference.</para>
/// <para>Pinning is not stored separately: a pinned panel is a docked one. Unpinning moves a panel to
/// <see cref="PanelDockMode.AutoHidden"/> and pinning it again docks it to the side it remembers, so
/// the two can never disagree.</para>
/// <para>There is no hidden state. The Planner's panels are its permanent tools, and unpinning is how
/// one is put out of the way. Layouts written when panels could be hidden carried an <c>IsVisible</c>
/// field; it is no longer a member, so it is skipped when such a file is read, and a panel that was
/// hidden simply comes back in the dock, float or edge tab it was last in.</para>
/// <para><see cref="SelectedTab"/> is the view a panel with several shows, by a stable id. It is null
/// for a panel with one view, and an id the panel does not have is read as its first view.</para>
/// </remarks>
public sealed record PlannerPanelLayout(
    string PanelId,
    PanelDockMode Mode = PanelDockMode.Docked,
    PanelDockSide Side = PanelDockSide.Right,
    double DockedSize = PlannerPanelLayout.DefaultDockedSize,
    double FloatingX = PlannerPanelLayout.DefaultFloatingOffset,
    double FloatingY = PlannerPanelLayout.DefaultFloatingOffset,
    double FloatingWidth = PlannerPanelLayout.DefaultFloatingWidth,
    double FloatingHeight = PlannerPanelLayout.DefaultFloatingHeight,
    int DockOrder = 0,
    string? SelectedTab = null)
{
    public const double DefaultDockedSize = 320;
    public const double MinimumDockedSize = 160;
    public const double MaximumDockedSize = 1600;
    public const double DefaultFloatingOffset = 56;
    public const double DefaultFloatingWidth = 380;
    public const double DefaultFloatingHeight = 300;
    public const double MinimumFloatingWidth = 200;
    public const double MinimumFloatingHeight = 120;

    /// <summary>A pinned panel is a docked one; see the remarks on this type.</summary>
    [JsonIgnore]
    public bool IsPinned => Mode == PanelDockMode.Docked;

    [JsonIgnore]
    public bool IsAcrossTheWidth => Side is PanelDockSide.Top or PanelDockSide.Bottom;

    /// <summary>
    /// The same placement with every number inside its limits. A hand-edited or older file, or one
    /// written on a far larger display, can hold anything; nothing here may produce a panel that
    /// cannot be seen or grabbed.
    /// </summary>
    public PlannerPanelLayout Normalised() => this with
    {
        DockedSize = Clamp(DockedSize, MinimumDockedSize, MaximumDockedSize, DefaultDockedSize),
        FloatingX = Clamp(FloatingX, 0, MaximumDockedSize * 4, DefaultFloatingOffset),
        FloatingY = Clamp(FloatingY, 0, MaximumDockedSize * 4, DefaultFloatingOffset),
        FloatingWidth = Clamp(FloatingWidth, MinimumFloatingWidth, MaximumDockedSize * 4, DefaultFloatingWidth),
        FloatingHeight = Clamp(FloatingHeight, MinimumFloatingHeight, MaximumDockedSize * 4, DefaultFloatingHeight),
        DockOrder = Math.Clamp(DockOrder, 0, 999)
    };

    /// <summary>
    /// The same placement with its floating rectangle brought back inside a workspace of this size.
    /// A layout saved on a larger monitor, or at a smaller text size, must never leave a panel out of
    /// reach: the panel shrinks to fit and is pushed back inside, and its title bar stays grabbable.
    /// </summary>
    public PlannerPanelLayout ClampedTo(double workspaceWidth, double workspaceHeight)
    {
        if (!double.IsFinite(workspaceWidth) || !double.IsFinite(workspaceHeight) ||
            workspaceWidth <= 0 || workspaceHeight <= 0) return this;
        var normalised = Normalised();
        var width = Math.Min(normalised.FloatingWidth, Math.Max(MinimumFloatingWidth, workspaceWidth));
        var height = Math.Min(normalised.FloatingHeight, Math.Max(MinimumFloatingHeight, workspaceHeight));
        return normalised with
        {
            FloatingWidth = width,
            FloatingHeight = height,
            FloatingX = Math.Clamp(normalised.FloatingX, 0, Math.Max(0, workspaceWidth - width)),
            FloatingY = Math.Clamp(normalised.FloatingY, 0, Math.Max(0, workspaceHeight - height)),
            DockedSize = Math.Min(normalised.DockedSize,
                Math.Max(MinimumDockedSize, (normalised.IsAcrossTheWidth ? workspaceHeight : workspaceWidth) * .8))
        };
    }

    private static double Clamp(double value, double minimum, double maximum, double fallback) =>
        Math.Clamp(double.IsFinite(value) ? value : fallback, minimum, maximum);
}

/// <summary>
/// The saved Planner panel layout: one entry per panel the user has moved from its default, and which
/// dock reaches into each corner.
/// </summary>
public sealed record PlannerDockLayout(
    IReadOnlyList<PlannerPanelLayout>? Panels = null,
    DockCornerOwnership? Corners = null)
{
    public static PlannerDockLayout Empty { get; } = new();

    [JsonIgnore]
    public IReadOnlyList<PlannerPanelLayout> EffectivePanels => Panels ?? [];

    [JsonIgnore]
    public DockCornerOwnership EffectiveCorners => (Corners ?? DockCornerOwnership.Default).Normalised();

    /// <summary>
    /// The application's panel set, with each panel's saved placement where there is one. The
    /// defaults decide which panels exist and in what order, so an id this version no longer
    /// declares disappears and a panel added since the file was written arrives at its default.
    /// </summary>
    public IReadOnlyList<PlannerPanelLayout> MergedOver(IReadOnlyList<PlannerPanelLayout> defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        var saved = new Dictionary<string, PlannerPanelLayout>(StringComparer.Ordinal);
        foreach (var panel in EffectivePanels)
            if (!string.IsNullOrWhiteSpace(panel.PanelId)) saved[panel.PanelId] = panel;
        return defaults
            .Select(fallback => saved.TryGetValue(fallback.PanelId, out var panel)
                ? (panel with { PanelId = fallback.PanelId }).Normalised()
                : fallback.Normalised())
            .ToArray();
    }
}
