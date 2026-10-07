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
        /// Windows versions filled in, plus the last few errors StartDock logged in
        /// the past two weeks (your Windows user name is taken out of them), so the
        /// report says what went wrong. Needs a GitHub account to submit, and
        /// nothing is sent until you do.</summary>
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

            string errors = RecentErrors();
            if (errors.Length > 0)
                body += "\n**Recent errors** (from StartDock's log. Remove anything you'd rather not share.)\n```\n" + errors + "\n```\n";

            OpenUrl($"https://github.com/{Updater.Repository}/issues/new?body=" + Uri.EscapeDataString(body));
        }

        /// <summary>Opens StartDock's page on GitHub.</summary>
        public static void OpenGitHub() => OpenUrl($"https://github.com/{Updater.Repository}");

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // No browser set up — nothing more we can do.
            }
        }

        /// <summary>The end of crash.log and update.log (if written in the last two
        /// weeks), at most about 1,500 characters, with the user name replaced.</summary>
        private static string RecentErrors()
        {
            const int maxChars = 1500;
            var parts = new List<string>();
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StartDock");
            foreach (string name in new[] { "crash.log", "update.log" })
            {
                try
                {
                    string path = Path.Combine(folder, name);
                    if (!File.Exists(path) || DateTime.Now - File.GetLastWriteTime(path) > TimeSpan.FromDays(14))
                        continue;
                    string text = File.ReadAllText(path).Trim();
                    if (text.Length == 0)
                        continue;
                    if (text.Length > maxChars / 2)
                        text = "…" + text.Substring(text.Length - maxChars / 2);
                    parts.Add($"[{name}]\n{text}");
                }
                catch
                {
                    // Unreadable — skip it.
                }
            }

            string all = string.Join("\n\n", parts);
            string user = Environment.UserName;
            if (user.Length > 0)
                all = all.Replace(user, "<user>", StringComparison.OrdinalIgnoreCase);
            return all.Replace("```", "'''");
        }
    }
}
