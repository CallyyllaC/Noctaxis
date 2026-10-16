using Noctaxis.Core.Localisation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Themes;
using NodaTime;

namespace Noctaxis.Desktop.Controls;

/// <summary>
/// The Timeline's chart: every target's altitude across the Timeline window (24 hours on from its anchor,
/// across midnight if it falls there), against a clear horizon and a few altitude and hour guides, with the
/// plan's moment as the one strong cursor. The paths are the planning service's own samples; this only draws.
/// </summary>
/// <remarks>
/// Hierarchy, strongest first: the selected moment (an accent line, its time above it); the paths and their
/// names; astronomical darkness (a faint accent band behind everything on the plot); midnight (a firmer divider,
/// labelled with the new date); the 0° horizon (the geometric horizon, distinct from the terrain beneath it);
/// sunrise and sunset, then moonrise and moonset (short ticks on the time axis, named where there is room); then
/// faint hour and altitude guides, and the real present moment, which is drawn faintly and only when it falls
/// inside the window, so it is never mistaken for the selection. Every event cue is the window's own, calculated
/// with its paths (<see cref="Events"/>), never while drawing.
/// </remarks>
public sealed class HorizonGraph : ChromeDrawingControl
{
    /// <summary>The altitude range drawn, in degrees: a little below the horizon up to the zenith.</summary>
    public const double LowestAltitude = -20, HighestAltitude = 90;
    /// <summary>The altitude guides, below the zenith; 0° is the horizon and is drawn more firmly.</summary>
    public static readonly double[] AltitudeGuides = [0, 30, 60];
    /// <summary>The hour steps labels may take, chosen by the room each has.</summary>
    public static readonly int[] LabelSteps = [3, 6, 12];

    public static readonly StyledProperty<PlanningSnapshot?> SnapshotProperty =
        AvaloniaProperty.Register<HorizonGraph, PlanningSnapshot?>(nameof(Snapshot));
    public static readonly StyledProperty<CelestialColourSet> CelestialColoursProperty =
        AvaloniaProperty.Register<HorizonGraph, CelestialColourSet>(nameof(CelestialColours), CelestialColourSet.Default);
    public static readonly StyledProperty<IReadOnlyList<TimelinePath>?> PathsProperty =
        AvaloniaProperty.Register<HorizonGraph, IReadOnlyList<TimelinePath>?>(nameof(Paths));
    public static readonly StyledProperty<Instant> WindowStartProperty =
        AvaloniaProperty.Register<HorizonGraph, Instant>(nameof(WindowStart));
    public static readonly StyledProperty<Instant> WindowEndProperty =
        AvaloniaProperty.Register<HorizonGraph, Instant>(nameof(WindowEnd));
    public static readonly StyledProperty<DateTimeZone?> ZoneProperty =
        AvaloniaProperty.Register<HorizonGraph, DateTimeZone?>(nameof(Zone));
    public static readonly StyledProperty<Instant?> RealNowProperty =
        AvaloniaProperty.Register<HorizonGraph, Instant?>(nameof(RealNow));
    public static readonly StyledProperty<Instant?> SelectedProperty =
        AvaloniaProperty.Register<HorizonGraph, Instant?>(nameof(Selected));
    public static readonly StyledProperty<IBrush?> CursorBrushProperty =
        AvaloniaProperty.Register<HorizonGraph, IBrush?>(nameof(CursorBrush));
    public static readonly StyledProperty<TimelineEvents?> EventsProperty =
        AvaloniaProperty.Register<HorizonGraph, TimelineEvents?>(nameof(Events));
    public static readonly StyledProperty<IBrush?> DarknessBrushProperty =
        AvaloniaProperty.Register<HorizonGraph, IBrush?>(nameof(DarknessBrush));

