using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using StartDock.Models;
using StartDock.Services;

namespace StartDock.Views
{
    public partial class SettingsWindow : Window
    {
        /// <summary>Maps PositionCombo's on-screen item order (see SettingsWindow.xaml)
        /// to the actual DockPosition each one represents. Needed because the combo is
        /// grouped in a friendlier order (all top positions, then all middle, then all
        /// bottom) than DockPosition's own declaration order, which has to stay
        /// append-only instead (see that enum's doc comment) — so PositionCombo's
        /// SelectedIndex can no longer be cast straight to a DockPosition the way it
        /// used to when the two orders matched. Index into this array with
        /// SelectedIndex to get the DockPosition, or Array.IndexOf the other way —
        /// both directions are used below. Keep this in sync with PositionCombo's
        /// ComboBoxItems if either one changes.</summary>
        private static readonly DockPosition[] PositionComboOrder =
        {
            DockPosition.TopLeft,
            DockPosition.TopCenter,
            DockPosition.TopRight,
            DockPosition.MiddleLeft,
            DockPosition.Center,
            DockPosition.MiddleRight,
            DockPosition.BottomLeft,
            DockPosition.BottomCenter,
            DockPosition.BottomRight,
        };

        private readonly AppConfig _original;
        private readonly ConfigService _configService;

        // Absolute path to the (already-imported, see ConfigService.ImportBackgroundImage)
        // background image this dialog will save, or null for "no image" — seeded from
        // the incoming config and updated live by ChooseBackgroundImage_Click/
        // ClearBackgroundImage_Click, the same "takes effect on Save, not immediately"
        // pattern _pendingWindowWidth/_pendingWindowHeight already use below.
        private string? _pendingBackgroundImagePath;

        // The in-progress Custom keybind, captured live by CustomKeybindBox_PreviewKeyDown.
        // Seeded from the incoming config so an already-configured combo still shows
        // correctly even if the user never touches the capture field this time round.
        // _customVirtualKey == 0 means "nothing captured yet".
        private HotkeyModifiers _customModifiers;
        private int _customVirtualKey;

        // True while CustomKeybindBox has focus and is actively listening for the next
        // key combo — see CustomKeybindBox_GotFocus/_PreviewKeyDown/_LostFocus.
        private bool _capturingKeybind;

        // The dock's own persisted window size — normally untouched by this dialog
        // (it's set by dragging the dock's own resize grips), except when
        // ResetDockSize_Click shrinks it to MinDockWidth/MinDockHeight. Seeded from
        // the incoming config so Save keeps whatever size the dock already has if
        // Reset was never clicked.
        private double _pendingWindowWidth;
        private double _pendingWindowHeight;

        // Must match MainWindow's own private MinWidth/MinHeight resize-drag clamp
        // constants — duplicated here (rather than made public on MainWindow) since
        // Settings doesn't otherwise need a reference to the live dock window at
        // all, and this is the only place outside MainWindow that needs the value.
        private const double MinDockWidth = 380;
        private const double MinDockHeight = 320;

        // How much vertical space, beyond SettingsScrollViewer itself, this window
        // needs on top of it: its own title bar and borders (this Window, unlike
        // MainWindow, uses the normal WindowStyle rather than going chromeless),
        // the Margin="20" content padding top and bottom, the footer row
        // (Save/Cancel), and some slack for the validation message on the rare
        // occasion it's showing too. Deliberately generous — better to leave a
        // short screen with a little unused space at the bottom than to have this
        // window's own chrome push Save/Cancel below the visible screen after all,
        // which is the exact bug SettingsScrollViewerMaxHeight below exists to fix.
        private const double ChromeAndFooterAllowance = 220;

        // Never shrink the scrollable settings list below this, however short the
        // actual screen is — at that point every screen scrolls internally, and a
        // dialog with (almost) nothing above the fold reads as broken rather than
        // "just needs scrolling".
        private const double MinScrollViewerHeight = 300;

        /// <summary>The updated config, set only when the user clicks Save (DialogResult == true).</summary>
        public AppConfig? Result { get; private set; }

        public SettingsWindow(AppConfig current, ConfigService configService)
        {
            InitializeComponent();
            Services.ThemeService.ApplyTitleBarTheme(this);
            _original = current;
            _configService = configService;
            Closing += SettingsWindow_Closing;
            // Taken once everything is filled in, so "unchanged" means exactly what
            // was shown when Settings opened.
            Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() => _savedSnapshot = Snapshot()),
                System.Windows.Threading.DispatcherPriority.ContextIdle);

            // From the csproj's <Version> — the one place to bump it.
            // Shows a third number only when there is one (0.9, 0.9.1).
            VersionText.Text = $"StartDock {Updater.Display(Updater.CurrentVersion)}";

            // Caps the scrollable settings list to whatever actually fits the real
            // screen this dialog is opening on, so Save/Cancel below it (outside
            // the ScrollViewer — see SettingsWindow.xaml) can never end up pushed
            // past the bottom of a short display with no way to reach them. Called
            // before Show/ShowDialog, so this window has no hwnd of its own yet;
            // MonitorHelper falls back to the monitor under the mouse cursor in
            // that case, which works out well here specifically, since the user
            // just clicked the gear icon on the dock to get here — the cursor is
            // right where it needs to be.
            var workArea = MonitorHelper.GetWorkArea(this);
            SettingsScrollViewer.MaxHeight = Math.Max(MinScrollViewerHeight, workArea.Height - ChromeAndFooterAllowance);

