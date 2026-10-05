using System;
using System.Runtime.InteropServices;

namespace StartDock.Services
{
    /// <summary>
    /// Centralized Win32 P/Invoke declarations. Keeping every signature in one file
    /// makes the "why does this need native code" surface easy to audit.
    /// </summary>
    internal static class NativeMethods
    {
        // ---- Low-level keyboard hook (intercepting the Windows key) ----

        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;

        public const int VK_LWIN = 0x5B;
        public const int VK_RWIN = 0x5C;
        public const int VK_SHIFT = 0x10;
        public const int VK_LSHIFT = 0xA0;
        public const int VK_RSHIFT = 0xA1;
        public const int VK_ESCAPE = 0x1B;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12; // Alt — GetAsyncKeyState(VK_MENU) reports either Alt key, same as VK_CONTROL/VK_SHIFT do for Ctrl/Shift
        public const int VK_TAB = 0x09;

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        /// <summary>Set on data.flags when the event was synthesized (by us or anyone
        /// else) via SendInput/keybd_event rather than coming from real hardware.</summary>
        public const int LLKHF_INJECTED = 0x00000010;

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        // ---- Low-level mouse hook (StartButtonOverlayService swallowing clicks on
        //      a taskbar's Start button, the mouse counterpart of the keyboard hook
        //      right above) ----

        public const int WH_MOUSE_LL = 14;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_RBUTTONUP = 0x0205;

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt; // physical screen pixels — see POINT below
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        // Separate overload from the keyboard hook's SetWindowsHookEx above —
        // P/Invoke needs the callback parameter's exact delegate type to match at
        // the call site, and LowLevelKeyboardProc/LowLevelMouseProc aren't
        // interchangeable even though their signatures happen to look identical.
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        // ---- Synthesizing a Windows-key-down event (HotkeyService reconstructing a
        //      Win+X combo it had to tentatively withhold — see that class for why) ----

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        public const uint KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        // ---- Windows 11 system backdrop (Mica/Acrylic) + native corner rounding ----
        //
        // DWMWA_WINDOW_CORNER_PREFERENCE and DWMWA_SYSTEMBACKDROP_TYPE are the
        // modern, *documented* Windows 11 DWM attributes for exactly this: a
        // translucent material behind a window, shaped to the window's own rounded
        // corners, with none of SetWindowCompositionAttribute's undocumented-API
        // uncertainty. DWMSBT_TRANSIENTWINDOW specifically is the "Acrylic"
        // material Windows' own chromeless flyouts use — the Start Menu, taskbar
        // right-click menus, the notification center — so it's a much closer match
        // for MainWindow's own chromeless, AllowsTransparency shape than the older
        // API ever was. (An earlier comment here claimed the system-backdrop route
        // "assumes a normal window chrome" and ruled it out on that basis before it
        // was ever actually tried — Acrylic specifically is the one Windows uses
        // for chromeless popups just like this one, so that assumption doesn't
        // hold up; ApplyFrostedBackground now tries this route first.)
        //
        // Being DWM-native (the OS itself composites the material against the
        // window's real, known shape) rather than a raw window-rect blur, this has
        // a real chance of actually following BackgroundBorder's rounded corners
        // without SetWindowRgn needing to fight it into shape at all — which is
        // exactly the thing SetWindowCompositionAttribute's blur turned out not to
        // do reliably.
        //
        // Windows 10 doesn't recognize either attribute at all — DwmSetWindowAttribute
        // just returns a failure HRESULT there rather than throwing or crashing, so
        // ApplyFrostedBackground checks that return value and falls back to the
        // older SetWindowCompositionAttribute path below whenever this one doesn't
        // take, rather than leaving Windows 10 with no blur at all.
        public enum DwmWindowAttribute
        {
            DWMWA_WINDOW_CORNER_PREFERENCE = 33,
            DWMWA_SYSTEMBACKDROP_TYPE = 38,

