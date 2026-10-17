using Avalonia;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.Controls;

/// <summary>
/// Sizes the Planner map's two fixed instruments together: the Picture-in-Picture map at the top-left and the
/// Camera frame's row at the bottom-left. Each is the user's share of the map's width, kept between its own
/// minimum and a third of the map, and the two are resolved as a pair so the PiP's bottom never comes within
/// <see cref="Gap"/> of the row's top. This is arithmetic only; the window applies the result.
/// </summary>
/// <remarks>
/// When the map is too short for both preferences they shrink together, in proportion, until one reaches its
/// minimum, and then the other alone. When even their minimums do not fit, the Camera frame (which aims the
/// camera) stays and the PiP is set aside until there is room again. The preferences themselves are never
/// changed here: a resolved size is for now, and the next larger map restores toward the preference.
/// </remarks>
public static class MapInstrumentLayout
{
    /// <summary>The instruments' distance from the map's edges, and the least room between them.</summary>
    public const double Margin = 12, Gap = 8;

    /// <summary>
    /// The smallest each is drawn, measured from the instruments themselves: the PiP keeps its two 32 px
    /// buttons and its resize grip clear of each other with map above them; the Camera frame card (192 px of content
    /// inside its padding) keeps its bearing and pitch (182 px) and its horizon and obstruction (187 px at their
    /// widest ordinary values) each on one line, unclipped. In the default window (1440 × 900, the Timeline docked
    /// below the map) the map is about 466 px tall and the Camera frame card about 303, which leaves the PiP
    /// about 131 px: so its minimum is 120, not more, for it to be shown there without overlapping. The Camera frame's
    /// figures are for the font it was measured with; where the system's font sets the readings wider, the window
    /// passes the width they need instead (the <c>minimumCameraFrameWidth</c> parameters below).
    /// </summary>
    public const double MinimumPipSide = 120, MinimumCameraFrameWidth = 210;

    /// <summary>Neither is ever drawn wider than this share of the map: the map stays the workspace.</summary>
    public const double MaximumShare = 1 / 3d;

    /// <param name="PipSide">The PiP's side, when it is shown.</param>
    /// <param name="PipShown">False only when the map is too short for the PiP beside the Camera frame.</param>
    /// <param name="CameraFrameWidth">The Camera frame card's width.</param>
    public readonly record struct Result(double PipSide, bool PipShown, double CameraFrameWidth);

    /// <summary>The size each would be by preference alone on a map this wide, before the two are fitted together.</summary>
    /// <param name="minimumCameraFrameWidth">The Camera frame's least width, at least <see cref="MinimumCameraFrameWidth"/>.</param>
    public static (double PipSide, double CameraFrameWidth) Preferred(double mapWidth, PlannerOverlaySizes preferred,
        double minimumCameraFrameWidth = MinimumCameraFrameWidth)
    {
        var sizes = preferred.Normalised();
        return (Bound(sizes.PipWidthFraction * mapWidth, MinimumPipSide, mapWidth),
            Bound(sizes.CameraFrameWidthFraction * mapWidth, minimumCameraFrameWidth, mapWidth));
    }

    /// <summary>A width as the share of the map it is, for saving as a preference, within the drawable bounds.</summary>
    public static double FractionOf(double width, double minimum, double mapWidth) =>
        mapWidth > 0 ? Bound(width, minimum, mapWidth) / mapWidth : 0;

    /// <param name="map">The map's own viewport, after the docked panels have taken their room.</param>
    /// <param name="preferred">The user's preferences.</param>
    /// <param name="pipEnabled">Whether the PiP is on at all.</param>
    /// <param name="rowHeight">The bottom-left row's height for a Camera frame card of a given width.</param>
    /// <param name="minimumCameraFrameWidth">The Camera frame's least width, at least <see cref="MinimumCameraFrameWidth"/>.</param>
    public static Result Resolve(Size map, PlannerOverlaySizes preferred, bool pipEnabled, Func<double, double> rowHeight,
        double minimumCameraFrameWidth = MinimumCameraFrameWidth)
    {
        ArgumentNullException.ThrowIfNull(rowHeight);
        var minimumFrame = Math.Max(MinimumCameraFrameWidth, minimumCameraFrameWidth);
        var (pip, frame) = Preferred(map.Width, preferred, minimumFrame);
        var height = map.Height - 2 * Margin;
        if (map.Width <= 0 || map.Height <= 0) return new(pip, pipEnabled, frame);
        if (!pipEnabled) return new(0, false, FitAlone(frame, minimumFrame, height, rowHeight));
        bool Fits(double side, double width) => side + Gap + rowHeight(width) <= height + 1e-6;
        if (Fits(pip, frame)) return new(pip, true, frame);
        if (Fits(MinimumPipSide, minimumFrame))
        {
            // Shrink both in proportion; each stops at its minimum.
            double low = 0, high = 1;
            for (var i = 0; i < 40; i++)
            {
                var scale = (low + high) / 2;
                if (Fits(Math.Max(MinimumPipSide, pip * scale), Math.Max(minimumFrame, frame * scale))) low = scale;
                else high = scale;
            }
            return new(Math.Max(MinimumPipSide, pip * low), true, Math.Max(minimumFrame, frame * low));
        }
        return new(0, false, FitAlone(frame, minimumFrame, height, rowHeight));
    }

    // The Camera frame alone: as preferred where it fits, else as wide as fits, never below its minimum.
    private static double FitAlone(double frame, double minimum, double height, Func<double, double> rowHeight)
    {
        if (rowHeight(frame) <= height + 1e-6 || rowHeight(minimum) > height) return
            rowHeight(frame) <= height + 1e-6 ? frame : minimum;
        double low = minimum, high = frame;
        for (var i = 0; i < 40; i++)
        {
            var width = (low + high) / 2;
            if (rowHeight(width) <= height + 1e-6) low = width;
            else high = width;
        }
        return low;
    }

    private static double Bound(double width, double minimum, double mapWidth) =>
        Math.Clamp(double.IsFinite(width) ? width : minimum, minimum, Math.Max(minimum, mapWidth * MaximumShare));
}
