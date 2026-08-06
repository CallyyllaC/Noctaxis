using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.Controls;

public sealed class HorizonGraph : Control
{
    public static readonly StyledProperty<PlanningSnapshot?> SnapshotProperty =
        AvaloniaProperty.Register<HorizonGraph, PlanningSnapshot?>(nameof(Snapshot));

    public PlanningSnapshot? Snapshot { get => GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }

    static HorizonGraph() => AffectsRender<HorizonGraph>(SnapshotProperty);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0F151F")), Bounds);
        if (Snapshot is null || Bounds.Width < 20 || Bounds.Height < 20) return;
        var plot = new Rect(42, 12, Math.Max(1, Bounds.Width - 58), Math.Max(1, Bounds.Height - 38));
        double Y(double altitude) => plot.Bottom - Math.Clamp((altitude + 20) / 110, 0, 1) * plot.Height;
        var horizonY = Y(0);
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#4A5568")), 1), new Point(plot.Left, horizonY), new Point(plot.Right, horizonY));

        if (Snapshot.Terrain.Samples.Count > 1 && Snapshot.Path.Samples.Count > 1)
        {
            var terrain = new StreamGeometry();
            using var g = terrain.Open();
            g.BeginFigure(new Point(plot.Left, plot.Bottom), true);
            for (var i = 0; i < Snapshot.Path.Samples.Count; i++)
                g.LineTo(new Point(plot.Left + i * plot.Width / (Snapshot.Path.Samples.Count - 1), Y(Snapshot.Terrain.AltitudeAt(Snapshot.Path.Samples[i].Horizontal.AzimuthDegrees))));
            g.LineTo(new Point(plot.Right, plot.Bottom));
            g.EndFigure(true);
            context.DrawGeometry(new SolidColorBrush(Color.Parse("#2A342E")), new Pen(new SolidColorBrush(Color.Parse("#657563")), 1), terrain);
        }

        foreach (var objectPlan in Snapshot.EffectiveObjectPlans)
        {
            var objectPath = objectPlan.Path;
            if (objectPath.Samples.Count <= 1) continue;
            var primary = objectPlan.Position.Target.Id.Equals(Snapshot.Position.Target.Id, StringComparison.OrdinalIgnoreCase);
            for (var i = 1; i < objectPath.Samples.Count; i++)
            {
                var a = objectPath.Samples[i - 1];
                var b = objectPath.Samples[i];
                var colour = a.IsAboveHorizon || b.IsAboveHorizon
                    ? CelestialPalette.Colour(objectPlan.Position.Target, CelestialPalette.OrderFor(Snapshot, objectPlan.Position.Target))
                    : Color.Parse("#596273");
                context.DrawLine(new Pen(new SolidColorBrush(colour), primary ? 2.8 : 1.4,
                        dashStyle: !primary || (!a.IsAboveHorizon && !b.IsAboveHorizon) ? DashStyle.Dash : null),
                    new Point(plot.Left + (i - 1) * plot.Width / (objectPath.Samples.Count - 1), Y(a.Horizontal.AltitudeDegrees)),
                    new Point(plot.Left + i * plot.Width / (objectPath.Samples.Count - 1), Y(b.Horizontal.AltitudeDegrees)));
            }
        }
        if (Snapshot.Path.Samples.Count > 1)
        {
            DrawMarker(context, Snapshot.Path.Events.Rise, "R", plot);
            DrawMarker(context, Snapshot.Path.Events.Transit, "T", plot);
            DrawMarker(context, Snapshot.Path.Events.Set, "S", plot);
            DrawMarker(context, Snapshot.Session.Instant, "NOW", plot, Color.Parse("#FFFFFF"));
        }

        var label = new FormattedText("ALTITUDE  +90°", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10, new SolidColorBrush(Color.Parse("#8390A3")));
        context.DrawText(label, new Point(5, 10));
        var axis = new FormattedText("00:00                     LOCAL TIME                     24:00", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10, new SolidColorBrush(Color.Parse("#8390A3")));
        context.DrawText(axis, new Point(plot.Left, plot.Bottom + 8));
    }

    private void DrawMarker(DrawingContext context, NodaTime.Instant? instant, string label, Rect plot, Color? colour = null)
    {
        if (instant is null || Snapshot is null) return;
        var duration = Snapshot.Path.Samples[^1].Instant - Snapshot.Path.Samples[0].Instant;
        if (duration <= NodaTime.Duration.Zero) return;
        var fraction = (instant.Value - Snapshot.Path.Samples[0].Instant).TotalSeconds / duration.TotalSeconds;
        var x = plot.Left + fraction * plot.Width;
        var brush = new SolidColorBrush(colour ?? Color.Parse("#AAB4C3"));
        context.DrawLine(new Pen(brush, 1, dashStyle: DashStyle.Dot), new Point(x, plot.Top), new Point(x, plot.Bottom));
        var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 9, brush);
        context.DrawText(text, new Point(x + 3, plot.Top + 3));
    }
}
