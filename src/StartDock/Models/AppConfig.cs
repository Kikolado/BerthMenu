using System.Collections.Generic;

namespace StartDock.Models
{
    /// <summary>How the dock is activated from the keyboard.</summary>
    public enum HotkeyMode
    {
        /// <summary>Plain Windows key opens the dock and the native Start Menu is fully suppressed.</summary>
        WindowsKey,

        /// <summary>Only Shift+Windows opens the dock; a plain Windows key press is left alone
        /// (passes through to whatever Windows would normally do with it).</summary>
        ShiftWindowsKey,

        /// <summary>An arbitrary user-captured modifier+key combo (see
        /// CustomHotkeyModifiers/CustomHotkeyVirtualKey below, and SettingsWindow's
        /// capture field) — added after WindowsKey/ShiftWindowsKey so their existing
        /// numeric values (0/1) don't shift for anyone upgrading from an older config.</summary>
        Custom
    }

    /// <summary>See AppConfig.BorderColor.</summary>
    public enum BorderColorMode
    {
        Theme,
        White,
        Black,
        Custom, // AppConfig.CustomBorderColor
    }

    /// <summary>See AppConfig.BackgroundColor. Append-only: stored as a number.</summary>
    public enum BackgroundColorMode
    {
        Theme,
        Custom, // AppConfig.CustomBackgroundColor
    }

    /// <summary>See AppConfig.AccentColor. Append-only: stored as a number.</summary>
    public enum AccentColorMode
    {
        Theme,
        Windows, // the accent color chosen in Windows Settings → Personalization → Colors
        Custom,  // AppConfig.CustomAccentColor
    }

    /// <summary>Light or dark visual theme for the dock chrome.</summary>
    public enum ThemeMode
    {
        FollowWindows,
        Light,
        Dark
    }

    /// <summary>Overrides the dock's normal theme-driven text color with a fixed
    /// one, independent of ThemeMode. Default leaves PrimaryTextBrush/
    /// SecondaryTextBrush exactly as the active theme (Theme.xaml/Theme.Dark.xaml)
    /// defines them; Black/White pin the dock's text to a literal color regardless
    /// of which theme is active or what it would otherwise pick. See
    /// MainWindow.xaml.cs's ApplyTextColor.</summary>
    public enum TextColorMode
    {
        Default,
        Black,
        White,
        Custom, // AppConfig.CustomTextColor
    }

    /// <summary>Which tint Windows' frosted-glass material uses (see
    /// AppConfig.FrostTint). Append-only, like the other enums here — config.json
    /// stores the numeric value.</summary>
    /// <summary>Which site the search box's "Search the web" result opens.
    /// Append-only — config.json stores the numeric value.</summary>
    public enum WebSearchEngine
    {
        Google,
        Bing,
        DuckDuckGo
    }

    public enum FrostTintMode
    {
        MatchTheme,
        Light,
        Dark,

        /// <summary>A plain blur with no tint of its own — any color comes only
        /// from the dock's own Background opacity. Uses the older
        /// SetWindowCompositionAttribute blur rather than Windows 11's acrylic
        /// (which always tints), so its corners may come out square; see
        /// MainWindow.ApplyFrostedBackground.</summary>
        Clear
    }

    /// <summary>Color for an icon tile's own persistent background, when
    /// AppConfig.ShowIconBackground turns one on. Theme keeps it tied to the
    /// dock's normal hover color (DynamicResource-driven, so it follows a live
    /// theme switch); Black/White pin it to a literal color instead. See
    /// MainWindow.xaml's IconTileTemplate/DataTemplate.Triggers, the only place
    /// this is ever consumed.</summary>
    public enum IconBackgroundColorMode
    {
        Theme,
        Black,
        White,
        Custom, // AppConfig.CustomIconBackgroundColor
    }

    /// <summary>Which modifier keys must be held for HotkeyMode.Custom's activation
    /// combo — a plain, WPF-independent mirror of System.Windows.Input.ModifierKeys,
    /// with the exact same flag values on purpose (Alt=1, Control=2, Shift=4,
    /// Windows=8), so converting between the two on either side (SettingsWindow's
    /// capture UI, which naturally deals in WPF's own ModifierKeys) is just a plain
    /// cast — see SettingsWindow.xaml.cs. Keeping this as a plain enum here rather
    /// than referencing System.Windows.Input directly keeps the Models layer free of
    /// a WPF-specific dependency, matching HotkeyMode/ThemeMode alongside it.</summary>
    [System.Flags]
    public enum HotkeyModifiers
    {
        None = 0,
        Alt = 1,
        Control = 2,
        Shift = 4,
        Windows = 8,
    }

