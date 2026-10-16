using Noctaxis.Core.Persistence;
using SpecialFolder = System.Environment.SpecialFolder;

namespace Noctaxis.Core.Tests;

/// <summary>Settings, logs and caches live in each platform's conventional per-user folder.</summary>
public sealed class UserDataPathTests
{
    private static Func<SpecialFolder, string> Folders(params (SpecialFolder Folder, string Path)[] folders) =>
        folder => folders.FirstOrDefault(entry => entry.Folder == folder).Path ?? "";

    [Fact]
    public void WindowsAndLinuxUseTheRoamingConfigurationFolder()
    {
        // .NET's ApplicationData: %APPDATA% on Windows, $XDG_CONFIG_HOME or ~/.config on Linux.
        Assert.Equal(Path.Combine(@"C:\Users\Ann\AppData\Roaming", "Noctaxis"),
            PlatformUserDataPathProvider.Resolve(false, Folders((SpecialFolder.ApplicationData, @"C:\Users\Ann\AppData\Roaming"))));
        Assert.Equal(Path.Combine("/home/ann/.config", "Noctaxis"),
            PlatformUserDataPathProvider.Resolve(false, Folders((SpecialFolder.ApplicationData, "/home/ann/.config"),
                (SpecialFolder.LocalApplicationData, "/home/ann/.local/share"))));
    }

    /// <summary>macOS: Application Support, not the Linux-style ~/.config that .NET's ApplicationData gives there.</summary>
    [Fact]
    public void MacOSUsesApplicationSupport()
    {
        Assert.Equal(Path.Combine("/Users/ann/Library/Application Support", "Noctaxis"),
            PlatformUserDataPathProvider.Resolve(true, Folders((SpecialFolder.ApplicationData, "/Users/ann/.config"),
                (SpecialFolder.LocalApplicationData, "/Users/ann/Library/Application Support"))));
    }

    [Fact]
    public void WithoutTheFolderTheHomeFolderConventionIsUsed()
    {
        Assert.Equal(Path.Combine("/home/ann", ".config", "Noctaxis"),
            PlatformUserDataPathProvider.Resolve(false, Folders((SpecialFolder.UserProfile, "/home/ann"))));
        Assert.Equal(Path.Combine("/Users/ann", "Library", "Application Support", "Noctaxis"),
            PlatformUserDataPathProvider.Resolve(true, Folders((SpecialFolder.UserProfile, "/Users/ann"))));
    }

    /// <summary>On this machine, the folder is the one for the platform it runs on.</summary>
    [Fact]
    public void ThisPlatformsFolderIsUsed()
    {
        var directory = new PlatformUserDataPathProvider().GetApplicationDataDirectory();
        Assert.Equal("Noctaxis", Path.GetFileName(directory));
        Assert.True(Path.IsPathRooted(directory));
        if (OperatingSystem.IsMacOS())
            Assert.EndsWith(Path.Combine("Library", "Application Support", "Noctaxis"), directory);
        else
            Assert.Equal(Path.Combine(System.Environment.GetFolderPath(SpecialFolder.ApplicationData), "Noctaxis"), directory);
    }
}
