using System.Text;
using Noctaxis.Core.Supporter;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Noctaxis.Desktop.Tests;

// Official frozen protocol fixture. This key is never part of production trust.
internal static class AwooV1Fixture
{
    internal const string Payload = "{\"v\":1,\"iss\":\"awoo.ltd\",\"ent\":\"awoo.supporter\",\"iat\":1789308000,\"lid\":\"00000000-0000-4000-8000-000000000001\"}";
    internal const string Licence = "AWOO1.awoo-test-1.eyJ2IjoxLCJpc3MiOiJhd29vLmx0ZCIsImVudCI6ImF3b28uc3VwcG9ydGVyIiwiaWF0IjoxNzg5MzA4MDAwLCJsaWQiOiIwMDAwMDAwMC0wMDAwLTQwMDAtODAwMC0wMDAwMDAwMDAwMDEifQ.9bKZiSdSPkVXQ2lk_3y2YBkzOQvqviMiNKZqLJmJ65uQ0L4y5Sgh-DF5UWL67bJGvI5LngVKvlooiImDaY4pAQ";
    internal static AwooSupporterLicenceVerifier Verifier() => new(new AwooTrustedKeyStore([
        new("awoo-test-1", "Ed25519", Convert.FromBase64String("MCowBQYDK2VwAyEAA6EHv/POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg=")),
        new("test-alias", "Ed25519", Convert.FromBase64String("MCowBQYDK2VwAyEAA6EHv/POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg="))]));
    internal static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static string Sign(string json) => SignBytes(Encoding.UTF8.GetBytes(json));
    internal static string SignBytes(byte[] payload)
    {
        // Frozen TEST ONLY seed from the specification's PKCS#8 key.
        var key = new Ed25519PrivateKeyParameters(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), 0);
        var signer = new Ed25519Signer(); signer.Init(true, key);
        var input = "AWOO1.awoo-test-1." + Encode(payload);
        var bytes = Encoding.ASCII.GetBytes(input); signer.BlockUpdate(bytes, 0, bytes.Length);
        return input + "." + Encode(signer.GenerateSignature());
    }
}

