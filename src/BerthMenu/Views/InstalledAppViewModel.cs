using System.ComponentModel;
using System.Windows.Media;
using BerthMenu.Models;

namespace BerthMenu.Views
{
    /// <summary>Wraps an <see cref="InstalledAppInfo"/> with a lazily-loaded icon for
    /// the installed-apps picker list.</summary>
    public class InstalledAppViewModel : INotifyPropertyChanged
    {
        public InstalledAppInfo Model { get; }

        public string Name => Model.Name;

        private ImageSource? _iconImage;
        public ImageSource? IconImage
        {
            get => _iconImage;
            set
            {
                _iconImage = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconImage)));
            }
        }

        /// <summary>Set once the icon has been extracted and cached to disk, so that
        /// actually adding this app can reuse the file instead of extracting it again.</summary>
        public string? CachedIconPath { get; set; }

        public InstalledAppViewModel(InstalledAppInfo model)
        {
            Model = model;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
