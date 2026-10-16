using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.LogicalTree;

namespace Noctaxis.Desktop.Themes;

/// <summary>
/// The one way a custom control takes a theme resource as a property's default. The theme only supplies the
/// default: a value set on the control (in markup or code) and any style that sets the property win over it,
/// and a theme, appearance, colour-vision or text-size change moves the default without reasserting it over
/// them. A control that sets nothing follows every theme change live.
/// </summary>
/// <remarks>
/// Call it from the control's constructor, before styles apply: the default is bound at style priority, below
/// local values, and a style that applies afterwards outranks it. Bound at local priority instead, as
/// <c>Bind(property, new DynamicResourceExtension(key))</c> does, the next theme change pushed the theme value
/// back over an explicit one; that hid the Camera frame's picture under an opaque surface. Like a dynamic
/// resource, the value is looked up once the control is in the tree, so a control is not themed twice on its
/// way in (the map restyles its basemap, and drops tiles already fetched, on every change).
/// </remarks>
public static class ThemeDefault
{
    public static IDisposable BindThemeDefault(this StyledElement element, AvaloniaProperty property, object key) =>
        element.Bind(property, new AttachedResource(element, key), BindingPriority.Style);

    /// <summary>The resource's value from when the element joins the logical tree, and every change after it.</summary>
    private sealed class AttachedResource(StyledElement element, object key) : IObservable<object?>
    {
        public IDisposable Subscribe(IObserver<object?> observer)
        {
            IDisposable? resource = null;
            void Attach() => resource ??= element.GetResourceObservable(key).Subscribe(observer);
            void Attached(object? sender, LogicalTreeAttachmentEventArgs e) => Attach();
            if (((ILogical)element).IsAttachedToLogicalTree) Attach();
            else element.AttachedToLogicalTree += Attached;
            return new Subscription(() =>
            {
                element.AttachedToLogicalTree -= Attached;
                resource?.Dispose();
            });
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