    /// <summary>How each category's own row of icons is packed horizontally
    /// within the dock's width — see StartDock.Views.FlowGridPanel.Alignment,
    /// which this drives.</summary>
    public enum RowAlignment
    {
        /// <summary>Packed against the left edge — StartDock's original,
        /// default behavior. A row that doesn't fill the dock's full width
        /// (most often a category's last, partial row, or a whole category
        /// short enough to fit on one) sits flush left with empty space
        /// trailing it.</summary>
        Left,

        /// <summary>Horizontally centered within the dock's width instead.
        /// Only visibly different from Left for a row that doesn't already
        /// span the full width — a full row looks identical either way.</summary>
        Center,

        /// <summary>Packed against the right edge instead — the mirror image
        /// of Left. Added after Left/Center so their existing numeric values
        /// (0/1) don't shift for anyone upgrading from an older config (see
        /// DockPosition below, which follows the same append-only rule and
        /// explains why it matters: config.json persists this as a plain
        /// integer, not a name).</summary>
        Right,
    }

    /// <summary>Which edge of the dock the menu bar sits on (AppConfig.MenuBarPosition).
    /// Saved as a number, so new members go at the end.</summary>
    public enum MenuBarPlacement
    {
        Bottom,
        Top,
        Left,
        Right,
    }

    /// <summary>How icons are packed within a column in the Columns layout
    /// (AppConfig.ColumnAlignment) — Columns' counterpart to RowAlignment.</summary>
    public enum ColumnAlignment
    {
        Top,
        Center,
        Bottom,
    }

    /// <summary>Which way each category's icons run (AppConfig.LayoutDirection).
    /// Saved as a number like the other enums here, so new members go at the end.</summary>
    public enum LayoutDirection
    {
        /// <summary>The original layout: categories stacked top to bottom, each
        /// one's icons running left to right and wrapping onto more rows.</summary>
        Rows,

        /// <summary>Categories side by side, left to right, each one's icons
        /// running top to bottom and wrapping into more columns. The dock scrolls
        /// sideways when they don't all fit.</summary>
        Columns,
    }

    /// <summary>Where the dock appears on screen. New members are always added
    /// at the end, never inserted between existing ones — config.json persists
    /// this as a plain integer (System.Text.Json's default enum serialization,
    /// see ConfigService), so reordering would silently reinterpret whatever
    /// value an existing install already has saved as a different position the
    /// next time it loads.</summary>
    /// <summary>See AppConfig.DockAnimation. Append-only: stored as a number.</summary>
    public enum DockAnimation
    {
        Slide,
        Fade,
        None,
    }

    public enum DockPosition
    {
        /// <summary>Anchored to the bottom-left of the work area — StartDock's
        /// original placement (the default is now BottomCenter). Slides up from the
        /// taskbar edge like the other bottom positions (from 0.9.6; it used to
        /// appear instantly).</summary>
        BottomLeft,

        /// <summary>Bottom-center of the work area, sliding up from the taskbar edge
        /// when it appears — like the Windows 11 Start Menu.</summary>
        BottomCenter,

        /// <summary>Top-center of the work area, sliding down into place.</summary>
        TopCenter,

        /// <summary>Dead center of the work area. With Slide it rises a short way
        /// (there's no edge to slide in from), or fades without room. Shown in
        /// Settings as "Middle center" (see SettingsWindow.xaml's PositionCombo,
        /// grouped alongside Middle left/Middle right) — only that display label
        /// changed, not this member's name, since nothing about the rename requires
        /// touching the enum itself.</summary>
        Center,

        /// <summary>Top-right of the work area — the mirror image of BottomLeft's
        /// corner anchor, but top-anchored, so it slides down into place the same
        /// way TopCenter does rather than appearing instantly.</summary>
        TopRight,

        /// <summary>Bottom-right of the work area — the mirror image of BottomLeft,
        /// sliding up into place the same way BottomCenter does.</summary>
        BottomRight,

