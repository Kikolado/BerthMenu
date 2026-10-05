using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using StartDock.Models;

namespace StartDock.Services
{
    /// <summary>Launches a <see cref="DockIcon"/>'s target the same way Explorer would.</summary>
    public static class AppLauncher
    {
        public static bool Launch(DockIcon icon)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = icon.TargetPath,
                    UseShellExecute = true, // required for .lnk, documents, folders, and shell:AppsFolder\... targets
                };

                if (!string.IsNullOrWhiteSpace(icon.Arguments) && !icon.TargetPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    psi.Arguments = icon.Arguments;

                if (!string.IsNullOrWhiteSpace(icon.WorkingDirectory) && Directory.Exists(icon.WorkingDirectory))
                    psi.WorkingDirectory = icon.WorkingDirectory;

                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void OpenFileExplorer()
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            }
            catch
            {
                // Explorer not launching is unusual enough that there's nothing sensible
                // to fall back to; fail silently rather than showing a scary error dialog.
            }
        }

        public static void OpenWindowsSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true });
            }
            catch
            {
                // ignore
            }
        }

        public static void OpenTaskManager()
        {
            try
            {
                // Launched directly. An earlier attempt routed this through
                // explorer.exe to dodge a suspected auto-elevation/keyboard-hook
                // interaction (see git history) — but explorer.exe doesn't treat
                // a bare "taskmgr.exe" argument as something to launch, it just
                // falls back to opening a default folder, so that "fix" broke the
                // button outright. Back to the simple, known-working direct launch
                // until the elevation theory is actually confirmed.
                Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>Opens the classic per-app Volume Mixer (SndVol.exe) — the same
        /// thing "sndvol" from the Run dialog opens, and distinct from the single-slider
        /// quick popup a click on the taskbar's speaker icon shows.</summary>
        public static void OpenVolumeMixer()
        {
            try
            {
                Process.Start(new ProcessStartInfo("SndVol.exe") { UseShellExecute = true });
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>Opens the real, native Windows Start Menu — for the separate
        /// "open the real Start Menu" button some StartDock installs show (see
        /// AppConfig.ShowNativeStartMenuButton / MainWindow.xaml's
        /// NativeStartMenuButton), for anyone StartDock gets distributed to who
        /// still wants access to it even with HotkeyService/StartButtonOverlayService
        /// otherwise intercepting every Windows key press and every taskbar
        /// Start-button click.
        ///
        /// There's no public Win32/shell API to just "open the real Start Menu" from
        /// another process — Windows only opens it in response to actually seeing a
        /// Windows key press (or a click on its own taskbar button) arrive. So this
        /// works around that the same way HotkeyService already sends its own
        /// key taps to Windows (see its SendWinDownThenKey): it synthesizes
        /// a plain, isolated Win key tap via SendInput. That's what makes this safe
        /// to call even while HotkeyService's own keyboard hook and
        /// StartButtonOverlayService's own mouse hook are both active — every event
        /// SendInput generates is marked LLKHF_INJECTED, and both of those hooks
        /// already let injected input straight through untouched (see
        /// HotkeyService.HookCallback's isInjected check), so this tap reaches
        /// Windows exactly as if StartDock weren't intercepting anything at all.</summary>
        public static void OpenNativeStartMenu()
        {
            try
            {
                var down = new NativeMethods.INPUT
                {
                    type = NativeMethods.INPUT_KEYBOARD,
                    U = new NativeMethods.InputUnion
                    {
                        ki = new NativeMethods.KEYBDINPUT
                        {
                            wVk = (ushort)NativeMethods.VK_LWIN,
                            wScan = 0,
                            dwFlags = NativeMethods.KEYEVENTF_EXTENDEDKEY,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero,
                        },
                    },
                };

                var up = new NativeMethods.INPUT
                {
                    type = NativeMethods.INPUT_KEYBOARD,
                    U = new NativeMethods.InputUnion
                    {
                        ki = new NativeMethods.KEYBDINPUT
                        {
                            wVk = (ushort)NativeMethods.VK_LWIN,
                            wScan = 0,
                            dwFlags = NativeMethods.KEYEVENTF_EXTENDEDKEY | NativeMethods.KEYEVENTF_KEYUP,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero,
                        },
                    },
                };

                NativeMethods.SendInput(2, new[] { down, up }, Marshal.SizeOf<NativeMethods.INPUT>());
            }
            catch
            {
                // Worst case the button silently does nothing — never worth crashing
                // the dock over.
            }
        }
    }
}
