using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Noctaxis.Core.Tests")]
[assembly: InternalsVisibleTo("Noctaxis.Desktop.Tests")]

namespace Noctaxis.Core.Localisation;

/// <summary>
/// The one lookup for user-facing text, used by C# directly and by XAML through the Desktop's
/// <c>{loc:Text Key}</c> markup extension. Text lives in <c>.resx</c> resources; English (en-GB) is
/// the neutral language and the only one shipped. See docs/localisation.md.
/// </summary>
/// <remarks>
/// <para>A key is <c>Domain_Rest_Of_Name</c>. The domain is the name of the resource file that holds
/// it (<c>Settings_Equipment_Title</c> is in <c>Settings.resx</c>), so a key names exactly one
/// place and two files can never both define it.</para>
/// <para>Text is looked up for <see cref="CultureInfo.CurrentUICulture"/> and falls back to English
/// through the standard resource fallback. <see cref="Format(string, object?[])"/> formats its
/// values with <see cref="CultureInfo.CurrentCulture"/>. Neither is for machine-readable text:
/// persistence, URLs, cache keys and identifiers stay invariant and never pass through here.</para>
/// <para>A key that does not exist at all is a defect. It is shown as <c>⟦Key⟧</c>, never as a
/// blank, reported through <see cref="MissingKey"/> and traced once; tests fail on any key the
/// source refers to that does not resolve.</para>
/// </remarks>
public static class Strings
{
    private static readonly ConcurrentDictionary<string, ResourceManager> Domains = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, bool> Reported = new(StringComparer.Ordinal);

    static Strings() => Register(typeof(Strings).Assembly, "Noctaxis.Core.Resources");

    /// <summary>Raised for every lookup of a key that does not exist.</summary>
    public static event Action<string>? MissingKey;

    /// <summary>
    /// Test-only: replaces every resolved text, for example to lengthen it and expose layouts that
    /// assume English lengths. Never set in the application.
    /// </summary>
    internal static Func<string, string, string>? Transform
    {
        get => ScopedTransform.Value;
        set => ScopedTransform.Value = value;
    }

    // Scoped to the caller's execution context, so one test's expansion never reaches tests running beside it.
    private static readonly AsyncLocal<Func<string, string, string>?> ScopedTransform = new();

    /// <summary>The registered domains, each the name of a resource file.</summary>
    public static IReadOnlyCollection<string> RegisteredDomains => Domains.Keys.ToArray();

    /// <summary>
    /// Registers every resource file embedded in <paramref name="assembly"/> under
    /// <paramref name="resourceNamespace"/> as a domain named after the file.
    /// </summary>
    public static void Register(Assembly assembly, string resourceNamespace)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var prefix = resourceNamespace + ".";
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".resources", StringComparison.Ordinal)) continue;
            var baseName = name[..^".resources".Length];
            var domain = baseName[prefix.Length..];
            if (domain.Contains('.')) continue;
            var manager = new ResourceManager(baseName, assembly);
            if (!Domains.TryAdd(domain, manager) && Domains[domain].BaseName != baseName)
                throw new InvalidOperationException($"Localisation domain '{domain}' is defined by more than one resource file.");
        }
    }

    /// <summary>The text for <paramref name="key"/> in the current UI culture.</summary>
    public static string Get(string key)
    {
        if (TryGet(key, out var text)) return text;
        if (Reported.TryAdd(key, true)) Trace.TraceWarning($"Missing localisation key '{key}'.");
        MissingKey?.Invoke(key);
        return $"⟦{key}⟧";
    }

    /// <summary>
    /// The text for <paramref name="key"/> with its placeholders filled, values formatted for the
    /// current culture. The text is one whole sentence with <c>{0}</c> placeholders, so a translation
    /// can reorder them.
    /// </summary>
    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    public static bool TryGet(string key, out string text)
    {
        text = string.Empty;
        if (string.IsNullOrEmpty(key)) return false;
        var separator = key.IndexOf('_');
        if (separator <= 0 || !Domains.TryGetValue(key[..separator], out var manager)) return false;
        string? value;
        try { value = manager.GetString(key, CultureInfo.CurrentUICulture); }
        catch (MissingManifestResourceException) { return false; }
        if (value is null) return false;
        text = Transform is { } transform ? transform(key, value) : value;
        return true;
    }

    /// <summary>Every key of a domain with its English text, for tests and audits.</summary>
    public static IReadOnlyDictionary<string, string> Entries(string domain)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Domains.TryGetValue(domain, out var manager)) return entries;
        if (manager.GetResourceSet(CultureInfo.InvariantCulture, true, false) is not { } set) return entries;
        foreach (System.Collections.DictionaryEntry entry in set)
            if (entry.Key is string key && entry.Value is string value) entries[key] = value;
        return entries;
    }
}
