using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace StartDock.Services
{
    /// <summary>
    /// An app's recent files, for the "Recent" list on a tile's right-click menu —
    /// the same list the taskbar shows when you right-click the app there.
    ///
    /// Windows keeps that list per app, under the app's AppUserModelID: a Store
    /// app's id, an id the program sets for itself (Chrome, Office…), or — for most
    /// desktop programs — one Windows makes up from the program's path, with the
    /// start of the path written as a known-folder id
    /// ("{1AC14E77-…}\notepad.exe" for C:\Windows\System32\notepad.exe). The list is
    /// read with IApplicationDocumentLists, Windows' documented way to do this.
    ///
    /// Some apps (Office, VS Code and others) keep their own list instead, which
    /// Windows doesn't share. For those, the recent files Windows itself remembers
    /// (the Recent Items folder) are used when this app is what opens them.
    /// </summary>
    public static class JumpListService
    {
        /// <summary>One recent item. <see cref="OpenWithDefault"/>: open it the
        /// normal way (double-click) rather than handing it to the app.</summary>
        public sealed record Item(string Name, string Path, bool OpenWithDefault);

        private const int MaxItems = 10;
        private const string AppsFolderPrefix = "shell:AppsFolder\\";

        /// <summary>The recent items for a tile's target (an installed app, a program
        /// or a shortcut to one). Empty when Windows has none for it.</summary>
        public static Task<IReadOnlyList<Item>> GetRecentAsync(string target) =>
            StaWorker.Run(() => GetRecent(target)); // the shell's COM objects want STA

        /// <summary>Opens a recent item with the app it belongs to.</summary>
        public static void Open(string appTarget, Item item) =>
            _ = StaWorker.Run(() =>
            {
                try
                {
                    string? program = item.OpenWithDefault ? null : ProgramFor(appTarget);
                    // When this app is what opens the file anyway, open it normally,
                    // the way the taskbar does.
                    if (program != null && !SamePath(program, AssocExecutable(item.Path)))
                        Process.Start(new ProcessStartInfo(program, "\"" + item.Path + "\"") { UseShellExecute = true });
                    else
                        Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                }
                catch
                {
                    // The file's gone, or the app refused it — nothing to do.
                }
                return true;
            });

        private static IReadOnlyList<Item> GetRecent(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return Array.Empty<Item>();

            foreach (string appId in AppIdsFor(target))
            {
                var items = ReadList(appId);
                if (items.Count > 0)
                    return items;
            }
            return FromRecentItemsFolder(target);
        }

        // ---- Which AppUserModelID(s) a tile's target goes by

        private static IEnumerable<string> AppIdsFor(string target)
        {
            var ids = new List<string>();
            string? program = null;

            if (target.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string id = target.Substring(AppsFolderPrefix.Length);
                ids.Add(id);
                if (!id.Contains('!'))
                    program = ShortcutResolver.AppsFolderProgram(id);
            }
            else if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                if (ShortcutAppId(target) is { Length: > 0 } linkId)
                    ids.Add(linkId);
                program = ShortcutResolver.Shortcut(target)?.Path;
            }
            else if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                program = Environment.ExpandEnvironmentVariables(target);
            }

            if (program != null)
            {
                ids.Add(ImplicitAppId(program));
                ids.Add(program);
            }
            return ids.Where(i => i.Length > 0 && i.Length <= 128).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        // Known folders Windows writes as an id at the start of a program's implicit
        // AppUserModelID. The longest matching folder wins (System32 over Windows).
        private static readonly (string Guid, Func<string?> Folder)[] KnownFolders =
        {
            ("{6D809377-6AF0-444B-8957-A3773F02200E}", () => Environment.GetEnvironmentVariable("ProgramW6432") is { Length: > 0 } p ? p : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            ("{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}", () => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
            ("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}", () => Environment.GetFolderPath(Environment.SpecialFolder.System)),
            ("{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}", () => Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)),
            ("{F38BF404-1D43-42F2-9305-67DE0B28FC23}", () => Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
            ("{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}", () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            ("{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}", () => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            ("{62AB5D82-FDC1-4DC3-A9DD-070D1D495D97}", () => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            ("{5E6C858F-0E22-4760-9AFE-EA3317B67173}", () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        };

        /// <summary>The id Windows gives a program that doesn't choose its own:
        /// its path, starting with a known-folder id where one fits.</summary>
        private static string ImplicitAppId(string programPath)
        {
            string best = string.Empty, bestGuid = string.Empty;
            foreach (var (guid, folder) in KnownFolders)
            {
                string? path = folder();
                if (string.IsNullOrEmpty(path))
                    continue;
                path = path.TrimEnd('\\');
                if (path.Length > best.Length && programPath.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    best = path;
                    bestGuid = guid;
                }
            }
            return best.Length > 0 ? bestGuid + programPath.Substring(best.Length) : programPath;
        }

        /// <summary>The AppUserModelID a shortcut sets for the program it starts, if any.</summary>
        private static string? ShortcutAppId(string lnkPath)
        {
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                dynamic? folder = shell.NameSpace(Path.GetDirectoryName(lnkPath));
                dynamic? item = folder?.ParseName(Path.GetFileName(lnkPath));
                return item?.ExtendedProperty("System.AppUserModel.ID") as string;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The program behind a tile's target, or null (a Store app).</summary>
        private static string? ProgramFor(string target)
        {
            if (target.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
                return ShortcutResolver.AppsFolderProgram(target.Substring(AppsFolderPrefix.Length));
            if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                return ShortcutResolver.Shortcut(target)?.Path;
            if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return Environment.ExpandEnvironmentVariables(target);
            return null;
        }

        // ---- Windows' own list for an app id

        private static IReadOnlyList<Item> ReadList(string appId)
        {
            var result = new List<Item>();
            object? listsObject = null;
            object? arrayObject = null;
            try
            {
                var type = Type.GetTypeFromCLSID(new Guid("86BEC222-30F2-47E0-9F25-60D11CD75C28")); // CLSID_ApplicationDocumentLists
                if (type == null)
                    return result;
                listsObject = Activator.CreateInstance(type);
                if (listsObject is not IApplicationDocumentLists lists)
                    return result;
                if (lists.SetAppID(appId) != 0)
                    return result;

                Guid iidArray = typeof(IObjectArray).GUID;
                if (lists.GetList(ADLT_RECENT, MaxItems, ref iidArray, out arrayObject) != 0 || arrayObject is not IObjectArray array)
                    return result;
                if (array.GetCount(out uint count) != 0)
                    return result;

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (uint i = 0; i < count && result.Count < MaxItems; i++)
                {
                    if (ReadEntry(array, i) is { } entry && seen.Add(entry.Path))
                        result.Add(entry);
                }
            }
            catch
            {
                // No list for this id (or the shell refused) — the caller tries the next.
            }
            finally
            {
                if (arrayObject != null && Marshal.IsComObject(arrayObject)) Marshal.ReleaseComObject(arrayObject);
                if (listsObject != null && Marshal.IsComObject(listsObject)) Marshal.ReleaseComObject(listsObject);
            }
            return result;
        }

        private static Item? ReadEntry(IObjectArray array, uint index)
        {
            Guid iidItem = typeof(IShellItem).GUID;
            if (array.GetAt(index, ref iidItem, out object itemObject) == 0 && itemObject is IShellItem shellItem)
            {
                try
                {
                    string? path = DisplayName(shellItem, SIGDN_FILESYSPATH);
                    if (string.IsNullOrEmpty(path))
                        return null; // not a file or folder (a web page, a virtual item)
                    string name = DisplayName(shellItem, SIGDN_NORMALDISPLAY) ?? Path.GetFileName(path);
                    return new Item(name, path, OpenWithDefault: false);
                }
                finally
                {
                    Marshal.ReleaseComObject(itemObject);
                }
            }

            Guid iidLink = typeof(IShellLinkW).GUID;
            if (array.GetAt(index, ref iidLink, out object linkObject) == 0 && linkObject is IShellLinkW link)
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    if (link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0) != 0 || sb.Length == 0)
                        return null;
                    string path = sb.ToString();
                    return new Item(Path.GetFileName(path), path, OpenWithDefault: false);
                }
                finally
                {
                    Marshal.ReleaseComObject(linkObject);
                }
            }
            return null;
        }

        private static string? DisplayName(IShellItem item, uint sigdn)
        {
            if (item.GetDisplayName(sigdn, out IntPtr ptr) != 0 || ptr == IntPtr.Zero)
                return null;
            try { return Marshal.PtrToStringUni(ptr); }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }

        // ---- Apps that keep their own list: Windows' Recent Items, for file types this app opens

        private static IReadOnlyList<Item> FromRecentItemsFolder(string target)
        {
            var result = new List<Item>();
            string? program = ProgramFor(target);
            if (program == null)
                return result;

            try
            {
                var handlerByExtension = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var recent in RecentFilesService.GetRecentFiles(150))
                {
                    string ext = Path.GetExtension(recent.Name);
                    if (ext.Length == 0)
                        continue;
                    if (!handlerByExtension.TryGetValue(ext, out bool ours))
                    {
                        ours = SamePath(program, AssocExecutable("x" + ext));
                        handlerByExtension[ext] = ours;
                    }
                    if (!ours)
                        continue;

                    // Skip files that have since been deleted or moved.
                    string? file = ShortcutTargetPath(recent.ShortcutPath);
                    if (file == null || !File.Exists(file))
                        continue;
                    result.Add(new Item(recent.Name, file, OpenWithDefault: true));
                    if (result.Count >= MaxItems)
                        break;
                }
            }
            catch
            {
                // Recent Items unreadable — no list.
            }
            return result;
        }

        private static string? ShortcutTargetPath(string lnkPath)
        {
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                dynamic? folder = shell.NameSpace(Path.GetDirectoryName(lnkPath));
                dynamic? item = folder?.ParseName(Path.GetFileName(lnkPath));
                return item?.GetLink?.Path as string;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The program Windows opens a file (or file type) with, or null.</summary>
        private static string? AssocExecutable(string fileName)
        {
            string ext = Path.GetExtension(fileName);
            if (ext.Length == 0)
                return null;
            try
            {
                uint size = 1024;
                var sb = new StringBuilder((int)size);
                return AssocQueryString(ASSOCF_NOTRUNCATE, ASSOCSTR_EXECUTABLE, ext, null, sb, ref size) == 0 ? sb.ToString() : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool SamePath(string? a, string? b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
            && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        // ---- Windows functions

        private const int ADLT_RECENT = 0;
        private const uint SIGDN_NORMALDISPLAY = 0;
        private const uint SIGDN_FILESYSPATH = 0x80058000;
        private const uint ASSOCF_NOTRUNCATE = 0x20;
        private const int ASSOCSTR_EXECUTABLE = 2;

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int AssocQueryString(uint flags, int str, string pszAssoc, string? pszExtra, StringBuilder? pszOut, ref uint pcchOut);

        [ComImport, Guid("3C594F9F-9F30-47A1-979A-C9E83D3D0A06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationDocumentLists
        {
            [PreserveSig] int SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
            [PreserveSig] int GetList(int listType, uint cItemsDesired, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        }

        [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IObjectArray
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IntPtr ppsi);
            [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
            [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);
        }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        }
    }
}
