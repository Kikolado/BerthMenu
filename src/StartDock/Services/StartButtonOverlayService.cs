using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows.Automation;

namespace StartDock.Services
{
    /// <summary>
    /// Watches every mouse click system-wide via a global low-level mouse hook
    /// (WH_MOUSE_LL — the mouse counterpart of HotkeyService's WH_KEYBOARD_LL
    /// keyboard hook) and swallows any click that lands inside a taskbar's Start
    /// button, so clicking it opens StartDock instead of the native Start Menu.
    ///
    /// We don't hook or modify explorer.exe itself — that requires code injection,
    /// admin rights, and breaks on almost every Windows update. Instead, a
    /// DispatcherTimer periodically re-finds every taskbar's Start button via UI
    /// Automation (which button's exact bounds shift depending on taskbar
    /// alignment — centered by default in Windows 11 — DPI, and monitor) and
    /// remembers just its rectangle; the mouse hook then hit-tests every click
    /// against those remembered rectangles and swallows the ones that match.
    ///
    /// This replaced an earlier version of this class that instead kept an
    /// invisible, click-catching WPF window pinned on top of each button (still
    /// using the exact same UI-Automation lookup below to find where). That
    /// approach had a structural problem this one doesn't: it had to physically
    /// out-rank the real taskbar in Z-order to receive the click at all, and
    /// Explorer periodically reasserts Shell_TrayWnd's own place at the top of the
    /// Z-order on its own — redraws, notifications, another app also requesting
    /// topmost — which could silently sink an already-correctly-positioned overlay
    /// back underneath the real taskbar with nothing to say so. A low-level input
    /// hook doesn't have a "Z-order" to lose — it sees every click before Windows
    /// routes it to any window at all, ours or Explorer's, so there's nothing
    /// left to fight for position against.
    ///
    /// Multi-monitor: Windows has one primary taskbar ("Shell_TrayWnd") and, with "Show
    /// taskbar on all displays" enabled, one "Shell_SecondaryTrayWnd" per additional
    /// monitor. Whether a given secondary taskbar exposes its own Start-like button
    /// varies by Windows build/config; rather than assuming either way, every poll
    /// enumerates ALL taskbar windows and only remembers a rectangle for the ones
    /// that currently expose a findable Start button, so this adapts automatically as
    /// monitors are connected/disconnected or that setting is toggled.
    ///
    /// Diagnostics: on a taskbar where the button genuinely can't be found by either
    /// lookup in TryFindStartButtonRect, or where the lookup itself throws, this
    /// writes one detailed, capped dump to %AppData%\StartDock\overlay-diagnostics.log
    /// (once per process lifetime) — see MaybeDumpDiagnostics/LogFailure. There was
    /// previously no visibility at all into why this ever failed silently.
    /// </summary>
    public sealed class StartButtonOverlayService : IDisposable
    {
        private const string PrimaryTaskbarClassName = "Shell_TrayWnd";
        private const string SecondaryTaskbarClassName = "Shell_SecondaryTrayWnd";
        private const string StartButtonAutomationId = "StartButton";

        // The Start button rectangles found on the most recent poll — in physical
        // screen pixels, the same coordinate space MouseHookCallback's own
        // MSLLHOOKSTRUCT.pt arrives in (see its own comment). Rebuilt from scratch
        // on every poll rather than diffed against the previous set — with no
        // window objects to create/reuse/dispose any more, there's no cost to just
        // replacing the whole list each time.
        //
        // Both RefreshButtonRects (called from the DispatcherTimer below) and
        // MouseHookCallback (called by Windows via the hook) run on this object's
        // own thread — SetWindowsHookEx requires a thread with a message pump to
        // ever invoke either kind of low-level hook's callback, and that's the
        // same WPF Dispatcher thread the timer itself runs on — so this is never
        // touched from two threads at once and needs no locking.
        private List<NativeMethods.RECT> _buttonRects = new();

