using System;
using System.Drawing;
using System.Windows.Forms;

namespace BerthMenu.Services
{
    /// <summary>
    /// The system tray icon that keeps BerthMenu reachable while it runs in the
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
        public event Action? WhatsNewRequested;
        public event Action? TipsRequested;
        public event Action? ReportProblemRequested;
        public event Action? ExitRequested;

        /// <param name="isRunningAsAdministrator">When true, hides "Restart as
        /// Administrator" below — there's no point offering to relaunch elevated
        /// when this copy already is. See App.xaml.cs for how this is worked out
        /// and why relaunching elevated is ever useful: a fullscreen game (most
        /// often one with anti-cheat like EasyAntiCheat/BattlEye/Vanguard, which
        /// run elevated) sits at a higher Windows integrity level than a normally-
        /// launched BerthMenu, and Windows' UIPI security boundary means a lower-
        /// privilege process's low-level hooks (HotkeyService's keyboard hook,
        /// StartButtonOverlayService's mouse hook) simply cannot see input while a
        /// higher-privilege window has focus — no amount of hook-priority or
        /// retry logic on BerthMenu's side can work around that, only matching its
        /// own privilege level to the game's can.</param>
        public TrayIconService(bool isRunningAsAdministrator = false)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open BerthMenu", null, (_, _) => OpenDockRequested?.Invoke());
            menu.Items.Add("Settings...", null, (_, _) => OpenSettingsRequested?.Invoke());
            menu.Items.Add("Open Windows Start Menu", null, (_, _) => OpenNativeStartMenuRequested?.Invoke());

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("What's new", null, (_, _) => WhatsNewRequested?.Invoke());
            menu.Items.Add("Tips", null, (_, _) => TipsRequested?.Invoke());
            menu.Items.Add("Report a problem...", null, (_, _) => ReportProblemRequested?.Invoke());

            if (!isRunningAsAdministrator)
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Restart as Administrator", null, (_, _) => RestartAsAdminRequested?.Invoke());
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

            // Follow BerthMenu's light/dark theme each time the menu opens.
            menu.Opening += (_, _) => ApplyMenuTheme(menu);

            _notifyIcon = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "BerthMenu",
                ContextMenuStrip = menu,
                Visible = false,
            };

            _notifyIcon.DoubleClick += (_, _) => OpenDockRequested?.Invoke();
            _notifyIcon.BalloonTipClicked += (_, _) =>
            {
                var action = _balloonClicked;
                _balloonClicked = null;
                action?.Invoke();
            };
            _notifyIcon.BalloonTipClosed += (_, _) => _balloonClicked = null;
        }

        private Action? _balloonClicked;

        /// <summary>Reuses the .exe's own icon (Resources/BerthMenu.ico, wired up via
        /// the csproj's ApplicationIcon) for the tray, rather than embedding/loading a
        /// second copy of the same artwork — one source of truth for BerthMenu's
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

        private static void ApplyMenuTheme(ContextMenuStrip menu)
        {
            bool dark;
            try { dark = ThemeService.IsCurrentThemeDark(); }
            catch { dark = false; }

            if (!dark)
            {
                if (menu.Renderer is ToolStripProfessionalRenderer { ColorTable: DarkMenuColors })
                    menu.RenderMode = ToolStripRenderMode.ManagerRenderMode;
                foreach (ToolStripItem item in menu.Items)
                    item.ForeColor = SystemColors.ControlText;
                return;
            }

            menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = false };
            foreach (ToolStripItem item in menu.Items)
                item.ForeColor = Color.FromArgb(240, 240, 240);
        }

        /// <summary>The tray menu's colors on the dark theme.</summary>
        private sealed class DarkMenuColors : ProfessionalColorTable
        {
            private static readonly Color Back = Color.FromArgb(43, 43, 43);
            private static readonly Color Hover = Color.FromArgb(61, 61, 61);
            private static readonly Color Line = Color.FromArgb(70, 70, 70);

            public override Color ToolStripDropDownBackground => Back;
            public override Color ImageMarginGradientBegin => Back;
            public override Color ImageMarginGradientMiddle => Back;
            public override Color ImageMarginGradientEnd => Back;
            public override Color MenuBorder => Line;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color SeparatorDark => Line;
            public override Color SeparatorLight => Back;
        }

        public void Show() => _notifyIcon.Visible = true;

        /// <summary>A Windows notification from the tray icon (used for updates).
        /// <paramref name="onClick"/> runs if it's clicked.</summary>
        public void ShowNotification(string title, string text, Action? onClick = null)
        {
            _balloonClicked = onClick;
            _notifyIcon.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
        }

        public void Dispose()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
    }
}
