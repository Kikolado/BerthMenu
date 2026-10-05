using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StartDock.Models;

namespace StartDock.Services
{
    /// <summary>
    /// Remembers when StartDock first saw each installed app, for the optional
    /// "Recently added" section (AppConfig.ShowNewApps). Get-StartApps doesn't report
    /// install dates, so "new" means "appeared in the installed-apps list since the
    /// last time StartDock looked" — which matches what a user would call new.
    ///
    /// Stored in %AppData%\StartDock\apps-seen.json (AppId → first-seen time, UTC).
    /// The very first time this runs there's no file yet, so every app already
    /// installed is recorded as "seen long ago" — otherwise everything would show up
    /// as recently added on first launch.
    ///
    /// Only touched from the UI thread (InstalledAppsCache calls it after each load or
    /// refresh), so no locking. The file write itself happens in the background.
    /// </summary>
    public static class AppSeenTracker
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StartDock", "apps-seen.json");

        private static Dictionary<string, DateTime>? _firstSeen;

        public static void Record(IEnumerable<InstalledAppInfo> apps)
        {
            bool firstEver = EnsureLoaded();
            bool changed = false;

            foreach (var app in apps)
            {
                if (string.IsNullOrEmpty(app.AppId) || _firstSeen!.ContainsKey(app.AppId))
                    continue;

                _firstSeen[app.AppId] = firstEver ? DateTime.MinValue : DateTime.UtcNow;
                changed = true;
            }

            if (changed || firstEver)
                Save();
        }

        /// <summary>AppIds first seen within <paramref name="window"/>, newest first.</summary>
        public static List<string> GetRecentlyAdded(TimeSpan window)
        {
            EnsureLoaded();
            var cutoff = DateTime.UtcNow - window;
            return _firstSeen!
                .Where(kv => kv.Value > cutoff)
                .OrderByDescending(kv => kv.Value)
                .Select(kv => kv.Key)
                .ToList();
        }

        /// <summary>Loads the file once. Returns true if there was no file yet.</summary>
        private static bool EnsureLoaded()
        {
            if (_firstSeen != null)
                return false;

            _firstSeen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath))
                    return true;

                var loaded = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(FilePath));
                if (loaded != null)
                {
                    foreach (var kv in loaded)
                        _firstSeen[kv.Key] = kv.Value;
                }
            }
            catch
            {
                // Unreadable file — start over, treating this like a first run so
                // nothing is wrongly flagged as new.
                return true;
            }

            return false;
        }

        private static void Save()
        {
            var snapshot = new Dictionary<string, DateTime>(_firstSeen!);
            Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot));
                }
                catch
                {
                    // Not worth failing over — worst case an app shows as new again.
                }
            });
        }
    }
}
