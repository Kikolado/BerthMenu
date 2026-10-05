using Microsoft.Win32;
using StartDock.Models;

namespace StartDock.Services
{
    /// <summary>Resolves the effective light/dark theme for the dock chrome.</summary>
    public static class ThemeService
    {
        public static bool ResolveIsDark(ThemeMode mode)
        {
            return mode switch
            {
                ThemeMode.Dark => true,
                ThemeMode.Light => false,
                _ => !IsWindowsAppsLightThemeEnabled(),
            };
        }

        private static bool IsWindowsAppsLightThemeEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int value)
                    return value != 0;
            }
            catch
            {
                // ignore — default to light below
            }

            return true;
        }
    }
}