        private DispatcherTimer? _pollTimer;

        // Keeps a strong reference to the hook delegate for the hook's lifetime —
        // otherwise the GC can collect it out from under the unmanaged callback
        // (same reason HotkeyService keeps its own _proc field).
        private NativeMethods.LowLevelMouseProc? _mouseProc;
        private IntPtr _mouseHookHandle = IntPtr.Zero;

        // Where diagnostics get written (see MaybeDumpDiagnostics/LogFailure) —
        // %AppData%\StartDock\overlay-diagnostics.log, same folder ConfigService
        // already uses for crash.log. Optional (defaults to not logging at all)
        // purely so this class stays constructible without a folder in a test/
        // future context; App.xaml.cs always passes one in practice.
        private readonly string? _diagnosticsFolder;

        // Written at most once per process lifetime — see MaybeDumpDiagnostics.
        // This has genuinely never worked for at least one real install, so the
        // goal here isn't "log every miss" (which would either spam the file or,
        // rate-limited, still tell us nothing new after the first one) — it's "get
        // one detailed look at what this taskbar's automation tree actually
        // contains" to fix the lookup precisely instead of guessing blind.
        private bool _diagnosticsDumped;

        public bool Enabled { get; private set; }

        /// <summary>Raised when any covered Start button is clicked.</summary>
        public event Action? StartButtonClicked;

        /// <summary>Raised when something being dragged (a file from File Explorer,
        /// say) is held over a Start button for half a second — opens the dock so
        /// it can be dropped there to pin it.</summary>
        public event Action? StartButtonDragHover;

        // Left button state, for telling a click on the Start button from a drag
        // that started somewhere else and is passing over (or ending on) it.
        private bool _leftHeld;
        private bool _pressOnButton;
        private DispatcherTimer? _dragHoverTimer;

        public StartButtonOverlayService(string? diagnosticsFolder = null)
        {
            _diagnosticsFolder = diagnosticsFolder;
        }

        public void Start()
        {
            if (Enabled) return;
            Enabled = true;

            // Keep a strong reference to the delegate for the hook's lifetime — see
            // _mouseProc's own comment.
            _mouseProc = MouseHookCallback;

            using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            IntPtr hMod = NativeMethods.GetModuleHandle(curModule.ModuleName);

            _mouseHookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, hMod, 0);
            if (_mouseHookHandle == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"Failed to install mouse hook (Win32 error {err}).");
            }

