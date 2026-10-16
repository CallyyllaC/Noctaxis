# Localisation

Noctaxis v1 ships in English (en-GB) only. There is no translation. What exists is the foundation:
user-facing text lives in resources behind one lookup, and three things that are often confused are
kept apart.

| Concept | Decides | Comes from |
| --- | --- | --- |
| **Application language** | The text, and the language preferred for place names on the map | Settings → General → Language (`AppSettings.Language`); English (United Kingdom) is the only choice |
| **Regional formatting** | How numbers, dates and times are written | The operating system's regional format for the user. Not configurable in Noctaxis |
| **Measurement profile** | Which physical units are shown | Settings → General → Measurement system (`AppSettings.Units`): Metric, UK Mixed or Imperial |

None of the three is derived from another. English text with German number formats and metric units
is as valid as English text with British formats and UK Mixed units. Machine-readable values follow
none of them. Adding a language later should mean adding resource files, not reworking the
application.

## Architecture

One mechanism serves XAML and C#: .NET `.resx` resources read through `ResourceManager`.

- `Noctaxis.Core/Localisation/Strings.cs` is the single lookup. `Strings.Get(key)` returns text,
  `Strings.Format(key, values…)` fills placeholders, `Strings.TryGet` tests for a key.
- `Noctaxis.Desktop/Localisation/TextExtension.cs` is the XAML side, `{loc:Text Key}`. It calls
  `Strings.Get`, so markup and code can never disagree.
- English is the neutral language (`<NeutralLanguage>en-GB</NeutralLanguage>` in
  `Directory.Build.props`). The English text is embedded in the main assemblies, so the application
  cannot lose its text by losing a satellite file.
- Each assembly registers its resources once: Core in the `Strings` static constructor, Desktop in
  a module initialiser. Nothing needs to be started first, which keeps tests and previews simple.

There are no generated designer classes. Keys are plain strings, checked by tests (see *Testing*)
rather than by generated code that has to be kept in step with the files.

## File organisation

Resources are grouped by domain. The file name is the domain, and every key in a file begins with
that name.

| Project | File (`Resources/`) | Holds |
| --- | --- | --- |
| Core | `Data.resx` | Light-pollution data installation status |
| Core | `Equipment.resx` | Camera and lens validation, default equipment names |
| Core | `Export.resx` | Plan image sheet text |
| Core | `Places.resx` | Place search and reverse geocoding messages |
| Core | `Planning.resx` | Planning status and framing visibility text |
| Core | `Terrain.resx` | Terrain status and cache messages |
| Core | `Units.resx` | Unit symbols and the value-with-unit pattern |
| Core | `Weather.resx` | Weather summaries, status and errors |
| Desktop | `Common.resx` | Text shared across screens (Cancel, percentage pattern) |
| Desktop | `Dialogs.resx` | Confirmation and message dialogs |
| Desktop | `Locations.resx` | Locations page, search, saved-location editing |
| Desktop | `Planner.resx` | Planner, map layers, camera, timeline, weather panel |
| Desktop | `Settings.resx` | Every Settings category |
| Desktop | `Shell.resx` | Window title, navigation, title bar |
| Desktop | `Supporter.resx` | Supporter licence and Ko-fi text |

About 720 keys in total. Put a new string in the domain of the screen or service that shows it. Use
`Common` only for text that is truly the same thing everywhere; two screens that happen to use the
same English word today may need different words in another language.

## Key naming

`Domain_Area_Meaning_Kind`, in PascalCase segments joined by underscores:

- `Settings_Equipment_Title`
- `Planner_Layers_TerrainReliefOpacity_AccessibleName`
- `Dialogs_DeleteLocation_Message_Format`

Rules:

- The first segment is the resource file. The lookup uses it to pick the file, so a key names one
  place only.
- Name the meaning, not the English wording. A key must still make sense when the text changes.
- Keys are stable. Changing the text does not change the key; changing the meaning needs a new key.
- Common endings: `_Title`, `_Label`, `_Button`, `_Tab`, `_Tooltip`, `_Placeholder`, `_Status`,
  `_Message`, `_AccessibleName`, `_HelpText`, and `_Format` for text with placeholders.
- A few keys are completed at run time from a fixed set: `Settings_Weather_Field_<WeatherField>`,
  `Settings_Appearance_ThemeName_<theme id>`, `Settings_Language_Name_<language>`,
  `Dialogs_RemoveCamera_*` and `Dialogs_RemoveLens_*`.
  Tests check that every member of each set has its key.
- Add a `<comment>` to a resource when the text alone does not say what a placeholder is or where
  the text appears.

## XAML usage

