# Licensing: maintainer guide

This guide explains how maintainers apply the framework. [Awoo Licence v2.0](../LICENSE) contains the public terms; [AWOO-SUPPORTER-SYSTEM.md](../AWOO-SUPPORTER-SYSTEM.md) defines the Noctaxis supporter system and features. This guide does not replace or expand those terms.

## Operative licence and release context

[LICENSE](../LICENSE) is the sole operative project licence: Awoo Licence v2.0.
The developer confirms that Noctaxis has not had a public release under the
earlier licence. Obsolete draft archives and migration documentation have
therefore been removed. This records the developer's public-release history;
it does not establish what unknown private recipients or forks may hold, or
revoke rights already granted to any recipient.

The [final licence and supporter audit](licensing-final-audit.md) records the
current-source checks and cleanup. Source code, public licence version,
supporter-document revision and signed entitlement protocol are independent.

## Purpose

Awoo Licence v2.0 permits non-commercial use, modification and redistribution, and ordinary professional use as a tool. It reserves commercial exploitation for separate written permission and allows projects to identify an official supporter system with compliant enhancements to a reasonably usable ordinary experience. It also governs derivative identification, branding, optional asset exclusions, contributions, research exceptions and termination/cure.

Describe this as a **custom source-available licence**, not an OSI-approved open-source licence. Restrictions on commercial exploitation differ from the freedoms required by the [Open Source Definition](https://opensource.org/osd).

## Distinguishing the uses

| Use | Treatment |
| --- | --- |
| Non-commercial use, modification and redistribution | Permitted subject to the public licence, including its redistribution and notice obligations. |
| Ordinary professional use as a tool | Permitted. Planning paid photography with Noctaxis does not by itself constitute Commercial Use. Income from a person's work does not itself turn tool use into software exploitation. |
| Voluntary Support | Genuinely optional contributions without functionality, features, services, downloads, updates, access or entitlements in exchange. Credits and similar non-functional recognition are allowed. |
| Official Supporter System | The expressly identified official mechanism may grant defined Supporter Features. This is a specific exception to Commercial Use; it is not Voluntary Support when functionality is provided in exchange. |
| Commercial Use | Selling or monetising the Software or a Derivative Work, or requiring or soliciting consideration for software access, features, hosting, updates, distribution or other entitlements, requires separate written permission, subject to the stated exceptions. |

A fork may accept separate Voluntary Support, even alongside the Official Supporter System. Its own support mechanism must not unlock official Supporter Features or redirect support intended for Noctaxis/Awoo. Calling a payment a donation does not make it Voluntary Support when software benefits are exchanged for it.

## File responsibilities

| File | Responsibility |
| --- | --- |
| [LICENSE](../LICENSE) | Exact v2.0 public terms, including contributions, branding, supporter requirements, research, separate permission and termination. |
| [AWOO-SUPPORTER-SYSTEM.md](../AWOO-SUPPORTER-SYSTEM.md) | Noctaxis's official components and destinations, each protected feature, its Core Functionality, non-supporter implementation and protected scope. |
| `AWOO-ASSETS-DEFINITION.md` (optional, currently absent) | Clearly identifies project assets excluded from the software grant or subject to separate terms. Do not create blanket exclusions without an explicit project decision. |
| [CONTRIBUTING.md](../CONTRIBUTING.md) | Makes section 14 visible before submission; it does not replace that grant or assign copyright. |
| [COMMERCIAL-LICENSING.md](../COMMERCIAL-LICENSING.md) | Examples and the route for requesting separate written permission. It is not a commercial grant. |
| [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md) | Dependency, service and dataset notices and the locations of their separate terms. |
| [docs/supporter-licensing.md](supporter-licensing.md) | The signed supporter licence protocol, trust, storage and activation workflow. Its protocol version is separate from the public licence version. |
| [docs/supporter-themes.md](supporter-themes.md) | Theme implementation history and validation notes. The root supporter definition is the current licensing reference. |

No `PUBLIC-COMMERCIAL-GRANTS.md` has been created: there is no identified need for a public transparency register today. Commercial grants are made separately in writing. If a register becomes useful, mark it as optional, allow licensees to request or add appropriate public entries, and state that absence does not invalidate a written grant. Do not require confidential arrangements to be published or make the register the authority for whether a commercial licence exists.

## Activating supporter-specific provisions

If a project does not include `AWOO-SUPPORTER-SYSTEM.md`, then the supporter-specific provisions of the Awoo Licence do not apply to that project. A flag in code, theme name or support link alone is not an express feature definition.

Copies and derivatives use the definition supplied with the version of the original project they incorporate. Deleting or rewriting that file in a fork does not erase existing obligations for retained Software. A fork can remove the Official Supporter System, including all defined Supporter Features, and document that removal. It cannot retain or deliberately reproduce protected functionality without entitlement by calling this removal.

When preparing distributions, include `LICENSE` and applicable third-party notices and licence texts. Also carry applicable supporter and asset definitions. A complete compliant removal of the Official Supporter System may omit the supporter definition under sections 4 and 9; removing a file alone never unlocks retained protected material. Check binary packages as well as source archives. The Desktop project copies the third-party notice collection on build and publish. See the [third-party audit and outstanding release work](../THIRD_PARTY_NOTICES.md#compatibility-and-outstanding-release-work); copy metadata alone does not establish that a release artifact meets every redistribution obligation.

## Ordinary experience and feature acceptance

Sections 6 to 8 of v2.0 use a reasonably usable ordinary experience test,
considered as a whole. They prohibit deliberate degradation and artificial
friction, protect implemented compatibility, necessary accessibility and
security fixes, and require reasonably practicable shared supporter systems
for participating projects with the same Licensor.

The **Related Core Functionality** field remains useful project documentation of
the task behind each feature; it is not a defined v2.0 legal term. Noctaxis's
five current cosmetic families still preserve usable normal themes and every
existing planning workflow. This audit does not change any runtime gate or protected scope.

Before adding a feature:

1. Identify the exact enhancement and the ordinary task behind it.
2. Check that the non-supporter application remains reasonably usable in
   practice, without deliberately withheld ordinary functionality, artificial
   waits, nuisance prompts, unreasonable workarounds or poor defaults.
3. Preserve implemented platform/device/protocol/format compatibility,
   necessary accessibility and security fixes without supporter entitlement.
4. Justify higher limits using genuine constraints or costs.
5. Use the shared supporter identity where section 8 applies; separate
   project or feature purchases must not be presented as support.
6. Define protection narrowly and check that the actual gate and user-facing
   explanation match it, excluding independent generic work and third-party
   material.

For Noctaxis, appearance modes, colour-vision controls, text scale, normal
themes and the theme system itself remain available to everyone.

## Required per-feature justification

Use all five fields for each feature:

- **Supporter feature:** exactly what supporters receive, with stable identifiers or resource references.
- **Related Core Functionality:** the underlying substantive capability available to everyone.
- **Non-supporter implementation:** the concrete usable alternative, including relevant limits or fallback.
- **Why this is not a commercial restriction:** why the ordinary implementation remains reasonably usable without artificial degradation, how compatibility/accessibility/security remain available, and how official access fits the supporter exception. This field does not declare the feature non-commercial in every context or give forks commercial permission.
- **Protected scope:** the specific implementation and deliberate substantially equivalent reproductions intended to evade entitlement. Leave independently developed generic functionality outside the restriction.

Similarity alone does not establish circumvention. For themes, protect the identified definitions and palettes against deliberate entitlement bypass; do not claim individual colours or prohibit independent themes. Shared accessibility and rendering mechanisms do not become protected features merely because supporter themes use them.

## Current Noctaxis implementation audit

The five palette flags, production catalogue gate, global entitlement and current tests were rechecked on 9 October 2026. The audit followed the runtime entitlement path and palette definitions rather than relying on older implementation reports.

| Evidence | Finding |
| --- | --- |
| [BuiltInPalettes.cs](../Noctaxis.Desktop/Themes/BuiltInPalettes.cs) | Five definitions have the supporter flag: `supporter.noctaxis-neon`, `supporter.foxfire`, `supporter.deep-space`, `supporter.moonlit`, `supporter.pigeon`. `builtin.noctaxis` and `builtin.highcontrast` are ungated. Each family has Light/Dark palettes and colour-vision variants. |
| [ThemeCatalogue.cs](../Noctaxis.Desktop/Themes/ThemeCatalogue.cs) and [ThemeService.cs](../Noctaxis.Desktop/Themes/ThemeService.cs) | Availability depends on the supporter flag and the effective `HasAwooSupporterEntitlement` state. Unavailable families resolve to Noctaxis. |
| [MainViewModel.Appearance.cs](../Noctaxis.Desktop/ViewModels/MainViewModel.Appearance.cs) and [AppearancePreferences.cs](../Noctaxis.Core/Domain/AppearancePreferences.cs) | Theme, System/Dark/Light mode, colour-vision choice and text scale are separate. Unavailable supporter themes are disabled in the picker; accessibility controls are ungated. |
| [MainViewModel.SupporterLicence.cs](../Noctaxis.Desktop/ViewModels/MainViewModel.SupporterLicence.cs) | Codes are verified on load and before activation/replacement. Entitlement reaches the theme service. Ko-fi points to `https://ko-fi.com/callyyllac`; opening it does not activate entitlement. |
| [AwooSupporterLicenceVerifier.cs](../Noctaxis.Core/Supporter/AwooSupporterLicenceVerifier.cs), [AwooTrustedKeyStore.cs](../Noctaxis.Core/Supporter/AwooTrustedKeyStore.cs) and [AwooSupporterLicence.cs](../Noctaxis.Core/Supporter/AwooSupporterLicence.cs) | Offline Ed25519 verification authenticates an `AWOO1` licence with issuer `awoo.ltd` and entitlement `awoo.supporter`. Production trusts the embedded key identified as `awoo-2026-01`. |
| [MainWindow.axaml](../Noctaxis.Desktop/Views/MainWindow.axaml), [Supporter.resx](../Noctaxis.Desktop/Resources/Supporter.resx) and [Settings.resx](../Noctaxis.Desktop/Resources/Settings.resx) | Status, validity, add/replace/cancel workflow, errors, Ko-fi button and locked-theme labels form the supporter-facing interface. |
| [SupporterThemeTests.cs](../Noctaxis.Desktop.Tests/SupporterThemeTests.cs) and [SupporterLicenceTests.cs](../Noctaxis.Desktop.Tests/SupporterLicenceTests.cs) | Existing tests describe five gated families, picker availability, activation, invalid replacement preservation and the development override. Focused automated results are recorded in the final licensing audit. |

No other supporter-gated capabilities were found. Each of the five features is a cosmetic appearance; non-supporters retain the usable Noctaxis and High Contrast themes, appearance modes and accessibility controls. Family-specific map colours belong to the defined appearances; map functionality and third-party map data are not supporter-only.

`NOCTAXIS_FORCE_UNLICENSED=1` only suppresses entitlement. Test-only injected verifiers and signing keys are test infrastructure, not another official entitlement issuer.

## Pigeon authorship and acknowledgement

The first supporter's request was a concept request; CallyyllaC designed and
implemented Pigeon. The dedication conveys no ownership, licensing, approval,
control or exclusivity rights and no personal licence agreement. It does not
create a special entitlement tier. Actual submitted code/artwork would require
its own contribution and ownership assessment. See the
[Pigeon definition](../AWOO-SUPPORTER-SYSTEM.md#pigeon-request-and-acknowledgement).

## Adding, updating and removing features

For a new feature, inspect the implementation, complete the five fields and update the project definition with matching identifiers and a revision date. Keep the definition, actual gate, non-supporter experience and public documentation in agreement before distribution. Do not list unimplemented proposals as current Noctaxis Supporter Features.

For changes, assess the non-supporter implementation again. A variation can cease to qualify if its free alternative becomes unusable or its underlying workflow is removed. Record scope, identifier, destination and entitlement changes explicitly; do not silently broaden protection through vague wording.

When removing a feature from the original project, update its implementation and definition together for the new version. In a fork, removal of the system and features is permitted; removing gates while retaining or deliberately recreating protected features without entitlement is not. Preserve applicable original terms and identify removed components clearly.

Keep release-specific copies of the licence and definition. Current edits do not silently change an older release's terms or separately issued commercial permission.

## Reuse and versioning

Not all Awoo projects use the Awoo Licence; check each project's own licence. For another project that adopts it, copy `LICENSE` unchanged, identify that project's copyright holder or holders as the Licensor in its project-specific documentation, add concise public links and retain its third-party notices. Create its commercial guide with the appropriate public contact. Do not substitute a project-specific name into the generic licence.

If the project has an official supporter system, use the template below to define its actual components and features. Do not copy Noctaxis's keys, destinations or feature list unless they really apply.

If copyright ownership later changes, update the project-specific copyright and Licensor statements. The definition of `Licensor` in Awoo Licence v2.0 remains stable. No change to the universal licence is needed merely to identify a different copyright holder.

The Awoo Licence version changes when universal rules change. Project supporter definitions have independent revisions and can change as their features and implementation change. The signed supporter protocol, project definition revision and public licence version are separate concerns.

There is no automatic upgrade or "or later" permission. A fork need not track upstream. Later feature retirement or graduation applies to material updated or superseded from that later upstream version, as section 17 describes; it does not retroactively unlock older retained material.

Do not silently rewrite historical licence versions. Publish changed universal rules under a new version and preserve earlier text with the releases that used it. Record which licence and supporter definition each release supplies.

## Third-party material and commercial permission

Third-party software, services, datasets and other material retain their own terms. OpenNGC's bundled licence, Matplotlib's colour-table licence, package terms and external data/service conditions remain applicable. Do not label them as Awoo-owned or assume commercial permission from the Licensor grants provider rights. Follow [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md) and [3rd Party References](user-guide/third-party-references.md).

Commercial permission is a separate written grant from the Licensor, assessed case by case. The Licensor determines the scope of that written grant. No public file, supporter code, donation or enquiry constitutes permission. See [Commercial licensing](../COMMERCIAL-LICENSING.md).

## Project identity and contact

The generic licence defines **Licensor** as the copyright holder or holders, or another person authorised by them, who make the Software available under it. Do not assume that an authorised agent owns the underlying copyright. For Noctaxis, the current copyright holder and Licensor is **CallyyllaC**: Copyright Â© 2026 CallyyllaC.

**Awoo.ltd** is the project/site identity and infrastructure, not a limited company. The signed supporter issuer `awoo.ltd`, entitlement `awoo.supporter`, protocol identifier `AWOO1` and Awoo branding are unchanged. They identify the supporter system and do not assert incorporation.

The official public contact for commercial-licensing enquiries is [licensing@awoo.ltd](mailto:licensing@awoo.ltd). Supporter status and contributions remain separate from commercial permission.

## Derivatives, assets, research and contributions

Distributed derivatives must identify their origin and unofficial status both
in repository/documentation and in the distributed product or package. No
source publication of fork modifications is required by Awoo alone; any
third-party source requirements still apply.

Section 12 restricts branding reuse and misleading official presentation.
Noctaxis has no `AWOO-ASSETS-DEFINITION.md`. Absence of that file does not override section 12 or third-party
rights. Identify assets precisely and confirm ownership before introducing
separate asset terms for a future release.

Section 10 allows proportionate, temporary good-faith research/debugging and
interoperability testing. It does not permit distributing unlocks, routine
unentitled supporter use, or monetising bypasses. It grants no permission to
reverse-engineer separately licensed third-party software contrary to its terms.

Section 14 grants broad, irrevocable copyright rights to the Licensor for
intentional submissions, including commercial exploitation and relicensing.
Contributors keep copyright and have no automatic right to revenue. Read
[CONTRIBUTING.md](../CONTRIBUTING.md) before accepting new work. Do not assume
section 14 obtains new rights in earlier submissions or third-party
material; obtain the necessary agreement and preserve provenance. It contains
no express contributor patent grant or moral-rights waiver.

Section 18 terminates permissions for material breach and gives conditional
30-day cure/reinstatement. Repeated or deliberate breach can remove that
route through written notice. Track discovery/notice and cure rather than
promising unconditional permanent permissions.

## Publication review notes

- Review the [current licence questions](licensing-final-audit.md#manual-review), particularly branding scope and contributor authority. The legal text is preserved rather than silently resolving ambiguities.
- The [third-party audit](../THIRD_PARTY_NOTICES.md#compatibility-and-outstanding-release-work) links to the targeted Windows publish check: previously missing managed/font/MS-PL notices are supplied, the unchanged Windows binaries have verified provenance and applicable notices, and separate Open-Meteo service restrictions remain documented. Resolve the applicable items before release. Light Pollution Atlas permission is resolved: David Lorenz granted explicit permission on 8 October 2026 for its use in Noctaxis, recorded in the [Atlas permission record](../THIRD_PARTY_NOTICES.md#david-lorenz-light-pollution-atlas-permission). The Atlas remains his third-party data and is not under the Awoo Licence. This framework grants no new rights in third-party material.

## Reusable supporter definition template

Create `AWOO-SUPPORTER-SYSTEM.md` in the project's root. Replace these instructions with verified project facts and repeat the feature section for each actual protected feature. If there are none, say so.

If a project does not include `AWOO-SUPPORTER-SYSTEM.md`, then the supporter-specific provisions of the Awoo Licence do not apply to that project.

```markdown
# PROJECT NAME Supporter System

**Project:** PROJECT NAME
**Copyright holder / Licensor:** COPYRIGHT HOLDER OR HOLDERS
**Public licence:** Awoo Licence v2.0 (see LICENSE)
**Definition revision:** REVISION AND DATE

This is the supporter-system definition for PROJECT NAME under Awoo Licence v2.0.

## Official Supporter System

Identify official supporter branding, links and destinations.
Describe actual activation, verification, entitlement and status mechanisms,
including any signing/verification relationship and supporter-facing interface.
Identify participation in a shared system and the shared identity/entitlement
where section 8 applies; do not describe separate feature purchases as support.
Identify how entitlement connects to the features below, using stable identifiers
and implementation references. Do not publish private signing keys or credentials.

A Derivative Work may remove this system, including all defined Supporter Features.
It may not bypass entitlement to continue providing protected features or repurpose
the identified components to redirect or misrepresent support intended for Awoo
or the original project as support for another party.

A fork may add a clearly separate Voluntary Support mechanism, including alongside
this system, but that mechanism must not grant access to official Supporter Features.

## Supporter Features

List only expressly identified, implemented supporter-gated features.
If there are none, state that no Supporter Features are currently defined.

### FEATURE NAME

**Supporter feature**

Describe exactly what supporters receive. Identify the implementation or resources.

**Related Core Functionality**

Describe the substantive capability and distinct task or workflow available to everyone.

**Non-supporter implementation**

Describe the reasonably usable implementation available without entitlement.

**Why this is not a commercial restriction**

Explain the cosmetic variant, enhancement, optimisation, higher limit or convenience.
Show that the ordinary application remains reasonably usable without artificial
degradation. Preserve implemented compatibility, necessary accessibility and
security fixes, and meet the shared supporter requirements in sections 6 to 8.
Official access uses the public licence's supporter exception and does not itself
grant commercial exploitation rights.

**Protected scope**

Identify the specific implementation and deliberate substantially equivalent
reproductions intended to provide it without official entitlement. Do not claim
independently developed generic functionality merely because it solves a similar
problem. Keep third-party material and its separate terms outside this scope.
```
