using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Media;
using BerthMenu.Models;

namespace BerthMenu.Views
{
    /// <summary>Wraps a <see cref="DockIcon"/> with its resolved, UI-ready icon image —
    /// or, for a folder tile, with its children's view-models instead.</summary>
    public class DockIconViewModel : INotifyPropertyChanged
    {
        public DockIcon Model { get; }

        public string Name => Model.Name;

        public bool IsFolder => Model.IsFolder;
        public bool IsSingleIcon => !IsFolder;

        /// <summary>True for a transient tile shown only while searching all of the
        /// computer's installed apps (see MainWindow's search feature) — it isn't
        /// pinned to BerthMenu, has no persisted DockIcon of its own, and disappears
        /// once the search is cleared unless the user chooses to pin it.</summary>
        public bool IsSearchResult { get; }

        public bool IsPinned => !IsSearchResult;

        /// <summary>A file tile — from the Recent Files section or a file search
        /// result — so app-only actions (Run as administrator, Uninstall) don't apply.</summary>
        public bool IsFile { get; init; }

        /// <summary>A folder shown while browsing a pinned folder inside the dock
        /// (see MainWindow.OpenBrowse): clicking it opens it there too.</summary>
        public bool IsBrowseFolder { get; init; }

        /// <summary>Something that can be launched as a program (as opposed to a
        /// folder tile, a document, a Settings page, or a recent file) — what the
        /// right-click Run as administrator / Uninstall items apply to.</summary>
        public bool IsApp
        {
            get
            {
                if (IsFolder || IsFile || IsAction)
                    return false;
                string target = Model.TargetPath ?? string.Empty;
                return target.StartsWith("shell:AppsFolder\\", System.StringComparison.OrdinalIgnoreCase)
                    || target.EndsWith(".exe", System.StringComparison.OrdinalIgnoreCase)
                    || target.EndsWith(".lnk", System.StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>Run as administrator applies to desktop programs, not Store/UWP
        /// apps (Windows' own Start Menu doesn't offer it for those either). A Store
        /// app's AppsFolder id is "PackageFamilyName!AppId" — the "!" marks it.</summary>
        /// <summary>A pinned single tile (not a folder, not a search result or
        /// automatic-section tile) — what right-click Rename / Change icon apply to.</summary>
        public bool CanCustomize => IsPinned && !IsFolder;

        /// <summary>A built-in search action rather than something on disk: the
        /// calculator's answer or "Search the web for …" (see MainWindow's
        /// RenderSearchResults). Can't be pinned, and has no file location.</summary>
        public bool IsAction { get; init; }

        /// <summary>Right-click → Pin to BerthMenu.</summary>
        public bool CanPin => IsSearchResult && !IsAction;

        /// <summary>Right-click → Refresh icon: pinned tiles, and installed apps shown
        /// in search results or Recently Added (which refresh the shared app list's
        /// copy — see MainWindow.RefreshIcon_Click).</summary>
        public bool CanRefreshIcon => CanCustomize
            || (IsSearchResult && !IsAction && !IsFile
                && (Model.TargetPath ?? string.Empty).StartsWith("shell:AppsFolder\\", System.StringComparison.OrdinalIgnoreCase));

        /// <summary>A website tile (Add → Add a website…): the target is an http(s) address.</summary>
        public bool IsWebsite =>
            (Model.TargetPath ?? string.Empty).StartsWith("http://", System.StringComparison.OrdinalIgnoreCase)
            || (Model.TargetPath ?? string.Empty).StartsWith("https://", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>A saved Remote Desktop connection (.rdp file): right-click → Edit connection.</summary>
        public bool IsRemoteDesktop =>
            !IsFolder && (Model.TargetPath ?? string.Empty).EndsWith(".rdp", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>Right-click → Open file location. Only for things that are on
        /// disk: not websites, Settings pages or Store apps (whose id contains a "!").</summary>
        public bool CanOpenLocation
        {
            get
            {
                if (IsFolder || IsAction || IsWebsite)
                    return false;
                string target = Model.TargetPath ?? string.Empty;
                if (target.StartsWith("shell:AppsFolder\\", System.StringComparison.OrdinalIgnoreCase))
                    return !target.Contains('!');
                // Settings pages, shell: folders and other addresses have no file.
                int colon = target.IndexOf(':');
                return !(colon > 1 && !target.StartsWith("%"));
            }
        }

        private bool _canHideFromSearch;
        /// <summary>Right-click → Hide from search: set on search results that
        /// aren't pinned or built-in actions (MainWindow.RenderSearchResults).</summary>
        public bool CanHideFromSearch
        {
            get => _canHideFromSearch;
            set
            {
                if (_canHideFromSearch == value)
                    return;
                _canHideFromSearch = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanHideFromSearch)));
            }
        }

        private string? _shortcutBadge;
        /// <summary>"1"–"9" while Alt is held in the dock: Alt+that number opens
        /// this tile (MainWindow.ShowShortcutBadges). Null otherwise.</summary>
        public string? ShortcutBadge
        {
            get => _shortcutBadge;
            set
            {
                if (_shortcutBadge == value)
                    return;
                _shortcutBadge = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShortcutBadge)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasShortcutBadge)));
            }
        }

        public bool HasShortcutBadge => _shortcutBadge != null;

        private bool _isRunning;
        /// <summary>The app is open right now — shows the small bar under the tile
        /// (AppConfig.ShowRunningIndicator; see Services/RunningApps).</summary>
        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (_isRunning == value)
                    return;
                _isRunning = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
            }
        }

        public bool CanRunAsAdmin => IsApp && !(Model.TargetPath ?? string.Empty).Contains('!');

        private ImageSource? _iconImage;
        public ImageSource? IconImage
        {
            get => _iconImage;
            set
            {
                _iconImage = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconImage)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AnimatedIconPath)));
            }
        }

        /// <summary>The icon file when it's a GIF (a custom icon picked with Change
        /// icon…), so the tile can play it — see AnimatedGif. Null otherwise.
        /// Re-read whenever IconImage changes, which is when a new icon is loaded.</summary>
        public string? AnimatedIconPath => AnimatedGif.IsGif(Model.CachedIconPath) ? Model.CachedIconPath : null;

        /// <summary>All of a folder's contents. Empty for a plain (non-folder) tile.
        /// Used both for the folder's in-dock browse view (see MainWindow's
        /// OpenFolder/CloseFolder) and, via <see cref="PreviewChildren"/>, for the
        /// folder tile's own mini-icon preview.</summary>
        public ObservableCollection<DockIconViewModel> Children { get; }

        /// <summary>The first 4 children, for the folder tile's 2x2 mini-icon preview —
        /// matches how the Windows Start Menu previews a group's contents.</summary>
        public IEnumerable<DockIconViewModel> PreviewChildren => Children.Take(4);

        public DockIconViewModel(DockIcon model, bool isSearchResult = false)
        {
            Model = model;
            IsSearchResult = isSearchResult;
            Children = new ObservableCollection<DockIconViewModel>(
                (model.Children ?? new List<DockIcon>()).Select(c => new DockIconViewModel(c)));
        }

        /// <summary>Raised after <see cref="Children"/> is mutated (an app moved in or
        /// out of this folder) so the tile's folder/single-icon visual and mini preview
        /// pick up the change immediately.</summary>
        public void NotifyChildrenChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFolder)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSingleIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewChildren)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsApp)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRunAsAdmin)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCustomize)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRefreshIcon)));
        }

        /// <summary>Raised after a tile's target is changed (Properties...), so the
        /// right-click menu's options follow what it now opens.</summary>
        public void NotifyTargetChanged()
        {
            foreach (string name in new[] { nameof(IsApp), nameof(CanRunAsAdmin), nameof(CanOpenLocation),
                                            nameof(IsWebsite), nameof(IsRemoteDesktop), nameof(CanRefreshIcon) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>Raised after this folder is renamed via the in-dock folder header.</summary>
        public void NotifyNameChanged() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
