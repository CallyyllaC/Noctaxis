using System.Buffers.Binary;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Headless.XUnit;
using Noctaxis.Desktop.Branding;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The release packages' own files (packaging/, scripts/package.*; docs/releasing.md) agree with the app they
/// package: the executable they name, its window class, its version placeholder and its icons.
/// </summary>
public sealed class PackagingTests
{
    private static string Repository => Path.GetDirectoryName(TestPaths.DesktopProject)!;
    private static string Packaging(params string[] parts) => Path.Combine([Repository, "packaging", .. parts]);

    /// <summary>The apphost's name: the Desktop project's assembly name, which no setting overrides.</summary>
    private const string Executable = "Noctaxis.Desktop";
    /// <summary>The Linux menu entry, by its reverse-DNS desktop file ID.</summary>
    private const string DesktopEntry = "io.github.callyyllac.noctaxis.desktop";

    /// <summary>
    /// Every file the Linux package adds beside the app has a name no app file has in any letter case: a menu entry
    /// once named noctaxis.desktop replaced the Noctaxis.Desktop executable when extracted on a case-insensitive disk.
    /// </summary>
    [Fact]
    public void NoPackagedFileNameDiffersFromAnAppFileOnlyInCase()
    {
        var added = Directory.GetFiles(Packaging("linux")).Select(Path.GetFileName).ToArray();
        var app = new[] { Executable, Executable + ".dll", Executable + ".pdb", "Noctaxis.Core.dll", "LICENSE", "THIRD_PARTY_NOTICES.md" };
        foreach (var name in added)
            Assert.DoesNotContain(app, file => string.Equals(file, name, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(added.Length, added.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(DesktopEntry, added);
        Assert.Contains(DesktopEntry, File.ReadAllText(Path.Combine(Repository, "scripts", "package.sh")));
        Assert.Contains(DesktopEntry, File.ReadAllText(Packaging("linux", "install-desktop-entry.sh")));
    }

    private static int Drift(SKBitmap committed, SKBitmap expected)
    {
        var drift = 0;
        for (var y = 0; y < expected.Height; y++)
        for (var x = 0; x < expected.Width; x++)
        {
            SKColor a = committed.GetPixel(x, y), b = expected.GetPixel(x, y);
            int P(byte channel, byte alpha) => channel * alpha / 255;
            drift = Math.Max(drift, new[] { P(a.Red, a.Alpha) - P(b.Red, b.Alpha), P(a.Green, a.Alpha) - P(b.Green, b.Alpha),
                P(a.Blue, a.Alpha) - P(b.Blue, b.Alpha), a.Alpha - b.Alpha }.Max(Math.Abs));
        }
        return drift;
    }

    [Fact]
    public void TheExecutableNameIsTheAssemblyName()
    {
        var project = XDocument.Load(Path.Combine(TestPaths.DesktopProject, "Noctaxis.Desktop.csproj"));
        Assert.DoesNotContain(project.Descendants(), element => element.Name.LocalName is "AssemblyName" or "AppHostFileName");
        Assert.Equal(Executable, Path.GetFileNameWithoutExtension(Path.Combine(TestPaths.DesktopProject, "Noctaxis.Desktop.csproj")));
    }

    /// <summary>The bundle names the executable and icon it carries, the version the script fills in, and .NET 10's lowest macOS.</summary>
    [Fact]
    public void TheMacOSBundleDescribesTheApp()
    {
        var plist = XDocument.Load(Packaging("macos", "Info.plist"));
        var entries = plist.Root!.Element("dict")!.Elements().ToArray();
        string? Value(string key)
        {
            var index = Array.FindIndex(entries, element => element.Name == "key" && element.Value == key);
            return index < 0 ? null : entries[index + 1].Value;
        }
        Assert.Equal(Executable, Value("CFBundleExecutable"));
        Assert.Equal(NoctaxisIcon.IcnsFileName, Value("CFBundleIconFile"));
        Assert.True(File.Exists(Packaging("macos", NoctaxisIcon.IcnsFileName)));
        Assert.Equal("Noctaxis", Value("CFBundleName"));
        Assert.Equal("APPL", Value("CFBundlePackageType"));
        Assert.Equal("@VERSION@", Value("CFBundleShortVersionString"));
        Assert.Equal("@VERSION@", Value("CFBundleVersion"));
        Assert.Equal("14.0", Value("LSMinimumSystemVersion"));
        Assert.Matches(@"^[a-z0-9]+(\.[a-z0-9-]+)+$", Value("CFBundleIdentifier"));
        Assert.Contains("@VERSION@", File.ReadAllText(Path.Combine(Repository, "scripts", "package.sh")));
    }

    /// <summary>The menu entry runs the executable, shows the packaged icon and is matched to the window's class.</summary>
    [Fact]
    public void TheLinuxMenuEntryDescribesTheApp()
    {
        var entry = File.ReadAllLines(Packaging("linux", DesktopEntry))
            .Where(line => line.Contains('=')).ToDictionary(line => line[..line.IndexOf('=')], line => line[(line.IndexOf('=') + 1)..]);
        Assert.Equal("Application", entry["Type"]);
        Assert.Equal(Executable, entry["Exec"]);
        Assert.Equal(Path.GetFileNameWithoutExtension(NoctaxisIcon.LinuxIconFileName), entry["Icon"]);
        Assert.Equal(Program.LinuxWindowClass, entry["StartupWMClass"]);
        Assert.False(File.ReadAllText(Packaging("linux", DesktopEntry)).Contains('\r'));
        Assert.False(File.ReadAllText(Packaging("linux", "install-desktop-entry.sh")).Contains('\r'));
        Assert.False(File.ReadAllText(Path.Combine(Repository, "scripts", "package.sh")).Contains('\r'));
        Assert.False(File.ReadAllText(Path.Combine(Repository, "scripts", "check-startup-log.sh")).Contains('\r'));
    }

    /// <summary>The application's name, which macOS shows in its menu bar.</summary>
    [AvaloniaFact]
    public void TheApplicationIsNamedNoctaxis() => Assert.Equal("Noctaxis", Application.Current!.Name);

    /// <summary>The bundle and desktop icons are the canonical logo; regenerate with NoctaxisIcon.WritePackagingAssets.</summary>
    [Fact]
    public void ThePackageIconsAreGeneratedFromTheCanonicalLogo()
    {
        var icns = File.ReadAllBytes(Packaging("macos", NoctaxisIcon.IcnsFileName));
        Assert.Equal("icns"u8.ToArray(), icns[..4]);
        Assert.Equal(icns.Length, BinaryPrimitives.ReadInt32BigEndian(icns.AsSpan(4)));
        var offset = 8;
        foreach (var (type, size) in NoctaxisIcon.IcnsEntries)
        {
            Assert.Equal(type, System.Text.Encoding.ASCII.GetString(icns, offset, 4));
            var length = BinaryPrimitives.ReadInt32BigEndian(icns.AsSpan(offset + 4));
            using var committed = SKBitmap.Decode(icns.AsSpan(offset + 8, length - 8));
            using var expected = NoctaxisIcon.Render(size);
            Assert.Equal((size, size), (committed.Width, committed.Height));
            Assert.True(Drift(committed, expected) <= 3, $"{type} ({size}px) differs from the canonical logo");
            offset += length;
        }
        Assert.Equal(icns.Length, offset);

        using var linux = SKBitmap.Decode(Packaging("linux", NoctaxisIcon.LinuxIconFileName));
        using var canonical = NoctaxisIcon.Render(NoctaxisIcon.LinuxIconSize);
        Assert.True(Drift(linux, canonical) <= 3, "the Linux icon differs from the canonical logo");
    }

    /// <summary>Every package's hosted runner: its own operating system and processor.</summary>
    private static readonly (string Runner, string Rid)[] PackageRunners =
    [
        ("windows-latest", "win-x64"), ("windows-11-arm", "win-arm64"), ("ubuntu-latest", "linux-x64"),
        ("ubuntu-24.04-arm", "linux-arm64"), ("macos-latest", "osx-arm64"), ("macos-15-intel", "osx-x64"),
    ];

    /// <summary>The platforms whose test suites run in CI, each in a workflow (and README badge) of its own.</summary>
    private static readonly (string Runner, string Rid)[] TestRunners =
        [("windows-latest", "win-x64"), ("ubuntu-latest", "linux-x64"), ("macos-latest", "osx-arm64")];

    private static string Workflows => Path.Combine(Repository, ".github", "workflows");
    private static string Workflow(string name) => File.ReadAllText(Path.Combine(Workflows, name)).ReplaceLineEndings("\n");
    private static string Triggers(string workflow) =>
        workflow[workflow.IndexOf("\non:\n", StringComparison.Ordinal)..workflow.IndexOf("\npermissions:", StringComparison.Ordinal)];

    /// <summary>
    /// The package workflows keep to the support policy (docs/releasing.md):
    /// - every package is built, checked and started on a hosted runner of its own operating system and processor,
    ///   which the job confirms first;
    /// - each platform has a build workflow of its own (its README badge) for pushes and pull requests, which keeps nothing;
    /// - only package.yml keeps packages, and it runs only by hand or for a version tag, never on an ordinary push;
    /// - nothing is published and no secret is used.
    /// </summary>
    [Fact]
    public void ThePackageWorkflowsStartEveryPackageOnItsOwnSystemAndKeepItOnlyWhenWanted()
    {
        var shared = Workflow("platform-package.yml");
        var release = Workflow("package.yml");
        foreach (var (runner, rid) in PackageRunners)
        {
            Assert.Contains($"- {{ os: {runner}, rid: {rid} }}", release);
            var build = Workflow($"build-{rid}.yml");
            Assert.Contains($"uses: ./.github/workflows/platform-package.yml\n    with:\n      runner: {runner}\n      rid: {rid}\n", build);
            Assert.DoesNotContain("keep-package", build);
            var triggers = Triggers(build);
            Assert.Contains("branches:", triggers);
            Assert.Contains("pull_request:", triggers);
            Assert.DoesNotContain("tags:", triggers);
            Assert.DoesNotContain("secrets.", build);
        }
        Assert.Contains("uses: ./.github/workflows/platform-package.yml", release);
        Assert.Contains("keep-package: true", release);

        // Every package is started: no step is limited to some of them.
        Assert.DoesNotContain("matrix.smoke", shared);
        var platformCheck = shared.IndexOf("bash scripts/check-runner-platform.sh ${{ inputs.rid }}", StringComparison.Ordinal);
        Assert.InRange(platformCheck, 0, shared.IndexOf("- name: Package (Windows)", StringComparison.Ordinal));
        foreach (var system in new[] { "Windows", "Linux", "macOS" })
            Assert.Contains($"- name: Start the package ({system})\n        if: runner.os == '{system}'", shared);
        Assert.Contains("bash scripts/check-startup-log.sh", shared);
        Assert.Contains("- name: Keep the package\n        if: inputs.keep-package\n", shared);

        var releaseTriggers = Triggers(release);
        Assert.Contains("workflow_dispatch:", releaseTriggers);
        Assert.Contains("tags:", releaseTriggers);
        Assert.DoesNotContain("branches:", releaseTriggers);
        Assert.DoesNotContain("pull_request", releaseTriggers);
        Assert.DoesNotContain("schedule:", releaseTriggers);
        foreach (var workflow in new[] { shared, release })
        {
            Assert.DoesNotContain("gh release", workflow);
            Assert.DoesNotContain("secrets.", workflow);
            Assert.Contains("contents: read", workflow);
        }
    }

    /// <summary>
    /// Each tested platform runs the suites natively in a workflow of its own, and no workflow turns a failure into a
    /// success, so a README badge never shows a pass that did not happen.
    /// </summary>
    [Fact]
    public void EveryPlatformWorkflowReportsItsOwnFailures()
    {
        foreach (var (runner, rid) in TestRunners)
            Assert.Contains($"uses: ./.github/workflows/platform-tests.yml\n    with:\n      runner: {runner}\n      rid: {rid}\n",
                Workflow($"tests-{rid}.yml"));
        Assert.Contains("bash scripts/check-runner-platform.sh ${{ inputs.rid }}", Workflow("platform-tests.yml"));
        foreach (var file in Directory.GetFiles(Workflows, "*.yml"))
            Assert.DoesNotContain("continue-on-error", File.ReadAllText(file));
    }

    /// <summary>Every README status badge shows and links a workflow that exists, for pushes to main.</summary>
    [Fact]
    public void TheReadmeBadgesShowRealWorkflows()
    {
        var readme = File.ReadAllText(Path.Combine(Repository, "README.md"));
        const string Actions = @"https://github\.com/CallyyllaC/Noctaxis/actions/workflows/";
        var badges = Regex.Matches(readme,
            $@"\[!\[[^\]]+\]\({Actions}([\w.-]+\.yml)/badge\.svg\?branch=main&event=push\)\]\({Actions}([\w.-]+\.yml)\?query=branch%3Amain\)");
        var shown = badges.Select(badge => badge.Groups[1].Value).ToArray();
        Assert.Equal(PackageRunners.Select(platform => $"build-{platform.Rid}.yml")
            .Concat(TestRunners.Select(platform => $"tests-{platform.Rid}.yml")).Order(), shown.Order());
        foreach (Match badge in badges)
        {
            Assert.Equal(badge.Groups[1].Value, badge.Groups[2].Value);
            Assert.True(File.Exists(Path.Combine(Workflows, badge.Groups[1].Value)), badge.Groups[1].Value);
        }
        // Only real badges: no static shields standing in for a status.
        Assert.DoesNotContain("img.shields.io", readme);
    }

    /// <summary>Each package checks for the runtime, the native libraries and the licence texts before it is written.</summary>
    [Fact]
    public void ThePackageScriptsCheckWhatTheyShip()
    {
        var shell = File.ReadAllText(Path.Combine(Repository, "scripts", "package.sh"));
        var windows = File.ReadAllText(Path.Combine(Repository, "scripts", "package.ps1"));
        foreach (var script in new[] { shell, windows })
        {
            Assert.Contains("--self-contained true", script);
            Assert.Contains("THIRD_PARTY_NOTICES.md", script);
            Assert.Contains("Microsoft-NOTICES.txt", script);
            Assert.Contains("libSkiaSharp", script);
            Assert.Contains("lib*.pdb", script);
            // Nothing is signed with a certificate, uploaded or published.
            Assert.DoesNotContain("signtool", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("notarytool", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gh release", script, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("libAvaloniaNative.dylib", shell);
        // Ad-hoc ("-"): each file in Contents/MacOS, then the bundle, then a strict check of the whole.
        Assert.Contains("codesign --force --sign - --timestamp=none \"$app\"", shell);
        Assert.Contains("xargs -0 -n 1 codesign --force --sign -", shell);
        Assert.Contains("codesign --verify --deep --strict", shell);
    }
}
