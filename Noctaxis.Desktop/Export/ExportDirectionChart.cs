using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Localisation;
using Noctaxis.Desktop.Controls;
using NodaTime;

namespace Noctaxis.Desktop.Export;

/// <summary>
/// The plan image's chart: when to point the camera at the focused target, and which way, against the local terrain.
/// One axis, the pitch (altitude), against time. The target's altitude is drawn over the terrain horizon in the
/// target's own direction at each moment, so where the line is above the terrain the target is in view and where it
/// is below, the terrain hides it. Which way to face is written under the time axis: each major tick gives the time
/// and the target's bearing then ("SE 135°"). Behind them: astronomical darkness, the geometric horizon, and the
/// Sun's and Moon's events. This is the export's own chart; the Planner's Timeline is unchanged.
/// </summary>
public sealed class ExportDirectionChart : Control
{
    /// <summary>The axis runs a little below the horizon, so a rise or a set reads as a crossing; it is labelled from 0°.</summary>
    public const double MinimumPitch = -12, MaximumPitch = 90;
    private const double LeftGutter = 48, RightGutter = 16, TopGutter = 56, BottomGutter = 44, FontSize = 12;
    private static readonly Typeface Face = new("Inter");

    /// <summary>The target where it is out of sight, below the horizon or behind the terrain: dotted, in its own colour.</summary>
    internal static readonly IDashStyle HiddenPitchDash = new ImmutableDashStyle([1, 2.2], 0);

    private readonly PlannerExportSnapshot _sheet;

    public ExportDirectionChart(PlannerExportSnapshot sheet) => _sheet = sheet;

    private ExportTheme Colours => _sheet.Theme;
    private ExportTargetSeries Target => _sheet.Target;

    /// <summary>The target in view: its own colour, solid and heavy.</summary>
    internal Pen VisiblePitchPen => new(Brush(Colours.Celestial[Target.Role]), 2.6);
    /// <summary>The target out of sight: the same colour, thin and dotted (heavier under a colour-vision mode).</summary>
    internal Pen HiddenPitchPen => new(Brush(Colours.Celestial[Target.Role]), Colours.Celestial.EmphasisePatterns ? 1.8 : 1.4, HiddenPitchDash);
    /// <summary>The terrain horizon in the target's direction: a filled silhouette with a firm edge, never a line like the target's.</summary>
    internal IBrush TerrainFill => Brush(Colours.Celestial.TerrainFill);
    internal Pen TerrainEdge => new(Brush(Colours.Celestial.TerrainOutline), 1.5);

    /// <summary>A major tick: its moment, its time (or date at midnight), and the target's bearing then, when it is shown.</summary>
    internal sealed record Tick(Instant Time, string Clock, string? Bearing, bool IsMidnight);

    /// <summary>The ticks as last laid out, for tests.</summary>
    internal IReadOnlyList<Tick> Ticks { get; private set; } = [];

    internal Rect Plot => new(LeftGutter, TopGutter, Math.Max(1, Bounds.Width - LeftGutter - RightGutter),
        Math.Max(1, Bounds.Height - TopGutter - BottomGutter));

    private double X(Instant time) => Plot.Left + (time - _sheet.WindowStart).TotalMinutes /
        Math.Max(1, (_sheet.WindowEnd - _sheet.WindowStart).TotalMinutes) * Plot.Width;
    internal double PitchY(double pitch) =>
        Plot.Bottom - (Math.Clamp(pitch, MinimumPitch, MaximumPitch) - MinimumPitch) / (MaximumPitch - MinimumPitch) * Plot.Height;

    /// <summary>
    /// The major ticks, every three hours on the clock, each with the target's bearing at that moment. Where two
    /// bearings would crowd each other, every other one is left out; the times always stay.
    /// </summary>
    internal IReadOnlyList<Tick> LayOutTicks()
    {
        var zone = _sheet.Zone;
        var first = _sheet.WindowStart.InZone(zone);
        var next = first.LocalDateTime.Date.AtMidnight().PlusHours(first.Hour - first.Hour % 3 + 3).InZoneLeniently(zone).ToInstant();
        var times = new List<Instant>();
        for (var time = next; time < _sheet.WindowEnd; time += Duration.FromHours(3)) times.Add(time);
        var bearings = times.Select(time => Target.BearingAt(time) is { } bearing ? ExportTargetSeries.Compass(bearing) : null).ToArray();
        var widest = bearings.Where(text => text is not null).Select(text => Label(text!, FontSize, Brushes.Black).Width).DefaultIfEmpty(0).Max();
        var spacing = times.Count > 1 ? X(times[1]) - X(times[0]) : double.PositiveInfinity;
        var every = spacing >= widest + 16 ? 1 : 2;
        return times.Select((time, index) =>
        {
            var local = time.InZone(zone);
            var midnight = local.Hour == 0 && local.Minute == 0;
            return new Tick(time, midnight ? local.Date.ToString("ddd d MMM", System.Globalization.CultureInfo.CurrentCulture) : HorizonGraph.Clock(time, zone),
                index % every == 0 ? bearings[index] : null, midnight);
        }).ToArray();
    }