            _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(600)
            };
            _pollTimer.Tick += (_, _) => RefreshButtonRects();
            _pollTimer.Start();

            RefreshButtonRects();
        }

        public void Stop()
        {
            Enabled = false;
            _pollTimer?.Stop();
            _pollTimer = null;

            if (_mouseHookHandle != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHookHandle);
                _mouseHookHandle = IntPtr.Zero;
            }
            _mouseProc = null;

            _buttonRects = new();
        }

        /// <summary>Re-finds every taskbar's Start button via UI Automation and
        /// replaces <see cref="_buttonRects"/> wholesale with whatever's found this
        /// time — called once immediately in Start and every 600ms afterward by
        /// _pollTimer. There's no per-entry object to create, reuse, or dispose any
        /// more (no overlay windows), so unlike the old Reposition this doesn't need
        /// to diff against what it found last time at all.</summary>
        private void RefreshButtonRects()
        {
            var rects = new List<NativeMethods.RECT>();

            foreach (var taskbarHwnd in FindAllTaskbarWindows())
            {
                var rect = TryFindStartButtonRect(taskbarHwnd);
                if (rect != null)
                    rects.Add(rect.Value);
                // else: this taskbar currently has no findable Start button — most
                // secondary-monitor taskbars fall in this bucket. Nothing to add.
            }

            _buttonRects = rects;
        }

        /// <summary>The WH_MOUSE_LL callback — invoked by Windows for every mouse
        /// event system-wide, on this object's own thread (see _buttonRects' own
        /// comment on why that means no locking is needed here). Swallows a left or
        /// right button press/release that lands inside any remembered Start button
        /// rectangle, firing StartButtonClicked on the left button's release —
        /// ordinary "click" semantics, not the press — and returns without ever
        /// calling CallNextHookEx for either half of that click, which is what
        /// actually keeps it from reaching the real taskbar underneath: a low-level
        /// hook blocks an event by returning a nonzero value instead of chaining to
        /// the next hook, the same technique HotkeyService already uses to swallow
        /// the Windows key.
        ///
        /// The right button is swallowed too without doing anything itself — purely
        /// preserving this class's original behavior (its previous, overlay-window
        /// version also ate right-clicks unconditionally) rather than changing it as
        /// a side effect of this rewrite. Worth knowing: this does mean right-
        /// clicking the covered area no longer reaches Explorer's own Start-button
        /// context menu (the Win+X "quick link" menu) at all.</summary>
        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                return MouseHookCallbackCore(nCode, wParam, lParam);
            }
            catch (Exception ex)
            {
                // See HotkeyService.HookCallback's matching wrapper for why this
                // exists: WH_MOUSE_LL is invoked by Windows outside WPF's own
                // Dispatcher.UnhandledException machinery, so an exception escaping
                // here (most realistically from a StartButtonClicked subscriber
                // opening/laying out the dock window) would otherwise take the
                // whole process down hard with a content-free native crash instead
                // of landing in this class's own overlay-diagnostics.log via
                // LogFailure below. Falls through to CallNextHookEx — pass the
                // click through untouched rather than swallowing it, so a broken
                // dock doesn't also break normal clicking.
                LogFailure(ex);
                return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
            }
        }

        private IntPtr MouseHookCallbackCore(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
                return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);

            bool isLeftDown = wParam == (IntPtr)NativeMethods.WM_LBUTTONDOWN;
            bool isLeftUp = wParam == (IntPtr)NativeMethods.WM_LBUTTONUP;
            bool isRightDown = wParam == (IntPtr)NativeMethods.WM_RBUTTONDOWN;
            bool isRightUp = wParam == (IntPtr)NativeMethods.WM_RBUTTONUP;
            bool isMove = wParam == (IntPtr)WM_MOUSEMOVE;

            // Moves only matter while the left button is held (a drag).
            if (isMove && !_leftHeld)
                return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);

            if (isLeftDown || isLeftUp || isRightDown || isRightUp || isMove)
            {
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

                // MSLLHOOKSTRUCT.pt is already in physical screen pixels (Windows
                // never DPI-adjusts raw input coordinates for this hook), the same
                // space UI Automation's BoundingRectangle gave us when
                // TryFindStartButtonRect built these rectangles — no manual DPI
                // math needed.
                bool over = PointIsOverAnyButton(data.pt);

                if (isMove)
                {
                    // A drag from somewhere else held over the Start button: open
                    // the dock after a moment (see StartButtonDragHover). Never
                    // swallowed — the drag carries on normally.
                    bool stillHeld = (NativeMethods.GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
                    if (!stillHeld)
                        _leftHeld = false;
                    if (over && stillHeld && !_pressOnButton)
                        StartDragHover();
                    else
                        StopDragHover();
                    return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
                }

                if (isLeftDown)
                {
                    _leftHeld = true;
                    _pressOnButton = over;
                }
                else if (isLeftUp)
                {
                    bool pressWasOnButton = _pressOnButton;
                    _leftHeld = false;
                    _pressOnButton = false;
                    StopDragHover();

                    // The end of a drag that started elsewhere: let it through, so
                    // whatever was dragging never misses its button release.
                    if (over && !pressWasOnButton)
                        return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
                }

                if (over)
                {
                    if (isLeftUp)
                        StartButtonClicked?.Invoke();

                    return (IntPtr)1; // swallow — never let this reach the real taskbar
                }
            }

            return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        private const int WM_MOUSEMOVE = 0x0200;
        private const int VK_LBUTTON = 0x01;

        private void StartDragHover()
        {
            if (_dragHoverTimer is { IsEnabled: true })
                return;
            _dragHoverTimer ??= CreateDragHoverTimer();
            _dragHoverTimer.Start();
        }

        private void StopDragHover() => _dragHoverTimer?.Stop();

        private DispatcherTimer CreateDragHoverTimer()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_leftHeld && (NativeMethods.GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0)
                    StartButtonDragHover?.Invoke();
            };
            return timer;
        }

        private bool PointIsOverAnyButton(NativeMethods.POINT pt)
        {
            foreach (var r in _buttonRects)
            {
                if (pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom)
                    return true;
            }

            return false;
        }

        /// <summary>Enumerates every currently-open taskbar window: the one primary
        /// ("Shell_TrayWnd") plus one "Shell_SecondaryTrayWnd" per additional monitor
        /// that has "Show taskbar on all displays" in effect.</summary>
        internal static List<IntPtr> FindAllTaskbarWindows()
        {
            var handles = new List<IntPtr>();

            foreach (var className in new[] { PrimaryTaskbarClassName, SecondaryTaskbarClassName })
            {
                IntPtr hwnd = IntPtr.Zero;
                while (true)
                {
                    hwnd = NativeMethods.FindWindowEx(IntPtr.Zero, hwnd, className, null);
                    if (hwnd == IntPtr.Zero)
                        break;
                    handles.Add(hwnd);
                }
            }

            return handles;
        }

        /// <summary>
        /// Locates a taskbar's Start button via UI Automation. Returns null if this
        /// particular taskbar doesn't currently expose one (e.g. most secondary-monitor
        /// taskbars), momentarily can't be queried (e.g. during an Explorer restart), or
        /// — the case this has apparently always hit on at least one real machine —
        /// neither lookup below matches anything on that Windows build's taskbar at all.
        /// </summary>
        private NativeMethods.RECT? TryFindStartButtonRect(IntPtr taskbarHwnd)
        {
            try
            {
                var taskbarElement = AutomationElement.FromHandle(taskbarHwnd);
                if (taskbarElement == null)
                    return null;

                var byId = new PropertyCondition(AutomationElement.AutomationIdProperty, StartButtonAutomationId);
                var startButton = taskbarElement.FindFirst(TreeScope.Descendants, byId);

                if (startButton == null)
                {
                    // Fallback for builds where the AutomationId differs: look for
                    // anything named "start" (any control type, case-insensitive —
                    // PropertyCondition's own string match is exact and case-sensitive,
                    // so this walks by hand instead via FindAll + a manual comparison).
                    // Best-effort — if a future Windows update renames it to something
                    // that doesn't even contain "start", this simply stops finding the
                    // button and that taskbar's overlay stays put until it does again.
                    try
                    {
                        foreach (AutomationElement candidate in taskbarElement.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
                        {
                            string name = SafeCurrent(() => candidate.Current.Name);
                            if (string.Equals(name, "Start", StringComparison.OrdinalIgnoreCase))
                            {
                                startButton = candidate;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // Leave startButton null — the outer catch below already treats
                        // that as "not found for now", same as any other failure here.
                    }
                }

                if (startButton == null)
                {
                    MaybeDumpDiagnostics(taskbarElement, taskbarHwnd);
                    return null;
                }

                var bounds = startButton.Current.BoundingRectangle;
                if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
                    return null;

                return new NativeMethods.RECT
                {
                    Left = (int)Math.Round(bounds.Left),
                    Top = (int)Math.Round(bounds.Top),
                    Right = (int)Math.Round(bounds.Right),
                    Bottom = (int)Math.Round(bounds.Bottom),
                };
            }
            catch (Exception ex)
            {
                // UI Automation can throw transient COM exceptions (e.g. element went
                // away mid-call during an Explorer restart) — but if this is throwing
                // on EVERY poll rather than occasionally, that's a real, previously
                // invisible failure (nothing was ever logged here before), so log it
                // rather than silently treating it exactly like a normal transient miss.
                LogFailure(ex);
                return null;
            }
        }

        private static string SafeCurrent(Func<string> getter)
        {
            try { return getter(); }
            catch { return string.Empty; }
        }

        /// <summary>Writes a one-time, capped dump of the taskbar's automation tree
        /// (every descendant's Name/AutomationId/ControlType/ClassName/bounds) to
        /// %AppData%\StartDock\overlay-diagnostics.log — the actual data needed to
        /// fix the lookup precisely for a taskbar where neither strategy above finds
        /// anything, instead of guessing blind. Runs on a background thread (a full
        /// TreeScope.Descendants walk of a modern, XAML-hosted Windows 11 taskbar can
        /// genuinely be large) so a slow walk can never freeze the dock's UI thread,
        /// which is what calls into this in the first place (RefreshButtonRects runs
        /// off a DispatcherTimer tick). Bounded by both an element count and a
        /// wall-clock cutoff so a pathological tree can't run away indefinitely.</summary>
        private void MaybeDumpDiagnostics(AutomationElement taskbarElement, IntPtr taskbarHwnd)
        {
            if (_diagnosticsDumped || string.IsNullOrEmpty(_diagnosticsFolder))
                return;
            _diagnosticsDumped = true;

            string folder = _diagnosticsFolder;
            _ = Task.Run(() =>
            {
                var sb = new StringBuilder();
                try
                {
                    sb.AppendLine($"{DateTime.Now}: Start button not found on taskbar 0x{taskbarHwnd.ToInt64():X} — automation tree dump follows (first match, depth-first, capped):");

                    var deadline = DateTime.UtcNow.AddSeconds(4);
                    int count = 0;

                    void Walk(AutomationElement element, int depth)
                    {
                        if (count >= 300 || DateTime.UtcNow > deadline)
                            return;
                        count++;

                        try
                        {
                            var c = element.Current;
                            sb.AppendLine($"{new string(' ', depth * 2)}[{c.ControlType.ProgrammaticName}] Name=\"{c.Name}\" AutomationId=\"{c.AutomationId}\" ClassName=\"{c.ClassName}\" Rect={c.BoundingRectangle}");
                        }
                        catch (Exception exInner)
                        {
                            sb.AppendLine($"{new string(' ', depth * 2)}<element unreadable: {exInner.Message}>");
                        }

                        AutomationElement? child;
                        try { child = TreeWalker.RawViewWalker.GetFirstChild(element); }
                        catch { return; }

                        while (child != null && count < 300 && DateTime.UtcNow <= deadline)
                        {
                            Walk(child, depth + 1);
                            try { child = TreeWalker.RawViewWalker.GetNextSibling(child); }
                            catch { break; }
                        }
                    }

                    Walk(taskbarElement, 0);
                    sb.AppendLine($"{DateTime.Now}: dump complete — {count} element(s) visited.");

                    Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder, "overlay-diagnostics.log"), sb.ToString());
                }
                catch
                {
                    // Best-effort diagnostics for a best-effort feature — never let a
                    // failure writing the dump itself become a new problem.
                }
            });
        }

        private void LogFailure(Exception ex)
        {
            if (string.IsNullOrEmpty(_diagnosticsFolder))
                return;

            try
            {
                Directory.CreateDirectory(_diagnosticsFolder);
                File.AppendAllText(
                    Path.Combine(_diagnosticsFolder, "overlay-diagnostics.log"),
                    $"{DateTime.Now}: TryFindStartButtonRect threw: {ex}\n\n");
            }
            catch
            {
                // Ignore — see MaybeDumpDiagnostics' own comment.
            }
        }

        public void Dispose() => Stop();
    }
}
