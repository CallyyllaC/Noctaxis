# Accessibility

This is the accessibility model Noctaxis implements today: the rules every surface follows, how themes and
colour-vision modes keep meaning visible, how targets are told apart, and how this is tested. It replaces
`colour-vision-accessibility.md`, whose colour-vision material is kept here in full under
*Colour and theme accessibility*.

## Principles

- **Hover is never required.** Nothing essential is shown only on hover. Tooltips repeat or name what is
  already visible; explanations that matter open from an explicit control (see *Information affordances*).
- **Touch and mouse are equal.** Every action has a visible control that a click, a tap and the keyboard
  all reach.
- **Right-click is an accelerator, never a gate.** A context menu only offers actions that also have a
  visible route. A long-press may open the same menu, and is never the only way in either.
- **Keyboard access, with visible focus.** Interactive controls take keyboard focus, show it with the
  theme's focus ring, and act on Enter or Space; custom controls take the arrow keys where they adjust a value.
- **Meaningful names.** Controls carry an accessible name and, where useful, help text, resourced like any
  other text (`docs/localisation.md`, *Accessibility strings*).
- **Visual footprint is not hit area.** A compact control may look small; its hit target stays generous,
  and interaction states never change a control's size or place (`docs/interaction-states.md`).
- **Meaning never rests on colour alone.** Every colour-coded distinction has a second cue: a pattern, a
  weight, a shape, an icon or a label.

## Colour and theme accessibility

Colour-vision support is part of the theme system, not a separate palette. Every theme family (Noctaxis,
High Contrast, Neon, Foxfire, Deep Space, Moonlit, Pigeon) has Light and Dark palettes, and each palette has
four variants: None, Protanopia, Deuteranopia and Tritanopia. That makes 56 combinations, and all of them can
be switched live. Theme, Light/Dark, colour-vision mode and text size are independent preferences.

```
AppearancePreferences (family, Light/Dark, colour-vision mode, text size)
  → ThemeCatalogue.Resolve
  → BuiltInPalettes variant (ApplySemanticVariant)
  → ThemeService: brushes, <token>Color and CelestialColourSet in one replaceable dictionary
  → FluentResourceBridge (Fluent control keys)
  → DynamicResource consumers
```

The consumers are the XAML UI, the timeline chart, the target line samples, the map overlays, the
basemap, the Mapsui canvas and the Picture-in-Picture map. Drawing code only names semantic roles; it never
names a theme family.

**Theme defaults never overwrite explicit values.** A custom control takes a theme resource only as a
property's default, through `ThemeDefault.BindThemeDefault`, called from its constructor. A value set on
the control (in markup or code) and any style that sets the property win over it, and a theme, Light/Dark,
colour-vision or text-size change moves the default without reasserting it over them; a control that sets
nothing follows every change live. This is what keeps a deliberate choice — a transparent overlay, a
higher-contrast brush — in place across live theme changes. Maintainers: never bind a theme resource to a
control's own property with `Bind(property, new DynamicResourceExtension(key))`; that binds at local
priority, and the next theme change pushes the theme value back over the explicit one. It once painted the
Camera frame's transparent direction pad with the opaque surface colour and hid the whole picture.

**High Contrast** is a family like the others: the same semantic roles, so it keeps every distinction the
other families make, with plain surfaces (no status washes) and heavier state borders.

### What a colour-vision variant changes

A variant starts from the family's own colours. It changes only what separation needs, so it still looks
like its theme.

| Roles | Change |
|---|---|
| `Success`, `Warning`, `Error`, `Information` | Separate authored sets for each deficiency, Light/Dark and High Contrast. |
| Status backgrounds | A light wash of the status colour over the surface (High Contrast keeps plain surfaces). |
| `CelestialSun`, `CelestialMoon`, `CelestialDeepSky1`–`5` | Separate authored sets for each deficiency and Light/Dark. |
| Target patterns | Drawn heavier (see *Target identity*). |
| `MapWater`, `MapVegetation`, `MapBuiltUp` | Nudged only where a pair collapses for that family (`MapAreaOverrides`). |
| `MapPinActivity`, `MapBearing` | Changed only where needed: High Contrast (protanopia) and Neon (deuteranopia). |

