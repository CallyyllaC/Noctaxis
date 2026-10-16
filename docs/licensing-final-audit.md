# Noctaxis licence and supporter-system final audit

Audit date: 9 October 2026. The current local working tree is authoritative.
This confirms the Awoo Licence v2.0 framework and five-theme supporter system;
it does not restart the third-party dependency audit. No substantive licence
terms or application behaviour were changed.

## Authoritative arrangement

| File | Role |
| --- | --- |
| [LICENSE](../LICENSE) | Sole operative project licence, Awoo Licence v2.0; generic text unchanged. |
| [AWOO-SUPPORTER-SYSTEM.md](../AWOO-SUPPORTER-SYSTEM.md) | Initial public Noctaxis supporter definition, revision 1 dated 9 October 2026; five specific cosmetic features. |
| [COMMERCIAL-LICENSING.md](../COMMERCIAL-LICENSING.md) | Practical commercial-use explanation and `licensing@awoo.ltd`; written grants remain separate. |
| [CONTRIBUTING.md](../CONTRIBUTING.md) | Visibility of section 14 before intentional submissions; no copyright assignment implied. |
| [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md) and [ThirdParty/](../Noctaxis.Desktop/ThirdParty/README.md) | Separate third-party terms, permission records and retained original notices. |
| [Licensing guide](licensing.md) | Maintainer explanation and reusable supporter template; it cannot change LICENSE. |

CallyyllaC is the current Noctaxis copyright holder/Licensor. Awoo.ltd identifies
the site and infrastructure, not a limited company. The licence remains custom
source-available, with ordinary professional tool use permitted and software
commercialisation requiring separate written permission. Supporter status
conveys only defined feature entitlement, never commercial permission.

## Reference classification and cleanup

The developer confirms that Noctaxis has never had a public release under
Awoo Licence v1.0. Local Git history contains no committed version of the
licence/definition/archive files and no local release tags. Those observations
support the draft classification; they cannot establish what unknown private
recipients or forks possess. Removing drafts does not revoke rights already
granted to any recipient. Git history was not rewritten.

The initial scan recorded 17 exact old-licence/archive/migration references in
eight source documentation files. The bare version comparison in the maintainer
guide was also removed. Their dispositions are:

| Before-cleanup file and locations | Classification / disposition |
| --- | --- |
| LICENSE, lines 11 and 522 | Retained: exact-version separation clauses in the authoritative reusable v2.0 text. They grant no old-version option. Changing them would change the supplied legal text unnecessarily. |
| docs/licences/Awoo-Licence-v1.0.txt, heading | Removed the obsolete draft copy. |
| docs/licences/Noctaxis-Supporter-System-v1.txt, lines 4 and 7 | Removed the obsolete four-theme draft definition. |
| docs/licence-v2-review.md, archive links and migration/validation narrative | Removed the obsolete adoption report. Current substantive review questions are below. |
| docs/licensing.md, opening archive links, old comparison and publication-review link | Replaced with the current operative arrangement and final audit; no fictitious earlier public-release migration. |
| docs/windows-redistribution-audit.md, opening migration links and old source-licence conclusions | Removed the old licence-version narrative; retained only the concrete internal binary/notice evidence with its original scope. |
| THIRD_PARTY_NOTICES.md, adoption-report link and old audit presentation | Linked this confirmation and labelled the earlier binary conclusions as prior internal evidence. Third-party terms and the new Lorenz permission record remain intact. |
| docs/branding.md, adoption-report link | Linked the current manual-review question. |

Other appearances of product `v1.0` in ROADMAP.md and docs/releasing.md describe
Noctaxis release planning, not Awoo licensing, and were retained. `AWOO1`, wire
protocol v1, test fixtures, schema identifiers and `noctaxis_galaxy_style_v1.json`
are independent technical versions and were retained. Tests may refer to old
paths as negative regression checks; they do not supply an operative licence.
No old operative licence URL, conflicting package licence metadata, wiki file
or CI licence version was found in the current source.

