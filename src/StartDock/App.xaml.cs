using System;
using System.Threading;
using System.Windows;
using StartDock.Models;
using StartDock.Services;
using StartDock.Views;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace StartDock
{
    public partial class App : Application
    {
        // Prevents a second copy of StartDock from running (e.g. launched again from
        // the Run-key autostart entry while a manual instance is already active) —
        // two copies would both try to install the keyboard hook and Start-button
        // overlay, fighting each other.
        private static Mutex? _singleInstanceMutex;

        private ConfigService _configService = null!;
        private HotkeyService _hotkeyService = null!;
        private StartButtonOverlayService _overlayService = null!;
        private ClickOutsideService _clickOutsideService = null!;
        private TrayIconService _trayIconService = null!;
        private MainWindow? _dockWindow;

        public AppConfig Config { get; private set; } = new();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool alreadyRelaunched = Array.IndexOf(e.Args, ElevatedRelaunchArg) >= 0;

            // Single-instance check by actually *acquiring* the mutex, not by asking
            // whether this process created it. The old check (initiallyOwned + the
            // createdNew flag) broke "Restart as Admin": the outgoing copy releases
            // the mutex before launching the elevated one, but it still has the
            // mutex open while it shuts down, so the named object still exists —
            // the new copy saw createdNew == false, reported "already running", and
            // quit, while the old one finished exiting too, leaving nothing running.
            // Ownership is what actually matters. A copy started by
            // RestartAsAdministrator also waits a few seconds in case the old one
            // hasn't let go yet.
            bool acquired;
            try
            {
                _singleInstanceMutex = new Mutex(initiallyOwned: false, "Global\\StartDock-SingleInstance-9F3B2C4E");
                try
                {
                    acquired = _singleInstanceMutex.WaitOne(alreadyRelaunched ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true; // previous copy crashed without releasing it — it's ours now
                }
            }
            catch (UnauthorizedAccessException)
            {
                // The mutex exists but belongs to an elevated copy this (unelevated)
                // one isn't allowed to open — which itself means one is running.
                acquired = false;
            }

            if (!acquired)
            {
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = null;
                MessageBox.Show("StartDock is already running (check your system tray).", "StartDock",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (_, args) =>
            {
                // A background/tray app crashing silently is worse than a visible error —
                // but we don't want a hook or overlay hiccup to be fatal either, so log
                // and keep running rather than propagating.
                try
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(_configService.AppDataFolder, "crash.log"),
                        $"{DateTime.Now}: {args.Exception}\n\n");
                }
                catch { /* ignore */ }
                args.Handled = true;
            };

            _configService = new ConfigService();
            Config = _configService.Load();

            // AppConfig.StartAsAdmin: hand off to an elevated copy before anything
            // (hooks, overlay, tray icon) gets installed here. The marker argument
            // stops an endless relaunch loop if the new copy somehow still comes up
            // un-elevated. If the UAC prompt is declined, RestartAsAdministrator
            // returns false and this launch just carries on as a normal one.
            if (Config.StartAsAdmin && !IsElevated && !alreadyRelaunched && RestartAsAdministrator())
                return;

            // Re-sync the sign-in entry with where this .exe actually lives now. It
            // stores the full .exe path, so without this, installing a new version
            // to a different folder (or moving the .exe) would leave sign-in
            // pointing at the old location until Settings was saved again.
            try { AutostartService.SetEnabled(Config.AutoStart); } catch { /* not worth failing startup over */ }

            ApplyTheme();

            _dockWindow = new MainWindow(_configService, Config);
            _dockWindow.ConfigChanged += OnConfigChanged;
            _dockWindow.Initialize();

            _hotkeyService = new HotkeyService(_configService.AppDataFolder)
            {
                Mode = Config.Hotkey,
                CustomModifiers = Config.CustomHotkeyModifiers,
                CustomVirtualKey = Config.CustomHotkeyVirtualKey,
            };
            _hotkeyService.DockRequested += () => _dockWindow.ToggleVisibility();
            TryStart("keyboard hook", _hotkeyService.Start);

            _overlayService = new StartButtonOverlayService(_configService.AppDataFolder);
            _overlayService.StartButtonClicked += () => _dockWindow.ToggleVisibility();
            if (Config.ReplaceStartButton)
                TryStart("Start-button overlay", _overlayService.Start);

            // See ClickOutsideService's own doc comment for why this exists alongside
            // MainWindow's plain Window_Deactivated handler rather than instead of it —
            // short version: Deactivated alone was proving unreliable for a Topmost,
            // hook-triggered, AllowsTransparency dock like this one. Always started
            // (not gated behind a setting) — it's a pure watcher, never swallows a
            // click, and does nothing at all while the dock is hidden (see
            // IsDockVisible below), so there's no real cost to leaving it running.
            _clickOutsideService = new ClickOutsideService(_configService.AppDataFolder)
            {
                IsDockVisible = () => _dockWindow?.Visibility == Visibility.Visible,
                IsClickOnDockContent = pt => _dockWindow?.IsDockContentAt(pt) ?? true,
            };
            _clickOutsideService.OutsideClickDetected += () => _dockWindow?.HideDock();
            TryStart("click-outside detection", _clickOutsideService.Start);

            _trayIconService = new TrayIconService(IsElevated);
            _trayIconService.OpenDockRequested += () => _dockWindow.ShowDock();
            _trayIconService.OpenSettingsRequested += () => _dockWindow.OpenSettings();
            _trayIconService.OpenNativeStartMenuRequested += AppLauncher.OpenNativeStartMenu;
            _trayIconService.RestartAsAdminRequested += () => RestartAsAdministrator();
            _trayIconService.ExitRequested += Shutdown;
            _trayIconService.Show();
        }

        /// <summary>Relaunches this same .exe elevated (via the "runas" shell verb,
        /// which shows the normal UAC prompt) and exits this copy — see
        /// TrayIconService's own doc comment on why running elevated can matter
        /// (fullscreen games with elevated anti-cheat). Environment.ProcessPath
        /// (rather than Assembly.Location, which is always empty for a single-file
        /// publish — see TrayIconService.LoadAppIcon's own comment on that same
        /// pitfall) reliably resolves the real .exe path in every publish mode.</summary>
        /// <remarks>Returns true once the elevated copy has launched (and this one is
        /// shutting down), false if it didn't — most often a declined UAC prompt — in
        /// which case this instance keeps running and keeps its single-instance
        /// mutex. Used by the tray menu, SettingsWindow's "Restart as Admin" button,
        /// and AppConfig.StartAsAdmin at startup.</remarks>
        internal bool RestartAsAdministrator()
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                    return false;

                var psi = new System.Diagnostics.ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = ElevatedRelaunchArg,
                };

                // Release the single-instance mutex now, before launching the
                // elevated copy — not just in OnExit below, which runs later
                // (after Shutdown() asynchronously winds down the dispatcher) —
                // otherwise the new elevated process can start fast enough to see
                // this still-exiting instance still holding the mutex, conclude
                // "already running", and quietly exit instead of actually taking
                // over. OnExit's own release is now defensive/best-effort only
                // (see its try/catch) for the ordinary Exit path, where this
                // early release never runs.
                try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* already released, or never owned — fine either way */ }

                System.Diagnostics.Process.Start(psi);
                Shutdown();
                return true;
            }
            catch
            {
                // Most likely the UAC prompt was cancelled (Win32Exception), or
                // something else stopped the elevated copy launching — either way,
                // stay running exactly as before. The mutex was released above in
                // anticipation of handing over, so take it back; otherwise a second
                // copy launched later would think nothing was running.
                try { _singleInstanceMutex?.WaitOne(0); } catch { /* best effort */ }
                return false;
            }
        }

        /// <summary>Passed to the elevated copy by RestartAsAdministrator, so that
        /// copy never tries AppConfig.StartAsAdmin's auto-elevation again.</summary>
        private const string ElevatedRelaunchArg = "--elevated-relaunch";

        /// <summary>Whether this process is running with admin rights.</summary>
        internal static bool IsElevated { get; } =
            new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

        /// <summary>
        /// Runs a native-hook installation step without letting a failure (e.g. a
        /// restrictive Group Policy blocking global hooks, or the taskbar not existing
        /// yet during an Explorer restart) bring down the whole background app. StartDock
        /// keeps running — just without that one feature — and logs why.
        /// </summary>
        private void TryStart(string featureName, Action start)
        {
            try
            {
                start();
            }
            catch (Exception ex)
            {
                try
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(_configService.AppDataFolder, "crash.log"),
                        $"{DateTime.Now}: failed to start {featureName}: {ex}\n\n");
                }
                catch { /* ignore */ }
            }
        }

        private void OnConfigChanged(AppConfig updated)
        {
            Config = updated;
            _configService.Save(updated);

            _hotkeyService.Mode = updated.Hotkey;
            _hotkeyService.CustomModifiers = updated.CustomHotkeyModifiers;
            _hotkeyService.CustomVirtualKey = updated.CustomHotkeyVirtualKey;

            if (updated.ReplaceStartButton && !_overlayService.Enabled)
                TryStart("Start-button overlay", _overlayService.Start);
            else if (!updated.ReplaceStartButton && _overlayService.Enabled)
                _overlayService.Stop();

            AutostartService.SetEnabled(updated.AutoStart);

            ApplyTheme();
        }

        private void ApplyTheme()
        {
            bool isDark = ThemeService.ResolveIsDark(Config.Theme);
            var themeUri = new Uri(isDark ? "Resources/Theme.Dark.xaml" : "Resources/Theme.xaml", UriKind.Relative);
            var newDict = new ResourceDictionary { Source = themeUri };

            // Index 0 is reserved for the theme dictionary (see App.xaml) — swap just
            // that entry so Styles.xaml's DynamicResource lookups pick up new brushes
            // immediately without reloading the whole resource tree.
            Resources.MergedDictionaries[0] = newDict;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _hotkeyService?.Dispose();
            _overlayService?.Dispose();
            _clickOutsideService?.Dispose();
            _trayIconService?.Dispose();

            // Guarded because RestartAsAdministrator above may already have
            // released this same mutex ahead of calling Shutdown() — releasing an
            // already-released Mutex throws, and that's expected/harmless here,
            // not a real error to surface.
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* see above */ }

            base.OnExit(e);
        }
    }
}
