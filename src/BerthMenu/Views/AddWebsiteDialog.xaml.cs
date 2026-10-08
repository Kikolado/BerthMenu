using System;
using System.Windows;
using System.Windows.Controls;

namespace BerthMenu.Views
{
    public partial class AddWebsiteDialog : Window
    {
        /// <summary>The full address (https:// added if it was left out), set on Add.</summary>
        public string Url { get; private set; } = string.Empty;

        /// <summary>The tile's name, set on Add.</summary>
        public string SiteName { get; private set; } = string.Empty;

        // The name follows the address (example.com → "Example") until it's typed in by hand.
        private bool _nameEdited;
        private bool _settingName;

        public AddWebsiteDialog()
        {
            InitializeComponent();
            Services.ThemeService.ApplyTitleBarTheme(this);
            Loaded += (_, _) => AddressBox.Focus();
        }

        /// <summary>"example.com/page" → https://example.com/page. Null if it isn't a
        /// usable web address.</summary>
        public static Uri? Normalize(string text)
        {
            text = text.Trim();
            if (text.Length == 0 || text.Contains(' '))
                return null;
            if (!text.Contains("://"))
                text = "https://" + text;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
                return null;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return null;
            if (!uri.Host.Contains('.') && uri.Host != "localhost")
                return null;
            return uri;
        }

        /// <summary>A name from the address: www.youtube.com → "Youtube".</summary>
        public static string NameFor(Uri uri)
        {
            string host = uri.Host;
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                host = host.Substring(4);
            string[] parts = host.Split('.');
            string main = parts.Length >= 2 ? parts[^2] : parts[0];
            // Short country domains like bbc.co.uk: take the part before "co".
            if (parts.Length >= 3 && main.Length <= 3 && parts[^1].Length == 2)
                main = parts[^3];
            return main.Length == 0 ? host : char.ToUpperInvariant(main[0]) + main.Substring(1);
        }

        private void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ErrorText.Visibility = Visibility.Collapsed;
            if (_nameEdited)
                return;
            _settingName = true;
            NameBox.Text = Normalize(AddressBox.Text) is { } uri ? NameFor(uri) : string.Empty;
            _settingName = false;
        }

        private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_settingName)
                _nameEdited = NameBox.Text.Length > 0;
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var uri = Normalize(AddressBox.Text);
            if (uri == null)
            {
                ErrorText.Visibility = Visibility.Visible;
                AddressBox.Focus();
                return;
            }
            Url = uri.AbsoluteUri;
            SiteName = NameBox.Text.Trim();
            if (SiteName.Length == 0)
                SiteName = NameFor(uri);
            DialogResult = true;
        }
    }
}
