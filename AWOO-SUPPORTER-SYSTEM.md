# Noctaxis supporter system

**Definition revision:** 1, 9 October 2026  
**Public licence:** [AWOO Licence v2.0](LICENSE)

Noctaxis participates in my shared Awoo supporter system. Supporting my work can unlock five **optional appearance themes**. The planning tools, regular themes, high-contrast mode, text scaling and colour-vision options remain available without a supporter licence.

This page identifies the official supporter system and the specific supporter features under the AWOO Licence. It isn't a separate commercial licence.

## How supporter access works

Support is available through [Ko-fi](https://ko-fi.com/callyyllac), also linked from [Awoo](https://awoo.ltd/support/). After receiving an official supporter code, enter it under **Settings → Supporter** in Noctaxis. You can add or replace a code there, and a failed replacement won't discard your existing licence.

The code is checked **locally and offline**, using an Awoo-issued `AWOO1` Ed25519 signature and the embedded trusted public key (`awoo-2026-01`). The private signing key is not distributed. Entitlement is permanent, isn't tied to a device or online account, and can apply to supporter features in other participating Awoo projects. It doesn't create benefits for projects without their own supporter system.

The official implementation includes the Noctaxis/Awoo support identity and Ko-fi destination, licence entry and verification, stored entitlement, supporter interface, and theme-access checks. The implementation is in `Noctaxis.Core/Supporter/`, `Noctaxis.Desktop/ViewModels/MainViewModel.SupporterLicence.cs`, `Noctaxis.Desktop/Themes/`, and the related Settings interface. For technical details, see [Supporter licence implementation](docs/supporter-licensing.md).

## Supporter themes

These are the **only** supporter features currently defined for Noctaxis:

| Theme family | Identifier | Appearance |
| --- | --- | --- |
| **Noctaxis Neon** | `supporter.noctaxis-neon` | Cyan and fuchsia |
| **Foxfire** | `supporter.foxfire` | Copper and orange with cool cyan highlights |
| **Deep Space** | `supporter.deep-space` | Blue, indigo and starlight colours |
| **Moonlit** | `supporter.moonlit` | Subdued graphite and slate |
| **Pigeon** | `supporter.pigeon` | Rock-pigeon silver/slate with iridescent teal and violet |

Each family includes its authored **Light and Dark** palettes, associated application and map colours, and colour-vision variants. System appearance simply selects the appropriate Light or Dark version. The definitions are implemented in `Noctaxis.Desktop/Themes/BuiltInPalettes.cs`.

Without supporter access, **Noctaxis** (`builtin.noctaxis`) and **High Contrast** (`builtin.highcontrast`) remain available with Light, Dark and System modes, colour-vision settings (None, Protanopia, Deuteranopia, Tritanopia) and text scaling. If a locked family is selected without entitlement, Noctaxis falls back to its standard theme. No core planning feature or necessary accessibility or security fix is supporter-only.

Under the [AWOO Licence](LICENSE), the protected supporter features are these *specific authored theme families* and deliberate substantially equivalent reproductions intended to bypass entitlement. I don't claim ownership of colours, aesthetic ideas, independently created themes, the general appearance system, or third-party material. Forks may remove the official supporter system and its supporter features entirely; bypassing the checks while continuing to provide those features isn't the same as removing them. Separate voluntary support for a fork must not impersonate or redirect the official Noctaxis/Awoo system. The licence's limited research and interoperability exceptions still apply.

## Pigeon

Pigeon is dedicated to Noctaxis's first supporter, who suggested the concept. I designed and implemented the theme and its palettes. The acknowledgement doesn't transfer ownership or create a private theme tier: Pigeon is available through the same official supporter entitlement as the other four families.

The [AWOO Licence v2.0](LICENSE) governs these definitions and the use of the official supporter system.