            HotkeyWinRadio.IsChecked = current.Hotkey == HotkeyMode.WindowsKey;
            HotkeyShiftWinRadio.IsChecked = current.Hotkey == HotkeyMode.ShiftWindowsKey;
            HotkeyCustomRadio.IsChecked = current.Hotkey == HotkeyMode.Custom;

            _customModifiers = current.CustomHotkeyModifiers;
            _customVirtualKey = current.CustomHotkeyVirtualKey;
            CustomKeybindBox.Text = FormatCombo(_customModifiers, _customVirtualKey);

            ReplaceStartButtonCheck.IsChecked = current.ReplaceStartButton;
            ShowNativeStartMenuButtonCheck.IsChecked = current.ShowNativeStartMenuButton;
            ShowTaskbarOverFullscreenCheck.IsChecked = current.ShowTaskbarOverFullscreen;
            AutoStartCheck.IsChecked = current.AutoStart;
            StartAsAdminCheck.IsChecked = current.StartAsAdmin;
            AutoUpdateCheck.IsChecked = current.AutoUpdate;
            ExplorerPinMenuCheck.IsChecked = current.ExplorerPinMenu;
            Updater.StatusChanged += UpdateUpdaterStatus;
            Closed += (_, _) => Updater.StatusChanged -= UpdateUpdaterStatus;
            UpdateUpdaterStatus();
            UpdateBackupStatus();
            _hidden = new System.Collections.Generic.List<HiddenSearchItem>(current.HiddenFromSearch);
            UpdateHiddenSearchText();
            AdminStatusText.Text = App.IsElevated ? "Running as Admin" : "Not running as Admin";
            RestartAsAdminButton.IsEnabled = !App.IsElevated;
            ThemeCombo.SelectedIndex = current.Theme switch
            {
                ThemeMode.Light => 1,
                ThemeMode.Dark => 2,
                _ => 0,
            };

            // PositionCombo's display order no longer matches DockPosition's own
            // declaration order — look up where this position's item actually sits
            // (see PositionComboOrder's own doc comment). Falls back to index 0
            // (Top left) in the same defensive spirit as the ThemeCombo switch
            // above, if that ever somehow doesn't find a match.
            int positionIndex = Array.IndexOf(PositionComboOrder, current.Position);
            PositionCombo.SelectedIndex = positionIndex >= 0 ? positionIndex : 0;

            MenuBarPositionCombo.SelectedIndex = (int)current.GetMenuBarPlacement(); // items ordered to match MenuBarPlacement
            FlipMenuBarOrderCheck.IsChecked = current.FlipMenuBarOrder;

            // Same trick — RowAlignmentCombo's items are ordered Left, Center,
            // Right to match RowAlignment's own declaration order.
            RowAlignmentCombo.SelectedIndex = (int)current.IconAlignment;
            LayoutDirectionCombo.SelectedIndex = current.LayoutDirection == LayoutDirection.Columns ? 1 : 0;
            SnapDockWidthCheck.IsChecked = current.SnapDockWidth;
            ColumnAlignmentCombo.SelectedIndex = (int)current.ColumnAlignment; // items ordered to match ColumnAlignment
            UpdateAlignmentRows();

            OpacitySlider.Value = current.BackgroundOpacity;

            _pendingBackgroundImagePath = current.BackgroundImagePath;
            UpdateBackgroundImageStatusText();
            FitBackgroundImageCheck.IsChecked = current.FitBackgroundImage;

            FrostedBackgroundCheck.IsChecked = current.FrostedBackground;
            FrostTintCombo.SelectedIndex = (int)current.FrostTint; // items ordered to match FrostTintMode
            HideBorderCheck.IsChecked = current.HideBorder;
            BoldBorderCheck.IsChecked = current.BoldBorder;
            BorderColorCombo.SelectedIndex = (int)current.BorderColor;
            // Reflects the just-seeded HideBorderCheck state in BoldBorderCheck's
            // IsEnabled right away — see UpdateBorderCheckboxEnabled.
            UpdateBorderCheckboxEnabled();

            TextColorCombo.SelectedIndex = current.TextColor switch
            {
                TextColorMode.Black => 1,
                TextColorMode.White => 2,
                _ => 0,
            };

            HideSearchBarCheck.IsChecked = current.HideSearchBar;
            ShowNewAppsCheck.IsChecked = current.ShowNewApps;
            ShowRecentAppsCheck.IsChecked = current.ShowRecentApps;
            ShowRecentFilesCheck.IsChecked = current.ShowRecentFiles;
            // Count combos' items are 2..12.
            NewAppsCountCombo.SelectedIndex = Math.Clamp(current.NewAppsCount, 2, 12) - 2;
            RecentAppsCountCombo.SelectedIndex = Math.Clamp(current.RecentAppsCount, 2, 12) - 2;
            RecentFilesCountCombo.SelectedIndex = Math.Clamp(current.RecentFilesCount, 2, 12) - 2;
            RecentSectionsCombo.SelectedIndex = current.CombineRecentSections ? 1 : 0;
            SearchFilesCheck.IsChecked = current.SearchFiles;
            SearchCalculatorCheck.IsChecked = current.SearchCalculator;
            SearchWebCheck.IsChecked = current.SearchWeb;
            SearchRunCheck.IsChecked = current.SearchRun;
            ShowRunningIndicatorCheck.IsChecked = current.ShowRunningIndicator;
            WebSearchEngineCombo.SelectedIndex = (int)current.WebSearchEngine; // items ordered to match WebSearchEngine
            HideCategoryBannersCheck.IsChecked = current.HideCategoryBanners;
            HideIconNamesCheck.IsChecked = current.HideIconNames;

