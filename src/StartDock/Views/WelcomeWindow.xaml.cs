using System.Windows;
using StartDock.Models;

namespace StartDock.Views
{
    /// <summary>
    /// The first-run welcome (App.ShowWelcome): how to open StartDock, where it
    /// opens, its look, starting with your most used apps or an empty dock, and
    /// sign-in startup. "Get started" writes the choices into the config it was
    /// given; closing it any other way keeps the defaults.
    /// </summary>
    public partial class WelcomeWindow : Window
    {
        private readonly AppConfig _config;

        /// <summary>"The apps I use most" was chosen: pin them (MainWindow.ApplyWelcome).</summary>
        public bool PinMostUsed { get; private set; }

        // Same order as Settings' Dock Position list (SettingsWindow.PositionComboOrder).
        private static readonly DockPosition[] Positions =
        {
            DockPosition.TopLeft, DockPosition.TopCenter, DockPosition.TopRight,
            DockPosition.MiddleLeft, DockPosition.Center, DockPosition.MiddleRight,
            DockPosition.BottomLeft, DockPosition.BottomCenter, DockPosition.BottomRight,
        };

        public WelcomeWindow(AppConfig config)
        {
            InitializeComponent();
            Services.ThemeService.ApplyTitleBarTheme(this);
            _config = config;

            ShiftWindowsKeyRadio.IsChecked = config.Hotkey == HotkeyMode.ShiftWindowsKey;
            WindowsKeyRadio.IsChecked = config.Hotkey != HotkeyMode.ShiftWindowsKey;
            StartButtonCheck.IsChecked = config.ReplaceStartButton;
            int position = System.Array.IndexOf(Positions, config.Position);
            PositionCombo.SelectedIndex = position < 0 ? 7 : position;
            DirectionCombo.SelectedIndex = config.LayoutDirection == LayoutDirection.Columns ? 1 : 0;
            RowAlignmentCombo.SelectedIndex = (int)config.IconAlignment;
            ColumnAlignmentCombo.SelectedIndex = (int)config.ColumnAlignment;
            UpdateAlignment();
            ThemeCombo.SelectedIndex = (int)config.Theme;
            AutoStartCheck.IsChecked = config.AutoStart;
        }

        private void DirectionCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
            UpdateAlignment();

        /// <summary>Row alignment (Left/Center/Right) for Rows, Column alignment
        /// (Top/Center/Bottom) for Columns — the same pair Settings shows.</summary>
        private void UpdateAlignment()
        {
            if (DirectionCombo == null || RowAlignmentCombo == null || ColumnAlignmentCombo == null || AlignmentLabel == null)
                return; // still being built
            bool columns = DirectionCombo.SelectedIndex == 1;
            AlignmentLabel.Text = columns ? "Column alignment" : "Row alignment";
            RowAlignmentCombo.Visibility = columns ? Visibility.Collapsed : Visibility.Visible;
            ColumnAlignmentCombo.Visibility = columns ? Visibility.Visible : Visibility.Collapsed;
        }

        private void GetStarted_Click(object sender, RoutedEventArgs e)
        {
            _config.Hotkey = ShiftWindowsKeyRadio.IsChecked == true ? HotkeyMode.ShiftWindowsKey : HotkeyMode.WindowsKey;
            _config.ReplaceStartButton = StartButtonCheck.IsChecked == true;
            _config.Position = Positions[System.Math.Max(0, PositionCombo.SelectedIndex)];
            _config.Theme = (ThemeMode)System.Math.Max(0, ThemeCombo.SelectedIndex);
            _config.LayoutDirection = DirectionCombo.SelectedIndex == 1 ? LayoutDirection.Columns : LayoutDirection.Rows;
            _config.IconAlignment = (RowAlignment)System.Math.Max(0, RowAlignmentCombo.SelectedIndex);
            _config.ColumnAlignment = (ColumnAlignment)System.Math.Max(0, ColumnAlignmentCombo.SelectedIndex);
            _config.AutoStart = AutoStartCheck.IsChecked == true;
            PinMostUsed = MostUsedRadio.IsChecked == true;
            DialogResult = true;
        }
    }
}
