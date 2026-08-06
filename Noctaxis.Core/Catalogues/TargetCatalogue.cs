using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Catalogues;

public sealed record CatalogueSearchQuery(string Text = "", AstralTargetCategory? ObjectType = null,
    string? Constellation = null, string? CatalogueFamily = null);

public interface ITargetCatalogue
{
    IReadOnlyList<AstralTarget> Targets { get; }
    IReadOnlyList<AstralTargetCategory> ObjectTypes { get; }
    IReadOnlyList<string> Constellations { get; }
    IReadOnlyList<string> CatalogueFamilies { get; }
    string? ResolveId(string id);
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
        var text = Normalise(query.Text);
        if (text.Length is > 0 and < 2) return Task.FromResult<IReadOnlyList<AstralTarget>>([]);
        IEnumerable<AstralTarget> candidates = catalogue.Targets.Where(target => !target.IsSun && !target.IsMoon);
        if (query.ObjectType.HasValue) candidates = candidates.Where(target => target.Category == query.ObjectType);
        if (!string.IsNullOrWhiteSpace(query.Constellation)) candidates = candidates.Where(target => string.Equals(target.Constellation, query.Constellation, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query.CatalogueFamily)) candidates = candidates.Where(target => Identifiers(target).Any(id => CatalogueFamily(id).Equals(query.CatalogueFamily, StringComparison.OrdinalIgnoreCase)));
        if (text.Length >= 2) candidates = candidates.Where(target => SearchTerms(target).Any(term => Normalise(term).Contains(text, StringComparison.Ordinal)));
        IReadOnlyList<AstralTarget> result = candidates.OrderBy(target => target.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Clamp(maximumResults, 1, 50)).ToArray();
        return Task.FromResult(result);
    }

    private static IEnumerable<string> SearchTerms(AstralTarget target) =>
        new[] { target.DisplayName, target.Id, target.PrimaryIdentifier, target.Constellation }
            .Concat(target.Aliases ?? []).Concat(target.CatalogueIdentifiers ?? []).Where(value => !string.IsNullOrWhiteSpace(value))!;
    private static IEnumerable<string> Identifiers(AstralTarget target) =>
        new[] { target.PrimaryIdentifier }.Concat(target.CatalogueIdentifiers ?? []).Where(value => !string.IsNullOrWhiteSpace(value))!;

    internal static string CatalogueFamily(string identifier)
    {
        var compact = Normalise(identifier);
        if (compact.StartsWith("NGC", StringComparison.Ordinal)) return "NGC";
        if (compact.StartsWith("HIP", StringComparison.Ordinal)) return "HIP";
        if (compact.StartsWith("CALDWELL", StringComparison.Ordinal) || compact.StartsWith('C') && compact.Length > 1 && char.IsDigit(compact[1])) return "Caldwell";
        if (compact.StartsWith("IC", StringComparison.Ordinal)) return "IC";
        if (compact.Length > 1 && compact[0] == 'M' && char.IsDigit(compact[1])) return "Messier";
        if (compact.Length > 1 && compact[0] == 'B' && char.IsDigit(compact[1])) return "Barnard";
        return "Other";
    }

    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

/// <summary>Offline catalogue populated from the unmodified OpenNGC CSV resources (CC-BY-SA-4.0).</summary>
public sealed class OpenNgcTargetCatalogue : ITargetCatalogue
{
    public const string Attribution = "OpenNGC · CC BY-SA 4.0";
    private static readonly Regex DesignationPattern = new("^(NGC|IC)(0*)([0-9]+)(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly IReadOnlyDictionary<string, string> LegacyIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["andromeda"] = "NGC0224", ["orion-nebula"] = "NGC1976", ["pleiades"] = "Mel022",
        ["triangulum"] = "NGC0598", ["bodes-galaxy"] = "NGC3031", ["cigar-galaxy"] = "NGC3034",
        ["whirlpool-galaxy"] = "NGC5194", ["sombrero-galaxy"] = "NGC4594", ["lagoon-nebula"] = "NGC6523",
        ["eagle-nebula"] = "NGC6611", ["trifid-nebula"] = "NGC6514", ["dumbbell-nebula"] = "NGC6853",
        ["ring-nebula"] = "NGC6720", ["crab-nebula"] = "NGC1952", ["north-america-nebula"] = "NGC7000",
        ["heart-nebula"] = "IC1805", ["horsehead-nebula"] = "B033", ["beehive-cluster"] = "NGC2632",
        ["double-cluster"] = "NGC0869", ["omega-centauri"] = "NGC5139", ["hercules-cluster"] = "NGC6205",
        ["great-sagittarius-cluster"] = "NGC6656"
    };
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
        CatalogueFamilies = loaded.SelectMany(target => new[] { target.PrimaryIdentifier }.Concat(target.CatalogueIdentifiers ?? []))
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => LocalTargetSearchService.CatalogueFamily(value!))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(family => family != "Other").Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<AstralTarget> Targets => _targets;
    public IReadOnlyList<AstralTargetCategory> ObjectTypes { get; }
    public IReadOnlyList<string> Constellations { get; }
    public IReadOnlyList<string> CatalogueFamilies { get; }

    public string? ResolveId(string id)
    {
        if (_byId.ContainsKey(id)) return id;
        if (LegacyIds.TryGetValue(id, out var oldName) && _openNgcNameToId.TryGetValue(oldName, out var migrated)) return migrated;
        var compact = id.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (_openNgcNameToId.TryGetValue(compact, out var openNgc)) return openNgc;
        return _targets.FirstOrDefault(target => target.CatalogueIdentifiers?.Any(value => value.Equals(id, StringComparison.OrdinalIgnoreCase)) == true)?.Id;
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
            var catalogueIds = new[] { designation, messier, FormatNumbered("NGC", Field("NGC")), FormatNumbered("IC", Field("IC")) }
                .Concat(identifiers).OfType<string>().Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var displayName = commonNames.FirstOrDefault() ?? (messier is not null ? $"{messier} · {designation}" : designation);
            var aliases = commonNames.Skip(1).Concat(identifiers).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
        ["Sgr"]="Sagittarius", ["Sco"]="Scorpius", ["Scl"]="Sculptor", ["Sct"]="Scutum", ["Ser"]="Serpens", ["Sex"]="Sextans", ["Tau"]="Taurus", ["Tel"]="Telescopium",
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

/// <summary>Compatibility name retained for callers compiled against earlier Noctaxis builds; data is OpenNGC-backed.</summary>
public sealed class EmbeddedTargetCatalogue : ITargetCatalogue
{
    private readonly OpenNgcTargetCatalogue _inner = new();
    public IReadOnlyList<AstralTarget> Targets => _inner.Targets;
    public IReadOnlyList<AstralTargetCategory> ObjectTypes => _inner.ObjectTypes;
    public IReadOnlyList<string> Constellations => _inner.Constellations;
    public IReadOnlyList<string> CatalogueFamilies => _inner.CatalogueFamilies;
    public string? ResolveId(string id) => _inner.ResolveId(id);
    public AstralTarget Get(string id) => _inner.Get(id);
}
