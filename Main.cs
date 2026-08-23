using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
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
    ///  2. PowerToys Awake — controlled exclusively by writing the Awake module's
    ///     settings file (the same channel PowerToys' own AwakeService uses).
    ///     Requires the Awake module to be enabled in PowerToys (one-time); the
    ///     plugin never launches or closes an Awake process:
    ///         mode 3 (EXPIRABLE) + expirationDateTime  -> "on until 5pm"
    ///         mode 2 (TIMED) + intervalHours/Minutes   -> "on for &lt;duration&gt;"
    ///         mode 0 (PASSIVE)                         -> "off" (icon greys out)
    ///     After each write the plugin verifies the Awake process is still alive
    ///     and reports why it exited if it is gone.
    /// </summary>
    public class Main : IPlugin
    {
        public static string PluginID => "03B5849AB8264F58BC8817772EEBE19D";

        // End of the work shift: 17:00 local time.
        private const int EndHour = 17;

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
        //
        // Controlled exclusively through the Awake module's settings file — the
        // same channel PowerToys' own AwakeService uses (verified against the
        // PowerToys v0.100.x source). With the Awake module enabled, the runner
        // keeps a windowless PowerToys.Awake.exe instance running
        // (--use-pt-config --pid <runner pid>) that watches this file with a
        // ~25 ms throttle and applies whatever mode the file holds. The plugin
        // never launches or closes an Awake process.

        // Awake settings-file modes (PowerToys AwakeMode enum).
        private const int AwakeModePassive = 0;
        private const int AwakeModeTimed = 2;
        private const int AwakeModeExpirable = 3;

        // Module settings file, per PowerToys version:
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

        // Mirrors the Awake settings schema written by PowerToys (Settings.UI.Library AwakeSettings).
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

        // Returns the pid of the running PowerToys.Awake instance, or 0 when none.
        private static int GetAwakePid()
        {
            Process[] procs = Process.GetProcessesByName("PowerToys.Awake");
            try
            {
                return procs.Length == 0 ? 0 : procs[0].Id;
            }
            finally
            {
                foreach (Process p in procs)
                {
                    p.Dispose();
                }
            }
        }

        // Reads the existing settings file (preserving keepDisplayOn / customTrayTimes),
        // applies the mutation, and writes it back. Returns false when the PowerToys
        // settings folder does not exist or the write failed.
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
        private static string AwakeOnUntil(DateTime end)
        {
            int pid = GetAwakePid();
            if (pid == 0)
            {
                return "PowerToys Awake is not running — enable the Awake module in PowerToys (Settings > Awake), then try again";
            }

            bool ok = UpdateAwakeSettings(p =>
            {
                p.Mode = AwakeModeExpirable;
                p.ExpirationDateTime = new DateTimeOffset(end);
            });
            if (!ok)
            {
                return "could not update the PowerToys Awake settings file";
            }

            return VerifyAwakeStillRunning(pid, "on");
        }

        // Awake "on for <duration>". Returns null on success, an error message otherwise.
        private static string AwakeOnTimed(TimeSpan duration)
        {
            int pid = GetAwakePid();
            if (pid == 0)
            {
                return "PowerToys Awake is not running — enable the Awake module in PowerToys (Settings > Awake), then try again";
            }

            bool ok = UpdateAwakeSettings(p =>
            {
                p.Mode = AwakeModeTimed;
                p.IntervalHours = (uint)duration.Hours;
                p.IntervalMinutes = (uint)duration.Minutes;
            });
            if (!ok)
            {
                return "could not update the PowerToys Awake settings file";
            }

            return VerifyAwakeStillRunning(pid, "on");
        }

        // Awake "off". Returns null on success, an error message otherwise.
        // Never closes an Awake process: a running instance is only switched to
        // its inactive (PASSIVE) state — its tray icon greys out, it keeps running.
        private static string AwakeOff()
        {
            int pid = GetAwakePid();

            if (pid == 0 && !File.Exists(AwakeSettingsPath))
            {
                // No Awake instance and no module settings: nothing to do.
                return null;
            }

            bool ok = UpdateAwakeSettings(p =>
            {
                p.Mode = AwakeModePassive;
            });
            if (!ok)
            {
                return "could not update the PowerToys Awake settings file";
            }

            return VerifyAwakeStillRunning(pid, "off");
        }

        // Gives the instance a moment to react to the settings change, then checks
        // that it is still running. Returns null when alive, a diagnostic message
        // when it exited (its tray icon is gone until it starts again — per the
        // PowerToys source the icon is only removed on process exit).
        private static string VerifyAwakeStillRunning(int pid, string action)
        {
            if (pid == 0)
            {
                return null;
            }

            Thread.Sleep(1000);
            try
            {
                using Process p = Process.GetProcessById(pid);
                if (!p.HasExited)
                {
                    return null;
                }
            }
            catch
            {
                // Process is gone.
            }

            return $"PowerToys Awake exited right after being set to {action} — its tray icon is gone until it starts again. Check that the Awake module is still enabled in PowerToys (Settings > Awake)";
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
