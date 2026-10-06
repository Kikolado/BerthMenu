using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace StartDock.Services
{
    /// <summary>
    /// The Windows Run box (Win+R) inside search (AppConfig.SearchRun): what's typed
    /// can be run as a command or opened as a path, as one extra search result.
    ///
    ///  - Commands: a program found on the PATH or in Windows' "App Paths" list —
    ///    cmd, notepad, regedit, control, msconfig, services.msc, winver… — with any
    ///    arguments after it ("ping 8.8.8.8", "cmd /k ipconfig").
    ///  - Paths: C:\Users, C:, %AppData%, ~\Downloads, \\server\share. Local paths are
    ///    only offered once they exist (so a half-typed one doesn't show); network
    ///    paths are offered straight away, since checking them can take seconds.
    ///  - Addresses: anything with a registered protocol (ms-settings:display,
    ///    https://…, mailto:…), www.… sites, and shell: folders (shell:startup).
    ///
    /// Nothing runs until the result is clicked or Enter is pressed. Ctrl+Shift+Enter
    /// runs it as administrator, like the Run box.
    /// </summary>
    public static class RunCommand
    {
        public enum Kind { Command, Folder, File, Address }

        /// <summary>Something search can run. <see cref="FileName"/> is what's started
        /// (the full program path for a command), <see cref="Arguments"/> what follows it.</summary>
        public sealed record Target(Kind Kind, string Text, string FileName, string Arguments);

        // Program name (with and without its extension) → full path. Built in the
        // background on first use (see WarmUp); until then only paths and addresses work.
        private static volatile Dictionary<string, string>? _commands;
        private static bool _building;
        private static DateTime _builtAt;

        /// <summary>Reads the PATH folders and App Paths in the background, at most
        /// every 10 minutes (programs installed since then are picked up).</summary>
        public static void WarmUp()
        {
            if (_building || (_commands != null && DateTime.UtcNow - _builtAt < TimeSpan.FromMinutes(10)))
                return;
            _building = true;
            Task.Run(() =>
            {
                try { _commands = BuildCommandList(); _builtAt = DateTime.UtcNow; }
                catch { /* keep the old list */ }
                finally { _building = false; }
            });
        }

        private static readonly Regex DrivePath = new(@"^[a-zA-Z]:([\\/].*)?$", RegexOptions.Compiled);
        private static readonly Regex Scheme = new(@"^([a-zA-Z][a-zA-Z0-9+.\-]+):", RegexOptions.Compiled);

        /// <summary>What <paramref name="text"/> would run, or null if it isn't a
        /// command, path or address. Cheap enough to call on every keystroke.</summary>
        public static Target? Parse(string text)
        {
            text = text.Trim();
            if (text.Length == 0)
                return null;

            // Paths
            string expanded = Environment.ExpandEnvironmentVariables(text);
            if (expanded == "~" || expanded.StartsWith("~\\") || expanded.StartsWith("~/"))
                expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + expanded.Substring(1);
            if (expanded.StartsWith(@"\\"))
            {
                return expanded.Length > 2 && expanded[2] != '\\'
                    ? new Target(Kind.Folder, text, expanded, string.Empty)
                    : null;
            }
            if (DrivePath.IsMatch(expanded) || (text != expanded && Path.IsPathRooted(expanded)))
            {
                string path = expanded.Length == 2 ? expanded + "\\" : expanded;
                try
                {
                    if (Directory.Exists(path))
                        return new Target(Kind.Folder, text, path, string.Empty);
                    if (File.Exists(path))
                        return new Target(Kind.File, text, path, string.Empty);
                }
                catch { /* invalid characters */ }
                return null;
            }

            // Addresses
            if (!text.Contains(' '))
            {
                if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && text.Length > 6 && text.IndexOf('.', 4) > 4)
                    return new Target(Kind.Address, text, "https://" + text, string.Empty);
                var m = Scheme.Match(text);
                if (m.Success && (m.Groups[1].Value.Equals("shell", StringComparison.OrdinalIgnoreCase) || IsRegisteredProtocol(m.Groups[1].Value)))
                    return text.Length > m.Length ? new Target(Kind.Address, text, text, string.Empty) : null;
            }

            // Commands
            var commands = _commands;
            if (commands == null)
                return null;
            SplitCommand(text, out string name, out string args);
            if (name.Length == 0 || name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
                return null;
            if (commands.TryGetValue(name, out string? full))
                return new Target(Kind.Command, text, full, args);
            return null;
        }

        /// <summary>Starts it. Returns false if Windows couldn't (or the admin prompt was declined).</summary>
        public static bool Run(Target target, bool asAdministrator)
        {
            try
            {
                var psi = new ProcessStartInfo(target.FileName)
                {
                    UseShellExecute = true,
                    Arguments = target.Arguments,
                };
                if (target.Kind == Kind.Command)
                    psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (asAdministrator && (target.Kind == Kind.Command || target.Kind == Kind.File))
                    psi.Verb = "runas";
                if (target.Kind == Kind.Address && target.FileName.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                {
                    psi.FileName = "explorer.exe";
                    psi.Arguments = target.FileName;
                }
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void SplitCommand(string text, out string name, out string args)
        {
            if (text.StartsWith("\""))
            {
                int end = text.IndexOf('"', 1);
                if (end < 0) { name = text.Trim('"'); args = string.Empty; return; }
                name = text.Substring(1, end - 1);
                args = text.Substring(end + 1).Trim();
                return;
            }
            int space = text.IndexOf(' ');
            name = space < 0 ? text : text.Substring(0, space);
            args = space < 0 ? string.Empty : text.Substring(space + 1).Trim();
        }

        private static bool IsRegisteredProtocol(string scheme)
        {
            if (scheme.Length < 2)
                return false; // a drive letter
            try
            {
                using var key = Registry.ClassesRoot.OpenSubKey(scheme);
                return key?.GetValue("URL Protocol") != null;
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<string, string> BuildCommandList()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var extensions = new List<string>();
            foreach (string ext in (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries))
                extensions.Add(ext.ToLowerInvariant());
            extensions.Add(".msc");
            extensions.Add(".cpl");

            // App Paths first: the programs Windows' own Run box knows by name
            // (chrome, excel, wordpad…) even though their folders aren't on the PATH.
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using var appPaths = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");
                    if (appPaths == null)
                        continue;
                    foreach (string exeName in appPaths.GetSubKeyNames())
                    {
                        using var sub = appPaths.OpenSubKey(exeName);
                        if (sub?.GetValue(null) is not string path)
                            continue;
                        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                        if (path.Length == 0)
                            continue;
                        result.TryAdd(exeName, path);
                        result.TryAdd(Path.GetFileNameWithoutExtension(exeName), path);
                    }
                }
                catch { /* skip this hive */ }
            }

            // Then every program in a PATH folder. The first folder wins, as in cmd.
            var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string rawFolder in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string folder = Environment.ExpandEnvironmentVariables(rawFolder.Trim().Trim('"'));
                if (folder.Length == 0 || folder.StartsWith(@"\\") || !seenFolders.Add(folder))
                    continue; // network folders could stall the whole list
                try
                {
                    if (!Directory.Exists(folder))
                        continue;
                    foreach (string file in Directory.EnumerateFiles(folder))
                    {
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        if (!extensions.Contains(ext))
                            continue;
                        string fileName = Path.GetFileName(file);
                        result.TryAdd(fileName, file);
                        result.TryAdd(Path.GetFileNameWithoutExtension(file), file);
                    }
                }
                catch { /* unreadable folder */ }
            }

            return result;
        }
    }
}
