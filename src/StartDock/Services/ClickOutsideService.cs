using System;
using System.IO;
using System.Runtime.InteropServices;

namespace StartDock.Services
{
    /// <summary>
    /// Watches every left/right mouse-button-down system-wide via a global low-level
    /// mouse hook (WH_MOUSE_LL — the same technique StartButtonOverlayService already
    /// uses to catch clicks on the taskbar's Start button) and raises
    /// <see cref="OutsideClickDetected"/> whenever one lands somewhere that isn't
    /// really "on the dock", while the dock is showing.
    ///
    /// Why this exists instead of just relying on MainWindow's own Window_Deactivated
    /// handler (still there, and still worth keeping as a backstop — see MainWindow.
    /// xaml's Deactivated wiring): Deactivated only fires once Windows decides some
    /// *other* top-level window has taken activation away from this one, and that
    /// turns out not to happen reliably for a case like this. MainWindow is a
    /// Topmost, WS_EX-chromeless, AllowsTransparency dock that gets shown from a
    /// global keyboard/mouse hook callback rather than in response to the user
    /// directly clicking on it — exactly the kind of caller Windows' own
    /// focus-stealing-prevention heuristics are designed to be suspicious of, so
    /// Activate() succeeding fully (truly taking foreground activation, not just
    /// raising Visibility) is not actually guaranteed every single time. When it
    /// doesn't, later clicking elsewhere never generates a real activation-loss
    /// transition for this window to react to — which is exactly the "closes
    /// sometimes, not others, with no obvious pattern" behavior reported.
    ///
    /// On top of that, MainWindow is an AllowsTransparency window: real content
    /// (tiles, the search box, buttons) is opaque, but everything else — the 10px
    /// margin left around BackgroundBorder for its drop shadow, and the whole of
    /// BackgroundBorder's own fill once AppConfig.BackgroundOpacity is turned down to
    /// 0% — renders at zero alpha. WPF's hit-testing on a window like this follows
    /// that alpha: a click over a zero-alpha area isn't treated as "on this window"
    /// at all, so it passes straight through to whatever's actually behind it on the
    /// desktop — which is the click-through the background-opacity setting is
    /// actually for, but also means that click never reaches MainWindow, never
    /// changes its activation state, and so never triggers Window_Deactivated either,
    /// even though the user very much just interacted with something behind an
    /// (invisible) StartDock and would reasonably expect that to dismiss it.
    ///
    /// A global click hook sidesteps both problems at once, because it doesn't care
    /// which window (if any) actually ends up receiving the click, or whether this
    /// window is "active" in Windows' own terms — it just sees the literal screen
    /// coordinates of every click, always, and the owner (MainWindow, via App.xaml.cs's
    /// wiring — see <see cref="IsDockVisible"/>/<see cref="IsClickOnDockContent"/>)
    /// decides in its own terms whether that point actually landed on something real.
    /// </summary>
    public sealed class ClickOutsideService : IDisposable
    {
        /// <summary>Raised on a left- or right-button-down, while <see
        /// cref="IsDockVisible"/> reports true, that <see
        /// cref="IsClickOnDockContent"/> says isn't really on the dock.</summary>
        public event Action? OutsideClickDetected;

        /// <summary>Supplied by the owner (App.xaml.cs, wired straight to MainWindow) —
        /// kept as a plain delegate rather than a direct MainWindow reference so this
        /// class doesn't need to know anything about WPF windows or visual trees
        /// itself, the same shape HotkeyService/StartButtonOverlayService's own
        /// events already use ("just tell the owner something happened, let it decide
        /// what that means"). Checked before every hit-test below purely as a cheap
        /// early-out — there's no point walking MainWindow's visual tree for a click
        /// that arrived while the dock isn't even showing.</summary>
        public Func<bool>? IsDockVisible { get; set; }

