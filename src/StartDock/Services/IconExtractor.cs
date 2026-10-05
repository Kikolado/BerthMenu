using System;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Interop;

namespace StartDock.Services
{
    /// <summary>
    /// Extracts the shell icon for a file/shortcut and caches it as a PNG so the
    /// dock doesn't have to hit the shell every time it's opened.
    /// </summary>
    public class IconExtractor
    {
        private readonly ConfigService _config;

        public IconExtractor(ConfigService config)
        {
            _config = config;
        }

        /// <summary>Where ExtractAndCache saves the icon for <paramref name="iconId"/>.</summary>
        public string GetCachedPath(Guid iconId) => Path.Combine(_config.IconCacheFolder, $"{iconId}.png");

        /// <summary>A Guid that's always the same for the same key (an AppId, a file
        /// path), so an icon extracted once can be found again in the cache on the next
        /// run instead of being re-extracted into a new file every time.</summary>
        public static Guid StableId(string key)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            byte[] hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key.ToUpperInvariant()));
            return new Guid(hash);
        }

        /// <summary>
        /// Extracts a large (usually 32x32 or 48x48, DPI-dependent) icon for
        /// <paramref name="targetPath"/> and saves it as {iconId}.png in the icon cache.
        /// Returns the cached file path, or null if extraction failed.
        /// </summary>
        public string? ExtractAndCache(string targetPath, Guid iconId)
        {
            // "shell:AppsFolder\<AppID>" (Store/UWP apps and other Start-menu-registered
            // apps picked via the installed-apps list) is a virtual shell path, not a
            // real file — it needs the PIDL-based lookup instead of the plain one.
            return targetPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                ? ExtractAndCacheFromShellPath(targetPath, iconId)
                : ExtractAndCacheFromFilePath(targetPath, iconId);
        }

        private string? ExtractAndCacheFromFilePath(string targetPath, Guid iconId)
        {
            var shinfo = new NativeMethods.SHFILEINFO();
            IntPtr result = NativeMethods.SHGetFileInfo(
                targetPath,
                0,
                ref shinfo,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf(shinfo),
                NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);

            if (result == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero)
                return null;

            return SaveIconAndDestroy(shinfo, iconId);
        }

        private string? ExtractAndCacheFromShellPath(string shellPath, Guid iconId)
        {
            IntPtr pidl = IntPtr.Zero;
            try
            {
                int hr = NativeMethods.SHParseDisplayName(shellPath, IntPtr.Zero, out pidl, 0, out _);
                if (hr != 0 || pidl == IntPtr.Zero)
                    return null;

                var shinfo = new NativeMethods.SHFILEINFO();
                IntPtr result = NativeMethods.SHGetFileInfoByPidl(
                    pidl,
                    0,
                    ref shinfo,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf(shinfo),
                    NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON | NativeMethods.SHGFI_PIDL);

                if (result == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero)
                    return null;

                return SaveIconAndDestroy(shinfo, iconId);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (pidl != IntPtr.Zero)
                    NativeMethods.CoTaskMemFree(pidl);
            }
        }

        private string? SaveIconAndDestroy(NativeMethods.SHFILEINFO shinfo, Guid iconId)
        {
            try
            {
                using var icon = System.Drawing.Icon.FromHandle(shinfo.hIcon);
                var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                var cachedPath = Path.Combine(_config.IconCacheFolder, $"{iconId}.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                using (var fs = new FileStream(cachedPath, FileMode.Create, FileAccess.Write))
                {
                    encoder.Save(fs);
                }

                return cachedPath;
            }
            catch
            {
                return null;
            }
            finally
            {
                NativeMethods.DestroyIcon(shinfo.hIcon);
            }
        }
    }
}
