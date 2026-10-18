using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Tests;

// PF-05: the search index must rank exactly as the original per-query normalisation did, and the search
// must not run on the caller's (UI) thread.
public sealed class CatalogueSearchIndexTests
{
    private static readonly OpenNgcTargetCatalogue Catalogue = new();

    public static TheoryData<string> Queries => new()
    {
        "m3", "M31", "m 31", "messier 31", "ngc", "NGC 224", "ngc0224", "ngc 7000", "ic 434", "IC0434", "C14", "caldwell 14",
        "B33", "barnard 033", "HIP 1041", "hip 017465", "orion", "Orion Nebula", "andromeda", "pleiades", "veil", "crab",
        "ring", "whirlpool", "sombrero", "cyg", "cygnus", "ursa", "eta car", "47 tuc", "omega", "zzqx", "  m42  ",
        "Mel022", "openngc", "gal", "neb", "2", "10", "001", "é", "Boötes", "ngc-1976", "M 45", "c 001"
    };

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task IndexedSearchRanksExactlyAsTheOriginalNormalisation(string query)
    {
        var service = new LocalTargetSearchService(Catalogue);
        var expected = ReferenceSearch(new CatalogueSearchQuery(query), 50);
        var actual = await service.SearchAsync(query, 50, CancellationToken.None);
        Assert.Equal(expected.Select(target => target.Id), actual.Select(target => target.Id));
    }

    [Fact]
    public async Task IndexedSearchMatchesTheOriginalWithEveryFilter()
    {
        var service = new LocalTargetSearchService(Catalogue);
        var cases = new[]
        {
            new CatalogueSearchQuery("", AstralTargetCategory.Galaxy),
            new CatalogueSearchQuery("", Constellation: "Orion"),
            new CatalogueSearchQuery("", DesignationFamily: "Messier"),
            new CatalogueSearchQuery("Orion Nebula", AstralTargetCategory.Nebula, "Orion"),
            new CatalogueSearchQuery("Galaxy", AstralTargetCategory.Galaxy, DesignationFamily: "NGC"),
            new CatalogueSearchQuery("ngc", DesignationFamily: "hip"),
            new CatalogueSearchQuery("m", AstralTargetCategory.OpenCluster)
        };
        foreach (var query in cases)
        foreach (var maximum in new[] { 1, 12, 50 })
            Assert.Equal(ReferenceSearch(query, maximum).Select(target => target.Id),
                (await service.SearchAsync(query, maximum, CancellationToken.None)).Select(target => target.Id));
    }

    [Fact]
    public void EveryTargetRanksExactlyAsTheOriginalForItsOwnNames()
    {
        foreach (var target in Catalogue.Targets.Where(target => !target.IsSun && !target.IsMoon).Take(2_000))
        foreach (var query in new[] { target.DisplayName, target.PrimaryIdentifier, target.Aliases?.FirstOrDefault(), "ngc 1" })
        {
            if (query is null) continue;
            Assert.Equal(ReferenceMatchRank(target, query), TargetSearchNormalization.MatchRank(target, query));
        }
    }

    [Fact]
    public async Task SearchReturnsBeforeTheCatalogueIsReadOnTheCallersThread()
    {
        using var release = new ManualResetEventSlim();
        var catalogue = new BlockingCatalogue(Catalogue, release);
        var search = new LocalTargetSearchService(catalogue);
        var started = DateTime.UtcNow;
        var pending = search.SearchAsync("andromeda", 12, CancellationToken.None);
        // A synchronous search would still be blocked inside the catalogue here.
        Assert.False(pending.IsCompleted);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        release.Set();
        Assert.Contains(await pending, target => target.Id == "openngc:NGC0224");
    }

