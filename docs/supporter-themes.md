# Supporter theme families

This pass adds four authored cosmetic families on top of the existing family × appearance-mode × colour-vision × text-scale model. No entitlement service, payment integration, network lookup or external theme loader was added.

## Registered families

| ID | Display name | Supporter |
|---|---|---|
| `supporter.noctaxis-neon` | Noctaxis Neon | yes |
| `supporter.foxfire` | Foxfire | yes |
| `supporter.deep-space` | Deep Space | yes |
| `supporter.moonlit` | Moonlit | yes |

The existing availability predicate gates each family through the single Awoo entitlement state. All registered families remain visible in the picker; unavailable supporter families are marked `Locked · Supporter` and disabled. An unavailable or unknown requested family falls back to `builtin.noctaxis`. Controls contain no supporter-specific branches.

## Palette summaries

Values below are the authored base tokens. Every family has explicit Dark and Light dictionaries, and each dictionary has None, Protanopia, Deuteranopia and Tritanopia variants. All 16 combinations per family pass `ThemeDefinitionValidator` with zero inherited tokens.

| Family / mode | Application | Window | Surface | Elevated | Primary text | Secondary text | Border | Accent | Accent foreground | Selection | Selection foreground | Focus | Button | Hover | Pressed |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Neon Dark | #0B0E14 | #080A0F | #11151D | #171D27 | #F5F7FA | #B7C1CC | #3A4858 | #00D9FF | #00151A | #005A70 | #FFFFFF | #00D9FF | #18212C | #22313D | #2D3D49 |
| Neon Light | #F7FAFC | #F7FAFC | #FFFFFF | #FFFFFF | #101820 | #374C56 | #58717B | #007A91 | #FFFFFF | #BDEAF1 | #06242B | #007A91 | #E4F0F3 | #D2E8ED | #BEDDE4 |
| Foxfire Dark | #1B1512 | #15110F | #28201B | #30231C | #F7EEE5 | #CBB8A8 | #665044 | #D8753D | #1B100A | #754123 | #FFF7EF | #9DE7F0 | #34271F | #493328 | #5A3C2D |
| Foxfire Light | #FAF5EE | #FAF5EE | #FFFDF8 | #FFFFFF | #261812 | #5A3E30 | #80614E | #A64B20 | #FFFFFF | #F0C9AE | #2B160C | #167C8B | #F2E4D8 | #EAD4C3 | #DEC0A9 |
| Deep Space Dark | #080B18 | #050711 | #10162A | #151D36 | #EEF3FF | #B3C0DE | #35466C | #8295FF | #0A1025 | #3B4384 | #FFFFFF | #AFC8FF | #182342 | #22325A | #2E4270 |
| Deep Space Light | #F2F6FC | #F2F6FC | #FFFFFF | #FFFFFF | #101B33 | #3D4D6C | #607397 | #4B55A7 | #FFFFFF | #D8DDF8 | #1B204A | #4B55A7 | #E2E8F4 | #D3DDF0 | #C2D0E7 |
| Moonlit Dark | #1C2028 | #171A20 | #252B35 | #2D333E | #EDF1F5 | #BCC5CE | #526071 | #A7B9D7 | #14181E | #4D5D75 | #FFFFFF | #C5B9E5 | #303844 | #3D4857 | #4A5666 |
| Moonlit Light | #EEF0F3 | #EEF0F3 | #F8F9FA | #FFFFFF | #202733 | #45515F | #687585 | #566E91 | #FFFFFF | #D5DDE9 | #1D2735 | #596E9A | #E1E5EA | #D4DAE1 | #C5CED8 |

Neon is graphic black/charcoal with cyan identity and a restrained fuchsia decorative pressed-card overlay. Foxfire uses warm umber and copper against a cool ethereal focus highlight. Deep Space uses void blue-black, indigo and starlight contrast. Moonlit is the quiet low-saturation graphite/slate instrument theme. Light palettes are authored separately and are not RGB inversions.

## Semantic colour vision

Cosmetic surface identity remains stable across colour-vision modes. The validated shared semantic variant system changes Success, Warning, Error, Information and FocusBorder where required; Protanopia and Deuteranopia intentionally share the existing authored semantic set. Labels, checkmarks, warning/error symbols, current-location text and favourite stars remain non-colour cues. Scientific/celestial palettes, weather/data series, terrain pixels and user-owned terrain/framing colours remain outside this pass.

## Runtime, fallback and preservation

System resolves the selected family’s Dark or Light palette from the platform preference. Explicit Dark and Light ignore platform changes. ThemeService still owns one replaceable dictionary and one platform subscription. Repeated family/mode/vision switching is covered by the existing resource-lifetime tests.

Appearance updates continue through `ApplyAppearanceAsync`; they do not invoke Planner, terrain, weather or astronomy recalculation. Text scale, measurement system, radio settings UI, terrain semantics, saved-location data, scientific palettes and user-owned presentation colours are unchanged. Supporter licence storage and verification are documented in [supporter-licensing.md](supporter-licensing.md); the production verifier remains pending the frozen Awoo v1 wire format.

## Captures

The representative state captures are in `artifacts/appearance-supporter/`:

- [contact sheet](H:/Noctaxis/artifacts/appearance-supporter/contact-sheet.jpg)
- [Noctaxis Neon Dark](H:/Noctaxis/artifacts/appearance-supporter/supporter.noctaxis-neon-Dark.png)
- [Noctaxis Neon Light](H:/Noctaxis/artifacts/appearance-supporter/supporter.noctaxis-neon-Light.png)
- [Foxfire Dark](H:/Noctaxis/artifacts/appearance-supporter/supporter.foxfire-Dark.png)
- [Foxfire Light](H:/Noctaxis/artifacts/appearance-supporter/supporter.foxfire-Light.png)
- [Deep Space Dark](H:/Noctaxis/artifacts/appearance-supporter/supporter.deep-space-Dark.png)
- [Deep Space Light](H:/Noctaxis/artifacts/appearance-supporter/supporter.deep-space-Light.png)
- [Moonlit Dark](H:/Noctaxis/artifacts/appearance-supporter/supporter.moonlit-Dark.png)
- [Moonlit Light](H:/Noctaxis/artifacts/appearance-supporter/supporter.moonlit-Light.png)

Captures are headless Avalonia previews with synthetic semantic samples. They establish palette and state composition, not native GPU, mixed-DPI or screen-reader behaviour.

## Validation

Focused supporter/appearance run: 202 passed, 0 failed, 0 skipped before the discovery-only test-harness path correction; the dedicated supporter run after that correction passed 12/12. The final licence/supporter selection passed 26/26. Full sequential Release validation passed with Core 331 passed, 0 failed, 1 skipped and Desktop 598 passed, 0 failed, 0 skipped: 929 passed, 0 failed, 1 skipped (930 total). The sole skip is the opt-in live Terrarium integration test. `git diff --check` passed (exit 0); the latest full output is recorded in `artifacts/licence-full-release.log`.
