using System.Windows;
using System.Windows.Input;

namespace BerthMenu.Views
{
    public partial class UnsavedChangesDialog : Window
    {
        public enum Choice { Stay, Save, Discard }

        private Choice _choice = Choice.Stay;

        private UnsavedChangesDialog()
        {
            InitializeComponent();
            Services.ThemeService.ApplyTitleBarTheme(this);
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                    Close(); // back to Settings
            };
        }

        /// <summary>Save changes, Close without saving, or (closing this window or
        /// Esc) neither — go back to Settings.</summary>
        public static Choice Ask(Window owner)
        {
            var dialog = new UnsavedChangesDialog { Owner = owner };
            dialog.ShowDialog();
            return dialog._choice;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _choice = Choice.Save;
            Close();
        }

        private void Discard_Click(object sender, RoutedEventArgs e)
        {
            _choice = Choice.Discard;
            Close();
        }
    }
}
