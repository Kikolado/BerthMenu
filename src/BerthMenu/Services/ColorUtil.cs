using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace BerthMenu.Services
{
    /// <summary>Helpers for the "Custom color" settings, which are stored in
    /// config.json as "#RRGGBB" text.</summary>
    public static class ColorUtil
    {
        /// <summary>Reads "#RRGGBB", "RRGGBB", "#RGB" or "RGB" (any case, spaces
        /// ignored).</summary>
        public static bool TryParse(string? text, out Color color)
        {
            color = Colors.Black;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string hex = text.Trim().TrimStart('#').Replace(" ", "");
            if (hex.Length == 3)
                hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                return false;

            color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }

        /// <summary>The stored color, or <paramref name="fallback"/> if it can't be read.</summary>
        public static Color Parse(string? text, Color fallback) => TryParse(text, out var c) ? c : fallback;

        public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        public static SolidColorBrush Brush(Color color, byte alpha = 255)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }

        /// <summary>True for colors where white text reads better than black
        /// (relative luminance, as in WCAG).</summary>
        public static bool IsDark(Color color)
        {
            static double Channel(byte v)
            {
                double c = v / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            double luminance = 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
            // White on it beats black on it below ~0.18.
            return luminance < 0.18;
        }

        /// <summary>The accent color picked in Windows Settings → Personalization →
        /// Colors.</summary>
        public static Color WindowsAccent()
        {
            try
            {
                // Stored by Windows as a DWORD in ABGR order.
                if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr)
                    return Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
            }
            catch
            {
                // Fall through to WPF's view of it.
            }

            var glass = SystemParameters.WindowGlassColor;
            return Color.FromRgb(glass.R, glass.G, glass.B);
        }
    }
}
