# Caffeine — PowerToys Run plugin

Keeps a work laptop awake with **Caffeine** ([Zhorn Software](https://www.zhornsoftware.co.uk/caffeine/)) —
`caffeine64.exe` from the user's **Music folder** (simulates a keypress every
59 s so Windows never sleeps/locks).

> **PowerToys Awake support is being redesigned** and is not present in the
> current build. The plugin currently controls Caffeine only.

## Usage

Type in PowerToys Run (default shortcut `Alt+Space`):

| Query | What happens |
| --- | --- |
| `caff` | Shows usage hints |
| `caff on` | Caffeine active **until 5:00 PM** — no matter the current time (before 5pm → today, after 5pm → tomorrow). Caffeine is started if it isn't running, or its timer is reset if it is. |
| `caff off` | Deactivates Caffeine (the app stays in the tray in its inactive state — it is **not** closed). |
| `caff 1:30` | Caffeine active for **1 hour 30 minutes**, then stops on its own. |
| `caff 2` | Caffeine active for **2 hours** (a bare number is hours). |

Duration format: `H:MM` or bare hours, 1 minute to 30 days.

A toast confirms every action; failures (missing exe) show a notification and
leave Run open.

## How it works

| Command | Caffeine switch(es) |
| --- | --- |
| `caff on` | `-activefor:<minutes until 17:00>` (app becomes inactive at 5pm; stays in tray) |
| `caff <time>` | `-exitafter:<minutes>` (app auto-exits after the duration) |
| `caff off` | `-appoff` (deactivates the running instance; the app stays in the tray, inactive) |

If an instance is already running, `-replace` is added so the old instance is
closed and the new timer takes effect. The exe is located at
`%USERPROFILE%\Music\caffeine64.exe` (via the `MyMusic` known folder, so
OneDrive redirects are honored).

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
- Targets `net9.0-windows`; requires a .NET 9-era PowerToys (0.9x+).
