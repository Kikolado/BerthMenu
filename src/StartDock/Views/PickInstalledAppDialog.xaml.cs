using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using StartDock.Models;
using StartDock.Services;

namespace StartDock.Views
{
    public partial class PickInstalledAppDialog : Window
    {
        private readonly IconExtractor _iconExtractor;
        private readonly ObservableCollection<InstalledAppViewModel> _allApps = new();
        private readonly ICollectionView _view;

        /// <summary>Set when the user picks an app and clicks Add (DialogResult == true).</summary>
        public InstalledAppInfo? SelectedApp { get; private set; }

        /// <summary>The already-extracted icon for <see cref="SelectedApp"/>, if the
        /// background loader finished it in time — lets the caller skip re-extracting.</summary>
        public string? SelectedIconPath { get; private set; }

        /// <summary>True when the user clicked "Browse for a file instead" (DialogResult == false in that case too).</summary>
        public bool BrowseInstead { get; private set; }

        /// <summary>True when the user clicked "Add a folder" instead.</summary>
        public bool BrowseFolderInstead { get; private set; }

        /// <summary>True when the user clicked "Add a website" instead.</summary>
        public bool BrowseWebsiteInstead { get; private set; }

        public PickInstalledAppDialog(ConfigService configService)
        {
            InitializeComponent();
            Services.ThemeService.ApplyTitleBarTheme(this);

            _iconExtractor = new IconExtractor(configService);

            _view = CollectionViewSource.GetDefaultView(_allApps);
            AppsListBox.ItemsSource = _view;

            Loaded += async (_, _) => await LoadAppsAsync();
        }

        private async Task LoadAppsAsync()
        {
            // Session-cached — only the very first dialog opened after StartDock starts
            // actually pays the Get-StartApps/icon-extraction cost; every later open
            // reuses the same list and whatever icons were already resolved.
            var apps = await InstalledAppsCache.GetAppsAsync();

            LoadingText.Visibility = Visibility.Collapsed;

            if (apps.Count == 0)
            {
                NoResultsText.Text = "Couldn't list installed apps on this PC. Use \"Browse for a file\" below instead.";
                NoResultsText.Visibility = Visibility.Visible;
                return;
            }

            // Reuse the cache's own view-model instances (not copies) so icon updates
            // from EnsureIconsLoadedAsync below are visible immediately in this dialog.
            foreach (var vm in apps)
                _allApps.Add(vm);

            UpdateNoResultsVisibility();

            // Fire-and-forget: only extracts icons still missing (a no-op once a prior
            // dialog this session already finished loading them all).
            _ = InstalledAppsCache.EnsureIconsLoadedAsync(_iconExtractor);
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filter = SearchBox.Text.Trim();
            _view.Filter = string.IsNullOrEmpty(filter)
                ? null
                : o => o is InstalledAppViewModel app && app.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);

            UpdateNoResultsVisibility();
        }

        private void UpdateNoResultsVisibility()
        {
            if (_allApps.Count == 0)
                return; // the "couldn't list apps" message from LoadAppsAsync already covers this

            bool hasAnyVisible = false;
            foreach (var _ in _view)
            {
                hasAnyVisible = true;
                break;
            }

            NoResultsText.Text = "No matching apps found.";
            NoResultsText.Visibility = hasAnyVisible ? Visibility.Collapsed : Visibility.Visible;
        }

        private void AppsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AddButton.IsEnabled = AppsListBox.SelectedItem is InstalledAppViewModel;
        }

        private void AppsListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (AppsListBox.SelectedItem is InstalledAppViewModel)
                Add_Click(sender, new RoutedEventArgs());
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            if (AppsListBox.SelectedItem is not InstalledAppViewModel vm)
                return;

            SelectedApp = vm.Model;
            SelectedIconPath = vm.CachedIconPath;
            DialogResult = true;
            Close();
        }

        private void BrowseFolderInstead_Click(object sender, RoutedEventArgs e)
        {
            BrowseFolderInstead = true;
            DialogResult = false;
            Close();
        }

        private void BrowseWebsiteInstead_Click(object sender, RoutedEventArgs e)
        {
            BrowseWebsiteInstead = true;
            DialogResult = false;
            Close();
        }

        private void BrowseInstead_Click(object sender, RoutedEventArgs e)
        {
            BrowseInstead = true;
            DialogResult = false;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
