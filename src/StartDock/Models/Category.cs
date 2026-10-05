using System;
using System.Collections.Generic;

namespace StartDock.Models
{
    /// <summary>
    /// A named, independently-wrapping row of icons — StartDock's top-level
    /// organizing unit, replacing the earlier free-placement grid entirely.
    /// Icons within a category simply flow left to right and wrap to as many
    /// lines as they need (see StartDock.Views.FlowGridPanel) — the same dense,
    /// gap-free packing a folder's own contents already used, so there's no more
    /// "leave a deliberate gap" positioning anywhere in the app.
    /// </summary>
    public class Category
    {
        /// <summary>Stable identifier — not currently used for lookup (categories
        /// are matched by reference/index in memory), but kept for the same reason
        /// DockIcon has one: cheap future-proofing (e.g. per-category settings)
        /// without a schema change.</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Shown as the row's banner heading, editable in place the same
        /// way a folder's name is (see MainWindow's category rename handlers).</summary>
        public string Name { get; set; } = "New category";

        public List<DockIcon> Icons { get; set; } = new();
    }
}
