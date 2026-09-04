using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Catalogues;

public sealed record CatalogueSearchQuery(string Text = "", AstralTargetCategory? ObjectType = null,
    string? Constellation = null, string? DesignationFamily = null);

public interface ITargetCatalogue
{
    IReadOnlyList<AstralTarget> Targets { get; }
    IReadOnlyList<AstralTargetCategory> ObjectTypes { get; }
    IReadOnlyList<string> Constellations { get; }
    IReadOnlyList<string> DesignationFamilies { get; }
    string? ResolveId(string id);
    string? ResolveConfiguredTargetId(string persistedReference);
    AstralTarget Get(string id);
}

public interface ITargetSearchService
{
    Task<IReadOnlyList<AstralTarget>> SearchAsync(CatalogueSearchQuery query, int maximumResults, CancellationToken cancellationToken);
    Task<IReadOnlyList<AstralTarget>> SearchAsync(string query, int maximumResults, CancellationToken cancellationToken) =>
        SearchAsync(new CatalogueSearchQuery(query), maximumResults, cancellationToken);
}

public sealed class LocalTargetSearchService(ITargetCatalogue catalogue) : ITargetSearchService
{
    public Task<IReadOnlyList<AstralTarget>> SearchAsync(string query, int maximumResults, CancellationToken cancellationToken) =>
        SearchAsync(new CatalogueSearchQuery(query), maximumResults, cancellationToken);

    public Task<IReadOnlyList<AstralTarget>> SearchAsync(CatalogueSearchQuery query, int maximumResults, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = TargetSearchNormalization.NormalizeText(query.Text);
        if (text.Length is > 0 and < 2) return Task.FromResult<IReadOnlyList<AstralTarget>>([]);
        IEnumerable<AstralTarget> candidates = catalogue.Targets.Where(target => !target.IsSun && !target.IsMoon);
        if (query.ObjectType.HasValue) candidates = candidates.Where(target => target.Category == query.ObjectType);
        if (!string.IsNullOrWhiteSpace(query.Constellation)) candidates = candidates.Where(target => string.Equals(target.Constellation, query.Constellation, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query.DesignationFamily))
            candidates = candidates.Where(target => Identifiers(target).Any(id =>
                TargetSearchNormalization.DesignationFamily(id).Equals(query.DesignationFamily, StringComparison.OrdinalIgnoreCase)));
        var ranked = text.Length >= 2
            ? candidates.Select(target => (Target: target, Rank: TargetSearchNormalization.MatchRank(target, query.Text)))
                .Where(match => match.Rank.HasValue)
            : candidates.Select(target => (Target: target, Rank: (int?)0));
        IReadOnlyList<AstralTarget> result = ranked.OrderBy(match => match.Rank)
            .ThenBy(match => match.Target.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(match => match.Target.Id, StringComparer.OrdinalIgnoreCase)
            .Select(match => match.Target)
            .Take(Math.Clamp(maximumResults, 1, 50)).ToArray();
        return Task.FromResult(result);
    }

    private static IEnumerable<string> Identifiers(AstralTarget target) =>
        new[] { target.PrimaryIdentifier }.Concat(target.CatalogueIdentifiers ?? []).Where(value => !string.IsNullOrWhiteSpace(value))!;
}

internal static class TargetSearchNormalization
{
    private sealed record Designation(string Family, string Number, string Suffix);

