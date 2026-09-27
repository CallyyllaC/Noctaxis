using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Noctaxis.Desktop.Branding;

namespace Noctaxis.Desktop.Controls;

/// <summary>
/// The Noctaxis mark (<see cref="NoctaxisLogo"/>) in the active theme: brackets in
/// <see cref="FrameResourceKey"/> and the star in <see cref="StarResourceKey"/>, exactly those two theme
/// colours and no effects. It scales uniformly to its bounds and switches to the heavier small weight
/// when drawn at taskbar-like pixel sizes.
/// </summary>
public sealed class NoctaxisLogoMark : Control
{
    /// <summary>Theme roles of the two logo colours.</summary>
    public const string FrameResourceKey = "Accent", StarResourceKey = "PrimaryText";

    public static readonly StyledProperty<IBrush?> FrameBrushProperty =
        AvaloniaProperty.Register<NoctaxisLogoMark, IBrush?>(nameof(FrameBrush));
    public static readonly StyledProperty<IBrush?> StarBrushProperty =
        AvaloniaProperty.Register<NoctaxisLogoMark, IBrush?>(nameof(StarBrush));

    public IBrush? FrameBrush { get => GetValue(FrameBrushProperty); set => SetValue(FrameBrushProperty, value); }
    public IBrush? StarBrush { get => GetValue(StarBrushProperty); set => SetValue(StarBrushProperty, value); }

    // Parsed per weight; pixel-snapped weights repeat for each size and display scaling in use.
    private static readonly Dictionary<NoctaxisLogo.Weight, (Geometry Brackets, Geometry Star)> Geometries = [];

    private static (Geometry Brackets, Geometry Star) GeometryFor(NoctaxisLogo.Weight weight)
    {
        if (!Geometries.TryGetValue(weight, out var geometry))
            Geometries[weight] = geometry = (Geometry.Parse(NoctaxisLogo.BracketsPathData(weight)),
                Geometry.Parse(NoctaxisLogo.StarPathData(weight)));
        return geometry;
    }

    static NoctaxisLogoMark() => AffectsRender<NoctaxisLogoMark>(FrameBrushProperty, StarBrushProperty);

    public NoctaxisLogoMark()
    {
        this.Bind(FrameBrushProperty, new DynamicResourceExtension(FrameResourceKey));
        this.Bind(StarBrushProperty, new DynamicResourceExtension(StarResourceKey));
    }

    /// <summary>The weight drawn at the current size and display scaling.</summary>
    internal NoctaxisLogo.Weight CurrentWeight
    {
        get
        {
            var side = Math.Min(Bounds.Width, Bounds.Height);
            var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            return NoctaxisLogo.ForPixelSize(side * scaling);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Square by default; explicit Width/Height win.
        var side = Math.Min(availableSize.Width, availableSize.Height);
        return double.IsInfinity(side) ? new Size(NoctaxisLogo.GridSize / 4, NoctaxisLogo.GridSize / 4) : new Size(side, side);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0) return;
        var weight = CurrentWeight;
        var (brackets, star) = GeometryFor(weight);
        var scale = side / NoctaxisLogo.GridSize;
        var offset = new Vector((Bounds.Width - side) / 2, (Bounds.Height - side) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset)))
        {
            if (FrameBrush is { } frame)
                context.DrawGeometry(null, new Pen(frame, weight.BracketStroke, lineCap: PenLineCap.Round,
                    lineJoin: PenLineJoin.Round), brackets);
            if (StarBrush is { } starBrush)
                context.DrawGeometry(starBrush, null, star);
        }
    }
}