```xml
<Window xmlns:loc="clr-namespace:Noctaxis.Desktop.Localisation" …>
  <TextBlock Text="{loc:Text Settings_Equipment_Title}"/>
  <Button Content="{loc:Text Common_Cancel_Button}"
          ToolTip.Tip="{loc:Text Shell_Close_Tooltip}"/>
  <TextBlock Text="{Binding Opacity, StringFormat={loc:Text Common_PercentWhole_Format}}"/>
</Window>
```

The extension resolves when the XAML loads. Binding format strings are resources too, because the
position of a unit or symbol around a number differs between languages.

## C# usage

```csharp
using Noctaxis.Core.Localisation;

Status = Strings.Get("Planner_Status_Ready");
Message = Strings.Format("Dialogs_DeleteLocation_Message_Format", location.Name);
```

Core and Desktop use the same class. Text is resolved when it is produced, so view-model text built
before a culture change keeps the language it was built in.

## Placeholders

Text with values is one whole sentence with numbered placeholders, never fragments joined in code:

```
Dialogs_DeleteLocation_Message_Format = Delete ‘{0}’? This cannot be undone.
```

A translator can then move `{0}` to wherever the language needs it. Placeholders may carry a format
(`{0:0.#}`, `{0:dd MMM yyyy}`); the value is formatted for the current culture. Do not build a
sentence from a resourced half and a concatenated half.

Plurals keep the behaviour English already had: where the application distinguished one from many,
there are two keys (`Locations_Summary_One`, `Locations_Summary_Many_Format`) and the code chooses.
That is enough for English and not for languages with more plural forms; see *Limitations*.

## Accessibility strings

Accessible names, help text and tooltips are user-facing text and are resourced like any other:

```xml
<Slider x:Name="TerrainReliefOpacitySlider"
        AutomationProperties.Name="{loc:Text Planner_Layers_TerrainReliefOpacity_AccessibleName}"
        AutomationProperties.HelpText="{loc:Text Planner_Layers_AdjustHowStrongly_HelpText}"/>
```

Control names (`x:Name`) and automation IDs are identifiers, not text. They are never localised, and
tests find controls by them, not by English wording.

## Application language

The application language is one persisted value, `AppSettings.Language`, holding a .NET culture
identifier (BCP 47) such as `en-GB`. It is the single source of truth for two things:

1. the language of the text (`CurrentUICulture`, which selects the resources);
2. the language preferred for place names on the map (see *Map labels*).

There is no separate map-language setting. The language does **not** decide how numbers and dates
are written (see *Regional formatting*) or which units are shown (see *Measurement profile*), and
changing it changes neither.

- `Noctaxis.Core/Localisation/AppLanguages.cs` holds the supported identifiers (`SupportedIds`, today
  only `en-GB`), the default (`DefaultId`), `Normalise`, and `Apply`, which sets the UI culture for
  the process and records `Current`. `Apply` never touches `CurrentCulture`.
- The identifier is technical and stable: it is saved as it is, compared ordinally, and never
  translated or formatted. A missing, unknown or unsupported value is treated as `en-GB`.
- The name shown to the user is resource text, one key per language:
  `Settings_Language_Name_<identifier with - as _>` (`Settings_Language_Name_en_GB` is
  "English (United Kingdom)").
- At start-up, before any window or view model exists, `App` reads the saved identifier with
  `AppLanguages.ReadSaved` and applies it, so everything is built in that language. When the setting
  changes, `App` applies the new value.
- The setting behaves like every other: applied and saved at once, no Apply button, no restart
  notice. With one language there is nothing to switch to. See *Adding a translation later* for what
  a second language needs.

## Regional formatting

How a number, date or time is written is the user's regional format: `CultureInfo.CurrentCulture`,
which .NET takes from the operating system. Noctaxis never sets it, never stores it and has no
setting for it in v1. It is not inferred from the application language: English (United Kingdom)
text does not imply British number formats.

- `Strings.Format`, `MeasurementValue.Format` and XAML binding format strings all format with
  `CurrentCulture`.
- The region decides the decimal separator, digit grouping, date order, and the month and day names
  inside a formatted date. A German-region user of the English application therefore sees English
  sentences containing `12,3` and a German month abbreviation. That is what separating the two
  means; see *Limitations*.
- The region never chooses a unit. Under a German region a user on UK Mixed sees `7,7 mi`, not
  kilometres.

## Measurement profile

Which unit a physical quantity is shown in is an explicit user choice, `AppSettings.Units`, with
exactly three profiles. It follows neither the language nor the region, and changing either leaves
it alone. Nothing detects it automatically.

