using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StartDock.Views;

namespace StartDock.Services
{
    /// <summary>
    /// Caches the installed-apps list (and their extracted icons) so search and the
    /// "Add to StartDock" picker don't re-run Get-StartApps or re-extract every icon
    /// each time. The list is refreshed automatically when it gets stale (see
    /// RefreshIfStaleAsync, called whenever the dock opens) and on demand from
    /// Settings' "Refresh Apps".
    ///
    /// Keeping the dock responsive while this runs:
    ///  - Get-StartApps (PowerShell) and its JSON parsing run on a background thread.
    ///  - A refresh only adds and removes what changed; apps already listed keep
    ///    their view-models and icons, so a refresh that finds nothing new costs
    ///    one background query and nothing on the UI thread.
    ///  - Icons: extraction AND image decoding both happen on background threads,
    ///    and the finished images are handed to the UI at background priority, so
    ///    they trickle in without stalling input. Previously each icon was decoded
    ///    on the UI thread with a blocking Dispatcher.Invoke — 100+ of those back to
    ///    back is what caused the ~2 second freeze when a fresh list loaded.
    ///  - Icons are cached on disk under a stable per-app id, so later runs just
    ///    load the saved PNG instead of extracting the icon again.
    ///
    /// Everything here is called from the UI thread, so there's no locking beyond the
    /// "already running" checks below.
    /// </summary>
    public static class InstalledAppsCache
    {
        private static ObservableCollection<InstalledAppViewModel>? _apps;
        private static Task<ObservableCollection<InstalledAppViewModel>>? _initialLoad;
        private static Task? _iconLoadTask;
        private static Task<int>? _refreshTask;
        private static DateTime _lastLoadedUtc;

        /// <summary>True once the list has been loaded at least once.</summary>
        public static bool IsLoaded => _apps != null;

        /// <summary>The cached list, or null if it hasn't loaded yet.</summary>
        public static ObservableCollection<InstalledAppViewModel>? Apps => _apps;

        /// <summary>Returns the shared apps collection, querying Get-StartApps only the
        /// first time. The same instance is returned every time, so icon updates and
        /// refreshes are visible to everything holding it.</summary>
        public static Task<ObservableCollection<InstalledAppViewModel>> GetAppsAsync()
        {
            if (_apps != null)
                return Task.FromResult(_apps);

            // Share one in-flight load between callers (startup warm-up, a search
            // typed before it finished, the picker) instead of running PowerShell twice.
            _initialLoad ??= LoadInitialAsync();
            return _initialLoad;
        }

        private static async Task<ObservableCollection<InstalledAppViewModel>> LoadInitialAsync()
        {
            try
            {
                var apps = await StartAppsService.GetInstalledAppsAsync();
                _apps = new ObservableCollection<InstalledAppViewModel>(apps.Select(a => new InstalledAppViewModel(a)));
                _lastLoadedUtc = DateTime.UtcNow;
                AppSeenTracker.Record(apps);
                return _apps;
            }
            finally
            {
                _initialLoad = null;
            }
        }

        /// <summary>Refreshes the list only if it's older than <paramref name="maxAge"/>
        /// (and has loaded at least once). Returns how many new apps were found.</summary>
        public static Task<int> RefreshIfStaleAsync(TimeSpan maxAge)
        {
            if (_apps == null || DateTime.UtcNow - _lastLoadedUtc < maxAge)
                return Task.FromResult(0);

            return RefreshAsync();
        }

        /// <summary>Re-queries Windows for the installed-apps list and updates the
        /// cached collection in place — adding new apps, dropping uninstalled ones,
        /// leaving the rest untouched. Returns how many new apps were found. Concurrent
        /// calls share the same refresh.</summary>
        public static Task<int> RefreshAsync()
        {
            _refreshTask ??= RefreshCoreAsync();
            return _refreshTask;
        }

        private static async Task<int> RefreshCoreAsync()
        {
            try
            {
                if (_apps == null)
                {
                    var loaded = await GetAppsAsync();
                    return loaded.Count;
                }

                var fresh = await StartAppsService.GetInstalledAppsAsync();
                _lastLoadedUtc = DateTime.UtcNow;

                // An empty result almost always means PowerShell failed this time
                // (see StartAppsService), not that every app was uninstalled — keep
                // the list we have rather than wiping it.
                if (fresh.Count == 0)
                    return 0;

                AppSeenTracker.Record(fresh);

                var freshIds = new HashSet<string>(fresh.Select(a => a.AppId), StringComparer.OrdinalIgnoreCase);
                var existingIds = new HashSet<string>(_apps.Select(vm => vm.Model.AppId), StringComparer.OrdinalIgnoreCase);

                for (int i = _apps.Count - 1; i >= 0; i--)
                {
                    if (!freshIds.Contains(_apps[i].Model.AppId))
                        _apps.RemoveAt(i);
                }

                int added = 0;
                foreach (var app in fresh)
                {
                    if (!existingIds.Add(app.AppId))
                        continue; // already listed (or a duplicate within this same query)
                    _apps.Add(new InstalledAppViewModel(app));
                    added++;
                }

                // Let the next EnsureIconsLoadedAsync start a fresh pass, so the new
                // entries get icons instead of it returning the old, finished task.
                if (added > 0)
                    _iconLoadTask = null;

                return added;
            }
            finally
            {
                _refreshTask = null;
            }
        }