        /// <summary>Top-left of the work area — the mirror image of TopRight, sliding
        /// down into place the same way. Added after the original six members so
        /// their saved integer values never shift (see this enum's own doc comment);
        /// SettingsWindow's PositionCombo reorders these into a friendlier on-screen
        /// grouping without touching these numeric values (see PositionComboOrder).</summary>
        TopLeft,

        /// <summary>Vertically centered against the left edge of the work area.
        /// With Slide it comes in from the left edge (see MainWindow.ChooseAnimation).</summary>
        MiddleLeft,

        /// <summary>Vertically centered against the right edge of the work area — the
        /// mirror image of MiddleLeft, sliding in from the right edge.</summary>
        MiddleRight,
    }

    /// <summary>
    /// Everything StartDock persists between sessions. Serialized as JSON to
    /// %AppData%\StartDock\config.json.
    /// </summary>
    public class AppConfig
    {
        /// <summary>The dock's top-level content: each category is an independently-
        /// wrapping named row of icons (see Category, and StartDock.Views.FlowGridPanel
        /// for how a row lays itself out). Replaces the old flat, freely-placed
        /// <c>Icons</c> list from schema version 1 — see ConfigService.Load for the
        /// one-time migration that wraps an old config's icons into a single "Pinned"
        /// category the first time it's loaded under the new schema.</summary>
        public List<Category> Categories { get; set; } = new();

        public HotkeyMode Hotkey { get; set; } = HotkeyMode.WindowsKey;

        /// <summary>Only meaningful when Hotkey == HotkeyMode.Custom — which modifiers
        /// must be held (see HotkeyModifiers) and 0 (unset) here means no combo has
        /// been captured yet, in which case Custom mode simply never fires.</summary>
        public HotkeyModifiers CustomHotkeyModifiers { get; set; } = HotkeyModifiers.None;

        /// <summary>Only meaningful when Hotkey == HotkeyMode.Custom — the Win32
        /// virtual-key code (VK_*) of the combo's own non-modifier key, e.g. 0x20 for
        /// Space. 0 means unset.</summary>
        public int CustomHotkeyVirtualKey { get; set; } = 0;

        public ThemeMode Theme { get; set; } = ThemeMode.FollowWindows;

        /// <summary>Overrides the dock's text color independent of Theme — Default
        /// leaves the active theme's PrimaryTextBrush/SecondaryTextBrush alone,
        /// Black/White pin it to a literal color instead. See MainWindow.xaml.cs's
        /// ApplyTextColor.</summary>
        public TextColorMode TextColor { get; set; } = TextColorMode.Default;

        // The colors behind each "Custom color" choice, as "#RRGGBB". Kept when
        // another choice is picked, so switching back to Custom brings them back.
        public string CustomTextColor { get; set; } = "#FFFFFF";
        public string CustomIconBackgroundColor { get; set; } = "#0078D4";
        public string CustomBorderColor { get; set; } = "#0078D4";
        public string CustomBackgroundColor { get; set; } = "#1F2A3A";
        public string CustomAccentColor { get; set; } = "#0078D4";

        /// <summary>The dock's background color (Settings → Background → Color):
        /// the theme's, or CustomBackgroundColor. Opacity, frosted glass and a
        /// background picture still apply on top.</summary>
        public BackgroundColorMode BackgroundColor { get; set; } = BackgroundColorMode.Theme;

        /// <summary>The dock's highlight color (Settings → Look → Accent color): the
        /// running-app bar, Alt+number badges and focus outlines, plus a tint of it
        /// for hover, pressed tiles, folder previews and "Theme" icon tiles.</summary>
        public AccentColorMode AccentColor { get; set; } = AccentColorMode.Theme;

        /// <summary>Light or dark variant of the frosted-glass material
        /// (FrostedBackground). Windows' acrylic isn't a plain blur: it lays a
        /// light or dark tint over the blurred backdrop, so light frosting over a
        /// black desktop reads as gray. MatchTheme follows the dock's own Theme,
        /// which keeps its text readable; Light/Dark pin it either way.</summary>
        public FrostTintMode FrostTint { get; set; } = FrostTintMode.MatchTheme;

        /// <summary>Whether clicking the real taskbar Start button opens StartDock instead of native Start.</summary>
        public bool ReplaceStartButton { get; set; } = true;

