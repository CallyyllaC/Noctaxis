using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.Controls;

public sealed class HorizonGraph : ChromeDrawingControl
{
    public static readonly StyledProperty<PlanningSnapshot?> SnapshotProperty =
        AvaloniaProperty.Register<HorizonGraph, PlanningSnapshot?>(nameof(Snapshot));
    public static readonly StyledProperty<CelestialColourSet> CelestialColoursProperty =
        AvaloniaProperty.Register<HorizonGraph, CelestialColourSet>(nameof(CelestialColours), CelestialColourSet.Default);

    public PlanningSnapshot? Snapshot { get => GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
    /// <summary>Theme data colours (<see cref="CelestialThemeTokens"/>), bound to the current theme.</summary>
    public CelestialColourSet CelestialColours { get => GetValue(CelestialColoursProperty); set => SetValue(CelestialColoursProperty, value); }

    public HorizonGraph() => this.Bind(CelestialColoursProperty, new DynamicResourceExtension(CelestialThemeTokens.ColourSet));

    static HorizonGraph() => AffectsRender<HorizonGraph>(SnapshotProperty, CelestialColoursProperty);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(ChromeBackground, new Rect(Bounds.Size));
        if (Snapshot is null || Bounds.Width < 20 || Bounds.Height < 20) return;
        var colours = CelestialColours;
        var plot = new Rect(42, ChromeFontSize + 12, Math.Max(1, Bounds.Width - 58), Math.Max(1, Bounds.Height - ChromeFontSize * 3 - 18));
        double Y(double altitude) => plot.Bottom - Math.Clamp((altitude + 20) / 110, 0, 1) * plot.Height;
        var horizonY = Y(0);
        context.DrawLine(new Pen(ChromeForeground, 1), new Point(plot.Left, horizonY), new Point(plot.Right, horizonY));

        if (Snapshot.Terrain.Samples.Count > 1 && Snapshot.Path.Samples.Count > 1)
        {
            var terrain = new StreamGeometry();
            using var g = terrain.Open();
            g.BeginFigure(new Point(plot.Left, plot.Bottom), true);
            for (var i = 0; i < Snapshot.Path.Samples.Count; i++)
                g.LineTo(new Point(plot.Left + i * plot.Width / (Snapshot.Path.Samples.Count - 1), Y(Snapshot.Terrain.AltitudeAt(Snapshot.Path.Samples[i].Horizontal.AzimuthDegrees))));
            g.LineTo(new Point(plot.Right, plot.Bottom));
            g.EndFigure(true);
            context.DrawGeometry(new SolidColorBrush(colours.TerrainFill), new Pen(new SolidColorBrush(colours.TerrainOutline), 1), terrain);
        }

        var culminations = new List<(CelestialRole Role, Point Point, IBrush Brush, string Label)>();
        var belowHorizon = new SolidColorBrush(colours.BelowHorizon);
        foreach (var objectPlan in Snapshot.EffectiveObjectPlans)
        {
            var objectPath = objectPlan.Path;
            if (objectPath.Samples.Count <= 1) continue;
            var target = objectPlan.Position.Target;
            var primary = target.Id.Equals(Snapshot.Position.Target.Id, StringComparison.OrdinalIgnoreCase);
            var role = CelestialPalette.Role(Snapshot, target);
            var brush = new SolidColorBrush(colours[role]);
            // Primary: thick and solid. Secondary: thinner, in the object's own dash pattern. Below the
            // horizon every path is thinner, dashed and muted, and sits under the horizon line.
            var above = new Pen(brush, primary ? 2.8 : 1.6, dashStyle: primary ? null : CelestialPalette.Dash(role));
            var below = new Pen(belowHorizon, primary ? 1.6 : 1.2, dashStyle: primary ? DashStyle.Dash : CelestialPalette.Dash(role));
            double X(int index) => plot.Left + index * plot.Width / (objectPath.Samples.Count - 1);
            var highest = -1;
            for (var i = 1; i < objectPath.Samples.Count; i++)
            {
                var a = objectPath.Samples[i - 1];
                var b = objectPath.Samples[i];
                context.DrawLine(a.IsAboveHorizon || b.IsAboveHorizon ? above : below,
                    new Point(X(i - 1), Y(a.Horizontal.AltitudeDegrees)), new Point(X(i), Y(b.Horizontal.AltitudeDegrees)));
            }
            for (var i = 0; i < objectPath.Samples.Count; i++)
                if (objectPath.Samples[i].IsAboveHorizon && (highest < 0 || objectPath.Samples[i].Horizontal.AltitudeDegrees > objectPath.Samples[highest].Horizontal.AltitudeDegrees))
                    highest = i;
            if (highest >= 0)
                culminations.Add((role, new Point(X(highest), Y(objectPath.Samples[highest].Horizontal.AltitudeDegrees)), brush, CelestialPalette.ShortLabel(target)));
        }
        DrawIdentityMarkers(context, plot, culminations);
        if (Snapshot.Path.Samples.Count > 1)
        {
            DrawMarker(context, Snapshot.Path.Events.Rise, "R", plot);
            DrawMarker(context, Snapshot.Path.Events.Transit, "T", plot);
            DrawMarker(context, Snapshot.Path.Events.Set, "S", plot);
            DrawMarker(context, Snapshot.Session.Instant, "NOW", plot);
        }

        context.DrawText(ChromeText("ALTITUDE  +90°"), new Point(5, 2));
        var start = ChromeText("00:00"); var middle = ChromeText("LOCAL TIME"); var end = ChromeText("24:00");
        var labelY = plot.Bottom + 6;
        context.DrawText(start, new Point(plot.Left, labelY));
        context.DrawText(middle, new Point(plot.Center.X - middle.Width / 2, labelY));
        context.DrawText(end, new Point(plot.Right - end.Width, labelY));
    }