Accent, selection, focus, surfaces, text, chrome and the other map roles keep the family values.
`ColourVisionAccessibilityTests.ColourVisionVariantsKeepTheFamilyIdentity` enforces this.

### Meaning never rests on colour alone

| Meaning | Colour | Non-colour cue |
|---|---|---|
| Focused vs other targets | Celestial role | The focused target's path, ray and line sample are solid and heavier; every other target is dashed. |
| Which celestial object | Celestial role | Each role has its own dash pattern, the same on the map, the timeline and its line sample. The timeline names each path at its highest point ("Sun", "Moon", catalogue ID). A screen reader hears the pattern by name. |
| Below the horizon | `ChartBelowHorizon` | The line is thinner and dashed, and sits under the horizon line. |
| Beyond the weather visibility distance | Greyed cone | A dashed frontier arc at the visibility distance, drawn in the basemap label colour. Framing edges beyond it are dotted. |
| Terrain obstruction | User-chosen tint | A solid frontier edge. It differs by pattern from the dashed weather frontier. |
| Camera frame aim vs coverage threshold | Fixed tints on the grayscale picture | The aim line is dashed and thin; the threshold line is solid and thicker. A legend sits behind the frame's info button. |
| Status messages | Status role | An icon: circled i, circled tick, triangle, octagon (`StatusText`). |
| Hover and press | Fill change | An inset ring thickens to 2 px (3 px when pressed in High Contrast) and strengthens (`StrongBorder`). |
| Selected tab or settings category | Selection fill | A pipe or underline marker and a semibold label. |
| Disabled controls | Disabled text colour | The control's border is hidden and it takes no input; dependent settings dim as a group under the setting they depend on. |

### Measured thresholds

The colour science is CIEDE2000 on colours simulated with the Machado et al. (2009) model at full
severity, plus WCAG 2 contrast. `ColourScience` in the test project implements it. Each palette is judged
by the viewer it is meant for: a colour-vision variant under its own deficiency, and None with normal
vision.

