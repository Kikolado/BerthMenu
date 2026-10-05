using System;

namespace StartDock.Services
{
    /// <summary>
    /// Brings the taskbar forward while the dock is open over a fullscreen app or
    /// game, matching what the real Start Menu does (AppConfig.ShowTaskbarOverFullscreen).
    ///
    /// How Windows hides the taskbar in the first place: while a fullscreen window
    /// is in front on a monitor, Explorer drops that monitor's taskbar out of the
    /// "topmost" layer, so the app covers it. So the signal used here is simply
    /// whether the taskbar has lost its topmost flag — no guessing about what counts
    /// as fullscreen. If it's already topmost (the normal, visible case) nothing is
    /// touched at all.
    ///
    /// Restore puts things back the way Explorer had them: out of the topmost layer
    /// again, and slotted in just behind whatever app was in front when the dock
    /// opened, so the taskbar doesn't linger over the game after the dock closes.
    /// Focus is deliberately not touched — if the dock closed because an app was
    /// launched from it, that app should keep it.
    ///
    /// Limits: an auto-hidden taskbar is off-screen rather than just behind the app,
    /// so this can't reveal it; and exclusive-fullscreen games (as opposed to
    /// borderless/windowed-fullscreen) don't let any other window — the dock
    /// included — draw over them.
    /// </summary>
    internal sealed class FullscreenTaskbarService
    {
        private IntPtr _raisedTaskbar;
        private IntPtr _appInFront;

        /// <summary>Raises the taskbar on the monitor under the cursor if (and only
        /// if) a fullscreen app currently has it pushed behind.</summary>
        public void RaiseOnMonitorAtCursor()
        {
            try
            {
                Restore(); // never stack two raises

                IntPtr foreground = NativeMethods.GetForegroundWindow();
                if (!NativeMethods.GetCursorPos(out var cursor))
                    return;
                IntPtr monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);

                foreach (IntPtr taskbar in StartButtonOverlayService.FindAllTaskbarWindows())
                {
                    if (NativeMethods.MonitorFromWindow(taskbar, NativeMethods.MONITOR_DEFAULTTONEAREST) != monitor)
                        continue;

                    bool isTopmost = (NativeMethods.GetWindowLong(taskbar, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOPMOST) != 0;
                    if (isTopmost)
                        return; // taskbar already showing normally — nothing to do

                    NativeMethods.SetWindowPos(taskbar, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

                    _raisedTaskbar = taskbar;
                    _appInFront = foreground;
                    return;
                }
            }
            catch
            {
                // Cosmetic only — never let this get in the way of the dock opening.
            }
        }

        /// <summary>Undoes RaiseOnMonitorAtCursor, if it raised anything.</summary>
        public void Restore()
        {
            IntPtr taskbar = _raisedTaskbar;
            IntPtr app = _appInFront;
            _raisedTaskbar = IntPtr.Zero;
            _appInFront = IntPtr.Zero;

            if (taskbar == IntPtr.Zero)
                return;

            try
            {
                if (!NativeMethods.IsWindow(taskbar))
                    return; // Explorer restarted meanwhile — the new taskbar is its own business

                const uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;
                NativeMethods.SetWindowPos(taskbar, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, flags);

                // Leaving the topmost layer puts the taskbar at the top of the normal
                // layer — still above a normal (non-topmost) fullscreen app. Slot it
                // in right behind that app. Skipped when the app is itself topmost:
                // the taskbar is already beneath it, and inserting behind a topmost
                // window would pull the taskbar back into the topmost layer.
                if (app != IntPtr.Zero && NativeMethods.IsWindow(app))
                {
                    bool appIsTopmost = (NativeMethods.GetWindowLong(app, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOPMOST) != 0;
                    if (!appIsTopmost)
                        NativeMethods.SetWindowPos(taskbar, app, 0, 0, 0, 0, flags);
                }
            }
            catch
            {
                // Best effort — Explorer re-evaluates the taskbar itself on the next
                // foreground change anyway.
            }
        }
    }
}