        /// <summary>Kicks off icon loading for any cached app that doesn't have one
        /// yet. Safe to call often — once every app has an icon this is a no-op.</summary>
        public static Task EnsureIconsLoadedAsync(IconExtractor iconExtractor)
        {
            if (_apps == null)
                return Task.CompletedTask;

            // Already running (or already finished) — return the same task rather than
            // starting a second pass over the same apps.
            _iconLoadTask ??= LoadIconsAsync(_apps.Where(vm => vm.IconImage == null).ToList(), iconExtractor);
            return _iconLoadTask;
        }

        private static async Task LoadIconsAsync(List<InstalledAppViewModel> pending, IconExtractor iconExtractor)
        {
            // A few at a time — Get-StartApps commonly returns 100+ entries, and more
            // parallel shell lookups than this is no faster in practice.
            using var throttle = new SemaphoreSlim(4);
            var dispatcher = System.Windows.Application.Current?.Dispatcher;

            var tasks = pending.Select(async vm =>
            {
                await throttle.WaitAsync().ConfigureAwait(false);
                try
                {
                    var (path, image) = await StaWorker.Run(() => LoadIcon(vm.Model.AppId, iconExtractor)).ConfigureAwait(false);
                    if (image == null || dispatcher == null)
                        return;

                    // BeginInvoke at Background priority: hand the finished image to the
                    // UI without blocking this worker, and without jumping ahead of input
                    // or rendering — icons fill in as the UI has time.
                    // Discarded on purpose: this worker doesn't need to wait for it.
                    _ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        vm.CachedIconPath = path;
                        vm.IconImage = image;
                    }));
                }
                catch
                {
                    // This one app's icon failed — leave it blank rather than losing the
                    // rest of the list over it.
                }
                finally
                {
                    throttle.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);
        }

        // How long a saved app icon is trusted before it's extracted again. Keeps icons
        // current when an app updates its own, and gives an icon that came out blank
        // (the shell's icon cache can be cold right after an install) another chance
        // on its own, without anyone having to notice and fix it by hand.
        private static readonly TimeSpan IconMaxAge = TimeSpan.FromDays(14);

        /// <summary>Runs on a background thread: reuses this app's icon from the disk
        /// cache if it's there and recent, otherwise extracts it fresh, then decodes and
        /// freezes the image so it can be handed to the UI thread as-is.</summary>
        private static (string? Path, ImageSource? Image) LoadIcon(string appId, IconExtractor iconExtractor, bool forceExtract = false)
        {
            Guid id = IconExtractor.StableId("app:" + appId);
            string? path = iconExtractor.GetCachedPath(id);

            bool stale = forceExtract
                || !File.Exists(path)
                || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > IconMaxAge;
            if (stale)
            {
                if (File.Exists(path))
                {
                    try { File.Delete(path); } catch { /* overwritten below anyway */ }
                }
                path = iconExtractor.ExtractAndCache($"shell:AppsFolder\\{appId}", id);
            }

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return (null, null);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            // The file name stays the same when an icon is re-extracted, so skip WPF's
            // per-path image cache — otherwise it could hand back the old picture.
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return (path, bitmap);
        }

        /// <summary>Right-click → Refresh icon on an installed app in search results:
        /// extracts that app's icon again and updates the cached list itself, so every
        /// later search (and the Add picker) shows the fixed icon, not just this tile.
        /// Returns the new icon, or null if it still couldn't be extracted.</summary>
        public static async Task<ImageSource?> RefreshIconAsync(string appId, IconExtractor iconExtractor)
        {
            var (path, image) = await StaWorker.Run(() => LoadIcon(appId, iconExtractor, forceExtract: true));

            var vm = _apps?.FirstOrDefault(a => string.Equals(a.Model.AppId, appId, StringComparison.OrdinalIgnoreCase));
            if (vm != null && image != null)
            {
                vm.CachedIconPath = path;
                vm.IconImage = image;
            }
            return image;
        }
    }
}