        /// <summary>True if the given point (physical screen pixels, the same space
        /// MSLLHOOKSTRUCT.pt arrives in) lands on something that actually counts as
        /// "the dock" — a tile, a button, the search box, a resize grip, the open
        /// power flyout, and so on — false for dead space: outside the window
        /// entirely, its transparent margin, or an invisible-but-technically-there
        /// area like BackgroundBorder's own fill at 0% BackgroundOpacity. See
        /// MainWindow.IsDockContentAt, the only real implementation of this.</summary>
        public Func<System.Windows.Point, bool>? IsClickOnDockContent { get; set; }

        // Keeps a strong reference to the hook delegate for the hook's lifetime —
        // otherwise the GC can collect it out from under the unmanaged callback, the
        // same reason every other hook in this codebase (HotkeyService's _proc,
        // StartButtonOverlayService's _mouseProc) keeps its own field for this too.
        private NativeMethods.LowLevelMouseProc? _mouseProc;
        private IntPtr _hookHandle = IntPtr.Zero;

        // Where a failure inside MouseHookCallback gets logged — see that method's
        // own comment for why the catch exists at all. Optional (defaults to not
        // logging) purely so this class stays constructible without a folder in a
        // test/future context; App.xaml.cs always passes one in practice, the same
        // %AppData%\StartDock folder every other service's own crash/diagnostics log
        // already uses.
        private readonly string? _diagnosticsFolder;

        public ClickOutsideService(string? diagnosticsFolder = null)
        {
            _diagnosticsFolder = diagnosticsFolder;
        }

        public void Start()
        {
            if (_hookHandle != IntPtr.Zero)
                return;

            _mouseProc = MouseHookCallback;

            using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            IntPtr hMod = NativeMethods.GetModuleHandle(curModule.ModuleName);

            _hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, hMod, 0);
            if (_hookHandle == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"Failed to install click-outside mouse hook (Win32 error {err}).");
            }
        }

        public void Stop()
        {
            if (_hookHandle != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }
            _mouseProc = null;
        }

        /// <summary>The WH_MOUSE_LL callback Windows itself invokes for every mouse
        /// event system-wide. Deliberately just a try/catch shim around <see
        /// cref="MouseHookCallbackCore"/> — see HotkeyService.HookCallback's matching
        /// wrapper for the full story on why a low-level hook needs this at all
        /// (short version: an exception escaping a hook callback bypasses WPF's own
        /// Dispatcher.UnhandledException handling entirely and takes the whole
        /// process down with a bare, content-free native crash). Falls through to
        /// CallNextHookEx either way — never swallowing a real click, so a broken
        /// dismiss-check doesn't also break normal clicking anywhere else.</summary>
        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                return MouseHookCallbackCore(nCode, wParam, lParam);
            }
            catch (Exception ex)
            {
                LogFailure(ex);
                return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }
        }

        private IntPtr MouseHookCallbackCore(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
                return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            bool isLeftDown = wParam == (IntPtr)NativeMethods.WM_LBUTTONDOWN;
            bool isRightDown = wParam == (IntPtr)NativeMethods.WM_RBUTTONDOWN;

            // Only the initial button-down matters here — matches how a click
            // "outside" a native flyout/menu is judged the moment the press happens,
            // not on release.
            if ((isLeftDown || isRightDown) && IsDockVisible?.Invoke() == true)
            {
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var screenPoint = new System.Windows.Point(data.pt.X, data.pt.Y);

                if (IsClickOnDockContent?.Invoke(screenPoint) == false)
                    OutsideClickDetected?.Invoke();
            }

            // Never swallow anything — this service only ever watches, exactly like
            // Window_Deactivated already only watches; the click itself (and
            // whichever window, if any, actually receives it) is completely
            // untouched.
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        private void LogFailure(Exception ex)
        {
            if (string.IsNullOrEmpty(_diagnosticsFolder))
                return;

            try
            {
                Directory.CreateDirectory(_diagnosticsFolder);
                File.AppendAllText(
                    Path.Combine(_diagnosticsFolder, "crash.log"),
                    $"{DateTime.Now}: ClickOutsideService.MouseHookCallback threw: {ex}\n\n");
            }
            catch
            {
                // Best-effort logging for a best-effort catch-all — never let a
                // failure writing the log itself become a new problem.
            }
        }

        public void Dispose() => Stop();
    }
}