            HideMenuBarCheck.IsChecked = current.HideMenuBar;
            ShowTaskManagerButtonCheck.IsChecked = current.ShowTaskManagerButton;
            ShowVolumeMixerButtonCheck.IsChecked = current.ShowVolumeMixerButton;
            ShowFileExplorerButtonCheck.IsChecked = current.ShowFileExplorerButton;
            ShowCalculatorButtonCheck.IsChecked = current.ShowCalculatorButton;
            ShowSettingsButtonCheck.IsChecked = current.ShowSettingsButton;
            ShowPowerButtonCheck.IsChecked = current.ShowPowerButton;
            ShowAddButtonCheck.IsChecked = current.ShowAddButton;
            ShowUserButtonCheck.IsChecked = current.ShowUserButton;
            HideMenuBarDividerCheck.IsChecked = !current.ShowMenuBarDivider;
            // Reflects the just-seeded HideMenuBarCheck state in the five checkboxes'
            // IsEnabled right away, rather than waiting for the user to touch
            // HideMenuBarCheck themselves first — see UpdateMenuBarButtonCheckboxesEnabled.
            UpdateMenuBarButtonCheckboxesEnabled();

            IconSizeSlider.Value = current.IconSize;
            RowSpacingSlider.Value = Math.Clamp(current.RowCategorySpacing, 0, 48);
            ColumnSpacingSlider.Value = Math.Clamp(current.ColumnCategorySpacing, 0, 48);
            // The ValueChanged handler doesn't fire for a value equal to the slider's
            // starting 0, so set both labels directly too.
            RowSpacingValueText.Text = $"{(int)RowSpacingSlider.Value} px";
            ColumnSpacingValueText.Text = $"{(int)ColumnSpacingSlider.Value} px";
            CategoryFontSizeSlider.Value = current.CategoryFontSize;
            IconNameFontSizeSlider.Value = current.IconNameFontSize;
            BoldCategoryNamesCheck.IsChecked = current.BoldCategoryNames;
            BoldIconNamesCheck.IsChecked = current.BoldIconNames;

            ShowIconBackgroundCheck.IsChecked = current.ShowIconBackground;
            IconBackgroundOpacitySlider.Value = Math.Clamp(current.IconBackgroundOpacity, 10, 100);
            IconBackgroundColorCombo.SelectedIndex = current.IconBackgroundColor switch
            {
                IconBackgroundColorMode.Black => 1,
                IconBackgroundColorMode.White => 2,
                _ => 0,
            };