        /// <summary>When the dock opens over a fullscreen app or game, also bring
        /// that monitor's taskbar to the front — the way the real Start Menu does —
        /// and send it back behind the app when the dock closes. See
        /// FullscreenTaskbarService.</summary>
        public bool ShowTaskbarOverFullscreen { get; set; } = false;

        /// <summary>Shows a small button in the menu bar (see MainWindow.xaml's
        /// NativeStartMenuButton) that opens the real, native Windows Start Menu —
        /// for anyone StartDock gets distributed to who still wants access to it,
        /// since ReplaceStartButton/HotkeyService otherwise intercept every Windows
        /// key press and every taskbar Start-button click and redirect them to this
        /// dock instead, with no other way back to the native menu. See
        /// AppLauncher.OpenNativeStartMenu for how the button itself works around
        /// that interception.</summary>
        public bool ShowNativeStartMenuButton { get; set; } = true;

        /// <summary>Individual show/hide toggles for the rest of the menu bar's
        /// utility buttons (see MainWindow.xaml's MenuBarRow) — each defaults to
        /// visible, matching StartDock's original, everything-shown bar. Settings
        /// itself is always still reachable even with ShowSettingsButton off,
        /// via the tray icon's own "Settings..." item (see TrayIconService), so
        /// hiding it from the bar is never a dead end.</summary>
        public bool ShowTaskManagerButton { get; set; } = true;
        public bool ShowVolumeMixerButton { get; set; } = true;
        public bool ShowFileExplorerButton { get; set; } = true;
        public bool ShowCalculatorButton { get; set; } = true;
        public bool ShowSettingsButton { get; set; } = true;
        public bool ShowPowerButton { get; set; } = true;

        /// <summary>The menu bar's "+" (add an app, file, or folder) button. Apps
        /// can still be added while it's hidden by pinning a search result.</summary>
        public bool ShowAddButton { get; set; } = true;

        /// <summary>The menu bar's account picture (the signed-in user's icon,
        /// which opens Windows' account settings when clicked).</summary>
        public bool ShowUserButton { get; set; } = true;

        /// <summary>The thin line between the menu bar and the rest of the dock.
        /// Independent of HideBorder/BoldBorder, which still decide its color and
        /// thickness while it's showing.</summary>
        public bool ShowMenuBarDivider { get; set; } = true;

        /// <summary>Automatic "Recently added" section at the bottom of the dock:
        /// apps installed in the last week (see AppSeenTracker).</summary>
        public bool ShowNewApps { get; set; } = false;

        /// <summary>Automatic "Recent files" section at the bottom of the dock, from
        /// Windows' Recent Items list (see RecentFilesService).</summary>
        public bool ShowRecentFiles { get; set; } = false;

        /// <summary>Opacity (10–100%) of the persistent icon tile background
        /// (ShowIconBackground). Only the tile's background fades — the icon and
        /// its name stay fully visible.</summary>
        public double IconBackgroundOpacity { get; set; } = 100;

        /// <summary>How many tiles the Recent Files section shows (2–12).</summary>
        public int RecentFilesCount { get; set; } = 6;

        /// <summary>Automatic "Recently used" section at the bottom of the dock: apps
        /// opened lately, from StartDock or from Windows itself (see AppUsageService).</summary>
        public bool ShowRecentApps { get; set; } = false;

        /// <summary>How many tiles the Recently Added section shows (2–12).</summary>
        public int NewAppsCount { get; set; } = 6;

        /// <summary>How many tiles the Recently Used section shows (2–12).</summary>
        public int RecentAppsCount { get; set; } = 6;

        /// <summary>Show Recently Added, Recently Used and Recent Files together as
        /// one "Recent" section instead of a section each.</summary>
        public bool CombineRecentSections { get; set; } = false;

        /// <summary>Include files from Windows' search index in dock search results,
        /// alongside apps and Settings pages. See FileSearchService.</summary>
        public bool SearchFiles { get; set; } = true;

        /// <summary>Math typed into the search box (125*4, 15% of 80…) shows its
        /// answer as the first result. See Services/Calculator.</summary>
        public bool SearchCalculator { get; set; } = true;

        /// <summary>Adds a "Search the web for …" result at the end of search
        /// results, opening the default browser with WebSearchEngine.</summary>
        public bool SearchWeb { get; set; } = true;

