namespace Noctaxis.Desktop.Tests;

internal static class TestPaths
{
    public static string MainWindowMarkup
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Noctaxis.slnx")))
                {
                    return Path.Combine(directory.FullName, "Noctaxis.Desktop", "Views", "MainWindow.axaml");
                }
            }

            throw new DirectoryNotFoundException("Could not locate the Noctaxis workspace root.");
        }
    }

    /// <summary>The Desktop project directory.</summary>
    public static string DesktopProject => Path.GetDirectoryName(Path.GetDirectoryName(MainWindowMarkup))!;
}

/// <summary>
/// Markup as the user reads it: every <c>{loc:Text Key}</c> is replaced by its English text, so tests
/// that check structure and displayed copy read the same words the window shows.
/// </summary>
internal static partial class TestMarkup
{
    [System.Text.RegularExpressions.GeneratedRegex(@"\{loc:Text\s+(?<key>[A-Za-z0-9_]+)\}")]
    private static partial System.Text.RegularExpressions.Regex Reference();

    // The Desktop registers its resources when its module loads; a test that only reads files has not loaded it.
    static TestMarkup() => System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(Noctaxis.Desktop.App).Module.ModuleHandle);

    public static string Resolve(string markup) => Reference().Replace(markup, match =>
        System.Security.SecurityElement.Escape(Noctaxis.Core.Localisation.Strings.Get(match.Groups["key"].Value))!);

    public static string ReadMainWindow() => Resolve(File.ReadAllText(TestPaths.MainWindowMarkup));

    public static System.Xml.Linq.XDocument LoadMainWindow() => System.Xml.Linq.XDocument.Parse(ReadMainWindow());
}

internal static class TestMaps
{
    /// <summary>
    /// Keeps a Planner window's Picture-in-Picture map offline, as the harnesses keep the main map: its
    /// basemap layers are switched off, so no test reaches the tile server.
    /// </summary>
    public static void OfflinePip(Noctaxis.Desktop.Views.MainWindow window)
    {
        if (window.PipForTesting is not { } pip) return;
        foreach (var (id, layer) in pip.CompositionForTesting.NativeLayerOrder)
            if (id is "basemap" or Noctaxis.Desktop.Mapping.PlannerMapComposition.BasemapReferenceId) layer.Enabled = false;
    }
}
