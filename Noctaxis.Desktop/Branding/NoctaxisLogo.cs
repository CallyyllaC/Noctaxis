using System.Globalization;

namespace Noctaxis.Desktop.Branding;

/// <summary>
/// The Noctaxis mark: four rounded viewfinder brackets around a centred four-point star. This is the
/// single definition of its geometry. The in-app mark (<see cref="Controls.NoctaxisLogoMark"/>), the
/// Windows icon (<see cref="NoctaxisIcon"/>) and the SVG asset are all drawn from it.
/// </summary>
/// <remarks>
/// Geometry is on a <see cref="GridSize"/> × <see cref="GridSize"/> design grid with a transparent
/// background. The brackets are open centre-line paths stroked with round caps and joins; the star is a
/// filled path. The star is longer across than it is tall, like the reference concept.
/// Small renderings use <see cref="Small"/>: heavier brackets and a fuller star, so strokes and the gaps
/// between bracket and star survive at taskbar sizes rather than fading out.
/// </remarks>
public static class NoctaxisLogo
{
    public const double GridSize = 64;
    /// <summary>At or below this many device pixels across, <see cref="Small"/> is used.</summary>
    public const double SmallVariantMaximumPixels = 24;

    /// <summary>Proportions of one rendering weight, in design-grid units.</summary>
    /// <param name="FrameInset">Distance from the grid edge to the brackets' outer edges.</param>
    /// <param name="BracketStroke">Bracket stroke width.</param>
    /// <param name="BracketArm">Bracket arm length along its centre line, from the frame corner.</param>
    /// <param name="BracketRadius">Radius of the bracket's corner, on its centre line.</param>
    /// <param name="StarHalfWidth">Distance from the centre to the left and right star tips.</param>
    /// <param name="StarHalfHeight">Distance from the centre to the top and bottom star tips.</param>
    /// <param name="StarWaist">
    /// Offset of the point each concave side is drawn towards, along the diagonals from the centre;
    /// larger gives a fuller star body.
    /// </param>
    public sealed record Weight(string Name, double FrameInset, double BracketStroke, double BracketArm,
        double BracketRadius, double StarHalfWidth, double StarHalfHeight, double StarWaist);

    public static Weight Regular { get; } = new("regular", FrameInset: 4, BracketStroke: 4, BracketArm: 15,
        BracketRadius: 2, StarHalfWidth: 27, StarHalfHeight: 25, StarWaist: 2.6);

    /// <summary>Heavier brackets and a fuller star for taskbar-sized renderings.</summary>
    public static Weight Small { get; } = new("small", FrameInset: 4, BracketStroke: 6, BracketArm: 14,
        BracketRadius: 2.5, StarHalfWidth: 26, StarHalfHeight: 24, StarWaist: 5.6);

    /// <summary>
    /// The weight for a rendering <paramref name="pixels"/> device pixels across. Below the design size the
    /// bracket inset and stroke are snapped to whole device pixels, so bracket edges fall on pixel boundaries
    /// and stay crisp rather than blurring across two pixels.
    /// </summary>
    public static Weight ForPixelSize(double pixels)
    {
        var weight = pixels <= SmallVariantMaximumPixels ? Small : Regular;
        if (pixels <= 0 || pixels >= GridSize) return weight;
        var scale = pixels / GridSize;
        var stroke = Math.Max(1, Math.Round(weight.BracketStroke * scale));
        var inset = Math.Max(1, Math.Round(weight.FrameInset * scale));
        return weight with { BracketStroke = stroke / scale, FrameInset = inset / scale };
    }

    /// <summary>The four brackets as open centre-line subpaths (SVG path syntax), to be stroked.</summary>
    public static string BracketsPathData(Weight weight)
    {
        var near = weight.FrameInset + weight.BracketStroke / 2;
        var far = GridSize - near;
        var arm = weight.BracketArm;
        var r = weight.BracketRadius;
        return string.Join(' ',
            // Top-left, top-right, bottom-right, bottom-left; each runs from one arm end round the corner.
            $"M{F(near)},{F(near + arm)} V{F(near + r)} A{F(r)},{F(r)} 0 0 1 {F(near + r)},{F(near)} H{F(near + arm)}",
            $"M{F(far - arm)},{F(near)} H{F(far - r)} A{F(r)},{F(r)} 0 0 1 {F(far)},{F(near + r)} V{F(near + arm)}",
            $"M{F(far)},{F(far - arm)} V{F(far - r)} A{F(r)},{F(r)} 0 0 1 {F(far - r)},{F(far)} H{F(far - arm)}",
            $"M{F(near + arm)},{F(far)} H{F(near + r)} A{F(r)},{F(r)} 0 0 1 {F(near)},{F(far - r)} V{F(far - arm)}");
    }

    /// <summary>The star as one closed, filled path (SVG path syntax) with concave sides.</summary>
    public static string StarPathData(Weight weight)
    {
        const double c = GridSize / 2;
        double w = weight.StarHalfWidth, h = weight.StarHalfHeight, k = weight.StarWaist;
        // Tips clockwise from the top. Each side is a cubic whose control points both sit on the waist
        // point, drawing the side deep towards the centre so the arms taper to fine points.
        (double X, double Y)[] tips = [(c, c - h), (c + w, c), (c, c + h), (c - w, c)];
        (double X, double Y)[] waists = [(c + k, c - k), (c + k, c + k), (c - k, c + k), (c - k, c - k)];
        var path = $"M{F(tips[0].X)},{F(tips[0].Y)}";
        for (var i = 0; i < 4; i++)
        {
            var (toX, toY) = tips[(i + 1) % 4];
            var (waistX, waistY) = waists[i];
            path += $" C{F(waistX)},{F(waistY)} {F(waistX)},{F(waistY)} {F(toX)},{F(toY)}";
        }
        return path + " Z";
    }

    /// <summary>Fixed brand colours of the Windows icon (theme-independent), as <c>#RRGGBB</c>.</summary>
    /// <remarks>
    /// Two tones of the Noctaxis violet. Both are mid-luminance, so the transparent icon keeps its shape on
    /// light and dark taskbars alike; the frame is the stronger tone and the star the lighter one.
    /// </remarks>
    public const string BrandFrame = "#7B61FF", BrandStar = "#9A88FF";

    /// <summary>The mark as a standalone SVG document in the fixed brand colours.</summary>
    public static string ToSvg(Weight weight, string frame = BrandFrame, string star = BrandStar) =>
        $"""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {F(GridSize)} {F(GridSize)}" width="{F(GridSize)}" height="{F(GridSize)}">
          <title>Noctaxis</title>
          <path d="{BracketsPathData(weight)}" fill="none" stroke="{frame}" stroke-width="{F(weight.BracketStroke)}" stroke-linecap="round" stroke-linejoin="round"/>
          <path d="{StarPathData(weight)}" fill="{star}"/>
        </svg>

        """;

    private static string F(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
}
