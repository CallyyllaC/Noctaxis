# Noctaxis logo

The mark is four rounded viewfinder brackets around a centred four-point star (concept 1A).

## One definition

`Noctaxis.Desktop/Branding/NoctaxisLogo.cs` is the only definition of the geometry. It uses a 64 × 64
design grid with a transparent background:

- the brackets are open centre-line paths, stroked with round caps and joins;
- the star is one filled path with concave sides, slightly wider than it is tall.

There are two weights:

- **Regular** matches the concept.
- **Small** is used at 24 device pixels and below. Its brackets are heavier and its star body fuller,
  so strokes and gaps survive at taskbar sizes.

Below 64 px, `ForPixelSize` also snaps the bracket inset and stroke to whole device pixels. This keeps
bracket edges crisp; they would otherwise blur across two pixels.

Everything else is drawn from that definition:

| Use | Where | Colours |
| --- | --- | --- |
| Windows exe and taskbar icon | `Assets/noctaxis.ico` (`ApplicationIcon`, `MainWindow.Icon`, dialog windows) | Fixed brand pair: frame `#7B61FF`, star `#9A88FF` |
| Vector master | `Assets/noctaxis-logo.svg` | Fixed brand pair |
| Titlebar mark | `Controls/NoctaxisLogoMark` | Theme: frame `Accent`, star `PrimaryText` |

The brand pair is two tones of the Noctaxis violet. Both are mid-luminance, so the transparent icon keeps
its shape on light and dark taskbars (see `BrandingTests`). The titlebar mark takes exactly those two
theme roles through the existing `ThemeService` resources, and uses no gradients, glow or shadow.

## Regenerating the icon

The `.ico` is 16, 20, 24, 32, 40, 48, 64 and 256 px, one PNG frame each. After changing the logo, run
`NoctaxisIcon.WriteAssets("<repo>/Noctaxis.Desktop/Assets")` from any small program that references
`Noctaxis.Desktop`. `BrandingTests.CommittedAssetsAreGeneratedFromTheCanonicalLogo` fails until the
committed assets match the definition.

## Licence and reuse

The branding geometry and generated assets above identify Noctaxis. Section 12
of [Awoo Licence v2.0](../LICENSE) governs names, logos and source identity;
accurate attribution/reference use is permitted, while other reuse needs
applicable permission. Distributed forks must identify themselves as modified
and unofficial under section 11. See the [current licence audit](licensing-final-audit.md#manual-review)
for the scope question concerning unchanged bundled logos. No separate
`AWOO-ASSETS-DEFINITION.md` is currently supplied for Noctaxis.