| Name shown | Saved identifier | Meaning |
| --- | --- | --- |
| Metric | `Metric` | SI units as used in everyday life |
| UK Mixed | `UK` | Contemporary British usage: miles and miles per hour, otherwise metric. The default |
| Imperial | `Imperial` | Non-metric terrestrial units. British Imperial and US customary are one profile, because they agree on every unit Noctaxis shows (mile, foot, inch, °F, mph, inHg) |

The names are resource text (`Settings_General_Metric_Option`, `Settings_General_UK_Option`,
`Settings_General_Imperial_Option`); the identifiers are technical, never translated, and compared
ordinally without regard to case.

**One definition.** `Noctaxis.Core/Measurements/MeasurementProfile` defines, for each profile, the
unit and conversion of every physical quantity. Screens ask it (`Settings.MeasurementProfile`); no
screen tests for "Imperial" itself. Values are stored and calculated in SI and converted only for
display. This is the mapping, and the only place it may be changed:

| Quantity | Stored in | Metric | UK Mixed | Imperial | Shown |
| --- | --- | --- | --- | --- | --- |
| Air temperature, dew point | °C | °C | °C | °F | Planner weather, plan image |
| Wind speed, gusts | m/s | km/h | mph | mph | Planner weather, plan image |
| Visibility | km | km | mi | mi | Planner weather; framing status ("Weather visibility: …") |
| Precipitation amount | mm | mm | mm | in | Planner weather |
| Long distance over the ground | km | km | mi | mi | Forecast reuse distance (Settings → Weather); terrain obstruction distance; minimap radius and scale bar |
| Short distance over the ground | m | m, below 1 km | m, below 1 mile | ft, below 1 mile | Terrain obstruction distance (rounded to 10 m or 50 ft); minimap radius and scale bar |
| Ground elevation, height above ground | m | m | m | ft | Ground elevation, effective camera altitude and the terrain-disabled "0 m MSL fallback" wording (Planner); camera height above ground (Settings → Equipment) |
| Atmospheric pressure | hPa | hPa | hPa | inHg | Nothing yet: Noctaxis does not request or show pressure. The rule is fixed for when it does |

Conversions use the modern shared definitions: 1 mi = 1 609.344 m, 1 ft = 0.3048 m, 1 in = 25.4 mm,
°F = °C × 9/5 + 32, 1 inHg = 33.8638866667 hPa.

**Edited values.** Camera height and forecast reuse distance are edited in the profile's unit, to one
decimal place, with the unit named in the label and the accessible name. They are still saved in
metres and kilometres; changing the profile changes nothing that is stored, and a value a control
writes back unchanged is not treated as an edit.

Their limits are defined in SI and nowhere else. Camera height is
`AppSettings.Minimum/MaximumCameraHeightAboveGroundMetres` (0 m to 100 m); the control's maximum is
that value converted to the profile's unit and rounded up to the control's one decimal place
(328.1 ft), so the limit itself can be typed, while what is applied is always held to exactly 100 m
after conversion. A converted limit is never a rule of its own.

**Not governed by the profile.** Astronomical and photographic quantities keep their conventional
units in every profile: focal length and sensor dimensions in mm, field of view, bearing, pitch,
altitude, azimuth and terrain clearance in degrees, times and durations, percentages (cloud,
humidity, moon illumination, opacity), wind direction in degrees. A 400 mm lens is 400 mm under
Imperial. Data sizes (terrain cache in GiB) are not physical quantities and are unaffected.
Developer diagnostics (the terrain debug text and minimap) stay in SI.

**Existing settings.** Every identifier Noctaxis has ever saved keeps its meaning:

| Saved value | Loads as |
| --- | --- |
| `Metric` | Metric. Wind is now shown in km/h, not m/s |
| `UK` | UK Mixed. Short distances are now in metres, not feet |
| `Imperial` | Imperial. Elevation and heights are now in feet, not metres |
| missing, `null`, or any other text | UK Mixed, the default. Before, this was Metric |

A saved `Metric` cannot be told apart from the old default, so it is kept as Metric: an explicit
preference is never discarded. Only a file with no usable value takes the new default.

## Map labels

Noctaxis draws the OpenFreeMap vector basemap itself (`Noctaxis.Desktop/Mapping/Vector`), so it
also chooses which name each label shows.

**Where names come from.** The tiles use the OpenMapTiles schema. The tile request has no language:
the URL is `…/{z}/{x}/{y}.pbf`, taken from the TileJSON document, and every tile carries all of a
feature's names as properties of that feature. One downloaded tile therefore serves every language.
The name-bearing layers are `place`, `water_name`, `mountain_peak`, `poi`, `transportation_name`,
`aerodrome_label`, `park`, `waterway` and `boundary`. Each declares the same name fields:

