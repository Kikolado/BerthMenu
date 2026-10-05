using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using StartDock.Models;

namespace StartDock.Services
{
    /// <summary>
    /// Loads and saves <see cref="AppConfig"/> as JSON under
    /// %AppData%\StartDock\config.json, plus exposes the app's data folders.
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
                "StartDock");
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

        public AppConfig Load()
        {
            try
            {
                if (!File.Exists(ConfigFilePath))
                {
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
