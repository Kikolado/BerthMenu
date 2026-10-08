using System;
using System.IO;
using BerthMenu.Models;

namespace BerthMenu.Services
{
    /// <summary>
    /// Tells real apps apart from the other things installers drop into the Start
    /// menu, for the "Recently added" and "Recently used" sections.
    ///
    /// Get-StartApps lists every Start menu entry, not just programs. An installer like
    /// Inno Setup adds shortcuts to its help file (.chm), a web page (.htm), a website
    /// link (.url) and an examples folder next to the program itself. Each of those
    /// counts as a new "app" to Get-StartApps, which is how "Recently added" filled up
    /// with documentation. Search still lists them (so does the Windows Start menu);
    /// only the Recent sections leave them out.
    ///
    /// How to tell: an entry's AppId is either an app id (a Store app such as
    /// "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", or a desktop app's registered
    /// id such as "Chrome"), which never contains a backslash, or the path the
    /// shortcut points to, such as "{6D809377-…}\Inno Setup 6\Compil32.exe". A path
    /// only counts as an app if it ends in a program extension.
    /// </summary>
    public static class AppKind
    {
        private static readonly string[] ProgramExtensions = { ".exe", ".msc", ".cpl", ".bat", ".cmd" };

        public static bool IsRealApp(InstalledAppInfo app)
        {
            string id = app.AppId ?? string.Empty;
            if (id.Length == 0)
                return false;

            // Website shortcuts: the AppId is the address itself.
            if (id.Contains("://", StringComparison.Ordinal))
                return false;

            // Uninstallers are programs, but nobody means them by "new app".
            if (app.Name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)
                || app.Name.Contains(" Uninstall", StringComparison.OrdinalIgnoreCase))
                return false;

            // BerthMenu itself.
            if (IsBerthMenuItself(app))
                return false;

            bool isPath = id.Contains('\\') || id.Contains('/');
            if (!isPath)
                return true; // a Store or registered app id

            string ext = Path.GetExtension(id);
            foreach (string programExt in ProgramExtensions)
            {
                if (string.Equals(ext, programExt, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false; // a document, help file, folder, …
        }

        private static bool IsBerthMenuItself(InstalledAppInfo app)
        {
            if (string.Equals(app.Name, "BerthMenu", StringComparison.OrdinalIgnoreCase))
                return true;
            string? self = Environment.ProcessPath;
            return !string.IsNullOrEmpty(self)
                && string.Equals(Path.GetFileName(app.AppId), Path.GetFileName(self), StringComparison.OrdinalIgnoreCase);
        }
    }
}
