using System;
using System.Drawing;
using System.Windows.Forms;

namespace StartDock.Services
{
    /// <summary>
    /// The system tray icon that keeps StartDock reachable while it runs in the
    /// background waiting for its hotkey/Start-button click. Uses WinForms' NotifyIcon
    /// (WPF has no built-in equivalent) — this is the one place the app touches WinForms.
    /// </summary>
    public sealed class TrayIconService : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;

        public event Action? OpenDockRequested;
        public event Action? OpenSettingsRequested;
        public event Action? OpenNativeStartMenuRequested;
        public event Action? RestartAsAdminRequested;
        public event Action? ExitRequested;

        /// <param name="isRunningAsAdministrator">When true, hides "Restart as
        /// Administrator" below — there's no point offering to relaunch elevated
        /// when this copy already is. See App.xaml.cs for how this is worked out
        /// and why relaunching elevated is ever useful: a fullscreen game (most
        /// often one with anti-cheat like EasyAntiCheat/BattlEye/Vanguard, which
        /// run elevated) sits at a higher Windows integrity level than a normally-
        /// launched StartDock, and Windows' UIPI security boundary means a lower-
        /// privilege process's low-level hooks (HotkeyService's keyboard hook,
        /// StartButtonOverlayService's mouse hook) simply cannot see input while a
        /// higher-privilege window has focus — no amount of hook-priority or
        /// retry logic on StartDock's side can work around that, only matching its
        /// own privilege level to the game's can.</param>
        public TrayIconService(bool isRunningAsAdministrator = false)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open StartDock", null, (_, _) => OpenDockRequested?.Invoke());
            menu.Items.Add("Settings...", null, (_, _) => OpenSettingsRequested?.Invoke());
            menu.Items.Add("Open Windows Start Menu", null, (_, _) => OpenNativeStartMenuRequested?.Invoke());

            if (!isRunningAsAdministrator)
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Restart as Administrator", null, (_, _) => RestartAsAdminRequested?.Invoke());
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

            _notifyIcon = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "StartDock",
                ContextMenuStrip = menu,
                Visible = false,
            };

            _notifyIcon.DoubleClick += (_, _) => OpenDockRequested?.Invoke();
        }

        /// <summary>Reuses the .exe's own icon (Resources/StartDock.ico, wired up via
        /// the csproj's ApplicationIcon) for the tray, rather than embedding/loading a
        /// second copy of the same artwork — one source of truth for StartDock's
        /// branding. Falls back to the generic system icon if that ever fails.</summary>
        private static Icon LoadAppIcon()
        {
            try
            {
                // Environment.ProcessPath is the real .exe in every publish mode,
                // including single-file, where Assembly.Location is always empty.
                string? exePath = Environment.ProcessPath;

                if (!string.IsNullOrEmpty(exePath))
                {
                    var icon = Icon.ExtractAssociatedIcon(exePath);
                    if (icon != null)
                        return icon;
                }
            }
            catch
            {
                // fall through to the generic system icon below
            }

            return SystemIcons.Application;
        }

        public void Show() => _notifyIcon.Visible = true;

        public void Dispose()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
    }
}
