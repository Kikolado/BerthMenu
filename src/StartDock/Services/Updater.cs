using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace StartDock.Services
{
    /// <summary>
    /// Automatic updates from the GitHub releases at
    /// https://github.com/Kikolado/StartDock/releases — see ci/release.yml for how
    /// those are built and published.
    ///
    /// How an update happens:
    ///  1. Check: ask GitHub for the latest release and compare its version (the tag,
    ///     e.g. v0.9.0) with this copy's own. Drafts and pre-releases are skipped.
    ///  2. Download: the release's StartDock-Setup-*.exe into %TEMP%\StartDock-Update,
    ///     plus its .sha256 fingerprint file. The download is only kept if its SHA-256
    ///     matches — a damaged or tampered file is deleted, never run. A release
    ///     without a fingerprint is refused.
    ///  3. Install: run that installer silently (App.InstallUpdate), and exit. It
    ///     replaces the installed copy in place, keeping the install folder and
    ///     settings, then starts StartDock again (the /RELAUNCH=1 switch — see
    ///     installer\StartDock.iss).
    ///
    /// App checks a minute after starting and every six hours, and downloads a new
    /// version when there is one — Settings then shows "Update x.y available" next to
    /// the version number. With AppConfig.AutoUpdate on it's also installed as soon as
    /// the dock isn't open; otherwise it waits for that label or "Update now".
    /// Builds run from Visual Studio (Debug) never install on their own.
    ///
    /// Everything here runs on the UI thread (the network work is awaited, not
    /// blocking), so no locking is needed.
    /// </summary>
    public static class Updater
    {
        public const string Repository = "Kikolado/StartDock";

        private static readonly HttpClient Http = CreateClient();

        /// <summary>This copy's version, e.g. 0.8.0.0 (from the csproj's &lt;Version&gt;).</summary>
        public static Version CurrentVersion { get; } =
            Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0));

        /// <summary>"0.8" style, for showing to people.</summary>
        public static string Display(Version v) =>
            v.Build > 0 || v.Revision > 0 ? v.ToString(v.Revision > 0 ? 4 : 3) : v.ToString(2);

        /// <summary>A short line for Settings: up to date, downloading, ready, failed…</summary>
        public static string Status { get; private set; } = string.Empty;
        public static event Action? StatusChanged;

        /// <summary>Set once an update has been downloaded and checked: the installer to run.</summary>
        public static string? ReadyInstallerPath { get; private set; }
        public static Version? ReadyVersion { get; private set; }

        public static bool IsBusy { get; private set; }

        /// <summary>The newer version GitHub has, once a check has found one (it may
        /// still be downloading). Null when up to date or not checked yet.</summary>
        public static Version? AvailableVersion { get; private set; }

        /// <summary>The longer version of Status, for its tooltip (an error's
        /// details, say). Same as Status when there's nothing more to say.</summary>
        public static string StatusDetail { get; private set; } = string.Empty;

        // Status is kept short so it fits beside "Check for updates" in Settings
        // (the version number is already in the Settings footer).
        private static void SetStatus(string text, string? detail = null)
        {
            Status = text;
            StatusDetail = detail ?? text;
            StatusChanged?.Invoke();
        }

        /// <summary>Checks GitHub and, if there's a newer version, downloads it.
        /// Returns true when an update is downloaded and ready to install.
        /// <paramref name="quiet"/> keeps "can't reach GitHub" from showing as an
        /// error for a background check.</summary>
        public static async Task<bool> CheckAndDownloadAsync(bool quiet)
        {
            if (ReadyInstallerPath != null && File.Exists(ReadyInstallerPath))
                return true;
            if (IsBusy)
                return false;

            IsBusy = true;
            try
            {
                SetStatus("Checking…", "Checking for updates…");
                var release = await GetLatestReleaseAsync();
                if (release == null || release.Version <= CurrentVersion)
                {
                    AvailableVersion = null;
                    SetStatus("Up to date", $"StartDock {Display(CurrentVersion)} is the newest version.");
                    return false;
                }

                AvailableVersion = release.Version;

                SetStatus($"Downloading {Display(release.Version)}…", $"Downloading StartDock {Display(release.Version)}…");
                string path = await DownloadAndVerifyAsync(release);

                ReadyInstallerPath = path;
                ReadyVersion = release.Version;
                SetStatus($"{Display(release.Version)} is ready", $"StartDock {Display(release.Version)} is downloaded and ready to install.");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Update check failed: {ex.Message}");
                if (quiet)
                    SetStatus(string.Empty);
                else
                    SetStatus("Couldn't check", $"Couldn't check for updates: {ex.Message}");
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>The arguments App passes when running a downloaded installer:
        /// no wizard or message boxes, close a running StartDock, no reboot, and
        /// start StartDock again afterwards.</summary>
        public const string SilentInstallArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1";

        /// <summary>Whether installing will show Windows' admin prompt: StartDock was
        /// installed for all users (Program Files) and isn't running as admin. Then
        /// an update isn't installed unattended — it waits for "Update now".</summary>
        public static bool InstallNeedsAdmin(bool isElevated)
        {
            if (isElevated)
                return false;
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return false;
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string root = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(root) && exe.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static void MarkInstalling() =>
            SetStatus($"Installing {Display(ReadyVersion ?? CurrentVersion)}…", $"Installing StartDock {Display(ReadyVersion ?? CurrentVersion)}…");

        public static void MarkInstallFailed(string reason) =>
            SetStatus("Update didn't start", $"Couldn't start the update: {reason}");

        // ---- After an update: "StartDock was updated to 0.9" from the tray.

        private static string PendingFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StartDock", "update-pending.txt");

        /// <summary>Remembers which version is being installed, so the next start can
        /// say it was updated.</summary>
        public static void RememberPendingUpdate()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PendingFile)!);
                File.WriteAllText(PendingFile, (ReadyVersion ?? CurrentVersion).ToString());
            }
            catch { /* only used for the "updated" message */ }
        }

        /// <summary>On startup: the version an update just installed, if this start
        /// is the first one after it (and clears the note). Null otherwise.</summary>
        public static Version? TakeJustUpdatedVersion()
        {
            try
            {
                if (!File.Exists(PendingFile))
                    return null;
                string text = File.ReadAllText(PendingFile).Trim();
                File.Delete(PendingFile);
                return Version.TryParse(text, out var v) && Normalize(v) <= CurrentVersion ? CurrentVersion : null;
            }
            catch
            {
                return null;
            }
        }

        // ---- GitHub

        private sealed class Release
        {
            public required Version Version { get; init; }
            public required string InstallerName { get; init; }
            public required string InstallerUrl { get; init; }
            public string? Sha256Url { get; init; }
        }

        private static async Task<Release?> GetLatestReleaseAsync()
        {
            // "latest" never returns drafts or pre-releases.
            using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null; // no releases yet
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
                return null;

            string? installerName = null, installerUrl = null, shaUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    string url = asset.GetProperty("browser_download_url").GetString() ?? "";
                    if (name.StartsWith("StartDock-Setup-", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        installerName = name;
                        installerUrl = url;
                    }
                }
                if (installerName != null)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        if (string.Equals(asset.GetProperty("name").GetString(), installerName + ".sha256", StringComparison.OrdinalIgnoreCase))
                            shaUrl = asset.GetProperty("browser_download_url").GetString();
                    }
                }
            }

            if (installerName == null || installerUrl == null)
                return null; // a release without an installer (yet)

            return new Release
            {
                Version = Normalize(version),
                InstallerName = installerName,
                InstallerUrl = installerUrl,
                Sha256Url = shaUrl,
            };
        }

        private static async Task<string> DownloadAndVerifyAsync(Release release)
        {
            if (release.Sha256Url == null)
                throw new InvalidOperationException("the release has no fingerprint (.sha256) file");

            string expected = (await Http.GetStringAsync(release.Sha256Url)).Trim().Split(' ', '\t')[0].ToLowerInvariant();
            if (expected.Length != 64)
                throw new InvalidOperationException("the release's fingerprint file isn't valid");

            string folder = Path.Combine(Path.GetTempPath(), "StartDock-Update");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, Path.GetFileName(release.InstallerName));
            string partial = path + ".part";

            using (var response = await Http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var target = File.Create(partial);
                await source.CopyToAsync(target);
            }

            string actual;
            await using (var stream = File.OpenRead(partial))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();

            if (actual != expected)
            {
                try { File.Delete(partial); } catch { /* best effort */ }
                throw new InvalidOperationException("the download didn't match its fingerprint, so it was discarded");
            }

            File.Move(partial, path, overwrite: true);
            return path;
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            // GitHub's API requires a User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"StartDock/{CurrentVersionString()}");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        private static string CurrentVersionString() =>
            (Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0)).ToString();

        /// <summary>Fills in missing parts as 0, so 0.9.0 and 0.9.0.0 compare as equal.</summary>
        private static Version Normalize(Version v) =>
            new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

        private static void Log(string message)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StartDock");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "update.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch { /* best effort */ }
        }
    }
}
