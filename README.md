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
| `caff off` | Deactivates Caffeine (the app stays in the tray in its inactive state — it is **not** closed) and turns PowerToys Awake off (its session becomes inactive; the process keeps running — the plugin **never** closes Awake). |
| `caff 1:30` | Both active for **1 hour 30 minutes**, then stop on their own. |
| `caff 2` | Both active for **2 hours** (a bare number is hours). |

Duration format: `H:MM` or bare hours, 1 minute to 30 days.

A toast confirms every action; failures (missing exe, PowerToys not installed)
show a notification and leave Run open.

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

Verified against the PowerToys v0.100.x/v0.101.x source:

- When the Awake module is enabled, the runner launches
  `PowerToys.Awake.exe --use-pt-config --pid <runner pid>`. That instance has
  **no console window** and watches its settings file
  (`%LOCALAPPDATA%\Microsoft\PowerToys\Awake\settings.json`, resolved by
  PowerToys' `SettingPath`) with a ~25 ms throttle.
- Writing that file is exactly how PowerToys' own `AwakeService` (the
  runner's module-control layer) steers Awake — the plugin uses the same
  channel. **The plugin never launches an Awake process itself.**
  (A standalone `PowerToys.Awake.exe -t …` is deliberately avoided: without a
  PID binding, Awake calls `AllocConsole()` and keeps a console window open
  for the whole session — there is no flag to prevent that in any version.)

So the plugin simply writes the file, preserving `keepDisplayOn` /
`customTrayTimes` from the existing one:

- `caff on` → `mode: 3` (EXPIRABLE) + `expirationDateTime` = 5:00 PM
- `caff <time>` → `mode: 2` (TIMED) + `intervalHours` / `intervalMinutes`
- `caff off` → `mode: 0` (PASSIVE)

The running instance is identified by process name; its command line (via
`NtQueryInformationProcess`) is checked for `-c`/`--use-pt-config` to tell a
runner-managed instance apart from a manually started one. A manually started
instance (with its own console window) can't be steered through the file, and
the plugin never closes Awake processes: `caff off` tells you to close its
window, and `caff on`/`caff <time>` ask you to close it first.

**One-time setup:** the Awake module must be enabled in PowerToys
(PowerToys Settings → Awake → *Enable Awake*, or the Awake tray icon →
Enable). If it isn't, `caff on`/`caff <time>` still run Caffeine and show a
notification telling you to enable the module.

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
- If you ever start `PowerToys.Awake.exe` manually from a terminal, the plugin
  leaves it alone and tells you to close its window (it never closes Awake).
  Same for a standalone instance left behind by an older version of this
  plugin — close its console window once.
- Works with PowerToys v0.100.x+ (new settings location) and older versions
  that use `%LOCALAPPDATA%\PowerToys\settings\Awake.json` (the existing file
  is auto-detected).
- Targets `net9.0-windows`; requires a .NET 9-era PowerToys (0.9x+).