        /// <summary>Like the Windows Run box: a command (cmd, notepad, ping 8.8.8.8)
        /// or a path (C:\Users, %AppData%) typed into search shows a "Run" / "Open"
        /// result. See Services/RunCommand.</summary>
        public bool SearchRun { get; set; } = true;

        /// <summary>A small bar under pinned apps that are open right now.
        /// See Services/RunningApps.</summary>
        public bool ShowRunningIndicator { get; set; } = false;

        /// <summary>Clicking a pinned folder (from disk, not a folder of tiles) shows
        /// its contents inside the dock, with Open in File Explorer one click away.
        /// Off: it opens in File Explorer. Shift+click always opens File Explorer.</summary>
        public bool OpenFoldersInDock { get; set; } = true;

        /// <summary>How the dock appears and goes away: Slide (in from the screen edge it
        /// sits against: up from behind the taskbar at the bottom), Fade, or None. Settings →
        /// Layout → Animation.</summary>
        public DockAnimation DockAnimation { get; set; } = DockAnimation.Slide;

        /// <summary>A folder shown inside the dock lists folders then files by name
        /// (true), or everything newest first (false). Changed with the sort button
        /// in the folder's header.</summary>
        public bool FolderBrowseByName { get; set; } = false;

        /// <summary>Things hidden from search with right-click → Hide from search
        /// (installed apps, Windows tools, files). Settings → Search → Manage brings
        /// them back. Pinned tiles always show.</summary>
        public List<HiddenSearchItem> HiddenFromSearch { get; set; } = new();

        public WebSearchEngine WebSearchEngine { get; set; } = WebSearchEngine.Google;

        /// <summary>Where the dock appears on screen — see DockPosition.</summary>
        // Bottom center (like the Windows 11 Start Menu) for new installs. An
        // existing config.json keeps whatever position it already has.
        public DockPosition Position { get; set; } = DockPosition.BottomCenter;

        /// <summary>How solid the dock's background is, as a percentage: 100 = today's
        /// fully solid look, 0 = fully invisible. Fades MainWindow's single shared
        /// BackgroundBorder (fill, border, and shadow), which spans the dock's entire
        /// height and sits directly behind the menu bar too (see MenuBarAtTop's own
        /// comment for why the menu bar has no background fill of its own) — so this
        /// one fade already covers the menu bar's background as well, in lockstep,
        /// with nothing extra to keep in sync. Icons, text, and the menu bar's buttons
        /// always stay fully crisp either way.</summary>
        public double BackgroundOpacity { get; set; } = 100;

        /// <summary>Absolute path to a user-chosen picture shown behind the dock's
        /// icons instead of the plain DockBackgroundBrush color — null (the default)
        /// means "no image, use the solid color as before". Always a copy ConfigService
        /// made under %AppData%\StartDock\Background, never the original file the user
        /// picked (see ConfigService.ImportBackgroundImage) — so moving, renaming, or
        /// deleting that original afterward doesn't break the dock's background.
        /// BackgroundOpacity above still fades it exactly the same way it fades the
        /// solid color, since both are just whatever's currently in BackgroundBorder's
        /// own Background property (see MainWindow.xaml.cs's ApplyBackgroundImage).</summary>
        public string? BackgroundImagePath { get; set; } = null;

        /// <summary>How BackgroundImagePath's picture is scaled to the dock's own
        /// size — false (the default) crops it to fill the dock edge-to-edge with no
        /// empty space, preserving the picture's aspect ratio (WPF's
        /// Stretch.UniformToFill — the original, only behavior before this setting
        /// existed); true instead scales it down to show the whole picture with
        /// nothing cropped off, which can letterbox — leave a sliver of the plain
        /// background color visible on one axis — when the picture's own aspect
        /// ratio doesn't match the dock's current size (Stretch.Uniform). See
        /// MainWindow.xaml.cs's ApplyBackgroundImage.</summary>
        public bool FitBackgroundImage { get; set; } = false;

