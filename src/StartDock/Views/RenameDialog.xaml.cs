using System.Windows;

namespace StartDock.Views
{
    public partial class RenameDialog : Window
    {
        /// <summary>The trimmed new name, set only when the user clicks Save.</summary>
        public string NewName { get; private set; } = string.Empty;

        public RenameDialog(string currentName)
        {
            InitializeComponent();
            Services.ThemeService.ApplyTitleBarTheme(this);
            NameBox.Text = currentName;
            Loaded += (_, _) =>
            {
                NameBox.Focus();
                NameBox.SelectAll();
            };
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string name = NameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                NameBox.Focus();
                return; // an empty name isn't allowed — keep the dialog open
            }

            NewName = name;
            DialogResult = true;
        }
    }
}
