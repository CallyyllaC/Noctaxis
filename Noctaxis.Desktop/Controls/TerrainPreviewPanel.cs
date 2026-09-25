using Avalonia;
using Avalonia.Controls;

namespace Noctaxis.Desktop.Controls;

/// <summary>Reflows fixed raster surfaces with scalable chrome when a vertical stack would not fit.</summary>
public sealed class TerrainPreviewPanel : Panel
{
    private bool _sideBySide;
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(new Size(208, double.PositiveInfinity));
        var stackedHeight = Children.Sum(c => c.DesiredSize.Height) + 8 * Math.Max(0, Children.Count - 1);
        _sideBySide = Children.Count == 2 && availableSize.Width >= 428 && stackedHeight > availableSize.Height;
        return _sideBySide ? new Size(428, Children.Max(c => c.DesiredSize.Height)) : new Size(208, stackedHeight);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double offset = 0;
        foreach (var child in Children)
        {
            child.Arrange(new Rect(_sideBySide ? offset : 0, _sideBySide ? 0 : offset, 208, child.DesiredSize.Height));
            offset += _sideBySide ? 220 : child.DesiredSize.Height + 8;
        }
        return finalSize;
    }
}
