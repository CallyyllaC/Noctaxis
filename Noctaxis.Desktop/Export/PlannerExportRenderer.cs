using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Noctaxis.Core.Localisation;
using Noctaxis.Desktop.Controls;

namespace Noctaxis.Desktop.Export;

/// <summary>
/// The one renderer of a plan image. Save PNG and Copy PNG both deliver its output; neither draws anything of
/// its own. It returns the image's pixels as a PNG with no plan in it: the plan is added by
/// <see cref="Noctaxis.Core.Export.NoctaxisPlanImage.Embed"/>.
/// </summary>
public interface IPlannerExportRenderer
{
    byte[] RenderPng(PlannerExportSnapshot snapshot);
}

/// <summary>
/// Draws a planning sheet for one target from a <see cref="PlannerExportSnapshot"/>: a header (place; target and
/// planning window; equipment), the clean map beside a compact weather panel, the full-width chart of the target's
/// direction over time (<see cref="ExportDirectionChart"/>) and a footer of credits. The sheet is composed as its own detached visual tree and rendered off screen — never a capture of
/// the window — in the snapshot's colours, so the same snapshot always draws the same pixels.
/// </summary>
public sealed class PlannerExportRenderer : IPlannerExportRenderer
{
    public const int Width = 1600, Height = 1100;
    /// <summary>The map panel's size in sheet pixels, which the clean map is composed at (it fills the panel).</summary>
    public const int MapWidth = 1140, MapHeight = 470;
    /// <summary>The chart's height: the sheet's main section, below the map and weather.</summary>
    public const int ChartHeight = 420;
    private const double Margin = 32, TitleSize = 28, BodySize = 15, CaptionSize = 12;
    private static readonly FontFamily Font = new("Inter");