            // Picks the light or dark variant of the system backdrop material
            // (and of any DWM-drawn window chrome). Unset means light — which is
            // why the acrylic used to come out gray even over a black desktop.
            DWMWA_USE_IMMERSIVE_DARK_MODE = 20,

            // Color of the thin border Windows 11 draws around the window's edge
            // (COLORREF; 0xFFFFFFFF = system default). See MainWindow.SetSystemBorderColor.
            DWMWA_BORDER_COLOR = 34,
        }

        public enum DwmWindowCornerPreference
        {
            DWMWCP_DEFAULT = 0,
            DWMWCP_DONOTROUND = 1,
            DWMWCP_ROUND = 2,
            DWMWCP_ROUNDSMALL = 3,
        }

        public enum DwmSystemBackdropType
        {
            DWMSBT_AUTO = 0,
            DWMSBT_NONE = 1,
            DWMSBT_MAINWINDOW = 2,      // "Mica"
            DWMSBT_TRANSIENTWINDOW = 3, // "Acrylic" — the flyout/context-menu material
            DWMSBT_TABBEDWINDOW = 4,    // "Mica Alt"
        }

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, DwmWindowAttribute dwAttribute, ref int pvAttribute, int cbAttribute);

        // ---- Frosted/blurred background — legacy fallback (MainWindow's "Frost
        //      the background" setting, when the Windows 11 route above isn't
        //      available) ----
        //
        // SetWindowCompositionAttribute is undocumented (no public MSDN page), but
        // it's the long-standing, widely-used way apps get DWM to blur whatever's
        // behind a window — it's what the pre-Windows 11 era's "Acrylic"/"Fluent"
        // WPF app samples all use, and it works cleanly with a chromeless,
        // AllowsTransparency-layered window like MainWindow. Being undocumented,
        // it's wrapped defensively wherever it's actually called (see
        // MainWindow.xaml.cs's ApplyFrostedBackground) — a failure here should
        // just mean no blur, never a crash.

        public enum AccentState
        {
            ACCENT_DISABLED = 0,
            ACCENT_ENABLE_GRADIENT = 1,
            ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
            ACCENT_ENABLE_BLURBEHIND = 3,
            ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
            ACCENT_ENABLE_HOSTBACKDROP = 5,
            ACCENT_INVALID_STATE = 6,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct AccentPolicy
        {
            public AccentState AccentState;
            public int AccentFlags;
            public int GradientColor; // 0xAABBGGRR — unused for plain ACCENT_ENABLE_BLURBEHIND (no tint of its own; MainWindow's own BackgroundBorder supplies the color/image on top)
            public int AnimationId;
        }

        public enum WindowCompositionAttribute
        {
            WCA_ACCENT_POLICY = 19,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WindowCompositionAttributeData
        {
            public WindowCompositionAttribute Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("user32.dll")]
        public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        // ---- Window region (clipping MainWindow's own hwnd to a rounded rect — see
        //      MainWindow.xaml.cs's ApplyWindowRegion) ----
        //
        // SetWindowCompositionAttribute's blur-behind above is applied to a window's
        // whole rectangular bounds — it has no concept of "only blur this rounded
        // sub-area" on its own, so without this it blurs square corners straight
        // through the transparent margin MainWindow leaves around BackgroundBorder
        // for its drop shadow, reading as a squared-off blur sitting behind the
        // dock's own rounded card rather than filling it. SetWindowRgn constrains
        // the *whole hwnd* (rendering, DWM compositing, and hit-testing alike) to an
        // arbitrary GDI region — giving the window itself a rounded silhouette fixes
        // the blur's shape as a side effect, since DWM only ever composites within a
        // window's own region. This is a long-standing, widely-used technique for
        // exactly this combination (a layered/AllowsTransparency WPF window with
        // SetWindowCompositionAttribute blur-behind, wanting rounded corners) even
        // though SetWindowRgn itself predates layered windows entirely.
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int cornerWidth, int cornerHeight);

