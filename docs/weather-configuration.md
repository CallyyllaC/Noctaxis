# Open-Meteo weather configuration

Noctaxis uses the Open-Meteo Forecast API. No account or API key is required.
The **Settings → Weather** tab controls which meteorological variables are
requested and which weather/astronomy fields appear in the inspector and PNG
metadata.

Hourly requests use exact WGS84 coordinates, UTC Unix timestamps, metric source
units, and a bounded `HttpClient` timeout. Provider JSON is mapped into internal
domain records before it reaches planning or UI code. Missing arrays and values
remain visibly unavailable; network failures and rate limits never create
synthetic conditions.

Normal planning refreshes are debounced. Successful results are cached with
their request coordinate and retrieval timestamp. A cached result is eligible
only when it represents the same forecast hour, is closer than the configured
great-circle radius (5 km by default), and is less than ten minutes old.

**Refresh weather** and both PNG export actions bypass every cache check and
request fresh weather for the exact current coordinates. A successful forced
request replaces the normal cache entry. A failed manual refresh retains the
last valid on-screen weather; a failed export aborts before any PNG is written
or copied.

Sunrise, sunset, twilight, astronomical darkness, moon phase/illumination, and
moonrise/moonset come from the existing Astronomy Engine integration rather
than being duplicated from a weather response. All displayed and exported
times use the effective timezone selected in General settings.

Automated tests use stored Open-Meteo sample JSON and fake HTTP handlers. They
never contact a live weather service.