        /// <summary>When true, asks the desktop compositor (DWM) to blur whatever's
        /// on screen behind the dock — see MainWindow.xaml.cs's
        /// ApplyFrostedBackground and NativeMethods' own comment on
        /// SetWindowCompositionAttribute for the mechanism. Purely a compositing
        /// effect of the window itself, so it works the same way underneath either
        /// kind of background this dock can show (the plain color, or a
        /// BackgroundImagePath picture) without either of them needing to know
        /// about it — BackgroundOpacity still controls how much of that blur (as
        /// opposed to solid color/image) actually shows through, exactly as it
        /// already controls how much of the plain desktop shows through today.
        /// Defaults off: it's an extra DWM call this window didn't need to make
        /// before, kept opt-in rather than risking it on every install.</summary>
        public bool FrostedBackground { get; set; } = false;

        /// <summary>When true, strengthens the dock's existing outline (BackgroundBorder's
        /// own BorderBrush/BorderThickness — see MainWindow.xaml) and the menu bar's
        /// divider line (DividerBorder) from their normal, subtle theme colors
        /// (DockBorderBrush/DividerBrush — barely-there grays, deliberately so they
        /// read as a soft edge rather than a frame) to a bold, high-contrast one
        /// instead: black on the light theme, white on the dark theme — whichever
        /// actually reads as "bold" against that theme's background, rather than a
        /// single fixed color that would all but disappear on one of the two themes.
        /// Not a second, independent border on top of the existing one — this
        /// replaces the existing border/divider's own color and thickness outright;
        /// see MainWindow.xaml.cs's ApplyBorderStyle. Follows ThemeMode.Follow the
        /// same way every other theme-aware color on this window does (recomputed
        /// via ThemeService.ResolveIsDark whenever ApplyAppearanceSettings runs, so
        /// switching between light/dark — or Windows' own theme changing, next time
        /// settings are touched — picks the right color automatically rather than
        /// freezing in whichever theme was active when this was turned on).</summary>
        public bool BoldBorder { get; set; } = false;

        /// <summary>When true, removes the dock's outline and the menu bar's divider
        /// line altogether — BackgroundBorder's BorderThickness and DividerBorder's
        /// Height both collapse to 0 (see MainWindow.xaml.cs's ApplyBorderStyle).
        /// Takes precedence over BoldBorder: a border that's hidden stays hidden
        /// regardless of whether Bold border is also checked, rather than the two
        /// settings fighting over how the (invisible) border should look. Kept as
        /// its own independent bool rather than folding BoldBorder into a three-way
        /// enum, so an existing saved BoldBorder: true/false value in a user's
        /// config.json keeps deserializing exactly as before (System.Text.Json
        /// enums serialize by position, and a bool-to-enum type change would break
        /// on the old value).</summary>
        public bool HideBorder { get; set; } = false;

        /// <summary>The outline's color (Settings → Look → Border color): Theme gives
        /// the normal soft gray (white or black when Bold, by theme); White/Black pin
        /// it, at either thickness. The menu bar divider follows it too.</summary>
        public BorderColorMode BorderColor { get; set; } = BorderColorMode.Theme;

        /// <summary>When true, the menu bar (user avatar, task manager, volume mixer,
        /// file explorer, settings, power — see MainWindow.xaml's MenuBarRow) sits at
        /// the top of the dock instead of the bottom, with the divider line between it
        /// and the icon grid moving along with it. The bar has no background fill of
        /// its own (an earlier pass gave it one, colored to match the rest of the
        /// dock — but a second same-colored fill stacked right at the seam between it
        /// and the main panel still read as a faint seam rather than one continuous
        /// surface), so MainWindow's single BackgroundBorder — which already spans the
        /// dock's full height and is already rounded on all four corners — shows
        /// through underneath it instead, at either position, genuinely seamlessly.</summary>
        public bool MenuBarAtTop { get; set; } = false;

        /// <summary>Which edge the menu bar sits on: Bottom, Top, Left or Right.
        /// Replaces MenuBarAtTop, which is still read for a config saved before this
        /// existed (null here) — see GetMenuBarPlacement — and still written, kept in
        /// step, so an older StartDock reading this config lands on Top or Bottom.</summary>
        public MenuBarPlacement? MenuBarPosition { get; set; }

        /// <summary>Reverses the menu bar's button order. Normally a top/bottom bar
        /// runs User icon … Power from left to right, and a left/right bar runs
        /// Power … User icon from top to bottom.</summary>
        public bool FlipMenuBarOrder { get; set; } = false;