        [DllImport("user32.dll")]
        public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

        // ---- Window/monitor geometry (positioning the overlay + dock) ----

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        // Used (with hwndChildAfter passed the previously-found handle, in a loop) to
        // enumerate every top-level window of a given class — e.g. all of a multi-monitor
        // setup's "Shell_SecondaryTrayWnd" taskbars, of which there can be more than one.
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        // The window's true physical client size, straight from the OS — used by
        // MainWindow.ApplyWindowRegion instead of re-deriving it from WPF's
        // ActualWidth/ActualHeight times the DPI scale, which can land a device
        // pixel short of the real window on the right/bottom (see that method).
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        // ---- Window z-order (FullscreenTaskbarService: raising the taskbar over
        //      a fullscreen app while the dock is open) ----

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        // ---- Taking keyboard focus when the dock opens (MainWindow.ForceForeground) ----

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOPMOST = 0x00000008;
        public static readonly IntPtr HWND_TOPMOST = new(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new(-2);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;

        public const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        // MDT_EFFECTIVE_DPI: the DPI actually applied to content on that monitor
        // right now (accounts for the user's per-monitor scaling setting) — the
        // other two GetDpiForMonitor modes (angular/raw) are for edge cases this
        // app doesn't have.
        public const int MDT_EFFECTIVE_DPI = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor; // the monitor's full bounds
            public RECT rcWork;    // the monitor's bounds minus its taskbar (what "work area" means)
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        // Note: this used to also be where StartButtonOverlayService's window-styling
        // P/Invokes lived (GetWindowLong/SetWindowLong/SetWindowPos and their
        // WS_EX_*/SWP_*/HWND_TOPMOST constants) — that class no longer creates any
        // window at all (see its own doc comment for why: a global mouse hook
        // replaced the invisible click-catching window it used to position here),
        // so those declarations were removed along with their only caller rather
        // than left behind unused.

        // ---- Watching for taskbar/DPI/display changes ----

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        public const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

        // ---- Power actions ----

        [DllImport("powrprof.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

        public const uint EWX_LOGOFF = 0x00000000;
        public const uint EWX_SHUTDOWN = 0x00000001;
        public const uint EWX_REBOOT = 0x00000002;
        public const uint EWX_FORCE = 0x00000004;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool LockWorkStation();

        // ---- Current user's "friendly" display name ----

        public enum EXTENDED_NAME_FORMAT
        {
            NameUnknown = 0,
            NameFullyQualifiedDN = 1,
            NameSamCompatible = 2,
            NameDisplay = 3,
            NameUniqueId = 6,
            NameCanonical = 7,
            NameUserPrincipal = 8,
        }

        [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetUserNameEx(EXTENDED_NAME_FORMAT nameFormat, System.Text.StringBuilder lpNameBuffer, ref uint lpnSize);

        // ---- Shell icon extraction ----

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        public const uint SHGFI_ICON = 0x000000100;
        public const uint SHGFI_LARGEICON = 0x000000000;
        public const uint SHGFI_SMALLICON = 0x000000001;
        public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        // Overload for virtual shell paths (e.g. "shell:AppsFolder\<AppID>", used for
        // Store/UWP and other Start-menu-registered apps): plain SHGetFileInfo(string,...)
        // only resolves real filesystem paths, so these go through a PIDL instead —
        // parse the display name to a PIDL, then request its icon with SHGFI_PIDL.
        [DllImport("shell32.dll", CharSet = CharSet.Auto, EntryPoint = "SHGetFileInfo")]
        public static extern IntPtr SHGetFileInfoByPidl(IntPtr pidl, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("ole32.dll")]
        public static extern void CoTaskMemFree(IntPtr pv);

        public const uint SHGFI_PIDL = 0x000000008;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }
}