    /// <summary>Each path's marker at its highest point with a compact name, so identity never rests on colour.</summary>
    private void DrawIdentityMarkers(DrawingContext context, Rect plot,
        IReadOnlyList<(CelestialRole Role, Point Point, IBrush Brush, string Label)> culminations)
    {
        var placed = new List<Rect>();
        foreach (var (role, point, brush, label) in culminations)
        {
            CelestialPalette.DrawMarker(context, role, point, 8, brush, ChromeBackground);
            var text = ChromeText(label);
            var x = point.X + 7 + text.Width > plot.Right ? point.X - 7 - text.Width : point.X + 7;
            var bounds = new Rect(x, point.Y - text.Height - 1, text.Width, text.Height);
            // Nudge overlapping labels down rather than stacking them on top of each other.
            for (var attempt = 0; attempt < 4 && placed.Any(other => other.Intersects(bounds)); attempt++)
                bounds = bounds.Translate(new Vector(0, text.Height));
            bounds = bounds.WithY(Math.Clamp(bounds.Y, 0, Math.Max(0, Bounds.Height - text.Height)));
            placed.Add(bounds);
            context.FillRectangle(ChromeBackground, bounds.Inflate(1), 2);
            context.DrawText(text, bounds.TopLeft);
        }
    }

    private void DrawMarker(DrawingContext context, NodaTime.Instant? instant, string label, Rect plot, Color? colour = null)
    {
        if (instant is null || Snapshot is null) return;
        var duration = Snapshot.Path.Samples[^1].Instant - Snapshot.Path.Samples[0].Instant;
        if (duration <= NodaTime.Duration.Zero) return;
        var fraction = (instant.Value - Snapshot.Path.Samples[0].Instant).TotalSeconds / duration.TotalSeconds;
        var x = plot.Left + fraction * plot.Width;
        var brush = colour is { } c ? new SolidColorBrush(c) : ChromeForeground;
        context.DrawLine(new Pen(brush, 1, dashStyle: DashStyle.Dot), new Point(x, plot.Top), new Point(x, plot.Bottom));
        var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, ChromeFontSize, brush);
        context.DrawText(text, new Point(x + 3, plot.Top + 3));
    }
}
