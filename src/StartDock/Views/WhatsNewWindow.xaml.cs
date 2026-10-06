using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StartDock.Services;

namespace StartDock.Views
{
    /// <summary>
    /// "What's new" (CHANGELOG.md) and "Tips" (Resources/Tips.md) — one window with
    /// two pages, since both are the same kind of list (see Services/Changelog).
    /// Only one is open at a time; asking for the other page switches it.
    /// </summary>
    public partial class WhatsNewWindow : Window
    {
        private static WhatsNewWindow? _open;

        private bool _showingTips;

        /// <summary>Shows What's new, or brings the already open window to the front.</summary>
        public static void ShowOrActivate() => Open(null, tips: false);

        /// <param name="owner">Opened from another window (Settings): keeps it in
        /// front of that one and centered on it.</param>
        public static void ShowOrActivate(Window? owner) => Open(owner, tips: false);

        /// <summary>Shows the Tips page.</summary>
        public static void ShowTips() => Open(null, tips: true);

        public static void ShowTips(Window? owner) => Open(owner, tips: true);

        private static void Open(Window? owner, bool tips)
        {
            if (_open != null)
            {
                _open.ShowPage(tips);
                if (_open.WindowState == WindowState.Minimized)
                    _open.WindowState = WindowState.Normal;
                _open.Activate();
                return;
            }
            _open = new WhatsNewWindow();
            _open.ShowPage(tips);
            if (owner != null && owner.IsVisible)
            {
                _open.Owner = owner;
                _open.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            _open.Closed += (_, _) => _open = null;
            _open.Show();
            _open.Activate();
        }

        private WhatsNewWindow()
        {
            InitializeComponent();
            ThemeService.ApplyTitleBarTheme(this);
        }

        private void ShowPage(bool tips)
        {
            _showingTips = tips;
            Title = tips ? "StartDock tips" : "What's new in StartDock";
            SwitchLink.Text = tips ? "What's new" : "Tips";
            NotesPanel.Children.Clear();
            NotesScroll.ScrollToTop();

            if (tips)
                Fill(Changelog.Tips, heading: e => e.Version, highlight: null);
            else
                Fill(Changelog.Entries, heading: e => "StartDock " + e.Version, highlight: Updater.Display(Updater.CurrentVersion));
        }

        private void Fill(IReadOnlyList<Changelog.Entry> entries, System.Func<Changelog.Entry, string> heading, string? highlight)
        {
            var secondary = (Brush)FindResource("SecondaryTextBrush");
            if (entries.Count == 0)
            {
                NotesPanel.Children.Add(new TextBlock { Text = "This couldn't be loaded.", Foreground = secondary });
                return;
            }

            bool first = true;
            foreach (var entry in entries)
            {
                var title = new TextBlock
                {
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, first ? 0 : 18, 0, 6),
                    Text = heading(entry),
                };
                if (highlight != null && entry.Version == highlight)
                {
                    title.Inlines.Add(new System.Windows.Documents.Run("   this version")
                    {
                        FontSize = 12,
                        FontWeight = FontWeights.Normal,
                        Foreground = (Brush)FindResource("AccentBrush"),
                    });
                }
                NotesPanel.Children.Add(title);
                first = false;

                foreach (string line in entry.Changes)
                {
                    var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    var bullet = new TextBlock { Text = "•", Foreground = secondary };
                    var text = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap };
                    Grid.SetColumn(text, 1);
                    row.Children.Add(bullet);
                    row.Children.Add(text);
                    NotesPanel.Children.Add(row);
                }
            }
        }

        private void SwitchLink_Click(object sender, MouseButtonEventArgs e) => ShowPage(!_showingTips);

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
