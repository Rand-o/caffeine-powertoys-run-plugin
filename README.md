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
| `caff off` | Stops the Caffeine session entirely (terminates `caffeine64.exe`) and turns PowerToys Awake off. |
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
| `caff off` | `-appexit` (terminates the running instance) |

If an instance is already running, `-replace` is added so the old instance is
closed and the new timer takes effect. The exe is located at
`%USERPROFILE%\Music\caffeine64.exe` (via the `MyMusic` known folder, so
OneDrive redirects are honored).

### PowerToys Awake

Verified against the PowerToys v0.100.x source (same CLI since v0.75):

- CLI: `PowerToys.Awake.exe -t <seconds>` (timed), `-e <datetime>`
  (expirable), `-c/--use-pt-config` (watch the settings file). Awake enforces
  **one instance** via a named mutex.
- When the Awake module is enabled in PowerToys, the runner launches it with
  `--use-pt-config --pid <runner pid>` and steers it by writing
  `%LOCALAPPDATA%\PowerToys\settings\Awake.json` — the same channel the
  runner's own `AwakeService` uses.

The plugin does the same:

1. **Config-managed instance running** (command line contains `-c` /
   `--use-pt-config`, detected via `NtQueryInformationProcess`):
   - `caff on` → write `mode: 3` (EXPIRABLE) + `expirationDateTime` = 5:00 PM
   - `caff <time>` → write `mode: 2` (TIMED) + `intervalHours` / `intervalMinutes`
   - `caff off` → write `mode: 0` (PASSIVE)

   The existing file is read first, so the user's `keepDisplayOn` and
   `customTrayTimes` settings are preserved.
2. **Standalone instance running** (e.g. left over from an earlier `caff`):
   killed and replaced.
3. **No instance running**: the settings file is updated (best effort) and a
   standalone `PowerToys.Awake.exe -t <seconds>` is launched. Such an instance
   calls `AllocConsole()` (Awake behavior when started without a PID binding),
   so the plugin hides its console window in the background.

`PowerToys.Awake.exe` is found from the running process's path, or by scanning
`%LOCALAPPDATA%\Microsoft\PowerToys\<version>\` and
`C:\Program Files\PowerToys\<version>\` (highest version wins).

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
- Caffeine's `-activefor` leaves the app in the tray *inactive* at 5pm —
  `caff off` (or double-clicking the tray icon) removes it. Timed sessions
  (`caff 1:30`) auto-exit the app instead.
- If the Awake module is **disabled** in PowerToys, the plugin runs a
  standalone Awake instance (console window auto-hidden). If you later enable
  the module in the tray while that instance is running, the runner's own
  launch will be rejected by Awake's single-instance mutex — run `caff off`
  first, then re-enable the module.
- Requires PowerToys v0.75+ for the Awake CLI (v0.100.x verified).
- Targets `net9.0-windows`; requires a .NET 9-era PowerToys (0.9x+).