public sealed class AwooProtocolTests
{
    [Fact] public void FrozenVectorVerifiesExactly()
    {
        Assert.Equal(AwooV1Fixture.Licence, AwooV1Fixture.Sign(AwooV1Fixture.Payload));
        var result = AwooV1Fixture.Verifier().Verify(" \r\n" + AwooV1Fixture.Licence + "\t");
        Assert.True(result.IsAwooSupporter); Assert.Equal(1, result.Version); Assert.Equal("awoo-test-1", result.KeyId);
        Assert.Equal(1789308000, result.IssuedAt!.Value.ToUnixTimeSeconds());
    }
    [Fact] public void ProductionTrustExcludesTestKeysAndPrivateMaterial()
    {
        var keys = AwooTrustedKeyStore.CreateDefault();
        Assert.Equal("awoo-2026-01", Assert.Single(keys.Keys).KeyId);
        Assert.False(keys.TryGet("awoo-test-1", out _));
        Assert.Equal(SupporterLicenceVerificationStatus.UnknownKey, new AwooSupporterLicenceVerifier().Verify(AwooV1Fixture.Licence).Status);
        var assembly = typeof(AwooTrustedKeyStore).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            Assert.DoesNotContain("PRIVATE KEY", reader.ReadToEnd());
        }
        var root = Path.GetDirectoryName(Path.GetDirectoryName(TestPaths.MainWindowMarkup))!;
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
            Assert.DoesNotContain("BEGIN PRIVATE KEY", File.ReadAllText(path));
    }
    public static IEnumerable<object[]> BadEnvelopes()
    {
        var parts = AwooV1Fixture.Licence.Split('.');
        yield return ["awoo1." + string.Join('.', parts.Skip(1)), SupporterLicenceVerificationStatus.Malformed];
        yield return [AwooV1Fixture.Licence.Replace("AWOO1", "AWOO2"), SupporterLicenceVerificationStatus.UnsupportedVersion];
        yield return [AwooV1Fixture.Licence.Replace("awoo-test-1", "unknown"), SupporterLicenceVerificationStatus.UnknownKey];
        yield return [AwooV1Fixture.Licence.Replace("awoo-test-1", "test-alias"), SupporterLicenceVerificationStatus.Invalid];
        yield return [AwooV1Fixture.Licence.Replace("awoo-test-1", "bad kid"), SupporterLicenceVerificationStatus.Malformed];
        yield return [string.Join('.', parts.Take(3)), SupporterLicenceVerificationStatus.Malformed];
        yield return [AwooV1Fixture.Licence + ".extra", SupporterLicenceVerificationStatus.Malformed];
        yield return [new string('A', 513), SupporterLicenceVerificationStatus.Malformed];
        yield return [AwooV1Fixture.Licence.Replace("awoo-test-1", "é"), SupporterLicenceVerificationStatus.Malformed];
        foreach (var value in new[] { "", "A", "AB", "AAB", "AA=", "AA+", "AA/", "AA\n" })
            foreach (var index in new[] { 2, 3 })
            { var copy = parts.ToArray(); copy[index] = value; yield return [string.Join('.', copy), SupporterLicenceVerificationStatus.Malformed]; }
        yield return [string.Join('.', parts.Take(3)) + ".AA", SupporterLicenceVerificationStatus.Malformed];
        yield return [AwooV1Fixture.Licence[..^1] + "R", SupporterLicenceVerificationStatus.Malformed];
        yield return [string.Join('.', parts.Take(3)) + ".A" + parts[3][1..], SupporterLicenceVerificationStatus.Invalid];
        yield return ["AWOO1.awoo-test-1.bm90IGpzb24." + parts[3], SupporterLicenceVerificationStatus.Invalid];
    }
    [Theory] [MemberData(nameof(BadEnvelopes))]
    public void EnvelopeMutationsFailClosed(string code, SupporterLicenceVerificationStatus status) => Assert.Equal(status, AwooV1Fixture.Verifier().Verify(code).Status);

    [Theory]
    [InlineData("\"v\":1", "\"v\":2", SupporterLicenceVerificationStatus.UnsupportedVersion)]
    [InlineData("\"v\":1", "\"v\":1.0", SupporterLicenceVerificationStatus.Malformed)]
    [InlineData("awoo.ltd", "wrong", SupporterLicenceVerificationStatus.Invalid)]
    [InlineData("awoo.supporter", "wrong", SupporterLicenceVerificationStatus.Invalid)]
    [InlineData("1789308000", "-1", SupporterLicenceVerificationStatus.Malformed)]
    [InlineData("1789308000", "1.5", SupporterLicenceVerificationStatus.Malformed)]
    [InlineData("1789308000", "1e2", SupporterLicenceVerificationStatus.Malformed)]
    [InlineData("1789308000", "9007199254740992", SupporterLicenceVerificationStatus.Malformed)]
    [InlineData("1789308000", "\"1\"", SupporterLicenceVerificationStatus.Malformed)]
    [InlineData("00000000-0000-4000-8000-000000000001", "invalid", SupporterLicenceVerificationStatus.Malformed)]
    [InlineData("4000-8000", "1000-8000", SupporterLicenceVerificationStatus.Malformed)]
    [InlineData("4000-8000", "4000-7000", SupporterLicenceVerificationStatus.Malformed)]
    [InlineData("\"v\":1,", "", SupporterLicenceVerificationStatus.Malformed)]
    [InlineData("\"v\":1,", "\"v\":1,\"extra\":0,", SupporterLicenceVerificationStatus.Malformed)]
    [InlineData("\"v\":1,", "\"v\":1,\"v\":1,", SupporterLicenceVerificationStatus.Malformed)]
    [InlineData("\"iss\":\"awoo.ltd\"", "\"iss\":null", SupporterLicenceVerificationStatus.Malformed)]
    public void SignedPayloadValidation(string before, string after, SupporterLicenceVerificationStatus expected) =>
        Assert.Equal(expected, AwooV1Fixture.Verifier().Verify(AwooV1Fixture.Sign(AwooV1Fixture.Payload.Replace(before, after))).Status);
    [Theory] [InlineData("null")] [InlineData("[]")] [InlineData("{")] [InlineData("{}")]
    public void SignedMalformedJsonFails(string json) => Assert.Equal(SupporterLicenceVerificationStatus.Malformed, AwooV1Fixture.Verifier().Verify(AwooV1Fixture.Sign(json)).Status);
    [Fact] public void InvalidUtf8FailsAfterAuthentication() => Assert.Equal(SupporterLicenceVerificationStatus.Malformed,
        AwooV1Fixture.Verifier().Verify(AwooV1Fixture.SignBytes([0xff])).Status);
    [Theory] [InlineData("0")] [InlineData("9007199254740991")]
    public void PermanentLicenceAcceptsSafeIssuanceRange(string seconds) => Assert.True(AwooV1Fixture.Verifier().Verify(AwooV1Fixture.Sign(AwooV1Fixture.Payload.Replace("1789308000", seconds))).IsAwooSupporter);
    [Fact] public void OriginalEncodedJsonIsSignedWithoutCanonicalising() => Assert.True(AwooV1Fixture.Verifier().Verify(AwooV1Fixture.Sign(" \n" + AwooV1Fixture.Payload.Replace(",", ", ") + "\n")).IsAwooSupporter);
}
