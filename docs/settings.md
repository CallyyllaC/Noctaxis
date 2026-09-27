# Settings

Settings save automatically and take effect at once unless noted below. There is no Save, Apply or
Revert. The Settings page has no copy of the settings of its own. Every control is a view of
`MainViewModel.Settings`, so it always shows the saved value, or the value it is about to apply.

## Categories

| Category | Settings | Actions |
| --- | --- | --- |
| Appearance | Theme, Mode (System/Dark/Light), Colour vision mode, Text size | — |
| General | Measurement system, Planning time zone, Time slider snap interval, Open locations in (external map) | — |
| Equipment | Camera height above ground, Cameras, Lenses | Add/remove camera or lens |
| Planner map | Terrain calculations, Minimum terrain frame coverage, Terrain minimap context, Camera framing on map, Visibility limits, Overlay opacity, Terrain tint strength, Terrain obstruction colour, Boundary line thickness, Light pollution colour map | Use Cividis |
| Weather | Forecast reuse distance, Weather values (requested, shown and exported) | — |
| Celestial objects | Configured objects: visibility, order and primary | Search/add/remove, Restore default objects |
| Data | Maximum terrain cache size | Clear terrain cache, Install/remove the light-pollution atlas, Repair saved map images |
| Supporter | — | Licence status, add/replace licence, Ko-fi |
| Diagnostics | Terrain diagnostics | Copy terrain debug snapshot |

Light-pollution overlay visibility and opacity are Planner → Layers controls. They commit through that
panel and are not duplicated in Settings.

### Navigation

The categories are a vertical rail on the left, with the selected category on the right. This is the
`SettingsCategories` TabControl styled by `TabControl.settingsNavigation` in `App.axaml`:

- **Rail:** it scrolls on its own and takes at most about a third of the width. Long names wrap within
  it instead of clipping or pushing the content aside.
- **Content:** each category's content scrolls independently of the rail.
- **Selection:** the selected category shows a pipe marker, a semibold label and a selection fill, so it
  never relies on colour alone.
- **Keyboard:** Up and Down change category; Tab moves into the content.
- **Screen readers:** the rail is announced as "Settings categories", and each entry as a tab with its
  own name and selection state.

It is a presentation change only: the settings model and its behaviour are unchanged.

## Destructive actions

Ordinary settings never ask for confirmation. Actions that permanently remove something the user
created, or that is expensive to recreate, ask first, through Noctaxis' own `ConfirmationDialog`
(`IPlannerDialogService.ConfirmDestructiveAsync`). The dialog:

- names what will be removed, and labels its button with the verb (for example "Remove camera");
- uses the destructive style;
- focuses **Cancel** when it opens, and neither Enter nor Escape ever confirms.

| Action | Why it asks |
| --- | --- |
| Remove camera / Remove lens | User-created equipment profile; applied and saved at once when confirmed |
| Restore default objects | Replaces the user's configured celestial objects (only asks when they differ from the defaults) |
| Remove (Light Pollution Atlas) | Deletes the installed atlas, which is slow to download again |
| Clear terrain cache | Deletes all cached terrain (unchanged; it is downloaded again when needed) |

Removing a celestial object from the list does not ask, because it can be added back from the
catalogue. If a confirmed removal cannot be saved, the save-failure notice says so.

There is deliberately no action that removes the supporter licence, so there is nothing to confirm. A


The maximum terrain cache size is a limit and nothing more. It can't be set below 0.25 GiB
(`MainViewModel.MinimumTerrainCacheLimitGiB`), so entering 0 no longer empties the cache. Emptying it is
the explicit **Clear terrain cache** action. Older settings files saved with a limit of 0 show and save
0.25 GiB.

## Inventory

All settings live in `AppSettings`, saved in `state.json` by `JsonUserDataStore` through
`StateSaveScheduler`. Commit timing: **Now** means applied at once; **Pause** means applied after
editing pauses (`SettingCommit.Deferred`, 700 ms); **Release** means applied when the interaction ends.

