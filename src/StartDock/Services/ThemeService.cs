using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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

        /// <summary>Gives a dialog window (Settings, Add to StartDock, Rename) a dark
        /// title bar while the dark theme is in use — Windows draws a light one
        /// otherwise, above a dark window. Call right after InitializeComponent.</summary>
        public static void ApplyTitleBarTheme(Window window)
        {
            window.SourceInitialized += (_, _) =>
            {
                try
                {
                    IntPtr hwnd = new WindowInteropHelper(window).Handle;
                    int dark = IsCurrentThemeDark() ? 1 : 0;
                    NativeMethods.DwmSetWindowAttribute(hwnd,
                        NativeMethods.DwmWindowAttribute.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                }
                catch
                {
                    // Older Windows without dark title bars — the default one is fine.
                }
            };
        }

        /// <summary>Whether the theme currently loaded (Theme.xaml or Theme.Dark.xaml)
        /// is the dark one, judged by its background color.</summary>
        public static bool IsCurrentThemeDark() =>
            Application.Current?.TryFindResource("DockBackgroundColor") is Color c
            && 0.299 * c.R + 0.587 * c.G + 0.114 * c.B < 128;

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
