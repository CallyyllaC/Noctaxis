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
}
