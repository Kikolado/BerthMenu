using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace StartDock.Models
{
    /// <summary>
    /// One tile in the icon grid: either a manually added shortcut to an app, file, or
    /// folder to launch, or (when <see cref="Children"/> is set) a folder tile grouping
    /// several other tiles the way dragging one Start Menu tile onto another does.
    /// </summary>
    public class DockIcon
    {
        /// <summary>Stable identifier, used as the cached-icon file name and for reordering/removal.</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Display name shown under the icon.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The path StartDock launches: an .exe, a .lnk shortcut, a document, a folder,
        /// or a "shell:AppsFolder\{AppUserModelId}" string for a Store/packaged app.
        /// </summary>
        public string TargetPath { get; set; } = string.Empty;

        /// <summary>Optional command-line arguments (ignored for .lnk / AppsFolder targets).</summary>
        public string Arguments { get; set; } = string.Empty;

        /// <summary>Optional "start in" working directory. Empty = let Windows decide.</summary>
        public string WorkingDirectory { get; set; } = string.Empty;

        /// <summary>Always start as administrator (Properties → "Always run as
        /// administrator"). Ignored for Store apps, which can't be.</summary>
        public bool RunAsAdmin { get; set; }

        /// <summary>Given its own name (Rename or Properties). Removing a tile asks
        /// first when it was customized — see MainWindow.RemoveIcon_Click.</summary>
        public bool Renamed { get; set; }

        /// <summary>
        /// Path to the cached, pre-extracted icon image (PNG) under the app data folder.
        /// Re-extracted automatically if missing. Unused for a folder tile — its tile
        /// shows a preview built from its children's icons instead (see MainWindow).
        /// </summary>
        public string CachedIconPath { get; set; } = string.Empty;

        /// <summary>
        /// When set, this tile is a folder grouping these icons instead of launching
        /// anything of its own — TargetPath/Arguments/WorkingDirectory are unused for a
        /// folder. Folders can't contain folders (dragging one onto another just
        /// reorders, matching the Windows Start Menu's own behavior).
        /// </summary>
        public List<DockIcon>? Children { get; set; }

        [JsonIgnore]
        public bool IsFolder => Children != null && Children.Count > 0;
    }
}
