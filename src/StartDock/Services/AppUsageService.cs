using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace StartDock.Services
{
    /// <summary>
    /// When each app was last opened, for the optional "Recently used" section
    /// (AppConfig.ShowRecentApps). Two sources, newest time wins:
    ///
    ///  1. Apps opened from StartDock itself: recorded by RecordLaunch in
    ///     %AppData%\StartDock\apps-used.json (AppId → last launch, UTC).
    ///  2. Apps opened anywhere else in Windows (its Start menu, the taskbar,
    ///     Explorer): read from Windows' own UserAssist list in the registry, the
    ///     same launch history the Start menu uses for its "Most used" list. Windows
    ///     only keeps it while Settings → Privacy → General → "Let Windows improve
    ///     Start and search results by tracking app launches" is on. With that off,
    ///     only StartDock's own launches count.
    ///
    /// Nothing here leaves the computer. UserAssist is only ever read, never changed.
    /// </summary>
    public static class AppUsageService
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StartDock", "apps-used.json");

        private const int MaxOwnEntries = 200;
        private static readonly object Gate = new();
        private static Dictionary<string, DateTime>? _ownLaunches;

        /// <summary>Remembers that StartDock just opened this app (its Get-StartApps AppId).</summary>
        public static void RecordLaunch(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId))
                return;

            Dictionary<string, DateTime> snapshot;
            lock (Gate)
            {
                EnsureLoaded();
                _ownLaunches![appId] = DateTime.UtcNow;

                // Keep the file small: drop the oldest entries past the cap.
                if (_ownLaunches.Count > MaxOwnEntries)
                {
                    var oldestFirst = new List<KeyValuePair<string, DateTime>>(_ownLaunches);
                    oldestFirst.Sort((a, b) => a.Value.CompareTo(b.Value));
                    for (int i = 0; i < oldestFirst.Count - MaxOwnEntries; i++)
                        _ownLaunches.Remove(oldestFirst[i].Key);
                }
                snapshot = new Dictionary<string, DateTime>(_ownLaunches);
            }

            Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot));
                }
                catch
                {
                    // Not worth failing over — this launch just won't count.
                }
            });
        }

        /// <summary>Last-used time (UTC) per app, keyed by <see cref="NormalizeId"/>
        /// of the AppId. Reads the registry, so call it off the UI thread.</summary>
        public static Dictionary<string, DateTime> GetLastUsed()
        {
            var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            lock (Gate)
            {
                EnsureLoaded();
                foreach (var kv in _ownLaunches!)
                    Merge(result, kv.Key, kv.Value);
            }

            try
            {
                ReadUserAssist(result);
            }
            catch
            {
                // Registry unreadable — StartDock's own launches still count.
            }

            return result;
        }

        private static void Merge(Dictionary<string, DateTime> result, string appId, DateTime whenUtc)
        {
            string key = NormalizeId(appId);
            if (!result.TryGetValue(key, out var existing) || whenUtc > existing)
                result[key] = whenUtc;
        }

        /// <summary>
        /// UserAssist: HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{GUID}\Count.
        /// Each value's name is the app (an app id or a program path) written in ROT13;
        /// its data (72 bytes since Windows 7) holds the run count at offset 4 and the
        /// last run time, as a FILETIME, at offset 60.
        /// </summary>
        private static void ReadUserAssist(Dictionary<string, DateTime> result)
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist");
            if (root == null)
                return;

            foreach (string guid in root.GetSubKeyNames())
            {
                using var count = root.OpenSubKey(guid + @"\Count");
                if (count == null)
                    continue;

                foreach (string encodedName in count.GetValueNames())
                {
                    if (count.GetValue(encodedName) is not byte[] data || data.Length < 68)
                        continue;

                    int runCount = BitConverter.ToInt32(data, 4);
                    long fileTime = BitConverter.ToInt64(data, 60);
                    if (runCount <= 0 || fileTime <= 0)
                        continue;

                    DateTime whenUtc;
                    try { whenUtc = DateTime.FromFileTimeUtc(fileTime); }
                    catch { continue; }

                    Merge(result, Rot13(encodedName), whenUtc);
                }
            }
        }

        private static string Rot13(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c >= 'a' && c <= 'z') sb.Append((char)('a' + (c - 'a' + 13) % 26));
                else if (c >= 'A' && c <= 'Z') sb.Append((char)('A' + (c - 'A' + 13) % 26));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // Known-folder ids Windows uses at the start of program paths, both in
        // Get-StartApps' AppIds and in UserAssist. Either side may spell a path with
        // the id or with the real folder, so both are turned into the real folder
        // before comparing.
        private static readonly (string Guid, Environment.SpecialFolder Folder)[] KnownFolders =
        {
            ("{6D809377-6AF0-444B-8957-A3773F02200E}", Environment.SpecialFolder.ProgramFiles),     // Program Files (64-bit)
            ("{905E63B6-C1BF-494E-B29C-65B732D3D21A}", Environment.SpecialFolder.ProgramFiles),     // Program Files
            ("{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}", Environment.SpecialFolder.ProgramFilesX86),  // Program Files (x86)
            ("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}", Environment.SpecialFolder.System),           // System32
            ("{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}", Environment.SpecialFolder.SystemX86),        // SysWOW64
            ("{F38BF404-1D43-42F2-9305-67DE0B28FC23}", Environment.SpecialFolder.Windows),          // Windows
            ("{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}", Environment.SpecialFolder.LocalApplicationData),
            ("{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}", Environment.SpecialFolder.ApplicationData),
            ("{A77F5D77-2E2B-44C3-A6A2-ABA601054A51}", Environment.SpecialFolder.Programs),
        };

        /// <summary>The form AppIds are compared in: a leading known-folder id is
        /// replaced with its real path. App ids without a path are left as they are.</summary>
        public static string NormalizeId(string appId)
        {
            if (appId.Length > 38 && appId[0] == '{' && appId[37] == '}')
            {
                string guid = appId.Substring(0, 38);
                foreach (var (knownGuid, folder) in KnownFolders)
                {
                    if (!string.Equals(guid, knownGuid, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string path = Environment.GetFolderPath(folder);
                    // The 64-bit Program Files, even if this is a 32-bit build.
                    if (knownGuid == "{6D809377-6AF0-444B-8957-A3773F02200E}"
                        && Environment.GetEnvironmentVariable("ProgramW6432") is { Length: > 0 } programW6432)
                        path = programW6432;
                    if (!string.IsNullOrEmpty(path))
                        return path.TrimEnd('\\') + appId.Substring(38);
                    break;
                }
            }
            return appId;
        }

        private static void EnsureLoaded()
        {
            if (_ownLaunches != null)
                return;

            _ownLaunches = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath))
                    return;
                var loaded = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(FilePath));
                if (loaded != null)
                {
                    foreach (var kv in loaded)
                        _ownLaunches[kv.Key] = kv.Value;
                }
            }
            catch
            {
                // Unreadable file — start a fresh history.
            }
        }
    }
}
