# Logs and diagnostics export

Noctaxis is a windowed application with no console, so its diagnostics go to files in the data folder. From 0.2 it
keeps one log per session, compresses earlier days into one ZIP per day, keeps the ten most recent of those days, and
lets the user export everything from **Settings → Diagnostics → Export Logs**.

The code is in `Noctaxis.Desktop/Diagnostics`:

| Type | Does |
| --- | --- |
| `RollingFileLoggerProvider` | Writes the session log and starts a new file when the local date changes |
| `LogArchiver` | Archives earlier days, recovers interrupted archives, applies retention |
| `LogFileNames` | The names Noctaxis owns in the logs folder; everything else there is left alone |
| `DiagnosticsExportService` | Builds the export ZIP |

## Where the logs are

`<data folder>/logs`, from `PlatformUserDataPathProvider`:

- **Windows:** `%APPDATA%\Noctaxis\logs`
- **Linux:** `~/.config/Noctaxis/logs` (`$XDG_CONFIG_HOME/Noctaxis/logs`)
- **macOS:** `~/Library/Application Support/Noctaxis/logs`

A harness can send a run's logs somewhere else by setting `NOCTAXIS_LOG_DIRECTORY` (see *Tests and harnesses*). Normal
runs never set it.

```
logs/
  Noctaxis-2026-10-18_09-15-00.log      today's sessions, uncompressed
  Noctaxis-2026-10-18_09-15-00-2.log    a second launch in the same second
  Noctaxis-2026-10-18_14-02-41.log      the session being written now
  Noctaxis-2026-10-17.zip               one archive per earlier day that had logs; at most ten
  Noctaxis-2026-10-15.zip
  …
  Noctaxis-logs.lock                    held while archives are written, pruned or exported
```

Names use the local date and wall-clock time, with invariant digits and the Gregorian calendar whatever the regional
format, so they sort by date.

## Session logs

- **One file per session.** Each file is created new (`FileMode.CreateNew`). If the name is already taken, for example
  by several launches in one second or by a wall-clock time that a clock change repeats, a suffix `-2`, `-3` … is added.
  No earlier session is ever opened, truncated or overwritten.
- **Entries belong to the local date they are written on**, not the date the session started. Every write compares
  the local date with the date of the current file. When the date has changed, because midnight passed or the clock
  or time zone changed, the file is closed and a new one is started for the new date. The check runs on each write,
  so it needs no midnight timer. A session left open over midnight starts its new file at its first entry after
  midnight.
- **Format and content are unchanged:** a UTC timestamp, the level (`TRC` … `CRT`), the category and the message,
  with any exception. The category filters in `App.ConfigureServices` are unchanged too. A file stops growing at
  5 MiB.
- The file is shared for reading only, so an export can copy it while it grows.

## Daily archives

When a launch starts, and again whenever the date changes while Noctaxis runs, `LogArchiver.Run` runs on a
background thread, so logging never waits for it. It holds `Noctaxis-logs.lock`, an exclusively opened file (an
advisory lock on Linux and macOS), so two runs never overlap, whether they come from two threads, two processes or
an export. A run that cannot get the lock skips its turn, and the next launch or change of date tries again.

Each run:

1. **Recovers.** It deletes leftover temporary archives (`Noctaxis-<date>.<id>.zip.tmp`). Only a run holding the lock
   writes one, so any that remain come from a run that was interrupted. That run deleted no sources, because sources
   are only deleted after the archive is in place.
2. **Archives each earlier date.** It takes the session logs dated before today, groups them by date and opens each
   one exclusively. A file that is still being written can't be opened that way (the writer allows reading only), so
   it waits for a later run. Noctaxis' own current file is skipped by name as well. For each date it then:
   - writes a temporary ZIP holding the date's existing archive, if there is one, plus the new logs. A log the
     archive already holds with the same content is not added again. A different log with the same name is added
     under `~2`, `~3` …;
   - reads the temporary ZIP back and checks that it holds exactly the expected entries, each with the same length
     and SHA-256 as was written;
   - moves it over `Noctaxis-<date>.zip`, so an existing archive is only ever replaced by one that holds everything it
     held;
   - only then deletes the sources. A source that can't be deleted is found in the archive, unchanged, by the next run
     and deleted then.
3. **Applies retention.** It keeps the archives of the ten most recent dates, counting only dates that have an
   archive. Today never counts: today's logs stay uncompressed and never take one of the ten places, and today's
   archive is never deleted (one exists only after the clock was set back). Days when Noctaxis wasn't used don't
   count. Many launches on one day add files to that day only, so they never evict history. Retention is not by age,
   launches or consecutive days.

**Failures.** If writing or checking an archive fails, the sources are kept, the temporary file is removed and an
`[ERR]` entry from `Noctaxis.Desktop.Diagnostics.LogArchiver` is written to the current log. Noctaxis carries on
normally and the next run tries again. An existing archive that doesn't read completely is never overwritten. It is
renamed aside as `Noctaxis-<date>.corrupt-<id>.zip`, a new archive is started for that date, and the set-aside file
is deleted only once its date would no longer have been kept. A file with an archive's name that doesn't open as an
archive holds no place in retention, and neither does a temporary file.

