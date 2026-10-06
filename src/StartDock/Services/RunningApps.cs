using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StartDock.Services
{
    /// <summary>
    /// Which pinned apps are open right now, for the small bar under their tiles
    /// (AppConfig.ShowRunningIndicator). Checked each time the dock opens.
    ///
    /// "Open" means the app has a window the taskbar would show: visible, not owned
    /// by another window, not a tool window, not hidden ("cloaked") by Windows. An
    /// app only running in the background or in the tray doesn't count.
    ///
    /// Each window is matched to tiles three ways:
    ///  - the program file behind it (C:\…\chrome.exe) — for pinned programs and
    ///    shortcuts, and installed apps whose Start menu entry points to a program;
    ///  - its app id (AppUserModelID), which Store apps and many others have — for
    ///    installed apps pinned from the app list ("shell:AppsFolder\…");
    ///  - the program's name for app ids that are just a name ("Chrome" →
    ///    chrome.exe, "MSEdge" → msedge.exe).
    /// </summary>
    public static class RunningApps
    {
        /// <summary>Which of <paramref name="targets"/> (tile TargetPaths) are open.
        /// Runs on its own background thread (the shell lookups need an STA thread).</summary>
        public static Task<HashSet<string>> FindRunningAsync(IReadOnlyCollection<string> targets)
        {
            var tcs = new TaskCompletionSource<HashSet<string>>();
            var thread = new Thread(() =>
            {
                try { tcs.SetResult(FindRunning(targets)); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return tcs.Task;
        }

        private sealed class Snapshot
        {
            public readonly HashSet<string> Paths = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> ExeNames = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> AppIds = new(StringComparer.OrdinalIgnoreCase);
        }

        private static HashSet<string> FindRunning(IReadOnlyCollection<string> targets)
        {
            var snap = TakeSnapshot();
            var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string target in targets)
            {
                if (IsRunning(target, snap))
                    running.Add(target);
            }
            return running;
        }

        private static bool IsRunning(string target, Snapshot snap)
        {
            if (string.IsNullOrWhiteSpace(target))
                return false;

            const string appsFolder = "shell:AppsFolder\\";
            if (target.StartsWith(appsFolder, StringComparison.OrdinalIgnoreCase))
            {
                string id = target.Substring(appsFolder.Length);
                if (snap.AppIds.Contains(id))
                    return true;
                if (id.Contains('!'))
                    return false; // a Store app: only its app id identifies it

                string normalized = AppUsageService.NormalizeId(id);
                if (Path.IsPathRooted(normalized))
                    return snap.Paths.Contains(normalized);

                if (snap.ExeNames.Contains(id + ".exe"))
                    return true;
                return ResolveAppsFolderProgram(id) is { } program && snap.Paths.Contains(program);
            }

            if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                return ResolveShortcut(target) is { } program && snap.Paths.Contains(program);

            if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return snap.Paths.Contains(target);

            return false; // folders, files, websites, scripts
        }

        // ---- What a tile points to — see ShortcutResolver.

        private static string? ResolveAppsFolderProgram(string appId) => ShortcutResolver.AppsFolderProgram(appId);

        private static string? ResolveShortcut(string lnkPath) => ShortcutResolver.Shortcut(lnkPath)?.Path;

        // ---- The open windows

        private static Snapshot TakeSnapshot()
        {
            var snap = new Snapshot();
            var seenPids = new HashSet<uint>();
            uint self = (uint)Environment.ProcessId;

            EnumWindows((hwnd, _) =>
            {
                if (!IsTaskbarWindow(hwnd))
                    return true;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0 || pid == self)
                    return true;

                if (WindowAppId(hwnd) is { } windowAppId)
                    snap.AppIds.Add(windowAppId);

                AddProcess(pid, snap, seenPids, out string? exe);

                // Store apps are drawn inside an ApplicationFrameHost window; the
                // app's own process owns a child window inside it.
                if (exe != null && Path.GetFileName(exe).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
                {
                    EnumChildWindows(hwnd, (child, lParam) =>
                    {
                        GetWindowThreadProcessId(child, out uint childPid);
                        if (childPid != 0 && childPid != pid)
                            AddProcess(childPid, snap, seenPids, out string? _);
                        return true;
                    }, IntPtr.Zero);
                }
                return true;
            }, IntPtr.Zero);

            return snap;
        }

        private static void AddProcess(uint pid, Snapshot snap, HashSet<uint> seen, out string? exe)
        {
            exe = null;
            IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (h == IntPtr.Zero)
                return;
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref size))
                    exe = sb.ToString();

                if (!seen.Add(pid))
                    return;
                if (exe != null)
                {
                    snap.Paths.Add(exe);
                    snap.ExeNames.Add(Path.GetFileName(exe));
                }

                uint len = 256;
                var id = new StringBuilder((int)len);
                if (GetApplicationUserModelId(h, ref len, id) == 0)
                    snap.AppIds.Add(id.ToString());
            }
            catch
            {
                // Skip this one.
            }
            finally
            {
                CloseHandle(h);
            }
        }

        private static bool IsTaskbarWindow(IntPtr hwnd)
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
                return false;
            long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0)
                return false;
            if (GetWindowTextLength(hwnd) == 0)
                return false;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return false;
            return true;
        }

        /// <summary>The app id a window was given (Firefox, Chrome and others set one
        /// per window), or null.</summary>
        private static string? WindowAppId(IntPtr hwnd)
        {
            IPropertyStore? store = null;
            try
            {
                Guid iid = typeof(IPropertyStore).GUID;
                if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store == null)
                    return null;
                var key = new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
                var value = new PropVariant();
                if (store.GetValue(ref key, ref value) != 0)
                    return null;
                try
                {
                    return value.vt == VT_LPWSTR && value.p != IntPtr.Zero ? Marshal.PtrToStringUni(value.p) : null;
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (store != null)
                    Marshal.ReleaseComObject(store);
            }
        }

        // ---- Windows functions

        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint GW_OWNER = 4;
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x80;
        private const long WS_EX_APPWINDOW = 0x40000;
        private const int DWMWA_CLOAKED = 14;
        private const ushort VT_LPWSTR = 31;

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
        private static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder id);

        [DllImport("shell32.dll")]
        private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pv);

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr p;
            [FieldOffset(16)] public IntPtr p2;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }
    }
}