Ignored generated copies remain in these internal output locations:

- `artifacts/licensing-win-x64-20261003/publish/`: its LICENSE, supporter definition
  and root notice describe the earlier internal inspection.
- `.test-artifacts/xplat/publish-win-x64/`, `publish-linux-x64/`,
  `publish-osx-x64/` and `publish-osx-arm64/`: generated licence files and root
  notices contain the reusable licence's version-separation text and/or stale
  migration prose.

These are not current alpha packages or source licence authorities. They are
retained as generated inspection/test evidence, not frozen public-release
licence copies. This audit does not rewrite those old binaries or their terms.
Use a fresh package and the current validation guards for release. The retained
[3 October native audit](windows-redistribution-audit.md) and
[binary manifest](windows-redistribution-artifact.json) have a concrete purpose:
they explain the component-specific notices already supplied. Their hashes and
findings have not been represented as a new dependency sweep or current build.

## Five-theme confirmation

| Family | Identifier | Ordinary alternative |
| --- | --- | --- |
| Noctaxis Neon | `supporter.noctaxis-neon` | Noctaxis / High Contrast, appearance modes, colour-vision controls and text scale. |
| Foxfire | `supporter.foxfire` | Same ordinary alternatives. |
| Deep Space | `supporter.deep-space` | Same ordinary alternatives. |
| Moonlit | `supporter.moonlit` | Same ordinary alternatives. |
| Pigeon | `supporter.pigeon` | Same ordinary alternatives. |

The runtime catalogue registers exactly these five supporter families and two
ungated standard families. ThemeService passes one effective entitlement to
ThemeCatalogue; the predicate leaves standard families available and an
unavailable supporter choice falls back to Noctaxis. No additional tier or
requester privilege exists. Entitlement use is confined to verification/state
and cosmetic theme availability, not planning, export, accessibility or other
ordinary workflows.

The offline `AWOO1` envelope, issuer `awoo.ltd`, entitlement `awoo.supporter` and
production key `awoo-2026-01` remain intact. Test keys/verifiers are injected
only in tests; the force-unlicensed development option only suppresses access.

The definition was reset from internal revision 3 to initial public revision 1,
independently of licence v2.0 and protocol v1. All five per-feature fields remain;
the earlier four feature sections and protected scopes are unchanged. Pigeon's
protected scope expressly excludes generic colours, styles and the pigeon
concept. Its specific authored palettes and deliberate entitlement circumvention
remain the protected feature. Stale four-family picker wording was corrected.

## Pigeon ownership and acknowledgement

The developer's stated facts are a concept request by the first supporter and
design/implementation by CallyyllaC. The definition now separates that request
from copyrightable code/artwork, ownership of the implementation, supporter
access and voluntary dedication. It grants no requester ownership, licensing
authority, approval/control rights or exclusivity, and creates no personal
licence agreement. All official supporters receive the same entitlement.

