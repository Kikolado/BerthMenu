using System;
using System.IO;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace BerthMenu.Services
{
    /// <summary>Reads the signed-in user's display name and account picture for the bottom bar.</summary>
    public static class UserInfoService
    {
        /// <summary>
        /// The "friendly" name shown in Windows' own Start Menu (full name if available,
        /// e.g. for domain/Microsoft accounts), falling back to the plain Windows username.
        /// </summary>
        public static string GetDisplayName()
        {
            try
            {
                uint size = 0;
                var sb = new StringBuilder();
                NativeMethods.GetUserNameEx(NativeMethods.EXTENDED_NAME_FORMAT.NameDisplay, sb, ref size);
                if (size > 0)
                {
                    sb.EnsureCapacity((int)size);
                    if (NativeMethods.GetUserNameEx(NativeMethods.EXTENDED_NAME_FORMAT.NameDisplay, sb, ref size) && sb.Length > 0)
                        return sb.ToString();
                }
            }
            catch
            {
                // fall through to the plain username
            }

            return Environment.UserName;
        }

        /// <summary>
        /// Path to the user's account picture, matching what Windows itself shows in the
        /// Start Menu (the largest cached size available), or null if none is set.
        /// </summary>
        public static string? GetAccountPicturePath()
        {
            try
            {
                var sid = WindowsIdentity.GetCurrent().User?.Value;
                if (string.IsNullOrEmpty(sid))
                    return null;

                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\{sid}");
                if (key == null)
                    return null;

                // Prefer the largest cached size; Windows stores several under value
                // names like Image1080, Image448, Image240, Image96, Image48, Image32.
                foreach (var valueName in new[] { "Image1080", "Image448", "Image240", "Image96", "Image48", "Image32" })
                {
                    if (key.GetValue(valueName) is string path && File.Exists(path))
                        return path;
                }
            }
            catch
            {
                // ignore — caller falls back to a generic person glyph
            }

            return null;
        }
    }
}
