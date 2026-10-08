using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BerthMenu.Services
{
    /// <summary>
    /// Shared per-monitor-DPI-aware work-area lookup for any Window in the app.
    ///
    /// Originally written inside MainWindow (see its own PositionDock/DockPosition
    /// code for the full story) to fix "Middle center" landing off-center: on a
    /// Per-Monitor-V2-aware process like this one (see app.manifest),
    /// SystemParameters.WorkArea only ever reports the *primary* monitor and
    /// converts it to DIPs using a DPI figure that doesn't reliably match the DPI
    /// the window is actually running at, so anything measured against it can come
    /// out shifted on a scaled or multi-monitor setup. Pulled out into its own
    /// class once SettingsWindow needed the exact same correctness for a different
    /// reason (capping its own height to the real screen it's on, so Save/Cancel
    /// can never end up off-screen on a short display) — a second, slightly
    /// different copy of this logic would have been one more place for the two to
    /// quietly drift apart.
    /// </summary>
    public static class MonitorHelper
    {
        /// <summary>The given window's target monitor's work area (screen minus
        /// taskbar), in DIPs relative to that monitor's own top-left corner.
        /// Prefers whichever monitor already holds the window (so a call made
        /// after the window has been shown/moved stays correct); before that
        /// hwnd exists — most usefully, right at construction time, before the
        /// first Show/ShowDialog — falls back to the monitor under the mouse
        /// cursor, since that's normally a good proxy for "which screen the user
        /// is paying attention to right now" (they just clicked whatever opened
        /// this window).</summary>
        public static Rect GetWorkArea(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            IntPtr hMonitor = hwnd != IntPtr.Zero
                ? NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST)
                : IntPtr.Zero;

            if (hMonitor == IntPtr.Zero && NativeMethods.GetCursorPos(out var cursor))
                hMonitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);

            return GetWorkAreaForMonitor(hMonitor);
        }

        /// <summary>The monitor under the mouse cursor's work area, in the same DIPs-
        /// relative-to-that-monitor's-own-top-left shape <see cref="GetWorkArea"/>
        /// returns. Unlike GetWorkArea, this deliberately never prefers "whichever
        /// monitor the window is already on" — see MainWindow.xaml.cs's own
        /// GetTargetWorkArea for why that distinction actually matters for the dock
        /// specifically: MainWindow is constructed once and then just hidden/shown
        /// (Visibility toggles, never re-Show'n from scratch), so its hwnd keeps
        /// existing — and keeps reporting whatever monitor it was LAST positioned
        /// on — the entire time it sits hidden. GetWorkArea's own "prefer the
        /// window's monitor" branch was therefore silently reading stale, possibly
        /// long-out-of-date geometry: open the dock on monitor A, move to monitor B
        /// (or turn A off entirely), press the hotkey again, and the dock would
        /// still reposition itself against monitor A's work area — including, per a
        /// real report, a monitor the user had since powered off outright — because
        /// MonitorFromWindow(hwnd) doesn't care whether the window is currently
        /// visible or where the user actually is right now, only where it happened
        /// to sit last. Always resolving fresh from the live cursor position (the
        /// same "good proxy for which screen the user is paying attention to right
        /// now" reasoning GetWorkArea's own fallback already uses, just made the
        /// primary and only source here rather than a last resort) is what actually
        /// answers "which monitor should this open on", every single time the dock
        /// is about to be shown.</summary>
        public static Rect GetWorkAreaAtCursor()
        {
            IntPtr hMonitor = NativeMethods.GetCursorPos(out var cursor)
                ? NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST)
                : IntPtr.Zero;

            return GetWorkAreaForMonitor(hMonitor);
        }

        private static Rect GetWorkAreaForMonitor(IntPtr hMonitor)
        {
            if (hMonitor == IntPtr.Zero)
                return SystemParameters.WorkArea; // should be unreachable — last-resort fallback only

            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfo(hMonitor, ref info))
                return SystemParameters.WorkArea;

            double scaleX = 1.0, scaleY = 1.0;
            if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY) == 0)
            {
                scaleX = dpiX / 96.0;
                scaleY = dpiY / 96.0;
            }

            var work = info.rcWork;
            return new Rect(
                work.Left / scaleX,
                work.Top / scaleY,
                work.Width / scaleX,
                work.Height / scaleY);
        }
    }
}
