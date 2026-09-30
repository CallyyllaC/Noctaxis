using System.Runtime.CompilerServices;
using Avalonia.Markup.Xaml;
using Noctaxis.Core.Localisation;

namespace Noctaxis.Desktop.Localisation;

/// <summary>
/// XAML access to localised text: <c>Text="{loc:Text Settings_Equipment_Title}"</c>. It resolves
/// through <see cref="Strings"/>, the same lookup C# uses, when the XAML is loaded. The language
/// cannot change while the application runs, which v1 (English only) does not need.
/// </summary>
public sealed class TextExtension : MarkupExtension
{
    public TextExtension() { }
    public TextExtension(string key) => Key = key;

    /// <summary>The resource key, <c>Domain_Rest_Of_Name</c>.</summary>
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}

internal static class DesktopResources
{
    // Registered when the assembly loads, so design-time previews and tests need no application start.
    [ModuleInitializer]
    internal static void Register() => Strings.Register(typeof(DesktopResources).Assembly, "Noctaxis.Desktop.Resources");
}
