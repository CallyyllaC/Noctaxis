using System.Globalization;
using System.Text.Json;

namespace Noctaxis.Core.Localisation;

/// <summary>
/// The application language: one persisted culture identifier that decides the language of the text
/// and which language the map's place names prefer. It does not decide how numbers, dates and times
/// are written (the regional culture does) or which units are shown (the measurement profile does).
/// See docs/localisation.md.
/// </summary>
/// <remarks>
/// Identifiers are .NET culture names (BCP 47), such as <c>en-GB</c>. They are stored and compared as
/// they are, never translated; the name shown to the user comes from the resources.
/// </remarks>
public static class AppLanguages
{
    public const string DefaultId = "en-GB";

    /// <summary>Languages Noctaxis ships. A language is added here only when its resources exist.</summary>
    public static IReadOnlyList<string> SupportedIds { get; } = [DefaultId];

    private static string _current = DefaultId;

    /// <summary>The language this process is using.</summary>
    public static string Current => Volatile.Read(ref _current);

    /// <summary>Raised after <see cref="Apply"/> changes <see cref="Current"/>.</summary>
    public static event Action<string>? CurrentChanged;

    /// <summary>The supported identifier for a stored value; anything unknown or missing is the default.</summary>
    public static string Normalise(string? id) =>
        SupportedIds.FirstOrDefault(supported => string.Equals(supported, id?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? DefaultId;

    public static CultureInfo Culture(string? id) => CultureInfo.GetCultureInfo(Normalise(id));

    /// <summary>
    /// Makes a language the process's language for text: the UI culture, which selects resources,
    /// on this thread and every thread started later.
    /// </summary>
    /// <remarks>
    /// The regional culture (<see cref="CultureInfo.CurrentCulture"/>), which decides how numbers,
    /// dates and times are written, is deliberately left alone: it is the user's operating-system
    /// setting and is not inferred from the language. English text with German number formats is a
    /// valid combination. Machine-readable text uses neither; it is always invariant.
    /// </remarks>
    public static void Apply(string? id)
    {
        var language = Normalise(id);
        var culture = CultureInfo.GetCultureInfo(language);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        if (Interlocked.Exchange(ref _current, language) != language) CurrentChanged?.Invoke(language);
    }

    /// <summary>
    /// The language saved in a state file, read before anything else so the first window is built in
    /// it. A missing, unreadable or unrecognised value is the default; this never fails.
    /// </summary>
    public static string ReadSaved(string stateFilePath)
    {
        try
        {
            if (!File.Exists(stateFilePath)) return DefaultId;
            using var stream = new FileStream(stateFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = JsonDocument.Parse(stream);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.NameEquals("Settings") && !property.Name.Equals("Settings", StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind != JsonValueKind.Object) break;
                foreach (var setting in property.Value.EnumerateObject())
                    if (setting.Name.Equals("Language", StringComparison.OrdinalIgnoreCase) && setting.Value.ValueKind == JsonValueKind.String)
                        return Normalise(setting.Value.GetString());
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
        return DefaultId;
    }
}