**Clock changes.** Logs dated after today (the clock was set back) wait, uncompressed, until their date is in the
past. Nothing is deleted because of a clock change. A date that comes round again is merged into its existing archive.
A 0.1 log (`noctaxis-yyyyMMdd-HHmmss-pid.log`, named by its UTC start) is archived under its local start date.

Only files with the names above are ever read, moved or deleted. Any other file in the logs folder is left alone.

## Export Logs

**Settings → Diagnostics** is shown to everyone. It holds the heading, a short description, the **Export Logs** button
and a note that the archive may contain file paths or location information and should be reviewed before sharing.
The terrain developer tools below it are still shown only when Noctaxis starts with `NOCTAXIS_DIAGNOSTICS=1`.

The button opens the platform's own Save File dialog, suggesting `Noctaxis-Diagnostics-<yyyy-MM-dd-HHmm>.zip` in local
time. Cancelling it does nothing. Otherwise the export is built off the UI thread into a temporary file, then copied
to the chosen file. On success the Diagnostics page says where it was saved. On failure the page and the notice bar
both report the error, and a partly written file is removed. The logs folder is only ever read. Nothing is uploaded.

The ZIP holds:

| Path | Content |
| --- | --- |
| `diagnostics.json` | The application and runtime, and an inventory of the other files |
| `archived/Noctaxis-<date>.zip` | Each retained daily archive, stored unchanged (byte for byte) |
| `sessions/<name>.log` | Every uncompressed session log: today's, a copy of the one being written, and any earlier log still awaiting its archive |

The live log is copied through a read that shares the file for reading, writing and deletion. Logging carries on
during the copy, and the file is never locked, truncated or reordered. The copy stops at the file's length when the
read began, trimmed to the last complete line. Each log copy is limited to slightly more than the logger's own
5 MiB limit. The export holds the archive lock while it reads, so no archive is replaced or pruned while it is copied.
It never triggers archiving or retention itself.

`diagnostics.json` holds: format version; export time (UTC) and the local UTC offset; application name, version and
build configuration; operating system description, version, platform and architecture; .NET framework description,
runtime version, runtime identifier and process architecture; processor count, garbage-collector mode, working set,
managed heap and uptime; current and UI culture names; the logging limits; and, for each included file, its path in
the ZIP, kind, date, size, SHA-256 and whether it was cut short.

**Privacy.** The export holds no user, machine or host names, no hardware or network identifiers, no paths in the
manifest, no settings, no time-zone name and no secrets. It holds only files named as Noctaxis logs: never state
files, settings, saved locations, equipment, caches, the Lorenz atlas or anything else from the data folder. The log
text itself may hold file paths (the cache folder at start-up) and coordinates or place names from warnings, which is
why the Settings note asks the user to review the export before sharing it. The supporter licence is never logged
(see [supporter-licensing.md](supporter-licensing.md)).

## Tests and harnesses

Tests never touch the user's logs folder. `Program.FileLogging` is null under test, so the application under test
writes no file log, and `DiagnosticsExportService.ForCurrentProcess()` then exports `diagnostics.json` only. Logging
tests (`RollingFileLoggerProviderTests`, `LogArchiverTests`, `DiagnosticsExportTests`, `DiagnosticsSettingsTests`)
each use a temporary directory they own and delete (`LogTestDirectory`), an injected `TimeProvider` with a fixed time
zone (`ManualTimeProvider`, `LogTestZones`), and inline archiving, so they are deterministic. The production default,
background archiving, has a test of its own.

`artifacts/performance-prealpha/native-perf.ps1` starts the real application. It sets `NOCTAXIS_LOG_DIRECTORY` to a
folder of its own under `out\` for each run, so profiling never adds to, prunes or reads the user's logs. It still uses
the user's real data folder: that is an explicit opt-in, documented in its README, and `run.ps1` refuses to run
without a state backup, which it restores and checks after each run. The wiki capture host
(`scripts/generate-wiki-screenshots.ps1`) runs with its own data and writes no file log.

The CI package smoke test (`platform-package.yml`, `scripts/check-startup-log.sh`) reads the newest `*.log` in the
platform logs folder of a fresh runner, which is still the session it started.

## Limitations

- In-use detection for logs written by another process relies on file sharing (Windows) and advisory `flock` locks
  (Linux and macOS). On network file systems that don't support those locks, .NET takes no lock for a writer, so a
  log another machine is writing could be archived. Noctaxis allows one instance per user and data folder, so this
  needs two machines writing one shared, synchronised logs folder.
- A clock set far ahead and back again can leave an archive dated in the future, which counts as the newest until
  that date passes.
- An archive is not written if the process is killed while writing it. It is recovered at the next launch.
- A 0.1 session that ran over midnight is archived under the date it started.
