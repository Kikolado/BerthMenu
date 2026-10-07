using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StartDock.Services;

namespace StartDock.Views
{
    public partial class RestoreBackupDialog : Window
    {
        /// <summary>The backup to restore, set when Restore is clicked.</summary>
        public ConfigService.Backup? Selected { get; private set; }

        public sealed record Row(ConfigService.Backup Backup, string Title, string Details);

        public RestoreBackupDialog(IReadOnlyList<ConfigService.Backup> backups)
        {
            InitializeComponent();
            ThemeService.ApplyTitleBarTheme(this);

            BackupsList.ItemsSource = backups.Select(b => new Row(
                b,
                Describe(b.Taken),
                (b.Reason != null ? $"Saved {b.Reason} · " : string.Empty)
                    + Plural(b.Categories, "category", "categories") + ", " + Plural(b.Tiles, "app", "apps")))
                .ToList();
        }

        /// <summary>"Today, 9:41 PM", "Yesterday, 8:02 AM" or "Monday, Oct 5, 9:41 PM".</summary>
        public static string Describe(DateTime when)
        {
            string time = when.ToString("t");
            if (when.Date == DateTime.Today)
                return "Today, " + time;
            if (when.Date == DateTime.Today.AddDays(-1))
                return "Yesterday, " + time;
            return when.ToString("dddd, MMM d") + ", " + time;
        }

        private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

        private void BackupsList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            RestoreButton.IsEnabled = BackupsList.SelectedItem != null;

        private void BackupsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (BackupsList.SelectedItem != null)
                Restore_Click(sender, e);
        }

        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (BackupsList.SelectedItem is not Row row)
                return;
            Selected = row.Backup;
            DialogResult = true;
        }
    }
}
