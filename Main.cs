using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Wox.Plugin;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Caffeine
{
    /// <summary>
    /// "caff" — keeps the work laptop awake with Caffeine (Zhorn Software,
    /// caffeine64.exe from the user's Music folder):
    ///     - caff on      -> -activefor:&lt;minutes until 17:00&gt;  (active until 5pm)
    ///     - caff &lt;time&gt;  -> -exitafter:&lt;minutes&gt;               (auto-exits after the duration)
    ///     - caff off     -> -appoff                             (deactivates; app stays in tray, inactive)
    ///     A running instance is always replaced (-replace) so the timer is reset.
    ///
    /// PowerToys Awake support is being redesigned and is not present in this build.
    /// </summary>
    public class Main : IPlugin
    {
        public static string PluginID => "03B5849AB8264F58BC8817772EEBE19D";

        // End of the work shift: 17:00 local time.
        private const int EndHour = 17;

        private PluginInitContext _context;

        public string Name => "Caffeine";

        public string Description => "Caffeine: on until 5pm, off, or for a set duration";

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
                        $"Caffeine ON until {endOn:hh:mm tt}{DayNote(endOn)}",
                        $"Caffeine active until {endOn:hh:mm tt} ({FormatDuration(endOn - DateTime.Now)}) — Enter to activate",
                        () => ActivateUntil(endOn));

                case "off":
                    if (parts.Length > 1)
                    {
                        return new List<Result> { Invalid("Usage: caff off") };
                    }

                    return Single(
                        "Caffeine OFF",
                        "Deactivates Caffeine (app stays in tray, inactive) — Enter to deactivate",
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
                            $"Caffeine for {FormatDuration(duration)}",
                            $"Active until {endFor:hh:mm tt} — Enter to activate",
                            () => ActivateFor(duration));
                    }

                    return new List<Result>
                    {
                        Invalid($"'{parts[0]}' is not a command or a time — try caff on, caff off, or caff 1:30")
                    };
            }
        }

        // --- Actions ---

        // "caff on": Caffeine active until the next 17:00.
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

            if (errors.Count > 0)
            {
                _context.API.ShowMsg("Caffeine", string.Join(" · ", errors));
                return false;
            }

            _context.API.ShowMsg(
                "Caffeine",
                $"ON until {end:hh:mm tt}{DayNote(end)} — Caffeine ({FormatDuration(end - DateTime.Now)})");
            return true;
        }

        // "caff <duration>": Caffeine active for the given duration, then stops on its own.
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

            if (errors.Count > 0)
            {
                _context.API.ShowMsg("Caffeine", string.Join(" · ", errors));
                return false;
            }

            _context.API.ShowMsg(
                "Caffeine",
                $"ON for {FormatDuration(duration)} until {DateTime.Now + duration:hh:mm tt} — Caffeine");
            return true;
        }

        // "caff off": stop Caffeine.
        private bool DeactivateAll()
        {
            var errors = new List<string>();

            bool caffeineWasRunning = IsCaffeineRunning();
            if (caffeineWasRunning && StartCaffeine("-appoff") == null)
            {
                errors.Add("failed to deactivate Caffeine");
            }

            if (errors.Count > 0)
            {
                _context.API.ShowMsg("Caffeine", string.Join(" · ", errors));
                return false;
            }

            string caffeineState = caffeineWasRunning ? "Caffeine deactivated" : "Caffeine was not running";
            _context.API.ShowMsg("Caffeine", caffeineState);
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
            Hint("on", $"Caffeine until {(EndHour > 12 ? EndHour - 12 : EndHour)}:00 PM (end of shift)"),
            Hint("off", "Turn off Caffeine"),
            Hint("1:30", "Keep Caffeine awake for a duration — 1:30 = 1h 30m, 2 = 2h")
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