    internal static string NormalizeText(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    internal static string DesignationFamily(string identifier) =>
        TryParseDesignation(identifier, out var designation) ? designation.Family : "Other";

    internal static int? MatchRank(AstralTarget target, string query)
    {
        var normalizedQuery = NormalizeText(query);
        if (normalizedQuery.Length == 0) return 0;
        var identifiers = new[] { target.PrimaryIdentifier }.Concat(target.CatalogueIdentifiers ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var aliases = (target.Aliases ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var normalizedDisplay = NormalizeText(target.DisplayName);

        if (normalizedDisplay.Equals(normalizedQuery, StringComparison.Ordinal)) return 0;
        if (identifiers.Any(value => value.Trim().Equals(query.Trim(), StringComparison.OrdinalIgnoreCase))) return 1;
        if (identifiers.Any(value => NormalizeText(value).Equals(normalizedQuery, StringComparison.Ordinal)) ||
            TryParseDesignation(query, out var queryDesignation) && identifiers.Any(value => SameDesignation(value, queryDesignation))) return 2;
        if (normalizedDisplay.StartsWith(normalizedQuery, StringComparison.Ordinal) ||
            identifiers.Any(value => NormalizeText(value).StartsWith(normalizedQuery, StringComparison.Ordinal))) return 3;
        if (aliases.Any(value => NormalizeText(value).Equals(normalizedQuery, StringComparison.Ordinal))) return 4;
        if (aliases.Any(value => NormalizeText(value).StartsWith(normalizedQuery, StringComparison.Ordinal)) ||
            identifiers.Any(value => NormalizeText(value).Contains(normalizedQuery, StringComparison.Ordinal))) return 5;
        return new[] { target.DisplayName, target.Id, target.Constellation }.Concat(aliases).Concat(identifiers)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Any(value => NormalizeText(value).Contains(normalizedQuery, StringComparison.Ordinal)) ? 6 : null;
    }

    internal static bool IsExactDesignation(string value, string query) =>
        TryParseDesignation(query, out var queryDesignation) && SameDesignation(value, queryDesignation);

    private static bool SameDesignation(string value, Designation expected) =>
        TryParseDesignation(value, out var actual) && actual == expected;

    private static bool TryParseDesignation(string? value, out Designation designation)
    {
        designation = null!;
        var compact = NormalizeText(value);
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
            designation = new Designation(family, number.Length == 0 ? "0" : number, remainder[digitCount..]);
            return true;
        }
        return false;
    }
}

/// <summary>Offline catalogue populated from the unmodified OpenNGC CSV resources (CC-BY-SA-4.0).</summary>
public sealed class OpenNgcTargetCatalogue : ITargetCatalogue
{
    public const string Attribution = "OpenNGC · CC BY-SA 4.0";
    private static readonly Regex DesignationPattern = new("^(NGC|IC)(0*)([0-9]+)(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly IReadOnlyList<AstralTarget> _targets;
    private readonly IReadOnlyDictionary<string, AstralTarget> _byId;
    private readonly IReadOnlyDictionary<string, string> _openNgcNameToId;

    public OpenNgcTargetCatalogue()
    {
        var loaded = new List<AstralTarget>(14_000) { Sun(), Moon() };
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        LoadResource("Noctaxis.Core.Data.OpenNGC-NGC.csv", loaded, names);
        LoadResource("Noctaxis.Core.Data.OpenNGC-addendum.csv", loaded, names);
        _targets = loaded;
        _byId = loaded.ToDictionary(target => target.Id, StringComparer.OrdinalIgnoreCase);
        _openNgcNameToId = names;
        Validate(_targets);
        ObjectTypes = loaded.Where(target => !target.IsSun && !target.IsMoon).Select(target => target.Category).Distinct().Order().ToArray();
        Constellations = loaded.Select(target => target.Constellation).Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray()!;
        DesignationFamilies = loaded.SelectMany(target => new[] { target.PrimaryIdentifier }.Concat(target.CatalogueIdentifiers ?? []))
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => TargetSearchNormalization.DesignationFamily(value!))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(family => family != "Other").Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<AstralTarget> Targets => _targets;
    public IReadOnlyList<AstralTargetCategory> ObjectTypes { get; }
    public IReadOnlyList<string> Constellations { get; }
    public IReadOnlyList<string> DesignationFamilies { get; }

    public string? ResolveId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (_byId.TryGetValue(id, out var direct)) return direct.Id;
        var compact = id.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (_openNgcNameToId.TryGetValue(compact, out var openNgc)) return openNgc;
        var matches = _targets.Where(target => new[] { target.PrimaryIdentifier }.Concat(target.CatalogueIdentifiers ?? [])
            .Any(value => value?.Equals(id, StringComparison.OrdinalIgnoreCase) == true)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Id : null;
    }

    public string? ResolveConfiguredTargetId(string persistedReference)
    {
        if (string.IsNullOrWhiteSpace(persistedReference)) return null;
        var resolved = ResolveId(persistedReference);
        if (resolved is not null) return resolved;

        var designationMatches = _targets.Where(target => new[] { target.PrimaryIdentifier }.Concat(target.CatalogueIdentifiers ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Any(value => TargetSearchNormalization.IsExactDesignation(value!, persistedReference))).Take(2).ToArray();
        if (designationMatches.Length == 1) return designationMatches[0].Id;

        var normalized = TargetSearchNormalization.NormalizeText(persistedReference);
        var displayMatches = _targets.Where(target =>
            TargetSearchNormalization.NormalizeText(target.DisplayName).Equals(normalized, StringComparison.Ordinal)).Take(2).ToArray();
        if (displayMatches.Length == 1) return displayMatches[0].Id;

        var aliasMatches = _targets.Where(target => (target.Aliases ?? []).Any(alias =>
            TargetSearchNormalization.NormalizeText(alias).Equals(normalized, StringComparison.Ordinal))).Take(2).ToArray();
        return aliasMatches.Length == 1 ? aliasMatches[0].Id : null;
    }

    public AstralTarget Get(string id)
    {
        var resolved = ResolveId(id);
        return resolved is not null && _byId.TryGetValue(resolved, out var target)
            ? target : throw new KeyNotFoundException($"Unknown astral target '{id}'.");
    }

    private static void LoadResource(string resourceName, ICollection<AstralTarget> output, IDictionary<string, string> names)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The bundled OpenNGC resource '{resourceName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var header = ParseRow(reader.ReadLine() ?? throw new InvalidDataException("OpenNGC CSV has no header."));
        var columns = header.Select((name, index) => (name, index)).ToDictionary(pair => pair.name, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var row = ParseRow(line);
            string Field(string name) => columns.TryGetValue(name, out var index) && index < row.Count ? row[index].Trim() : string.Empty;
            var name = Field("Name");
            var type = Field("Type");
            if (name.Length == 0 || type is "Dup" or "NonEx" || !TryRa(Field("RA"), out var ra) || !TryDec(Field("Dec"), out var dec)) continue;
            var stableId = "openngc:" + name;
            var messier = FormatNumbered("M", Field("M"));
            var designation = FormatDesignation(name);
            var commonNames = SplitValues(Field("Common names"));
            var identifiers = SplitValues(Field("Identifiers"));
            var centralStarNames = SplitValues(Field("Cstar Names"));
            var catalogueIds = new[] { designation, messier, FormatNumbered("NGC", Field("NGC")), FormatNumbered("IC", Field("IC")) }
                .Concat(identifiers).Concat(centralStarNames).OfType<string>().Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var displayName = commonNames.FirstOrDefault() ?? (messier is not null ? $"{messier} · {designation}" : designation);
            var aliases = commonNames.Skip(1).Concat(identifiers).Concat(centralStarNames).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var magnitude = ParseDouble(Field("V-Mag")) ?? ParseDouble(Field("B-Mag"));
            var major = ParseDouble(Field("MajAx"));
            var minor = ParseDouble(Field("MinAx"));
            var angularSize = major.HasValue ? minor.HasValue ? $"{major:0.##} × {minor:0.##} arcmin" : $"{major:0.##} arcmin" : null;
            var notes = string.Join(" ", new[] { Field("OpenNGC notes"), Field("NED notes") }.Where(value => value.Length > 0));
            var target = new AstralTarget(stableId, displayName, MapType(type), ra, dec, "J2000",
                notes.Length == 0 ? null : notes, messier ?? designation, FullConstellation(Field("Const")), aliases,
                catalogueIds, magnitude, angularSize, $"OpenNGC CC-BY-SA-4.0; {Field("Sources")}");
            output.Add(target);
            names[name] = stableId;
        }
    }

    private static IReadOnlyList<string> ParseRow(string line)
    {
        var values = new List<string>(32);
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"') { current.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (character == ';' && !quoted) { values.Add(current.ToString()); current.Clear(); }
            else current.Append(character);
        }
        values.Add(current.ToString());
        return values;
    }

    private static bool TryRa(string value, out double hours)
    {
        hours = 0;
        var parts = value.Split(':');
        if (parts.Length != 3 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return false;
        hours = h + m / 60 + s / 3600;
        return hours is >= 0 and < 24;
    }

    private static bool TryDec(string value, out double degrees)
    {
        degrees = 0;
        var parts = value.TrimStart('+', '-').Split(':');
        if (parts.Length != 3 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return false;
        degrees = d + m / 60 + s / 3600;
        if (value.StartsWith('-')) degrees = -degrees;
        return degrees is >= -90 and <= 90;
    }

    private static double? ParseDouble(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    private static IReadOnlyList<string> SplitValues(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string? FormatNumbered(string prefix, string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? $"{prefix}{number}" : null;
    private static string FormatDesignation(string name)
    {
        var match = DesignationPattern.Match(name);
        return match.Success ? $"{match.Groups[1].Value.ToUpperInvariant()} {match.Groups[3].Value}{match.Groups[4].Value}".Trim() : name;
    }

    private static AstralTargetCategory MapType(string type) => type switch
    {
        "*" => AstralTargetCategory.Star, "**" => AstralTargetCategory.DoubleStar, "*Ass" => AstralTargetCategory.Asterism,
        "OCl" => AstralTargetCategory.OpenCluster, "GCl" => AstralTargetCategory.GlobularCluster,
        "G" or "GPair" or "GTrpl" => AstralTargetCategory.Galaxy, "GGroup" => AstralTargetCategory.GalaxyCluster,
        "PN" => AstralTargetCategory.PlanetaryNebula, "EmN" or "HII" => AstralTargetCategory.EmissionNebula,
        "RfN" => AstralTargetCategory.ReflectionNebula, "DrkN" => AstralTargetCategory.DarkNebula,
        "SNR" => AstralTargetCategory.SupernovaRemnant, "Cl+N" => AstralTargetCategory.Nebula,
        "Neb" => AstralTargetCategory.Nebula, _ => AstralTargetCategory.Other
    };

    private static string FullConstellation(string abbreviation) => ConstellationNames.TryGetValue(abbreviation, out var name) ? name : abbreviation;
    private static readonly IReadOnlyDictionary<string, string> ConstellationNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["And"]="Andromeda", ["Ant"]="Antlia", ["Aps"]="Apus", ["Aqr"]="Aquarius", ["Aql"]="Aquila", ["Ara"]="Ara", ["Ari"]="Aries", ["Aur"]="Auriga",
        ["Boo"]="Boötes", ["Cae"]="Caelum", ["Cam"]="Camelopardalis", ["Cnc"]="Cancer", ["CVn"]="Canes Venatici", ["CMa"]="Canis Major", ["CMi"]="Canis Minor",
        ["Cap"]="Capricornus", ["Car"]="Carina", ["Cas"]="Cassiopeia", ["Cen"]="Centaurus", ["Cep"]="Cepheus", ["Cet"]="Cetus", ["Cha"]="Chamaeleon", ["Cir"]="Circinus",
        ["Col"]="Columba", ["Com"]="Coma Berenices", ["CrA"]="Corona Australis", ["CrB"]="Corona Borealis", ["Crv"]="Corvus", ["Crt"]="Crater", ["Cru"]="Crux", ["Cyg"]="Cygnus",
        ["Del"]="Delphinus", ["Dor"]="Dorado", ["Dra"]="Draco", ["Equ"]="Equuleus", ["Eri"]="Eridanus", ["For"]="Fornax", ["Gem"]="Gemini", ["Gru"]="Grus",
        ["Her"]="Hercules", ["Hor"]="Horologium", ["Hya"]="Hydra", ["Hyi"]="Hydrus", ["Ind"]="Indus", ["Lac"]="Lacerta", ["Leo"]="Leo", ["LMi"]="Leo Minor",
        ["Lep"]="Lepus", ["Lib"]="Libra", ["Lup"]="Lupus", ["Lyn"]="Lynx", ["Lyr"]="Lyra", ["Men"]="Mensa", ["Mic"]="Microscopium", ["Mon"]="Monoceros",
        ["Mus"]="Musca", ["Nor"]="Norma", ["Oct"]="Octans", ["Oph"]="Ophiuchus", ["Ori"]="Orion", ["Pav"]="Pavo", ["Peg"]="Pegasus", ["Per"]="Perseus",
        ["Phe"]="Phoenix", ["Pic"]="Pictor", ["Psc"]="Pisces", ["PsA"]="Piscis Austrinus", ["Pup"]="Puppis", ["Pyx"]="Pyxis", ["Ret"]="Reticulum", ["Sge"]="Sagitta",
        ["Sgr"]="Sagittarius", ["Sco"]="Scorpius", ["Scl"]="Sculptor", ["Sct"]="Scutum", ["Ser"]="Serpens", ["Se1"]="Serpens Caput", ["Se2"]="Serpens Cauda", ["Sex"]="Sextans", ["Tau"]="Taurus", ["Tel"]="Telescopium",
        ["Tri"]="Triangulum", ["TrA"]="Triangulum Australe", ["Tuc"]="Tucana", ["UMa"]="Ursa Major", ["UMi"]="Ursa Minor", ["Vel"]="Vela", ["Vir"]="Virgo", ["Vol"]="Volans", ["Vul"]="Vulpecula"
    };

    private static AstralTarget Sun() => new("sun", "Sun", AstralTargetCategory.Solar, null, null, "OfDate", PrimaryIdentifier: "Sun", Aliases: ["Sol"], Source: "Astronomy Engine solar-system ephemeris");
    private static AstralTarget Moon() => new("moon", "Moon", AstralTargetCategory.Lunar, null, null, "OfDate", PrimaryIdentifier: "Moon", Aliases: ["Luna"], Source: "Astronomy Engine solar-system ephemeris");

    private static void Validate(IReadOnlyList<AstralTarget> targets)
    {
        var duplicate = targets.GroupBy(target => target.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Duplicate OpenNGC identifier '{duplicate.Key}'.");
        foreach (var target in targets.Where(target => !target.IsSun && !target.IsMoon))
        {
            if (target.RightAscensionHours is not >= 0 or >= 24) throw new InvalidDataException($"OpenNGC target '{target.Id}' has invalid J2000 right ascension.");
            if (target.DeclinationDegrees is not >= -90 or > 90) throw new InvalidDataException($"OpenNGC target '{target.Id}' has invalid J2000 declination.");
        }
    }
}
