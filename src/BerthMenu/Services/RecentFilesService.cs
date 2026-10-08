using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BerthMenu.Services
{
    /// <summary>
    /// Recently opened files, for the optional "Recent files" section
    /// (AppConfig.ShowRecentFiles) — read from Windows' own Recent Items folder
    /// (%AppData%\Microsoft\Windows\Recent), the same list File Explorer's "Recent"
    /// and Quick Access draw on. Each entry there is a .lnk shortcut that Windows
    /// keeps up to date as files are opened; launching the shortcut opens the file.
    /// </summary>
    public static class RecentFilesService
    {
        public readonly record struct RecentFile(string Name, string ShortcutPath);

        /// <summary>The most recently opened files, newest first. Cheap — a single
        /// directory listing — but still meant to be called off the UI thread.</summary>
        public static List<RecentFile> GetRecentFiles(int max)
        {
            try
            {
                string folder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                    return new List<RecentFile>();

                return new DirectoryInfo(folder)
                    .EnumerateFiles("*.lnk")
                    .Select(f => (File: f, Name: Path.GetFileNameWithoutExtension(f.Name)))
                    // Windows names a file's shortcut "report.docx.lnk" and a folder's
                    // "Downloads.lnk" — keeping only names that still have an extension
                    // once ".lnk" is stripped leaves files and skips folders, without
                    // having to open every shortcut to see what it points at.
                    .Where(x => Path.HasExtension(x.Name))
                    .OrderByDescending(x => x.File.LastWriteTimeUtc)
                    .Take(max)
                    .Select(x => new RecentFile(x.Name, x.File.FullName))
                    .ToList();
            }
            catch
            {
                return new List<RecentFile>();
            }
        }
    }
}