This does not adjudicate hypothetical disputed authorship or an undisclosed
agreement. Actual code, artwork or palette submissions would need their own
rights assessment. The distinction is consistent with official guidance that
copyright protects creative work rather than an idea, and that commissioning
alone does not transfer copyright:
[UK copyright guidance](https://www.business.gov.uk/support/intellectual-property/using-copyright-protect-creative-work/),
[IPO ownership guidance](https://www.gov.uk/guidance/ownership-of-copyright-works).

## Packaging and automated checks

Both package scripts check the v2.0 heading, byte-identical copies of root
LICENSE, the supporter definition and the root third-party notice, every file
in the retained third-party collection, and the OpenNGC licence. The existing
MSBuild content copies remain; no project-file changes were necessary. The
package workflow now includes changes to these licensing inputs in its PR
path filters. Platform READMEs identify the correct licence locations.

New LicensingTests check build-output terms/notices, equality between declared
and registered supporter features, ordinary theme availability, and absence of
obsolete draft links from operative documents. Existing Pigeon, protocol,
storage and live headless activation/replacement tests cover current behaviour.
Tests do not assert that Git history never contained an older licence.

Validation completed against the current tree:

- 99 focused Desktop tests passed, zero failed/skipped, including PackagingTests,
  new LicensingTests, the five-theme matrix, Pigeon, protocol, storage and signed
  activation/replacement workflow. Core and Desktop were built as dependencies.
- The real Windows validator passed current fixture files and rejected a wrong
  licence version, stale supporter definition and changed third-party notice.
- PowerShell parsing and Bash syntax checks passed. Linux/macOS packages were
  not executed on their native platforms in this confirmation.
- A fresh self-contained Release/win-x64 package was built using package.ps1:
  `artifacts/awoo-final-20261009/packages/Noctaxis-0.1.0-win-x64.zip`.
  Its 62 licence/notice/inventory files and Windows README match repository
  sources byte for byte. It includes the v2.0 text, all five declared themes and
  self-contained runtime; obsolete draft files and test assemblies are absent.
- Archive SHA-256:
  `e0f55270d325647b4cf9ba03f1b9e600e75b62ceadd8809289c73b35285a26fb`.
- The current-pass baseline confirms unchanged LICENSE, runtime palette/gate/
  protocol implementation, third-party licence texts and Desktop project file.
  Reconstructing the definition's metadata/Pigeon wording edits reproduces its
  original hash, confirming the earlier four sections are unchanged.
- 107 local links in the checked changed documentation resolved; final added
  file links were checked again after completion. `git diff --check` passed.

The 99-test run supersedes the initial overlapping 91-test selection; they are
not additive. No new live visual/accessibility certification or comprehensive
third-party compatibility sweep is claimed. Concurrent unrelated source work
was preserved. Nothing was committed, pushed or published.

## Manual review

- Section 12 still leaves a scope question about unchanged unofficial
  redistribution retaining Noctaxis logos. Section 2 permits redistribution;
  section 12 limits branding reuse. This concerns fork presentation, not the
  developer's ability to issue the official alpha. No branding permission or
  asset exclusion has been invented to settle it.
- Section 14's broad contribution/relicensing grant requires actual authority
  and visible submission terms. It cannot manufacture rights in prior work,
  employer-owned submissions or third-party material. Pigeon's concept-only
  request is not treated as such a copyright grant.
- Future supporter features must satisfy v2.0's whole-project usability,
  anti-degradation, compatibility/accessibility/security and shared-system
  requirements. No new feature category is inferred from the five themes.

Third-party/provider rights remain separate. The Lorenz permission granted on
8 October 2026 is preserved in the existing notices. This confirmation supplies
no additional provider rights or general third-party compatibility verdict.

## Files changed by this audit

Removed:

- `docs/licences/Awoo-Licence-v1.0.txt`
- `docs/licences/Noctaxis-Supporter-System-v1.txt`
- `docs/licence-v2-review.md`

Edited:

- `AWOO-SUPPORTER-SYSTEM.md`
- `docs/licensing.md`
- `docs/supporter-themes.md`
- `docs/supporter-licensing.md`
- `docs/branding.md`
- `docs/windows-redistribution-audit.md`
- `THIRD_PARTY_NOTICES.md` (licensing/audit references only; existing notices and
  provider permission records retained)
- `docs/releasing.md`
- `scripts/package.ps1`
- `scripts/package.sh`
- `.github/workflows/package.yml`
- `packaging/windows/README.txt`
- `packaging/linux/README.txt`
- `packaging/macos/README.txt`

Created:

- `Noctaxis.Desktop.Tests/LicensingTests.cs`
- `docs/licensing-final-audit.md`

The new Windows ZIP and focused-test TRX files are ignored local validation
outputs. Public README/user-guide version and five-theme references were already
correct and required no changes. The legal text, commercial guide, contribution
guide, application code and existing third-party licence files were not edited
by this audit.
