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

    /// <summary>
    /// The platforms whose test suites run natively: the supported one in CI and before every release, the
    /// best-effort ones in a workflow (and badge) of their own that gates nothing.
    /// </summary>
    private static readonly (string Runner, string Rid) SupportedTestRunner = ("windows-latest", "win-x64");
    private static readonly (string Runner, string Rid)[] BestEffortTestRunners =
        [("ubuntu-latest", "linux-x64"), ("macos-latest", "osx-arm64")];

    private static string Workflows => Path.Combine(Repository, ".github", "workflows");
    private static string Workflow(string name) => File.ReadAllText(Path.Combine(Workflows, name)).ReplaceLineEndings("\n");
    private static string Triggers(string workflow) =>
        workflow[workflow.IndexOf("\non:\n", StringComparison.Ordinal)..workflow.IndexOf("\npermissions:", StringComparison.Ordinal)];
    private static string Job(string workflow, string job) =>
        workflow[workflow.IndexOf($"\n  {job}:\n", StringComparison.Ordinal)..];

    /// <summary>
    /// The release workflow keeps to the support policy (docs/releasing.md):
    /// - every package is built, checked and started from its archive on a hosted runner of its own operating
    ///   system and processor, which the job confirms first, and only then kept;
    /// - a release is published only by the publish job, only when the plan says so and every package and test job
    ///   succeeded, as a draft completed before it is made public;
    /// - it runs by hand (publishing only when asked), for pushes to main, or from the version bump, never for pull
    ///   requests, tags or on a schedule; only the publish job may write, with the workflow's own token.
    /// </summary>
    [Fact]
    public void TheReleaseWorkflowStartsEveryPackageOnItsOwnSystemAndPublishesOnlyWhenEverythingPassed()
    {
        var shared = Workflow("platform-package.yml");
        var release = Workflow("release.yml");
        foreach (var (runner, rid) in PackageRunners)
            Assert.Contains($"- {{ runner: {runner}, rid: {rid} }}", Job(release, "package"));
        Assert.Contains("uses: ./.github/workflows/platform-package.yml", Job(release, "package"));

        // Every package is checked from its archive, then started: no step is limited to some of them.
        int At(string text) => shared.IndexOf(text, StringComparison.Ordinal);
        Assert.InRange(At("bash scripts/check-runner-platform.sh ${{ inputs.rid }}"), 0, At("- name: Package (Windows)"));
        Assert.InRange(At("bash scripts/check-package.sh"), At("- name: Package (Linux, macOS)"), At("- name: Start the package (Windows)"));
        foreach (var system in new[] { "Windows", "Linux", "macOS" })
            Assert.Contains($"- name: Start the package ({system})\n        if: runner.os == '{system}'", shared);
        Assert.Contains("bash scripts/check-startup-log.sh", shared);
        Assert.Contains("${{ inputs.version }}", shared[At("- name: Start the package (Linux)")..]);
        Assert.True(At("- name: Keep the package") > At("- name: Start the package (macOS)"), "a package is kept only once it has started");

        var triggers = Triggers(release);
        Assert.Contains("workflow_dispatch:\n    inputs:\n      publish:", triggers);
        Assert.Contains("default: false", triggers);
        Assert.Contains("push:\n    branches:\n      - main\n", triggers);
        Assert.Contains("workflow_call:", triggers);
        foreach (var excluded in new[] { "pull_request", "tags:", "schedule:" })
            Assert.DoesNotContain(excluded, triggers);

        var publish = Job(release, "publish");
        Assert.Contains("needs: [plan, package, tests]", publish);
        Assert.Contains("if: needs.plan.outputs.publish == 'true'", publish);
        Assert.Contains("--draft", publish);
        Assert.Contains("gh release edit \"$TAG\" --draft=false", publish);
        Assert.Contains("\npermissions:\n  contents: read\n", release);
        Assert.Single(Regex.Matches(release, "contents: write"));
        Assert.Contains("contents: write", publish);
        Assert.DoesNotContain("gh release", release[..release.IndexOf("\n  publish:\n", StringComparison.Ordinal)]);

        // Nothing else publishes, and no workflow but the AWOO website hook uses a secret.
        foreach (var file in Directory.GetFiles(Workflows, "*.yml"))
        {
            var name = Path.GetFileName(file);
            var text = Workflow(name);
            if (name != "release.yml") Assert.DoesNotContain("gh release", text);
            if (name != "trigger-awoo-rebuild.yml") Assert.DoesNotContain("secrets.", text);
        }
        foreach (var name in new[] { "ci.yml", "platform-tests-best-effort.yml", "platform-package.yml", "platform-tests.yml" })
        {
            Assert.Contains("contents: read", Workflow(name));
            Assert.DoesNotContain("contents: write", Workflow(name));
        }
        // Ordinary development builds and tests; it never packages.
        Assert.DoesNotContain("platform-package.yml", Workflow("ci.yml"));
        Assert.DoesNotContain("platform-package.yml", Workflow("platform-tests-best-effort.yml"));
    }

    /// <summary>
    /// The suites run natively: the supported platform's in CI and as a required job of every release, the best-effort
    /// platforms' in their own workflow, each reporting its own result. No workflow turns a failure into a success, so
    /// a badge never shows a pass that did not happen.
    /// </summary>
    [Fact]
    public void EveryPlatformReportsItsOwnFailures()
    {
        foreach (var name in new[] { "ci.yml", "release.yml" })
        {
            var tests = Job(Workflow(name), "tests");
            Assert.Contains("uses: ./.github/workflows/platform-tests.yml", tests);
            Assert.Contains($"runner: {SupportedTestRunner.Runner}\n      rid: {SupportedTestRunner.Rid}\n", tests);
        }
        var bestEffort = Job(Workflow("platform-tests-best-effort.yml"), "tests");
        Assert.Contains("fail-fast: false", bestEffort);
        Assert.Contains("uses: ./.github/workflows/platform-tests.yml", bestEffort);
        foreach (var (runner, rid) in BestEffortTestRunners)
            Assert.Contains($"- {{ runner: {runner}, rid: {rid} }}", bestEffort);
        Assert.Equal(Triggers(Workflow("ci.yml")), Triggers(Workflow("platform-tests-best-effort.yml")));
        Assert.Contains("bash scripts/check-runner-platform.sh ${{ inputs.rid }}", Workflow("platform-tests.yml"));
        foreach (var file in Directory.GetFiles(Workflows, "*.yml"))
            Assert.DoesNotContain("continue-on-error", File.ReadAllText(file));
    }

    /// <summary>
    /// The README's status badges are those of CI, the best-effort platform tests and the release workflow, on main,
    /// and each shows and links a workflow that exists.
    /// </summary>
    [Fact]
    public void TheReadmeBadgesShowRealWorkflows()
    {
        var readme = File.ReadAllText(Path.Combine(Repository, "README.md"));
        const string Actions = @"https://github\.com/CallyyllaC/Noctaxis/actions/workflows/";
        var badges = Regex.Matches(readme,
            $@"\[!\[[^\]]+\]\({Actions}([\w.-]+\.yml)/badge\.svg\?branch=main\)\]\({Actions}([\w.-]+\.yml)\?query=branch%3Amain\)");
        Assert.Equal(["ci.yml", "platform-tests-best-effort.yml", "release.yml"], badges.Select(badge => badge.Groups[1].Value).Order());
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