    public override void Render(DrawingContext context)
    {
        var plot = Plot;
        var zone = _sheet.Zone;
        var muted = Brush(Colours.SecondaryText);
        var grid = new Pen(Brush(Color.FromArgb(46, Colours.SecondaryText.R, Colours.SecondaryText.G, Colours.SecondaryText.B)), 1);

        // Astronomical darkness, behind everything.
        var dark = Brush(Color.FromArgb(34, Colours.Accent.R, Colours.Accent.G, Colours.Accent.B));
        foreach (var (start, end) in _sheet.Events.DarknessIntervals(_sheet.WindowStart, _sheet.WindowEnd))
            context.FillRectangle(dark, new Rect(new Point(X(start), plot.Top), new Point(X(end), plot.Bottom)));

        Ticks = LayOutTicks();
        using (context.PushClip(plot))
        {
            foreach (var pitch in new[] { 30d, 60d, 90d })
                context.DrawLine(grid, new Point(plot.Left, PitchY(pitch)), new Point(plot.Right, PitchY(pitch)));
            foreach (var tick in Ticks)
                context.DrawLine(tick.IsMidnight ? new Pen(Brush(Color.FromArgb(90, Colours.SecondaryText.R, Colours.SecondaryText.G, Colours.SecondaryText.B)), 1) : grid,
                    new Point(X(tick.Time), plot.Top), new Point(X(tick.Time), plot.Bottom));

            // The terrain horizon in the target's direction at each moment: the height the target has to clear.
            foreach (var run in Runs(Target.Samples, sample => double.IsFinite(sample.Terrain)))
            {
                if (run.Count < 2) continue;
                var fill = new StreamGeometry();
                using (var g = fill.Open())
                {
                    g.BeginFigure(new Point(X(run[0].Time), plot.Bottom), true);
                    foreach (var sample in run) g.LineTo(new Point(X(sample.Time), PitchY(sample.Terrain)));
                    g.LineTo(new Point(X(run[^1].Time), plot.Bottom));
                    g.EndFigure(true);
                }
                context.DrawGeometry(TerrainFill, null, fill);
                Polyline(context, TerrainEdge, run.Select(sample => new Point(X(sample.Time), PitchY(sample.Terrain))).ToArray());
            }

            // The geometric horizon, over the terrain.
            context.DrawLine(new Pen(muted, 1.2), new Point(plot.Left, PitchY(0)), new Point(plot.Right, PitchY(0)));

            // The target: solid where it is in view, dotted where it is not.
            for (var i = 1; i < Target.Samples.Count; i++)
            {
                var (a, b) = (Target.Samples[i - 1], Target.Samples[i]);
                context.DrawLine(a.IsVisible && b.IsVisible ? VisiblePitchPen : HiddenPitchPen,
                    new Point(X(a.Time), PitchY(a.Pitch)), new Point(X(b.Time), PitchY(b.Pitch)));
            }
        }

        var taken = new List<Rect>();
        DrawAxes(context, plot, muted, taken);
        DrawEvents(context, plot, muted, zone, taken);
        DrawTargetMarks(context, plot, zone, taken);
    }

    private void DrawAxes(DrawingContext context, Rect plot, IBrush muted, List<Rect> taken)
    {
        var ink = Brush(Colours.PrimaryText);
        context.DrawText(Label(Strings.Format("Export_Chart_Title_Format", Target.Name), FontSize + 3, ink, FontWeight.SemiBold), new Point(plot.Left, 2));
        // The keys are the marks themselves: the target's line, then the terrain's silhouette.
        const double keyY = 33;
        context.DrawLine(VisiblePitchPen, new Point(plot.Left, keyY), new Point(plot.Left + 22, keyY));
        var pitchKey = Label(Strings.Get("Export_Chart_Pitch"), FontSize, ink);
        context.DrawText(pitchKey, new Point(plot.Left + 27, keyY - pitchKey.Height / 2));
        // The terrain's key only when there is terrain to read: without it the horizon is the geometric 0°.
        if (Target.Samples.Any(sample => double.IsFinite(sample.Terrain)))
        {
            var swatch = new Rect(plot.Left + 27 + pitchKey.Width + 22, keyY - 6, 22, 12);
            context.FillRectangle(TerrainFill, swatch);
            context.DrawLine(TerrainEdge, swatch.TopLeft, swatch.TopRight);
            var terrainKey = Label(Strings.Get("Export_Chart_Terrain"), FontSize, ink);
            context.DrawText(terrainKey, new Point(swatch.Right + 5, keyY - terrainKey.Height / 2));
        }

        foreach (var pitch in new[] { 0d, 30d, 60d, 90d })
        {
            var text = Label($"{pitch:0}°", FontSize, muted);
            context.DrawText(text, new Point(plot.Left - 6 - text.Width, PitchY(pitch) - text.Height / 2));
        }

        // Time, and under it which way the target is then.
        foreach (var tick in Ticks)
        {
            var x = X(tick.Time);
            context.DrawLine(new Pen(muted, 1), new Point(x, plot.Bottom), new Point(x, plot.Bottom + 4));
            var clock = Label(tick.Clock, FontSize, tick.IsMidnight ? ink : muted, tick.IsMidnight ? FontWeight.SemiBold : FontWeight.Normal);
            var clockAt = new Point(Math.Clamp(x - clock.Width / 2, plot.Left, plot.Right - clock.Width), plot.Bottom + 6);
            context.DrawText(clock, clockAt);
            taken.Add(new Rect(clockAt, new Size(clock.Width, clock.Height)));
            if (tick.Bearing is not { } bearing) continue;
            var direction = Label(bearing, FontSize, ink, FontWeight.SemiBold);
            var directionAt = new Point(Math.Clamp(x - direction.Width / 2, plot.Left, plot.Right - direction.Width), clockAt.Y + clock.Height + 1);
            context.DrawText(direction, directionAt);
            taken.Add(new Rect(directionAt, new Size(direction.Width, direction.Height)));
        }
    }