| Check (all 48 combinations) | Threshold | Result |
|---|---|---|
| Primary, secondary, muted and disabled text on every surface | ≥ 4.5:1 | pass |
| Status pairs | ΔE ≥ 10 | ≥ 16.0 in every variant |
| Status text on its background and on the surface | ≥ 4.5:1 | pass |
| Accent, selection and map-chrome text on their fills | ≥ 4.5:1 | pass |
| Celestial pairs | ΔE ≥ 10 | ≥ 15.0 in variants, ≥ 25 in None |
| None celestial colours seen with any deficiency | ΔE ≥ 7.5 | 8.0 (the previous colours measured 1.9) |
| Celestial lines on the chart surface and on map land | ≥ 3:1 | pass |
| Below-horizon path line | ≥ 3:1 | pass |
| Map area and road pairs | ΔE ≥ min(5, 0.85 × the family's normal-vision ΔE) | pass |
| Pin ring/activity and field-of-view/bearing | ΔE ≥ 10 | pass |
| Focus ring on every surface | ≥ 3:1 | pass |

Basemap areas are deliberately subtle, so the map rule is relative: a deficiency must not make a pair
harder to tell apart than the family's own design is for normal vision. Land and built-up differ by
lightness, which colour-vision deficiencies do not affect.

### Light Pollution

The default colour map, Grayscale, carries no hue. Turbo's description says it is not colour-vision safe.
When a colour-vision mode is active and Turbo is selected, Settings shows a notice with a **Use Cividis**
button. The user's choice is never changed for them.

### Intentionally literal colours

- The Camera frame's depth picture (grayscale terrain, night-blue sky): data, not theme chrome.
- The aim and threshold lines on that picture: fixed tints over grayscale, told apart by pattern and weight.
- The terrain debug renderers and topology colours: diagnostics only.
- The ground-facing fan outline in the environmental overlay: a rare, geometry-labelled state.
- The obstruction tint: a user setting.
- Saved-location image processing and the settlement glow: artwork for the location cards.
- Location-card shadows and glows in `App.axaml`: decorative, and they carry no meaning.
- Light Pollution colour maps: scientific scales chosen by the user.
- Satellite imagery: never recoloured.

## Target identity

A target is told apart by its **colour** and its **line**. There are no shapes along a path: the repeated
marker glyphs (disc, ring, square, triangles, diamond, cross) on the map rays, at the timeline peaks and in
the target list are no longer drawn.

- **The focused target** is drawn solid and heavier, in its colour, everywhere. It is the one solid path.
- **Every other target** is drawn in its colour and its role's dash pattern: long dashes (Sun), dots
  (Moon), then dash-dot, short dashes, dash-dot-dot, extra-long dashes and paired dashes for the five
  deep-sky slots. A deep-sky target's slot follows its order in Settings → Targets.
- **Normal colour vision:** colour is the primary cue and the pattern backs it.
- **Colour-vision modes:** the pattern is the primary non-colour cue. Dashed paths are drawn 1.35× heavier
  (`CelestialPalette.PatternEmphasis`) so the pattern reads at normal map zoom and on the timeline. The
  colours are re-authored for the deficiency and stay useful, but nothing depends on them.
- The patterns are chosen to be told apart, not merely to differ: any two differ in how many dashes a repeat
  has or, with as many, in a dash's length by at least half again. None is solid.

**One stroke.** `CelestialPalette.Stroke` is the only place a target's pen is made: colour, solid or
pattern, and emphasis. The map's rays, the timeline's paths and every line sample use it, with only the
surface's own line weight passed in, so they cannot drift apart. `CelestialPalette.Dash` holds the patterns
and nothing else holds a copy.

**Line samples in the UI.** `CelestialLegendGlyph` is a short horizontal line in the target's current
colour and pattern (solid when focused), with no glyph, 44 px wide so the longest pattern repeats twice. It
appears beside every target in the Inspector's Targets list and in Settings → Targets, where the order of
the list decides each target's pattern. Its accessible name is the pattern in words ("Dotted line", "Solid
line"), and the Targets info button explains the scheme.

## Text and scaling

- **Text size** (Settings → Appearance) runs from 100% to 200% and scales the whole type ramp. It applies on
  top of the operating system's display scaling, which Avalonia honours separately.
- Layout responds to it: search width, date and time pickers, location cards (three text lines above
  120%, an opaque scrim when enlarged), the Planner's top bar (compact before anything wraps or clips) and the
  Camera frame card's readings line, which wraps rather than clips.
- Line samples and other drawn keys keep their full size at every text size and are never cut by their rows.
- **Language** (`AppSettings.Language`) decides the text; **regional formatting** comes from the operating
  system and decides how numbers and dates are written; the **measurement profile** (Metric, UK Mixed,
  Imperial) decides units. None of the three implies the others (`docs/localisation.md`).
- The Planner's shipped layout is tuned for 1920 × 1080. Smaller windows keep each panel within its minimum
  size and draw the map's instruments smaller; the Picture-in-Picture map gives way first when the map is too
  small for both.

## Keyboard and focus

- Every button, check box, tab, slider, numeric input and list item is reachable with Tab and shows the
  theme's focus ring (≥ 3:1 against every surface). Focus never changes a control's size or place.
- **Timeline:** the chart takes focus; Left/Right move the selected time a step, Page Up/Page Down an hour,
  Home/End to the window's ends. Its name is "Time on the timeline" and its help text is the selected time.
- **Camera frame:** the direction control takes the arrow keys (Left/Right turn, Up/Down tilt); its four
  edge buttons repeat while held.
- **Map instruments:** the PiP and Camera frame resize grips take the arrow keys, a step at a time.
- **Context menus** open from the keyboard menu key as well as a right-click; on the map a keyboard-opened
  menu acts at the pin.
- **Settings:** Up and Down change category in the rail and Tab moves into the page; the rail is announced
  as "Settings categories". Sliders that relayout the window (text size) commit on release when dragged,
  and at once for keyboard and automation steps.

## Touch and pointer

- No hover is needed for anything. The Planner panels pin, unpin and move from their header buttons and
  menu; the PiP's Switch and Follow are buttons; the Camera frame's direction has visible arrows as well
  as a drag.
- **Context menus are accelerators.** The map's menu (Set pin here, Save location here) mirrors a click on
  the map and the top bar's Save location; a location card's menu is the same menu as its visible ••• button.
- Compact map-chrome buttons keep generous hit areas (at least 32 × 28 px for the Targets info button and
  similar), and tooltips are not hit-testable, so moving over one never steals the control's hover.

## Information affordances

- Explanations live behind an explicit ⓘ (`InfoButton`) that opens on click, tap, Enter or Space, never on
  hover. It is named for what it explains ("About targets").
- A setting's own label says what it is; the ⓘ holds the longer why, so pages stay uncluttered.
- Essential state is never tooltip-only: the focused target is a visible *Focused* label, a disabled
  dependent setting is dimmed in place, and status messages carry an icon and text.

## Planner accessibility

- **Targets:** colour plus pattern on the map and timeline, the same line sample beside each name, the
  focused target solid, names at each path's peak (see *Target identity*).
- **Timeline:** the selected moment is the one strong accent line with a grip tab and its time beside it;
  the horizon and altitude guides are labelled; below-horizon segments are thinner and dashed.
- **Camera frame:** bearing and pitch scales are figured in degrees and placed by the same projection as the
  skyline; the readings line states bearing, pitch, horizon and obstruction in words and units.
- **PiP:** Switch and Follow are named buttons reachable by keyboard; its zoom is kept between runs.
- **Panels:** pin/unpin, dock and the corner choices are menu items as well as drags; Reset Planner layout
  returns the shipped arrangement.

## Settings accessibility

- Settings are grouped by category in a rail; each category is a page of sections.
- Every setting with more to say has an ⓘ; units are shown beside values.
- Dependent settings sit under the setting they depend on and dim, with their controls disabled, when it
  is off.
- Colour-vision mode is one choice in Appearance, applied live across the whole app, with no restart.

## Testing and verification

- `AppearanceTests`: text, status, accent, selection, map-chrome and focus contrast across all 48
  combinations.
- `ColourVisionAccessibilityTests`: the colour-science matrix, role definitions, family identity,
  per-deficiency variants, and distinct target patterns.
- `ColourVisionRuntimeTests`: a live sweep of all 48 combinations through `ThemeService` leaves no stale
  colour in the chart, line samples, status icon, map overlay, Mapsui background or Fluent accent.
- `TargetIdentityTests`: no glyph drawing remains; every surface draws with `CelestialPalette.Stroke`;
  focused-solid and pattern rules; emphasis under every colour-vision mode in every theme; patterns pairwise
  distinct; the line sample renders as the line alone; Inspector and Settings samples match their targets and
  are named; samples stay whole at 200% text; an explicit colour set survives a theme change.
- `CameraFrameRenderTests`: an explicitly transparent overlay stays transparent after a theme change, with
  the frame's final pixels checked.
- `ThemeDefaultPrecedenceTests`: for every themed property of every custom control, a local value and a
  style both survive dark→light, light→dark, a family change, a colour-vision mode and a text size applied
  after the control is shown, while an unset control follows each change; no control binds a theme default
  at local priority.
- `InteractionGeometryTests`: no state changes a control's geometry, across themes and text sizes.
- `AppearancePolishTests`, `AppearancePolishPageTests`, `AppearancePolishTerrainTests` and
  `LocalisationLayoutTests`: layout at text sizes up to 200%.
- Keyboard and naming checks sit with their features: the Targets help (`PlannerFocusedTargetTests`), the
  Timeline chart, the PiP buttons, the resize grips and the Camera frame direction control.
- `ShippedPlannerLayoutTests`: the shipped layout fits at 1920 × 1080, 1440 × 900 and 1024 × 700.

## Known limitations

- **English only.** The Language setting has one choice; there is no right-to-left layout work
  (`docs/localisation.md`, *Limitations*).
- **No screen-reader run.** Accessible names and help text are tested; Noctaxis has not been tested end to
  end with a screen reader.
- **No user testing.** The colour-vision work is measured, not yet checked with colour-vision-deficient users.
- **Light Pollution colour maps** are scientific scales and are not re-authored per colour-vision mode;
  Turbo is flagged instead.
