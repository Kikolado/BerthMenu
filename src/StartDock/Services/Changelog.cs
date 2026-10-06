using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace StartDock.Services
{
    /// <summary>
    /// The release notes in CHANGELOG.md (at the top of the repository, built into
    /// StartDock as a resource — see the csproj). Shown in the "What's new" window,
    /// and used by ci/release.yml as each GitHub release's notes.
    ///
    /// Format: a "## 0.9.4" heading per version, newest first, followed by "- " lines.
    /// Anything else (the title, comments, blank lines) is ignored.
    /// </summary>
    public static class Changelog
    {
        /// <summary>One "## " section: a version (CHANGELOG.md) or a topic (Tips.md),
        /// and its "- " lines.</summary>
        public sealed record Entry(string Version, IReadOnlyList<string> Changes);

        private static IReadOnlyList<Entry>? _entries;
        private static IReadOnlyList<Entry>? _tips;

        public static IReadOnlyList<Entry> Entries => _entries ??= Load("StartDock.CHANGELOG.md");

        /// <summary>The tips in Resources/Tips.md (same format, a topic per "## "),
        /// shown by the Tips window (tray → Tips, Settings → Tips).</summary>
        public static IReadOnlyList<Entry> Tips => _tips ??= Load("StartDock.Tips.md");

        private static IReadOnlyList<Entry> Load(string resourceName)
        {
            var result = new List<Entry>();
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                if (stream == null)
                    return result;
                using var reader = new StreamReader(stream);

                string? version = null;
                var changes = new List<string>();
                bool inComment = false;
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    string t = line.Trim();
                    if (inComment)
                    {
                        if (t.Contains("-->")) inComment = false;
                        continue;
                    }
                    if (t.StartsWith("<!--"))
                    {
                        inComment = !t.Contains("-->");
                        continue;
                    }
                    if (t.StartsWith("## "))
                    {
                        if (version != null)
                            result.Add(new Entry(version, changes));
                        version = t.Substring(3).Trim();
                        changes = new List<string>();
                    }
                    else if (version != null && t.StartsWith("- "))
                    {
                        changes.Add(t.Substring(2).Trim());
                    }
                    else if (version != null && t.Length > 0 && changes.Count > 0)
                    {
                        // A wrapped line continues the previous change.
                        changes[^1] += " " + t;
                    }
                }
                if (version != null)
                    result.Add(new Entry(version, changes));
            }
            catch
            {
                // No notes is better than a crash.
            }
            return result;
        }

        /// <summary>Opens a new GitHub issue for StartDock with the StartDock and
        /// Windows versions already filled in. Needs a GitHub account to submit.</summary>
        public static void ReportProblem()
        {
            string version = Updater.Display(Updater.CurrentVersion);
            string body =
                "**What happened?**\n\n\n" +
                "**What did you expect to happen?**\n\n\n" +
                "**Steps to make it happen again**\n1. \n\n" +
                "---\n" +
                $"StartDock {version}\n" +
                $"Windows {Environment.OSVersion.Version}\n";
            string url = $"https://github.com/{Updater.Repository}/issues/new"
                + "?title=" + Uri.EscapeDataString("")
                + "&body=" + Uri.EscapeDataString(body);
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // No browser set up — nothing more we can do.
            }
        }
    }
}
