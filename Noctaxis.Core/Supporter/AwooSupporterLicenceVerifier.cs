using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Noctaxis.Core.Supporter;

/// <summary>Offline AWOO1 verification. The original encoded payload is authenticated before parsing.</summary>
public sealed class AwooSupporterLicenceVerifier : IAwooSupporterLicenceVerifier
{
    private readonly IReadOnlyDictionary<string, Ed25519PublicKeyParameters> _keys;
    public AwooSupporterLicenceVerifier() : this(AwooTrustedKeyStore.CreateDefault()) { }
    public AwooSupporterLicenceVerifier(AwooTrustedKeyStore keys) => _keys = keys.Keys.ToDictionary(
        key => key.KeyId,
        key => key.Algorithm == "Ed25519" && PublicKeyFactory.CreateKey(key.SubjectPublicKeyInfo.ToArray()) is Ed25519PublicKeyParameters ed
            ? ed : throw new ArgumentException("Awoo trusted keys must be Ed25519 SPKI public keys.", nameof(keys)),
        StringComparer.Ordinal);

    public SupporterLicenceResult Verify(string licenceCode)
    {
        var code = licenceCode?.Trim() ?? string.Empty;
        if (code.Length > 512 || code.Any(c => c > 127)) return Result(SupporterLicenceVerificationStatus.Malformed);
        var parts = code.Split('.');
        if (parts.Length != 4) return Result(SupporterLicenceVerificationStatus.Malformed);
        if (parts[0] != "AWOO1")
            return Result(parts[0].StartsWith("AWOO", StringComparison.Ordinal) && parts[0].Length > 4 &&
                parts[0][4..].All(char.IsAsciiDigit) ? SupporterLicenceVerificationStatus.UnsupportedVersion : SupporterLicenceVerificationStatus.Malformed);
        var kid = parts[1];
        if (kid.Length is < 1 or > 32 || !kid.All(IsUrlCharacter)) return Result(SupporterLicenceVerificationStatus.Malformed);
        if (!_keys.TryGetValue(kid, out var key)) return Result(SupporterLicenceVerificationStatus.UnknownKey);
        if (!IsCanonicalBase64Url(parts[2]) || !IsCanonicalBase64Url(parts[3])) return Result(SupporterLicenceVerificationStatus.Malformed);
        var signature = Decode(parts[3]);
        if (signature.Length != 64) return Result(SupporterLicenceVerificationStatus.Malformed);
        var signed = Encoding.ASCII.GetBytes(code[..code.LastIndexOf('.')]);
        var verifier = new Ed25519Signer();
        verifier.Init(false, key);
        verifier.BlockUpdate(signed, 0, signed.Length);
        if (!verifier.VerifySignature(signature)) return Result(SupporterLicenceVerificationStatus.Invalid);

        try
        {
            var json = new UTF8Encoding(false, true).GetString(Decode(parts[2]));
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Result(SupporterLicenceVerificationStatus.Malformed);
            var fields = root.EnumerateObject().Select(p => p.Name).ToArray();
            if (fields.Length != 5 || !fields.ToHashSet(StringComparer.Ordinal).SetEquals(["v", "iss", "ent", "iat", "lid"]))
                return Result(SupporterLicenceVerificationStatus.Malformed);
            var v = root.GetProperty("v");
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version)) return Result(SupporterLicenceVerificationStatus.Malformed);
            if (version != 1) return Result(SupporterLicenceVerificationStatus.UnsupportedVersion);
            var iss = root.GetProperty("iss"); var ent = root.GetProperty("ent"); var lid = root.GetProperty("lid");
            if (iss.ValueKind != JsonValueKind.String || ent.ValueKind != JsonValueKind.String || lid.ValueKind != JsonValueKind.String)
                return Result(SupporterLicenceVerificationStatus.Malformed);
            if (iss.GetString() != "awoo.ltd" || ent.GetString() != AwooSupporterEntitlements.Supporter) return Result(SupporterLicenceVerificationStatus.Invalid);
            var iat = root.GetProperty("iat");
            if (iat.ValueKind != JsonValueKind.Number || !iat.TryGetInt64(out var seconds) || seconds < 0 || seconds > 9007199254740991L)
                return Result(SupporterLicenceVerificationStatus.Malformed);
            var id = lid.GetString()!;
            if (!Guid.TryParseExact(id, "D", out _) || id.Length != 36 || id[14] != '4' || !"89abAB".Contains(id[19]))
                return Result(SupporterLicenceVerificationStatus.Malformed);
            // v1 allows safe integers beyond DateTimeOffset's representable range; issuance is informational.
            DateTimeOffset? issued = seconds <= 253402300799L ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
            return new(SupporterLicenceVerificationStatus.Valid, AwooSupporterEntitlements.Supporter, 1, kid, issued);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        { return Result(SupporterLicenceVerificationStatus.Malformed); }
    }

    private static SupporterLicenceResult Result(SupporterLicenceVerificationStatus status) => new(status);
    private static bool IsUrlCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';
    private static bool IsCanonicalBase64Url(string value)
    {
        if (value.Length == 0 || value.Length % 4 == 1 || !value.All(IsUrlCharacter)) return false;
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var last = alphabet.IndexOf(value[^1]);
        return (value.Length % 4) switch { 2 => (last & 15) == 0, 3 => (last & 3) == 0, _ => true };
    }
    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
}
