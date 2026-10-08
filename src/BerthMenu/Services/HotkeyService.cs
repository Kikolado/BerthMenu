using System;
using System.Runtime.InteropServices;
using BerthMenu.Models;

namespace BerthMenu.Services
{
    /// <summary>
    /// Installs a global low-level keyboard hook and watches for the Windows key
    /// (and, in Shift+Win mode, Shift+Windows key) to trigger the dock instead of
    /// the native Start Menu.
    ///
    /// This must be constructed and disposed on a thread that pumps Win32 messages
    /// (the WPF UI thread's Dispatcher qualifies) — SetWindowsHookEx requires that
    /// for WH_KEYBOARD_LL hooks to actually receive events.
    ///
    /// How the Windows key is handled. A keyboard hook has to decide about each key
    /// event as it happens, and can't know yet whether Win is being pressed on its
    /// own or as the start of a combo (Win+D, Win+L, Win+Shift+S…). So:
    ///  - Win going down is held back from Windows for now.
    ///  - If Win comes back up with nothing pressed in between, that was "Win
    ///    alone": the release is held back too, so Windows never saw any of it and
    ///    doesn't open its Start Menu — and the dock opens instead.
    ///  - If another key goes down first, it's a combo: that key is held back as
    ///    well, and BerthMenu sends Windows "Win down" followed by that key in one
    ///    go, so they arrive in the right order. From then on the keys flow through
    ///    normally, including Win's eventual release.
    ///
    /// Never leaving Windows thinking Win is still held: when Win is released, the
    /// hook checks whether Windows actually saw Win go down (GetAsyncKeyState — a
    /// keyboard hook runs before Windows records the key it's handling, so this
    /// reads Windows' state just before this release). If it did — because of a
    /// combo above, or because the hook answered too slowly and Windows passed the
    /// key-down on without waiting (it only gives hooks a fraction of a second, easy
    /// to miss while the dock is busy) — the release always goes through too. Only a
    /// release whose key-down Windows never saw is held back. An earlier version
    /// didn't check this, and could leave Windows believing Win was held, so D
    /// acted as Win+D and M as Win+M until Win was pressed again.
    ///
    /// (A version that let Win through and tapped an unused "mask" key to keep Start
    /// closed — the AutoHotkey trick — didn't stop Windows 11 opening Start, so this
    /// holds Win back instead.)
    ///
    /// Ctrl+Esc (the classic alternate Start shortcut) is suppressed outright in
    /// Windows key mode — Esc isn't a modifier, so swallowing it can't leave
    /// anything stuck.
    /// </summary>
    public sealed class HotkeyService : IDisposable
    {
        private NativeMethods.LowLevelKeyboardProc? _proc;
        private IntPtr _hookHandle = IntPtr.Zero;
        private bool _shiftDown;

        // Where a failure inside HookCallback gets logged — see HookCallback's own
        // comment on why that catch exists at all. Optional (defaults to not
        // logging) purely so this class stays constructible without a folder in a
        // test/future context; App.xaml.cs always passes one in practice, the same
        // %AppData%\BerthMenu folder StartButtonOverlayService and ConfigService's
        // own crash.log already use.
        private readonly string? _diagnosticsFolder;

        public HotkeyService(string? diagnosticsFolder = null)
        {
            _diagnosticsFolder = diagnosticsFolder;
        }

        // The vkCode of the Windows key currently held back from Windows (0 = none).
        // Set on its key-down, cleared on its key-up.
        private int _pendingWinVk;

        // True once another key went down while _pendingWinVk was held: a combo, and
        // Windows has been sent the Win key-down (see SendWinDownThenKey).
        private bool _pendingWinIsCombo;

        // When the last event for the held Win key arrived (KBDLLHOOKSTRUCT.time, ms).
        // A held key keeps repeating many times a second, so a long silence means its
        // release was missed (e.g. it happened on the lock screen, where this hook
        // doesn't run) — see IsPendingWinStale.
        private int _lastWinEventTime;

        public HotkeyMode Mode { get; set; } = HotkeyMode.WindowsKey;

        /// <summary>Only consulted when Mode == HotkeyMode.Custom — see AppConfig's
        /// CustomHotkeyModifiers/CustomHotkeyVirtualKey for where these come from.
        /// CustomVirtualKey == 0 means no combo has been captured yet, in which case
        /// Custom mode simply never fires (see HookCallback).</summary>
        public HotkeyModifiers CustomModifiers { get; set; } = HotkeyModifiers.None;
        public int CustomVirtualKey { get; set; }

        /// <summary>Raised when the configured activation shortcut is pressed.</summary>
        public event Action? DockRequested;

