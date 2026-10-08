using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BerthMenu.Models;

namespace BerthMenu.Services
{
    /// <summary>
    /// Loads and saves <see cref="AppConfig"/> as JSON under
    /// %AppData%\BerthMenu\config.json, plus exposes the app's data folders.
    /// </summary>
    public class ConfigService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
        };

        public string AppDataFolder { get; }
        public string IconCacheFolder { get; }
        public string BackgroundImageFolder { get; }
        public string ConfigFilePath { get; }

        public ConfigService()
        {
            AppDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BerthMenu");
            IconCacheFolder = Path.Combine(AppDataFolder, "IconCache");
            BackgroundImageFolder = Path.Combine(AppDataFolder, "Background");
            ConfigFilePath = Path.Combine(AppDataFolder, "config.json");

            Directory.CreateDirectory(AppDataFolder);
            Directory.CreateDirectory(IconCacheFolder);
            Directory.CreateDirectory(BackgroundImageFolder);
        }

        /// <summary>Copies a user-picked background image into BackgroundImageFolder as
        /// "background&lt;ext&gt;", first deleting whatever was there from an earlier pick
        /// (including one with a different extension — switching from a .png to a .jpg
        /// shouldn't leave the old .png behind forever). Returns the copy's own path,
        /// which is what AppConfig.BackgroundImagePath should store — never the original
        /// sourcePath — so the background keeps working even if the user later moves,
        /// renames, or deletes the file they originally picked.</summary>
        public string ImportBackgroundImage(string sourcePath)
        {
            ClearBackgroundImage();

            string ext = Path.GetExtension(sourcePath);
            if (string.IsNullOrEmpty(ext))
                ext = ".png";

            string destPath = Path.Combine(BackgroundImageFolder, "background" + ext);
            File.Copy(sourcePath, destPath, overwrite: true);
            return destPath;
        }

        /// <summary>Deletes any previously-imported background image (see
        /// ImportBackgroundImage) — used both when the user removes the background
        /// image entirely and as the first step of importing a new one.</summary>
        public void ClearBackgroundImage()
        {
            try
            {
                foreach (var old in Directory.GetFiles(BackgroundImageFolder, "background.*"))
                    File.Delete(old);
            }
            catch
            {
                // Best-effort — a leftover file here is cosmetic clutter, not a
                // correctness problem (AppConfig.BackgroundImagePath is always what
                // actually decides which file, if any, gets loaded).
            }
        }

        /// <summary>True when Load found no config.json — a new install, or a
        /// Reset — so BerthMenu starts with the welcome window.</summary>
        public bool IsFirstRun { get; private set; }

        public AppConfig Load()
        {
            try
            {
                if (!File.Exists(ConfigFilePath))
                {
                    // First run (or after Settings → Reset): App shows the welcome
                    // window (Views/WelcomeWindow).
                    IsFirstRun = true;
                    var fresh = new AppConfig();
                    Save(fresh);
                    return fresh;
                }

                var json = File.ReadAllText(ConfigFilePath);
                var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();

                // A pre-v2 config has no "Categories" property at all — AppConfig no
                // longer declares the old flat "Icons" list, so System.Text.Json just
                // silently drops it and leaves Categories at its empty default. Recover
                // it here, once, before that information is gone for good: wrap every
                // old top-level icon into a single new "Pinned" category, ordered by
                // whatever free-grid slot it used to occupy.
                if (config.Version < AppConfig.SchemaVersion && config.Categories.Count == 0)
                {
                    config = MigrateFromFreeGrid(json, config);
                    Save(config); // persist the migrated shape now so this only ever runs once
                }

                return config;
            }
            catch (Exception ex)
            {
                // A corrupt config file shouldn't take the whole app down. Back it up
                // and fall back to defaults so the user still gets a working dock.
                TryBackupCorruptConfig(ex);
                return new AppConfig();
            }
        }

        /// <summary>Upgrades a schema-version-1 config (a flat, freely-placed
        /// <c>Icons</c> list, each with its own <c>SortOrder</c> grid slot — both
        /// removed in v2) into v2's <c>Categories</c> shape. Reads the raw JSON
        /// directly rather than a strongly-typed legacy model, since the only
        /// pre-v2-specific field this needs (SortOrder) no longer exists on
        /// DockIcon at all — everything else on a legacy icon (including a
        /// folder's own Children) deserializes straight into the current DockIcon
        /// shape unchanged, so there's nothing legacy-specific to model for those.</summary>
        private static AppConfig MigrateFromFreeGrid(string originalJson, AppConfig config)
        {
            try
            {
                if (JsonNode.Parse(originalJson)?.AsObject() is JsonObject root &&
                    root["Icons"] is JsonArray legacyIcons)
                {
                    var ordered = legacyIcons
                        .Where(node => node != null)
                        .OrderBy(node => (int?)node!["SortOrder"] ?? 0)
                        .Select(node => node!.Deserialize<DockIcon>(JsonOptions))
                        .Where(icon => icon != null)
                        .Cast<DockIcon>()
                        .ToList();

                    if (ordered.Count > 0)
                        config.Categories.Add(new Category { Name = "Pinned", Icons = ordered });
                }
            }
            catch
            {
                // Best-effort — an empty dock (no categories) is a safe fallback if the
                // old file's shape is somehow unrecognizable.
            }

            config.Version = AppConfig.SchemaVersion;
            return config;
        }

        public void Save(AppConfig config)
        {
            if (SavesSuspended)
                return; // a backup was just restored and BerthMenu is restarting
            try
            {
                BackupBeforeSave();
            }
            catch
            {
                // A failed backup never stops the save itself.
            }
            try
            {
                var json = JsonSerializer.Serialize(config, JsonOptions);
                var tmpPath = ConfigFilePath + ".tmp";
                File.WriteAllText(tmpPath, json);

                // Write-then-replace avoids leaving a half-written config.json if the
                // process is killed mid-save.
                File.Copy(tmpPath, ConfigFilePath, overwrite: true);
                File.Delete(tmpPath);
            }
            catch
            {
                // Best-effort: if we truly can't write config (e.g. locked-down profile),
                // the app should keep running with the in-memory settings for this session.
            }
        }

        // ---------------------------------------------------------------
        // Automatic backups — Settings → Startup → "Restore a backup…"
        // ---------------------------------------------------------------
        //
        // Before a change is saved, a copy of config.json as it was is kept in
        // %AppData%\BerthMenu\Backups — at most one a day, so a day's worth of
        // tweaking costs one copy, and the copy is how things were before that
        // day's first change. The newest BackupsToKeep are kept. A copy identical
        // to the newest one isn't kept twice. Restoring first backs up the current
        // settings too ("Before restoring"), so a restore can itself be undone.
        //
        // Backups hold the settings and pinned tiles (config.json). Icons and the
        // background picture live in their own folders: an icon that's gone by
        // then is simply extracted again.

        public string BackupFolder => Path.Combine(AppDataFolder, "Backups");
        private const int BackupsToKeep = 10;
        private const string BeforeRestoreNote = "before-restore";

        /// <summary>Set once a backup has been restored: from then on nothing
        /// (this copy shutting down, say) may write over the restored file.</summary>
        public bool SavesSuspended { get; private set; }

        /// <param name="Reason">Why it was taken, when it wasn't just the daily copy:
        /// "before restoring a backup", "before importing settings", "before
        /// resetting". Null for the daily ones.</param>
        public sealed record Backup(string FilePath, DateTime Taken, string? Reason, int Categories, int Tiles);

        private const string BeforeImportNote = "before-import";
        private const string BeforeResetNote = "before-reset";

        private static string? ReasonFor(string file)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.EndsWith(BeforeRestoreNote, StringComparison.OrdinalIgnoreCase)) return "before restoring a backup";
            if (name.EndsWith(BeforeImportNote, StringComparison.OrdinalIgnoreCase)) return "before importing settings";
            if (name.EndsWith(BeforeResetNote, StringComparison.OrdinalIgnoreCase)) return "before resetting";
            return null;
        }

        private void BackupBeforeSave()
        {
            if (!File.Exists(ConfigFilePath))
                return;
            var newest = ListBackupFiles().FirstOrDefault();
            if (newest != null && DateTime.Now - File.GetLastWriteTime(newest) < TimeSpan.FromHours(24))
                return;
            TakeBackup(note: null);
        }

        private void TakeBackup(string? note)
        {
            if (!File.Exists(ConfigFilePath))
                return;
            Directory.CreateDirectory(BackupFolder);

            var newest = ListBackupFiles().FirstOrDefault();
            if (note == null && newest != null && FilesEqual(newest, ConfigFilePath))
            {
                File.SetLastWriteTime(newest, DateTime.Now); // still current — counts as today's
                return;
            }

            string name = $"config-{DateTime.Now:yyyyMMdd-HHmmss}" + (note != null ? "-" + note : string.Empty) + ".json";
            string path = Path.Combine(BackupFolder, name);
            File.Copy(ConfigFilePath, path, overwrite: true);
            File.SetLastWriteTime(path, DateTime.Now);

            foreach (string old in ListBackupFiles().Skip(BackupsToKeep))
            {
                try { File.Delete(old); } catch { /* try again next time */ }
            }
        }

        /// <summary>Backup files, newest first.</summary>
        private string[] ListBackupFiles()
        {
            if (!Directory.Exists(BackupFolder))
                return Array.Empty<string>();
            return Directory.GetFiles(BackupFolder, "config-*.json")
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToArray();
        }

        private static bool FilesEqual(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            return fa.Length == fb.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }

        /// <summary>The backups, newest first, with how many categories and tiles
        /// each has (to help tell them apart). Unreadable files are left out.</summary>
        public System.Collections.Generic.List<Backup> ListBackups()
        {
            var result = new System.Collections.Generic.List<Backup>();
            foreach (string file in ListBackupFiles())
            {
                try
                {
                    var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(file), JsonOptions);
                    if (config == null)
                        continue;
                    int tiles = config.Categories.Sum(c => c.Icons.Sum(i => i.Children is { Count: > 0 } kids ? kids.Count : 1));
                    result.Add(new Backup(file, File.GetLastWriteTime(file),
                        ReasonFor(file),
                        config.Categories.Count, tiles));
                }
                catch
                {
                    // Damaged — not offered.
                }
            }
            return result;
        }

        /// <summary>Makes <paramref name="backup"/> the current config.json, after
        /// backing up the current one. BerthMenu must restart afterwards to load it
        /// (App.Restart); until then nothing else is saved. False if the backup
        /// can't be read or written.</summary>
        public bool RestoreBackup(Backup backup)
        {
            try
            {
                string json = File.ReadAllText(backup.FilePath);
                if (JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) == null)
                    return false;

                TakeBackup(BeforeRestoreNote);
                File.WriteAllText(ConfigFilePath + ".tmp", json);
                File.Copy(ConfigFilePath + ".tmp", ConfigFilePath, overwrite: true);
                File.Delete(ConfigFilePath + ".tmp");
                SavesSuspended = true;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------
        // Export / import (Settings → Startup) and Reset
        // ---------------------------------------------------------------
        //
        // An export is a zip file (".berthmenu") holding config.json plus the
        // pictures it uses — every tile icon in the icon cache (custom ones
        // included) and the background picture — so it can be brought back on
        // this PC or another one. Importing puts those pictures into this PC's
        // BerthMenu folders and points the settings at them.

        public const string ExportExtension = ".berthmenu";

        /// <summary>Writes the saved settings (config.json) and their pictures to
        /// <paramref name="zipPath"/>.</summary>
        public void ExportSettings(string zipPath)
        {
            if (!File.Exists(ConfigFilePath))
                throw new InvalidOperationException("there are no saved settings yet");

            string json = File.ReadAllText(ConfigFilePath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();

            string temp = zipPath + ".tmp";
            if (File.Exists(temp))
                File.Delete(temp);
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(ConfigFilePath, "config.json");

                var added = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var icon in AllIcons(config))
                {
                    string path = icon.CachedIconPath ?? string.Empty;
                    if (path.Length > 0 && File.Exists(path) && added.Add(Path.GetFileName(path)))
                        zip.CreateEntryFromFile(path, "icons/" + Path.GetFileName(path));
                }
                if (!string.IsNullOrEmpty(config.BackgroundImagePath) && File.Exists(config.BackgroundImagePath))
                    zip.CreateEntryFromFile(config.BackgroundImagePath, "background/" + Path.GetFileName(config.BackgroundImagePath));
            }
            File.Move(temp, zipPath, overwrite: true);
        }

        /// <summary>Replaces the current settings with an export (after backing the
        /// current ones up). BerthMenu must restart afterwards (App.Restart); until
        /// then nothing else is saved. Throws with a readable message if the file
        /// isn't a BerthMenu export.</summary>
        public void ImportSettings(string zipPath)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var configEntry = zip.GetEntry("config.json")
                ?? throw new InvalidOperationException("this isn't a BerthMenu settings file");

            AppConfig config;
            using (var reader = new StreamReader(configEntry.Open()))
                config = JsonSerializer.Deserialize<AppConfig>(reader.ReadToEnd(), JsonOptions)
                    ?? throw new InvalidOperationException("the settings in this file couldn't be read");

            TakeBackup(BeforeImportNote);

            // Pictures go into this PC's folders; the settings point at them there.
            Directory.CreateDirectory(IconCacheFolder);
            foreach (var entry in zip.Entries)
            {
                string name = Path.GetFileName(entry.FullName);
                if (name.Length == 0)
                    continue;
                if (entry.FullName.StartsWith("icons/", StringComparison.OrdinalIgnoreCase))
                    entry.ExtractToFile(Path.Combine(IconCacheFolder, name), overwrite: true);
            }
            foreach (var icon in AllIcons(config))
            {
                string old = icon.CachedIconPath ?? string.Empty;
                if (old.Length == 0)
                    continue;
                string local = Path.Combine(IconCacheFolder, Path.GetFileName(old));
                icon.CachedIconPath = File.Exists(local) ? local : string.Empty; // missing: extracted again
            }

            if (!string.IsNullOrEmpty(config.BackgroundImagePath))
            {
                var backgroundEntry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.StartsWith("background/", StringComparison.OrdinalIgnoreCase) && e.Name.Length > 0);
                if (backgroundEntry != null)
                {
                    ClearBackgroundImage();
                    string local = Path.Combine(BackgroundImageFolder, backgroundEntry.Name);
                    backgroundEntry.ExtractToFile(local, overwrite: true);
                    config.BackgroundImagePath = local;
                }
                else
                {
                    config.BackgroundImagePath = null;
                }
            }

            string tmp = ConfigFilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions));
            File.Copy(tmp, ConfigFilePath, overwrite: true);
            File.Delete(tmp);
            SavesSuspended = true;
        }

        /// <summary>Settings → Reset: backs up the current settings, then removes
        /// them, so BerthMenu starts like a new install (with the welcome window)
        /// after App.Restart. Pinned apps and categories go too.</summary>
        public bool ResetSettings()
        {
            try
            {
                TakeBackup(BeforeResetNote);
                if (File.Exists(ConfigFilePath))
                    File.Delete(ConfigFilePath);
                SavesSuspended = true;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static System.Collections.Generic.IEnumerable<DockIcon> AllIcons(AppConfig config)
        {
            foreach (var category in config.Categories)
            {
                foreach (var icon in category.Icons)
                {
                    yield return icon;
                    if (icon.Children != null)
                        foreach (var child in icon.Children)
                            yield return child;
                }
            }
        }

        private void TryBackupCorruptConfig(Exception ex)
        {
            try
            {
                var backupPath = ConfigFilePath + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
                if (File.Exists(ConfigFilePath))
                    File.Copy(ConfigFilePath, backupPath, overwrite: true);
                File.WriteAllText(Path.Combine(AppDataFolder, "last-config-error.log"), ex.ToString());
            }
            catch
            {
                // Ignore — this is already the fallback path.
            }
        }
    }
}