    public byte[] RenderPng(PlannerExportSnapshot snapshot)
    {
        var sheet = Compose(snapshot);
        sheet.Measure(new Size(Width, Height));
        sheet.Arrange(new Rect(0, 0, Width, Height));
        using var bitmap = new RenderTargetBitmap(new PixelSize(Width, Height), new Vector(96, 96));
        bitmap.Render(sheet);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    /// <summary>The sheet as a detached visual tree, named by region: ExportHeader, ExportMap, ExportWeather, ExportTimeline, ExportFooter.</summary>
    internal static Control Compose(PlannerExportSnapshot s)
    {
        var t = s.Theme;
        var surface = Brush(t.Surface);
        var grid = new Grid
        {
            RowDefinitions = new($"Auto,*,{ChartHeight},Auto"),
            RowSpacing = 18,
            Margin = new Thickness(Margin)
        };

        var header = new StackPanel { Name = "ExportHeader", Spacing = 4 };
        header.Children.Add(Text(s.LocationName, TitleSize, t.PrimaryText, FontWeight.SemiBold));
        header.Children.Add(Text(s.Subject, BodySize + 1, t.PrimaryText));
        header.Children.Add(Text(s.Equipment, BodySize, t.SecondaryText));
        grid.Children.Add(header);

        var body = new Grid { ColumnDefinitions = new("*,380"), ColumnSpacing = 18 };
        Grid.SetRow(body, 1);
        body.Children.Add(MapPanel(s, surface));
        var weather = WeatherPanel(s, surface);
        Grid.SetColumn(weather, 1);
        body.Children.Add(weather);
        grid.Children.Add(body);

        // The target's direction over the window: pitch and bearing against time, with darkness, terrain and the
        // Sun's and Moon's events. The export's own chart; the Planner's Timeline is a different view.
        var chart = new ExportDirectionChart(s) { Name = "ExportTimeline", Margin = new Thickness(8, 10, 8, 8) };
        var timelineFrame = Panel(chart, surface, t.Border, "ExportTimelineFrame");
        Grid.SetRow(timelineFrame, 2);
        grid.Children.Add(timelineFrame);

        var footer = Text(s.Footer, CaptionSize, t.SecondaryText);
        footer.Name = "ExportFooter";
        Grid.SetRow(footer, 3);
        grid.Children.Add(footer);

        return new Border { Width = Width, Height = Height, Background = Brush(t.Background), Child = grid };
    }

    private static Control MapPanel(PlannerExportSnapshot s, IBrush surface)
    {
        var t = s.Theme;
        var map = new Grid { Name = "ExportMap", ClipToBounds = true, Background = Brush(t.MapSurface) };
        if (s.Map is { } image)
        {
            using var stream = new MemoryStream(image.Png, writable: false);
            map.Children.Add(new Image { Source = new Bitmap(stream), Stretch = Stretch.UniformToFill });
        }
        else
        {
            // Below the location's ring, which stays at the centre.
            var unavailable = Text(Strings.Get("Export_Sheet_MapUnavailable"), BodySize, t.SecondaryText, centre: true);
            unavailable.Margin = new Thickness(0, 64, 0, 0);
            map.Children.Add(unavailable);
        }
        // The planning location: a ring at the centre, which is where the map is centred.
        map.Children.Add(new Ellipse
        {
            Width = 18, Height = 18, Stroke = Brush(t.Accent), StrokeThickness = 3,
            Fill = Brush(Color.FromArgb(90, t.Accent.R, t.Accent.G, t.Accent.B)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        map.Children.Add(Chip(s.Coordinates, BodySize, t, HorizontalAlignment.Left, "ExportCoordinates"));
        if (s.Map is { } attributed)
            map.Children.Add(Chip(attributed.Attribution, CaptionSize, t, HorizontalAlignment.Right, "ExportMapAttribution"));
        return Panel(map, surface, t.Border, "ExportMapFrame");
    }

    private static Control WeatherPanel(PlannerExportSnapshot s, IBrush surface)
    {
        var t = s.Theme;
        var panel = new StackPanel { Name = "ExportWeather", Spacing = 8, Margin = new Thickness(18, 16) };
        panel.Children.Add(Text(Strings.Get("Export_Sheet_Weather_Heading"), BodySize + 2, t.PrimaryText, FontWeight.SemiBold));
        if (s.Weather.Count > 0)
        {
            var table = new Grid { ColumnDefinitions = new("Auto,*,*,*,*"), ColumnSpacing = 12, RowSpacing = 7 };
            string[] headings = [Strings.Get("Export_Sheet_Weather_Time"), Strings.Get("Export_Sheet_Weather_Cloud"),
                Strings.Get("Export_Sheet_Weather_Rain"), Strings.Get("Export_Sheet_Weather_Wind"), Strings.Get("Export_Sheet_Weather_Temperature")];
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var column = 0; column < headings.Length; column++) Cell(table, headings[column], 0, column, CaptionSize, t.SecondaryText);
            for (var row = 0; row < s.Weather.Count; row++)
            {
                var hour = s.Weather[row];
                table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                string[] values = [hour.Clock, hour.Cloud, hour.Rain, hour.Wind, hour.Temperature];
                for (var column = 0; column < values.Length; column++)
                    Cell(table, values[column], row + 1, column, BodySize, column == 0 ? t.SecondaryText : t.PrimaryText);
            }
            panel.Children.Add(table);
        }
        if (s.WeatherNote is { Length: > 0 } note) panel.Children.Add(Text(note, CaptionSize, t.SecondaryText));
        return Panel(panel, surface, t.Border, "ExportWeatherFrame");
    }

    private static void Cell(Grid table, string value, int row, int column, double size, Color colour)
    {
        var cell = Text(value, size, colour);
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        table.Children.Add(cell);
    }

    private static Border Panel(Control child, IBrush surface, Color border, string name) => new()
    {
        Name = name, Background = surface, BorderBrush = Brush(border), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = child
    };

    private static Border Chip(string text, double size, ExportTheme t, HorizontalAlignment side, string name) => new()
    {
        Name = name, HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10),
        Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(4),
        Background = Brush(Color.FromArgb(220, t.Surface.R, t.Surface.G, t.Surface.B)),
        Child = Text(text, size, t.PrimaryText)
    };

    private static TextBlock Text(string text, double size, Color colour, FontWeight? weight = null, bool centre = false) => new()
    {
        Text = text, FontFamily = Font, FontSize = size, Foreground = Brush(colour),
        FontWeight = weight ?? FontWeight.Normal, TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = centre ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
        VerticalAlignment = centre ? VerticalAlignment.Center : VerticalAlignment.Top
    };

    private static IBrush Brush(Color colour) => new Avalonia.Media.Immutable.ImmutableSolidColorBrush(colour);
}