| Field | Meaning |
| --- | --- |
| `name` | The local name, as mapped in OpenStreetMap |
| `name:<language>` | The name in a language; about seventy, for example `name:en`, `name:de`, `name:cy`, `name:zh-Hans`, `name:sr-Latn` |
| `name_en`, `name_de` | Older forms of `name:en` and `name:de` |
| `name_int` | The international name |
| `name:latin`, `name:nonlatin` | The name in Latin script and in another script, where the data can tell |

Noctaxis draws labels for four of those layers, all as point labels: places (country to
neighbourhood, by zoom), water names, mountain peaks from zoom 12 and the most notable points of
interest from zoom 16. It does not draw road names, road numbers, park or airport names; those
layers are simply not labelled. A label is only drawn where it fits wholly inside its tile.

**Before this change** the name was `name:latin` if present, otherwise `name`, regardless of any
language. A blank `name:latin` suppressed the label even when `name` was present.

**Now** `BasemapLabelNames.Resolve` chooses, for the application language:

1. the name in that language, most specific first: for `en-GB`, `name:en-GB`, then `name:en`, then
   the older `name_en`;
2. otherwise the local name, `name`;
3. otherwise any other usable name: `name:latin`, `name_int`, `name:nonlatin`, `name_en`, then the
   first remaining `name:*` or `name_*` in key order.

Blank values are skipped at every step, so a missing translation never removes a label. A feature
with no name of any kind has no label, as before.

**Changing the language.** Rendered tiles are images held in memory only; nothing rendered is stored
on disk, so there is no stale-language cache to clear. `PlannerMapComposition.ApplyLabelLanguage`
passes the language to the basemap, which discards and redraws only the tiles that contain names:
the reference pass (roads, boundaries, names) in the Planner, or the single full pass elsewhere. The
geography pass, terrain relief, light pollution and the downloaded and decoded vector data are
untouched, and nothing is fetched again. A tile that was being drawn when the language changed is
drawn again rather than shown in the old language. The Planner map takes its language from
`AppLanguages.Current` and follows `AppLanguages.CurrentChanged`. Satellite imagery has no labels of
its own and ignores the language. Nothing else redraws the labels: not the measurement profile and
not the regional format.

Saved-location map images are a separate pipeline and are not affected. Their source is
OpenStreetMap's own raster tiles, whose labels are part of the image and are in the local language;
Noctaxis draws no text of its own on them.

## Invariant persistence and machine formatting

Anything a machine reads is written and parsed with `CultureInfo.InvariantCulture` and never goes
through `Strings`:

- `state.json` and every other persisted file
- cache keys and cache file names
- URLs and query strings (weather, geocoding, terrain, external map links)
- export metadata and machine timestamps
- file names, including the backup made of a corrupt state file
- identifiers, enum names and protocol values, including the language identifier itself
- vector tile requests, which carry no language at all

This pass found two places that used the current culture for machine text and corrected them: the
local time in scouting-card export metadata and the timestamp in the corrupt-state backup file name.

## Strings that are not localised

- Identifiers: control names, automation IDs, setting keys, enum names, layer IDs.
- Product and provider names and their attribution: Noctaxis, OpenFreeMap, Open-Meteo, the light
  pollution atlas, external map providers, theme proper names such as Foxfire.
- Symbols: the `×`, `−`, `□` and `•••` glyphs.
- Catalogue data: object designations and names from OpenNGC.
- Log messages, exception messages for programmer errors and developer diagnostics (terrain debug
  text, camera frame diagnostics). These are for whoever is debugging, in English.
- Technical failure reasons stored with a saved location. They are persisted, so they must not vary
  with the language in use when they were written.

## Missing keys

A key that does not exist is a defect, and it is made obvious rather than hidden:

- The text shown is `⟦Key⟧`, never blank, so the screen still works and the gap is visible.
- `Strings.MissingKey` is raised and a warning is traced once per key.
- Tests fail if any key referred to in C# or XAML does not resolve, and if any resource is unused.

A key that exists in English but not in some future translation is not a defect: the standard
resource fallback shows the English text.

## Adding a translation later

Not done in v1. When it is:

1. Add a satellite file beside each English one, `Settings.de.resx` beside `Settings.resx`, holding
   the same keys. The build produces `de/Noctaxis.Desktop.resources.dll` and the Core equivalent.
   Keys missing from the translation fall back to English.
