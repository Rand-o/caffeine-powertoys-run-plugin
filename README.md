# Caffeine — PowerToys Run plugin

Keeps a work laptop awake with **two independent tools at once**:

- **Caffeine** ([Zhorn Software](https://www.zhornsoftware.co.uk/caffeine/)) —
  `caffeine64.exe` from the user's **Music folder** (simulates a keypress every
  59 s so Windows never sleeps/locks)
- **PowerToys Awake** (the PowerToys module that overrides system sleep)

Both are turned on and off together by one keyword.

## Usage

Type in PowerToys Run (default shortcut `Alt+Space`):

| Query | What happens |
| --- | --- |
| `caff` | Shows usage hints |
| `caff on` | Caffeine + Awake active **until 5:00 PM** — no matter the current time (before 5pm → today, after 5pm → tomorrow). Caffeine is started if it isn't running, or its timer is reset if it is. |
| `caff off` | Deactivates Caffeine (the app stays in the tray in its inactive state — it is **not** closed) and turns PowerToys Awake off (its session becomes inactive; the process keeps running and its tray icon greys out — the plugin **never** closes Awake). |
| `caff 1:30` | Both active for **1 hour 30 minutes**, then stop on their own. |
| `caff 2` | Both active for **2 hours** (a bare number is hours). |

Duration format: `H:MM` or bare hours, 1 minute to 30 days.

A toast confirms every action; failures show a notification and leave Run open.

## How it works

### Caffeine (Zhorn)

| Command | Caffeine switch(es) |
| --- | --- |
| `caff on` | `-activefor:<minutes until 17:00>` (app becomes inactive at 5pm; stays in tray) |
| `caff <time>` | `-exitafter:<minutes>` (app auto-exits after the duration) |
| `caff off` | `-appoff` (deactivates the running instance; the app stays in the tray, inactive) |

If an instance is already running, `-replace` is added so the old instance is
closed and the new timer takes effect. The exe is located at
`%USERPROFILE%\Music\caffeine64.exe` (via the `MyMusic` known folder, so
OneDrive redirects are honored).

### PowerToys Awake

The plugin controls Awake **exclusively through the Awake module's settings
file** — the same channel PowerToys' own `AwakeService` uses (verified against
the PowerToys v0.100.x source):

- With the Awake module enabled, the PowerToys runner keeps a **windowless**
  `PowerToys.Awake.exe` instance running (`--use-pt-config --pid <runner
  pid>`). That instance watches its settings file with a ~25 ms throttle and
  applies whatever mode the file holds.
- The plugin simply writes the file (preserving `keepDisplayOn` /
  `customTrayTimes` from the existing one):
  - `caff on` → `mode: 3` (EXPIRABLE) + `expirationDateTime` = 5:00 PM
  - `caff <time>` → `mode: 2` (TIMED) + `intervalHours` / `intervalMinutes`
  - `caff off` → `mode: 0` (PASSIVE)
- **The plugin never launches or closes an Awake process.** If no
  `PowerToys.Awake.exe` is running, `caff on` / `caff <time>` report that and
  Caffeine still works.
- **Self-check:** after each write the plugin waits a second and verifies the
  Awake process is still alive. If it exited, the toast says so explicitly —
  per the PowerToys source the tray icon can only disappear if the process
  exits (in passive mode it just greys out).

**One-time setup:** the Awake module must be enabled in PowerToys
(PowerToys Settings → Awake → *Enable Awake*). That's what starts the
windowless instance that watches the file. If you ever start
`PowerToys.Awake.exe` manually from a terminal, close that window — a
manually started instance doesn't watch the settings file.

**Tray icon:** while the module is enabled the Awake process never exits, so
the icon is always visible:

- `caff on` / `caff <time>` → active icon (tooltip shows the expiry time or a
  live countdown)
- `caff off` → disabled (greyed) icon — still in the tray

If you don't see it, Windows 11 likely parked it in the hidden-icons overflow
(`^` next to the clock) — drag it out, or enable *PowerToys Awake* under
Settings → Personalization → Taskbar → *Other system tray icons*.

## Building (Linux cross-compile)

Prerequisite: .NET 9 SDK (`EnableWindowsTargeting` makes Windows the target
without a Windows machine).

```bash
./build.sh
```

Builds Release for x64 (the distributed artifact) and ARM64 (compile-check
only), then produces:

- `dist/Caffeine/` — the install folder
- `dist/Caffeine.zip` — the same, zipped

## Installing (Windows 11)

1. Copy `dist/Caffeine/` to:
   `%LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Plugins\Caffeine\`
   (or `Expand-Archive .\Caffeine.zip -DestinationPath "$env:LOCALAPPDATA\Microsoft\PowerToys\PowerToys Run\Plugins"`)
2. Put `caffeine64.exe` in your **Music** folder.
3. Restart PowerToys (tray icon → Quit, relaunch) so it rescans plugins.
4. Type `caff` in PowerToys Run → pick a hint → Enter.

## Notes & edge cases

- **5 PM is fixed** at 17:00 local time (`EndHour` in `Main.cs` if you ever
  want a different end-of-shift).
- `caff on` after 5pm targets **tomorrow's** 5pm (the result says so).
- `caff off` deactivates Caffeine with `-appoff` — the app stays in the tray
  (empty cup) and can be re-activated with `caff on` or by double-clicking
  the tray icon. Timed sessions (`caff 1:30`) auto-exit the app instead.
- The Awake part needs the **Awake module enabled** in PowerToys (one-time).
  While it's disabled, the Awake side of `caff on`/`caff <time>` reports that
  and Caffeine still works.
- Works with PowerToys v0.100.x+ (new settings location) and older versions
  that use `%LOCALAPPDATA%\PowerToys\settings\Awake.json` (the existing file
  is auto-detected).
- Targets `net9.0-windows`; requires a .NET 9-era PowerToys (0.9x+).