            _pendingWindowWidth = current.WindowWidth;
            _pendingWindowHeight = current.WindowHeight;
        }

        /// <summary>HideMenuBarCheck's Checked/Unchecked handler — keeps the five
        /// individual button checkboxes visually reflecting that they have nothing to
        /// show while the whole bar is collapsed, purely cosmetic (grayed out, not
        /// unchecked — see UpdateMenuBarButtonCheckboxesEnabled for why their checked
        /// state is never touched here).</summary>
        private void HideMenuBarCheck_CheckedChanged(object sender, RoutedEventArgs e) => UpdateMenuBarButtonCheckboxesEnabled();

        /// <summary>Grays out (IsEnabled=false) the five per-button menu-bar
        /// checkboxes while HideMenuBarCheck is on, rather than unchecking them —
        /// AppConfig.HideMenuBar is a master switch layered on top of the individual
        /// ShowTaskManagerButton/etc. flags (see that property's own doc comment),
        /// not a replacement for them, so whatever was checked/unchecked here stays
        /// exactly as the user left it and simply takes effect again the moment
        /// HideMenuBarCheck goes back off. Called once from the constructor (to match
        /// the just-seeded initial state) and again every time HideMenuBarCheck's own
        /// checked state changes.</summary>
        private void UpdateMenuBarButtonCheckboxesEnabled()
        {
            bool enabled = HideMenuBarCheck.IsChecked != true;
            ShowAddButtonCheck.IsEnabled = enabled;
            ShowUserButtonCheck.IsEnabled = enabled;
            HideMenuBarDividerCheck.IsEnabled = enabled;
            ShowNativeStartMenuButtonCheck.IsEnabled = enabled;
            ShowTaskManagerButtonCheck.IsEnabled = enabled;
            ShowVolumeMixerButtonCheck.IsEnabled = enabled;
            ShowFileExplorerButtonCheck.IsEnabled = enabled;
            ShowCalculatorButtonCheck.IsEnabled = enabled;
            ShowSettingsButtonCheck.IsEnabled = enabled;
            ShowPowerButtonCheck.IsEnabled = enabled;
            MenuBarPositionCombo.IsEnabled = enabled;
            FlipMenuBarOrderCheck.IsEnabled = enabled;
        }

        private void LayoutDirectionCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateAlignmentRows();

        /// <summary>Shows Row alignment for the Rows layout and Column alignment for
        /// Columns — only the one that applies. Both are saved either way, so
        /// switching back and forth keeps each choice.</summary>
        private void UpdateAlignmentRows()
        {
            // Also raised while the window is still being built, before these exist.
            if (RowAlignmentRow == null || ColumnAlignmentRow == null || LayoutDirectionCombo == null)
                return;

            bool columns = LayoutDirectionCombo.SelectedIndex == 1;
            RowAlignmentRow.Visibility = columns ? Visibility.Collapsed : Visibility.Visible;
            ColumnAlignmentRow.Visibility = columns ? Visibility.Visible : Visibility.Collapsed;
            if (SnapDockWidthCheck != null)
                SnapDockWidthCheck.IsEnabled = !columns; // the Columns layout never snaps
            if (RowSpacingRow != null && ColumnSpacingRow != null)
            {
                RowSpacingRow.Visibility = columns ? Visibility.Collapsed : Visibility.Visible;
                ColumnSpacingRow.Visibility = columns ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>HideBorderCheck's Checked/Unchecked handler — keeps BoldBorderCheck
        /// visually reflecting that it has nothing left to bolden while the border is
        /// hidden entirely (grayed out, not unchecked — see UpdateBorderCheckboxEnabled
        /// for why its checked state is never touched here).</summary>
        private void HideBorderCheck_CheckedChanged(object sender, RoutedEventArgs e) => UpdateBorderCheckboxEnabled();

        /// <summary>Grays out (IsEnabled=false) BoldBorderCheck while HideBorderCheck is
        /// on, rather than unchecking it — AppConfig.HideBorder takes precedence over
        /// BoldBorder (see ApplyBorderStyle in MainWindow.xaml.cs) without replacing
        /// it, so whatever Bold border was left as stays exactly as the user left it
        /// and simply takes effect again the moment HideBorderCheck goes back off.
        /// Called once from the constructor (to match the just-seeded initial state)
        /// and again every time HideBorderCheck's own checked state changes.</summary>
        private void UpdateBorderCheckboxEnabled()
        {
            BoldBorderCheck.IsEnabled = HideBorderCheck.IsChecked != true;
            if (BorderColorCombo != null)
                BorderColorCombo.IsEnabled = HideBorderCheck.IsChecked != true;
        }

        /// <summary>Shrinks the dock to its smallest allowed size (see
        /// MinDockWidth/MinDockHeight) on the next Save — for anyone who can't just
        /// drag the dock's own top/right resize grips to do it by hand (working
        /// remotely from a laptop with a shorter screen, say). Takes effect on
        /// Save/Cancel like every other control here, not immediately, so it's easy
        /// to back out of with Cancel if clicked by mistake.</summary>
        private void ResetDockSize_Click(object sender, RoutedEventArgs e)
        {
            _pendingWindowWidth = MinDockWidth;
            _pendingWindowHeight = MinDockHeight;
            ResetDockSizeStatusText.Text = $"Will shrink to {MinDockWidth:0} × {MinDockHeight:0} when you save.";
            ResetDockSizeStatusText.Visibility = Visibility.Visible;
        }

        /// <summary>Convenience: picking the Custom radio by hand (rather than landing
        /// on it via a successful capture — see CustomKeybindBox_PreviewKeyDown, which
        /// also checks this radio) jumps focus straight into the capture field so
        /// there's no separate click needed to start recording a combo.</summary>
        private void HotkeyCustomRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (IsLoaded)
                CustomKeybindBox.Focus();
        }

        // Keyboard-focus events, not GotFocus/LostFocus: after a capture,
        // Keyboard.ClearFocus() drops keyboard focus but WPF keeps the box as this
        // window's *logical* focus, so clicking it again only moved keyboard focus
        // back — GotFocus never re-fired, and a second capture couldn't start
        // without toggling the Custom radio to force a fresh focus change.
        private void CustomKeybindBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            _capturingKeybind = true;
            CustomKeybindBox.Text = "Press a key combination…";
        }

        private void CustomKeybindBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!_capturingKeybind)
                return; // a completed capture already cleared this and set the final text itself

            _capturingKeybind = false;
            CustomKeybindBox.Text = FormatCombo(_customModifiers, _customVirtualKey);
        }

        /// <summary>Live-captures the next key combination typed while the field has
        /// focus. A lone modifier press (Ctrl/Alt/Shift/Win by itself) is ignored —
        /// capture only completes once a non-modifier key arrives. A modifier is no
        /// longer required at all: a single bare key (whatever's currently held —
        /// none, one, or several modifiers) is captured as-is, so a spare/unused key
        /// can be the whole activation combo on its own. Escape cancels and reverts
        /// to whatever combo was already saved.</summary>
        private void CustomKeybindBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_capturingKeybind)
                return;

            e.Handled = true;

            // Alt-involving presses arrive as Key.System with the real key in
            // e.SystemKey rather than e.Key — unwrap that the same way everywhere
            // else in the combo needs the "actual" key.
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Escape)
            {
                _capturingKeybind = false;
                CustomKeybindBox.Text = FormatCombo(_customModifiers, _customVirtualKey);
                Keyboard.ClearFocus();
                return;
            }

            if (IsModifierKey(key))
                return; // still waiting for the non-modifier key to complete the combo

            // WPF's ModifierKeys already shares StartDock's own HotkeyModifiers flag
            // values (Alt=1, Control=2, Shift=4, Windows=8 on both sides — see
            // HotkeyModifiers' doc comment), so this is a plain cast, no bit-juggling.
            // ModifierKeys.None casts to HotkeyModifiers.None just as cleanly — a
            // bare key with nothing held is now a perfectly valid combo.
            _customModifiers = (HotkeyModifiers)(int)Keyboard.Modifiers;
            _customVirtualKey = KeyInterop.VirtualKeyFromKey(key);

            _capturingKeybind = false;
            CustomKeybindBox.Text = FormatCombo(_customModifiers, _customVirtualKey);
            HotkeyCustomRadio.IsChecked = true;
            Keyboard.ClearFocus();
        }

        private static bool IsModifierKey(Key key) => key is
            Key.LeftCtrl or Key.RightCtrl or
            Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or
            Key.LWin or Key.RWin;

        private static string FormatCombo(HotkeyModifiers modifiers, int virtualKey)
        {
            if (virtualKey == 0)
                return "Click to set a shortcut";

            var sb = new StringBuilder();
            if ((modifiers & HotkeyModifiers.Control) != 0) sb.Append("Ctrl + ");
            if ((modifiers & HotkeyModifiers.Alt) != 0) sb.Append("Alt + ");
            if ((modifiers & HotkeyModifiers.Shift) != 0) sb.Append("Shift + ");
            if ((modifiers & HotkeyModifiers.Windows) != 0) sb.Append("Win + ");

            // Best-effort: not every VK_* code round-trips through KeyInterop (a few
            // OEM/media keys don't), but every key actually reachable from
            // CustomKeybindBox_PreviewKeyDown came from KeyInterop.VirtualKeyFromKey
            // in the first place, so this always succeeds for a combo captured here.
            try
            {
                sb.Append(KeyInterop.KeyFromVirtualKey(virtualKey));
            }
            catch
            {
                sb.Append('?');
            }

            return sb.ToString();
        }

        private void IconBackgroundOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (IconBackgroundOpacityValueText != null)
                IconBackgroundOpacityValueText.Text = $"{(int)Math.Round(e.NewValue)}%";
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (OpacityValueText != null)
                OpacityValueText.Text = $"{(int)Math.Round(e.NewValue)}%";
        }

        private void UpdateBackgroundImageStatusText()
        {
            bool hasImage = !string.IsNullOrEmpty(_pendingBackgroundImagePath);
            BackgroundImageStatusText.Text = hasImage ? Path.GetFileName(_pendingBackgroundImagePath) : "None";
            // The row is narrow enough that long file names get trimmed — the
            // full name is still one hover away.
            BackgroundImageStatusText.ToolTip = hasImage ? Path.GetFileName(_pendingBackgroundImagePath) : null;
            ClearBackgroundImageButton.IsEnabled = !string.IsNullOrEmpty(_pendingBackgroundImagePath);
        }

        /// <summary>Copies the picked file into ConfigService's own Background folder
        /// right away (see ConfigService.ImportBackgroundImage) rather than waiting for
        /// Save — MainWindow.xaml.cs's ApplyBackgroundImage only ever reads
        /// AppConfig.BackgroundImagePath from disk, so the copy has to exist before
        /// Save can meaningfully point at it. Clicking Cancel afterward just leaves
        /// that copy sitting unused in the Background folder rather than referenced
        /// by anything — harmless, and it'll simply be replaced or removed the next
        /// time this dialog's Choose/Remove is used.</summary>
        private void ChooseBackgroundImage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose a background image",
                Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                _pendingBackgroundImagePath = _configService.ImportBackgroundImage(dialog.FileName);
                UpdateBackgroundImageStatusText();
            }
            catch (Exception ex)
            {
                ValidationErrorText.Text = $"Couldn't use that image: {ex.Message}";
                ValidationErrorText.Visibility = Visibility.Visible;
            }
        }

        private void ClearBackgroundImage_Click(object sender, RoutedEventArgs e)
        {
            _configService.ClearBackgroundImage();
            _pendingBackgroundImagePath = null;
            UpdateBackgroundImageStatusText();
        }

        private void IconSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (IconSizeValueText != null)
                IconSizeValueText.Text = $"{(int)Math.Round(e.NewValue)} px";
        }

        /// <summary>Shows the updater's latest status, and turns the button into
        /// "Update now" once a new version is downloaded.</summary>
        private void UpdateUpdaterStatus()
        {
            UpdateStatusText.Text = Updater.Status;
            UpdateStatusText.ToolTip = string.IsNullOrEmpty(Updater.StatusDetail) ? null : Updater.StatusDetail;
            CheckUpdatesButton.Content = Updater.ReadyInstallerPath != null ? "Update now" : "Check for updates";
            CheckUpdatesButton.IsEnabled = !Updater.IsBusy;

            if (Updater.AvailableVersion is { } available)
            {
                UpdateAvailableText.Text = $"Update {Updater.Display(available)} available";
                UpdateAvailableText.ToolTip = Updater.ReadyInstallerPath != null
                    ? "Click to install it now (StartDock restarts)"
                    : "Downloading…";
                UpdateAvailableText.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateAvailableText.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateAvailable_Click(object sender, MouseButtonEventArgs e)
        {
            if (Updater.ReadyInstallerPath != null)
                ((App)Application.Current).InstallUpdate();
        }

        private void WhatsNew_Click(object sender, MouseButtonEventArgs e) =>
            WhatsNewWindow.ShowOrActivate(this);

        private void Tips_Click(object sender, MouseButtonEventArgs e) =>
            WhatsNewWindow.ShowTips(this);

        private void ReportProblem_Click(object sender, MouseButtonEventArgs e) => Changelog.ReportProblem();

        private void GitHub_Click(object sender, MouseButtonEventArgs e) => Changelog.OpenGitHub();

        private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (Updater.ReadyInstallerPath == null)
            {
                CheckUpdatesButton.IsEnabled = false;
                await Updater.CheckAndDownloadAsync(quiet: false);
                UpdateUpdaterStatus();
                return;
            }

            // Installing closes StartDock (and this window) and starts the new
            // version. Unsaved changes here are lost, like Restart as Admin.
            ((App)Application.Current).InstallUpdate();
        }

        private void CategorySpacingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var text = ReferenceEquals(sender, ColumnSpacingSlider) ? ColumnSpacingValueText : RowSpacingValueText;
            if (text != null)
                text.Text = $"{(int)Math.Round(e.NewValue)} px";
        }

        private void CategoryFontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (CategoryFontSizeValueText != null)
                CategoryFontSizeValueText.Text = $"{(int)Math.Round(e.NewValue)} px";
        }

        private void IconNameFontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (IconNameFontSizeValueText != null)
                IconNameFontSizeValueText.Text = $"{(int)Math.Round(e.NewValue)} px";
        }

        /// <summary>Raised by Apply: the dock should take on this config right away,
        /// with this window staying open. Save goes through DialogResult/Result
        /// instead, as before. See MainWindow.OpenSettings.</summary>
        public event Action<AppConfig>? Applied;

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!TryBuildResult())
                return;

            DialogResult = true; // closes the window
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (TryBuildResult() && Result != null)
            {
                Applied?.Invoke(Result);
                _savedSnapshot = Snapshot(); // applied — nothing unsaved now
            }
        }

        /// <summary>Re-reads the list of installed apps from Windows, for an app that
        /// isn't showing up in search or the Add picker yet — usually one installed
        /// since StartDock started, since that list is otherwise only read once per
        /// session (see InstalledAppsCache). Takes effect immediately; nothing to Save.</summary>
        private async void RefreshApps_Click(object sender, RoutedEventArgs e)
        {
            RefreshAppsButton.IsEnabled = false;
            RefreshAppsButton.Content = "Refreshing…";
            FooterStatusText.Text = string.Empty;
            try
            {
                int added = await InstalledAppsCache.RefreshAsync();
                FooterStatusText.Text = added switch
                {
                    0 => "App list is up to date.",
                    1 => "Found 1 new app.",
                    _ => $"Found {added} new apps.",
                };
            }
            catch (Exception ex)
            {
                FooterStatusText.Text = $"Couldn't refresh apps: {ex.Message}";
            }
            finally
            {
                RefreshAppsButton.Content = "Refresh Apps";
                RefreshAppsButton.IsEnabled = true;
            }
        }

        /// <summary>Validates and builds Result from the current controls. Shared by
        /// Save and Apply; returns false (with the reason shown) if something needs
        /// fixing first.</summary>
        private bool TryBuildResult()
        {
            bool customSelected = HotkeyCustomRadio.IsChecked == true;
            if (customSelected && _customVirtualKey == 0)
            {
                ValidationErrorText.Text = "Set a custom keybind (click the field above and press a key combination) before saving, or choose a different activation shortcut.";
                ValidationErrorText.Visibility = Visibility.Visible;
                return false;
            }

            ValidationErrorText.Visibility = Visibility.Collapsed;

            Result = BuildFromControls();

            return true;
        }

        /// <summary>The settings as the controls currently show them (no checks).</summary>
        private AppConfig BuildFromControls()
        {
            bool customSelected = HotkeyCustomRadio.IsChecked == true;
            return new AppConfig
            {
                Categories = _original.Categories, // category/icon list is managed from the dock itself, not this dialog
                HiddenFromSearch = new System.Collections.Generic.List<HiddenSearchItem>(_hidden),
                Hotkey = customSelected
                    ? HotkeyMode.Custom
                    : HotkeyShiftWinRadio.IsChecked == true ? HotkeyMode.ShiftWindowsKey : HotkeyMode.WindowsKey,
                CustomHotkeyModifiers = _customModifiers,
                CustomHotkeyVirtualKey = _customVirtualKey,
                ReplaceStartButton = ReplaceStartButtonCheck.IsChecked == true,
                ShowNativeStartMenuButton = ShowNativeStartMenuButtonCheck.IsChecked == true,
                ShowTaskbarOverFullscreen = ShowTaskbarOverFullscreenCheck.IsChecked == true,
                // See PositionComboOrder's doc comment — SelectedIndex is a position in
                // the *display* list, not the DockPosition's own int value anymore.
                Position = PositionComboOrder[PositionCombo.SelectedIndex],
                MenuBarPosition = (MenuBarPlacement)Math.Max(0, MenuBarPositionCombo.SelectedIndex),
                // Kept in step for an older StartDock reading this config (see AppConfig.MenuBarPosition).
                MenuBarAtTop = MenuBarPositionCombo.SelectedIndex == (int)MenuBarPlacement.Top,
                FlipMenuBarOrder = FlipMenuBarOrderCheck.IsChecked == true,
                IconAlignment = (RowAlignment)RowAlignmentCombo.SelectedIndex,
                LayoutDirection = LayoutDirectionCombo.SelectedIndex == 1 ? LayoutDirection.Columns : LayoutDirection.Rows,
                SnapDockWidth = SnapDockWidthCheck.IsChecked == true,
                ColumnAlignment = (ColumnAlignment)Math.Max(0, ColumnAlignmentCombo.SelectedIndex),
                BackgroundOpacity = OpacitySlider.Value,
                BackgroundImagePath = _pendingBackgroundImagePath,
                FitBackgroundImage = FitBackgroundImageCheck.IsChecked == true,
                ShowIconBackground = ShowIconBackgroundCheck.IsChecked == true,
                IconBackgroundOpacity = IconBackgroundOpacitySlider.Value,
                IconBackgroundColor = IconBackgroundColorCombo.SelectedIndex switch
                {
                    1 => IconBackgroundColorMode.Black,
                    2 => IconBackgroundColorMode.White,
                    _ => IconBackgroundColorMode.Theme,
                },
                FrostedBackground = FrostedBackgroundCheck.IsChecked == true,
                FrostTint = (FrostTintMode)Math.Max(0, FrostTintCombo.SelectedIndex),
                HideBorder = HideBorderCheck.IsChecked == true,
                BorderColor = (BorderColorMode)Math.Max(0, BorderColorCombo.SelectedIndex),
                BoldBorder = BoldBorderCheck.IsChecked == true,
                HideSearchBar = HideSearchBarCheck.IsChecked == true,
                ShowNewApps = ShowNewAppsCheck.IsChecked == true,
                ShowRecentApps = ShowRecentAppsCheck.IsChecked == true,
                ShowRecentFiles = ShowRecentFilesCheck.IsChecked == true,
                NewAppsCount = Math.Max(0, NewAppsCountCombo.SelectedIndex) + 2,
                RecentAppsCount = Math.Max(0, RecentAppsCountCombo.SelectedIndex) + 2,
                RecentFilesCount = Math.Max(0, RecentFilesCountCombo.SelectedIndex) + 2,
                CombineRecentSections = RecentSectionsCombo.SelectedIndex == 1,
                SearchFiles = SearchFilesCheck.IsChecked == true,
                SearchCalculator = SearchCalculatorCheck.IsChecked == true,
                SearchWeb = SearchWebCheck.IsChecked == true,
                SearchRun = SearchRunCheck.IsChecked == true,
                ExplorerPinMenu = ExplorerPinMenuCheck.IsChecked == true,
                ShowRunningIndicator = ShowRunningIndicatorCheck.IsChecked == true,
                WebSearchEngine = (WebSearchEngine)Math.Max(0, WebSearchEngineCombo.SelectedIndex),
                HideCategoryBanners = HideCategoryBannersCheck.IsChecked == true,
                HideIconNames = HideIconNamesCheck.IsChecked == true,
                HideMenuBar = HideMenuBarCheck.IsChecked == true,
                ShowTaskManagerButton = ShowTaskManagerButtonCheck.IsChecked == true,
                ShowVolumeMixerButton = ShowVolumeMixerButtonCheck.IsChecked == true,
                ShowFileExplorerButton = ShowFileExplorerButtonCheck.IsChecked == true,
                ShowCalculatorButton = ShowCalculatorButtonCheck.IsChecked == true,
                ShowSettingsButton = ShowSettingsButtonCheck.IsChecked == true,
                ShowPowerButton = ShowPowerButtonCheck.IsChecked == true,
                ShowAddButton = ShowAddButtonCheck.IsChecked == true,
                ShowUserButton = ShowUserButtonCheck.IsChecked == true,
                ShowMenuBarDivider = HideMenuBarDividerCheck.IsChecked != true,
                IconSize = IconSizeSlider.Value,
                RowCategorySpacing = RowSpacingSlider.Value,
                ColumnCategorySpacing = ColumnSpacingSlider.Value,
                CategoryFontSize = CategoryFontSizeSlider.Value,
                IconNameFontSize = IconNameFontSizeSlider.Value,
                BoldCategoryNames = BoldCategoryNamesCheck.IsChecked == true,
                BoldIconNames = BoldIconNamesCheck.IsChecked == true,
                WindowWidth = _pendingWindowWidth,
                WindowHeight = _pendingWindowHeight,
                AutoStart = AutoStartCheck.IsChecked == true,
                StartAsAdmin = StartAsAdminCheck.IsChecked == true,
                AutoUpdate = AutoUpdateCheck.IsChecked == true,
                Theme = ThemeCombo.SelectedIndex switch
                {
                    1 => ThemeMode.Light,
                    2 => ThemeMode.Dark,
                    _ => ThemeMode.FollowWindows,
                },
                TextColor = TextColorCombo.SelectedIndex switch
                {
                    1 => TextColorMode.Black,
                    2 => TextColorMode.White,
                    _ => TextColorMode.Default,
                },
            };
        }

        // ---- Unsaved changes: closing without Save & Close asks first.

        // The settings as last saved (opened, or Applied), to compare against.
        private string _savedSnapshot = string.Empty;
        private bool _closeWithoutAsking;

        private string Snapshot()
        {
            try
            {
                var config = BuildFromControls();
                config.Categories = new System.Collections.Generic.List<Category>(); // not edited here
                return System.Text.Json.JsonSerializer.Serialize(config);
            }
            catch
            {
                return string.Empty;
            }
        }

        private bool HasUnsavedChanges => _savedSnapshot.Length > 0 && Snapshot() != _savedSnapshot;

        private void SettingsWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_closeWithoutAsking || DialogResult == true || App.IsExiting || !HasUnsavedChanges)
                return;

            switch (UnsavedChangesDialog.Ask(this))
            {
                case UnsavedChangesDialog.Choice.Save:
                    if (!TryBuildResult())
                    {
                        e.Cancel = true; // something needs fixing first — it's shown
                        return;
                    }
                    // Can't set DialogResult while closing; hand the result over the
                    // same way Apply does.
                    if (Result != null)
                        Applied?.Invoke(Result);
                    break;
                case UnsavedChangesDialog.Choice.Discard:
                    break;
                default:
                    e.Cancel = true; // keep editing
                    break;
            }
        }


        /// <summary>Same relaunch the tray menu's "Restart as Admin" does (see
        /// App.RestartAsAdministrator). Left open on purpose while the UAC prompt
        /// is up: if it's declined, this window is still here exactly as it was;
        /// if it's accepted, the app's own Shutdown closes it.</summary>
        // ---- Hidden from search (AppConfig.HiddenFromSearch)

        // A working copy; saved with the rest of the settings.
        private System.Collections.Generic.List<HiddenSearchItem> _hidden = new();

        private void UpdateHiddenSearchText()
        {
            HiddenSearchText.Text = _hidden.Count switch
            {
                0 => "Nothing hidden from search",
                1 => "1 thing hidden from search",
                _ => $"{_hidden.Count} things hidden from search",
            };
            ManageHiddenButton.IsEnabled = _hidden.Count > 0;
        }

        private void ManageHidden_Click(object sender, RoutedEventArgs e)
        {
            new HiddenSearchDialog(_hidden) { Owner = this }.ShowDialog();
            UpdateHiddenSearchText();
        }

        // ---- Export / Import / Reset (see ConfigService)

        private void ExportSettings_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export StartDock settings",
                FileName = $"StartDock settings {DateTime.Now:yyyy-MM-dd}{ConfigService.ExportExtension}",
                Filter = $"StartDock settings (*{ConfigService.ExportExtension})|*{ConfigService.ExportExtension}",
                DefaultExt = ConfigService.ExportExtension,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dialog.ShowDialog(this) != true)
                return;
            try
            {
                _configService.ExportSettings(dialog.FileName);
                FooterStatusText.Text = HasUnsavedChanges
                    ? $"Exported to {Path.GetFileName(dialog.FileName)} (without the changes not saved yet)."
                    : $"Exported to {Path.GetFileName(dialog.FileName)}.";
                FooterStatusText.ToolTip = dialog.FileName;
            }
            catch (Exception ex)
            {
                ConfirmDialog.Tell(this, "Export", "Couldn't export your settings.", ex.Message);
            }
        }

        private void ImportSettings_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import StartDock settings",
                Filter = $"StartDock settings (*{ConfigService.ExportExtension})|*{ConfigService.ExportExtension}|All files (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                CheckFileExists = true,
            };
            if (dialog.ShowDialog(this) != true)
                return;

            if (!ConfirmDialog.Ask(this, "Import settings", $"Import \"{Path.GetFileName(dialog.FileName)}\"?",
                    "Your current settings and pinned apps are replaced. They're backed up first, so Restore a backup can bring them back. StartDock restarts.",
                    "Import"))
                return;

            try
            {
                _configService.ImportSettings(dialog.FileName);
            }
            catch (Exception ex)
            {
                ConfirmDialog.Tell(this, "Import", "Couldn't import those settings. Nothing was changed.", ex.Message);
                return;
            }

            _closeWithoutAsking = true;
            if (System.Windows.Application.Current is App app)
                app.Restart();
        }

        private void ResetSettings_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmDialog.Ask(this, "Reset", "Start over with a fresh StartDock?",
                    "Removes all your pinned apps and categories and puts every setting back to its default, like a new install. Your current setup is backed up first, so Restore a backup (or Import, if you exported it) can bring it back. StartDock restarts with the welcome screen.",
                    "Reset"))
                return;

            if (!_configService.ResetSettings())
            {
                ConfirmDialog.Tell(this, "Reset", "Couldn't reset StartDock. Nothing was changed.", string.Empty);
                return;
            }

            _closeWithoutAsking = true;
            if (System.Windows.Application.Current is App app)
                app.Restart();
        }

        // ---- Settings backups (see ConfigService)

        private void UpdateBackupStatus()
        {
            var backups = _configService.ListBackups();
            BackupStatusText.Text = backups.Count == 0
                ? "No backups yet"
                : $"Last backup: {RestoreBackupDialog.Describe(backups[0].Taken)}";
            RestoreBackupButton.IsEnabled = backups.Count > 0;
        }

        private void RestoreBackup_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new RestoreBackupDialog(_configService.ListBackups()) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Selected == null)
                return;

            if (!_configService.RestoreBackup(dialog.Selected))
            {
                MessageBox.Show(this, "That backup couldn't be restored. Your current settings weren't changed.",
                    "StartDock", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Everything (pinned apps, layout, hotkey…) is loaded fresh from the
            // restored file, the same way as when StartDock starts.
            _closeWithoutAsking = true;
            if (System.Windows.Application.Current is App app)
                app.Restart();
        }

        private void RestartAsAdmin_Click(object sender, RoutedEventArgs e)
        {
            if (System.Windows.Application.Current is App app)
                app.RestartAsAdministrator();
        }

        /// <summary>Cancel: closes; asks first if something was changed (see
        /// SettingsWindow_Closing).</summary>
        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}
