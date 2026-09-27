# Interaction states

Hover, press, keyboard focus, selection, checked, expanded and disabled states may change how a control
looks. They must never change where it is or how big it is.

## Why

Whether the pointer is over a control is decided by that control's geometry. If a hover state resized or
moved a control, the pointer near an edge could end up outside it. The hover state would switch off, the
control would return to its original geometry, and the pointer would be inside again, over and over, as
fast as Avalonia could re-test.

That was the location-card flicker. Hover styles thickened borders (1 → 2 px, up to 4 px in High
Contrast), and a thicker border takes layout space. The Add location card grew from 288 to 290 px.
`ResponsiveCardGridPanel` sizes every row from its tallest card, so every lower row moved down by 2 px.
A pointer on the top edge of a lower card then fell outside it, and the loop began. Every auto-sized
button grew by 2 px on hover and press in the same way, reflowing whatever came after it.

## How

- **Borders keep one width in every state.** The heavier state border is an inset ring: a `BoxShadow`,
  which takes no layout space. `ThemeService` publishes the rings from theme colours:
  - `StateHoverRing`, `StatePressedRing`, `StateFocusRing` and `StateSelectedRing` sit inside a control's
    1px border and add the rest of the state width;
  - `AccentHoverRing` and `AccentPressedRing` do the same for accent and map-chrome buttons;
  - `StateFocusOutline` and `StateSelectedOutline` are full-width rings for borderless items (tabs, list
    items, settings radio choices).
- **Location cards** draw their state border with the `Border.cardStateRing` overlay, which is not
  hit-testable. Its width never reaches the card's measured size.
- **Pressed buttons** no longer shrink. Fluent's `scale(0.98)` render transform also shrank the hit area,
  so a press near an edge could be released outside the button and lose the click.
- **Tooltips are informational.** `Controls/InformationalToolTips` hosts every tooltip popup in its
  window's overlay layer and makes the tooltip not hit-testable. Moving across a tooltip that overlaps
  its control therefore keeps the control hovered, and leaving the control still closes it. Before, each
  tooltip was a native popup window: the main window lost the pointer, and the control lost its hover.
  Flyouts, menus and drop-downs are separate popups and remain interactive. Interactive content belongs
  in a flyout, never a tooltip.

`InteractionGeometryTests` and the location-card geometry test check this across themes and text sizes.
When adding a state style, change colours or rings, never `BorderThickness`, `Margin`, `Padding`, size
or a render transform on the control that receives the pointer.