    [Fact]
    public async Task ACancelledSearchIsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalTargetSearchService(Catalogue).SearchAsync("andromeda", 12, cancellation.Token));
    }

    private sealed class BlockingCatalogue(ITargetCatalogue inner, ManualResetEventSlim release) : ITargetCatalogue
    {
        public IReadOnlyList<AstralTarget> Targets
        {
            get { release.Wait(TimeSpan.FromSeconds(30)); return inner.Targets; }
        }
        public IReadOnlyList<AstralTargetCategory> ObjectTypes => inner.ObjectTypes;
        public IReadOnlyList<string> Constellations => inner.Constellations;
        public IReadOnlyList<string> DesignationFamilies => inner.DesignationFamilies;
        public string? ResolveId(string id) => inner.ResolveId(id);
        public string? ResolveConfiguredTargetId(string persistedReference) => inner.ResolveConfiguredTargetId(persistedReference);
        public AstralTarget Get(string id) => inner.Get(id);
    }

    // The original implementation (before PF-05), kept verbatim as the reference.
    private static IReadOnlyList<AstralTarget> ReferenceSearch(CatalogueSearchQuery query, int maximumResults)
    {
        var text = ReferenceNormalize(query.Text);
        if (text.Length is > 0 and < 2) return [];
        IEnumerable<AstralTarget> candidates = Catalogue.Targets.Where(target => !target.IsSun && !target.IsMoon);
        if (query.ObjectType.HasValue) candidates = candidates.Where(target => target.Category == query.ObjectType);
        if (!string.IsNullOrWhiteSpace(query.Constellation)) candidates = candidates.Where(target => string.Equals(target.Constellation, query.Constellation, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query.DesignationFamily))
            candidates = candidates.Where(target => new[] { target.PrimaryIdentifier }.Concat(target.CatalogueIdentifiers ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value)).Any(id =>
                    (ReferenceTryParse(id, out var designation) ? designation.Family : "Other").Equals(query.DesignationFamily, StringComparison.OrdinalIgnoreCase)));
        var ranked = text.Length >= 2
            ? candidates.Select(target => (Target: target, Rank: ReferenceMatchRank(target, query.Text))).Where(match => match.Rank.HasValue)
            : candidates.Select(target => (Target: target, Rank: (int?)0));
        return ranked.OrderBy(match => match.Rank)
            .ThenBy(match => match.Target.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(match => match.Target.Id, StringComparer.OrdinalIgnoreCase)
            .Select(match => match.Target)
            .Take(Math.Clamp(maximumResults, 1, 50)).ToArray();
    }

    private sealed record ReferenceDesignation(string Family, string Number, string Suffix);

    private static string ReferenceNormalize(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static int? ReferenceMatchRank(AstralTarget target, string query)
    {
        var normalizedQuery = ReferenceNormalize(query);
        if (normalizedQuery.Length == 0) return 0;
        var identifiers = new[] { target.PrimaryIdentifier }.Concat(target.CatalogueIdentifiers ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var aliases = (target.Aliases ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var normalizedDisplay = ReferenceNormalize(target.DisplayName);
        if (normalizedDisplay.Equals(normalizedQuery, StringComparison.Ordinal)) return 0;
        if (identifiers.Any(value => value.Trim().Equals(query.Trim(), StringComparison.OrdinalIgnoreCase))) return 1;
        if (identifiers.Any(value => ReferenceNormalize(value).Equals(normalizedQuery, StringComparison.Ordinal)) ||
            ReferenceTryParse(query, out var queryDesignation) && identifiers.Any(value => ReferenceTryParse(value, out var actual) && actual == queryDesignation)) return 2;
        if (normalizedDisplay.StartsWith(normalizedQuery, StringComparison.Ordinal) ||
            identifiers.Any(value => ReferenceNormalize(value).StartsWith(normalizedQuery, StringComparison.Ordinal))) return 3;
        if (aliases.Any(value => ReferenceNormalize(value).Equals(normalizedQuery, StringComparison.Ordinal))) return 4;
        if (aliases.Any(value => ReferenceNormalize(value).StartsWith(normalizedQuery, StringComparison.Ordinal)) ||
            identifiers.Any(value => ReferenceNormalize(value).Contains(normalizedQuery, StringComparison.Ordinal))) return 5;
        return new[] { target.DisplayName, target.Id, target.Constellation }.Concat(aliases).Concat(identifiers)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Any(value => ReferenceNormalize(value).Contains(normalizedQuery, StringComparison.Ordinal)) ? 6 : null;
    }

    private static bool ReferenceTryParse(string? value, out ReferenceDesignation designation)
    {
        designation = null!;
        var compact = ReferenceNormalize(value);
        var families = new (string Prefix, string Family)[]
        {
            ("CALDWELL", "Caldwell"), ("BARNARD", "Barnard"), ("MESSIER", "Messier"),
            ("NGC", "NGC"), ("HIP", "HIP"), ("IC", "IC"),
            ("C", "Caldwell"), ("B", "Barnard"), ("M", "Messier")
        };
        foreach (var (prefix, family) in families)
        {
            if (!compact.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var remainder = compact[prefix.Length..];
            var digitCount = remainder.TakeWhile(char.IsDigit).Count();
            if (digitCount == 0) continue;
            var number = remainder[..digitCount].TrimStart('0');
            designation = new ReferenceDesignation(family, number.Length == 0 ? "0" : number, remainder[digitCount..]);
            return true;
        }
        return false;
    }
}
