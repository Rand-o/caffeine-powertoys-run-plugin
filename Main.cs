using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
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
    ///     - caff off     -> -appexit                            (terminates the running instance)
    ///     A running instance is always replaced (-replace) so the timer is reset.
    ///
    ///  2. PowerToys Awake (v0.75+ CLI: -t seconds / -e datetime / -c config-watch):
    ///     - If a config-managed instance is running (launched by the PowerToys runner
    ///       with --use-pt-config), it is steered by writing %LOCALAPPDATA%\PowerToys\
    ///       settings\Awake.json (the same channel the runner itself uses):
    ///         mode 3 (EXPIRABLE) + expirationDateTime  -> "on until 5pm"
    ///         mode 2 (TIMED) + intervalHours/Minutes   -> "on for &lt;duration&gt;"
    ///         mode 0 (PASSIVE)                         -> "off"
    ///     - Otherwise a standalone instance is launched with -t &lt;seconds&gt;. A
    ///       standalone Awake (no PID binding) calls AllocConsole(), so its console
    ///       window is hidden in the background.
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
                        "Stops the Caffeine session and turns PowerToys Awake off — Enter to deactivate",
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
            if (caffeineWasRunning && StartCaffeine("-appexit") == null)
            {
                errors.Add("failed to stop Caffeine");
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

            string caffeineState = caffeineWasRunning ? "Caffeine stopped" : "Caffeine was not running";
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

        // Returns the path to PowerToys.Awake.exe (null if not found) and, when an
        // instance is already running, its process id.
        private static string FindAwakeExe(out int runningPid)
        {
            runningPid = 0;
            Process[] procs = Process.GetProcessesByName("PowerToys.Awake");
            try
            {
                if (procs.Length > 0)
                {
                    runningPid = procs[0].Id;
                    try
                    {
                        return procs[0].MainModule?.FileName;
                    }
                    catch
                    {
                        // Access denied etc. — fall through to the disk search.
                    }
                }
            }
            finally
            {
                foreach (Process p in procs)
                {
                    p.Dispose();
                }
            }

            // Per-user install: %LOCALAPPDATA%\Microsoft\PowerToys\<version>\
            // System-wide install: C:\Program Files\PowerToys\<version>\
            string bestDir = null;
            Version bestVersion = null;
            string[] roots =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "PowerToys"),
                @"C:\Program Files\PowerToys"
            };

            foreach (string root in roots)
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                string[] dirs;
                try
                {
                    dirs = Directory.GetDirectories(root);
                }
                catch
                {
                    continue;
                }

                foreach (string dir in dirs)
                {
                    string name = Path.GetFileName(dir).TrimStart('v');
                    if (!Version.TryParse(name, out Version v))
                    {
                        continue;
                    }

                    if (!File.Exists(Path.Combine(dir, "PowerToys.Awake.exe")))
                    {
                        continue;
                    }

                    if (bestVersion == null || v > bestVersion)
                    {
                        bestVersion = v;
                        bestDir = dir;
                    }
                }
            }

            return bestDir == null ? null : Path.Combine(bestDir, "PowerToys.Awake.exe");
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

        private static string AwakeSettingsDir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PowerToys", "settings");

        private static string AwakeSettingsPath => Path.Combine(AwakeSettingsDir, "Awake.json");

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

        // Reads the existing Awake.json (preserving the user's keepDisplayOn /
        // customTrayTimes), applies the mutation, and writes it back. Returns false
        // when the PowerToys settings folder does not exist or the write failed.
        private static bool UpdateAwakeSettings(Action<AwakeSettingsProperties> mutate)
        {
            try
            {
                if (!Directory.Exists(AwakeSettingsDir))
                {
                    return false;
                }

                AwakeSettingsFile settings = new();
                if (File.Exists(AwakeSettingsPath))
                {
                    try
                    {
                        AwakeSettingsFile loaded = JsonSerializer.Deserialize<AwakeSettingsFile>(File.ReadAllText(AwakeSettingsPath));
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
                File.WriteAllText(AwakeSettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
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
            int pid = 0;
            string exe = FindAwakeExe(out pid);

            if (pid > 0 && IsConfigManaged(pid))
            {
                // Runner-managed instance: steer it through the settings file.
                bool ok = UpdateAwakeSettings(p =>
                {
                    p.Mode = AwakeModeExpirable;
                    p.ExpirationDateTime = new DateTimeOffset(end);
                });
                return ok ? null : "could not update the PowerToys Awake settings file";
            }

            if (exe == null)
            {
                return "PowerToys.Awake.exe not found — is PowerToys installed?";
            }

            if (pid > 0)
            {
                // Replace a standalone instance left over from an earlier "caff".
                KillProcess(pid, "PowerToys.Awake");
            }

            // Best effort: keep the module's stored state consistent too.
            UpdateAwakeSettings(p =>
            {
                p.Mode = AwakeModeExpirable;
                p.ExpirationDateTime = new DateTimeOffset(end);
            });

            long seconds = (long)Math.Max(1, Math.Ceiling((end - DateTime.Now).TotalSeconds));
            Process started = LaunchAwake(exe, $"-t {seconds}");
            if (started == null)
            {
                return "failed to launch PowerToys.Awake.exe";
            }

            HideConsoleWindow(started.Id);
            return null;
        }

        // Awake "on for <duration>". Returns null on success, an error message otherwise.
        private string AwakeOnTimed(TimeSpan duration)
        {
            int pid = 0;
            string exe = FindAwakeExe(out pid);

            if (pid > 0 && IsConfigManaged(pid))
            {
                bool ok = UpdateAwakeSettings(p =>
                {
                    p.Mode = AwakeModeTimed;
                    p.IntervalHours = (uint)duration.Hours;
                    p.IntervalMinutes = (uint)duration.Minutes;
                });
                return ok ? null : "could not update the PowerToys Awake settings file";
            }

            if (exe == null)
            {
                return "PowerToys.Awake.exe not found — is PowerToys installed?";
            }

            if (pid > 0)
            {
                KillProcess(pid, "PowerToys.Awake");
            }

            UpdateAwakeSettings(p =>
            {
                p.Mode = AwakeModeTimed;
                p.IntervalHours = (uint)duration.Hours;
                p.IntervalMinutes = (uint)duration.Minutes;
            });

            long seconds = (long)Math.Max(1, Math.Ceiling(duration.TotalSeconds));
            Process started = LaunchAwake(exe, $"-t {seconds}");
            if (started == null)
            {
                return "failed to launch PowerToys.Awake.exe";
            }

            HideConsoleWindow(started.Id);
            return null;
        }

        // Awake "off". Returns null on success, an error message otherwise.
        private string AwakeOff()
        {
            int pid = 0;
            FindAwakeExe(out pid);

            if (pid > 0)
            {
                if (IsConfigManaged(pid))
                {
                    bool ok = UpdateAwakeSettings(p =>
                    {
                        p.Mode = AwakeModePassive;
                    });
                    return ok ? null : "could not update the PowerToys Awake settings file";
                }

                KillProcess(pid, "PowerToys.Awake");
                return null;
            }

            // Nothing running: clear the stored state so a (re)started module stays off.
            UpdateAwakeSettings(p =>
            {
                p.Mode = AwakeModePassive;
            });
            return null;
        }

        // Returns the started process, or null on failure.
        private static Process LaunchAwake(string exe, string arguments)
        {
            try
            {
                return Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = arguments,
                    UseShellExecute = false
                });
            }
            catch (Exception e)
            {
                Log.Exception("Failed to launch PowerToys.Awake", e, typeof(Main));
                return null;
            }
        }

        private static void KillProcess(int pid, string name)
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                p.Kill();
            }
            catch (Exception e)
            {
                Log.Exception($"Failed to kill {name} (pid {pid})", e, typeof(Main));
            }
        }

        // A standalone Awake (started without a PID binding) calls AllocConsole(),
        // which shows a console window for the whole session. Best-effort: find the
        // process's top-level window and hide it. Runs on a background thread.
        private static void HideConsoleWindow(int pid)
        {
            Task.Run(() =>
            {
                try
                {
                    long deadline = Environment.TickCount64 + 5000;
                    while (Environment.TickCount64 < deadline)
                    {
                        bool hidden = false;
                        EnumWindows((hWnd, _) =>
                        {
                            if (GetWindowThreadProcessId(hWnd, out uint procId) == (uint)pid)
                            {
                                ShowWindow(hWnd, SW_HIDE);
                                hidden = true;
                                return false;
                            }

                            return true;
                        }, IntPtr.Zero);

                        if (hidden)
                        {
                            return;
                        }

                        Thread.Sleep(100);
                    }
                }
                catch
                {
                    // Cosmetic only — never break the plugin over a hidden window.
                }
            });
        }

        // --- Win32 interop ---

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;

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