2. Add its identifier to `AppLanguages.SupportedIds` and its name as
   `Settings_Language_Name_<identifier>`. It then appears under Settings → General → Language, is
   saved as its identifier, is applied at start-up, and the map prefers `name:<language>`.
3. Decide how a change of language reaches text that is already on screen. Text is resolved when it
   is produced: a window's XAML text when the window is built, a view model's text when it is
   computed. Changing the setting today applies to new text produced from then on and to the map
   labels at once, but text already shown stays as it was. Either register the setting as
   restart-required (`RequiresRestart: true`, which the Settings page already knows how to present)
   or make `{loc:Text}` return a refreshable binding and have view models rebuild their text. With
   one language the question cannot arise, so neither is built.
4. Run the layout test with the real translation and fix what it finds.

## Limitations

- English only. The Language setting has one choice, and switching language while running is not
  complete (see *Adding a translation later*, step 3).
- There is no regional-format setting: the format is whatever the operating system gives the user.
- Because text and formats are separate, a formatted date inside an English sentence uses the
  region's month and day names. A translation may want dates formatted with the language's names
  and the region's order; that is not built.
- Measurement profiles: British Imperial and US customary are not distinguished, nothing is detected
  automatically, and a profile cannot be mixed by quantity.
- Map labels: only point labels are drawn, road names are not, and a name is chosen by language
  alone. A place whose local name is in another script and has no English name is shown in that
  script.
- Plurals are one/many. Languages with more forms need a plural-aware formatter.
- No right-to-left layout work has been done.
- Grammatical gender and case are not handled: a placeholder receives the same form wherever it is
  used.
- Coordinates are shown with the culture's decimal separator, like other numbers. Whether they
  should instead always use a full stop is a product decision left open.
- Text persisted in the past stays in the language it was written in (saved-location descriptions,
  default equipment names created on first run).
- Text from outside Noctaxis is shown as received: place names, provider error details, catalogue
  names.

## Testing

- `Noctaxis.Core.Tests/LocalisationTests.cs`: resources load, formats and placeholders, the missing
  key marker and event, English fallback under another culture, quantities and dates following the
  culture, persisted JSON and machine identifiers staying invariant.
- `Noctaxis.Desktop.Tests/LocalisationReadinessTests.cs`: every domain loads and no key is defined
  twice; every key used in source resolves and no resource is orphaned; XAML and C# resolve through
  the same lookup; accessible names come from resources while control names stay fixed; display
  values follow de-DE while map links stay invariant.
- `Noctaxis.Core.Tests/AppLanguageTests.cs`: the language defaults to `en-GB`, unknown values fall
  back to it, the identifier is saved and read as it is in every culture, and applying it (once or
  repeatedly) sets the UI culture while the regional culture, under en-GB, de-DE, tr-TR and fa-IR,
  stays exactly as it was.
- `Noctaxis.Core.Tests/MeasurementUnitsTests.cs`: every quantity's unit and conversion under each of
  the three profiles, with a check that the table covers every quantity; short and long distances,
  scale bars; identifiers parsed the same in every culture; all three profiles round-tripping
  through `state.json` and older values keeping their meaning; the region changing the number and
  never the unit.
- `Noctaxis.Desktop.Tests/MeasurementProfileTests.cs`: the Planner's weather in each profile's units;
  heights and distances edited in the profile's unit and stored in SI; camera, lens and angular
  values identical in every profile; language, measurements and region not changing one another;
  measurements and region never redrawing the map's place names.
- `Noctaxis.Desktop.Tests/LanguageSettingTests.cs`: the setting, its single choice and its name
  from the resources.
- `Noctaxis.Desktop.Tests/BasemapLabelLanguageTests.cs`: the name chosen at each step of the
  fallback chain, a label drawn whichever name a feature has, a language change redrawing only
  name-bearing tiles from data already held, and tile requests carrying no language.
- `Noctaxis.Desktop.Tests/LocalisationLayoutTests.cs`: every main screen and dialog is laid out at
  100%, 150% and 200% text size, in English and with expanded text, and audited for text that is
  trimmed, overflows its box, is cut off by a clipping parent or leaves the window.

Expanded text is a test-only stand-in for a translation. `PseudoLocalisation.Apply()` makes every
resolved string about 35% longer while leaving placeholders alone. It works through an internal hook
on `Strings` that only the test projects can reach and that is scoped to the calling test; it is not
a language, is not in Settings and cannot be switched on in the application.

Tests that read XAML as text use `TestMarkup`, which replaces `{loc:Text Key}` with the English
text, and tests that find controls do so by name rather than by wording.

Set `NOCTAXIS_LAYOUT_AUDIT` to a file path to have the layout test write what it found per screen.
