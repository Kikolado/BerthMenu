using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BerthMenu.Services
{
    /// <summary>
    /// A small, hand-curated list of common Windows Settings pages (and, below,
    /// classic Windows tools — see ToolDefinitions), matched against
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
        /// <param name="IconSource">The file whose icon the tile shows (a Windows tool
        /// below). Null for Settings pages, which all show the Settings gear.</param>
        public sealed record Entry(string Name, string Uri, string[] Keywords, string? IconSource = null);

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
            new("Optional features", "ms-settings:optionalfeatures", new[] { "optional features", "add a feature" }),
            new("Startup apps", "ms-settings:startupapps", new[] { "startup", "start up", "launch at login" }),
            new("About this PC", "ms-settings:about", new[] { "about", "pc name", "rename this pc", "computer name", "specs", "system info" }),
            new("Remote Desktop settings", "ms-settings:remotedesktop", new[] { "remote desktop", "rdp" }),
            new("VPN settings", "ms-settings:network-vpn", new[] { "vpn" }),
            new("Recovery settings", "ms-settings:recovery", new[] { "recovery", "reset this pc", "reset pc" }),
            new("Activation settings", "ms-settings:activation", new[] { "activation", "activate windows", "product key" }),
            new("Multitasking settings", "ms-settings:multitasking", new[] { "multitasking", "snap", "alt+tab" }),
            new("Night light settings", "ms-settings:nightlight", new[] { "night light", "blue light" }),
            new("Lock screen settings", "ms-settings:personalization-lockscreen", new[] { "lock screen", "lockscreen" }),
            new("Sign-in options", "ms-settings:signinoptions", new[] { "sign-in options", "pin code", "password", "windows hello", "fingerprint" }),
        };

        /// <summary>
        /// Classic Windows tools that Windows' own Start search finds but that aren't
        /// in the Start menu's app list, so they never came up in BerthMenu's search
        /// ("Turn Windows features on or off" being the one that was missed). Each
        /// shows its own icon. A tool that isn't on this PC (Group Policy Editor on
        /// Windows Home, say) is left out. Tools that the app list does include
        /// (Services, Event Viewer…) appear once — see MainWindow.RenderSearchResults.
        /// </summary>
        private static readonly (string Name, string Target, string[] Keywords)[] ToolDefinitions =
        {
            ("Turn Windows features on or off", @"%WINDIR%\System32\OptionalFeatures.exe", new[] { "windows features", "optional features", "hyper-v", "wsl", "sandbox", ".net framework", "iis" }),
            ("Control Panel", @"%WINDIR%\System32\control.exe", new[] { "control panel" }),
            ("Programs and Features", @"%WINDIR%\System32\appwiz.cpl", new[] { "uninstall a program", "programs and features", "add or remove programs" }),
            ("Device Manager", @"%WINDIR%\System32\devmgmt.msc", new[] { "device manager", "drivers", "hardware" }),
            ("Disk Management", @"%WINDIR%\System32\diskmgmt.msc", new[] { "disk management", "partition", "format drive", "create and format hard disk partitions" }),
            ("Computer Management", @"%WINDIR%\System32\compmgmt.msc", new[] { "computer management", "local users and groups" }),
            ("Services", @"%WINDIR%\System32\services.msc", new[] { "services" }),
            ("Event Viewer", @"%WINDIR%\System32\eventvwr.msc", new[] { "event viewer", "event log", "logs" }),
            ("Task Scheduler", @"%WINDIR%\System32\taskschd.msc", new[] { "task scheduler", "scheduled tasks" }),
            ("Performance Monitor", @"%WINDIR%\System32\perfmon.msc", new[] { "performance monitor", "perfmon" }),
            ("Resource Monitor", @"%WINDIR%\System32\resmon.exe", new[] { "resource monitor", "resmon" }),
            ("Local Group Policy Editor", @"%WINDIR%\System32\gpedit.msc", new[] { "group policy", "gpedit", "edit group policy" }),
            ("Registry Editor", @"%WINDIR%\regedit.exe", new[] { "registry", "regedit" }),
            ("System Configuration", @"%WINDIR%\System32\msconfig.exe", new[] { "system configuration", "msconfig", "boot options" }),
            ("System Information", @"%WINDIR%\System32\msinfo32.exe", new[] { "system information", "msinfo", "specs" }),
            ("System Properties", @"%WINDIR%\System32\SystemPropertiesAdvanced.exe", new[] { "system properties", "environment variables", "edit the system environment variables", "virtual memory", "page file", "performance options" }),
            ("Create a restore point", @"%WINDIR%\System32\SystemPropertiesProtection.exe", new[] { "restore point", "system protection" }),
            ("System Restore", @"%WINDIR%\System32\rstrui.exe", new[] { "system restore", "restore point" }),
            ("Disk Cleanup", @"%WINDIR%\System32\cleanmgr.exe", new[] { "disk cleanup", "free up space", "cleanmgr" }),
            ("Defragment and Optimize Drives", @"%WINDIR%\System32\dfrgui.exe", new[] { "defragment", "defrag", "optimize drives", "trim" }),
            ("Network Connections", @"%WINDIR%\System32\ncpa.cpl", new[] { "network connections", "network adapters", "adapter settings", "ncpa" }),
            ("Windows Defender Firewall", @"%WINDIR%\System32\firewall.cpl", new[] { "firewall" }),
            ("Power Options", @"%WINDIR%\System32\powercfg.cpl", new[] { "power options", "power plan", "lid" }),
            ("Sound control panel", @"%WINDIR%\System32\mmsys.cpl", new[] { "playback devices", "recording devices", "sound control panel" }),
            ("Mouse properties", @"%WINDIR%\System32\main.cpl", new[] { "mouse properties", "pointer speed", "double-click speed" }),
            ("Internet Options", @"%WINDIR%\System32\inetcpl.cpl", new[] { "internet options", "proxy" }),
            ("User Accounts", @"%WINDIR%\System32\Netplwiz.exe", new[] { "user accounts", "netplwiz", "automatic sign-in", "auto login" }),
            ("Credential Manager", "shell:::{1206F5F1-0569-412C-8FEC-3204630DFB70}", new[] { "credential manager", "saved passwords", "windows credentials" }),
            ("Windows Tools", "shell:::{D20EA4E1-3957-11d2-A40B-0C5020524153}", new[] { "windows tools", "administrative tools" }),
            ("Fonts", "shell:Fonts", new[] { "fonts" }),
            ("Character Map", @"%WINDIR%\System32\charmap.exe", new[] { "character map", "charmap", "special characters", "symbols" }),
            ("On-Screen Keyboard", @"%WINDIR%\System32\osk.exe", new[] { "on-screen keyboard", "osk" }),
        };

        private static Entry[]? _tools;

        /// <summary>The tools above that exist on this PC, with their paths filled in.
        /// Worked out once, the first time search needs them.</summary>
        private static Entry[] Tools => _tools ??= ToolDefinitions
            .Select(t => (t.Name, Target: Environment.ExpandEnvironmentVariables(t.Target), t.Keywords))
            .Where(t => t.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || File.Exists(t.Target))
            .Select(t => new Entry(t.Name, t.Target, t.Keywords, IconSource: t.Target))
            .ToArray();

        /// <summary>Every entry whose name or one of its keywords contains
        /// <paramref name="filter"/> — same case-insensitive "contains" match
        /// MainWindow.RenderSearchResults already uses for pinned tiles and installed
        /// apps, so a settings result feels like just another kind of search hit
        /// rather than a special case with different matching rules.</summary>
        public static IEnumerable<Entry> Search(string filter) =>
            Entries.Concat(Tools).Where(e =>
                e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.Keywords.Any(k => k.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    }
}
