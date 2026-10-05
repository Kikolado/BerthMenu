using System;
using System.Runtime.InteropServices;
using StartDock.Models;

namespace StartDock.Services
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
    /// How the Windows key is handled: Windows always gets to see the real Win
    /// key-down and key-up — this hook never swallows either one. To stop the
    /// native Start Menu from opening, the moment Win goes down StartDock sends one
    /// tap of an unassigned "mask" key (VK 0xE8, the same trick AutoHotkey uses).
    /// Windows only opens Start when Win is released with nothing else pressed
    /// in between, so that tap is enough to keep Start closed, while every real
    /// combo (Win+D, Win+L, Win+Shift+S, ...) still works exactly as normal. If Win
    /// is released with no other key pressed, that was "Win alone", and the dock
    /// opens.
    ///
    /// Why it works this way: an earlier version withheld the Win key-down and
    /// swallowed the key-up instead. If the hook ever ran late (Windows gives a
    /// keyboard hook only a fraction of a second before passing a key on without
    /// it — easy to hit while the dock is busy, say animating a GIF background),
    /// Windows could see the key-down but never the key-up, and then believed Win
    /// was still held: D minimized everything (Win+D), M minimized all windows
    /// (Win+M), and so on, until Win was pressed again. Never swallowing Win's own
    /// events means Windows always sees a complete press, so that can't happen.
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
        // %AppData%\StartDock folder StartButtonOverlayService and ConfigService's
        // own crash.log already use.
        private readonly string? _diagnosticsFolder;

        public HotkeyService(string? diagnosticsFolder = null)
        {
            _diagnosticsFolder = diagnosticsFolder;
        }

        // The vkCode of the Windows key currently held that may open the dock when
        // released (0 = none). Set on its key-down, cleared on its key-up.
        private int _trackedWinVk;

        // True once any other key has gone down while _trackedWinVk is held — a combo
        // like Win+D, not "Win alone", so its release doesn't open the dock.
        private bool _otherKeyDuringWin;

        // Unassigned virtual-key code tapped while Win is held, so Windows doesn't
        // open its own Start Menu when Win is released — see the class comment.
        private const ushort MaskVirtualKey = 0xE8;

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

            _trackedWinVk = 0;
            _otherKeyDuringWin = false;
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
                return HandleWinKeyEvent(vk, isKeyDown, isKeyUp, nCode, wParam, lParam);

            // Any other key going down while Win is held makes this a combo (Win+D,
            // Win+Shift+S, ...), so releasing Win won't open the dock. Windows already
            // has the real Win key-down, so the combo itself just works.
            if (_trackedWinVk != 0 && isKeyDown)
                _otherKeyDuringWin = true;

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

        private IntPtr HandleWinKeyEvent(int vk, bool isKeyDown, bool isKeyUp, int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (isKeyDown)
            {
                if (_trackedWinVk == 0)
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

                    if (activatesDock)
                    {
                        _trackedWinVk = vk;
                        _otherKeyDuringWin = false;
                        SendMaskKeyTap(); // keeps the native Start Menu from opening on release
                    }
                }
                else if (vk != _trackedWinVk)
                {
                    _otherKeyDuringWin = true; // both Windows keys at once — not "Win alone"
                }

                // Always let Windows see Win go down (auto-repeats included).
                return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            if (isKeyUp && vk == _trackedWinVk)
            {
                bool alone = !_otherKeyDuringWin;
                _trackedWinVk = 0;
                _otherKeyDuringWin = false;

                if (alone)
                    DockRequested?.Invoke();
            }

            // Always let Windows see Win go up too, so it never thinks Win is still held.
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        /// <summary>Taps the unassigned mask key (down + up). It's injected, so this
        /// hook ignores it; Windows just sees "some other key was pressed with Win",
        /// which is what stops it opening the Start Menu on release. If the tap is
        /// blocked (e.g. an elevated window is in front and StartDock isn't running
        /// as admin), the worst case is the native Start Menu also opening.</summary>
        private static void SendMaskKeyTap()
        {
            static NativeMethods.INPUT Key(uint flags) => new()
            {
                type = NativeMethods.INPUT_KEYBOARD,
                U = new NativeMethods.InputUnion
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = MaskVirtualKey,
                        wScan = 0,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero,
                    },
                },
            };

            NativeMethods.SendInput(2, new[] { Key(0), Key(NativeMethods.KEYEVENTF_KEYUP) }, Marshal.SizeOf<NativeMethods.INPUT>());
        }

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
