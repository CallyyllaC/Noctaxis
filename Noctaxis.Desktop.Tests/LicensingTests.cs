using System.Text.RegularExpressions;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.Tests;

public sealed class LicensingTests
{
    private static string Repository => Path.GetDirectoryName(TestPaths.DesktopProject)!;
    private static string Source(string relative) => Path.Combine(Repository, relative);

    [Fact]
    public void BuildOutputCarriesCurrentProjectTermsAndEveryRetainedThirdPartyNotice()
    {
        Assert.Equal("# Awoo Licence v2.0", File.ReadLines(Source("LICENSE")).First());
        foreach (var relative in new[] { "LICENSE", "AWOO-SUPPORTER-SYSTEM.md", "THIRD_PARTY_NOTICES.md" })
            Assert.Equal(File.ReadAllBytes(Source(relative)), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, relative)));

        var notices = Source("Noctaxis.Desktop/ThirdParty");
        foreach (var file in Directory.EnumerateFiles(notices, "*", SearchOption.AllDirectories))
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
                "ThirdParty", Path.GetRelativePath(notices, file))));
        Assert.Equal(File.ReadAllBytes(Source("Noctaxis.Core/Data/OpenNGC-LICENSE.txt")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ThirdParty", "OpenNGC-LICENSE.txt")));
    }

    [Fact]
    public void DeclarationExactlyMatchesFiveRegisteredSupporterFamiliesAndUsableStandardThemes()
    {
        var document = File.ReadAllText(Source("AWOO-SUPPORTER-SYSTEM.md"));
        // The supporter features are the rows of the "Supporter themes" table: | **Name** | `identifier` | ... |,
        // which the definition says are the only ones.
        var table = Regex.Match(document, @"^## Supporter themes\r?$(?<body>.*?)^## ", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(table.Success, "the definition has no Supporter themes section");
        var rows = Regex.Matches(table.Groups["body"].Value, @"^\| \*\*(?<name>[^*|]+)\*\* \| `(?<id>[a-z.-]+)` \|", RegexOptions.Multiline)
            .Select(match => (Name: match.Groups["name"].Value, Id: match.Groups["id"].Value)).ToArray();
        Assert.Contains("the **only** supporter features currently defined", table.Groups["body"].Value);
        var declared = rows.Select(row => row.Id).ToArray();
        var families = BuiltInPalettes.Definitions.Where(theme => theme.IsSupporter).ToArray();
        Assert.Equal(5, declared.Length);
        Assert.Equal(families.Select(theme => theme.Id), declared);
        Assert.Equal(families.Select(theme => theme.DisplayName), rows.Select(row => row.Name));
        Assert.All(declared, id => Assert.StartsWith("supporter.", id));
        Assert.Contains("supporter.pigeon", declared);
        // The licence contract: the revision, the operative licence, one shared entitlement with no tier of its
        // own for Pigeon, and the standard themes that stay available without it.
        Assert.Contains("**Definition revision:** 1", document);
        Assert.Contains("[AWOO Licence v2.0](LICENSE)", document);
        Assert.Contains("same official supporter entitlement", document);
        Assert.Contains("private theme tier", document);
        Assert.Contains("`builtin.noctaxis`", document);
        Assert.Contains("`builtin.highcontrast`", document);

        var locked = new ThemeCatalogue();
        var unlocked = new ThemeCatalogue(isAvailable: _ => true);
        foreach (var family in families)
        {
            Assert.False(locked.IsAvailable(family));
            Assert.Equal(ThemeCatalogue.DefaultId, locked.Resolve(new(family.Id)).Definition.Id);
            Assert.True(unlocked.IsAvailable(family));
            Assert.Equal(family.Id, unlocked.Resolve(new(family.Id)).Definition.Id);
        }
        var standard = BuiltInPalettes.Definitions.Where(theme => !theme.IsSupporter).ToArray();
        Assert.Equal(new[] { "builtin.noctaxis", "builtin.highcontrast" }, standard.Select(theme => theme.Id));
        Assert.All(standard, theme => Assert.True(locked.IsAvailable(theme)));
    }

    [Fact]
    public void OperativeDocumentationDoesNotLinkObsoleteDraftTerms()
    {
        foreach (var relative in new[] { "README.md", "COMMERCIAL-LICENSING.md", "CONTRIBUTING.md",
                     "AWOO-SUPPORTER-SYSTEM.md", "THIRD_PARTY_NOTICES.md", "docs/licensing.md", "docs/awoo.md",
                     "docs/supporter-themes.md", "docs/supporter-licensing.md", "docs/user-guide/README.md",
                     "docs/user-guide/settings.md", "docs/user-guide/about.md", "docs/branding.md" })
        {
            var document = File.ReadAllText(Source(relative));
            Assert.False(Regex.IsMatch(document, @"Awoo Licence v1[.]0", RegexOptions.IgnoreCase), relative);
            Assert.DoesNotContain("licence-v2-review.md", document);
            Assert.DoesNotContain("licences/Awoo-", document);
            Assert.DoesNotContain("Supporter-System-v1", document);
        }
    }
}
