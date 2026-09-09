using Avalonia;
using Avalonia.Media;
using System.Globalization;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Terrain;

namespace Noctaxis.Desktop.Diagnostics;

internal static class TerrainSampleRenderer
{
        public static void Draw(DrawingContext context, PlanningSnapshot snapshot,
            double bearing, GeoCoordinate observer, double resolution,
            Func<GeoCoordinate, Point?> project, Action<IReadOnlyList<GeoCoordinate>, Pen> drawPath)
        {
            var profile = snapshot.Environment?.HorizonProfile ?? snapshot.Terrain;
            var sample = TerrainProfileDiagnostics.NearestBearingSample(profile, bearing);
            if (sample is null) return;

            var rawSamples = profile.ObserverDiagnostics?.TerrainSample.RawSamples ?? [];
            foreach (var raw in rawSamples)
            {
                var point = project(raw.Coordinate);
                if (point is null) continue;
                var fill = DebugBrush(raw.Status);
                context.DrawEllipse(fill, new Pen(Brushes.Black, 1), point.Value, 4, 4);
                if (resolution <= 12)
                    DrawDebugLabel(context, point.Value, raw.RawElevationMetres, raw.Status);
            }

            var sightline = sample.Value.Sightline ?? [];
            if (sightline.Count == 0) return;
            var stride = Math.Max(1, (int)Math.Ceiling(sightline.Count / 80d));
            for (var index = 0; index < sightline.Count; index += stride)
            {
                var radial = sightline[index];
                var coordinate = Angles.Destination(observer, sample.Value.BearingDegrees, radial.DistanceMetres);
                var point = project(coordinate);
                if (point is null) continue;
                var status = radial.GroundElevationMetres.HasValue
                    ? TerrainSampleStatus.Valid : radial.GroundStatus;
                context.DrawEllipse(DebugBrush(status), null, point.Value, 2.2, 2.2);
                if (resolution <= 4 && index < 32)
                    DrawDebugLabel(context, point.Value,
                        radial.GroundElevationMetres, status);
            }

            if (sample.Value.EffectiveHorizonFeatureDistanceMetres is not double winningDistance) return;
            var winningCoordinate = Angles.Destination(observer, sample.Value.BearingDegrees, winningDistance);
            drawPath([observer, winningCoordinate],
                new Pen(new SolidColorBrush(Color.FromArgb(210, 255, 88, 72)), 1.2, dashStyle: DashStyle.Dash));
            var winningPoint = project(winningCoordinate);
            if (winningPoint is not null)
                context.DrawEllipse(new SolidColorBrush(Color.FromArgb(245, 255, 70, 58)),
                    new Pen(Brushes.White, 1.5), winningPoint.Value, 6, 6);
        }

        private static IBrush DebugBrush(TerrainSampleStatus status) => status switch
        {
            TerrainSampleStatus.Water => new SolidColorBrush(Color.FromArgb(235, 52, 181, 255)),
            TerrainSampleStatus.Valid => new SolidColorBrush(Color.FromArgb(235, 92, 238, 148)),
            TerrainSampleStatus.NoData or TerrainSampleStatus.Error =>
                new SolidColorBrush(Color.FromArgb(245, 255, 75, 75)),
            _ => new SolidColorBrush(Color.FromArgb(230, 255, 183, 73))
        };

        private static void DrawDebugLabel(DrawingContext context, Point point, double? elevation,
            TerrainSampleStatus status)
        {
            var label = elevation.HasValue ? $"{elevation.Value:F1} m" : status.ToString();
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 9, Brushes.White);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(210, 7, 12, 19)), null,
                new Rect(point.X + 5, point.Y - 7, text.Width + 4, text.Height + 2));
            context.DrawText(text, new Point(point.X + 7, point.Y - 6));
        }

}
