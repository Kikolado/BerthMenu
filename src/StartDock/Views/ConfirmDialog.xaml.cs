using System.Windows;

namespace StartDock.Views
{
    public partial class ConfirmDialog : Window
    {
        private ConfirmDialog(string title, string heading, string message, string confirmText)
        {
            InitializeComponent();
            Services.ThemeService.ApplyTitleBarTheme(this);
            Title = title;
            HeadingText.Text = heading;
            MessageText.Text = message;
            MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
            ConfirmButton.Content = confirmText;
        }

        /// <summary>Asks, and returns true only if <paramref name="confirmText"/> was
        /// clicked. Enter and Esc both cancel, so nothing is removed by accident.</summary>
        public static bool Ask(Window owner, string title, string heading, string message, string confirmText)
        {
            var dialog = new ConfirmDialog(title, heading, message, confirmText) { Owner = owner };
            return dialog.ShowDialog() == true;
        }

        private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
