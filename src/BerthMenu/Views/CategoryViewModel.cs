using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using BerthMenu.Models;

namespace BerthMenu.Views
{
    /// <summary>Wraps a <see cref="Category"/> — one named, independently-wrapping
    /// row of icons (see MainWindow's category grid and FlowGridPanel).</summary>
    public class CategoryViewModel : INotifyPropertyChanged
    {
        public Category Model { get; }

        /// <summary>Settable (unlike DockIconViewModel.Name) so the banner's
        /// rename textbox can two-way bind straight to it — see MainWindow's
        /// CategoryNameBox_LostFocus/_KeyDown, which call
        /// BindingExpression.UpdateSource() to commit on blur or Enter, the same
        /// "commit, don't push every keystroke" moment a folder's rename box uses.</summary>
        public string Name
        {
            get => Model.Name;
            set
            {
                string trimmed = string.IsNullOrWhiteSpace(value) ? "New category" : value;
                if (Model.Name == trimmed)
                    return;

                Model.Name = trimmed;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }

        /// <summary>Whether this category has no icons left — the only state in
        /// which its banner's delete button does anything (see MainWindow's
        /// DeleteCategory_Click). Deleting a category that still has icons in it
        /// isn't supported: drag them out (or into another category) first, the
        /// same way you'd empty a folder before it's safe to remove.</summary>
        public bool IsEmpty => Icons.Count == 0;

        public ObservableCollection<DockIconViewModel> Icons { get; }

        public CategoryViewModel(Category model)
        {
            Model = model;
            Icons = new ObservableCollection<DockIconViewModel>(
                model.Icons.Select(i => new DockIconViewModel(i)));
            Icons.CollectionChanged += (_, _) => NotifyIsEmptyChanged();
        }

        private void NotifyIsEmptyChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEmpty)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CollapsedToolTip)));
        }

        /// <summary>Folded down to just its name — clicking the name toggles it
        /// (MainWindow.CategoryBanner_MouseLeftButtonUp). Saved with the category.</summary>
        public bool IsCollapsed
        {
            get => Model.Collapsed;
            set
            {
                if (Model.Collapsed == value)
                    return;
                Model.Collapsed = value;
                foreach (string name in new[] { nameof(IsCollapsed), nameof(IconsVisibility), nameof(ChevronGlyph), nameof(CollapsedToolTip) })
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }

        public Visibility IconsVisibility => IsCollapsed ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>Chevron right when folded, down when open (Segoe Fluent Icons).</summary>
        public string ChevronGlyph => IsCollapsed ? "\uE76C" : "\uE70D";

        /// <summary>The banner's tooltip while folded; none while open.</summary>
        public string? CollapsedToolTip => IsCollapsed
            ? $"{Icons.Count} {(Icons.Count == 1 ? "app" : "apps")} hidden. Click to show."
            : null;

        private bool _isRenaming;
        /// <summary>The name is being edited (right-click → Rename Category). Only
        /// then can the name box be clicked into; otherwise a click folds the category.</summary>
        public bool IsRenaming
        {
            get => _isRenaming;
            set
            {
                if (_isRenaming == value)
                    return;
                _isRenaming = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRenaming)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
