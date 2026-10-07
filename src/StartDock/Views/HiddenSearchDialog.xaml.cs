using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using StartDock.Models;

namespace StartDock.Views
{
    /// <summary>Lists AppConfig.HiddenFromSearch and removes the ones chosen with
    /// "Show again" — from the list it's given (Settings' working copy).</summary>
    public partial class HiddenSearchDialog : Window
    {
        private readonly List<HiddenSearchItem> _items;

        public HiddenSearchDialog(List<HiddenSearchItem> items)
        {
            InitializeComponent();
            Services.ThemeService.ApplyTitleBarTheme(this);
            _items = items;
            Refresh();
        }

        private void Refresh()
        {
            HiddenList.ItemsSource = null;
            HiddenList.ItemsSource = _items.OrderBy(i => i.Name, System.StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void HiddenList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            ShowAgainButton.IsEnabled = HiddenList.SelectedItems.Count > 0;

        private void ShowAgain_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in HiddenList.SelectedItems.OfType<HiddenSearchItem>().ToList())
                _items.Remove(item);
            Refresh();
            if (_items.Count == 0)
                Close();
        }
    }
}