        /// <summary>The menu bar's edge, falling back to the older MenuBarAtTop
        /// setting for a config saved before MenuBarPosition existed.</summary>
        public MenuBarPlacement GetMenuBarPlacement() =>
            MenuBarPosition ?? (MenuBarAtTop ? MenuBarPlacement.Top : MenuBarPlacement.Bottom);

        /// <summary>When true, the entire menu bar (user avatar, and every button
        /// covered by ShowTaskManagerButton/etc. below — see MainWindow.xaml's
        /// MenuBarRow) and the divider line beside it are collapsed outright,
        /// wherever MenuBarAtTop currently has them sitting — a single master
        /// switch on top of the individual per-button toggles below, for anyone
        /// who wants the icon grid alone with no bar at all rather than picking
        /// through which buttons to keep. The individual ShowTaskManagerButton/
        /// etc. flags are left exactly as they are while this is on (nothing
        /// resets), they just have nothing to show while the whole bar is gone;
        /// turning this back off brings back whichever of them were already
        /// enabled. Same reachability guarantee as hiding Settings' own button
        /// alone: Settings — and everything else on the bar — stays reachable via
        /// the tray icon regardless of this setting.</summary>
        public bool HideMenuBar { get; set; } = false;

        /// <summary>How each category's row of icons is packed horizontally — see
        /// RowAlignment.</summary>
        public RowAlignment IconAlignment { get; set; } = RowAlignment.Left;

        /// <summary>Rows (the original layout) or Columns.</summary>
        public LayoutDirection LayoutDirection { get; set; } = LayoutDirection.Rows;

        /// <summary>Rows layout: dragging the dock's left or right edge snaps its width to
        /// whole columns of icons, so rows end without a gap. Off: the width follows
        /// the mouse smoothly (handy with Center row alignment, where any leftover
        /// space is split evenly anyway). The Columns layout never snaps.</summary>
        public bool SnapDockWidth { get; set; } = true;

        /// <summary>Columns layout only: where a column that isn't full sits
        /// vertically. Rows uses IconAlignment instead (which still sets the Recent
        /// sections at the bottom, since those are always rows).</summary>
        public ColumnAlignment ColumnAlignment { get; set; } = ColumnAlignment.Top;

        /// <summary>Gap between categories in the Rows layout (below each one), in
        /// px. 2 is the original spacing. Category names sit inside each category,
        /// so even 0 never makes them overlap.</summary>
        public double RowCategorySpacing { get; set; } = 2;

        /// <summary>Gap between categories in the Columns layout (beside each one),
        /// in px. 16 is the original spacing. A column is always at least as wide as
        /// its name, so even 0 never makes names overlap.</summary>
        public double ColumnCategorySpacing { get; set; } = 16;

        /// <summary>When true, the search box starts each dock session hidden and its
        /// row collapsed — nothing lost functionally, since typing still reveals it
        /// instantly (see MainWindow's Window_PreviewTextInput) — for a cleaner look
        /// when paired with a low BackgroundOpacity.</summary>
        public bool HideSearchBar { get; set; } = false;

        /// <summary>When true, every category's banner (its name and, while empty
        /// and moused-over, its delete button) is collapsed — just the flowing rows
        /// of icons remain, no visible dividers between categories. Renaming a
        /// category and dragging its banner to reorder it are both unreachable
        /// while this is on, the same trade-off HideSearchBar makes for the search
        /// row (a visual simplification, not a removal of the underlying feature —
        /// turn it back off to get at them again).</summary>
        public bool HideCategoryBanners { get; set; } = false;

        /// <summary>When true, every tile's name label is collapsed, leaving just
        /// the icon. The tile itself always stays the same square size either way
        /// (see MainWindow.xaml's IconTileTemplate) — the icon graphic inside it
        /// grows to fill the label's old space instead (see MainWindow.xaml.cs's
        /// UpdateIconNameVisibility/IconGlyphSize), so a tile with its name hidden
        /// reads as a bigger, centered icon on a clean square rather than a
        /// normal-sized one with empty space left under it.</summary>
        public bool HideIconNames { get; set; } = false;

        /// <summary>When true, every icon tile shows a persistent, visible square
        /// background behind it, instead of only while actually moused over or
        /// pressed — see MainWindow.xaml's IconTileTemplate/DataTemplate.Triggers,
        /// and IconBackgroundColor below for which color it uses. Mainly useful
        /// for icons whose own artwork has a transparent background and otherwise
        /// reads as floating on nothing next to icons that do have one. Off by
        /// default, matching StartDock's original hover-only look.</summary>
        public bool ShowIconBackground { get; set; } = false;