    /// <summary>The Sun's and Moon's events as ticks on the plot's top edge, each named with its time where there is room.</summary>
    private void DrawEvents(DrawingContext context, Rect plot, IBrush muted, DateTimeZone zone, List<Rect> taken)
    {
        var tick = new Pen(muted, 1);
        foreach (var item in _sheet.Events.Events.Where(item => item.Instant >= _sheet.WindowStart && item.Instant <= _sheet.WindowEnd))
        {
            var x = X(item.Instant);
            context.DrawLine(tick, new Point(x, plot.Top), new Point(x, plot.Top + 7));
            var text = Label(Strings.Format("Export_Chart_Event_" + item.Kind + "_Format", HorizonGraph.Clock(item.Instant, zone)), FontSize - 1, muted);
            Place(context, text, new Point(x + 3, plot.Top + 2), plot, taken);
        }
    }

    /// <summary>The target's rises, sets and terrain crossings, named on its line, and its highest point.</summary>
    private void DrawTargetMarks(DrawingContext context, Rect plot, DateTimeZone zone, List<Rect> taken)
    {
        var colour = Brush(Colours.Celestial[Target.Role]);
        var ink = Brush(Colours.PrimaryText);
        foreach (var crossing in Target.Crossings())
        {
            var point = new Point(X(crossing.Time), PitchY(crossing.Pitch));
            context.DrawEllipse(colour, null, point, 3, 3);
            var text = Label(Strings.Format("Export_Chart_" + crossing.Kind + "_Format", HorizonGraph.Clock(crossing.Time, zone)), FontSize - 1, ink);
            Place(context, text, new Point(point.X + 5, point.Y - text.Height - 3), plot, taken);
        }
        if (Target.Culmination is { } top && top.Pitch > MinimumPitch)
        {
            var point = new Point(X(top.Time), PitchY(top.Pitch));
            context.DrawEllipse(colour, new Pen(Brush(Colours.Surface), 1.5), point, 4.5, 4.5);
            var text = Label(Strings.Format("Export_Chart_Highest_Format", top.Pitch, ExportTargetSeries.Compass(top.Bearing),
                HorizonGraph.Clock(top.Time, zone)), FontSize, ink, FontWeight.SemiBold);
            Place(context, text, new Point(point.X - text.Width / 2, point.Y - text.Height - 8), plot, taken);
        }
    }

    private void Place(DrawingContext context, FormattedText text, Point at, Rect plot, List<Rect> taken)
    {
        var bounds = new Rect(Math.Clamp(at.X, plot.Left, plot.Right - text.Width), Math.Clamp(at.Y, plot.Top, plot.Bottom - text.Height),
            text.Width, text.Height);
        for (var attempt = 0; attempt < 6 && taken.Any(other => other.Intersects(bounds)); attempt++)
            bounds = bounds.WithY(Math.Min(bounds.Y + text.Height + 1, plot.Bottom - text.Height));
        if (taken.Any(other => other.Intersects(bounds))) return;
        taken.Add(bounds);
        context.FillRectangle(Brush(Color.FromArgb(200, Colours.Surface.R, Colours.Surface.G, Colours.Surface.B)), bounds.Inflate(2), 3);
        context.DrawText(text, bounds.TopLeft);
    }

    /// <summary>The samples in unbroken runs where <paramref name="included"/> holds.</summary>
    private static IEnumerable<List<ExportTargetSample>> Runs(IReadOnlyList<ExportTargetSample> samples, Func<ExportTargetSample, bool> included)
    {
        var run = new List<ExportTargetSample>();
        foreach (var sample in samples)
        {
            if (included(sample)) { run.Add(sample); continue; }
            if (run.Count > 0) { yield return run; run = []; }
        }
        if (run.Count > 0) yield return run;
    }

    private static void Polyline(DrawingContext context, IPen pen, IReadOnlyList<Point> points)
    {
        if (points.Count < 2) return;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(points[0], false);
            foreach (var point in points.Skip(1)) g.LineTo(point);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private static FormattedText Label(string text, double size, IBrush brush, FontWeight? weight = null) =>
        new(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(Face.FontFamily, FontStyle.Normal, weight ?? FontWeight.Normal), size, brush);

    private static IBrush Brush(Color colour) => new ImmutableSolidColorBrush(colour);
}
