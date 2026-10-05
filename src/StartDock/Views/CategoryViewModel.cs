using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using StartDock.Models;

namespace StartDock.Views
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

        private void NotifyIsEmptyChanged() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEmpty)));

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
