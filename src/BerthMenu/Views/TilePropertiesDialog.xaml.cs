using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using BerthMenu.Models;
using BerthMenu.Services;

namespace BerthMenu.Views
{
    /// <summary>
    /// Right-click → Properties... on a pinned tile: its name, what it opens, the
    /// arguments and "Start in" folder it's started with, and "Always run as
    /// administrator". Lets one program have several tiles with different
    /// arguments. The results are read back by MainWindow.TileProperties_Click.
    /// </summary>
    public partial class TilePropertiesDialog : Window
    {
        public string TileName => NameBox.Text.Trim();
        public string Keywords => KeywordsBox.Text.Trim();
        public string Target => TargetBox.Text.Trim().Trim('"');
        public string Arguments => ArgumentsBox.IsEnabled ? ArgumentsBox.Text.Trim() : string.Empty;
        public string StartIn => StartInBox.Text.Trim().Trim('"');
        public bool RunAsAdmin => RunAsAdminCheck.IsEnabled && RunAsAdminCheck.IsChecked == true;

        public TilePropertiesDialog(DockIcon icon)
        {
            InitializeComponent();
            ThemeService.ApplyTitleBarTheme(this);

            NameBox.Text = icon.Name;
            KeywordsBox.Text = icon.Keywords ?? string.Empty;
            TargetBox.Text = icon.TargetPath;
            ArgumentsBox.Text = icon.Arguments;
            StartInBox.Text = icon.WorkingDirectory;
            RunAsAdminCheck.IsChecked = icon.RunAsAdmin;
            UpdateForTarget();

            Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
        }

        private void TargetBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (IsLoaded)
                UpdateForTarget();
        }

        /// <summary>Turns off what doesn't apply to this kind of target, and says why.</summary>
        private void UpdateForTarget()
        {
            string target = Target;
            bool isStoreApp = target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase) && target.Contains('!');
            bool acceptsArgs = AppLauncher.AcceptsArguments(target);
            bool isProgram = acceptsArgs || target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase);

            ArgumentsBox.IsEnabled = acceptsArgs;
            RunAsAdminCheck.IsEnabled = isProgram && !isStoreApp;

            ArgumentsNote.Text = isStoreApp
                ? "Store apps can't be given arguments or run as administrator."
                : !acceptsArgs
                    ? "Arguments only apply to programs, shortcuts and scripts."
                    : "Added after the program, e.g. --incognito or a file to open.";
        }

        private void BrowseTarget_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "What should this tile open?",
                Filter = "Programs, scripts and shortcuts (*.exe;*.lnk;*.cmd;*.bat;*.url;*.rdp)|*.exe;*.lnk;*.cmd;*.bat;*.url;*.rdp|All files (*.*)|*.*",
                CheckFileExists = true,
            };
            try
            {
                if (File.Exists(Target))
                    dialog.InitialDirectory = Path.GetDirectoryName(Target);
            }
            catch { /* not a path */ }
            if (dialog.ShowDialog(this) == true)
                TargetBox.Text = dialog.FileName;
        }

        private void BrowseStartIn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Start in" };
            if (Directory.Exists(StartIn))
                dialog.InitialDirectory = StartIn;
            if (dialog.ShowDialog(this) == true)
                StartInBox.Text = dialog.FolderName;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string? problem = null;
            if (TileName.Length == 0)
                problem = "Give the tile a name.";
            else if (Target.Length == 0)
                problem = "Choose what the tile opens.";
            else if (StartIn.Length > 0 && !Directory.Exists(Environment.ExpandEnvironmentVariables(StartIn)))
                problem = "The \"Start in\" folder doesn't exist.";

            if (problem != null)
            {
                ErrorText.Text = problem;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
            DialogResult = true;
        }
    }
}