| Setting | Stored as | Default | Applies | Why |
| --- | --- | --- | --- | --- |
| Theme / Mode / Colour vision | `Appearance` | Noctaxis, Dark, None | Now | Theme resources are republished live |
| Text size | `Appearance.TextScale` | 100% | Release (`CommitSlider`) | Relayouts the whole window, which would move the slider under the pointer mid-drag. Keyboard and automation steps commit at once |
| Measurement system | `Units` | Metric | Now | Formatting only |
| Planning time zone | `SelectedTimeZoneId` | system | Now | Recomputes astronomy for the current plan |
| Time slider snap | `TimeSnapMinutes` | 5 | Now | Read when scrubbing |
| External map | `ExternalMapProvider` | OpenStreetMap | Now | Read when opening a map |
| Camera height | `CameraHeightAboveGroundMetres` | 1.7 m | Pause | Re-resolves the observer and recalculates terrain |
| Cameras, lenses | `Equipment` | Migrated from the session lens | Pause, once all profiles are valid | Typing names would otherwise save every keystroke. Invalid drafts are shown with a message and never saved |
| Terrain calculations | `EnableTerrainCalculations` | On | Now | A toggle; cancels and restarts terrain work once |
| Minimum terrain coverage | `CameraFraming.MinimumTerrainFrameCoveragePercent` | 5% | Now | Recomputes cached framing only |
| Terrain minimap context | `TerrainMinimapContext` | 2.5× | Now (live while dragging) | Changes only the minimap's drawn extent |
| Framing overlay visible, visibility limits | `CameraFraming` | On, On | Now | Same setting as the Planner toggles |
| Overlay opacity, line thickness | `CameraFraming` | 10%, 1.25 | Now | Drawing only |
| Terrain tint strength | `CameraFraming.TerrainTintStrengthPercent` | 40% | Now (live while dragging) | Drawing only |
| Terrain obstruction colour | `CameraFraming.TerrainObstructionColour` | #9A6F9E | Now (live) | Drawing only |
| Light-pollution colour map | `LightPollution.PaletteId` | grayscale | Now | Overlay re-render only |
| Forecast reuse distance | `Weather.CacheDistanceKilometres` | 5 km | Pause | Refreshes the forecast over the network |
| Weather values | `Weather.EnabledFields` | All | Pause | Part of the forecast request: newly enabled values are fetched in one refresh |
| Celestial objects | `CelestialObjects` | Sun, Moon | Now | List edits |
| Terrain cache size | `TerrainCacheLimitBytes` | 2 GiB (minimum 0.25) | Pause | A lower limit deletes the cached files furthest from saved locations |
| Terrain diagnostics | `TerrainDebugOverlay` | Off | Now | Developer view |
| Supporter licence | `AwooSupporterLicenceCode` | none | On Add / Replace licence | Verified before it is stored |

Saves are debounced (500 ms) and coalesced. A save that fails appears in the application notice bar
until a later save succeeds, and is never shown as saved. `FlushAsync` also runs changes still in their
pause. Closing the window calls it, so quitting straight after a change keeps it. Writes are atomic:
a temporary file is written and then moved over the old one. A corrupt file is set aside.

## Restart-required settings

No current setting requires a restart. Everything above is applied in the running process. The
mechanism is still in place for a future setting:

- Register its `SettingDefinition` in `MainViewModel.Settings.cs` with `RequiresRestart: true`.
- Its consumers must read `MainViewModel.ActiveSettings`: the values this process started with. They
  never read `Settings`, so the new value is saved but not partly applied.
- Mark the control in the UI with a short "Requires restart" note beside it.

A restart is pending while any restart-required setting's saved value differs from its active value.
It is compared, not flagged, so changing a setting back to its active value clears it. While a restart
is pending, the Settings page shows a notice with **Restart Noctaxis**. The icon and wording carry the
meaning, not colour alone.

`ApplicationRestartService` restarts on Windows and Linux alike:

1. It saves settings; if that fails, it does not restart.
2. It starts the same executable with the same arguments, through the `dotnet` host when launched that
   way, and adds `--noctaxis-restart-after <pid>`.
3. Only once the new process has started does it close the main window. That runs the normal shutdown:
   the final save, then releasing resources.

The new process waits (up to 20 s) for the old one to exit before loading state. If the replacement
cannot be started, Noctaxis keeps running and explains how to restart manually.

## Defaults and reset

There is no general "reset to defaults". Its only legitimate predecessor, **Restore default objects**
(celestial objects), is kept and saves like any other change. The removed "Reset unsaved changes"
reverted edits, which no longer exist.