    /// <summary>The Sun's and Moon's events inside the window, and whether it opens in astronomical darkness.</summary>
    public TimelineEvents? Events { get => GetValue(EventsProperty); set => SetValue(EventsProperty, value); }
    /// <summary>The astronomical-darkness band's tint: the theme's accent, drawn very faintly.</summary>
    public IBrush? DarknessBrush { get => GetValue(DarknessBrushProperty); set => SetValue(DarknessBrushProperty, value); }
    /// <summary>How strongly the darkness band is tinted: enough to see, never enough to compete with a path.</summary>
    public const double DarknessOpacity = .1;

    /// <summary>The plan: its moment, its targets' roles and the terrain profile.</summary>
    public PlanningSnapshot? Snapshot { get => GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
    /// <summary>Theme data colours (<see cref="CelestialThemeTokens"/>), bound to the current theme.</summary>
    public CelestialColourSet CelestialColours { get => GetValue(CelestialColoursProperty); set => SetValue(CelestialColoursProperty, value); }
    /// <summary>Each target's path across the window.</summary>
    public IReadOnlyList<TimelinePath>? Paths { get => GetValue(PathsProperty); set => SetValue(PathsProperty, value); }
    public Instant WindowStart { get => GetValue(WindowStartProperty); set => SetValue(WindowStartProperty, value); }
    public Instant WindowEnd { get => GetValue(WindowEndProperty); set => SetValue(WindowEndProperty, value); }
    /// <summary>The zone the hours and dates are read in: the plan's.</summary>
    public DateTimeZone? Zone { get => GetValue(ZoneProperty); set => SetValue(ZoneProperty, value); }
    /// <summary>The real present moment, drawn faintly when it falls inside the window.</summary>
    public Instant? RealNow { get => GetValue(RealNowProperty); set => SetValue(RealNowProperty, value); }
    /// <summary>The moment chosen, which the cursor marks; without one, the plan's own moment.</summary>
    public Instant? Selected { get => GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    /// <summary>The selected moment's colour: the theme's accent.</summary>
    public IBrush? CursorBrush { get => GetValue(CursorBrushProperty); set => SetValue(CursorBrushProperty, value); }

    public HorizonGraph()
    {
        // Theme defaults: a value set on the chart, or a style, wins over them (ThemeDefault).
        this.BindThemeDefault(CelestialColoursProperty, CelestialThemeTokens.ColourSet);
        this.BindThemeDefault(CursorBrushProperty, "Accent");
        this.BindThemeDefault(DarknessBrushProperty, "Accent");
    }

    /// <summary>
    /// Whether the chart cues an event. The Moon's rise and set only while its own path is hidden, since a shown
    /// path already crosses the horizon there; the Sun's rise and set unless the Sun is the focused target, whose
    /// own rise and set marks are drawn; darkness always.
    /// </summary>
    public static bool ShowsEvent(TimelineEventKind kind, IReadOnlyList<TimelinePath>? paths, string? focusedId) => kind switch
    {
        TimelineEventKind.Moonrise or TimelineEventKind.Moonset => !(paths ?? []).Any(path => path.Target.IsMoon),
        TimelineEventKind.Sunrise or TimelineEventKind.Sunset => !string.Equals(focusedId, "sun", StringComparison.OrdinalIgnoreCase),
        _ => true
    };

    /// <summary>An event's time as the chart's hours read: 24-hour, in the plan's zone.</summary>
    public static string Clock(Instant instant, DateTimeZone zone)
    {
        var local = instant.InZone(zone);
        return $"{local.Hour:00}:{local.Minute:00}";
    }

    /// <summary>
    /// How far into the window the chosen moment is, in minutes: the chart's own scrubbing writes it (two-way, to
    /// the Planner's <c>TimelineOffsetMinutes</c>), which moves the Planner's one date and time.
    /// </summary>
    public static readonly StyledProperty<double> ScrubMinutesProperty =
        AvaloniaProperty.Register<HorizonGraph, double>(nameof(ScrubMinutes), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    /// <summary>One keyboard step, in minutes: the Planner's time snap.</summary>
    public static readonly StyledProperty<double> StepMinutesProperty =
        AvaloniaProperty.Register<HorizonGraph, double>(nameof(StepMinutes), 5);

    public double ScrubMinutes { get => GetValue(ScrubMinutesProperty); set => SetValue(ScrubMinutesProperty, value); }
    public double StepMinutes { get => GetValue(StepMinutesProperty); set => SetValue(StepMinutesProperty, value); }

    /// <summary>A drag or a tap on the chart has ended: the moment it chose can be committed.</summary>
    public event EventHandler? ScrubCompleted;

    /// <summary>The hours must be at least this far apart for the minor hourly guides to be drawn.</summary>
    public const double MinimumMinorGuideSpacing = 9;
    /// <summary>The cursor's tab: small to look at, above the plot, on the cursor.</summary>
    public const double HandleWidth = 14, HandleHeight = 11;

    static HorizonGraph()
    {
        AffectsRender<HorizonGraph>(SnapshotProperty, CelestialColoursProperty, PathsProperty,
            WindowStartProperty, WindowEndProperty, ZoneProperty, RealNowProperty, CursorBrushProperty, SelectedProperty,
            EventsProperty, DarknessBrushProperty);
        FocusableProperty.OverrideDefaultValue<HorizonGraph>(true);
    }

    /// <summary>What the last frame drew, for tests: its plot, its hour labels and guides, and its two moments.</summary>
    internal sealed record Layout(Rect Plot, IReadOnlyList<(string Text, Rect Bounds, bool IsMidnight)> TimeLabels,
        double? CursorX, double? RealNowX, double HorizonY, IReadOnlyList<(double Altitude, double Y)> Guides,
        IReadOnlyList<double> MajorGuideXs, IReadOnlyList<double> MinorGuideXs, IReadOnlyList<double> MidnightXs,
        Rect? Handle, Rect? CursorLabel, IReadOnlyList<(double Left, double Right)> DarkBands, IReadOnlyList<EventMark> EventMarks);

    /// <summary>One event cue as drawn: its tick's X, and its label and where it went, if it had room for one.</summary>
    internal sealed record EventMark(TimelineEventKind Kind, double X, string? Label, Rect? LabelBounds);
    internal Layout? LastLayout { get; private set; }

    /// <summary>Where the chart plots, in its own coordinates.</summary>
    internal Rect PlotRectangle => PlotFor(Bounds.Size);

    /// <summary>
    /// The plot area for a chart of this size: a gutter for the altitude labels, a row above for the cursor's tab
    /// and its time, and a row beneath for the hours.
    /// </summary>
    private Rect PlotFor(Size size)
    {
        var gutter = ChromeText("60°").Width + 8;
        var top = Math.Max(HandleHeight, ChromeText("00:00").Height) + 3;
        var hours = ChromeFontSize + 8;
        return new Rect(gutter, top, Math.Max(1, size.Width - gutter - 8), Math.Max(1, size.Height - top - hours));
    }

    private double Y(Rect plot, double altitude) =>
        plot.Bottom - Math.Clamp((altitude - LowestAltitude) / (HighestAltitude - LowestAltitude), 0, 1) * plot.Height;

    private double? X(Rect plot, Instant instant)
    {
        var span = (WindowEnd - WindowStart).TotalSeconds;
        if (span <= 0) return null;
        var fraction = (instant - WindowStart).TotalSeconds / span;
        return fraction is < -1e-9 or > 1 + 1e-9 ? null : plot.Left + fraction * plot.Width;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(ChromeBackground, new Rect(Bounds.Size));
        LastLayout = null;
        if (Bounds.Width < 40 || Bounds.Height < 40 || WindowEnd <= WindowStart) return;
        var plot = PlotFor(Bounds.Size);
        var colours = CelestialColours;
        var zone = Zone ?? DateTimeZone.Utc;
        var muted = Muted(ChromeForeground, .16);
        var mutedText = Muted(ChromeForeground, .6);

        // Astronomical darkness: a faint band behind everything else on the plot, from the window's edge when
        // the window opens or closes dark.
        var darkBands = new List<(double, double)>();
        if (Events is { } sky && DarknessBrush is { } darkness)
        {
            var band = Muted(darkness, DarknessOpacity);
            foreach (var (from, to) in sky.DarknessIntervals(WindowStart, WindowEnd))
                if (X(plot, from) is { } left && X(plot, to) is { } right && right > left)
                {
                    context.FillRectangle(band, new Rect(left, plot.Top, right - left, plot.Height));
                    darkBands.Add((left, right));
                }
        }

        // Altitude guides: faint at 30° and 60°, the horizon firmly; 90° is the plot's top, labelled only.
        var guides = new List<(double, double)>();
        foreach (var altitude in AltitudeGuides)
        {
            var y = Y(plot, altitude);
            guides.Add((altitude, y));
            if (altitude != 0) context.DrawLine(new Pen(muted, 1), new Point(plot.Left, y), new Point(plot.Right, y));
            DrawGutterLabel(context, $"{altitude:0}°", y, altitude == 0 ? ChromeForeground : mutedText);
        }
        DrawGutterLabel(context, $"{HighestAltitude:0}°", plot.Top + ChromeFontSize / 2, mutedText);
        var horizonY = Y(plot, 0);

        // Hour guides and labels, then midnight's firmer divider with the new date.
        var labels = DrawTimeScale(context, plot, zone, muted, mutedText);

        // Terrain under the primary path's azimuths, beneath everything else on the plot.
        var paths = Paths ?? [];
        // The focused target, drawn boldest with its terrain and rise, transit and set; none when nothing is focused.
        var primaryId = Snapshot is { Session.IsTargetFocused: true } focused ? focused.Position.Target.Id : null;
        var primaryPath = paths.FirstOrDefault(path => path.Target.Id.Equals(primaryId, StringComparison.OrdinalIgnoreCase))?.Path;
        if (Snapshot is { } snapshot && primaryPath is { Samples.Count: > 1 } && snapshot.Terrain.Samples.Count > 1)
        {
            var terrain = new StreamGeometry();
            using (var g = terrain.Open())
            {
                g.BeginFigure(new Point(plot.Left, plot.Bottom), true);
                foreach (var sample in primaryPath.Samples)
                    if (X(plot, sample.Instant) is { } x) g.LineTo(new Point(x, Y(plot, snapshot.Terrain.AltitudeAt(sample.Horizontal.AzimuthDegrees))));
                g.LineTo(new Point(plot.Right, plot.Bottom));
                g.EndFigure(true);
            }
            context.DrawGeometry(new SolidColorBrush(colours.TerrainFill), new Pen(new SolidColorBrush(colours.TerrainOutline), 1), terrain);
        }
        // The geometric horizon, over the terrain so the two are never confused.
        context.DrawLine(new Pen(Muted(ChromeForeground, .75), 1.3), new Point(plot.Left, horizonY), new Point(plot.Right, horizonY));

        // The paths, as before: primary thick and solid, the others thinner in their own dash; below the
        // horizon every path thinner, dashed and muted.
        var culminations = new List<(Point Point, string Label)>();
        var taken = new List<Rect>();
        if (Snapshot is { } plan)
        {
            var belowHorizon = new SolidColorBrush(colours.BelowHorizon);
            foreach (var (target, path) in paths)
            {
                if (path.Samples.Count <= 1) continue;
                var primary = target.Id.Equals(primaryId, StringComparison.OrdinalIgnoreCase);
                var role = CelestialPalette.Role(plan, target);
                var above = CelestialPalette.Stroke(colours, role, primary, primary ? 2.6 : 1.5);
                var below = new Pen(belowHorizon, primary ? 1.5 : 1.1, dashStyle: primary ? DashStyle.Dash : CelestialPalette.Dash(role));
                var highest = -1;
                for (var i = 1; i < path.Samples.Count; i++)
                {
                    var (a, b) = (path.Samples[i - 1], path.Samples[i]);
                    if (X(plot, a.Instant) is not { } xa || X(plot, b.Instant) is not { } xb) continue;
                    context.DrawLine(a.IsAboveHorizon || b.IsAboveHorizon ? above : below,
                        new Point(xa, Y(plot, a.Horizontal.AltitudeDegrees)), new Point(xb, Y(plot, b.Horizontal.AltitudeDegrees)));
                }
                for (var i = 0; i < path.Samples.Count; i++)
                    if (path.Samples[i].IsAboveHorizon && (highest < 0 || path.Samples[i].Horizontal.AltitudeDegrees > path.Samples[highest].Horizontal.AltitudeDegrees))
                        highest = i;
                if (highest >= 0 && X(plot, path.Samples[highest].Instant) is { } hx)
                    culminations.Add((new Point(hx, Y(plot, path.Samples[highest].Horizontal.AltitudeDegrees)), CelestialPalette.ShortLabel(target)));
            }
            taken.AddRange(DrawPeakLabels(context, plot, culminations));
            if (primaryPath is { } focus)
            {
                DrawEventMarker(context, plot, focus.Events.Rise, Strings.Get("Planner_Timeline_Marker_Rise"), mutedText);
                DrawEventMarker(context, plot, focus.Events.Transit, Strings.Get("Planner_Timeline_Marker_Transit"), mutedText);
                DrawEventMarker(context, plot, focus.Events.Set, Strings.Get("Planner_Timeline_Marker_Set"), mutedText);
            }
        }

        // The real present moment: faint and dotted, below the plot's labels, and only inside the window.
        double? nowX = RealNow is { } now ? X(plot, now) : null;
        if (nowX is { } nx)
        {
            context.DrawLine(new Pen(mutedText, 1, dashStyle: DashStyle.Dot), new Point(nx, plot.Top), new Point(nx, plot.Bottom));
            var text = ChromeText(Strings.Get("Planner_Timeline_Marker_RealNow"));
            text.SetForegroundBrush(mutedText);
            var at = new Point(Math.Min(nx + 3, plot.Right - text.Width), plot.Bottom - text.Height - 2);
            context.DrawText(text, at);
            taken.Add(new Rect(at, new Size(text.Width, text.Height)));
        }

        // The Sun's and Moon's events: short ticks on the time axis, named where there is room.
        var eventMarks = DrawSkyEvents(context, plot, zone, primaryId, paths, taken);

        // The selected moment: the one strong line, with its tab above the plot (what is dragged, and what has
        // the keyboard) and its time beside the tab, on whichever side has room.
        var moment = Selected ?? Snapshot?.Session.Instant;
        double? cursorX = moment is { } chosen ? X(plot, chosen) : null;
        Rect? handle = null, cursorLabel = null;
        if (cursorX is { } cx && moment is { } m)
        {
            var accent = CursorBrush ?? ChromeForeground;
            context.DrawLine(new Pen(accent, 1.8), new Point(cx, plot.Top - 2), new Point(cx, plot.Bottom));
            var tab = new Rect(cx - HandleWidth / 2, plot.Top - 2 - HandleHeight, HandleWidth, HandleHeight);
            context.DrawRectangle(accent, null, tab, 3, 3);
            var grip = new Pen(ChromeBackground, 1.2);
            context.DrawLine(grip, new Point(cx - 2, tab.Top + 3), new Point(cx - 2, tab.Bottom - 3));
            context.DrawLine(grip, new Point(cx + 2, tab.Top + 3), new Point(cx + 2, tab.Bottom - 3));
            if (_keyboardFocus && IsFocused)
                context.DrawRectangle(null, new Pen(accent, 1.5), tab.Inflate(3), 5, 5);
            handle = tab;

            var time = m.InZone(zone);
            var text = ChromeText($"{time.Hour:00}:{time.Minute:00}");
            text.SetForegroundBrush(accent);
            var right = tab.Right + 4;
            var x = right + text.Width <= plot.Right ? right : tab.Left - 4 - text.Width;
            x = Math.Clamp(x, plot.Left, plot.Right - text.Width);
            var y = tab.Center.Y - text.Height / 2;
            cursorLabel = new Rect(x, y, text.Width, text.Height);
            context.DrawText(text, new Point(x, y));
        }

        LastLayout = new Layout(plot, labels, cursorX, nowX, horizonY, guides,
            _majorGuides.ToArray(), _minorGuides.ToArray(), _midnights.ToArray(), handle, cursorLabel, darkBands, eventMarks);
    }

    private static IBrush Muted(IBrush brush, double opacity) =>
        brush is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, solid.Opacity * opacity) : brush;

    private void DrawGutterLabel(DrawingContext context, string label, double y, IBrush brush)
    {
        var text = ChromeText(label);
        text.SetForegroundBrush(brush);
        context.DrawText(text, new Point(PlotFor(Bounds.Size).Left - 5 - text.Width, y - text.Height / 2));
    }

    private readonly List<double> _majorGuides = [], _minorGuides = [], _midnights = [];

    /// <summary>
    /// Hour guides and labels across the window, in the plan's zone. A faint major guide every 3 hours; a fainter,
    /// dashed minor guide on each other whole hour, while the hours are at least
    /// <see cref="MinimumMinorGuideSpacing"/> apart (narrower, the minors go first); and midnight, a firmer
    /// divider in place of either, labelled with its date. Labels sit on whole hours that are a multiple of the
    /// step (3, 6 or 12 hours: the smallest whose labels have room), never overlapping. All of it is drawn before
    /// the paths, behind them.
    /// </summary>
    private List<(string Text, Rect Bounds, bool IsMidnight)> DrawTimeScale(DrawingContext context, Rect plot, DateTimeZone zone,
        IBrush guide, IBrush label)
    {
        var placed = new List<(string, Rect, bool)>();
        _majorGuides.Clear();
        _minorGuides.Clear();
        _midnights.Clear();
        var labelWidth = ChromeText("00:00").Width;
        var perHour = plot.Width / (WindowEnd - WindowStart).TotalHours;
        var step = LabelSteps.FirstOrDefault(hours => hours * perHour >= labelWidth + 12, LabelSteps[^1]);
        const int guideStep = 3;
        var minors = perHour >= MinimumMinorGuideSpacing;
        var labelY = plot.Bottom + 4;
        var midnightPen = new Pen(Muted(ChromeForeground, .5), 1.2);
        var guidePen = new Pen(guide, 1);
        var minorPen = new Pen(Muted(ChromeForeground, .09), 1, dashStyle: new DashStyle([2, 3], 0));

        // Whole hours from the first one after the window opens to its close.
        var start = WindowStart.InZone(zone);
        var hour = zone.AtLeniently(start.LocalDateTime.Date.At(new LocalTime(start.Hour, 0))).ToInstant();
        if (hour <= WindowStart) hour += Duration.FromHours(1);
        for (; hour < WindowEnd; hour += Duration.FromHours(1))
        {
            var local = hour.InZone(zone);
            if (local.Minute != 0 || X(plot, hour) is not { } x) continue;
            var midnight = local.Hour == 0;
            if (midnight)
            {
                context.DrawLine(midnightPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                _midnights.Add(x);
            }
            else if (local.Hour % guideStep == 0)
            {
                context.DrawLine(guidePen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                _majorGuides.Add(x);
            }
            else if (minors)
            {
                context.DrawLine(minorPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                _minorGuides.Add(x);
            }
            if (!midnight && local.Hour % step != 0) continue;
            var textValue = midnight
                ? Strings.Format("Planner_Timeline_Midnight_Format", local.Date.ToDateTimeUnspecified())
                : $"{local.Hour:00}:00";
            var text = ChromeText(textValue);
            text.SetForegroundBrush(midnight ? ChromeForeground : label);
            if (midnight) text.SetFontWeight(FontWeight.SemiBold);
            var bounds = new Rect(Math.Clamp(x - text.Width / 2, plot.Left, plot.Right - text.Width), labelY, text.Width, text.Height);
            // Midnight's date always shows; an hour label gives way to it, and to any label already placed.
            if (placed.Any(other => other.Item2.Inflate(new Thickness(4, 0)).Intersects(bounds)))
            {
                if (!midnight) continue;
                placed.RemoveAll(other => other.Item2.Inflate(new Thickness(4, 0)).Intersects(bounds));
            }
            placed.Add((textValue, bounds, midnight));
        }
        foreach (var (textValue, bounds, midnight) in placed)
        {
            var text = ChromeText(textValue);
            text.SetForegroundBrush(midnight ? ChromeForeground : label);
            if (midnight) text.SetFontWeight(FontWeight.SemiBold);
            context.DrawText(text, bounds.TopLeft);
        }
        return placed;
    }

    /// <summary>
    /// Each path's compact name beside its highest point, so a path is named as well as coloured and patterned.
    /// No shape is drawn on the path itself.
    /// </summary>
    private List<Rect> DrawPeakLabels(DrawingContext context, Rect plot, IReadOnlyList<(Point Point, string Label)> culminations)
    {
        var placed = new List<Rect>();
        foreach (var (point, label) in culminations)
        {
            var text = ChromeText(label);
            var x = point.X + 4 + text.Width > plot.Right ? point.X - 4 - text.Width : point.X + 4;
            var bounds = new Rect(x, point.Y - text.Height - 1, text.Width, text.Height);
            // Nudge overlapping labels down rather than stacking them on top of each other.
            for (var attempt = 0; attempt < 4 && placed.Any(other => other.Intersects(bounds)); attempt++)
                bounds = bounds.Translate(new Vector(0, text.Height));
            bounds = bounds.WithY(Math.Clamp(bounds.Y, plot.Top, Math.Max(plot.Top, plot.Bottom - text.Height)));
            placed.Add(bounds);
            context.FillRectangle(ChromeBackground, bounds.Inflate(1), 2);
            context.DrawText(text, bounds.TopLeft);
        }
        return placed;
    }

    /// <summary>
    /// The Sun's and Moon's events (<see cref="ShowsEvent"/>) inside the window, below the paths in weight: a
    /// short tick rising from the time axis, and its name with its time just above the axis — the name alone where
    /// that is too long, and no label at all where it would overlap another label, the Sun's first, then
    /// darkness's, then the Moon's. Nothing outside the window is drawn.
    /// </summary>
    private List<EventMark> DrawSkyEvents(DrawingContext context, Rect plot, DateTimeZone zone, string? focusedId,
        IReadOnlyList<TimelinePath> paths, List<Rect> taken)
    {
        var marks = new List<EventMark>();
        if (Events is not { } sky) return marks;
        static int Priority(TimelineEventKind kind) => kind switch
        {
            TimelineEventKind.Sunrise or TimelineEventKind.Sunset => 0,
            TimelineEventKind.AstronomicalDarknessBegins or TimelineEventKind.AstronomicalDarknessEnds => 1,
            _ => 2
        };
        foreach (var item in sky.Events.Where(item => ShowsEvent(item.Kind, paths, focusedId)).OrderBy(item => Priority(item.Kind)))
        {
            if (X(plot, item.Instant) is not { } x) continue;
            var strength = Priority(item.Kind) switch { 0 => .6, 1 => .5, _ => .42 };
            var brush = Muted(ChromeForeground, strength);
            var name = Strings.Get("Planner_Timeline_Event_" + item.Kind);
            string? placedLabel = null;
            Rect? placedBounds = null;
            // The name and time on the axis's own row, else on the row above it (cues close together stack),
            // else the name alone on either, else no label: never one over another.
            foreach (var candidate in new[] { Strings.Format("Planner_Timeline_EventLabel_Format", name, Clock(item.Instant, zone)), name })
            {
                var text = ChromeText(candidate);
                var left = x + 3 + text.Width <= plot.Right ? x + 3 : x - 3 - text.Width;
                if (left < plot.Left) continue;
                foreach (var row in new[] { 0, 1 })
                {
                    var bounds = new Rect(left, plot.Bottom - 9 - text.Height * (row + 1), text.Width, text.Height);
                    if (taken.Any(other => other.Inflate(new Thickness(3, 0)).Intersects(bounds))) continue;
                    text.SetForegroundBrush(brush);
                    context.DrawText(text, bounds.TopLeft);
                    taken.Add(bounds);
                    (placedLabel, placedBounds) = (candidate, bounds);
                    break;
                }
                if (placedLabel is not null) break;
            }
            // The tick rises from the axis to its label's row, so a stacked label still reads as its own.
            var top = placedBounds is { } placed ? Math.Min(plot.Bottom - 7, placed.Bottom) : plot.Bottom - 7;
            context.DrawLine(new Pen(brush, 1.2), new Point(x, top), new Point(x, plot.Bottom));
            marks.Add(new EventMark(item.Kind, x, placedLabel, placedBounds));
        }
        return marks;
    }

    /// <summary>The focused target's rise, transit and set within the window: faint, with their one-letter mark.</summary>
    private void DrawEventMarker(DrawingContext context, Rect plot, Instant? instant, string label, IBrush brush)
    {
        if (instant is null || X(plot, instant.Value) is not { } x) return;
        context.DrawLine(new Pen(brush, 1, dashStyle: DashStyle.Dot), new Point(x, plot.Top + ChromeFontSize + 4), new Point(x, plot.Bottom));
        var text = ChromeText(label);
        text.SetForegroundBrush(brush);
        context.DrawText(text, new Point(Math.Min(x + 3, plot.Right - text.Width), plot.Top + ChromeFontSize + 4));
    }
    // ---- Scrubbing ---------------------------------------------------------------------------------------

    private bool _scrubbing, _keyboardFocus;

    /// <summary>The window's minutes at a point across the chart: its X only, within the window.</summary>
    internal double MinutesAt(double x)
    {
        var plot = PlotFor(Bounds.Size);
        var minutes = (WindowEnd - WindowStart).TotalMinutes;
        return Math.Clamp((x - plot.Left) / plot.Width, 0, 1) * minutes;
    }

    /// <summary>
    /// A press anywhere on the chart — its plot, the cursor's tab, the hour row — moves the moment there, by X
    /// alone, and holding moves it as the pointer moves: a mouse, a pen or a finger alike. The pointer is held
    /// by the chart until it is let go, so nothing under it is selected or dragged, and the moment is committed then.
    /// </summary>
    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || WindowEnd <= WindowStart) return;
        e.Handled = true;
        _scrubbing = true;
        e.Pointer.Capture(this);
        Focus(Avalonia.Input.NavigationMethod.Pointer);
        ScrubMinutes = MinutesAt(e.GetPosition(this).X);
    }

    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_scrubbing) return;
        e.Handled = true;
        ScrubMinutes = MinutesAt(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_scrubbing) return;
        e.Handled = true;
        e.Pointer.Capture(null);
        EndScrub();
    }

    protected override void OnPointerCaptureLost(Avalonia.Input.PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndScrub();
    }

    private void EndScrub()
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        ScrubCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// With the keyboard on the chart (its tab shows a ring), Left and Right move the moment one time snap,
    /// Page Up and Page Down an hour, Home and End to the window's ends.
    /// </summary>
    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var length = (WindowEnd - WindowStart).TotalMinutes;
        double? next = e.Key switch
        {
            Avalonia.Input.Key.Right => ScrubMinutes + StepMinutes,
            Avalonia.Input.Key.Left => ScrubMinutes - StepMinutes,
            Avalonia.Input.Key.PageUp => ScrubMinutes + 60,
            Avalonia.Input.Key.PageDown => ScrubMinutes - 60,
            Avalonia.Input.Key.Home => 0,
            Avalonia.Input.Key.End => length,
            _ => null
        };
        if (next is not { } minutes || length <= 0) return;
        e.Handled = true;
        ScrubMinutes = Math.Clamp(minutes, 0, length);
    }

    protected override void OnGotFocus(Avalonia.Input.FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _keyboardFocus = e.NavigationMethod is Avalonia.Input.NavigationMethod.Tab or Avalonia.Input.NavigationMethod.Directional;
        InvalidateVisual();
    }

    protected override void OnLostFocus(Avalonia.Input.FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _keyboardFocus = false;
        InvalidateVisual();
    }
}
