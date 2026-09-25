# Awoo supporter licence v1 — implemented

Noctaxis implements the frozen Awoo_Supporter_Licence_v1.md through the existing synchronous IAwooSupporterLicenceVerifier boundary. Production startup and activation use AwooSupporterLicenceVerifier. The pending implementation is removed; the historical ProtocolPending enum value remains compatible but is never returned by the real verifier.

## Protocol and cryptography

The envelope is exactly `AWOO1.<kid>.<payload>.<signature>`, at most 512 ASCII characters after trimming outer whitespace. Internal characters remain unchanged. Key identifiers match `[A-Za-z0-9_-]{1,32}` and resolve by exact ordinal lookup. Unknown keys return UnknownKey; keys are never tried sequentially.

Both encoded segments must be canonical unpadded RFC 4648 Base64URL. Alphabet, length and unused final bits are checked before decoding. Padding, standard Base64 characters, empty segments and alternative spellings are rejected. The signature must decode to 64 bytes.

Ed25519 uses BouncyCastle.Cryptography 2.7.0, a managed cross-platform dependency. The installed .NET 10 reference API exposes no standalone Ed25519 verifier. No cryptography is implemented manually. Signed input is the original ASCII `AWOO1.<kid>.<payload>` with no newline or reserialization. Only after signature success is the payload decoded with strict UTF-8 and parsed as JSON.

The JSON object must contain exactly five unique fields: v, iss, ent, iat and lid. Version is integer 1, issuer is awoo.ltd, entitlement is awoo.supporter. Issuance is an integer from 0 through 9007199254740991 inclusive, informational and never expiry. Values beyond DateTimeOffset's range remain valid with optional IssuedAt metadata null. Licence id must be UUIDv4 with the RFC UUID variant. Additional and duplicate fields are rejected.

Malformed encodings, shapes, UTF-8, timestamps and UUIDs return Malformed. Unsupported numeric AWOO prefixes or signed payload versions return UnsupportedVersion. Signature failures and incorrect signed issuer/entitlement return Invalid. UI messages expose friendly result categories only.

## Production trust and privacy

Production trust begins with awoo-2026-01, embedded at `Noctaxis.Core/Data/Awoo/awoo-support-public.pem`. This is the supplied Ed25519 SPKI public key, byte-identical to the original. The exact SPKI structure is validated. Additional key ids can be registered while retaining old keys for permanent licences.

No production private key is present. The official awoo-test-1 public key and test signing seed exist only in the test project, in a separately injected store. Production rejects the official test licence as UnknownKey. No test key or force-licensed path is shipped in runtime trust.

Licences are permanent global Awoo entitlements. There is no email, device identifier, application binding, expiry, account or online validation. Verification makes no network request. Noctaxis neither sends nor logs the complete licence or signature. The existing local state.json stores it as Settings.AwooSupporterLicenceCode. The normal Windows directory is %APPDATA%/Noctaxis; Linux uses the existing platform path provider.

## Workflow

Startup verifies stored codes locally; missing/invalid codes leave themes locked. Activate verifies before storing, trims only outer whitespace, clears the editor and immediately updates status and availability. The valid UI displays `✓ Supporter licence active`, with Replace licence and Remove licence. Invalid replacement retains the previous active code and entitlement.

Removal clears the stored licence and derived entitlement. The requested supporter appearance remains persisted while the effective palette falls back to builtin.noctaxis; re-entitlement restores it. The live picker keeps all four supporter families visible and disables unavailable choices. Its availability refresh uses the existing appearance guard to prevent two-way selection notifications from rewriting the requested family.

NOCTAXIS_FORCE_UNLICENSED=1 suppresses effective entitlement even for a valid stored code. It is off by default, not persisted and not exposed in Settings. The code remains stored and valid. Removing the variable and restarting/re-evaluating restores real entitlement. There is no FORCE_LICENSED bypass.

The top-level Support section and Ko-fi button remain. Ko-fi opens https://ko-fi.com/callyyllac in the default browser and has no effect on licence state or verification. Licence/appearance changes use the existing persistence path, trigger zero Planner/terrain/weather/astronomy calculations and preserve user-owned framing/terrain colours.

## Validation

The official frozen licence verifies byte-for-byte with its test key. Signing the official payload with the documented test seed reproduces the complete vector. Mutated prefix, kid, payload and signature cases fail. Tests cover canonical encoding, malformed/semantically invalid signed payloads, safe integer boundaries, exact key lookup and production/test separation.

The real verifier drives automated Settings activation, all four theme selections, invalid replacement, restart, removal, fallback and re-entitlement. A real JsonUserDataStore test verifies disk persistence, restart, removal and absence of licence/signature in store logs. Request counters remain unchanged. Eight headless captures under artifacts/awoo-v1 document unlicensed, activated, each supporter family, invalid replacement and removal.

Focused and final sequential Release results are recorded in the final task report and artifacts/awoo-v1-*.log. Native Windows/Linux interactive and screen-reader validation are not claimed: tests use the production Avalonia window headlessly with an injected test-only key store. No production-signed licence was supplied; production key identity/trust exclusion are tested separately from the official vector.

Final validation (2026-09-13): protocol-only 55 passed; combined licence/supporter/Settings/Ko-fi selection 80 passed; appearance/Settings regression selection 201 passed. These selections overlap and must not be summed. Sequential Release command: `dotnet test Noctaxis.slnx -c Release --no-restore -m:1 -p:TestTfmsInParallel=false -p:OutDir=bin/Release/awoo-v1/`. Core: 331 passed, 1 skipped. Desktop: 661 passed. Solution: 992 passed, 0 failed, 1 skipped (993 total). The sole skip is the opt-in official live Terrarium integration test. `git diff --check` passed with line-ending conversion notices only.
