using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace BerthMenu.Services
{
    /// <summary>Registers/unregisters BerthMenu to launch at sign-in via the per-user Run key.</summary>
    public static class AutostartService
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "BerthMenu";
        private const string OldValueName = "StartDock"; // before the rename

        public static void SetEnabled(bool enabled)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                             ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null) return;

            // The entry from before the rename pointed at StartDock.exe.
            if (key.GetValue(OldValueName) != null)
                key.DeleteValue(OldValueName, throwOnMissingValue: false);

            if (enabled)
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                    key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
            }
            else
            {
                if (key.GetValue(ValueName) != null)
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }

        public static bool IsEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) != null;
        }
    }
}
