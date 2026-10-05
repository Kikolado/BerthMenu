using System;
using System.Collections.Generic;
using System.Linq;

namespace StartDock.Services
{
    /// <summary>
    /// A small, hand-curated list of common Windows Settings pages, matched against
    /// the dock's search box the same way installed apps are — so searching
    /// "bluetooth" (say) surfaces the Bluetooth settings page itself, not just any
    /// app that happens to have that word in its name, mirroring what the native
    /// Start Menu's own search does.
    ///
    /// Each entry launches via its "ms-settings:" URI (the same documented,
    /// Microsoft-published scheme the Settings app's own internal navigation uses —
    /// AppLauncher.Launch already runs every DockIcon.TargetPath through
    /// ShellExecute, which resolves this scheme natively, no special-casing needed
    /// anywhere else). A page id that's wrong or has stopped being recognized on some
    /// future Windows build fails softly — Windows just opens the plain Settings home
    /// page instead of a dead end — so an entry going stale here is a minor
    /// inconvenience, never a crash or a broken tile.
    ///
    /// Deliberately a short, high-value list rather than an attempt at completeness:
    /// every "ms-settings:" page id Microsoft has ever shipped would be a much larger
    /// (and, for the more obscure ones, less confidently correct without a Windows
    /// machine on hand to verify against) list to maintain. Add more entries here as
    /// they come up in practice.
    /// </summary>
    public static class WindowsSettingsCatalog
    {
        public sealed record Entry(string Name, string Uri, string[] Keywords);

        /// <summary>The real Settings app executable — used purely as an icon donor
        /// (see MainWindow.EnsureWindowsSettingsIconLoaded) so every settings search
        /// result tile shows the same recognizable gear icon the real Settings app
        /// uses, rather than sitting blank. %WINDIR% rather than a hardcoded "C:\"
        /// since Windows isn't always installed on the C: drive.</summary>
        public const string SettingsAppExePath = @"%WINDIR%\ImmersiveControlPanel\SystemSettings.exe";

        /// <summary>Fixed, arbitrary GUID identifying the cached icon file for
        /// SettingsAppExePath above (see IconExtractor.ExtractAndCache) — needs to be
        /// stable across runs so the same cached PNG is reused every time rather than
        /// re-extracted, but otherwise carries no meaning of its own.</summary>
        public static readonly Guid SettingsIconId = new("A5B6F0B2-6C0B-4B9F-9B1E-2C1E9B7C4D3A");

        private static readonly Entry[] Entries =
        {
            new("Bluetooth settings", "ms-settings:bluetooth", new[] { "bluetooth" }),
            new("Wi-Fi settings", "ms-settings:network-wifi", new[] { "wifi", "wi-fi", "wireless" }),
            new("Network settings", "ms-settings:network-status", new[] { "network", "ethernet", "internet" }),
            new("Display settings", "ms-settings:display", new[] { "display", "screen", "resolution", "monitor" }),
            new("Sound settings", "ms-settings:sound", new[] { "sound", "audio", "volume", "speaker", "microphone" }),
            new("Notifications settings", "ms-settings:notifications", new[] { "notifications", "notification" }),
            new("Power & battery settings", "ms-settings:powersleep", new[] { "power", "sleep", "battery" }),
            new("Storage settings", "ms-settings:storagesense", new[] { "storage", "disk space" }),
            new("Windows Update", "ms-settings:windowsupdate", new[] { "update", "windows update" }),
            new("Background settings", "ms-settings:personalization-background", new[] { "background", "wallpaper" }),
            new("Colors settings", "ms-settings:personalization-colors", new[] { "colors", "accent color", "dark mode", "light mode", "theme" }),
            new("Taskbar settings", "ms-settings:taskbar", new[] { "taskbar" }),
            new("Privacy settings", "ms-settings:privacy", new[] { "privacy" }),
            new("Apps & features", "ms-settings:appsfeatures", new[] { "apps", "uninstall", "programs" }),
            new("Default apps settings", "ms-settings:defaultapps", new[] { "default apps", "default programs" }),
            new("Date & time settings", "ms-settings:dateandtime", new[] { "date", "time", "clock", "timezone" }),
            new("Printers & scanners", "ms-settings:printers", new[] { "printer", "scanner", "printing" }),
            new("Mouse settings", "ms-settings:mousetouchpad", new[] { "mouse", "touchpad", "cursor" }),
            new("Accounts settings", "ms-settings:yourinfo", new[] { "account", "sign-in", "login" }),
            new("Windows Security", "windowsdefender:", new[] { "antivirus", "defender", "windows security", "virus" }),
        };

        /// <summary>Every entry whose name or one of its keywords contains
        /// <paramref name="filter"/> — same case-insensitive "contains" match
        /// MainWindow.RenderSearchResults already uses for pinned tiles and installed
        /// apps, so a settings result feels like just another kind of search hit
        /// rather than a special case with different matching rules.</summary>
        public static IEnumerable<Entry> Search(string filter) =>
            Entries.Where(e =>
                e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.Keywords.Any(k => k.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    }
}