        /// <summary>Which color ShowIconBackground's persistent tile background
        /// uses, when it's on. Theme (the default) is the same color the tile
        /// briefly shows on hover, and stays theme-aware through a live light/dark
        /// switch; Black/White instead pin it to a literal color regardless of
        /// theme, for a background that reads consistently against any icon
        /// artwork. Meaningless while ShowIconBackground itself is off.</summary>
        public IconBackgroundColorMode IconBackgroundColor { get; set; } = IconBackgroundColorMode.Theme;

        /// <summary>Width and height of one icon tile, in DIPs (see
        /// Views.MainWindow.xaml's IconTileTemplate) — the icon grid's own cell size
        /// (Views.FlowGridPanel.CellSize), and the icon graphic within each tile,
        /// both scale proportionally with it (see MainWindow.xaml.cs's
        /// ApplyAppearanceSettings/IconGlyphSize/IconCellSize). Default (92) matches
        /// StartDock's original, fixed tile size.</summary>
        public double IconSize { get; set; } = 92.0;

        /// <summary>Font size for a category's own name (see MainWindow.xaml's
        /// category banner). Independent of IconNameFontSize below — the two are
        /// deliberately separate settings so a user can, say, shrink icon names
        /// without also shrinking the category headers. Default (16) matches the
        /// original fixed value.</summary>
        public double CategoryFontSize { get; set; } = 16.0;

        /// <summary>Font size for an icon tile's own name label (see
        /// Views.MainWindow.xaml's IconTileTemplate) — independent of
        /// CategoryFontSize above. Default (11) matches the original fixed
        /// value.</summary>
        public double IconNameFontSize { get; set; } = 11.0;

        /// <summary>When true, a category's own name (the banner TextBox in
        /// MainWindow.xaml) renders Bold instead of its normal SemiBold — a further
        /// step up for anyone who wants category headers to stand out even more,
        /// independent of BoldIconNames below. See MainWindow.xaml.cs's
        /// CategoryNameFontWeight.</summary>
        public bool BoldCategoryNames { get; set; } = false;

        /// <summary>When true, an icon tile's own name label (MainWindow.xaml's
        /// IconTileTemplate) renders Bold instead of its normal weight —
        /// independent of BoldCategoryNames above, so either can be turned on
        /// without the other. See MainWindow.xaml.cs's IconNameFontWeight.</summary>
        public bool BoldIconNames { get; set; } = false;

        /// <summary>Persisted dock window size, in device-independent pixels.</summary>
        public double WindowWidth { get; set; } = 560;
        public double WindowHeight { get; set; } = 620;

        /// <summary>Launch StartDock's background hook/tray process at sign-in.</summary>
        public bool AutoStart { get; set; } = true;

        /// <summary>Adds "Pin to StartDock" to File Explorer's right-click menu for
        /// files and folders (Settings → Startup). Off by default — see
        /// Services/ExplorerMenuService.</summary>
        public bool ExplorerPinMenu { get; set; } = false;

        /// <summary>When true, StartDock relaunches itself elevated (UAC prompt) any
        /// time it starts without admin rights — see App.OnStartup. Useful for the
        /// same reason the tray's "Restart as Admin" exists (hooks/overlay reaching
        /// elevated fullscreen apps), without needing to do it by hand every launch.
        /// Declining the UAC prompt just leaves this launch running normally.</summary>
        public bool StartAsAdmin { get; set; } = false;

        /// <summary>Check GitHub for new versions (a minute after starting, then every
        /// six hours) and install them automatically while the dock isn't open — see
        /// Services/Updater.cs. Settings' "Check for updates" works either way.</summary>
        public bool AutoUpdate { get; set; } = true;

        // Version 2: the free-placement grid (DockIcon.SortOrder) was replaced by
        // named, flow-laid-out categories (see Categories above and
        // ConfigService.Load's migration).
        public const int SchemaVersion = 2;
        public int Version { get; set; } = SchemaVersion;
    }

    /// <summary>One entry in AppConfig.HiddenFromSearch: what it opens (matched
    /// against search results) and its name (for the Manage list).</summary>
    public class HiddenSearchItem
    {
        public string Name { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
    }
}
