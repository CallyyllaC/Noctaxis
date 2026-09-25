namespace Noctaxis.Core.Supporter;

public static class AwooSupporterEntitlements
{
    public const string Supporter = "awoo.supporter";
    public const string CurrentKeyId = "awoo-2026-01";
}

public enum SupporterLicenceVerificationStatus
{
    Valid,
    Invalid,
    UnsupportedVersion,
    UnknownKey,
    Malformed,
    ProtocolPending
}

public sealed record SupporterLicenceResult(
    SupporterLicenceVerificationStatus Status,
    string? Entitlement = null,
    int? Version = null,
    string? KeyId = null,
    DateTimeOffset? IssuedAt = null)
{
    public bool IsAwooSupporter => Status == SupporterLicenceVerificationStatus.Valid &&
                                   string.Equals(Entitlement, AwooSupporterEntitlements.Supporter, StringComparison.Ordinal);
}

public interface IAwooSupporterLicenceVerifier
{
    SupporterLicenceResult Verify(string licenceCode);
}

public sealed class AwooSupporterEntitlement
{
    public bool HasAwooSupporterEntitlement { get; private set; }
    public event EventHandler? Changed;

    public void Set(bool value)
    {
        if (HasAwooSupporterEntitlement == value) return;
        HasAwooSupporterEntitlement = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
