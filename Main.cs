using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wox.Plugin;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Caffeine
{
    /// <summary>
    /// "caff" — keeps the work laptop awake with two independent tools:
    ///
    ///  1. Caffeine (Zhorn Software, caffeine64.exe from the user's Music folder):
    ///     - caff on      -> -activefor:&lt;minutes until 17:00&gt;  (active until 5pm)
    ///     - caff &lt;time&gt;  -> -exitafter:&lt;minutes&gt;               (auto-exits after the duration)
    ///     - caff off     -> -appoff                             (deactivates; app stays in tray, inactive)
    ///     A running instance is always replaced (-replace) so the timer is reset.
    ///
    ///  2. PowerToys Awake (v0.100.x+):
    ///     Controlled exclusively through %LOCALAPPDATA%\Microsoft\PowerToys\Awake\
    ///     settings.json — the same channel PowerToys' own AwakeService uses. The
    ///     runner-managed instance (Awake module enabled in PowerToys) watches that
    ///     file and runs without any console window. This plugin never launches an
    ///     Awake process itself:
    ///         mode 3 (EXPIRABLE) + expirationDateTime  -> "on until 5pm"
    ///         mode 2 (TIMED) + intervalHours/Minutes   -> "on for &lt;duration&gt;"
    ///         mode 0 (PASSIVE)                         -> "off"
    ///     If the module is disabled (no running instance), a notification tells the
    ///     user to enable it in PowerToys first.
    /// </summary>
    public class Main : IPlugin
    {
        public static string PluginID => "03B5849AB8264F58BC8817772EEBE19D";

        // End of the work shift: 17:00 local time.
        private const int EndHour = 17;

        // Awake settings-file modes (PowerToys AwakeMode enum).
        private const int AwakeModePassive = 0;
        private const int AwakeModeTimed = 2;
        private const int AwakeModeExpirable = 3;

        private PluginInitContext _context;

        public string Name => "Caffeine";

        public string Description => "Caffeine + PowerToys Awake: on until 5pm, off, or for a set duration";

        public void Init(PluginInitContext context)
        {
            _context = context;
        }

        public List<Result> Query(Query query)
        {
            string[] parts = (query?.Search ?? string.Empty).Trim()
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
            {
                return NoCommandHints();
            }

            string command = parts[0].ToLowerInvariant();

            switch (command)
            {
                case "on":
                    if (parts.Length > 1)
                    {
                        return new List<Result> { Invalid("Usage: caff on") };
                    }

                    DateTime endOn = NextShiftEnd();
                    return Single(
                        $"Caffeine + Awake ON until {endOn:hh:mm tt}{DayNote(endOn)}",
                        $"Caffeine active until {endOn:hh:mm tt} ({FormatDuration(endOn - DateTime.Now)}) · PowerToys Awake until {endOn:hh:mm tt} — Enter to activate",
                        () => ActivateUntil(endOn));

                case "off":
                    if (parts.Length > 1)
                    {
                        return new List<Result> { Invalid("Usage: caff off") };
                    }

                    return Single(
                        "Caffeine + Awake OFF",
                        "Deactivates Caffeine (app stays in tray, inactive) and turns PowerToys Awake off — Enter to deactivate",
                        () => DeactivateAll());

                default:
                    if (parts.Length > 2)
                    {
                        return new List<Result> { Invalid("Expected a single time after 'caff', e.g. caff 1:30") };
                    }

                    if (TryParseDuration(parts[0], out TimeSpan duration))
                    {
                        DateTime endFor = DateTime.Now + duration;
                        return Single(
                            $"Caffeine + Awake for {FormatDuration(duration)}",
                            $"Both active until {endFor:hh:mm tt} — Enter to activate",
                            () => ActivateFor(duration));
                    }

                    return new List<Result>
                    {
                        Invalid($"'{parts[0]}' is not a command or a time — try caff on, caff off, or caff 1:30")
                    };
            }
        }

        // --- Actions ---

        // "caff on": both tools active until the next 17:00.
        private bool ActivateUntil(DateTime end)
        {
            var errors = new List<string>();

            int minutes = (int)Math.Max(1, Math.Ceiling((end - DateTime.Now).TotalMinutes));
            string caffeineArgs = IsCaffeineRunning()
                ? $"-activefor:{minutes} -replace"
                : $"-activefor:{minutes}";
            if (StartCaffeine(caffeineArgs) == null)
            {
                errors.Add(CaffeineError());
            }

            string awakeError = AwakeOnUntil(end);
            if (awakeError != null)
            {
                errors.Add(awakeError);
            }

            if (errors.Count > 0)
            {
                _context.API.ShowMsg("Caffeine", string.Join(" · ", errors));
                return false;
            }

            _context.API.ShowMsg(
                "Caffeine",
                $"ON until {end:hh:mm tt}{DayNote(end)} — Caffeine + PowerToys Awake ({FormatDuration(end - DateTime.Now)})");
            return true;
        }

        // "caff <duration>": both tools active for the given duration, then stop on their own.
        private bool ActivateFor(TimeSpan duration)
        {
            var errors = new List<string>();

            int minutes = (int)Math.Max(1, Math.Ceiling(duration.TotalMinutes));
            string caffeineArgs = IsCaffeineRunning()
                ? $"-exitafter:{minutes} -replace"
                : $"-exitafter:{minutes}";
            if (StartCaffeine(caffeineArgs) == null)
            {
                errors.Add(CaffeineError());
            }

            string awakeError = AwakeOnTimed(duration);
            if (awakeError != null)
            {
                errors.Add(awakeError);
            }

            if (errors.Count > 0)
            {
                _context.API.ShowMsg("Caffeine", string.Join(" · ", errors));
                return false;
            }

            _context.API.ShowMsg(
                "Caffeine",
                $"ON for {FormatDuration(duration)} until {DateTime.Now + duration:hh:mm tt} — Caffeine + PowerToys Awake");
            return true;
        }

        // "caff off": stop everything.
        private bool DeactivateAll()
        {
            var errors = new List<string>();

            bool caffeineWasRunning = IsCaffeineRunning();
            if (caffeineWasRunning && StartCaffeine("-appoff") == null)
            {
                errors.Add("failed to deactivate Caffeine");
            }

            string awakeError = AwakeOff();
            if (awakeError != null)
            {
                errors.Add(awakeError);
            }

            if (errors.Count > 0)
            {
                _context.API.ShowMsg("Caffeine", string.Join(" · ", errors));
                return false;
            }

            string caffeineState = caffeineWasRunning ? "Caffeine deactivated" : "Caffeine was not running";
            _context.API.ShowMsg("Caffeine", $"{caffeineState} · PowerToys Awake off");
            return true;
        }

        // --- Caffeine (Zhorn) ---

        private static string FindCaffeineExe()
        {
            string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            string path = Path.Combine(music, "caffeine64.exe");
            return File.Exists(path) ? path : null;
        }

        private static string CaffeineError() =>
            $"caffeine64.exe not found in {Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)}";

        private static bool IsCaffeineRunning()
        {
            Process[] procs = Process.GetProcessesByName("caffeine64");
            try
            {
                return procs.Length > 0;
            }
            finally
            {
                foreach (Process p in procs)
                {
                    p.Dispose();
                }
            }
        }

        // Returns the started process, or null on failure.
        private static Process StartCaffeine(string arguments)
        {
            string path = FindCaffeineExe();
            if (path == null)
            {
                Log.Warn($"caffeine64.exe not found in {Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)}", typeof(Main));
                return null;
            }

            try
            {
                return Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = arguments,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(path)
                });
            }
            catch (Exception e)
            {
                Log.Exception("Failed to start Caffeine", e, typeof(Main));
                return null;
            }
        }

        // --- PowerToys Awake ---

        // Returns the pid of the running PowerToys.Awake instance (0 when none) and
        // whether it was started with -c/--use-pt-config (the way the PowerToys
        // runner starts it — such an instance watches the settings file).
        private static int GetAwakeInstance(out bool configManaged)
        {
            configManaged = false;
            Process[] procs = Process.GetProcessesByName("PowerToys.Awake");
            try
            {
                if (procs.Length == 0)
                {
                    return 0;
                }

                int pid = procs[0].Id;
                configManaged = IsConfigManaged(pid);
                return pid;
            }
            finally
            {
                foreach (Process p in procs)
                {
                    p.Dispose();
                }
            }
        }

        // True when the running instance was started with -c/--use-pt-config (the way
        // the PowerToys runner starts it). Such an instance watches the settings file,
        // so it must be steered through the file instead of a new launch (Awake enforces
        // a single instance via a named mutex).
        private static bool IsConfigManaged(int pid)
        {
            string commandLine = GetProcessCommandLine(pid);
            if (commandLine == null)
            {
                return false;
            }

            foreach (string token in commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token == "-c" || token == "--use-pt-config")
                {
                    return true;
                }
            }

            return false;
        }

        // Module settings file, as resolved by PowerToys' SettingPath in v0.100.x+.
        // Awake module settings file, per PowerToys version:
        //   v0.100.x+ : %LOCALAPPDATA%\Microsoft\PowerToys\Awake\settings.json
        //   older     : %LOCALAPPDATA%\PowerToys\settings\Awake.json
        // Prefers the newer location; falls back to the older one when it exists.
        private static string AwakeSettingsPath
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string newPath = Path.Combine(local, "Microsoft", "PowerToys", "Awake", "settings.json");
                if (File.Exists(newPath))
                {
                    return newPath;
                }

                string oldPath = Path.Combine(local, "PowerToys", "settings", "Awake.json");
                if (File.Exists(oldPath))
                {
                    return oldPath;
                }

                return newPath; // default for current PowerToys
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        // Mirrors the Awake.json schema written by PowerToys (Settings.UI.Library AwakeSettings).
        private sealed class AwakeSettingsFile
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = "Awake";

            [JsonPropertyName("version")]
            public string Version { get; set; } = "1.0.0";

            [JsonPropertyName("properties")]
            public AwakeSettingsProperties Properties { get; set; } = new();
        }

        private sealed class AwakeSettingsProperties
        {
            [JsonPropertyName("keepDisplayOn")]
            public bool KeepDisplayOn { get; set; }

            [JsonPropertyName("mode")]
            public int Mode { get; set; }

            [JsonPropertyName("intervalHours")]
            public uint IntervalHours { get; set; }

            [JsonPropertyName("intervalMinutes")]
            public uint IntervalMinutes { get; set; } = 1;

            [JsonPropertyName("expirationDateTime")]
            public DateTimeOffset ExpirationDateTime { get; set; } = DateTimeOffset.Now;

            [JsonPropertyName("customTrayTimes")]
            public Dictionary<string, uint> CustomTrayTimes { get; set; } = new();
        }

        // Reads the existing settings.json (preserving the user's keepDisplayOn /
        // customTrayTimes), applies the mutation, and writes it back. Returns false
        // when the PowerToys settings folder does not exist or the write failed.
        private static bool UpdateAwakeSettings(Action<AwakeSettingsProperties> mutate)
        {
            try
            {
                string path = AwakeSettingsPath;
                if (!Directory.Exists(Path.GetDirectoryName(path)))
                {
                    return false;
                }

                AwakeSettingsFile settings = new();
                if (File.Exists(path))
                {
                    try
                    {
                        AwakeSettingsFile loaded = JsonSerializer.Deserialize<AwakeSettingsFile>(File.ReadAllText(path));
                        if (loaded?.Properties != null)
                        {
                            settings = loaded;
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Exception("Failed to read Awake settings file; using defaults", e, typeof(Main));
                    }
                }

                mutate(settings.Properties);
                File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
                return true;
            }
            catch (Exception e)
            {
                Log.Exception("Failed to write Awake settings file", e, typeof(Main));
                return false;
            }
        }

        // Awake "on until <end>". Returns null on success, an error message otherwise.
        private string AwakeOnUntil(DateTime end)
        {
            int pid = GetAwakeInstance(out bool configManaged);

            if (pid == 0)
            {
                return "PowerToys Awake is not running — enable the Awake module in PowerToys (Settings > Awake), then try again";
            }

            if (!configManaged)
            {
                return "a manually started PowerToys Awake instance is running — close its window, then try again";
            }

            // Steer the runner-managed instance through the settings file it watches.
            bool ok = UpdateAwakeSettings(p =>
            {
                p.Mode = AwakeModeExpirable;
                p.ExpirationDateTime = new DateTimeOffset(end);
            });
            return ok ? null : "could not update the PowerToys Awake settings file";
        }

        // Awake "on for <duration>". Returns null on success, an error message otherwise.
        private string AwakeOnTimed(TimeSpan duration)
        {
            int pid = GetAwakeInstance(out bool configManaged);

            if (pid == 0)
            {
                return "PowerToys Awake is not running — enable the Awake module in PowerToys (Settings > Awake), then try again";
            }

            if (!configManaged)
            {
                return "a manually started PowerToys Awake instance is running — close its window, then try again";
            }

            bool ok = UpdateAwakeSettings(p =>
            {
                p.Mode = AwakeModeTimed;
                p.IntervalHours = (uint)duration.Hours;
                p.IntervalMinutes = (uint)duration.Minutes;
            });
            return ok ? null : "could not update the PowerToys Awake settings file";
        }

        // Awake "off". Returns null on success, an error message otherwise.
        // Never closes an Awake process: a running instance is only switched to
        // its inactive (PASSIVE) state.
        private string AwakeOff()
        {
            int pid = GetAwakeInstance(out bool configManaged);

            if (pid > 0 && !configManaged)
            {
                // A manually started instance (e.g. from a terminal) watches no
                // settings file. This plugin never closes Awake processes — the
                // user has to close that instance's window themselves.
                return "a manually started PowerToys Awake instance is still running — close its window to stop it (this plugin never closes Awake)";
            }

            if (pid == 0 && !File.Exists(AwakeSettingsPath))
            {
                // No Awake instance and no module settings: nothing to do.
                return null;
            }

            // Runner-managed instance (or nothing running): set the stored state
            // to PASSIVE. A running instance picks it up and goes inactive — it
            // keeps running; a (re)started module comes up inactive.
            bool ok = UpdateAwakeSettings(p =>
            {
                p.Mode = AwakeModePassive;
            });
            return ok ? null : "could not update the PowerToys Awake settings file";
        }

        // --- Win32 interop ---

        private const int ProcessQueryLimitedInformation = 0x1000;
        private const int ProcessCommandLineInformation = 60;

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessCommandLineInfo
        {
            public uint Length;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string CommandLine;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int NtQueryInformationProcess(
            IntPtr processHandle,
            int processInformationClass,
            ref ProcessCommandLineInfo processInformation,
            uint processInformationLength,
            out uint returnLength);

        private static string GetProcessCommandLine(int pid)
        {
            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var info = new ProcessCommandLineInfo();
                int status = NtQueryInformationProcess(
                    handle, ProcessCommandLineInformation, ref info, (uint)Marshal.SizeOf(info), out _);
                return status == 0 ? info.CommandLine : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        // --- Query helpers ---

        // Next 17:00 — today when the shift is still running, tomorrow after 5pm.
        private static DateTime NextShiftEnd()
        {
            DateTime now = DateTime.Now;
            DateTime today = now.Date.AddHours(EndHour);
            return now < today ? today : today.AddDays(1);
        }

        private static string DayNote(DateTime end) =>
            end.Date > DateTime.Now.Date ? " (tomorrow)" : string.Empty;

        // Accepts "H:MM" (1:30 = 1h 30m) or bare hours ("2" = 2h).
        // Valid range: 1 minute .. 30 days.
        private static bool TryParseDuration(string value, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            int hours;
            int minutes = 0;

            if (value.Contains(':'))
            {
                string[] parts = value.Split(':');
                if (parts.Length != 2 ||
                    !int.TryParse(parts[0], out hours) ||
                    !int.TryParse(parts[1], out minutes))
                {
                    return false;
                }

                if (minutes < 0 || minutes > 59)
                {
                    return false;
                }
            }
            else
            {
                if (!int.TryParse(value, out hours))
                {
                    return false;
                }
            }

            if (hours < 0 || hours > 24 * 30)
            {
                return false;
            }

            duration = TimeSpan.FromHours(hours).Add(TimeSpan.FromMinutes(minutes));
            return duration >= TimeSpan.FromMinutes(1) && duration <= TimeSpan.FromDays(30);
        }

        private static string FormatDuration(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{(int)t.TotalMinutes}m";

        private static List<Result> NoCommandHints() => new List<Result>
        {
            Hint("on", $"Caffeine + PowerToys Awake until {(EndHour > 12 ? EndHour - 12 : EndHour)}:00 PM (end of shift)"),
            Hint("off", "Turn off Caffeine and PowerToys Awake"),
            Hint("1:30", "Keep both awake for a duration — 1:30 = 1h 30m, 2 = 2h")
        };

        private static Result Hint(string title, string subtitle) => new Result
        {
            Title = title,
            SubTitle = subtitle,
            IcoPath = "Images/icon.png",
            Score = 100
        };

        private static Result Invalid(string message) => new Result
        {
            Title = "Invalid value",
            SubTitle = message,
            IcoPath = "Images/icon.png",
            Score = 100
        };

        private List<Result> Single(string title, string subtitle, Func<bool> action) => new List<Result>
        {
            new Result
            {
                Title = title,
                SubTitle = subtitle,
                IcoPath = "Images/icon.png",
                Score = 100,
                Action = _ => action()
            }
        };
    }
}
