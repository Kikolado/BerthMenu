using System;
using System.Collections.Concurrent;
using System.IO;

namespace StartDock.Services
{
    /// <summary>
    /// What a shortcut or an installed app's Start menu entry actually starts —
    /// for the running-app indicator (RunningApps) and for launching a tile with its
    /// own arguments (AppLauncher, set in the tile's Properties).
    ///
    /// Uses Windows' Shell.Application object, which needs an STA thread (the UI
    /// thread, or RunningApps' own). Results are cached: they don't change while
    /// StartDock runs.
    /// </summary>
    public static class ShortcutResolver
    {
        public readonly record struct ShortcutInfo(string Path, string Arguments, string WorkingDirectory);

        private static readonly ConcurrentDictionary<string, string?> Programs = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, ShortcutInfo?> Shortcuts = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The program behind an installed app's Start menu entry, e.g.
        /// "Chrome" → C:\Program Files\Google\Chrome\Application\chrome.exe. Null for
        /// Store apps and entries that don't point to a program.</summary>
        public static string? AppsFolderProgram(string appId)
        {
            if (appId.Contains('!'))
                return null; // a Store app

            string normalized = AppUsageService.NormalizeId(appId);
            if (Path.IsPathRooted(normalized))
                return IsProgram(normalized) ? normalized : null;

            return Programs.GetOrAdd(appId, _ =>
            {
                try
                {
                    dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                    dynamic? folder = shell.NameSpace("shell:AppsFolder");
                    dynamic? item = folder?.ParseName(appId);
                    string? path = item?.ExtendedProperty("System.Link.TargetParsingPath") as string;
                    return IsProgram(path) ? path : null;
                }
                catch
                {
                    return null;
                }
            });
        }

        /// <summary>A .lnk shortcut's target program, its own arguments and its
        /// "Start in" folder. Null if it doesn't point to a program.</summary>
        public static ShortcutInfo? Shortcut(string lnkPath) =>
            Shortcuts.GetOrAdd(lnkPath, _ =>
            {
                try
                {
                    dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                    dynamic? folder = shell.NameSpace(Path.GetDirectoryName(lnkPath));
                    dynamic? item = folder?.ParseName(Path.GetFileName(lnkPath));
                    dynamic? link = item?.GetLink;
                    if (link == null)
                        return null;
                    string? path = link.Path as string;
                    if (!IsProgram(path))
                        return null;
                    return new ShortcutInfo(path!, (link.Arguments as string) ?? string.Empty, (link.WorkingDirectory as string) ?? string.Empty);
                }
                catch
                {
                    return null;
                }
            });

        private static bool IsProgram(string? path) =>
            !string.IsNullOrEmpty(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }
}