        public void Start()
        {
            if (_hookHandle != IntPtr.Zero)
                return;

            // Keep a strong reference to the delegate for the hook's lifetime — otherwise
            // the GC can collect it out from under the unmanaged callback.
            _proc = HookCallback;

            using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            IntPtr hMod = NativeMethods.GetModuleHandle(curModule.ModuleName);

            _hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, hMod, 0);

            if (_hookHandle == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"Failed to install keyboard hook (Win32 error {err}).");
            }
        }

        public void Stop()
        {
            if (_hookHandle != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }

            _pendingWinVk = 0;
            _pendingWinIsCombo = false;
        }

        /// <summary>The WH_KEYBOARD_LL callback Windows itself invokes for every
        /// keystroke system-wide. Deliberately just a try/catch shim around
        /// <see cref="HookCallbackCore"/> — a low-level hook callback runs outside
        /// WPF's own Dispatcher.UnhandledException machinery entirely, so an
        /// exception escaping from here (most realistically from a DockRequested
        /// subscriber doing real UI work, e.g. opening/laying out the dock window)
        /// doesn't get caught by that handler at all. Instead it propagates straight
        /// through this native callback boundary and Windows tears the whole process
        /// down immediately with a bare "unhandled exception ... during a user
        /// callback" crash and zero diagnostic information — no crash.log entry, no
        /// exception message, nothing. Catching everything right here, logging it,
        /// and falling through to CallNextHookEx (pass the keystroke through
        /// untouched rather than swallowing it, so a broken dock doesn't also take
        /// normal typing down with it) keeps one bad downstream frame from ever
        /// being fatal again — and, just as importantly, means the next crash.log
        /// actually says what happened instead of nothing at all.</summary>
        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                return HookCallbackCore(nCode, wParam, lParam);
            }
            catch (Exception ex)
            {
                LogFailure(ex);
                return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }
        }

        private IntPtr HookCallbackCore(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
                return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

            // Never touch an injected event — that includes the Win-down we ourselves
            // synthesize below. Without this check our own synthetic event would loop
            // straight back into this same swallow logic and never actually reach
            // Windows, defeating the whole point of reconstructing the combo.
            bool isInjected = (data.flags & NativeMethods.LLKHF_INJECTED) != 0;
            if (isInjected)
                return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            int vk = data.vkCode;
            bool isKeyDown = wParam == (IntPtr)NativeMethods.WM_KEYDOWN || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN;
            bool isKeyUp = wParam == (IntPtr)NativeMethods.WM_KEYUP || wParam == (IntPtr)NativeMethods.WM_SYSKEYUP;

            // Track shift state ourselves rather than calling GetAsyncKeyState for every
            // event — cheaper, and avoids subtle races with the hook's own event stream.
            if (vk == NativeMethods.VK_SHIFT || vk == NativeMethods.VK_LSHIFT || vk == NativeMethods.VK_RSHIFT)
            {
                if (isKeyDown) _shiftDown = true;
                if (isKeyUp) _shiftDown = false;
            }

            bool isWinKey = vk == NativeMethods.VK_LWIN || vk == NativeMethods.VK_RWIN;

            if (isWinKey)
                return HandleWinKeyEvent(vk, isKeyDown, isKeyUp, data.time, nCode, wParam, lParam);

            // Another key going down while Win is held back makes this a combo
            // (Win+D, Win+Shift+S, ...). Hold this key back too and send Windows
            // "Win down" then this key, in that order, so the combo works normally.
            if (_pendingWinVk != 0 && !_pendingWinIsCombo && isKeyDown)
            {
                if (IsPendingWinStale(data.time))
                {
                    // Win's release was missed — it isn't really held any more.
                    _pendingWinVk = 0;
                }
                else
                {
                    _pendingWinIsCombo = true;
                    SendWinDownThenKey(_pendingWinVk, data);
                    return (IntPtr)1;
                }
            }

            // Ctrl+Esc is the other classic "open Start" shortcut. Suppress it the same
            // way, but only while we're the ones fully replacing Start (WindowsKey mode) —
            // in Shift+Win mode we leave native Start shortcuts alone entirely.
            if (Mode == HotkeyMode.WindowsKey && vk == NativeMethods.VK_ESCAPE && isKeyDown)
            {
                bool ctrlDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
                if (ctrlDown)
                {
                    DockRequested?.Invoke();
                    return (IntPtr)1;
                }
            }

            // A custom activation combo (see AppConfig.CustomHotkeyModifiers/
            // CustomHotkeyVirtualKey) doesn't need the Win key's own tentative-withhold
            // dance above — unlike a bare Win press, there's no "alone vs. start of a
            // combo" ambiguity to wait out here, so this is really just the same
            // pattern as the Ctrl+Esc check right above: on the target key's own
            // key-down, check the modifier state is an exact match and fire.
            if (Mode == HotkeyMode.Custom && isKeyDown && CustomVirtualKey != 0 && vk == CustomVirtualKey && CustomModifiersMatch())
            {
                DockRequested?.Invoke();
                return (IntPtr)1;
            }

            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        /// <summary>True when the modifiers currently physically held (Ctrl/Alt/Shift/
        /// either Windows key) exactly match CustomModifiers — not merely "at least
        /// these", so pressing extra modifiers alongside the configured combo doesn't
        /// also trigger it. VK_CONTROL/VK_MENU/VK_SHIFT each already report true for
        /// either their left or right variant; VK_LWIN/VK_RWIN have no such combined
        /// constant, so both are checked explicitly.</summary>
        private bool CustomModifiersMatch()
        {
            bool ctrlDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
            bool altDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;
            bool shiftDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
            bool winDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LWIN) & 0x8000) != 0
                         || (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RWIN) & 0x8000) != 0;

            bool wantCtrl = (CustomModifiers & HotkeyModifiers.Control) != 0;
            bool wantAlt = (CustomModifiers & HotkeyModifiers.Alt) != 0;
            bool wantShift = (CustomModifiers & HotkeyModifiers.Shift) != 0;
            bool wantWin = (CustomModifiers & HotkeyModifiers.Windows) != 0;

            return ctrlDown == wantCtrl && altDown == wantAlt && shiftDown == wantShift && winDown == wantWin;
        }

        private IntPtr HandleWinKeyEvent(int vk, bool isKeyDown, bool isKeyUp, int time, int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (isKeyDown)
            {
                if (_pendingWinVk != 0 && IsPendingWinStale(time))
                    _pendingWinVk = 0; // an old press whose release was missed

                if (_pendingWinVk == 0)
                {
                    bool activatesDock = Mode switch
                    {
                        // Plain Win always drives the dock.
                        HotkeyMode.WindowsKey => true,

                        // Only Shift+Win drives the dock; if Shift isn't already held
                        // when Win goes down, leave Win alone entirely — native Start,
                        // Win+D, Win+Shift+S and the rest all behave as normal.
                        HotkeyMode.ShiftWindowsKey => _shiftDown,

                        _ => false,
                    };

                    if (!activatesDock)
                        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

                    _pendingWinVk = vk;
                    _pendingWinIsCombo = false;
                    _lastWinEventTime = time;
                    return (IntPtr)1; // held back for now
                }

                if (vk == _pendingWinVk)
                {
                    _lastWinEventTime = time;
                    // Auto-repeat while held: held back too, unless Windows already
                    // has Win down (a combo), in which case it's just passed along.
                    return _pendingWinIsCombo
                        ? NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam)
                        : (IntPtr)1;
                }

                // The other Windows key while one is held: leave it to Windows.
                return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            if (isKeyUp && vk == _pendingWinVk)
            {
                bool alone = !_pendingWinIsCombo;
                _pendingWinVk = 0;
                _pendingWinIsCombo = false;

                // Did Windows see this Win key go down? (The hook runs before Windows
                // records this release, so this is the state just before it.)
                bool windowsHasItDown = (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

                if (alone)
                    DockRequested?.Invoke();

                if (windowsHasItDown)
                    return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
                return (IntPtr)1; // Windows never saw the press, so it mustn't see the release
            }

            // A Windows key this hook isn't holding back — let Windows have it.
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        private bool IsPendingWinStale(int now) =>
            unchecked(now - _lastWinEventTime) > 1500;

        /// <summary>Sends Windows the held-back Win key-down followed by the key that
        /// made this a combo, as one SendInput call so they arrive in that order.
        /// Both are injected, so this hook lets them straight through.</summary>
        private static void SendWinDownThenKey(int winVk, NativeMethods.KBDLLHOOKSTRUCT key)
        {
            var inputs = new[]
            {
                KeyInput((ushort)winVk, 0, NativeMethods.KEYEVENTF_EXTENDEDKEY),
                KeyInput((ushort)key.vkCode, (ushort)key.scanCode,
                    (key.flags & LLKHF_EXTENDED) != 0 ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0),
            };
            NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        }

        private const int LLKHF_EXTENDED = 0x01;

        private static NativeMethods.INPUT KeyInput(ushort vk, ushort scan, uint flags) => new()
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = scan,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };

        private void LogFailure(Exception ex)
        {
            if (string.IsNullOrEmpty(_diagnosticsFolder))
                return;

            try
            {
                System.IO.Directory.CreateDirectory(_diagnosticsFolder);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(_diagnosticsFolder, "crash.log"),
                    $"{DateTime.Now}: HotkeyService.HookCallback threw: {ex}\n\n");
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
