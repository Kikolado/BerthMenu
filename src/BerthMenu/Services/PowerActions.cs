using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace BerthMenu.Services
{
    /// <summary>Wraps the same actions Windows' own power flyout offers.</summary>
    public static class PowerActions
    {
        public static void Sleep() => NativeMethods.SetSuspendState(false, true, true);

        public static void Lock() => NativeMethods.LockWorkStation();

        public static void SignOut() => NativeMethods.ExitWindowsEx(NativeMethods.EWX_LOGOFF, 0);

        public static void Restart()
        {
            // Routed through shutdown.exe (rather than ExitWindowsEx) so the user gets
            // the normal "close your apps" grace period and any pending-updates prompt,
            // matching what the native power menu does. Because this goes through the
            // same OS shutdown path the native Start menu uses, any staged Windows
            // Update is applied on the way down/up regardless of what our button's
            // label says — IsRestartPending() below only changes the label so the
            // user isn't surprised by an update screen, it doesn't change behavior.
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = false, CreateNoWindow = true });
        }

        public static void ShutDown()
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { UseShellExecute = false, CreateNoWindow = true });
        }

        /// <summary>
        /// True if Windows Update has staged an update that needs a restart to finish
        /// installing — the same condition that makes the native Start menu's power
        /// button read "Update and restart" / "Update and shut down" instead of the
        /// plain labels. There's no single documented API for this; Windows itself
        /// consults several registry locations that different update components each
        /// set when they need a reboot, so this checks the well-known ones and treats
        /// any of them existing as "pending." Wrapped defensively — a registry access
        /// failure (permissions, missing hive, whatever) should just mean we fall back
        /// to the plain "Restart"/"Shut down" labels, never a crash.
        /// </summary>
        public static bool IsRestartPending()
        {
            try
            {
                // Legacy Windows Update Agent flag — set by the older WUA components.
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                {
                    if (key != null)
                        return true;
                }

                // Component-Based Servicing — set for reboots needed by servicing
                // stack / CBS operations (most feature and cumulative updates today).
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                {
                    if (key != null)
                        return true;
                }

                // Newer Update Orchestrator (Windows 10/11 Update Client) reboot flag.
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\WindowsUpdate\UpdateOrchestrator\RebootRequired"))
                {
                    if (key != null)
                        return true;
                }

                // A pending file-rename operation (PendingFileRenameOperations) also
                // implies a reboot is needed to finish applying something, but that
                // value is used for far more than just Windows Update (installers,
                // driver setup, etc.) — checking it would make this fire in cases
                // that have nothing to do with "there's an update," so it's
                // deliberately left out in favor of the three update-specific keys
                // above staying a little conservative rather than over-eager.

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
