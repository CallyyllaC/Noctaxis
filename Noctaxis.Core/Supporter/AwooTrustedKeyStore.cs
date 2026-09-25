using System.Reflection;

namespace Noctaxis.Core.Supporter;

public sealed record AwooTrustedPublicKey(string KeyId, string Algorithm, ReadOnlyMemory<byte> SubjectPublicKeyInfo);

public sealed class AwooTrustedKeyStore
{
    private readonly IReadOnlyDictionary<string, AwooTrustedPublicKey> _keys;

    public AwooTrustedKeyStore(IEnumerable<AwooTrustedPublicKey> keys)
    {
        _keys = keys.ToDictionary(key => key.KeyId, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<AwooTrustedPublicKey> Keys => _keys.Values.ToArray();

    public bool TryGet(string keyId, out AwooTrustedPublicKey key) => _keys.TryGetValue(keyId, out key!);

    public static AwooTrustedKeyStore CreateDefault()
    {
        var assembly = typeof(AwooTrustedKeyStore).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Data.Awoo.awoo-support-public.pem", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Embedded Awoo public key resource is missing.");
        using var reader = new StreamReader(stream);
        var pem = reader.ReadToEnd();
        return new([new AwooTrustedPublicKey(
            AwooSupporterEntitlements.CurrentKeyId,
            "Ed25519",
            ParseSubjectPublicKeyInfo(pem))]);
    }

    private static byte[] ParseSubjectPublicKeyInfo(string pem)
    {
        const string begin = "-----BEGIN PUBLIC KEY-----";
        const string end = "-----END PUBLIC KEY-----";
        var start = pem.IndexOf(begin, StringComparison.Ordinal);
        var finish = pem.IndexOf(end, StringComparison.Ordinal);
        if (start < 0 || finish <= start) throw new InvalidDataException("Awoo public key PEM boundaries are invalid.");
        var encoded = pem[(start + begin.Length)..finish]
            .Where(character => !char.IsWhiteSpace(character)).ToArray();
        byte[] der;
        try { der = Convert.FromBase64String(new string(encoded)); }
        catch (FormatException exception) { throw new InvalidDataException("Awoo public key PEM content is invalid.", exception); }
        // RFC 8410 Ed25519 SPKI: absent algorithm parameters and exactly 32 public-key bytes.
        ReadOnlySpan<byte> header = [0x30, 0x2a, 0x30, 0x05, 0x06, 0x03, 0x2b, 0x65, 0x70, 0x03, 0x21, 0x00];
        if (der.Length != 44 || !der.AsSpan(0, 12).SequenceEqual(header))
            throw new InvalidDataException("Awoo public key is not a usable Ed25519 SubjectPublicKeyInfo.");
        return der;
    }
}
