using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using StartDock.Models;
using StartDock.Services;

namespace StartDock.Views
{
    public partial class MainWindow : Window
    {
        private readonly ConfigService _configService;
        private readonly IconExtractor _iconExtractor;
        private AppConfig _config;

        // The dock's top-level content: one CategoryViewModel per named,
        // independently-wrapping row (see Category/CategoryViewModel/FlowGridPanel).
        // Bound directly to CategoriesItemsControl.ItemsSource in the constructor —
        // populated later by LoadIconsFromConfig, same "bind the empty collection now,
        // fill it once Initialize runs" order the old _allIcons/_iconsView pair used.
        private readonly ObservableCollection<CategoryViewModel> _categories = new();

        // Guards against Window_Deactivated hiding the dock while a modal child
        // (Settings, the Add-icon file picker) is on screen and briefly steals activation.
        private bool _dialogOpen;

        /// <summary>True while a dialog (Settings, a file picker, …) is open from the
        /// dock — App waits for this to clear before installing an update.</summary>
        internal bool IsDialogOpen => _dialogOpen;

        // See AppConfig.ShowTaskbarOverFullscreen.
        private readonly FullscreenTaskbarService _fullscreenTaskbar = new();

        // Resize-drag state
        private bool _resizingTop;
        private bool _resizingRight;
        private Point _resizeStartPoint; // in physical screen pixels (see PointToScreen)
        private double _resizeDpiScaleX = 1.0, _resizeDpiScaleY = 1.0;
        private double _startHeight, _startWidth, _startTop;
        // 'new' here is deliberate, not a mistake: these are the resize-drag clamp
        // bounds used throughout this file, distinct from the inherited
        // FrameworkElement.MinWidth/MaxWidth/MinHeight/MaxHeight properties (which
        // this window doesn't set and which stay at their WPF defaults) — 'new'
        // just tells the compiler the name reuse is intentional so it stops
        // warning (CS0108) every unqualified reference here to the private const.
        private new const double MinWidth = 380, MaxWidth = 1100;
        private new const double MinHeight = 320, MaxHeight = 900;

        // How much of the window's own Width is "chrome" — everything that isn't the
        // icon grid itself (the search box row, the divider, the bottom bar,
        // BackgroundBorder/margins) — captured fresh at the start of each resize drag (see
        // RightResizeGrip_MouseLeftButtonDown) and used to snap the dragged edge to
        // whichever whole number of tile columns it's closest to (see SnapWidth), the
        // same "resize jumps between fixed grid-aligned sizes" feel the Windows 10
        // Start Menu has, rather than tracking the cursor pixel-for-pixel.
        //
        // There used to be a height counterpart to this (_chromeHeight/SnapHeight),
        // snapping the top edge to a whole number of tile *rows* the same way. That
        // stopped making sense once the grid became a vertical stack of independently
        // wrapping category rows (see FlowGridPanel) — there's no longer one single
        // "whole number of rows" the *window's total height* could cleanly land on,
        // since each category can be a different number of rows tall and the stack's
        // total height also includes a banner per category. So height-snapping was
        // dropped outright; the top-edge drag (see TopResizeGrip_MouseMove) is back to
        // a plain clamp, exactly like before either kind of snapping existed. Width
        // snapping still holds up fine, since column count is a single value shared
        // uniformly across every category.
        private double _chromeWidth;

        // Drag-to-reorder state (icon tiles, within/across categories)
        private Point _tileDragStartPoint;
        private DockIconViewModel? _tileDragCandidate;

        // Drag-to-reorder state (whole categories, via their banner)
        private Point _categoryDragStartPoint;
        private CategoryViewModel? _categoryDragCandidate;

        // Set while browsing inside a folder tile (see OpenFolder/CloseFolder) — the
        // search row swaps for a folder header and the grid shows the folder's contents
        // instead of the top-level category view.
        private DockIconViewModel? _openFolder;

        // Search-all-installed-apps state (see RenderSearchResults/SearchInstalledAppsAsync).
        // Shown instead of the category view whenever the search box has text and no
        // folder is open.
        private readonly ObservableCollection<DockIconViewModel> _searchResults = new();
        private ObservableCollection<InstalledAppViewModel>? _lastInstalledApps;

        /// <summary>Raised whenever a setting or the icon list changes and should be persisted/applied.</summary>
        public event Action<AppConfig>? ConfigChanged;

        public MainWindow(ConfigService configService, AppConfig config)
        {
            InitializeComponent();

            _configService = configService;
            _iconExtractor = new IconExtractor(configService);
            _config = config;

            CategoriesItemsControl.ItemsSource = _categories;
            ShowCategoryGrid();

            Width = _config.WindowWidth;
            Height = _config.WindowHeight;

            PowerFlyoutPopup.Closed += (_, _) => _dialogOpen = false;

            // Keeps the window's own rounded-rect clip region (see ApplyWindowRegion)
            // in sync with everything that can change its size or the monitor it's
            // on: SourceInitialized fires once the hwnd first exists (nothing to clip
            // before that), SizeChanged covers both a live resize-grip drag and a
            // Settings-driven Width/Height change, and DpiChanged covers the dock
            // moving to a monitor with a different scale factor (the region has to be
            // specified in that monitor's own device pixels).
            SourceInitialized += (_, _) => ApplyWindowRegion();
            SizeChanged += (_, _) => ApplyWindowRegion();
            DpiChanged += (_, _) => ApplyWindowRegion();
        }

        /// <summary>Switches the dock to the top-level category view, bound to
        /// <see cref="_categories"/> — the only one of the dock's three content
        /// sources (categories / search results / folder contents) that supports
        /// free reordering-by-drag (see CategoryIconsPanelTemplate/FlowGridPanel).</summary>
        private void ShowCategoryGrid()
        {
            CategoriesScrollViewer.Visibility = Visibility.Visible;
            FlatScrollViewer.Visibility = Visibility.Collapsed;
            UpdateIconNameVisibility();
        }

        /// <summary>Switches the dock to the plain, always-packed flow layout it's
        /// always used for search results or a folder's own contents — neither of
        /// which is organized into categories or supports free placement.
        /// isSearch just feeds UpdateIconNameVisibility below — see its own doc
        /// comment for why search specifically is exempt from HideIconNames.</summary>
        private void ShowFlowGrid(IEnumerable source, bool isSearch)
        {
            CategoriesScrollViewer.Visibility = Visibility.Collapsed;
            FlatScrollViewer.Visibility = Visibility.Visible;
            IconItemsControl.ItemsSource = source;
            _showingSearchResults = isSearch;
            UpdateIconNameVisibility();
        }

        // Set by ShowFlowGrid, read by UpdateIconNameVisibility — see that method.
        private bool _showingSearchResults;

        /// <summary>The single place IconNameVisibility (IconTileTemplate's shared
        /// name-label binding — see that property's own doc comment) ever gets set.
        /// AppConfig.HideIconNames is meant to declutter the main categories screen,
        /// not make a tile you specifically searched for illegible right after
        /// finding it — search results always show their names regardless of the
        /// setting. A folder's own contents, by contrast, are still "the main
        /// screen" in the sense that matters here (you're browsing your own pinned
        /// apps, not hunting for a specific one you already know the name of), so
        /// HideIconNames still applies there the same as the top-level grid.
        /// Called from every place that changes which view is showing
        /// (ShowCategoryGrid/ShowFlowGrid) and from ApplyAppearanceSettings itself,
        /// since HideIconNames can change while either view is already
        /// showing.
        ///
        /// Also the single place IconGlyphSize gets set — the tile itself always
        /// stays IconSize square (see IconTileTemplate's Button Width/Height, both
        /// bound straight to IconSize), but the icon graphic inside it grows from
        /// its normal proportion up to IconOnlyGlyphRatio's larger one whenever the
        /// label beneath it is collapsed, so the icon actually uses the space the
        /// text would have — rather than sitting at its normal size with empty
        /// space left below it. IconTileTemplate's StackPanel is vertically
        /// centered (see its own Margin/VerticalAlignment in MainWindow.xaml), so a
        /// tile with just the (now-larger) glyph in it reads as a centered icon on
        /// a clean square, not an icon pinned to the top of one.</summary>
        private void UpdateIconNameVisibility()
        {
            bool forceVisible = FlatScrollViewer.Visibility == Visibility.Visible && _showingSearchResults;
            bool visible = forceVisible || !_config.HideIconNames;
            IconNameVisibility = visible ? Visibility.Visible : Visibility.Collapsed;

            IconGlyphSize = Math.Round(_config.IconSize * (visible ? IconGlyphRatio : IconOnlyGlyphRatio));
        }

        /// <summary>
        /// Loads the icon grid and user info. Called by App after it has subscribed to
        /// <see cref="ConfigChanged"/>, so that any icon-cache paths resolved during the
        /// initial load (see LoadIconImageAsync) get persisted instead of silently dropped.
        /// </summary>
        public void Initialize()
        {
            LoadIconsFromConfig();
            RefreshUserInfo();
            ApplyAppearanceSettings();

            // Warm the installed-apps cache right away, in the background, so the
            // Get-StartApps round trip and icon extraction are already done (or well
            // underway) by the time the user's first search or "+" tile click needs
            // them — instead of paying that cost right there, mid-interaction.
            _ = WarmInstalledAppsCacheAsync();
        }

        /// <summary>Row alignment for every category's icons — a plain
        /// DependencyProperty set once, here on MainWindow itself (see
        /// ApplyAppearanceSettings), and consumed from deep inside the per-category
        /// DataTemplate via an explicit RelativeSource AncestorType binding on each
        /// FlowGridPanel (see MainWindow.xaml's CategoryIconsPanelTemplate). An
        /// earlier version of this tried to push the value down via WPF property-
        /// value inheritance from CategoriesItemsControl instead — simpler in
        /// theory, but it didn't reliably reach the dynamically-templated panels in
        /// practice, so this explicit-binding approach replaced it.</summary>
        public static readonly DependencyProperty IconAlignmentProperty =
            DependencyProperty.Register(nameof(IconAlignment), typeof(RowAlignment), typeof(MainWindow),
                new FrameworkPropertyMetadata(RowAlignment.Left));

        public RowAlignment IconAlignment
        {
            get => (RowAlignment)GetValue(IconAlignmentProperty);
            set => SetValue(IconAlignmentProperty, value);
        }

        /// <summary>A category banner's own name TextBox binds its TextAlignment
        /// here (see MainWindow.xaml) rather than to IconAlignment directly —
        /// RowAlignment and TextAlignment are two different enums (RowAlignment:
        /// Left/Center/Right; TextAlignment: Left/Right/Center/Justify, note the
        /// different order) so a plain cast between them would silently swap
        /// Center and Right. Computed from IconAlignment in ApplyAppearanceSettings,
        /// the same single place every other derived per-toggle property here is
        /// set.</summary>
        public static readonly DependencyProperty CategoryNameAlignmentProperty =
            DependencyProperty.Register(nameof(CategoryNameAlignment), typeof(TextAlignment), typeof(MainWindow),
                new FrameworkPropertyMetadata(TextAlignment.Left));

        public TextAlignment CategoryNameAlignment
        {
            get => (TextAlignment)GetValue(CategoryNameAlignmentProperty);
            set => SetValue(CategoryNameAlignmentProperty, value);
        }

        // ---- Rows / Columns (AppConfig.LayoutDirection) — see ApplyLayoutDirection.

        /// <summary>True in Columns: each category's FlowGridPanel runs its icons top
        /// to bottom (FlowGridPanel.IsVertical).</summary>
        public static readonly DependencyProperty IsColumnLayoutProperty =
            DependencyProperty.Register(nameof(IsColumnLayout), typeof(bool), typeof(MainWindow),
                new FrameworkPropertyMetadata(false));
        public bool IsColumnLayout
        {
            get => (bool)GetValue(IsColumnLayoutProperty);
            set => SetValue(IsColumnLayoutProperty, value);
        }

        /// <summary>How each category's own icons are packed (FlowGridPanel.Alignment):
        /// IconAlignment in Rows; AppConfig.ColumnAlignment in Columns, as
        /// Left = Top, Right = Bottom. The Recent sections keep using IconAlignment,
        /// since they're rows in both layouts.</summary>
        public static readonly DependencyProperty CategoryIconAlignmentProperty =
            DependencyProperty.Register(nameof(CategoryIconAlignment), typeof(RowAlignment), typeof(MainWindow),
                new FrameworkPropertyMetadata(RowAlignment.Left));
        public RowAlignment CategoryIconAlignment
        {
            get => (RowAlignment)GetValue(CategoryIconAlignmentProperty);
            set => SetValue(CategoryIconAlignmentProperty, value);
        }

        /// <summary>The gap after each category: 2px below it in Rows (the original
        /// value), 16px to its right in Columns.</summary>
        public static readonly DependencyProperty CategoryItemMarginProperty =
            DependencyProperty.Register(nameof(CategoryItemMargin), typeof(Thickness), typeof(MainWindow),
                new FrameworkPropertyMetadata(new Thickness(0, 0, 0, 2)));
        public Thickness CategoryItemMargin
        {
            get => (Thickness)GetValue(CategoryItemMarginProperty);
            set => SetValue(CategoryItemMarginProperty, value);
        }

        /// <summary>Which row of a category the name banner sits in: 0 (above the
        /// icons) normally; 2 (below them) in Columns with Column alignment Bottom,
        /// so the name stays next to icons that sit at the bottom of the dock.</summary>
        public static readonly DependencyProperty CategoryBannerRowProperty =
            DependencyProperty.Register(nameof(CategoryBannerRow), typeof(int), typeof(MainWindow),
                new FrameworkPropertyMetadata(0));
        public int CategoryBannerRow
        {
            get => (int)GetValue(CategoryBannerRowProperty);
            set => SetValue(CategoryBannerRowProperty, value);
        }

        /// <summary>Where the Columns category-reorder drop bars sit: centered in the
        /// gap to the left/right of a category (see ApplyLayoutDirection).</summary>
        public static readonly DependencyProperty CategoryDropBeforeMarginProperty =
            DependencyProperty.Register(nameof(CategoryDropBeforeMargin), typeof(Thickness), typeof(MainWindow),
                new FrameworkPropertyMetadata(new Thickness(-9, 4, 0, 4)));
        public Thickness CategoryDropBeforeMargin
        {
            get => (Thickness)GetValue(CategoryDropBeforeMarginProperty);
            set => SetValue(CategoryDropBeforeMarginProperty, value);
        }

        public static readonly DependencyProperty CategoryDropAfterMarginProperty =
            DependencyProperty.Register(nameof(CategoryDropAfterMargin), typeof(Thickness), typeof(MainWindow),
                new FrameworkPropertyMetadata(new Thickness(0, 4, -9, 4)));
        public Thickness CategoryDropAfterMargin
        {
            get => (Thickness)GetValue(CategoryDropAfterMarginProperty);
            set => SetValue(CategoryDropAfterMarginProperty, value);
        }

        /// <summary>Stretch in Rows (the original). In Columns a category can be
        /// wider than its icons (a long name), so its icons are centered under it.</summary>
        public static readonly DependencyProperty CategoryIconsHorizontalAlignmentProperty =
            DependencyProperty.Register(nameof(CategoryIconsHorizontalAlignment), typeof(HorizontalAlignment), typeof(MainWindow),
                new FrameworkPropertyMetadata(HorizontalAlignment.Stretch));
        public HorizontalAlignment CategoryIconsHorizontalAlignment
        {
            get => (HorizontalAlignment)GetValue(CategoryIconsHorizontalAlignmentProperty);
            set => SetValue(CategoryIconsHorizontalAlignmentProperty, value);
        }

        /// <summary>A category banner's name alignment: the same as
        /// CategoryNameAlignment in Rows; centered over its column in Columns, where
        /// Left/Center/Right picks the icons' vertical position instead. Separate from
        /// CategoryNameAlignment because the Recent section headers keep using that
        /// one — those stay rows in both layouts.</summary>
        public static readonly DependencyProperty CategoryBannerAlignmentProperty =
            DependencyProperty.Register(nameof(CategoryBannerAlignment), typeof(TextAlignment), typeof(MainWindow),
                new FrameworkPropertyMetadata(TextAlignment.Left));
        public TextAlignment CategoryBannerAlignment
        {
            get => (TextAlignment)GetValue(CategoryBannerAlignmentProperty);
            set => SetValue(CategoryBannerAlignmentProperty, value);
        }

        /// <summary>Same RelativeSource-binding pattern as IconAlignment above —
        /// collapses every category's banner (name + delete button) when
        /// HideCategoryBanners is on. Icons keep working exactly as normal; only
        /// renaming and drag-to-reorder-categories (both banner-driven) become
        /// unreachable while it's collapsed, the same trade-off HideSearchBar makes
        /// for the search row.</summary>
        public static readonly DependencyProperty CategoryBannerVisibilityProperty =
            DependencyProperty.Register(nameof(CategoryBannerVisibility), typeof(Visibility), typeof(MainWindow),
                new FrameworkPropertyMetadata(Visibility.Visible));

        public Visibility CategoryBannerVisibility
        {
            get => (Visibility)GetValue(CategoryBannerVisibilityProperty);
            set => SetValue(CategoryBannerVisibilityProperty, value);
        }

        /// <summary>Same pattern again — collapses every tile's name label
        /// (IconTileTemplate's TextBlock) when HideIconNames is on. The tile itself
        /// stays the same size either way (see IconSize/IconSizeProperty below;
        /// this toggle doesn't touch it), so hiding the label just leaves blank
        /// space beneath the icon rather than shrinking the tile or the grid's
        /// CellSize.
        ///
        /// IconTileTemplate is the one shared DataTemplate behind every icon tile
        /// anywhere in the dock — the categories grid, a folder's contents, and
        /// search results alike — so this single property is also what search
        /// results and folder tiles bind to. HideIconNames is meant to declutter
        /// just the main categories screen, not make an app you searched for or
        /// opened a folder to find illegible once you've actually found it, so
        /// this doesn't just mirror AppConfig.HideIconNames directly any more —
        /// see UpdateIconNameVisibility, which is now the only thing that ever
        /// sets this, and which forces it back to Visible whenever the flat
        /// (search/folder) view is what's actually showing.</summary>
        public static readonly DependencyProperty IconNameVisibilityProperty =
            DependencyProperty.Register(nameof(IconNameVisibility), typeof(Visibility), typeof(MainWindow),
                new FrameworkPropertyMetadata(Visibility.Visible));

        public Visibility IconNameVisibility
        {
            get => (Visibility)GetValue(IconNameVisibilityProperty);
            set => SetValue(IconNameVisibilityProperty, value);
        }

        /// <summary>Same RelativeSource-binding pattern as IconNameVisibility above —
        /// shows or hides MainWindow.xaml's NativeStartMenuButton per
        /// AppConfig.ShowNativeStartMenuButton.</summary>
        public static readonly DependencyProperty ShowNativeStartMenuButtonProperty =
            DependencyProperty.Register(nameof(ShowNativeStartMenuButton), typeof(Visibility), typeof(MainWindow),
                new FrameworkPropertyMetadata(Visibility.Visible));

        public Visibility ShowNativeStartMenuButton
        {
            get => (Visibility)GetValue(ShowNativeStartMenuButtonProperty);
            set => SetValue(ShowNativeStartMenuButtonProperty, value);
        }

        // Same RelativeSource-binding pattern again, one per remaining menu-bar
        // utility button (see MainWindow.xaml's MenuBarRow and
        // AppConfig.ShowTaskManagerButton/etc.) — four near-identical
        // DependencyProperty pairs rather than something more clever (a converter
        // keyed by button name, say) to stay consistent with every other per-toggle
        // property already on this window (IconNameVisibility, CategoryBannerVisibility,
        // ShowNativeStartMenuButton above), all following the exact same shape.
        /// <summary>Tiles for the automatic "Recently added", "Recently used" and
        /// "Recent files" sections at the bottom of the dock, or all three together in
        /// one "Recent" section (AppConfig.CombineRecentSections) — see UpdateAutoSections.</summary>
        public ObservableCollection<DockIconViewModel> NewAppTiles { get; } = new();
        public ObservableCollection<DockIconViewModel> RecentAppTiles { get; } = new();
        public ObservableCollection<DockIconViewModel> RecentFileTiles { get; } = new();
        public ObservableCollection<DockIconViewModel> CombinedRecentTiles { get; } = new();

        public static readonly DependencyProperty RecentAppsVisibilityProperty =
            DependencyProperty.Register(nameof(RecentAppsVisibility), typeof(Visibility), typeof(MainWindow),
                new FrameworkPropertyMetadata(Visibility.Collapsed));
        public Visibility RecentAppsVisibility
        {
            get => (Visibility)GetValue(RecentAppsVisibilityProperty);
            set => SetValue(RecentAppsVisibilityProperty, value);
        }

        public static readonly DependencyProperty CombinedRecentVisibilityProperty =
            DependencyProperty.Register(nameof(CombinedRecentVisibility), typeof(Visibility), typeof(MainWindow),
                new FrameworkPropertyMetadata(Visibility.Collapsed));
        public Visibility CombinedRecentVisibility
        {
            get => (Visibility)GetValue(CombinedRecentVisibilityProperty);
            set => SetValue(CombinedRecentVisibilityProperty, value);
        }

        /// <summary>IconTileTemplate's TileBg opacity, 0–1 — see AppConfig.IconBackgroundOpacity.</summary>
        public static readonly DependencyProperty IconTileOpacityProperty =
            DependencyProperty.Register(nameof(IconTileOpacity), typeof(double), typeof(MainWindow),
                new FrameworkPropertyMetadata(1.0));
        public double IconTileOpacity
        {
            get => (double)GetValue(IconTileOpacityProperty);
            set => SetValue(IconTileOpacityProperty, value);
        }

        public static readonly DependencyProperty NewAppsVisibilityProperty =
            DependencyProperty.Register(nameof(NewAppsVisibility), typeof(Visibility), typeof(MainWindow),
                new FrameworkPropertyMetadata(Visibility.Collapsed));
        public Visibility NewAppsVisibility
        {
            get => (Visibility)GetValue(NewAppsVisibilityProperty);
            set => SetValue(NewAppsVisibilityProperty, value);
        }

        public static readonly DependencyProperty RecentFilesVisibilityProperty =
            DependencyProperty.Register(nameof(RecentFilesVisibility), typeof(Visibility), typeof(MainWindow),
                new FrameworkPropertyMetadata(Visibility.Collapsed));
        public Visibility RecentFilesVisibility
        {
            get => (Visibility)GetValue(RecentFilesVisibilityProperty);
            set => SetValue(RecentFilesVisibilityProperty, value);
        }

        public static readonly DependencyProperty ShowUserButtonProperty =
            DependencyProperty.Register(nameof(ShowUserButton), typeof(Visibility), typeof(MainWindow),
                new FrameworkPropertyMetadata(Visibility.Visible));
        public Visibility ShowUserButton
        {
            get => (Visibility)GetValue(ShowUserButtonProperty);
            set => SetValue(ShowUserButtonProperty, value);
        }

        /// <summary>DividerBorder's own visibility: hidden along with the whole menu
        /// bar (HideMenuBar), or on its own via AppConfig.ShowMenuBarDivider.</summary>
        public static readonly DependencyProperty DividerVisibilityProperty =
            DependencyProperty.Register(nameof(DividerVisibility), typeof(Visibility), typeof(MainWindow),
                new FrameworkPropertyMetadata(Visibility.Visible));
        public Visibility DividerVisibility
        {
            get => (Visibility)GetValue(DividerVisibilityProperty);
            set => SetValue(DividerVisibilityProperty, value);
        }

        public static readonly DependencyProperty ShowAddButtonProperty =
            DependencyProperty.Register(nameof(ShowAddButton), typeof(Visibility), typeof(MainWindow),
                new FrameworkPropertyMetadata(Visibility.Visible));
        public Visibility ShowAddButton
        {
            get => (Visibility)GetValue(ShowAddButtonProperty);
            set => SetValue(ShowAddButtonProperty, value);
        }

        public static readonly DependencyProperty ShowTaskManagerButtonProperty =
            DependencyProperty.Register(nameof(ShowTaskManagerButton), typeof(Visibility), typeof(MainWindow),
                new FrameworkPropertyMetadata(Visibility.Visible));
        public Visibility ShowTaskManagerButton
        {
            get => (Visibility)GetValue(ShowTaskManagerButtonProperty);
            set => SetValue(ShowTaskManagerButtonProperty, value);
        }

        public static readonly DependencyProperty ShowVolumeMixerButtonProperty =
            DependencyProperty.Register(nameof(ShowVolumeMixerButton), typeof(Visibility), typeof(MainWindow),
                new FrameworkPropertyMetadata(Visibility.Visible));
        public Visibility ShowVolumeMixerButton
        {
            get => (Visibility)GetValue(ShowVolumeMixerButtonProperty);
            set => SetValue(ShowVolumeMixerButtonProperty, value);
        }

        public static readonly DependencyProperty ShowCalculatorButtonProperty =
            DependencyProperty.Register(nameof(ShowCalculatorButton), typeof(Visibility), typeof(MainWindow),
                new FrameworkPropertyMetadata(Visibility.Visible));
        public Visibility ShowCalculatorButton
        {
            get => (Visibility)GetValue(ShowCalculatorButtonProperty);
            set => SetValue(ShowCalculatorButtonProperty, value);
        }

        public static readonly DependencyProperty ShowFileExplorerButtonProperty =
            DependencyProperty.Register(nameof(ShowFileExplorerButton), typeof(Visibility), typeof(MainWindow),
                new FrameworkPropertyMetadata(Visibility.Visible));
        public Visibility ShowFileExplorerButton
        {
            get => (Visibility)GetValue(ShowFileExplorerButtonProperty);
            set => SetValue(ShowFileExplorerButtonProperty, value);
        }

        public static readonly DependencyProperty ShowSettingsButtonProperty =
            DependencyProperty.Register(nameof(ShowSettingsButton), typeof(Visibility), typeof(MainWindow),
                new FrameworkPropertyMetadata(Visibility.Visible));
        public Visibility ShowSettingsButton
        {
            get => (Visibility)GetValue(ShowSettingsButtonProperty);
            set => SetValue(ShowSettingsButtonProperty, value);
        }

        public static readonly DependencyProperty ShowPowerButtonProperty =
            DependencyProperty.Register(nameof(ShowPowerButton), typeof(Visibility), typeof(MainWindow),
                new FrameworkPropertyMetadata(Visibility.Visible));
        public Visibility ShowPowerButton
        {
            get => (Visibility)GetValue(ShowPowerButtonProperty);
            set => SetValue(ShowPowerButtonProperty, value);
        }

        /// <summary>Master switch for the whole menu bar (see AppConfig.HideMenuBar) —
        /// bound in MainWindow.xaml to both MenuBarRow's own Visibility and
        /// DividerBorder's (there's nothing left to divide once the bar it sits next
        /// to is gone). Both rows are Grid.RowDefinition Height="Auto" in ContentGrid
        /// (rebuilt by ApplyMenuBarPosition above, whichever side the bar's on), so
        /// collapsing them needs nothing beyond this one Visibility flip — an Auto row
        /// whose only child is Collapsed measures to zero height on its own, and the
        /// icon grid's "*" row simply reclaims the space, the same free ride
        /// HideSearchBar's row-collapse already gets.</summary>
        public static readonly DependencyProperty MenuBarVisibilityProperty =
            DependencyProperty.Register(nameof(MenuBarVisibility), typeof(Visibility), typeof(MainWindow),
                new FrameworkPropertyMetadata(Visibility.Visible));
        public Visibility MenuBarVisibility
        {
            get => (Visibility)GetValue(MenuBarVisibilityProperty);
            set => SetValue(MenuBarVisibilityProperty, value);
        }

        /// <summary>An icon tile's own Width/Height, in DIPs (see MainWindow.xaml's
        /// IconTileTemplate, bound here the same explicit-RelativeSource way as
        /// every other per-template setting on this window) — always square,
        /// whether or not the tile's name label is currently showing (see
        /// IconGlyphSize below for what changes instead). IconCellSize below is
        /// derived from this one value — see ApplyAppearanceSettings, which is the
        /// only place either is ever set — so a single Settings slider drives tile
        /// size and the grid's own cell size together, proportionally, rather than
        /// needing two separate knobs.</summary>
        public static readonly DependencyProperty IconSizeProperty =
            DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(MainWindow),
                new FrameworkPropertyMetadata(92.0));

        public double IconSize
        {
            get => (double)GetValue(IconSizeProperty);
            set => SetValue(IconSizeProperty, value);
        }

        /// <summary>The icon graphic's own Width/Height within a tile
        /// (IconTileTemplate's inner Grid, which the single-icon Image and the
        /// folder-preview both fill; the StackPanel around it is vertically
        /// centered, so this stays centered in the tile regardless of its size).
        /// Set in UpdateIconNameVisibility, not ApplyAppearanceSettings, since it
        /// depends on more than just IconSize now: IconGlyphRatio (the original
        /// fixed proportion — a 40px glyph inside a 92px tile) while the tile's
        /// name label is showing, or the larger IconOnlyGlyphRatio once
        /// AppConfig.HideIconNames collapses that label and leaves the glyph the
        /// whole tile to itself — a plain size bump rather than shrinking the tile
        /// down around the glyph (which is what this used to do; that made a
        /// tile's own background — see AppConfig.ShowIconBackground — a
        /// non-square rectangle instead of the clean square it's meant to
        /// be).</summary>
        public static readonly DependencyProperty IconGlyphSizeProperty =
            DependencyProperty.Register(nameof(IconGlyphSize), typeof(double), typeof(MainWindow),
                new FrameworkPropertyMetadata(40.0));

        public double IconGlyphSize
        {
            get => (double)GetValue(IconGlyphSizeProperty);
            set => SetValue(IconGlyphSizeProperty, value);
        }

        /// <summary>IconGlyphSize's proportion of IconSize while a tile's name
        /// label is showing — the original fixed values (a 40px glyph inside a
        /// 92px tile) preserved as a ratio, so IconSize's own default (92) still
        /// reproduces today's look pixel-for-pixel, with every other IconSize
        /// value scaling from it.</summary>
        private const double IconGlyphRatio = 40.0 / 92.0;

        /// <summary>IconGlyphSize's proportion of IconSize once the tile's name
        /// label is collapsed (see UpdateIconNameVisibility) — deliberately larger
        /// than IconGlyphRatio above, since the glyph now has the label's old
        /// space to itself instead of needing to leave room under it. Chosen to
        /// read as a modest, clearly-intentional size bump rather than either no
        /// visible change or the glyph swelling to fill the whole tile edge to
        /// edge.</summary>
        private const double IconOnlyGlyphRatio = 52.0 / 92.0;

        /// <summary>FlowGridPanel.CellSize for every category's icon grid (see
        /// MainWindow.xaml's CategoryIconsPanelTemplate) — must always equal
        /// IconSize plus the tile's own fixed 4px margin on each side (see
        /// IconTileTemplate), the same relationship FlowGridPanel.CellSize's own
        /// doc comment already calls out. Also what SnapWidth (see the resizing
        /// region below) snaps the dock's width to a whole number of columns of,
        /// so a resize keeps landing on a fixed grid regardless of IconSize.</summary>
        public static readonly DependencyProperty IconCellSizeProperty =
            DependencyProperty.Register(nameof(IconCellSize), typeof(double), typeof(MainWindow),
                new FrameworkPropertyMetadata(100.0));

        public double IconCellSize
        {
            get => (double)GetValue(IconCellSizeProperty);
            set => SetValue(IconCellSizeProperty, value);
        }

        /// <summary>Font size for a category's own name (MainWindow.xaml's category
        /// banner TextBox) — see AppConfig.CategoryFontSize for why this is kept
        /// independent of IconNameFontSize below.</summary>
        public static readonly DependencyProperty CategoryFontSizeProperty =
            DependencyProperty.Register(nameof(CategoryFontSize), typeof(double), typeof(MainWindow),
                new FrameworkPropertyMetadata(16.0));

        public double CategoryFontSize
        {
            get => (double)GetValue(CategoryFontSizeProperty);
            set => SetValue(CategoryFontSizeProperty, value);
        }

        /// <summary>Font size for an icon tile's own name label (MainWindow.xaml's
        /// IconTileTemplate) — see AppConfig.IconNameFontSize for why this is kept
        /// independent of CategoryFontSize above.</summary>
        public static readonly DependencyProperty IconNameFontSizeProperty =
            DependencyProperty.Register(nameof(IconNameFontSize), typeof(double), typeof(MainWindow),
                new FrameworkPropertyMetadata(11.0));

        public double IconNameFontSize
        {
            get => (double)GetValue(IconNameFontSizeProperty);
            set => SetValue(IconNameFontSizeProperty, value);
        }

        /// <summary>Font weight for a category's own name (MainWindow.xaml's
        /// category banner TextBox) — SemiBold by default (the fixed value that
        /// TextBox's FontWeight used to be hardcoded to, before
        /// AppConfig.BoldCategoryNames existed, preserved here as this property's
        /// own default so nobody sees a different look just from upgrading), Bold
        /// instead when that setting is on.</summary>
        public static readonly DependencyProperty CategoryNameFontWeightProperty =
            DependencyProperty.Register(nameof(CategoryNameFontWeight), typeof(FontWeight), typeof(MainWindow),
                new FrameworkPropertyMetadata(FontWeights.SemiBold));

        public FontWeight CategoryNameFontWeight
        {
            get => (FontWeight)GetValue(CategoryNameFontWeightProperty);
            set => SetValue(CategoryNameFontWeightProperty, value);
        }

        /// <summary>Font weight for an icon tile's own name label (MainWindow.xaml's
        /// IconTileTemplate) — Normal by default, Bold instead when
        /// AppConfig.BoldIconNames is on. Independent of CategoryNameFontWeight
        /// above, the same way IconNameFontSize is kept independent of
        /// CategoryFontSize.</summary>
        public static readonly DependencyProperty IconNameFontWeightProperty =
            DependencyProperty.Register(nameof(IconNameFontWeight), typeof(FontWeight), typeof(MainWindow),
                new FrameworkPropertyMetadata(FontWeights.Normal));

        public FontWeight IconNameFontWeight
        {
            get => (FontWeight)GetValue(IconNameFontWeightProperty);
            set => SetValue(IconNameFontWeightProperty, value);
        }

        /// <summary>Max height for an icon tile's own name label (MainWindow.xaml's
        /// IconTileTemplate) — used to be a flat 30 regardless of IconNameFontSize/
        /// IconNameFontWeight above, tight enough at the 11pt Normal default that a
        /// second wrapped line at Bold weight rendered right at that edge and had
        /// its descenders clipped. Recomputed from the actual font size in use
        /// (see ComputeIconNameMaxHeight, called from ApplyAppearanceSettings right
        /// alongside IconNameFontSize itself) so there's always room for two lines
        /// at whatever size/weight Settings has picked, not just the default.</summary>
        public static readonly DependencyProperty IconNameMaxHeightProperty =
            DependencyProperty.Register(nameof(IconNameMaxHeight), typeof(double), typeof(MainWindow),
                new FrameworkPropertyMetadata(30.0));

        public double IconNameMaxHeight
        {
            get => (double)GetValue(IconNameMaxHeightProperty);
            set => SetValue(IconNameMaxHeightProperty, value);
        }

        /// <summary>MaxHeight for an icon tile's name label: exactly enough for two
        /// full wrapped lines, or — if the tile genuinely can't fit two — whatever
        /// room it does have, in which case TextTrimming="CharacterEllipsis" shows
        /// one line ending in "…" instead.
        ///
        /// How WPF behaves here decides the shape of this. With TextWrapping="Wrap"
        /// plus CharacterEllipsis, a TextBlock only renders a wrapped line if the
        /// whole line fits under MaxHeight; one that would be even slightly cut is
        /// dropped, and the ellipsis moves up onto the line before it. So MaxHeight
        /// has to be at least two real line heights for "Google" / "Chrome" to
        /// show — any less and it collapses straight to "Google Ch…". And it must
        /// never be more than the tile can actually hold, because the tile is a
        /// fixed IconSize square whose AppTileButton template wraps content in a
        /// rounded Border, and WPF auto-clips a rounded Border's content: asking
        /// for more than fits gets a hard pixel cut through the second line instead
        /// (the original "bottom of the second line is cut off" bug).
        ///
        /// Line height: Segoe UI (this app's default UI font) has a LineSpacing of
        /// about 1.33 em, so two lines are 2 x 1.33 x fontSize — 29.3px at the
        /// default 11 — measured at ~14.6px per line in real screenshots. +1 covers
        /// rounding to whole pixels.
        ///
        /// Room: the tile square (IconSize), minus TileButton's vertical Padding (2
        /// top + 2 bottom — MainWindow.xaml's IconTileTemplate), minus the icon
        /// glyph (IconGlyphSize) and its own Margin (3 above + 3 below). Keep those
        /// literals in sync with that XAML. At a 74px tile (glyph 32) that's 32px —
        /// enough for two full lines at 11pt with a little to spare; the older,
        /// roomier padding/margins only left 22-26px there, which is why names were
        /// collapsing to one line on smaller tile sizes even after the clip fix.
        /// </summary>
        private double ComputeIconNameMaxHeight(double fontSize)
        {
            double twoLines = Math.Ceiling(fontSize * 1.33 * 2) + 1;

            double glyphAreaHeight = Math.Round(_config.IconSize * IconGlyphRatio) + 6;
            double available = Math.Max(0, _config.IconSize - 4 - glyphAreaHeight);

            return Math.Min(twoLines, available);
        }

        /// <summary>Whether an icon tile shows a persistent, visible background
        /// (AppTileButton's own hover color by default, shown all the time
        /// instead of only on hover/press — see IconBackgroundColor below for
        /// picking a different color) — see MainWindow.xaml's IconTileTemplate,
        /// whose DataTemplate.Triggers is the one place this is actually
        /// consumed, and AppConfig.ShowIconBackground.</summary>
        public static readonly DependencyProperty ShowIconBackgroundProperty =
            DependencyProperty.Register(nameof(ShowIconBackground), typeof(bool), typeof(MainWindow),
                new FrameworkPropertyMetadata(false));

        public bool ShowIconBackground
        {
            get => (bool)GetValue(ShowIconBackgroundProperty);
            set => SetValue(ShowIconBackgroundProperty, value);
        }

        /// <summary>Which color ShowIconBackground's persistent tile background
        /// uses, when it's on — Theme (the default) keeps it DynamicResource-
        /// driven off the normal hover color, so it follows a live theme switch
        /// automatically the same way most of this window's other colors do;
        /// Black/White pin it to a literal color instead, regardless of theme.
        /// See MainWindow.xaml's IconTileTemplate/DataTemplate.Triggers, the one
        /// place this is actually consumed (as a set of MultiDataTrigger
        /// conditions alongside ShowIconBackground, entirely in XAML — there's no
        /// C#-side brush computation for this the way ApplyBorderStyle's
        /// Black/White choice needs, since Theme mode's DynamicResource lookup
        /// only works declared directly in markup). See
        /// AppConfig.IconBackgroundColor.</summary>
        public static readonly DependencyProperty IconBackgroundColorProperty =
            DependencyProperty.Register(nameof(IconBackgroundColor), typeof(IconBackgroundColorMode), typeof(MainWindow),
                new FrameworkPropertyMetadata(IconBackgroundColorMode.Theme));

        public IconBackgroundColorMode IconBackgroundColor
        {
            get => (IconBackgroundColorMode)GetValue(IconBackgroundColorProperty);
            set => SetValue(IconBackgroundColorProperty, value);
        }

        /// <summary>Applies the appearance-related settings that Settings can change
        /// live (background opacity; whether the search bar starts hidden; row
        /// alignment; hidden category banners/icon names; icon/grid size; category
        /// and icon-name font sizes; menu bar position) without needing a restart —
        /// called once at startup (see Initialize) and again right after OpenSettings
        /// applies a newly-saved config. Dock position (DockPosition, the whole
        /// window's screen placement) isn't handled here since it only actually
        /// matters at the moment the dock is (re)shown — see PositionDock, called
        /// from ShowDock — though OpenSettings does also re-run PositionDock itself
        /// if the dock happens to be visible right now, so a position change is
        /// reflected immediately too. Menu bar position (top vs. bottom *within* the
        /// dock, unrelated to DockPosition) is handled here instead, via
        /// ApplyMenuBarPosition, since it's a layout change to the dock's own content
        /// rather than to where the window sits on screen.</summary>
        private void ApplyAppearanceSettings()
        {
            // Sets *what* BackgroundBorder shows (a picture, or the plain themeable
            // color) before the Opacity fade right below touches *how visible* that
            // is — see ApplyBackgroundImage's own comment for why this has to run
            // first rather than the other way round.
            ApplyBackgroundImage();
            ApplyFrostedBackground();
            ApplyBorderStyle();
            ApplyTextColor();

            // Fades BackgroundBorder alone — MenuBarRow has no background fill of its
            // own any more (see its comment in MainWindow.xaml), so this single fade
            // already covers the menu bar's background too, wherever ApplyMenuBarPosition
            // below has it sitting.
            BackgroundBorder.Opacity = Math.Clamp(_config.BackgroundOpacity / 100.0, 0.0, 1.0);

            ApplyMenuBarPosition();

            IconAlignment = _config.IconAlignment;
            CategoryNameAlignment = _config.IconAlignment switch
            {
                RowAlignment.Center => TextAlignment.Center,
                RowAlignment.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            };
            CategoryBannerVisibility = _config.HideCategoryBanners ? Visibility.Collapsed : Visibility.Visible;
            ApplyLayoutDirection();

            // The +8 ratio here is the original fixed value (a 92px tile with a
            // 4px margin on each side) — preserved exactly so IconSize's own
            // default (92) reproduces today's look pixel-for-pixel, with every
            // other IconSize value scaling from it. IconGlyphSize isn't set here —
            // see UpdateIconNameVisibility, which computes it fresh every time
            // (including right below) since it depends on more than just IconSize.
            IconSize = _config.IconSize;
            IconCellSize = _config.IconSize + 8;

            UpdateIconNameVisibility();
            ShowNativeStartMenuButton = _config.ShowNativeStartMenuButton ? Visibility.Visible : Visibility.Collapsed;
            ShowTaskManagerButton = _config.ShowTaskManagerButton ? Visibility.Visible : Visibility.Collapsed;
            ShowVolumeMixerButton = _config.ShowVolumeMixerButton ? Visibility.Visible : Visibility.Collapsed;
            ShowFileExplorerButton = _config.ShowFileExplorerButton ? Visibility.Visible : Visibility.Collapsed;
            ShowCalculatorButton = _config.ShowCalculatorButton ? Visibility.Visible : Visibility.Collapsed;
            ShowSettingsButton = _config.ShowSettingsButton ? Visibility.Visible : Visibility.Collapsed;
            ShowPowerButton = _config.ShowPowerButton ? Visibility.Visible : Visibility.Collapsed;
            ShowAddButton = _config.ShowAddButton ? Visibility.Visible : Visibility.Collapsed;
            ShowUserButton = _config.ShowUserButton ? Visibility.Visible : Visibility.Collapsed;
            MenuBarVisibility = _config.HideMenuBar ? Visibility.Collapsed : Visibility.Visible;
            DividerVisibility = !_config.HideMenuBar && _config.ShowMenuBarDivider ? Visibility.Visible : Visibility.Collapsed;

            CategoryFontSize = _config.CategoryFontSize;
            IconNameFontSize = _config.IconNameFontSize;
            IconNameMaxHeight = ComputeIconNameMaxHeight(_config.IconNameFontSize);
            CategoryNameFontWeight = _config.BoldCategoryNames ? FontWeights.Bold : FontWeights.SemiBold;
            IconNameFontWeight = _config.BoldIconNames ? FontWeights.Bold : FontWeights.Normal;
            ShowIconBackground = _config.ShowIconBackground;
            IconTileOpacity = Math.Clamp(_config.IconBackgroundOpacity, 10, 100) / 100.0;
            IconBackgroundColor = _config.IconBackgroundColor;

            // Only meaningful while the dock is actually open — if it's hidden right
            // now this just primes the state for the next ShowDock. Never touches
            // FolderHeaderGrid/_openFolder: that's a separate, unrelated reason the
            // search row can be swapped out, and it takes priority whenever it's
            // active (see UpdateSearchBarVisibility's own _openFolder check).
            if (_openFolder == null)
                UpdateSearchBarVisibility();
        }

        /// <summary>
        /// Rows (the original layout) or Columns — AppConfig.LayoutDirection. Only
        /// the category view changes; search results, open folders and the Recent
        /// sections look the same in both.
        ///
        /// Columns, in short:
        ///  - Categories sit side by side (CategoriesColumnsPanelTemplate) and the
        ///    view scrolls sideways instead of down; the mouse wheel scrolls it
        ///    sideways too (CategoriesScrollViewer_PreviewMouseWheel).
        ///  - Each category's icons run top to bottom, filling the dock's height,
        ///    and wrap into another column under the same name (FlowGridPanel.IsVertical).
        ///  - The new-category drop strip becomes a strip to the right of the last
        ///    category.
        ///  - The Recent sections stay a full-width strip along the bottom
        ///    (UpdateAutoSectionsPin).
        /// Arrow keys need nothing special: they already move to whichever tile is
        /// on screen in that direction, so Left/Right step between columns and
        /// categories.
        /// </summary>
        private void ApplyLayoutDirection()
        {
            bool columns = _config.LayoutDirection == LayoutDirection.Columns;

            IsColumnLayout = columns;
            CategoryBannerRow = columns && _config.ColumnAlignment == ColumnAlignment.Bottom ? 2 : 0;
            CategoryIconAlignment = !columns
                ? _config.IconAlignment
                : _config.ColumnAlignment switch
                {
                    ColumnAlignment.Center => RowAlignment.Center,
                    ColumnAlignment.Bottom => RowAlignment.Right,
                    _ => RowAlignment.Left,
                };
            // Category spacing (Settings → Layout): beside each category in Columns,
            // below it in Rows. Each column is already as wide as its name, so no
            // value here can make names overlap.
            double gap = Math.Clamp(columns ? _config.ColumnCategorySpacing : _config.RowCategorySpacing, 0, 64);
            CategoryItemMargin = columns ? new Thickness(0, 0, gap, 0) : new Thickness(0, 0, 0, gap);
            // Category-reorder drop bars sit in the middle of the gap.
            double barOffset = -(gap / 2 + 1);
            CategoryDropBeforeMargin = new Thickness(barOffset, 4, 0, 4);
            CategoryDropAfterMargin = new Thickness(0, 4, barOffset, 4);
            CategoryIconsHorizontalAlignment = columns ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            CategoryBannerAlignment = columns ? TextAlignment.Center : CategoryNameAlignment;

            var panel = (ItemsPanelTemplate)FindResource(columns ? "CategoriesColumnsPanelTemplate" : "CategoriesRowsPanelTemplate");
            if (!ReferenceEquals(CategoriesItemsControl.ItemsPanel, panel))
                CategoriesItemsControl.ItemsPanel = panel;

            CategoriesStack.Orientation = columns ? Orientation.Horizontal : Orientation.Vertical;
            CategoriesScrollViewer.VerticalScrollBarVisibility = columns ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            CategoriesScrollViewer.HorizontalScrollBarVisibility = columns ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;

            if (columns)
            {
                NewCategoryDropZone.Height = double.NaN;
                NewCategoryDropZone.Width = 48;
                NewCategoryDropZone.Margin = new Thickness(0, 0, 4, 8);
            }
            else
            {
                NewCategoryDropZone.Width = double.NaN;
                NewCategoryDropZone.Height = 36;
                NewCategoryDropZone.Margin = new Thickness(4, 0, 4, 8);
            }

            UpdateAutoSectionsPin();
        }

        private readonly TranslateTransform _autoSectionsShift = new();

        /// <summary>Columns only: the categories scroll sideways, but the Recent
        /// sections below them should stay put as a full-width strip. They live
        /// inside the same scrolling area (so Rows keeps scrolling everything as one
        /// list, as before), so here they're sized to the visible width and shifted
        /// right by exactly as far as the view has scrolled, which keeps them in
        /// place on screen. In Rows this puts everything back to normal.</summary>
        private void UpdateAutoSectionsPin()
        {
            if (!IsColumnLayout)
            {
                AutoSectionsPanel.ClearValue(WidthProperty);
                AutoSectionsPanel.ClearValue(HorizontalAlignmentProperty);
                AutoSectionsPanel.ClearValue(RenderTransformProperty);
                return;
            }

            double viewport = CategoriesScrollViewer.ViewportWidth > 0
                ? CategoriesScrollViewer.ViewportWidth
                : CategoriesScrollViewer.ActualWidth;
            double width = Math.Max(0, viewport - AutoSectionsPanel.Margin.Left - AutoSectionsPanel.Margin.Right);
            if (double.IsNaN(AutoSectionsPanel.Width) || Math.Abs(AutoSectionsPanel.Width - width) > 0.5)
                AutoSectionsPanel.Width = width;

            AutoSectionsPanel.HorizontalAlignment = HorizontalAlignment.Left;
            _autoSectionsShift.X = CategoriesScrollViewer.HorizontalOffset;
            if (!ReferenceEquals(AutoSectionsPanel.RenderTransform, _autoSectionsShift))
                AutoSectionsPanel.RenderTransform = _autoSectionsShift;
        }

        private void CategoriesScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // ScrollChanged bubbles up from scrolling areas inside, like a category
            // name's text box — only this one's own changes matter here.
            if (IsColumnLayout && ReferenceEquals(e.OriginalSource, CategoriesScrollViewer))
                UpdateAutoSectionsPin();
        }

        /// <summary>Columns: the mouse wheel scrolls the categories sideways (the
        /// view can't scroll up and down in that layout). Rows: untouched.</summary>
        private void CategoriesScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!IsColumnLayout)
                return;

            CategoriesScrollViewer.ScrollToHorizontalOffset(CategoriesScrollViewer.HorizontalOffset - e.Delta);
            e.Handled = true;
        }

        /// <summary>Columns: while dragging an icon or category near the left or
        /// right edge, scroll that way, so it can be dropped on a category that's
        /// currently scrolled out of view. Doesn't mark the event handled, so the
        /// drop targets underneath still get it.</summary>
        private void CategoriesScrollViewer_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (!IsColumnLayout)
                return;

            const double edge = 40, step = 14;
            double x = e.GetPosition(CategoriesScrollViewer).X;
            if (x < edge)
                CategoriesScrollViewer.ScrollToHorizontalOffset(CategoriesScrollViewer.HorizontalOffset - step);
            else if (x > CategoriesScrollViewer.ViewportWidth - edge)
                CategoriesScrollViewer.ScrollToHorizontalOffset(CategoriesScrollViewer.HorizontalOffset + step);
        }

        /// <summary>Puts MenuBarRow — and DividerBorder, which always stays between it
        /// and the icon-grid content — on whichever edge AppConfig.MenuBarPosition
        /// says: Bottom (the default), Top, Left or Right. Called from
        /// ApplyAppearanceSettings, the same single place every other live-appearance
        /// setting is applied.
        ///
        /// ContentGrid's Row/ColumnDefinitions are rebuilt from scratch for the
        /// current arrangement rather than reshuffled in place, because the
        /// content's "*" (fill-remaining-space) row or column moves with the bar.
        /// Top/Bottom: one column, four rows (bar, divider, search, content or the
        /// reverse) — exactly as before. Left/Right: two rows (search, content) and
        /// three columns, with the bar and divider spanning both rows at one side.
        /// The buttons inside the bar are then laid out along it by
        /// LayoutMenuBarButtons.</summary>
        private void ApplyMenuBarPosition()
        {
            var placement = _config.GetMenuBarPlacement();
            bool vertical = placement is MenuBarPlacement.Left or MenuBarPlacement.Right;

            ContentGrid.RowDefinitions.Clear();
            ContentGrid.ColumnDefinitions.Clear();
            foreach (UIElement el in new UIElement[] { SearchGrid, FolderHeaderGrid, CategoriesScrollViewer, FlatScrollViewer, EmptyStateText, DividerBorder, MenuBarRow })
            {
                Grid.SetColumn(el, 0);
                Grid.SetRowSpan(el, 1);
            }

            if (placement == MenuBarPlacement.Top)
            {
                ContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // 0: menu bar
                ContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // 1: divider
                ContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // 2: search / folder header
                ContentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 3: icon grid / empty state

                Grid.SetRow(MenuBarRow, 0);
                Grid.SetRow(DividerBorder, 1);
                Grid.SetRow(SearchGrid, 2);
                Grid.SetRow(FolderHeaderGrid, 2);
                Grid.SetRow(CategoriesScrollViewer, 3);
                Grid.SetRow(FlatScrollViewer, 3);
                Grid.SetRow(EmptyStateText, 3);
            }
            else if (placement == MenuBarPlacement.Bottom)
            {
                ContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // 0: search / folder header
                ContentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 1: icon grid / empty state
                ContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // 2: divider
                ContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // 3: menu bar

                Grid.SetRow(SearchGrid, 0);
                Grid.SetRow(FolderHeaderGrid, 0);
                Grid.SetRow(CategoriesScrollViewer, 1);
                Grid.SetRow(FlatScrollViewer, 1);
                Grid.SetRow(EmptyStateText, 1);
                Grid.SetRow(DividerBorder, 2);
                Grid.SetRow(MenuBarRow, 3);
            }
            else
            {
                bool left = placement == MenuBarPlacement.Left;

                ContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // 0: search / folder header
                ContentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 1: icon grid / empty state

                // Left: bar | divider | content.  Right: content | divider | bar.
                var star = new GridLength(1, GridUnitType.Star);
                ContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = left ? GridLength.Auto : star });
                ContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                ContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = left ? star : GridLength.Auto });
                int barColumn = left ? 0 : 2;
                int contentColumn = left ? 2 : 0;

                Grid.SetRow(SearchGrid, 0);
                Grid.SetRow(FolderHeaderGrid, 0);
                Grid.SetRow(CategoriesScrollViewer, 1);
                Grid.SetRow(FlatScrollViewer, 1);
                Grid.SetRow(EmptyStateText, 1);
                foreach (UIElement el in new UIElement[] { SearchGrid, FolderHeaderGrid, CategoriesScrollViewer, FlatScrollViewer, EmptyStateText })
                    Grid.SetColumn(el, contentColumn);

                Grid.SetRow(DividerBorder, 0);
                Grid.SetRowSpan(DividerBorder, 2);
                Grid.SetColumn(DividerBorder, 1);
                Grid.SetRow(MenuBarRow, 0);
                Grid.SetRowSpan(MenuBarRow, 2);
                Grid.SetColumn(MenuBarRow, barColumn);
            }

            _menuBarVertical = vertical;
            ApplyDividerShape();
            LayoutMenuBarButtons(vertical, placement == MenuBarPlacement.Right);
        }

        private bool _menuBarVertical;
        private double _dividerThickness = 1;

        /// <summary>The divider line between the menu bar and the rest of the dock:
        /// a horizontal line for a top/bottom bar, a vertical one for a left/right
        /// bar. Its thickness (0 hidden, 1 normal, 2 bold) comes from ApplyBorderStyle.</summary>
        private void ApplyDividerShape()
        {
            if (_menuBarVertical)
            {
                DividerBorder.Height = double.NaN;
                DividerBorder.Width = _dividerThickness;
                DividerBorder.Margin = new Thickness(4, 16, 4, 16);
            }
            else
            {
                DividerBorder.Width = double.NaN;
                DividerBorder.Height = _dividerThickness;
                DividerBorder.Margin = new Thickness(16, 4, 16, 4);
            }
        }

        // The menu bar's buttons in their default order (a top/bottom bar, left to
        // right) and the margins MainWindow.xaml gives them for that layout,
        // captured the first time so every other arrangement is derived from them.
        private FrameworkElement[]? _menuBarButtons;
        private Thickness[]? _menuBarBaseMargins;

        /// <summary>Lays the menu bar's buttons out along the bar.
        ///
        /// Top/bottom bar: User icon at the left end, the rest packed at the right,
        /// ending with Power (the original layout). Left/right bar: Power at the top
        /// down to the User icon at the bottom. "Flip icon order"
        /// (AppConfig.FlipMenuBarOrder) reverses either one. The User icon always sits
        /// in the one stretching slot, pinned to the outer end, so the other buttons
        /// stay packed together at the opposite end, exactly like the original bar.
        ///
        /// Margins: the XAML ones (8px before the User icon, 4px between buttons,
        /// 10px after Power) are mirrored when the order is reversed and turned
        /// sideways for a vertical bar, so the spacing reads the same in every layout.</summary>
        private void LayoutMenuBarButtons(bool vertical, bool rightSide)
        {
            _menuBarButtons ??= new FrameworkElement[]
            {
                UserButtonElement, AddTileButton, NativeStartMenuButton, TaskManagerButtonElement,
                VolumeMixerButtonElement, FileExplorerButtonElement, CalculatorButtonElement, SettingsButtonElement, PowerButtonElement,
            };
            _menuBarBaseMargins ??= _menuBarButtons.Select(b => b.Margin).ToArray();

            bool reversed = vertical ^ _config.FlipMenuBarOrder;

            MenuBarRow.RowDefinitions.Clear();
            MenuBarRow.ColumnDefinitions.Clear();
            if (vertical)
            {
                MenuBarRow.Height = double.NaN;
                MenuBarRow.Width = 52;
                // Keep the buttons clear of the 10px resize grips along the top edge
                // (TopResizeGrip) and, on the right side, the right edge
                // (RightResizeGrip), so an edge never steals a click.
                MenuBarRow.Margin = new Thickness(0, 2, rightSide ? 6 : 0, 0);
            }
            else
            {
                MenuBarRow.Width = double.NaN;
                MenuBarRow.Height = 52;
                MenuBarRow.Margin = new Thickness(0);
            }

            for (int slot = 0; slot < _menuBarButtons.Length; slot++)
            {
                int baseIndex = reversed ? _menuBarButtons.Length - 1 - slot : slot;
                var button = _menuBarButtons[baseIndex];
                var length = ReferenceEquals(button, UserButtonElement) ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;

                if (vertical)
                {
                    MenuBarRow.RowDefinitions.Add(new RowDefinition { Height = length });
                    Grid.SetRow(button, slot);
                    Grid.SetColumn(button, 0);
                }
                else
                {
                    MenuBarRow.ColumnDefinitions.Add(new ColumnDefinition { Width = length });
                    Grid.SetColumn(button, slot);
                    Grid.SetRow(button, 0);
                }

                var m = _menuBarBaseMargins[baseIndex];
                if (reversed)
                    m = new Thickness(m.Right, m.Top, m.Left, m.Bottom); // mirror left/right
                if (vertical)
                    m = new Thickness(m.Top, m.Left, m.Bottom, m.Right); // turn sideways
                button.Margin = m;
            }

            // The User icon hugs the outer end of its stretching slot: the start of
            // the bar when it comes first, the end when it comes last.
            bool userFirst = !reversed;
            if (vertical)
            {
                UserButtonElement.HorizontalAlignment = HorizontalAlignment.Center;
                UserButtonElement.VerticalAlignment = userFirst ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            }
            else
            {
                UserButtonElement.HorizontalAlignment = userFirst ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                UserButtonElement.ClearValue(VerticalAlignmentProperty);
            }
        }

        /// <summary>Sets BackgroundBorder's own Background to a user-picked picture
        /// (AppConfig.BackgroundImagePath) when one is configured, or restores it to
        /// the plain, themeable DockBackgroundBrush color when it isn't — called from
        /// ApplyAppearanceSettings, before that method's own Opacity fade, since
        /// Opacity fades whatever this leaves in place (image or color) exactly the
        /// same way either way; there's nothing image-specific about the fade itself.
        ///
        /// SetResourceReference (rather than reading the resource once and assigning
        /// it) is what makes reverting to the solid color safe even if the app's
        /// theme changes later (see ThemeService swapping Theme.xaml/Theme.Dark.xaml
        /// at runtime) — a plain one-time lookup would freeze in whichever theme's
        /// color happened to be active the moment the image was removed.</summary>
        private void ApplyBackgroundImage()
        {
            if (!string.IsNullOrEmpty(_config.BackgroundImagePath) && File.Exists(_config.BackgroundImagePath))
            {
                try
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(_config.BackgroundImagePath, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();

                    // UniformToFill (default) crops to fill edge-to-edge; Uniform (when
                    // FitBackgroundImage is on) shows the whole picture instead, scaled
                    // down to fit — see AppConfig.FitBackgroundImage's own doc comment.
                    var brush = new ImageBrush(bitmap)
                    {
                        Stretch = _config.FitBackgroundImage ? Stretch.Uniform : Stretch.UniformToFill,
                    };

                    StopBackgroundAnimation();
                    bool isGif = AnimatedGif.IsGif(_config.BackgroundImagePath);
                    if (!isGif)
                        brush.Freeze(); // a GIF's brush stays unfrozen so it can play

                    // Set before starting the animation below: once a GIF is already
                    // decoded (e.g. Apply, then Save), AnimateBrush continues straight
                    // away, and its "is this still the background?" check has to see
                    // this brush. It used to run before this line, so that check
                    // failed and the GIF stayed still.
                    BackgroundBorder.Background = brush;

                    if (isGif)
                    {
                        // Shows the first frame right away, then plays once every
                        // frame is decoded (AnimatedGif).
                        _animatedBackgroundBrush = brush;
                        AnimatedGif.AnimateBrush(brush, _config.BackgroundImagePath,
                            () => ReferenceEquals(BackgroundBorder.Background, brush));
                    }
                    return;
                }
                catch
                {
                    // Falls through to the solid-color background below — a corrupt or
                    // no-longer-readable image file shouldn't leave the dock with no
                    // background at all.
                }
            }

            StopBackgroundAnimation();
            BackgroundBorder.SetResourceReference(Border.BackgroundProperty, "DockBackgroundBrush");
        }

        // The current animated GIF background's brush, if any — stopped whenever the
        // background is replaced, so a discarded brush doesn't keep animating unseen.
        private ImageBrush? _animatedBackgroundBrush;

        private void StopBackgroundAnimation()
        {
            _animatedBackgroundBrush?.BeginAnimation(ImageBrush.ImageSourceProperty, null);
            _animatedBackgroundBrush = null;
        }

        /// <summary>Turns DWM's blur-behind on or off per AppConfig.FrostedBackground
        /// — called from ApplyAppearanceSettings right alongside ApplyBackgroundImage
        /// above, since both just control what shows up behind/underneath the dock's
        /// own content; BackgroundOpacity (set right after, in ApplyAppearanceSettings)
        /// then decides how much of that blur — versus the solid color or image
        /// ApplyBackgroundImage just set — actually shows through, exactly the same
        /// way it already decides how much of the plain, unblurred desktop shows
        /// through today. Purely a compositing effect of the window itself, so it
        /// works identically underneath either kind of background without either of
        /// them needing to know it's there.
        ///
        /// EnsureHandle() (rather than reading WindowInteropHelper.Handle directly)
        /// is what makes this safe to call from Initialize(), before the dock has
        /// ever actually been shown — a WPF window has no real Win32 handle yet at
        /// that point unless something forces one into existence early, and both
        /// DWM attribute calls below need a real hwnd to act on.
        ///
        /// Tries the modern, documented Windows 11 route first — DWMWA_SYSTEMBACKDROP_TYPE
        /// set to DWMSBT_TRANSIENTWINDOW ("Acrylic", the same material Windows' own
        /// chromeless flyouts use — see NativeMethods' own comment) — and only falls
        /// back to the older, undocumented SetWindowCompositionAttribute approach
        /// (ApplyLegacyBlurBehind, right below) when DWM doesn't recognize that
        /// attribute at all, which is how a plain Windows 10 machine reports back:
        /// a failure HRESULT, not an exception. That fallback is also what's still
        /// in play on Windows 11 itself if the newer attribute is ever rejected for
        /// some other reason — this should never leave the dock with no blur at all
        /// just because the preferred route didn't take.
        ///
        /// DWMWA_WINDOW_CORNER_PREFERENCE is requested unconditionally, blur or not
        /// — asking DWM to treat this as a natively rounded window costs nothing
        /// when it's ignored (Windows 10) and gives whichever backdrop route ends
        /// up active its best chance of actually respecting BackgroundBorder's own
        /// rounded shape rather than SetWindowRgn needing to fight it into shape.</summary>
        /// <summary>Whether the frosted material should use its dark variant — see
        /// AppConfig.FrostTint.</summary>
        private bool FrostIsDark() => _config.FrostTint switch
        {
            FrostTintMode.Dark => true,
            FrostTintMode.Light => false,
            _ => ThemeService.ResolveIsDark(_config.Theme),
        };

        private void ApplyFrostedBackground()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();

                int cornerPreference = (int)NativeMethods.DwmWindowCornerPreference.DWMWCP_ROUND;
                NativeMethods.DwmSetWindowAttribute(
                    hwnd,
                    NativeMethods.DwmWindowAttribute.DWMWA_WINDOW_CORNER_PREFERENCE,
                    ref cornerPreference,
                    sizeof(int));

                // Choose the frosted material's light or dark variant. Without
                // this DWM always used the light one, so frosting over a dark
                // desktop came out a flat mid-gray instead of a dark blur.
                int useDarkMode = FrostIsDark() ? 1 : 0;
                NativeMethods.DwmSetWindowAttribute(
                    hwnd,
                    NativeMethods.DwmWindowAttribute.DWMWA_USE_IMMERSIVE_DARK_MODE,
                    ref useDarkMode,
                    sizeof(int));

                // Frost tint "Clear": Windows 11's acrylic always adds a light or
                // dark tint, with no way to turn it off, so a plain untinted blur
                // has to come from the older SetWindowCompositionAttribute blur
                // (ACCENT_ENABLE_BLURBEHIND, no GradientColor) instead. Its known
                // catch: back when this app used it before, DWM drew it with
                // square corners regardless of SetWindowRgn. The window now also
                // asks for DWMWCP_ROUND above and the card fills the whole window,
                // so it may round properly now — but that's untested, hence this
                // being an opt-in choice rather than the default.
                bool clearBlur = _config.FrostedBackground && _config.FrostTint == FrostTintMode.Clear;

                int backdropType = (int)(_config.FrostedBackground && !clearBlur
                    ? NativeMethods.DwmSystemBackdropType.DWMSBT_TRANSIENTWINDOW
                    : NativeMethods.DwmSystemBackdropType.DWMSBT_NONE);
                int backdropResult = NativeMethods.DwmSetWindowAttribute(
                    hwnd,
                    NativeMethods.DwmWindowAttribute.DWMWA_SYSTEMBACKDROP_TYPE,
                    ref backdropType,
                    sizeof(int));

                if (backdropResult == 0) // S_OK — Windows 11 route available
                {
                    // Either the plain blur (Clear), or explicitly off — so switching
                    // away from Clear never leaves the old blur running underneath
                    // the acrylic.
                    ApplyLegacyBlurBehind(hwnd, clearBlur
                        ? NativeMethods.AccentState.ACCENT_ENABLE_BLURBEHIND
                        : NativeMethods.AccentState.ACCENT_DISABLED);
                    return;
                }

                // Pre-Windows-11 fallback: the legacy API is all there is.
                ApplyLegacyBlurBehind(hwnd, !_config.FrostedBackground
                    ? NativeMethods.AccentState.ACCENT_DISABLED
                    : clearBlur
                        ? NativeMethods.AccentState.ACCENT_ENABLE_BLURBEHIND
                        : NativeMethods.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND);
            }
            catch
            {
                // Both routes below are either undocumented or Windows-11-only — if
                // either is ever missing, rejected, or behaves unexpectedly on some
                // Windows build, the dock should just fall back to its plain,
                // unblurred background rather than failing to open at all.
            }
        }

        /// <summary>The pre-Windows-11 fallback for ApplyFrostedBackground, right
        /// above — SetWindowCompositionAttribute is undocumented (no public MSDN
        /// page), but it's the long-standing, widely-used way apps get DWM to blur
        /// whatever's behind a window, and it works cleanly with a chromeless,
        /// AllowsTransparency-layered window like MainWindow. ACCENT_ENABLE_ACRYLICBLURBEHIND
        /// rather than the older plain ACCENT_ENABLE_BLURBEHIND: the latter is the
        /// cruder Windows 10-1809-era accent state, and blurs whatever rectangle the
        /// window occupies on screen regardless of what shape the window itself
        /// claims to be, where ACRYLICBLURBEHIND is the newer Fluent "Acrylic
        /// material" accent state and at least has a chance of respecting a layered
        /// window's SetWindowRgn shape. It needs a real (even if tiny) alpha in
        /// GradientColor to render as acrylic at all — a fully zero-alpha tint can
        /// make it fall back to rendering as fully transparent instead of blurred —
        /// so this uses the barest sliver of white (1/255 alpha) rather than 0,
        /// deliberately not tinting the blur itself: BackgroundBorder's own
        /// DockBackgroundBrush, layered on top, still supplies all of the dock's
        /// actual visible color.</summary>
        private void ApplyLegacyBlurBehind(IntPtr hwnd, NativeMethods.AccentState state)
        {
            var accent = new NativeMethods.AccentPolicy
            {
                AccentState = state,
                // 0xAABBGGRR — 1/255 alpha, white: just enough non-zero alpha to
                // make DWM actually render the acrylic material rather than
                // treating a fully-zero-alpha tint as "nothing to draw", without
                // visibly tinting anything itself.
                GradientColor = state == NativeMethods.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND
                    ? (FrostIsDark() ? 0x01000000 : 0x01FFFFFF)
                    : 0,
            };

            int accentSize = Marshal.SizeOf(accent);
            IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);
            try
            {
                Marshal.StructureToPtr(accent, accentPtr, false);

                var data = new NativeMethods.WindowCompositionAttributeData
                {
                    Attribute = NativeMethods.WindowCompositionAttribute.WCA_ACCENT_POLICY,
                    SizeOfData = accentSize,
                    Data = accentPtr,
                };

                NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(accentPtr);
            }
        }

        /// <summary>Clips this window's own hwnd to a rounded rectangle matching
        /// BackgroundBorder's own shape — called whenever the hwnd first exists, the
        /// window is resized, or it moves to a different-DPI monitor (see the
        /// constructor's SourceInitialized/SizeChanged/DpiChanged wiring).
        ///
        /// Without this, "Frost the background" (see ApplyFrostedBackground right
        /// above) blurs this window's full, square-cornered rectangular bounds —
        /// including the 10px transparent margin around BackgroundBorder that exists
        /// so its own drop shadow has room to render — rather than the rounded card
        /// the dock actually shows, which reads as a squared-off blurred panel sitting
        /// behind the visible, rounded dock rather than filling it exactly.
        ///
        /// A first version of this rounded the window's own *outer* bounds instead of
        /// BackgroundBorder's — that was the actual bug behind "I don't think the
        /// frosting shape is correct yet": BackgroundBorder.CornerRadius is 8dip, and
        /// CreateRoundRectRgn's corner argument is a diameter, so the region's own
        /// rounding was only ever ~16dip (scaled) across — tiny next to a window that's
        /// several hundred dip wide. Rounding the window's full rectangle by that
        /// little only nicks the four literal corners; the ~10dip transparent margin
        /// around BackgroundBorder (there for its own drop shadow to bleed into) is a
        /// separate rectangle offset inward from those corners entirely, so it stayed
        /// part of the region almost everywhere and kept reading as a flat, square
        /// panel sitting behind the visibly rounded card — exactly the original
        /// complaint, basically unchanged. The region needs to track BackgroundBorder's
        /// own rect, not the window's, for the blur to actually take its shape.
        ///
        /// Zero bleed, on purpose: an earlier version of this kept a few pixels of
        /// margin past BackgroundBorder's own edge so its DropShadowEffect had room
        /// to bleed into — explicitly reported back as still visible, past the point
        /// where the card's own border line ends. So this clips exactly to
        /// BackgroundBorder's own rect now — "the visible border is where the
        /// frosting should stop" — at the cost of the shadow's outer falloff getting
        /// clipped away along with it. A harder edge than a soft drop shadow usually
        /// wants, but it's what was actually asked for, and it's unambiguous: nothing
        /// (blur, shadow, or otherwise) renders past that line.</summary>
        private void ApplyWindowRegion()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero || ActualWidth <= 0 || ActualHeight <= 0)
                    return; // nothing to clip yet — SourceInitialized/SizeChanged will call this again once there is

                // PresentationSource.CompositionTarget.TransformToDevice is the exact
                // DIP-to-physical-pixel matrix WPF itself uses to actually render this
                // specific window's surface — used here instead of the previous
                // VisualTreeHelper.GetDpi(this).DpiScaleX/Y reading, which turned out
                // not to always agree with it closely enough. That mismatch is what
                // caused "the right border went missing": SetWindowRgn's rect is
                // anchored at the window's own top-left corner, so an *undersized*
                // region (computed from too small a scale factor) clips away whatever
                // is nearest the right and bottom edges first — not just a rounding
                // sliver, but enough of the bottom edge in this case to take the
                // divider and the entire menu bar with it, since both sit right along
                // it. Reading the conversion straight from this window's own real
                // rendering transform, rather than a second, separately-obtained DPI
                // value, removes any room for the two to disagree.
                var source = PresentationSource.FromVisual(this);
                Matrix toDevice = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;

                // Sanity guard: a real per-monitor DPI scale is always in a fairly
                // narrow, well-known range (100%-400%, i.e. 1.0-4.0). If this ever
                // comes back outside that — no CompositionTarget yet, or some future
                // Windows/·NET quirk — trust nothing and leave the window's plain
                // rectangular shape alone rather than risk clipping real content away
                // again the way the previous, unguarded version did.
                if (toDevice.M11 < 1.0 || toDevice.M11 > 4.0 || toDevice.M22 < 1.0 || toDevice.M22 > 4.0)
                    return;

                const double shadowBleedDip = 0;
                Thickness margin = BackgroundBorder.Margin;
                double leftDip = Math.Max(0, margin.Left - shadowBleedDip);
                double topDip = Math.Max(0, margin.Top - shadowBleedDip);
                double rightInsetDip = Math.Max(0, margin.Right - shadowBleedDip);
                double bottomInsetDip = Math.Max(0, margin.Bottom - shadowBleedDip);

                // Region bounds come from the window's real physical client rect
                // (GetClientRect), not from ActualWidth/ActualHeight times the DPI
                // scale. Why: DWM's acrylic backdrop (see ApplyFrostedBackground)
                // fills the whole window rect and ignores SetWindowRgn entirely, so
                // any pixel this region leaves out isn't "clipped to nothing" — it
                // shows raw backdrop. Measured straight off screenshots, the old
                // DIP-derived bounds came out exactly one device pixel short of the
                // real window on the right and bottom (the edges a Win32 rect grows
                // toward), which cut off the card's own outermost border pixel on
                // those two sides only and let a bright backdrop line show in its
                // place — "more white on the right and bottom", most obvious with a
                // black bold border, where that last pixel read near-white instead
                // of black. Flipping the rounding direction (an earlier attempt)
                // couldn't help, because the boundary was already landing on a whole
                // pixel; the error was in the size itself, not the rounding.
                //
                // Paired with BackgroundBorder/ContentGrid's Margin now being 0 (see
                // MainWindow.xaml), this region covers every pixel WPF paints, so
                // there's no longer a transparent ring anywhere for the backdrop to
                // show through — only the four corners get cut, to match the card's
                // own rounding. Margin is still honored below, scaled into device
                // pixels, in case it's ever made non-zero again.
                if (!NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT client) || client.Width <= 0 || client.Height <= 0)
                    return;

                int leftPx = (int)Math.Round(leftDip * toDevice.M11);
                int topPx = (int)Math.Round(topDip * toDevice.M22);
                int rightInsetPx = (int)Math.Round(rightInsetDip * toDevice.M11);
                int bottomInsetPx = (int)Math.Round(bottomInsetDip * toDevice.M22);

                // CreateRoundRectRgn excludes its right/bottom edge (ordinary Win32
                // RECT rules), so with no inset, reach one past the client size to
                // be certain the last real column/row is inside — extending past the
                // window itself is harmless, the OS just ignores the overhang.
                int rightPx = client.Width - rightInsetPx + (rightInsetPx == 0 ? 1 : 0);
                int bottomPx = client.Height - bottomInsetPx + (bottomInsetPx == 0 ? 1 : 0);
                if (rightPx <= leftPx || bottomPx <= topPx)
                    return; // window too small right now for a sane region — next SizeChanged will retry

                // CreateRoundRectRgn's cornerWidth/cornerHeight are the corner
                // ellipse's full diameter, not its radius — hence the *2. Read from
                // BackgroundBorder's own live CornerRadius (rather than a second
                // hardcoded literal here) so this can never quietly drift out of sync
                // with the XAML value it's meant to match. shadowBleedDip stays in
                // the formula (even pinned to 0 above) so a future adjustment there
                // keeps the region's own corner curve in sync with wherever its
                // straight edges end up, rather than needing to be touched in two
                // places.
                double cornerRadiusDip = BackgroundBorder.CornerRadius.TopLeft + shadowBleedDip;
                int cornerPx = (int)Math.Round(cornerRadiusDip * 2 * toDevice.M11);

                IntPtr region = NativeMethods.CreateRoundRectRgn(leftPx, topPx, rightPx, bottomPx, cornerPx, cornerPx);
                if (region == IntPtr.Zero)
                    return;

                // SetWindowRgn takes ownership of the region handle once it succeeds —
                // Windows frees it (and whatever region was set before it) on its own,
                // including the next time this replaces it, so there's nothing for us
                // to DeleteObject here ourselves.
                NativeMethods.SetWindowRgn(hwnd, region, true);
            }
            catch
            {
                // Same defensive posture as ApplyFrostedBackground right above — a
                // region that can't be applied should just leave the window's
                // default rectangular shape rather than failing to open at all.
            }
        }

        /// <summary>See AppConfig.BoldBorder's own doc comment for what this setting
        /// does and why. When it's off, both BackgroundBorder and DividerBorder are
        /// put back on their normal DynamicResource-driven theme colors/thicknesses
        /// via SetResourceReference — not just left alone — since a previous call
        /// with BoldBorder on would otherwise have left a literal, non-theme-aware
        /// Brush sitting in BorderBrush/Background that a later theme switch could
        /// never update again. When it's on, ThemeService.ResolveIsDark (the same
        /// helper App.xaml.cs uses to pick which theme dictionary is loaded) decides
        /// black-on-light vs white-on-dark, so this stays correct through
        /// ThemeMode.Follow and through a live theme change, since Save re-runs this
        /// (via ApplyAppearanceSettings) regardless of which setting actually
        /// changed.</summary>
        private void ApplyBorderStyle()
        {
            // OutlineBorder (not BackgroundBorder) carries the outline now, so
            // Background opacity doesn't fade it — see its comment in MainWindow.xaml.
            //
            // Takes precedence over BoldBorder below — a hidden border stays hidden
            // regardless of what the (now invisible) Bold border setting says.
            if (_config.HideBorder)
            {
                OutlineBorder.BorderThickness = new Thickness(0);
                _dividerThickness = 0;
                ApplyDividerShape();
                SetSystemBorderColor(null);
                return;
            }

            if (!_config.BoldBorder)
            {
                OutlineBorder.SetResourceReference(Border.BorderBrushProperty, "DockBorderBrush");
                OutlineBorder.BorderThickness = new Thickness(1);
                DividerBorder.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
                _dividerThickness = 1;
                ApplyDividerShape();
                SetSystemBorderColor(null);
                return;
            }

            Color boldColor = ThemeService.ResolveIsDark(_config.Theme) ? Colors.White : Colors.Black;
            var boldBrush = new SolidColorBrush(boldColor);
            boldBrush.Freeze();

            OutlineBorder.BorderBrush = boldBrush;
            OutlineBorder.BorderThickness = new Thickness(2);
            DividerBorder.Background = boldBrush;
            _dividerThickness = 2;
            ApplyDividerShape();
            SetSystemBorderColor(boldColor);
        }

        /// <summary>Windows 11 draws its own thin 1px border around the window's
        /// outermost edge (on top of everything, since the window asks for rounded
        /// corners). Left at its default gray, it sat just outside a Bold border as
        /// a second, dimmer ring. This colors it to match Bold instead (null =
        /// Windows' default). Ignored on Windows 10, which has no such attribute.</summary>
        private void SetSystemBorderColor(Color? color)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
                // COLORREF is 0x00BBGGRR; DWMWA_COLOR_DEFAULT is 0xFFFFFFFF.
                int colorRef = color is Color c
                    ? (c.B << 16) | (c.G << 8) | c.R
                    : unchecked((int)0xFFFFFFFF);
                NativeMethods.DwmSetWindowAttribute(
                    hwnd,
                    NativeMethods.DwmWindowAttribute.DWMWA_BORDER_COLOR,
                    ref colorRef,
                    sizeof(int));
            }
            catch
            {
                // Purely cosmetic — never worth failing over.
            }
        }

        /// <summary>Overrides the dock's text color independent of the active theme.
        /// Every text element in MainWindow's own content binds to PrimaryTextBrush/
        /// SecondaryTextBrush with {DynamicResource ...} (icon names, search box,
        /// category name edit box, empty-state text, and so on), which WPF resolves
        /// by walking up the resource tree from the element to the Application —
        /// so setting those two keys directly on this Window's own Resources
        /// shadows the merged theme dictionary (Theme.xaml/Theme.Dark.xaml) for
        /// every one of those bindings at once, without editing each of them
        /// individually. SettingsWindow is a separate Window (its own resource-tree
        /// root), so this deliberately has no effect on the Settings dialog's own
        /// text — only the dock's content.</summary>
        private void ApplyTextColor()
        {
            if (_config.TextColor == TextColorMode.Default)
            {
                Resources.Remove("PrimaryTextBrush");
                Resources.Remove("SecondaryTextBrush");
                return;
            }

            var brush = new SolidColorBrush(_config.TextColor == TextColorMode.White ? Colors.White : Colors.Black);
            brush.Freeze();

            Resources["PrimaryTextBrush"] = brush;
            Resources["SecondaryTextBrush"] = brush;
        }

        /// <summary>Fire-and-forget cache warm-up, called once at startup. Errors are
        /// swallowed rather than surfaced — a failed warm-up just means the first real
        /// search/picker open falls back to doing the same work on demand, exactly as
        /// it always has.</summary>
        private async Task WarmInstalledAppsCacheAsync()
        {
            try
            {
                await InstalledAppsCache.GetAppsAsync();
                await InstalledAppsCache.EnsureIconsLoadedAsync(_iconExtractor);
                UpdateAutoSections();
            }
            catch
            {
                // Ignore — see summary above.
            }
        }

        // How old the installed-apps list can get before opening the dock quietly
        // refreshes it in the background (see AutoRefreshAppsAsync).
        private static readonly TimeSpan AppListMaxAge = TimeSpan.FromMinutes(5);

        // How long a newly installed app stays in "Recently added". How many tiles
        // each automatic section shows is up to Settings (2–12 each).
        private static readonly TimeSpan NewAppWindow = TimeSpan.FromDays(7);

        /// <summary>Called whenever the dock opens: if the installed-apps list is more
        /// than a few minutes old, re-read it in the background so newly installed apps
        /// show up in search, the Add picker and "Recently added" without needing
        /// Settings → Refresh Apps. Everything heavy runs off the UI thread (see
        /// InstalledAppsCache), so opening the dock isn't slowed down by it.</summary>
        private async Task AutoRefreshAppsAsync()
        {
            try
            {
                int added = await InstalledAppsCache.RefreshIfStaleAsync(AppListMaxAge);
                if (added <= 0)
                    return;

                await InstalledAppsCache.EnsureIconsLoadedAsync(_iconExtractor);
                UpdateAutoSections();
            }
            catch
            {
                // A failed background refresh just leaves the current list in place.
            }
        }

        // Icons for recent files, kept for the session so reopening the dock doesn't
        // re-extract the same ones every time.
        private readonly Dictionary<string, ImageSource?> _recentFileIcons = new(StringComparer.OrdinalIgnoreCase);
        private int _autoSectionsVersion;

        /// <summary>Rebuilds the automatic sections at the bottom of the dock —
        /// Recently Added, Recently Used and Recent Files, each only if turned on in
        /// Settings — either as a section each or combined into one "Recent" section
        /// (AppConfig.CombineRecentSections). An empty section is hidden. The
        /// registry read (Recently Used) and recent files' icons happen on a
        /// background thread.</summary>
        private async void UpdateAutoSections()
        {
            int version = ++_autoSectionsVersion; // a newer call supersedes this one

            var addedTiles = new List<DockIconViewModel>();
            var usedTiles = new List<DockIconViewModel>();
            var fileTiles = new List<DockIconViewModel>();

            var apps = InstalledAppsCache.Apps;
            var pinnedTargets = new HashSet<string>(
                _categories.SelectMany(c => c.Icons).Where(v => !v.IsFolder).Select(v => v.Model.TargetPath),
                StringComparer.OrdinalIgnoreCase);
            bool IsPinned(InstalledAppViewModel app) => pinnedTargets.Contains($"shell:AppsFolder\\{app.Model.AppId}");

            // Recently added: real apps (see AppKind) first seen in the last week,
            // minus anything pinned.
            if (_config.ShowNewApps && apps != null)
            {
                int max = Math.Clamp(_config.NewAppsCount, 2, 12);
                var byId = apps.GroupBy(a => a.Model.AppId, StringComparer.OrdinalIgnoreCase)
                               .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                foreach (string appId in AppSeenTracker.GetRecentlyAdded(NewAppWindow))
                {
                    if (addedTiles.Count >= max)
                        break;
                    if (!byId.TryGetValue(appId, out var app) || !AppKind.IsRealApp(app.Model) || IsPinned(app))
                        continue;
                    addedTiles.Add(CreateSearchResultTile(app));
                }
            }

            try
            {
                // Recently used: real apps by last-opened time, newest first, minus
                // anything pinned (those are a click away already).
                if (_config.ShowRecentApps && apps != null)
                {
                    int max = Math.Clamp(_config.RecentAppsCount, 2, 12);
                    var candidates = apps.Where(a => AppKind.IsRealApp(a.Model) && !IsPinned(a)).ToList();
                    var lastUsed = await Task.Run(() => AppUsageService.GetLastUsed());
                    if (version != _autoSectionsVersion)
                        return;

                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var ranked = new List<(InstalledAppViewModel App, DateTime When)>();
                    foreach (var app in candidates)
                    {
                        if (!seen.Add(app.Model.AppId))
                            continue;
                        if (lastUsed.TryGetValue(AppUsageService.NormalizeId(app.Model.AppId), out var when))
                            ranked.Add((app, when));
                    }
                    foreach (var (app, _) in ranked.OrderByDescending(r => r.When).Take(max))
                        usedTiles.Add(CreateSearchResultTile(app));
                }

                // Recent files, from Windows' Recent Items list.
                if (_config.ShowRecentFiles)
                {
                    int recentCount = Math.Clamp(_config.RecentFilesCount, 2, 12);
                    var known = new Dictionary<string, ImageSource?>(_recentFileIcons, StringComparer.OrdinalIgnoreCase);
                    var built = await Task.Run(() =>
                    {
                        var list = new List<(RecentFilesService.RecentFile File, string? IconPath, ImageSource? Icon)>();
                        foreach (var file in RecentFilesService.GetRecentFiles(recentCount))
                        {
                            if (known.TryGetValue(file.ShortcutPath, out var cachedIcon))
                            {
                                list.Add((file, null, cachedIcon));
                                continue;
                            }
                            Guid id = IconExtractor.StableId("recent:" + file.ShortcutPath);
                            string? iconPath = _iconExtractor.ExtractAndCache(file.ShortcutPath, id);
                            list.Add((file, iconPath, LoadFrozenImage(iconPath)));
                        }
                        return list;
                    });
                    if (version != _autoSectionsVersion)
                        return;

                    foreach (var (file, iconPath, icon) in built)
                    {
                        _recentFileIcons[file.ShortcutPath] = icon;
                        var model = new DockIcon
                        {
                            Name = file.Name,
                            TargetPath = file.ShortcutPath,
                            CachedIconPath = iconPath ?? string.Empty,
                        };
                        fileTiles.Add(new DockIconViewModel(model, isSearchResult: true) { IsFile = true, IconImage = icon });
                    }
                }
            }
            catch
            {
                // A failed read just leaves that part out this time.
            }

            if (version != _autoSectionsVersion)
                return;

            NewAppTiles.Clear();
            RecentAppTiles.Clear();
            RecentFileTiles.Clear();
            CombinedRecentTiles.Clear();

            if (_config.CombineRecentSections)
            {
                // One "Recent" section: recently added, then recently used, then
                // files. An app that's both new and recently used shows once.
                var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var tile in addedTiles.Concat(usedTiles).Concat(fileTiles))
                {
                    if (shown.Add(tile.Model.TargetPath ?? string.Empty))
                        CombinedRecentTiles.Add(tile);
                }
            }
            else
            {
                foreach (var tile in addedTiles) NewAppTiles.Add(tile);
                foreach (var tile in usedTiles) RecentAppTiles.Add(tile);
                foreach (var tile in fileTiles) RecentFileTiles.Add(tile);
            }

            NewAppsVisibility = NewAppTiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            RecentAppsVisibility = RecentAppTiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            RecentFilesVisibility = RecentFileTiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            CombinedRecentVisibility = CombinedRecentTiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Decodes a cached icon PNG into a frozen image, safe to create on a
        /// background thread and hand to the UI. Null if there's nothing to load.</summary>
        private static ImageSource? LoadFrozenImage(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;
            try
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        // ---------------------------------------------------------------
        // Show / hide
        // ---------------------------------------------------------------

        public void ToggleVisibility()
        {
            if (Visibility == Visibility.Visible)
                HideDock();
            else
                ShowDock();
        }

        public void ShowDock()
        {
            // Always reopen at the top level rather than remembering a folder that was
            // left open the last time the dock was hidden.
            if (_openFolder != null)
                CloseFolder();

            PositionDock();
            SearchBox.Text = string.Empty;

            // Before Show/Activate: the raise puts the taskbar at the top of the
            // topmost band, and the dock activating right after puts itself back
            // above it. The monitor is the one under the cursor, same as
            // PositionDock's GetTargetWorkArea.
            if (_config.ShowTaskbarOverFullscreen)
                _fullscreenTaskbar.RaiseOnMonitorAtCursor();

            Visibility = Visibility.Visible;
            Show();
            Activate();
            ForceForeground();

            // Deliberately after Show()/Activate(), not before: UpdateSearchBarVisibility
            // ends by calling Keyboard.Focus on either SearchBox or this window itself
            // (see its own doc comment), and a WPF window reliably accepts a real
            // keyboard focus change only once it's actually visible/active — calling
            // this while the window was still Hidden (as this used to do, before Show()
            // ran) meant the focus-set could silently fail, then get further stomped on
            // by Activate() right after, which is exactly what made "press Win and just
            // start typing" land in the search box inconsistently rather than every time.
            UpdateSearchBarVisibility();

            BeginAppearAnimation();

            // After the dock is up, so neither delays it appearing.
            RefreshUsageAsync();
            UpdateAutoSections();
            _ = AutoRefreshAppsAsync();
        }

        /// <summary>
        /// Makes sure the dock really becomes the active window, so typing goes into
        /// its search box rather than the app that was in front before.
        ///
        /// Activate() alone sometimes isn't enough: Windows only lets a program bring
        /// itself to the front when it "owns" the latest keyboard or mouse input, and
        /// StartDock watches the Windows key through a keyboard hook rather than
        /// receiving it, so Windows can refuse. The dock then appears on top (it's
        /// topmost) but the other app keeps the keyboard — which is how typing ended
        /// up in another window until the dock was clicked.
        ///
        /// If the dock didn't get the front, two standard workarounds are tried in
        /// turn: briefly joining the front app's input queue (AttachThreadInput),
        /// which lets the switch through; and, failing that, a tap of Alt around the
        /// request, since Windows always allows switching windows while Alt is down
        /// (that's how Alt+Tab works).
        /// </summary>
        private void ForceForeground()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero || NativeMethods.GetForegroundWindow() == hwnd)
                    return;

                IntPtr foreground = NativeMethods.GetForegroundWindow();
                uint foregroundThread = foreground != IntPtr.Zero
                    ? NativeMethods.GetWindowThreadProcessId(foreground, IntPtr.Zero)
                    : 0;
                uint ourThread = NativeMethods.GetCurrentThreadId();

                bool attached = foregroundThread != 0 && foregroundThread != ourThread
                    && NativeMethods.AttachThreadInput(ourThread, foregroundThread, true);
                try
                {
                    NativeMethods.BringWindowToTop(hwnd);
                    NativeMethods.SetForegroundWindow(hwnd);
                }
                finally
                {
                    if (attached)
                        NativeMethods.AttachThreadInput(ourThread, foregroundThread, false);
                }

                if (NativeMethods.GetForegroundWindow() != hwnd)
                {
                    SendAltKey(keyUp: false);
                    NativeMethods.SetForegroundWindow(hwnd);
                    SendAltKey(keyUp: true);
                }

                Activate(); // let WPF catch up on which of its windows is active
            }
            catch
            {
                // Best effort — the dock is still shown, just possibly without focus.
            }
        }

        private static void SendAltKey(bool keyUp)
        {
            var input = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                U = new NativeMethods.InputUnion
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = (ushort)NativeMethods.VK_MENU,
                        wScan = 0,
                        dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero,
                    },
                },
            };
            NativeMethods.SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
        }

        /// <summary>The dock is one window that's shown and hidden for the whole time
        /// StartDock runs, so it must never actually close. Alt+F4 while it has focus
        /// (or anything else asking it to close) used to close it for good — then the
        /// next tray-icon click or Windows key press crashed with "Cannot set
        /// Visibility or call Show ... after a Window has closed". Now that just hides
        /// it. Quitting StartDock (tray → Exit) still closes it: WPF ignores this
        /// cancel while the app itself is shutting down.</summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            e.Cancel = true;
            HideDock();
            base.OnClosing(e);
        }

        public void HideDock()
        {
            if (_dialogOpen) return;
            _fullscreenTaskbar.Restore(); // no-op unless ShowDock actually raised it
            BeginDisappearAnimation();
        }

        /// <summary>Sets Left/Top for the dock's configured DockPosition — everything
        /// but BottomLeft (the original, unanimated default — see DockPosition) also
        /// gets a quick appear/disappear animation, driven from ShowDock/HideDock via
        /// BeginAppearAnimation/BeginDisappearAnimation rather than here: this method
        /// only ever sets the *resting* position, since the animations below need to
        /// know that resting value before they can compute where to animate from.</summary>
        private void PositionDock()
        {
            // Clear any leftover Top/Left animation hold first. WPF animations default
            // to FillBehavior.HoldEnd, so even a SlideIn/SlideInHorizontal that finished
            // playing minutes ago keeps its clock attached and keeps overriding
            // GetValue(Top/LeftProperty) with its own held value — a later plain
            // assignment below would silently have no visible effect at all, still
            // showing wherever that old clock left it. (SlideOut/SlideOutHorizontal
            // already clear their own clock on completion; SlideIn/SlideInHorizontal
            // now do too, right below — this is belt-and-suspenders for the case where
            // Save's own reposition, or a fresh ShowDock, runs while an old clock is
            // still attached, possibly mid-animation.) Same reasoning BeginAppearAnimation
            // already applies to Opacity for the exact same reason.
            BeginAnimation(TopProperty, null);
            BeginAnimation(LeftProperty, null);

            var workArea = GetTargetWorkArea();

            switch (_config.Position)
            {
                case DockPosition.BottomCenter:
                    Left = workArea.Left + (workArea.Width - Width) / 2;
                    Top = workArea.Bottom - Height;
                    break;

                case DockPosition.TopCenter:
                    Left = workArea.Left + (workArea.Width - Width) / 2;
                    Top = workArea.Top + 6;
                    break;

                case DockPosition.Center:
                    Left = workArea.Left + (workArea.Width - Width) / 2;
                    Top = workArea.Top + (workArea.Height - Height) / 2;
                    break;

                case DockPosition.TopRight:
                    Left = workArea.Right - Width - 6;
                    Top = workArea.Top + 6;
                    break;

                case DockPosition.BottomRight:
                    Left = workArea.Right - Width - 6;
                    Top = workArea.Bottom - Height;
                    break;

                case DockPosition.TopLeft:
                    Left = workArea.Left + 6;
                    Top = workArea.Top + 6;
                    break;

                case DockPosition.MiddleLeft:
                    Left = workArea.Left + 6;
                    Top = workArea.Top + (workArea.Height - Height) / 2;
                    break;

                case DockPosition.MiddleRight:
                    Left = workArea.Right - Width - 6;
                    Top = workArea.Top + (workArea.Height - Height) / 2;
                    break;

                default: // BottomLeft
                    // Anchored bottom-left of the target monitor's work area, matching
                    // the Windows 10 Start Menu's default anchor point (the size is what
                    // the user resizes; the anchor corner stays fixed).
                    Left = workArea.Left + 6;
                    Top = workArea.Bottom - Height;
                    break;
            }
        }

        /// <summary>The work area (screen minus taskbar) every DockPosition case above
        /// measures against, in DIPs relative to its own monitor's top-left corner.
        ///
        /// This is deliberately NOT SystemParameters.WorkArea: that property only ever
        /// reports the *primary* monitor, and — the part that actually broke Center —
        /// converts it to DIPs using a DPI value that doesn't reliably match the DPI
        /// this Per-Monitor-V2-aware process (see app.manifest) is actually running
        /// the window at, so on a scaled display (anything other than 100%) both the
        /// horizontal and vertical center math would come out shifted by however far
        /// those two DPI figures disagreed. Center is the one position where that
        /// shows up as an obvious, both-axes miss — every corner/edge position has the
        /// exact same exposure, they just hide it better since at least one of their
        /// anchor points sits flush against a screen edge either way.
        ///
        /// The actual lookup lives in the shared MonitorHelper (SettingsWindow needed
        /// the same correctness for a different reason — capping its own height to
        /// the real screen — so this was pulled out rather than kept as a second,
        /// easy-to-drift-apart copy); this wrapper just keeps every existing call site
        /// below unchanged.
        ///
        /// Deliberately MonitorHelper.GetWorkAreaAtCursor(), not the plain
        /// GetWorkArea(this) overload: this window is constructed once and then just
        /// hidden/shown for the rest of the process's life (Visibility toggles —
        /// nothing ever re-Show()s it from scratch), so its hwnd exists continuously
        /// and GetWorkArea(this)'s own "prefer whichever monitor the window is
        /// already on" behavior would resolve against wherever the dock was last
        /// positioned, not where the user actually is right now — stale enough to
        /// reposition the dock onto a monitor the user has since turned off. See
        /// GetWorkAreaAtCursor's own doc comment for the full story; this is exactly
        /// the case it exists for.</summary>
        private Rect GetTargetWorkArea() => MonitorHelper.GetWorkAreaAtCursor();

        // How far (in DIPs) BottomCenter/TopCenter slide from, and how long every
        // appear/disappear animation below takes. "Quick" per the Windows 11 Start
        // Menu's own feel — long enough to read as motion, short enough to never feel
        // like it's in the way of actually using the dock.
        private const double SlideDistance = 40;
        private static readonly Duration AnimationDuration = new(TimeSpan.FromMilliseconds(160));

        /// <summary>Plays this dock's configured appear animation — called from
        /// ShowDock right after Visibility/Show/Activate, once Left/Top already hold
        /// their resting position (see PositionDock). BottomLeft, the original
        /// default position, deliberately keeps its plain instant appear rather than
        /// gaining an animation along with the newer positions — see DockPosition.</summary>
        private void BeginAppearAnimation()
        {
            // Always clear any leftover Opacity animation/value first — if the user
            // switched away from Center since the dock was last hidden, a Center
            // disappear's fade-to-0 (see BeginDisappearAnimation) could otherwise
            // still be holding Opacity near 0 for a position that never touches
            // Opacity itself (BottomCenter/TopCenter/BottomLeft), leaving the dock
            // invisible even though Visibility says it's showing.
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;

            switch (_config.Position)
            {
                case DockPosition.BottomCenter:
                case DockPosition.BottomRight:
                    SlideIn(fromOffset: SlideDistance); // slides up into place from below
                    break;

                case DockPosition.TopCenter:
                case DockPosition.TopRight:
                case DockPosition.TopLeft:
                    SlideIn(fromOffset: -SlideDistance); // slides down into place from above
                    break;

                case DockPosition.Center:
                    Opacity = 0;
                    BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, AnimationDuration));
                    break;

                case DockPosition.MiddleLeft:
                    SlideInHorizontal(fromOffset: -SlideDistance); // slides in from off-screen to the left
                    break;

                case DockPosition.MiddleRight:
                    SlideInHorizontal(fromOffset: SlideDistance); // slides in from off-screen to the right
                    break;

                default: // BottomLeft — instant, no animation at all
                    break;
            }
        }

        /// <summary>The mirror image of BeginAppearAnimation, called from HideDock
        /// instead of setting Visibility=Hidden directly — every branch still ends up
        /// setting Visibility=Hidden itself, just after its own animation finishes
        /// rather than immediately.</summary>
        private void BeginDisappearAnimation()
        {
            switch (_config.Position)
            {
                case DockPosition.BottomCenter:
                case DockPosition.BottomRight:
                    SlideOut(toOffset: SlideDistance);
                    break;

                case DockPosition.TopCenter:
                case DockPosition.TopRight:
                case DockPosition.TopLeft:
                    SlideOut(toOffset: -SlideDistance);
                    break;

                case DockPosition.Center:
                    var fade = new DoubleAnimation(Opacity, 0, AnimationDuration);
                    fade.Completed += (_, _) => Visibility = Visibility.Hidden;
                    BeginAnimation(OpacityProperty, fade);
                    break;

                case DockPosition.MiddleLeft:
                    SlideOutHorizontal(toOffset: -SlideDistance); // slides back out to the left
                    break;

                case DockPosition.MiddleRight:
                    SlideOutHorizontal(toOffset: SlideDistance); // slides back out to the right
                    break;

                default: // BottomLeft
                    Visibility = Visibility.Hidden;
                    break;
            }
        }

        private void SlideIn(double fromOffset)
        {
            double restingTop = Top;
            var anim = new DoubleAnimation
            {
                From = restingTop + fromOffset,
                To = restingTop,
                Duration = AnimationDuration,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            anim.Completed += (_, _) =>
            {
                // Mirrors SlideOut's own Completed handler below — without this, this
                // animation's clock stays attached (WPF's default FillBehavior.HoldEnd)
                // even after it's visually finished, so it keeps overriding
                // GetValue(TopProperty) with its own held value — a later plain "Top = "
                // assignment (PositionDock, after a Settings Save, say) would silently
                // have no visible effect until this clock is explicitly cleared. See
                // PositionDock's own belt-and-suspenders clear of this too.
                BeginAnimation(TopProperty, null);
                Top = restingTop;
            };
            BeginAnimation(TopProperty, anim);
        }

        private void SlideOut(double toOffset)
        {
            double restingTop = Top;
            var anim = new DoubleAnimation
            {
                From = restingTop,
                To = restingTop + toOffset,
                Duration = AnimationDuration,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            };
            anim.Completed += (_, _) =>
            {
                Visibility = Visibility.Hidden;
                // Release the animation's hold on Top and put it back exactly at its
                // resting value — otherwise the next ShowDock's PositionDock call would
                // be fighting a still-active animation still holding Top at the
                // slid-away value.
                BeginAnimation(TopProperty, null);
                Top = restingTop;
            };
            BeginAnimation(TopProperty, anim);
        }

        /// <summary>MiddleLeft/MiddleRight's counterpart to SlideIn — those two
        /// positions have no top/bottom work-area edge to slide in from (they're
        /// vertically centered against a side edge instead), so they animate Left
        /// rather than Top.</summary>
        private void SlideInHorizontal(double fromOffset)
        {
            double restingLeft = Left;
            var anim = new DoubleAnimation
            {
                From = restingLeft + fromOffset,
                To = restingLeft,
                Duration = AnimationDuration,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            anim.Completed += (_, _) =>
            {
                // Same reasoning as SlideIn's own Completed handler above, but for Left.
                BeginAnimation(LeftProperty, null);
                Left = restingLeft;
            };
            BeginAnimation(LeftProperty, anim);
        }

        /// <summary>MiddleLeft/MiddleRight's counterpart to SlideOut — see
        /// SlideInHorizontal for why this animates Left instead of Top.</summary>
        private void SlideOutHorizontal(double toOffset)
        {
            double restingLeft = Left;
            var anim = new DoubleAnimation
            {
                From = restingLeft,
                To = restingLeft + toOffset,
                Duration = AnimationDuration,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            };
            anim.Completed += (_, _) =>
            {
                Visibility = Visibility.Hidden;
                // Same reasoning as SlideOut's Completed handler above, but for Left.
                BeginAnimation(LeftProperty, null);
                Left = restingLeft;
            };
            BeginAnimation(LeftProperty, anim);
        }

        private void Window_Deactivated(object sender, EventArgs e) => HideDock();

        /// <summary>The real logic behind ClickOutsideService's global click-dismiss
        /// mechanism (see App.xaml.cs for the wiring, and ClickOutsideService's own
        /// doc comment for why Window_Deactivated above isn't enough on its own) —
        /// given a click's physical screen coordinates, true if it landed on
        /// something that actually counts as "the dock": a tile, a menu-bar button,
        /// the search box, a resize grip, or the open power flyout. Anything else —
        /// off the window entirely, in the transparent margin around BackgroundBorder,
        /// or over BackgroundBorder's own (possibly fully invisible, at 0%
        /// BackgroundOpacity) fill with nothing drawn on top of it — comes back
        /// false, and ClickOutsideService's caller (App.xaml.cs) treats that as
        /// equivalent to a click outside and hides the dock.</summary>
        public bool IsDockContentAt(Point screenPoint)
        {
            // A modal child (Settings, the Add-tile file picker) genuinely can steal
            // the click here — it's a separate top-level window, quite possibly
            // positioned somewhere outside this window's own bounds entirely, which
            // the plain bounds-check below would otherwise misread as "outside the
            // dock" and wrongly hide it out from under whatever dialog the user is
            // actually using. Same guard Window_Deactivated already relies on
            // (_dialogOpen), just consulted here too rather than only there.
            if (_dialogOpen)
                return true;

            if (PowerFlyoutPopup.IsOpen)
            {
                // The flyout renders in its own top-level Popup surface — placed
                // above PowerButtonElement, so not necessarily even within this
                // window's own Left/Top/Width/Height — rather than as part of this
                // window's own visual tree, so InputHitTest below could never find
                // it anyway. WPF's own Popup(StaysOpen="False") already dismisses
                // itself correctly on an outside click without any help from here;
                // this just stays out of its way instead of risking a false
                // "outside" verdict for a click that's actually on Lock/Sleep/etc.
                return true;
            }

            Point windowPoint;
            try
            {
                windowPoint = PointFromScreen(screenPoint);
            }
            catch
            {
                // No real hwnd to translate against (shouldn't happen — this is only
                // ever called while IsDockVisible reported true — but never treat a
                // failure here as a false positive that could wrongly hide the dock).
                return true;
            }

            if (windowPoint.X < 0 || windowPoint.Y < 0 || windowPoint.X > ActualWidth || windowPoint.Y > ActualHeight)
                return false; // genuinely outside this window's own bounds

            return IsRealDockContent(InputHitTest(windowPoint) as DependencyObject);
        }

        /// <summary>Walks up from a hit-test result looking for real dock content.
        ///
        /// A previous version of this method only recognized a hand-picked allowlist
        /// of "interactive" types (Button, TextBox, Thumb/ScrollBar, the two named
        /// resize grips) — everything else, including plain background chrome, fell
        /// through to false. That was the actual bug behind "the icons on the bottom
        /// right don't have a consistent hitbox": ContentGrid and MenuBarRow are both
        /// plain Grids with no Background of their own (see MainWindow.xaml), so WPF
        /// doesn't hit-test them at all at a point with no child drawn there — a click
        /// landing in the few pixels of margin *between* two menu-bar buttons (or on a
        /// category banner's padding, or anywhere else that isn't literally a Button/
        /// TextBox) falls straight through to BackgroundBorder underneath, which the
        /// old allowlist didn't recognize as dock content at all. That misclassified a
        /// near-miss click as "outside" and closed the dock before the button's own
        /// Click event could ever fire — exactly "have to click close to the center of
        /// the square" (only dead-center clicks reliably landed on the Button itself;
        /// anything nearer an edge had a real chance of landing on that gap instead).
        ///
        /// BackgroundBorder is genuinely special, though: it's the one element
        /// BackgroundOpacity fades toward fully invisible (see its own comment in
        /// MainWindow.xaml) — every other real element (tiles, buttons, the search
        /// box, the menu bar, category banners, the resize grips) lives in
        /// ContentGrid, a separate sibling that's never faded, so landing on
        /// BackgroundBorder itself means there's genuinely nothing else drawn at that
        /// point. Below a small threshold that's meant to behave exactly like the
        /// fully transparent margin around it (see ApplyWindowRegion) — items (f)/(g)
        /// need a click there to count as "outside" and close the dock, same as one
        /// that lands on truly empty space. Above that threshold, it's the dock's own
        /// visible panel and clicking it shouldn't dismiss the dock any more than
        /// clicking a tile would.
        ///
        /// So the rule simplifies to: anything hit-testable at all, other than
        /// BackgroundBorder while it's faded down near-invisible, counts as dock
        /// content — no more naming individual control types one at a time.</summary>
        private bool IsRealDockContent(DependencyObject? element)
        {
            if (element == null)
                return false; // nothing here at all — the transparent shadow margin, or off the window entirely

            while (element != null && !ReferenceEquals(element, this))
            {
                if (ReferenceEquals(element, BackgroundBorder))
                    return BackgroundBorder.Opacity > 0.05;

                element = VisualTreeHelper.GetParent(element);
            }

            // Walked all the way up through ContentGrid (a button, banner, resize
            // grip, divider, or any other real, never-faded content) without ever
            // passing through BackgroundBorder — definitely real dock content.
            return true;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                HideDock();
                return;
            }

            HandleGridKeyNavigation(e);
        }

        /// <summary>Routes a typed character to the search box no matter what
        /// currently has focus — the "just start typing" feel the native Start
        /// Menu has — reveals the search row if HideSearchBar had it collapsed
        /// (see UpdateSearchBarVisibility), and works regardless of HideSearchBar's
        /// value at all: it used to only ever do this while that setting was on,
        /// which is exactly why typing right after opening the dock landed in the
        /// search box inconsistently rather than reliably — with the setting off,
        /// this handler used to do nothing at all and just hope SearchBox already
        /// had real keyboard focus (see ShowDock's own fix for the other half of
        /// that same bug).
        ///
        /// The one thing this always defers to instead: a TextBox that's already
        /// mid-edit, most importantly a category or folder's own rename box (see
        /// CategoryNameBox/FolderNameBox) — typing a new name for either of those
        /// must reach that box, not get hijacked into search out from under the
        /// user. That box's own normal (non-preview, bubvbling) routing already
        /// delivers the keystroke there on its own; this handler (a *Preview*,
        /// tunneling event, which always fires here first regardless of who
        /// actually has focus) just needs to get out of the way rather than also
        /// acting on it. Once focus leaves that TextBox — including simply
        /// clicking away from it — this resumes routing typing to search again,
        /// with nothing further to reset.</summary>
        private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox)
                return;

            // Browsing inside a folder replaces the search row with FolderHeaderGrid
            // (see OpenFolder) — there's no search-within-a-folder feature for stray
            // typing to land in, so this leaves it alone entirely, same as before.
            if (_openFolder != null)
                return;

            // Keys like Backspace, Tab, Esc and Ctrl+letter also arrive here, as
            // invisible control characters. Typing one of those into the search box
            // used to start a search for it — showing a "Search the web for """ tile
            // with nothing visible in the box. Only real, printable text counts.
            if (string.IsNullOrEmpty(e.Text) || e.Text.All(char.IsControl))
                return;

            SearchGrid.Visibility = Visibility.Visible;
            SearchBox.Focus();
            SearchBox.Text = e.Text; // triggers SearchBox_TextChanged normally, same as if typed directly
            SearchBox.CaretIndex = SearchBox.Text.Length;
            e.Handled = true; // already delivered — don't let it fall through anywhere else
        }

        /// <summary>Shows or collapses the search row per the HideSearchBar setting —
        /// called whenever the dock is about to sit in "no folder open" territory
        /// with nothing typed (ShowDock, CloseFolder, and SearchBox_TextChanged
        /// clearing back to empty). With the setting off, or a folder open, or text
        /// already in the box, this always just shows it — exactly how the search row
        /// behaved before HideSearchBar existed.</summary>
        private void UpdateSearchBarVisibility()
        {
            bool hide = _config.HideSearchBar && _openFolder == null && string.IsNullOrEmpty(SearchBox.Text);
            SearchGrid.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;

            if (_openFolder != null)
                return; // folder header owns focus in that case — see OpenFolder

            if (hide)
                Keyboard.Focus(this); // nothing inside the (now collapsed) row can hold focus — see Window_PreviewTextInput
            else
                Keyboard.Focus(SearchBox);
        }

        // ---------------------------------------------------------------
        // Keyboard navigation (arrow keys + Enter) across the icon grid
        // ---------------------------------------------------------------

        private void HandleGridKeyNavigation(KeyEventArgs e)
        {
            // Down/Enter from the search box should behave the same way when the
            // search row is hidden (HideSearchBar) and the window itself is holding
            // focus in its place — see UpdateSearchBarVisibility — as when SearchBox
            // is focused directly, rather than silently doing nothing until the user
            // types something first.
            bool searchFocused = Keyboard.FocusedElement == SearchBox ||
                (SearchGrid.Visibility != Visibility.Visible && ReferenceEquals(Keyboard.FocusedElement, this));
            bool tileFocused = Keyboard.FocusedElement is Button { Tag: DockIconViewModel };

            switch (e.Key)
            {
                case Key.Down when searchFocused:
                    FocusTile(FirstVisibleTile());
                    e.Handled = true;
                    break;

                case Key.Down when tileFocused:
                    MoveTileFocus(FocusNavigationDirection.Down);
                    e.Handled = true;
                    break;

                case Key.Up when tileFocused:
                    // Leaving the top row of the grid goes back up to whatever's shown
                    // in the header row (search box, or the folder Back button when
                    // browsing inside a folder), rather than doing nothing.
                    if (!MoveTileFocus(FocusNavigationDirection.Up))
                        Keyboard.Focus(_openFolder != null ? (IInputElement)FolderBackButton : SearchBox);
                    e.Handled = true;
                    break;

                case Key.Left when tileFocused:
                    MoveTileFocus(FocusNavigationDirection.Left);
                    e.Handled = true;
                    break;

                case Key.Right when tileFocused:
                    MoveTileFocus(FocusNavigationDirection.Right);
                    e.Handled = true;
                    break;

                case Key.Enter when searchFocused:
                    // Mirrors the native Start Menu: Enter from the search box launches
                    // the first (top-left) result currently shown in the grid.
                    LaunchTile(FirstVisibleTile());
                    e.Handled = true;
                    break;

                case Key.Enter when tileFocused:
                    if (Keyboard.FocusedElement is Button { Tag: DockIconViewModel vm })
                    {
                        LaunchTile(vm);
                    }
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>Moves keyboard focus one tile over using WPF's geometry-based
        /// directional navigation. Returns false if there was nowhere to go (e.g.
        /// already on the top row and moving Up) so the caller can fall back.</summary>
        private static bool MoveTileFocus(FocusNavigationDirection direction)
        {
            if (Keyboard.FocusedElement is not UIElement focused)
                return false;

            var next = focused.PredictFocus(direction) as UIElement;
            if (next == null)
                return false;

            next.Focus();
            return true;
        }

        /// <summary>The tile that "jump to the first result" keyboard actions
        /// (Down from an empty search box, Enter from the search box) should land
        /// on: whatever's visually first in the grid currently shown. For the
        /// category view that's the first icon of the first category — both
        /// categories and a category's own icons are plain dense lists now, so
        /// list order already matches visual order (see StartDock.Models.Category)
        /// — for search results or a folder's contents, both plain flow layouts,
        /// it's simply the first item.</summary>
        private DockIconViewModel? FirstVisibleTile()
        {
            if (_openFolder == null && string.IsNullOrEmpty(SearchBox.Text))
                return _categories.SelectMany(c => c.Icons).FirstOrDefault();

            return IconItemsControl.Items.OfType<DockIconViewModel>().FirstOrDefault();
        }

        private void FocusTile(DockIconViewModel? vm)
        {
            if (vm == null)
                return;

            var hostingItemsControl = FindHostingItemsControl(vm);
            if (hostingItemsControl == null)
                return;

            hostingItemsControl.UpdateLayout();
            if (hostingItemsControl.ItemContainerGenerator.ContainerFromItem(vm) is DependencyObject container)
                FindVisualChild<Button>(container)?.Focus();
        }

        /// <summary>Finds the ItemsControl that actually hosts <paramref name="vm"/>'s
        /// own generated container. Needed because a category-view tile now lives
        /// inside a per-category nested ItemsControl (see MainWindow.xaml's
        /// CategoriesItemsControl.ItemTemplate), and
        /// ItemContainerGenerator.ContainerFromItem only resolves items that are
        /// direct children of the ItemsControl it's called on — not items nested
        /// inside another ItemsControl further down the visual tree. Search results
        /// and a folder's own contents are still flat (IconItemsControl hosts them
        /// directly), so those short-circuit here without touching the category
        /// tree at all.</summary>
        private ItemsControl? FindHostingItemsControl(DockIconViewModel vm)
        {
            if (_openFolder != null || !string.IsNullOrEmpty(SearchBox.Text))
                return IconItemsControl;

            var category = FindCategoryContaining(vm);
            if (category == null)
                return null;

            CategoriesItemsControl.UpdateLayout();
            if (CategoriesItemsControl.ItemContainerGenerator.ContainerFromItem(category) is not DependencyObject categoryContainer)
                return null;

            return FindVisualChild<ItemsControl>(categoryContainer);
        }

        private void LaunchTile(DockIconViewModel? vm)
        {
            if (vm == null)
                return;

            if (vm.IsFolder)
            {
                OpenFolder(vm);
                return;
            }

            string target = vm.Model.TargetPath ?? string.Empty;

            // Calculator result: copy the answer, the way Windows' Start search does.
            if (target.StartsWith(CalcTargetPrefix, StringComparison.Ordinal))
            {
                try { Clipboard.SetText(target.Substring(CalcTargetPrefix.Length)); } catch { /* clipboard busy — nothing to do */ }
                HideDock();
                return;
            }

            // "Search the web for …": open the default browser.
            if (target.StartsWith(WebTargetPrefix, StringComparison.Ordinal))
            {
                OpenWebSearch(target.Substring(WebTargetPrefix.Length));
                HideDock();
                return;
            }

            AppLauncher.Launch(vm.Model);
            RecordAppLaunch(target);
            HideDock();
        }

        /// <summary>Counts a launch from StartDock — any tile: an installed app, a
        /// pinned program or file, a Settings page. Feeds "Recently used" and puts
        /// the things you open most first in search results (see UsageScore).</summary>
        private void RecordAppLaunch(string target)
        {
            string key = AppUsageService.KeyFor(target);
            if (key.Length == 0)
                return;
            AppUsageService.RecordLaunch(key);

            // Keep the search ranking's copy current too, without re-reading it all.
            if (_usage != null)
            {
                _usage.TryGetValue(key, out var u);
                _usage[key] = new AppUsageService.Usage(u.Count + 1, DateTime.UtcNow);
            }
        }

        // ---------------------------------------------------------------
        // Search ranking: the apps you open most come first
        // ---------------------------------------------------------------

        // How often and how recently things were opened (AppUsageService.GetUsage —
        // StartDock's own launches plus Windows' launch history), re-read in the
        // background each time the dock opens. Null until the first read finishes.
        private Dictionary<string, AppUsageService.Usage>? _usage;
        private bool _usageRefreshing;

        private async void RefreshUsageAsync()
        {
            if (_usageRefreshing)
                return;
            _usageRefreshing = true;
            try
            {
                _usage = await Task.Run(() => AppUsageService.GetUsage());
            }
            catch
            {
                // No ranking data this time — results just sort by name.
            }
            finally
            {
                _usageRefreshing = false;
            }
        }

        /// <summary>How strongly a search result has been used: its launch count,
        /// weighted towards recent use (full weight if used in the last week, half
        /// in the last month, a quarter before that), so an app you've switched to
        /// lately overtakes one you used to open a lot. 0 if never opened.</summary>
        private double UsageScore(DockIconViewModel vm)
        {
            if (_usage == null || vm.IsFolder)
                return 0;
            string key = AppUsageService.KeyFor(vm.Model.TargetPath);
            if (key.Length == 0 || !_usage.TryGetValue(key, out var u) || u.Count <= 0)
                return 0;

            double days = (DateTime.UtcNow - u.LastUsedUtc).TotalDays;
            double weight = days <= 7 ? 1.0 : days <= 30 ? 0.5 : 0.25;
            return u.Count * weight;
        }

        /// <summary>0 when the search text starts the name or any word in it
        /// ("Power" → "Windows PowerShell"), 1 for any other match.</summary>
        private static int MatchTier(string name, string filter)
        {
            int index = name.IndexOf(filter, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                if (index == 0 || !char.IsLetterOrDigit(name[index - 1]))
                    return 0;
                index = name.IndexOf(filter, index + 1, StringComparison.OrdinalIgnoreCase);
            }
            return 1;
        }

        // ---------------------------------------------------------------
        // Search actions: calculator answer and web search
        // ---------------------------------------------------------------

        // Internal markers for the two built-in action tiles — never real paths, and
        // never saved anywhere (these tiles can't be pinned).
        private const string CalcTargetPrefix = "startdock:calc:";
        private const string WebTargetPrefix = "startdock:web:";

        private DockIconViewModel CreateCalculatorTile(double value)
        {
            string answer = Calculator.Format(value);
            var model = new DockIcon { Name = "= " + answer, TargetPath = CalcTargetPrefix + answer };
            return new DockIconViewModel(model, isSearchResult: true)
            {
                IsAction = true,
                IconImage = CreateGlyphImage("\uE8EF"), // Calculator
            };
        }

        private DockIconViewModel CreateWebSearchTile(string query)
        {
            var model = new DockIcon { Name = $"Search the web for \"{query}\"", TargetPath = WebTargetPrefix + query };
            return new DockIconViewModel(model, isSearchResult: true)
            {
                IsAction = true,
                IconImage = CreateGlyphImage("\uE774"), // Globe
            };
        }

        private void OpenWebSearch(string query)
        {
            string q = Uri.EscapeDataString(query);
            string url = _config.WebSearchEngine switch
            {
                WebSearchEngine.Bing => $"https://www.bing.com/search?q={q}",
                WebSearchEngine.DuckDuckGo => $"https://duckduckgo.com/?q={q}",
                _ => $"https://www.google.com/search?q={q}",
            };
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // No default browser registered — nothing more to do.
            }
        }

        /// <summary>A tile icon drawn from one of the Segoe Fluent Icons / MDL2 glyphs
        /// the menu bar already uses, in the dock's current text color.</summary>
        private ImageSource? CreateGlyphImage(string glyph)
        {
            try
            {
                var color = (TryFindResource("PrimaryTextBrush") as SolidColorBrush)?.Color ?? Colors.Gray;
                var brush = new SolidColorBrush(color);
                brush.Freeze();

                var family = TryFindResource("IconFont") as FontFamily ?? new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
                var text = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    64, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

                var image = new DrawingImage(new GeometryDrawing(brush, null, text.BuildGeometry(new Point(0, 0))));
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed)
                    return typed;

                var found = FindVisualChild<T>(child);
                if (found != null)
                    return found;
            }
            return null;
        }

        // ---------------------------------------------------------------
        // Icon grid
        // ---------------------------------------------------------------

        private void LoadIconsFromConfig()
        {
            _categories.Clear();
            foreach (var category in _config.Categories)
            {
                var categoryVm = new CategoryViewModel(category);
                _categories.Add(categoryVm);

                foreach (var iconVm in categoryVm.Icons)
                    LoadIconOrFolderImagesAsync(iconVm);
            }

            RefreshEmptyState();
        }

        /// <summary>Loads a tile's own icon, or — for a folder tile, which has no icon
        /// of its own — each of its children's icons (used for the folder's mini preview
        /// and its browse view).</summary>
        private void LoadIconOrFolderImagesAsync(DockIconViewModel vm)
        {
            if (vm.IsFolder)
            {
                foreach (var child in vm.Children)
                    LoadIconImageAsync(child);
            }
            else
            {
                LoadIconImageAsync(vm);
            }
        }

        private void LoadIconImageAsync(DockIconViewModel vm)
        {
            try
            {
                string? path = vm.Model.CachedIconPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    path = _iconExtractor.ExtractAndCache(vm.Model.TargetPath, vm.Model.Id);
                    if (path != null)
                    {
                        vm.Model.CachedIconPath = path;
                        PersistConfig();
                    }
                }

                if (path != null && File.Exists(path))
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(path, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    vm.IconImage = bitmap;
                }
            }
            catch
            {
                // A single bad icon shouldn't block the rest of the grid from loading.
            }
        }

        private void RefreshEmptyState()
        {
            if (_openFolder != null)
            {
                EmptyStateText.Text = "This folder is empty.";
                EmptyStateText.Visibility = _openFolder.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            if (!string.IsNullOrEmpty(SearchBox.Text))
            {
                EmptyStateText.Text = "No matching apps found.";
                EmptyStateText.Visibility = _searchResults.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            EmptyStateText.Text = "No apps pinned yet.\nClick the + next to the search box to add one.";
            EmptyStateText.Visibility = _categories.All(c => c.Icons.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------------------------------------------------------
        // Search (pinned tiles + every app installed on the computer)
        // ---------------------------------------------------------------

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Strip any invisible control characters that got into the box (pasted,
            // say) so they can't turn into a search for "nothing" — see
            // Window_PreviewTextInput. Clearing them re-raises this event with the
            // cleaned text.
            if (SearchBox.Text.Any(char.IsControl))
            {
                int caret = SearchBox.CaretIndex;
                string cleaned = new string(SearchBox.Text.Where(c => !char.IsControl(c)).ToArray());
                SearchBox.Text = cleaned;
                SearchBox.CaretIndex = Math.Min(caret, cleaned.Length);
                return;
            }

            string filter = SearchBox.Text.Trim();
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(filter) ? Visibility.Visible : Visibility.Collapsed;

            if (string.IsNullOrEmpty(filter))
            {
                ShowCategoryGrid();
                UpdateSearchBarVisibility(); // cleared back to empty — re-collapse if HideSearchBar is on
                RefreshEmptyState();
                return;
            }

            ShowFlowGrid(_searchResults, isSearch: true);

            // Instant partial results from whatever's already cached (pinned tiles
            // always; installed apps too, once a search or the "+" picker has loaded
            // them at least once this session) while the full list, if needed, loads.
            RenderSearchResults(filter, _lastInstalledApps);
            _ = SearchInstalledAppsAsync(filter);
            _ = SearchFilesAsync(filter);
        }

        // File search (AppConfig.SearchFiles): the latest results, and the search text
        // they were for. While a newer search is still running, the previous results
        // keep showing (narrowed to what still matches) so they don't flicker away on
        // every keystroke.
        private List<DockIconViewModel> _fileResults = new();
        private string _fileResultsFilter = string.Empty;
        private int _fileSearchVersion;
        private readonly Dictionary<string, ImageSource?> _fileIcons = new(StringComparer.OrdinalIgnoreCase);
        private const int MaxFileResults = 8;
        private static readonly TimeSpan FileSearchDelay = TimeSpan.FromMilliseconds(250);

        /// <summary>Queries Windows' search index for files matching the search text —
        /// on a background thread, and only once typing has paused for a moment, so a
        /// fast typist doesn't fire a query per keystroke and the dock never waits on
        /// it. Results are added after the app results when they arrive.</summary>
        private async Task SearchFilesAsync(string filter)
        {
            int version = ++_fileSearchVersion;

            if (!_config.SearchFiles || filter.Length < 2)
            {
                _fileResults = new List<DockIconViewModel>();
                _fileResultsFilter = filter;
                return;
            }

            await Task.Delay(FileSearchDelay);
            if (version != _fileSearchVersion)
                return; // more typing happened — that search will run instead

            List<(FileSearchService.FileResult File, string? IconPath, ImageSource? Icon)> found;
            try
            {
                var knownIcons = new Dictionary<string, ImageSource?>(_fileIcons, StringComparer.OrdinalIgnoreCase);
                found = await Task.Run(() =>
                {
                    var list = new List<(FileSearchService.FileResult, string?, ImageSource?)>();
                    foreach (var file in FileSearchService.Search(filter, MaxFileResults))
                    {
                        if (knownIcons.TryGetValue(file.Path, out var cachedIcon))
                        {
                            list.Add((file, null, cachedIcon));
                            continue;
                        }
                        Guid id = IconExtractor.StableId("file:" + file.Path);
                        string? iconPath = _iconExtractor.GetCachedPath(id);
                        if (!File.Exists(iconPath))
                            iconPath = _iconExtractor.ExtractAndCache(file.Path, id);
                        list.Add((file, iconPath, LoadFrozenImage(iconPath)));
                    }
                    return list;
                });
            }
            catch
            {
                return;
            }

            if (version != _fileSearchVersion || SearchBox.Text.Trim() != filter)
                return;

            var tiles = new List<DockIconViewModel>();
            foreach (var (file, iconPath, icon) in found)
            {
                _fileIcons[file.Path] = icon;
                var model = new DockIcon
                {
                    Name = file.Name,
                    TargetPath = file.Path,
                    CachedIconPath = iconPath ?? string.Empty,
                };
                tiles.Add(new DockIconViewModel(model, isSearchResult: true) { IsFile = true, IconImage = icon });
            }

            _fileResults = tiles;
            _fileResultsFilter = filter;
            RenderSearchResults(filter, _lastInstalledApps);
        }

        /// <summary>Fetches (or reuses the session cache of) every app installed on the
        /// computer and re-renders the search results once it's ready — matching the
        /// native Start Menu's "search finds anything installed, not just pinned apps."</summary>
        private async Task SearchInstalledAppsAsync(string filter)
        {
            var installedApps = await InstalledAppsCache.GetAppsAsync();
            _lastInstalledApps = installedApps;

            if (SearchBox.Text.Trim() != filter)
                return; // a newer keystroke already replaced this search

            RenderSearchResults(filter, installedApps);

            // Icons for apps nobody has searched or pinned yet this session may still
            // be extracting — a no-op once everything's already been loaded once.
            await InstalledAppsCache.EnsureIconsLoadedAsync(_iconExtractor);

            if (SearchBox.Text.Trim() != filter)
                return;

            RenderSearchResults(filter, installedApps);
        }

        /// <summary>Rebuilds <see cref="_searchResults"/>: every pinned tile (including
        /// folders, across every category) whose name matches, plus every other
        /// installed app that matches and isn't already pinned — shown as transient,
        /// launchable-but-not-yet-pinned tiles (see <see cref="CreateSearchResultTile"/>).
        /// Best matches (name starts with the query) sort first, mirroring the native
        /// Start Menu's search.</summary>
        private void RenderSearchResults(string filter, ObservableCollection<InstalledAppViewModel>? installedApps)
        {
            var results = new List<DockIconViewModel>();
            var pinned = _categories.SelectMany(c => c.Icons).ToList();

            // Pinned matches reuse the live view-models directly, so clicking,
            // right-clicking, or opening a matching folder all behave exactly as they do
            // in the main grid.
            foreach (var vm in pinned)
            {
                if (vm.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    results.Add(vm);
            }

            if (installedApps != null)
            {
                var pinnedAppIds = new HashSet<string>(
                    pinned
                        .Where(v => !v.IsFolder && v.Model.TargetPath.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
                        .Select(v => v.Model.TargetPath.Substring("shell:AppsFolder\\".Length)),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var app in installedApps)
                {
                    if (pinnedAppIds.Contains(app.Model.AppId))
                        continue; // already listed above as a pinned tile

                    if (!app.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    results.Add(CreateSearchResultTile(app));
                }
            }

            // Windows Settings pages (Bluetooth, Wi-Fi, Display, ...) — see
            // WindowsSettingsCatalog's own doc comment. Matched independently of
            // pinned tiles/installed apps above (there's no overlap to dedupe:
            // nothing here can already be "pinned" the way an installed app can) and
            // just added into the same results list, so they sort into place
            // alongside everything else by the ordering right below rather than
            // getting a separate section of their own.
            foreach (var entry in WindowsSettingsCatalog.Search(filter))
                results.Add(CreateSettingsResultTile(entry));

            // Best matches first (the text starts the name or a word in it), and
            // within those, what you open most (UsageScore) — so after a few uses,
            // "Power" puts PowerShell first instead of whatever sorts first by name.
            // Then whole-name matches, then alphabetical.
            var ordered = results
                .OrderBy(v => MatchTier(v.Name, filter))
                .ThenByDescending(UsageScore)
                .ThenBy(v => v.Name.StartsWith(filter, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .Take(24) // enough to fill several rows without overwhelming the grid
                .ToList();

            // Calculator answer first, so Enter copies it straight away.
            if (_config.SearchCalculator && Calculator.TryEvaluate(filter, out double answer))
                ordered.Insert(0, CreateCalculatorTile(answer));

            // Files go after apps and Settings pages, in their own order (most recently
            // modified first, as the index returned them). Results from a slightly
            // older search text are reused, narrowed to what still matches, until the
            // search for this exact text comes back — see SearchFilesAsync.
            if (_config.SearchFiles)
            {
                var files = _fileResultsFilter == filter
                    ? _fileResults
                    : _fileResults.Where(f => f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
                ordered.AddRange(files);
            }

            // Web search last — only does anything if clicked; nothing is sent while typing.
            if (_config.SearchWeb)
                ordered.Add(CreateWebSearchTile(filter));

            _searchResults.Clear();
            foreach (var vm in ordered)
                _searchResults.Add(vm);

            RefreshEmptyState();
        }

        /// <summary>Wraps an installed-but-not-pinned app as a transient, launchable
        /// tile for the search grid. Reuses the icon InstalledAppsCache already
        /// extracted (if ready) instead of extracting a second copy.</summary>
        private static DockIconViewModel CreateSearchResultTile(InstalledAppViewModel app)
        {
            var transientModel = new DockIcon
            {
                Name = app.Model.Name,
                TargetPath = $"shell:AppsFolder\\{app.Model.AppId}",
                CachedIconPath = app.CachedIconPath ?? string.Empty,
            };

            return new DockIconViewModel(transientModel, isSearchResult: true) { IconImage = app.IconImage };
        }

        // Lazily extracted, once per process, and shared by every Windows-Settings
        // search-result tile — see EnsureWindowsSettingsIconLoaded. _windowsSettingsIconPath
        // being non-null (even as string.Empty, meaning "tried and failed") is what
        // marks the attempt as already made, so a search that keeps matching a
        // settings page on every keystroke doesn't re-run shell icon extraction each time.
        private string? _windowsSettingsIconPath;
        private ImageSource? _windowsSettingsIconImage;

        /// <summary>Wraps a WindowsSettingsCatalog entry as a transient, launchable
        /// search-result tile — the settings-page counterpart to
        /// CreateSearchResultTile right above. Every settings tile shares the one
        /// icon EnsureWindowsSettingsIconLoaded extracts (the real Settings app's own
        /// gear icon), same as every "app not yet found an icon" case elsewhere just
        /// shows blank rather than a per-tile icon of its own — there's no
        /// per-settings-page icon to extract here, these aren't real files.</summary>
        private DockIconViewModel CreateSettingsResultTile(WindowsSettingsCatalog.Entry entry)
        {
            EnsureWindowsSettingsIconLoaded();

            var transientModel = new DockIcon
            {
                Name = entry.Name,
                TargetPath = entry.Uri,
                // Set (not left blank) so PinSearchResult_Click's own
                // CachedIconPath-forwarding carries the icon over correctly if the
                // user pins this result — see AddIcon's preResolvedIconPath handling.
                CachedIconPath = _windowsSettingsIconPath ?? string.Empty,
            };

            return new DockIconViewModel(transientModel, isSearchResult: true) { IconImage = _windowsSettingsIconImage };
        }

        private void EnsureWindowsSettingsIconLoaded()
        {
            if (_windowsSettingsIconPath != null)
                return; // already attempted this session, success or failure alike

            _windowsSettingsIconPath = string.Empty; // mark "attempted" up front so a failure below can't retry every keystroke
            try
            {
                string exePath = Environment.ExpandEnvironmentVariables(WindowsSettingsCatalog.SettingsAppExePath);
                string? cached = _iconExtractor.ExtractAndCache(exePath, WindowsSettingsCatalog.SettingsIconId);
                if (string.IsNullOrEmpty(cached) || !File.Exists(cached))
                    return;

                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(cached, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                _windowsSettingsIconPath = cached;
                _windowsSettingsIconImage = bitmap;
            }
            catch
            {
                // Leave _windowsSettingsIconPath as string.Empty (attempted, no icon) —
                // the tile still works, launching its ms-settings: URI just fine, it
                // simply shows no icon, the same graceful fallback every other icon
                // extraction failure in this app already gets.
            }
        }

        private void PinSearchResult_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: DockIconViewModel vm } || !vm.IsSearchResult)
                return;

            AddIcon(vm.Model.Name, vm.Model.TargetPath,
                string.IsNullOrEmpty(vm.Model.CachedIconPath) ? null : vm.Model.CachedIconPath);

            // Refresh so this result now shows as pinned (its context menu switches
            // from "Pin to StartDock" to "Remove from StartDock").
            string filter = SearchBox.Text.Trim();
            if (!string.IsNullOrEmpty(filter))
                RenderSearchResults(filter, _lastInstalledApps);

            // A just-pinned app drops out of "Recently added" (it's pinned now).
            UpdateAutoSections();
        }

        private void AppTile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: DockIconViewModel vm })
                LaunchTile(vm);
        }

        // ---------------------------------------------------------------
        // Folders
        // ---------------------------------------------------------------

        private void OpenFolder(DockIconViewModel folderVm)
        {
            _openFolder = folderVm;
            FolderNameBox.Text = folderVm.Name;

            // A folder's own children are already a plain, dense list (DockIcon no
            // longer has any kind of grid-slot field at all — see Models/DockIcon.cs)
            // so, unlike before, there's nothing here to renumber before showing it.
            ShowFlowGrid(folderVm.Children, isSearch: false);
            SearchGrid.Visibility = Visibility.Collapsed;
            FolderHeaderGrid.Visibility = Visibility.Visible;
            RefreshEmptyState();
        }

        private void CloseFolder()
        {
            _openFolder = null;
            FolderHeaderGrid.Visibility = Visibility.Collapsed;

            // Resume the search filter if the box still has text in it (e.g. the
            // folder was opened by clicking a search result) instead of silently
            // reverting to the full category view while the box still shows a query.
            string filter = SearchBox.Text.Trim();
            if (string.IsNullOrEmpty(filter))
            {
                ShowCategoryGrid();
                UpdateSearchBarVisibility();
                RefreshEmptyState();
            }
            else
            {
                // A filter's already active, so the search row has something to show
                // regardless of HideSearchBar — that setting only affects the empty,
                // nothing-typed-yet state (see UpdateSearchBarVisibility).
                SearchGrid.Visibility = Visibility.Visible;
                ShowFlowGrid(_searchResults, isSearch: true);
                RenderSearchResults(filter, _lastInstalledApps);
            }
        }

        private void FolderBack_Click(object sender, RoutedEventArgs e) => CloseFolder();

        /// <summary>"Rename folder" on a folder tile's context menu — opens the folder
        /// if it isn't already, and puts the cursor straight into the name box with the
        /// current name selected, ready to type over. (The name box is always editable
        /// once a folder's open; this just makes renaming easy to find.)</summary>
        private void RenameFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: DockIconViewModel vm } || !vm.IsFolder)
                return;

            if (_openFolder != vm)
                OpenFolder(vm);

            FolderNameBox.Focus();
            FolderNameBox.SelectAll();
        }

        private void FolderNameBox_GotFocus(object sender, RoutedEventArgs e) => FolderNameBox.SelectAll();

        private void FolderNameBox_LostFocus(object sender, RoutedEventArgs e) => CommitFolderRename();

        private void FolderNameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitFolderRename();
                e.Handled = true;
            }
        }

        private void CommitFolderRename()
        {
            if (_openFolder == null) return;

            string newName = FolderNameBox.Text.Trim();
            if (string.IsNullOrEmpty(newName))
                newName = "Folder";

            if (_openFolder.Model.Name == newName)
                return;

            _openFolder.Model.Name = newName;
            _openFolder.NotifyNameChanged();
            PersistConfig();
        }

        private void UngroupFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_openFolder != null)
                UngroupFolder(_openFolder);
        }

        /// <summary>Dissolves a folder tile, moving all of its children back into its
        /// own category, right where the folder itself used to sit, and closing the
        /// folder view. Also used (with 0 or 1 remaining children) whenever removing
        /// an app leaves a folder too small to still be one — Windows' Start Menu does
        /// the same thing.</summary>
        private void UngroupFolder(DockIconViewModel folder)
        {
            var category = FindCategoryContaining(folder);
            if (category == null) return;

            int insertAt = category.Icons.IndexOf(folder);
            category.Model.Icons.Remove(folder.Model);
            category.Icons.Remove(folder);

            // Unlike the old free-placement grid (where there was no single "next"
            // slot to preserve, so unpacked children just landed wherever the next
            // free spot happened to be), a category's dense list has an obvious place
            // to put them back: exactly where the folder itself used to sit, in order.
            foreach (var child in folder.Children)
            {
                int clamped = Math.Min(insertAt, category.Icons.Count);
                category.Model.Icons.Insert(clamped, child.Model);
                category.Icons.Insert(clamped, child);
                insertAt++;
            }

            CloseFolder();
            RefreshEmptyState();
            PersistConfig();
        }

        private void AddTile_Click(object sender, RoutedEventArgs e)
        {
            _dialogOpen = true;
            try
            {
                var picker = new PickInstalledAppDialog(_configService) { Owner = this };
                bool? pickerResult = picker.ShowDialog();

                if (pickerResult == true && picker.SelectedApp != null)
                {
                    AddIcon(picker.SelectedApp.Name, $"shell:AppsFolder\\{picker.SelectedApp.AppId}", picker.SelectedIconPath);
                }
                else if (picker.BrowseInstead)
                {
                    BrowseForFileAndAdd();
                }
                // else: user cancelled — do nothing.
            }
            finally
            {
                _dialogOpen = false;
                Keyboard.Focus(SearchBox);
            }
        }

        private void BrowseForFileAndAdd()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Add to StartDock",
                Filter = "Programs and shortcuts (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog(this) == true)
                AddIcon(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName);
        }

        private void AddIcon(string name, string targetPath, string? preResolvedIconPath = null)
        {
            var icon = new DockIcon
            {
                Name = name,
                TargetPath = targetPath,
            };

            // The installed-apps picker often already extracted this exact icon for its
            // own list — reuse that file instead of asking the shell for it a second time.
            if (!string.IsNullOrEmpty(preResolvedIconPath) && File.Exists(preResolvedIconPath))
                icon.CachedIconPath = preResolvedIconPath;

            var category = TargetCategoryForNewIcon();
            category.Model.Icons.Add(icon);
            var vm = new DockIconViewModel(icon);
            category.Icons.Add(vm);

            LoadIconImageAsync(vm);
            RefreshEmptyState();
            PersistConfig();
        }

        /// <summary>Where a freshly pinned (or unpacked) icon lands: the dock's first
        /// category, creating one — named "Pinned", matching what the free-grid
        /// migration itself calls its own catch-all category, see
        /// ConfigService.MigrateFromFreeGrid — if the dock doesn't have any yet.</summary>
        private CategoryViewModel TargetCategoryForNewIcon()
        {
            if (_categories.Count > 0)
                return _categories[0];

            var category = new Category { Name = "Pinned" };
            _config.Categories.Add(category);
            var categoryVm = new CategoryViewModel(category);
            _categories.Add(categoryVm);
            return categoryVm;
        }

        private void RemoveIcon_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: DockIconViewModel vm })
                return;

            // A search result isn't pinned — there's nothing to remove. This menu item
            // is hidden for these anyway; the check is just cheap insurance.
            if (vm.IsSearchResult)
                return;

            if (_openFolder != null)
            {
                // Removing an app while browsing inside a folder removes it from that
                // folder specifically, not from wherever the folder itself lives.
                _openFolder.Model.Children?.RemoveAll(i => i.Id == vm.Model.Id);
                _openFolder.Children.Remove(vm);
                DeleteCachedIcon(vm);

                if (_openFolder.Children.Count <= 1)
                {
                    // Too small to still be a folder — UngroupFolder also closes the
                    // folder view, refreshes the empty state, and persists.
                    UngroupFolder(_openFolder);
                    return;
                }

                _openFolder.NotifyChildrenChanged();
                RefreshEmptyState();
                PersistConfig();
                return;
            }

            var category = FindCategoryContaining(vm);
            if (category == null)
                return;

            category.Model.Icons.RemoveAll(i => i.Id == vm.Model.Id);
            category.Icons.Remove(vm);
            _searchResults.Remove(vm); // keep an active search's results in sync too

            // A folder has no cached icon of its own to clean up — its children do.
            if (vm.IsFolder)
                foreach (var child in vm.Children) DeleteCachedIcon(child);
            else
                DeleteCachedIcon(vm);

            RefreshEmptyState();
            PersistConfig();
        }

        /// <summary>"Refresh icon" on a tile's context menu — discards whatever's
        /// cached for it and re-extracts from scratch. Mainly for a tile that came
        /// back with a blank/generic icon instead of its real one, a symptom seen
        /// with some Steam games: IconExtractor asks the shell for the icon via
        /// SHGetFileInfo, and the shell can hand back its generic fallback icon
        /// (not a failure StartDock can detect — the call still "succeeds") when its
        /// own icon cache hasn't caught up with a path yet, most often right after a
        /// second drive with a game library on it has just mounted/spun up. Once
        /// that's cached here as if it were the real icon, LoadIconImageAsync has no
        /// reason to ever try again on its own (the cached file exists and loads
        /// fine) — this gives a manual way to force that retry.</summary>
        private async void RefreshIcon_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: DockIconViewModel vm } || !vm.CanRefreshIcon)
                return;

            // An installed app shown in search results (or Recently Added): fix the
            // shared app list's icon, so the next search shows it too — not just this
            // one temporary tile.
            if (vm.IsSearchResult)
            {
                const string prefix = "shell:AppsFolder\\";
                string appId = vm.Model.TargetPath.Substring(prefix.Length);
                try
                {
                    var icon = await InstalledAppsCache.RefreshIconAsync(appId, _iconExtractor);
                    if (icon != null)
                        vm.IconImage = icon;
                }
                catch
                {
                    // Extraction failed again — leave the tile as it was.
                }
                return;
            }

            DeleteCachedIcon(vm);
            vm.Model.CachedIconPath = string.Empty;
            LoadIconImageAsync(vm);
            PersistConfig();
        }

        /// <summary>Right-click → Rename on a pinned tile: gives it your own label.
        /// Only the name shown in StartDock changes, never the app itself.</summary>
        private void RenameTile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: DockIconViewModel vm } || !vm.CanCustomize)
                return;

            _dialogOpen = true; // keep the dock open while the dialog has focus
            try
            {
                var dialog = new RenameDialog(vm.Name) { Owner = this };
                if (dialog.ShowDialog() != true || dialog.NewName == vm.Name)
                    return;

                vm.Model.Name = dialog.NewName;
                vm.NotifyNameChanged();
                PersistConfig();

                string filter = SearchBox.Text.Trim();
                if (!string.IsNullOrEmpty(filter))
                    RenderSearchResults(filter, _lastInstalledApps);
            }
            finally
            {
                _dialogOpen = false;
                Activate();
            }
        }

        /// <summary>Right-click → Change icon... on a pinned tile: use any image or
        /// .ico file instead of the app's own icon. The picture is converted to a PNG
        /// (at most 256px — the largest size inside an .ico) and stored in StartDock's
        /// icon cache, so the original file can be moved or deleted afterwards.
        /// Refresh icon puts the app's own icon back.</summary>
        private void ChangeIcon_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: DockIconViewModel vm } || !vm.CanCustomize)
                return;

            _dialogOpen = true;
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = $"Choose an icon for {vm.Name}",
                    Filter = "Images and icons (*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*",
                    CheckFileExists = true,
                };
                if (dialog.ShowDialog(this) != true)
                    return;

                // A new file name each time, so the image cache can't hand back the
                // previous picture for a path it has already loaded once.
                string newPath;
                if (AnimatedGif.IsGif(dialog.FileName))
                {
                    // Kept as a GIF so the tile can play it (see AnimatedGif); a still
                    // GIF just shows as a still picture, the same as before.
                    newPath = Path.Combine(_configService.IconCacheFolder,
                        $"{vm.Model.Id}-custom-{DateTime.UtcNow.Ticks}.gif");
                    File.Copy(dialog.FileName, newPath, overwrite: true);
                }
                else
                {
                    newPath = Path.Combine(_configService.IconCacheFolder,
                        $"{vm.Model.Id}-custom-{DateTime.UtcNow.Ticks}.png");
                    SaveAsIconPng(dialog.FileName, newPath);
                }

                DeleteCachedIcon(vm);
                vm.Model.CachedIconPath = newPath;
                LoadIconImageAsync(vm);
                PersistConfig();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't use that file as an icon:\n{ex.Message}", "StartDock",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _dialogOpen = false;
                Activate();
            }
        }

        /// <summary>Converts an image or .ico file into a PNG of at most 256px on its
        /// longest side. For an .ico, uses the largest size it contains — WPF would
        /// otherwise pick the first one, often a blurry 16px version.</summary>
        private static void SaveAsIconPng(string sourcePath, string destinationPath)
        {
            const int maxSize = 256;
            BitmapSource source;

            if (sourcePath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = File.OpenRead(sourcePath);
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                source = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            }
            else
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(sourcePath, UriKind.Absolute);
                image.EndInit();
                source = image;
            }

            int longest = Math.Max(source.PixelWidth, source.PixelHeight);
            if (longest > maxSize)
            {
                double scale = (double)maxSize / longest;
                source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var output = File.Create(destinationPath);
            encoder.Save(output);
        }

        private static void DeleteCachedIcon(DockIconViewModel vm)
        {
            if (!string.IsNullOrEmpty(vm.Model.CachedIconPath) && File.Exists(vm.Model.CachedIconPath))
            {
                try { File.Delete(vm.Model.CachedIconPath); } catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>The category that currently holds <paramref name="vm"/>, or null
        /// if it isn't pinned anywhere (a search result, or a folder's own child).</summary>
        private CategoryViewModel? FindCategoryContaining(DockIconViewModel vm) =>
            _categories.FirstOrDefault(c => c.Icons.Contains(vm));

        // ---------------------------------------------------------------
        // Drag-to-reorder (icon tiles — within a category, or across categories)
        // ---------------------------------------------------------------

        private void AppTile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _tileDragStartPoint = e.GetPosition(null);
            // Only pinned tiles can be dragged — not the automatic "Recently added" /
            // "Recent files" tiles, which don't belong to any category.
            var candidate = (sender as Button)?.Tag as DockIconViewModel;
            _tileDragCandidate = candidate is { IsPinned: true } ? candidate : null;
        }

        private void AppTile_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            // Reordering/merging only applies to the category view for now — not while
            // browsing inside a folder, or while search results are shown.
            if (_openFolder != null)
                return;

            if (e.LeftButton != MouseButtonState.Pressed || _tileDragCandidate == null)
                return;

            if (!string.IsNullOrEmpty(SearchBox.Text))
                return;

            var current = e.GetPosition(null);
            if (Math.Abs(current.X - _tileDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - _tileDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            var dragged = _tileDragCandidate;
            _tileDragCandidate = null; // one drag session per mouse-down

            if (sender is Button button && dragged != null)
                DragDrop.DoDragDrop(button, dragged, DragDropEffects.Move);
        }

        private static bool TryGetIconPayload(DragEventArgs e, out DockIconViewModel icon)
        {
            if (e.Data.GetDataPresent(typeof(DockIconViewModel)) && e.Data.GetData(typeof(DockIconViewModel)) is DockIconViewModel vm)
            {
                icon = vm;
                return true;
            }

            icon = null!;
            return false;
        }

        private static bool TryGetCategoryPayload(DragEventArgs e, out CategoryViewModel category)
        {
            if (e.Data.GetDataPresent(typeof(CategoryViewModel)) && e.Data.GetData(typeof(CategoryViewModel)) is CategoryViewModel vm)
            {
                category = vm;
                return true;
            }

            category = null!;
            return false;
        }

        /// <summary>Repaints the live drop preview (insertion line or merge/landing
        /// outline — see FlowGridPanel.DropFeedback) as a drag moves across a
        /// category's own icon row. Every tile has AllowDrop="True" for cursor
        /// feedback but no Drop handler of its own any more — everything here and in
        /// CategoryIcons_Drop resolves purely from the pointer's position (see
        /// ResolveDropTarget), which both sidesteps needing to know exactly which
        /// element the cursor is over and means a drop right at a tile's edge
        /// (rather than its exact center) is read as "insert alongside" instead of
        /// "merge with it".</summary>
        private void CategoryIcons_DragOver(object sender, DragEventArgs e)
        {
            if (sender is not FlowGridPanel panel || panel.Tag is not CategoryViewModel category)
                return;

            if (_openFolder != null || !TryGetIconPayload(e, out var dragged))
            {
                panel.Feedback = null;
                return;
            }

            var (index, isMerge) = ResolveDropTarget(panel, category, dragged, e.GetPosition(panel));
            panel.Feedback = isMerge ? FlowGridPanel.DropFeedback.Cell(index) : FlowGridPanel.DropFeedback.Line(index);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void CategoryIcons_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is FlowGridPanel panel)
                panel.Feedback = null;
        }

        private void CategoryIcons_Drop(object sender, DragEventArgs e)
        {
            if (sender is not FlowGridPanel panel || panel.Tag is not CategoryViewModel targetCategory)
                return;

            panel.Feedback = null;

            if (_openFolder != null || !TryGetIconPayload(e, out var dragged))
                return;

            var (index, isMerge) = ResolveDropTarget(panel, targetCategory, dragged, e.GetPosition(panel));

            if (!isMerge)
            {
                MoveIconToCategory(dragged, targetCategory, index);
                RefreshEmptyState();
                PersistConfig();
                return;
            }

            var occupant = targetCategory.Icons[Math.Min(index, targetCategory.Icons.Count - 1)];
            if (ReferenceEquals(occupant, dragged))
                return; // dropped back exactly where it already was

            if (occupant.IsFolder && !dragged.IsFolder)
            {
                // Drop a plain app onto an existing folder tile: it joins the
                // folder instead of just swapping places with it.
                MoveIconIntoFolder(dragged, occupant); // persists internally
            }
            else if (!occupant.IsFolder && !dragged.IsFolder)
            {
                // Two plain apps dropped on each other's center: group them into
                // a brand-new folder, the same gesture the Windows Start Menu
                // itself uses to create a group.
                CreateFolderFromMerge(dragged, occupant, targetCategory); // persists internally
            }
            else
            {
                // Either both are folders, or a folder was dropped onto a plain
                // app — folders can't nest, so both cases just swap places.
                SwapIcons(dragged, occupant, targetCategory); // persists internally
            }
        }

        /// <summary>Works out what a drag currently hovering at <paramref
        /// name="posInPanel"/> would do if dropped into <paramref
        /// name="targetCategory"/> — used identically by the live preview
        /// (CategoryIcons_DragOver) and the actual drop (CategoryIcons_Drop) so they
        /// never disagree. Landing on an empty cell (including an entirely empty
        /// category), or in the left/right edge zone of an occupied one (see
        /// FlowGridPanel.GetZoneAt), means "insert here" (isMerge: false, Index is an
        /// insertion index into targetCategory.Icons) — dropping squarely on the
        /// center of an occupied cell means "merge with it" (isMerge: true, Index is
        /// that occupied tile's own index — see CategoryIcons_Drop for what "merge"
        /// resolves to for a given pair of tiles).</summary>
        private static (int Index, bool IsMerge) ResolveDropTarget(FlowGridPanel panel, CategoryViewModel targetCategory, DockIconViewModel dragged, Point posInPanel)
        {
            int occupiedIndex = panel.GetOccupiedIndexAt(posInPanel);
            if (occupiedIndex < 0)
                return (0, false); // an empty category: the only sensible drop is "land here"

            var occupant = targetCategory.Icons[occupiedIndex];
            if (ReferenceEquals(occupant, dragged))
                return (panel.GetInsertIndexAt(posInPanel), false); // hovering over its own current tile

            var zone = panel.GetZoneAt(posInPanel);
            if (zone == FlowGridPanel.CellZone.Center)
                return (occupiedIndex, true);

            int insertIndex = zone == FlowGridPanel.CellZone.Right ? occupiedIndex + 1 : occupiedIndex;
            return (insertIndex, false);
        }

        /// <summary>Moves <paramref name="dragged"/> to sit at <paramref
        /// name="insertIndex"/> within <paramref name="targetCategory"/> — whether
        /// that's the same category it's already in (a reorder) or a different one
        /// (a cross-category move). Doesn't persist or refresh the empty-state hint
        /// itself; callers do that (see CategoryIcons_Drop/NewCategoryDropZone_Drop),
        /// same convention the rest of this file's mutation helpers follow.</summary>
        private void MoveIconToCategory(DockIconViewModel dragged, CategoryViewModel targetCategory, int insertIndex)
        {
            var sourceCategory = FindCategoryContaining(dragged);
            if (sourceCategory == null)
                return;

            if (ReferenceEquals(sourceCategory, targetCategory))
            {
                int oldIndex = targetCategory.Icons.IndexOf(dragged);
                if (oldIndex < 0)
                    return;

                // ObservableCollection.Move's own newIndex is "the index after this
                // item has already been removed" — insertIndex, resolved from the
                // panel, is "the index before removal", so it needs shifting down by
                // one whenever the drop target sits after dragged's own current spot.
                int adjustedIndex = insertIndex > oldIndex ? insertIndex - 1 : insertIndex;
                adjustedIndex = Math.Max(0, Math.Min(targetCategory.Icons.Count - 1, adjustedIndex));

                targetCategory.Icons.Move(oldIndex, adjustedIndex);
                targetCategory.Model.Icons.RemoveAt(oldIndex);
                targetCategory.Model.Icons.Insert(adjustedIndex, dragged.Model);
                return;
            }

            sourceCategory.Model.Icons.Remove(dragged.Model);
            sourceCategory.Icons.Remove(dragged);

            int clamped = Math.Max(0, Math.Min(targetCategory.Icons.Count, insertIndex));
            targetCategory.Icons.Insert(clamped, dragged);
            targetCategory.Model.Icons.Insert(clamped, dragged.Model);
        }

        private void CreateFolderFromMerge(DockIconViewModel dragged, DockIconViewModel target, CategoryViewModel targetCategory)
        {
            var draggedCategory = FindCategoryContaining(dragged);
            draggedCategory?.Model.Icons.Remove(dragged.Model);
            draggedCategory?.Icons.Remove(dragged);

            // Captured after dragged's own removal (which may have shifted target's
            // index down by one, if both were in this same category and dragged sat
            // earlier in it) so this always points at target's true current spot.
            int targetIndex = targetCategory.Icons.IndexOf(target);

            var folderModel = new DockIcon
            {
                Name = "Folder",
                Children = new List<DockIcon> { target.Model, dragged.Model },
            };
            var folderVm = new DockIconViewModel(folderModel);

            // DockIconViewModel's constructor just built brand-new, icon-less child
            // view-models from the bare models above — swap them out for target/
            // dragged's own view-models, which already have their icons loaded.
            // Without this the folder's mini-preview and its browse view both start
            // (and stay) blank.
            folderVm.Children.Clear();
            folderVm.Children.Add(target);
            folderVm.Children.Add(dragged);

            targetCategory.Model.Icons.RemoveAt(targetIndex);
            targetCategory.Icons.RemoveAt(targetIndex);

            targetCategory.Model.Icons.Insert(targetIndex, folderModel);
            targetCategory.Icons.Insert(targetIndex, folderVm);

            RefreshEmptyState();
            PersistConfig();
        }

        private void MoveIconIntoFolder(DockIconViewModel icon, DockIconViewModel folder)
        {
            var category = FindCategoryContaining(icon);
            if (category == null)
                return; // only a plain, currently-pinned icon can be dropped into a folder this way

            category.Model.Icons.Remove(icon.Model);
            category.Icons.Remove(icon); // leaves icon's old spot in this category

            folder.Model.Children ??= new List<DockIcon>();
            folder.Model.Children.Add(icon.Model);
            folder.Children.Add(icon);
            LoadIconImageAsync(icon);
            folder.NotifyChildrenChanged();

            RefreshEmptyState();
            PersistConfig();
        }

        /// <summary>True swap — dragged and occupant trade places — used when a drop
        /// can't merge (both are folders, or a folder was dropped onto a plain app,
        /// which can't nest inside it). ObservableCollection.Move isn't a swap (it
        /// shifts every item in between rather than exchanging two specific ones), so
        /// this reaches for each list's own indexer instead — same idea whether
        /// dragged and occupant share a category or not.</summary>
        private void SwapIcons(DockIconViewModel dragged, DockIconViewModel occupant, CategoryViewModel occupantCategory)
        {
            var draggedCategory = FindCategoryContaining(dragged);
            if (draggedCategory == null)
                return;

            int draggedIndex = draggedCategory.Icons.IndexOf(dragged);
            int occupantIndex = occupantCategory.Icons.IndexOf(occupant);
            if (draggedIndex < 0 || occupantIndex < 0)
                return;

            draggedCategory.Icons[draggedIndex] = occupant;
            draggedCategory.Model.Icons[draggedIndex] = occupant.Model;

            occupantCategory.Icons[occupantIndex] = dragged;
            occupantCategory.Model.Icons[occupantIndex] = dragged.Model;

            RefreshEmptyState();
            PersistConfig();
        }

        /// <summary>Dragging an icon down past the last category (into this strip
        /// below it — see MainWindow.xaml's NewCategoryDropZone) creates a brand-new
        /// category and drops the icon into it, the gesture the user asked for
        /// alongside the explicit "+ Add category" button for starting an empty one
        /// with nothing to drag yet.</summary>
        private void NewCategoryDropZone_DragOver(object sender, DragEventArgs e)
        {
            if (_openFolder != null || !TryGetIconPayload(e, out _))
                return;

            if (sender is Border zone)
                zone.Background = (Brush)FindResource("TileHoverBrush");

            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void NewCategoryDropZone_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is Border zone)
                zone.Background = Brushes.Transparent;
        }

        private void NewCategoryDropZone_Drop(object sender, DragEventArgs e)
        {
            if (sender is Border zone)
                zone.Background = Brushes.Transparent;

            if (_openFolder != null || !TryGetIconPayload(e, out var dragged))
                return;

            var category = new Category { Name = "New category" };
            _config.Categories.Add(category);
            var categoryVm = new CategoryViewModel(category);
            _categories.Add(categoryVm);

            MoveIconToCategory(dragged, categoryVm, 0);
            RefreshEmptyState();
            PersistConfig();
        }

        /// <summary>Right-click → Run as administrator: launches the tile's program
        /// elevated (UAC prompt), like the same item in Windows' own Start Menu.
        /// Cancelling the prompt just does nothing.</summary>
        private void RunAsAdmin_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: DockIconViewModel vm } || !vm.CanRunAsAdmin)
                return;

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(vm.Model.TargetPath)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                };
                if (!string.IsNullOrWhiteSpace(vm.Model.Arguments) && !vm.Model.TargetPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    psi.Arguments = vm.Model.Arguments;
                if (!string.IsNullOrWhiteSpace(vm.Model.WorkingDirectory) && Directory.Exists(vm.Model.WorkingDirectory))
                    psi.WorkingDirectory = vm.Model.WorkingDirectory;

                System.Diagnostics.Process.Start(psi);
                RecordAppLaunch(vm.Model.TargetPath);
                HideDock();
            }
            catch
            {
                // UAC prompt declined (or the target can't be elevated) — nothing to do.
            }
        }

        /// <summary>Right-click → Uninstall...: opens Windows' Installed apps page,
        /// where the app can be uninstalled. Windows doesn't offer a way to jump
        /// straight to one particular app's entry there, so the page opens at the top.</summary>
        private void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:appsfeatures")
                {
                    UseShellExecute = true,
                });
                HideDock();
            }
            catch
            {
                // Settings unavailable — nothing more to do.
            }
        }

        private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Tag: DockIconViewModel vm })
            {
                // Store/UWP entries (and anything else added via the installed-apps
                // picker) point at a virtual "shell:AppsFolder\..." id, not a real file
                // on disk — there's no folder to reveal for those.
                if (vm.Model.TargetPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                    return;

                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{vm.Model.TargetPath}\"")
                    {
                        UseShellExecute = true,
                    });
                }
                catch { /* ignore */ }
            }
        }

        // ---------------------------------------------------------------
        // Categories (banner: rename, drag-to-reorder, delete when empty)
        // ---------------------------------------------------------------

        private void CategoryBanner_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _categoryDragStartPoint = e.GetPosition(null);
            _categoryDragCandidate = (sender as FrameworkElement)?.Tag as CategoryViewModel;
        }

        private void CategoryBanner_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _categoryDragCandidate == null)
                return;

            // A press-and-drag that started inside the rename TextBox is selecting its
            // text, not asking to move the whole category — leave it to the TextBox.
            if (Keyboard.FocusedElement is TextBox)
                return;

            var current = e.GetPosition(null);
            if (Math.Abs(current.X - _categoryDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - _categoryDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            var dragged = _categoryDragCandidate;
            _categoryDragCandidate = null; // one drag session per mouse-down

            if (sender is UIElement element && dragged != null)
                DragDrop.DoDragDrop(element, dragged, DragDropEffects.Move);
        }

        private void CategoryBanner_DragOver(object sender, DragEventArgs e)
        {
            if (!TryGetCategoryPayload(e, out var dragged) || sender is not FrameworkElement contentGrid ||
                contentGrid.Tag is not CategoryViewModel target || ReferenceEquals(dragged, target))
            {
                SetCategoryDropIndicator(sender, null);
                return;
            }

            bool above = IsCategoryDropBefore(e, contentGrid);
            SetCategoryDropIndicator(sender, above);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void CategoryBanner_DragLeave(object sender, DragEventArgs e) => SetCategoryDropIndicator(sender, null);

        private void CategoryBanner_Drop(object sender, DragEventArgs e)
        {
            SetCategoryDropIndicator(sender, null);

            if (!TryGetCategoryPayload(e, out var dragged) || sender is not FrameworkElement contentGrid ||
                contentGrid.Tag is not CategoryViewModel target || ReferenceEquals(dragged, target))
                return;

            bool above = IsCategoryDropBefore(e, contentGrid);
            ReorderCategory(dragged, target, above);
        }

        /// <summary>Shows/hides a banner's own pair of thin drop-indicator strips
        /// (see MainWindow.xaml's CategoryDropAbove/CategoryDropBelow) — above ==
        /// true shows the top one, false shows the bottom one, null hides both.
        /// <paramref name="sender"/> is the banner's inner content Grid, where the
        /// drag/drop handlers are wired (see MainWindow.xaml) — not the outer 3-row
        /// Grid that actually owns the two indicator Borders as its Row 0/2
        /// children. FindName still reaches them from here regardless, because a
        /// DataTemplate instance shares one name scope across everything inside it,
        /// not just within whichever single element a given handler happens to be
        /// attached to.</summary>
        private void SetCategoryDropIndicator(object sender, bool? above)
        {
            if (sender is not FrameworkElement contentGrid)
                return;

            // Rows: a line above or below the banner. Columns: a bar in the gap to
            // the left or right of the category (CategoryDropBefore/After).
            bool columns = IsColumnLayout;
            SetDropBar(contentGrid, "CategoryDropAbove", !columns && above == true);
            SetDropBar(contentGrid, "CategoryDropBelow", !columns && above == false);
            SetDropBar(contentGrid, "CategoryDropBefore", columns && above == true);
            SetDropBar(contentGrid, "CategoryDropAfter", columns && above == false);
        }

        private static void SetDropBar(FrameworkElement scope, string name, bool visible)
        {
            if (scope.FindName(name) is Border bar)
                bar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Whether a category dropped here goes before the one under the
        /// pointer: the top half of its banner in Rows, the left half in Columns.</summary>
        private bool IsCategoryDropBefore(DragEventArgs e, FrameworkElement contentGrid)
        {
            var p = e.GetPosition(contentGrid);
            return IsColumnLayout ? p.X < contentGrid.ActualWidth / 2 : p.Y < contentGrid.ActualHeight / 2;
        }

        /// <summary>Moves a whole category to sit just above or below <paramref
        /// name="target"/> — mirrors MoveIconToCategory's own same-list Move/index-
        /// adjustment logic, just one level up (categories within
        /// _categories/_config.Categories instead of icons within a category).</summary>
        private void ReorderCategory(CategoryViewModel dragged, CategoryViewModel target, bool above)
        {
            int oldIndex = _categories.IndexOf(dragged);
            int targetIndex = _categories.IndexOf(target);
            if (oldIndex < 0 || targetIndex < 0)
                return;

            int insertIndex = above ? targetIndex : targetIndex + 1;
            int adjustedIndex = insertIndex > oldIndex ? insertIndex - 1 : insertIndex;
            adjustedIndex = Math.Max(0, Math.Min(_categories.Count - 1, adjustedIndex));

            _categories.Move(oldIndex, adjustedIndex);
            _config.Categories.RemoveAt(oldIndex);
            _config.Categories.Insert(adjustedIndex, dragged.Model);

            PersistConfig();
        }

        private void CategoryNameBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
                tb.SelectAll();
        }

        private void CategoryNameBox_LostFocus(object sender, RoutedEventArgs e) => CommitCategoryRename(sender as TextBox);

        private void CategoryNameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitCategoryRename(sender as TextBox);
                Keyboard.ClearFocus(); // matches the folder name box's own Enter-to-commit-and-defocus feel
                e.Handled = true;
            }
        }

        /// <summary>Commits a category rename textbox's pending edit. Unlike a
        /// folder's single named FolderNameBox (one static field to read from
        /// directly), each category gets its own TextBox instance from the
        /// DataTemplate, so there's no single field to address imperatively here —
        /// the box is bound to CategoryViewModel.Name with
        /// UpdateSourceTrigger=Explicit precisely so this can push it on blur/Enter
        /// instead of on every keystroke, the same moment the folder name box
        /// commits at.</summary>
        private static void CommitCategoryRename(TextBox? tb) =>
            tb?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

        private void AddCategory_Click(object sender, RoutedEventArgs e)
        {
            var category = new Category();
            _config.Categories.Add(category);
            var categoryVm = new CategoryViewModel(category);
            _categories.Add(categoryVm);

            RefreshEmptyState();
            PersistConfig();

            // Focus straight into the new category's rename box with its placeholder
            // name selected, ready to type over — mirrors RenameFolder_Click's own
            // "select all, ready to replace" feel. Deferred to Loaded priority so the
            // ItemsControl has actually generated the new item's container first.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                CategoriesItemsControl.UpdateLayout();
                if (CategoriesItemsControl.ItemContainerGenerator.ContainerFromItem(categoryVm) is DependencyObject container &&
                    FindVisualChild<TextBox>(container) is TextBox nameBox)
                {
                    nameBox.Focus();
                    nameBox.SelectAll();
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>"Rename Category" on a category banner's right-click menu (see
        /// MainWindow.xaml's TextBox.ContextMenu) — focuses that category's own
        /// name TextBox with its text selected, ready to type over, the same
        /// "select all, ready to replace" feel RenameFolder_Click and
        /// AddCategory_Click both already use. Locating the right TextBox instance
        /// works the same way AddCategory_Click does: each category gets its own
        /// TextBox from the shared DataTemplate, so there's no single named field to
        /// address directly — ItemContainerGenerator + a visual-tree search finds
        /// this specific category's copy instead.</summary>
        private void RenameCategoryMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: CategoryViewModel vm })
                return;

            CategoriesItemsControl.UpdateLayout();
            if (CategoriesItemsControl.ItemContainerGenerator.ContainerFromItem(vm) is DependencyObject container &&
                FindVisualChild<TextBox>(container) is TextBox nameBox)
            {
                nameBox.Focus();
                nameBox.SelectAll();
            }
        }

        /// <summary>"Remove Category" on the same right-click menu — sender is a
        /// MenuItem now rather than the old hover-only Button, so this matches on
        /// the FrameworkElement base both share rather than either type specifically.</summary>
        private void DeleteCategory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: CategoryViewModel vm })
                return;

            // Defensive re-check — the menu item's own IsEnabled already gates this
            // to IsEmpty==True (see MainWindow.xaml's TextBox.ContextMenu), but
            // that's a rendering/interaction concern, not a guarantee against a
            // stale click landing after something else changed the category's
            // contents in between.
            if (!vm.IsEmpty)
                return;

            _config.Categories.Remove(vm.Model);
            _categories.Remove(vm);

            RefreshEmptyState();
            PersistConfig();
        }

        // ---------------------------------------------------------------
        // Bottom bar
        // ---------------------------------------------------------------

        private void RefreshUserInfo()
        {
            // The dock's bottom-left button shows only the avatar (see MainWindow.xaml);
            // the display name is still surfaced as a ToolTip rather than inline text.
            UserButtonElement.ToolTip = UserInfoService.GetDisplayName();

            var picturePath = UserInfoService.GetAccountPicturePath();
            if (picturePath != null)
            {
                try
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(picturePath, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    UserAvatarImage.Source = bitmap;
                    UserAvatarImage.Visibility = Visibility.Visible;
                    UserGlyphFallback.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    // keep the generic glyph fallback
                }
            }
        }

        private void UserButton_Click(object sender, RoutedEventArgs e)
        {
            AppLauncher.OpenWindowsSettings();
            HideDock();
        }

        /// <summary>Opens the real, native Windows Start Menu (see AppLauncher.
        /// OpenNativeStartMenu) and gets this dock out of the way first — deliberately
        /// not via HideDock(), which for every DockPosition except BottomLeft plays a
        /// ~160ms slide/fade animation before finally setting Visibility=Hidden. This
        /// window is Topmost, so leaving it up on screen for even that long could
        /// visually cover the native Start Menu popping up underneath it; clearing any
        /// in-flight animation and hiding immediately avoids that z-order flicker.</summary>
        private void NativeStartMenuButton_Click(object sender, RoutedEventArgs e)
        {
            BeginAnimation(OpacityProperty, null);
            BeginAnimation(TopProperty, null);
            Visibility = Visibility.Hidden;

            AppLauncher.OpenNativeStartMenu();
        }

        private void TaskManagerButton_Click(object sender, RoutedEventArgs e)
        {
            AppLauncher.OpenTaskManager();
            HideDock();
        }

        private void VolumeMixerButton_Click(object sender, RoutedEventArgs e)
        {
            AppLauncher.OpenVolumeMixer();
            HideDock();
        }

        private void FileExplorerButton_Click(object sender, RoutedEventArgs e)
        {
            AppLauncher.OpenFileExplorer();
            HideDock();
        }

        private void CalculatorButton_Click(object sender, RoutedEventArgs e)
        {
            AppLauncher.OpenCalculator();
            HideDock();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

        public void OpenSettings()
        {
            _dialogOpen = true;
            try
            {
                var settingsWindow = new SettingsWindow(_config, _configService) { Owner = this };
                // Apply: same as Save, but the Settings window stays open.
                settingsWindow.Applied += ApplyConfigFromSettings;
                if (settingsWindow.ShowDialog() == true && settingsWindow.Result != null)
                    ApplyConfigFromSettings(settingsWindow.Result);
            }
            finally
            {
                _dialogOpen = false;
            }
        }

        /// <summary>Takes on a config from the Settings window (its Save or Apply)
        /// and passes it on through ConfigChanged so App saves it and updates the
        /// hotkey, Start-button overlay, autostart and theme.</summary>
        private void ApplyConfigFromSettings(AppConfig updated)
        {
            _config = updated;
            Width = _config.WindowWidth;
            Height = _config.WindowHeight;
            ApplyAppearanceSettings();
            UpdateAutoSections();

            // The dock stays visible (just behind the modal Settings dialog) the
            // whole time it's open — if Position changed, move it to its new
            // resting spot right away rather than waiting for the next ShowDock.
            if (Visibility == Visibility.Visible)
                PositionDock();

            ConfigChanged?.Invoke(_config);
        }

        private void PowerButton_Click(object sender, RoutedEventArgs e)
        {
            // Checked fresh on every open (rather than once at startup) since Windows
            // Update can stage a reboot-needed update at any point while the dock is
            // running — matches the native Start menu re-checking each time you open
            // its power flyout, too.
            bool updatePending = PowerActions.IsRestartPending();
            RestartButton.Content = updatePending ? "Update and restart" : "Restart";
            ShutDownButton.Content = updatePending ? "Update and shut down" : "Shut down";

            _dialogOpen = true;
            PowerFlyoutPopup.IsOpen = true;
        }

        private void Lock_Click(object sender, RoutedEventArgs e) { PowerFlyoutPopup.IsOpen = false; PowerActions.Lock(); HideDock(); }
        private void Sleep_Click(object sender, RoutedEventArgs e) { PowerFlyoutPopup.IsOpen = false; PowerActions.Sleep(); HideDock(); }
        private void SignOut_Click(object sender, RoutedEventArgs e) { PowerFlyoutPopup.IsOpen = false; PowerActions.SignOut(); }
        private void Restart_Click(object sender, RoutedEventArgs e) { PowerFlyoutPopup.IsOpen = false; PowerActions.Restart(); }
        private void ShutDown_Click(object sender, RoutedEventArgs e) { PowerFlyoutPopup.IsOpen = false; PowerActions.ShutDown(); }

        // ---------------------------------------------------------------
        // Resizing (top edge = height, right edge = width; bottom-left stays anchored)
        // ---------------------------------------------------------------

        // Captured once at the start of a resize drag (see *ResizeGrip_MouseLeftButtonDown)
        // and reused for that whole drag by TopResizeGrip_MouseMove's Middle center
        // branch and by RepositionHorizontallyDuringResize — rather than asking Win32
        // for the target monitor's DPI-aware work area (see GetTargetWorkArea) again
        // on every single mouse-move tick a drag fires. The monitor a resize starts
        // on isn't going to change mid-drag, so one lookup per drag gesture is enough.
        private Rect _resizeWorkArea;

        private void TopResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _resizingTop = true;
            _resizeStartPoint = PointToScreen(e.GetPosition(this));
            CaptureCurrentDpiScale();
            _startHeight = Height;
            _startTop = Top;
            _resizeWorkArea = GetTargetWorkArea();

            ((UIElement)sender).CaptureMouse();
        }

        /// <summary>Plain clamp, no grid-row snapping — see the doc comment on
        /// <see cref="_chromeWidth"/> for why height-snapping (which this used to
        /// do) was dropped once the grid became a vertical stack of independently
        /// wrapping category rows.
        ///
        /// Top's own math branches on DockPosition: BottomLeft/BottomCenter/
        /// BottomRight are anchored to the *bottom* of the work area, so growing
        /// height needs to keep that bottom edge fixed and extend upward instead
        /// (the original, unconditional formula, kept as-is for those three).
        /// Middle center has no edge to anchor to at all — it's vertically
        /// centered — so it needs Top recomputed from the work area's vertical
        /// center on every move instead, the same formula PositionDock uses, or
        /// growing/shrinking height would visibly drag it off-center until the
        /// dock was next closed and reopened (PositionDock's own fix, but only
        /// applied at ShowDock time). TopCenter/TopRight keep the original
        /// bottom-fixed formula too, for now, even though they're top-anchored —
        /// see the review notes if this class picks up more edge cases than these
        /// six positions later.</summary>
        private void TopResizeGrip_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_resizingTop || e.LeftButton != MouseButtonState.Pressed) return;

            // PointToScreen returns physical pixels, while Window.Height/Top are DIPs —
            // convert the delta through the DPI scale captured at drag-start so resizing
            // tracks the mouse 1:1 at any display scaling (100%, 125%, 150%, ...).
            var current = PointToScreen(e.GetPosition(this));
            double deltaDip = (_resizeStartPoint.Y - current.Y) / _resizeDpiScaleY; // dragging up increases height
            double newHeight = Clamp(_startHeight + deltaDip, MinHeight, MaxHeight);

            Height = newHeight;

            // MiddleLeft/MiddleRight are vertically centered against a side edge, the
            // same as Center is against the whole work area — same recompute-from-
            // center reasoning as this method's doc comment gives for Center itself.
            if (_config.Position == DockPosition.Center
                || _config.Position == DockPosition.MiddleLeft
                || _config.Position == DockPosition.MiddleRight)
                Top = _resizeWorkArea.Top + (_resizeWorkArea.Height - newHeight) / 2;
            else
                Top = _startTop + (_startHeight - newHeight); // keep bottom edge fixed
        }

        private void RightResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _resizingRight = true;
            _resizeStartPoint = PointToScreen(e.GetPosition(this));
            CaptureCurrentDpiScale();
            _startWidth = Width;
            _resizeWorkArea = GetTargetWorkArea();

            // CategoriesScrollViewer (not IconItemsControl, which now only hosts the
            // flat search/folder view — see ShowCategoryGrid/ShowFlowGrid) is what
            // stretches to fill the available width in the normal, top-level view, so
            // it's what SnapWidth needs to measure "chrome" against.
            _chromeWidth = Width - CategoriesScrollViewer.ActualWidth;

            ((UIElement)sender).CaptureMouse();
        }

        private void RightResizeGrip_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_resizingRight || e.LeftButton != MouseButtonState.Pressed) return;

            var current = PointToScreen(e.GetPosition(this));
            double deltaDip = (current.X - _resizeStartPoint.X) / _resizeDpiScaleX; // dragging right increases width
            Width = SnapWidth(_startWidth + deltaDip);

            RepositionHorizontallyDuringResize();
        }

        /// <summary>Keeps Left correct for whatever DockPosition is configured *while*
        /// the width resize-grip is being dragged — called after every Width change
        /// in RightResizeGrip_MouseMove — rather than only the next time the dock
        /// happens to be (re)shown, which is all PositionDock alone gives you (it
        /// runs once, at ShowDock, and nothing kept it in sync with a live-changing
        /// Width after that). Three cases: BottomLeft needs nothing at all (Left
        /// already stays put on its own, untouched here); TopRight/BottomRight are
        /// right-anchored, so they need Left recomputed to keep the *right* edge
        /// fixed as Width grows, the mirror image of BottomLeft's left anchor;
        /// everything else (BottomCenter/TopCenter/Middle center) is horizontally
        /// centered, needing the same centering formula PositionDock uses.</summary>
        private void RepositionHorizontallyDuringResize()
        {
            switch (_config.Position)
            {
                case DockPosition.BottomLeft:
                case DockPosition.TopLeft:
                case DockPosition.MiddleLeft:
                    return; // left-anchored — Left already stays put on its own

                case DockPosition.TopRight:
                case DockPosition.BottomRight:
                case DockPosition.MiddleRight:
                    Left = _resizeWorkArea.Right - Width - 6;
                    return;

                default:
                    Left = _resizeWorkArea.Left + (_resizeWorkArea.Width - Width) / 2;
                    return;
            }
        }

        /// <summary>Rounds a candidate window width so the icon grid lands on a
        /// whole number of tile columns (at least one) — the Windows 10 Start
        /// Menu's own "resize jumps between fixed sizes" feel — then clamps to the
        /// usual min/max. See the doc comment on <see cref="_chromeWidth"/> for why
        /// there's no height counterpart to this any more. Snaps to IconCellSize
        /// (IconSize + the tile's own fixed margin), not a hardcoded 100, so this
        /// keeps working correctly whatever icon size Settings is configured for.</summary>
        private double SnapWidth(double rawWidth)
        {
            double gridWidth = rawWidth - _chromeWidth;
            int columns = Math.Max(1, (int)Math.Round(gridWidth / IconCellSize));
            return Clamp(_chromeWidth + columns * IconCellSize, MinWidth, MaxWidth);
        }

        private void CaptureCurrentDpiScale()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            _resizeDpiScaleX = dpi.DpiScaleX;
            _resizeDpiScaleY = dpi.DpiScaleY;
        }

        private void ResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_resizingTop && !_resizingRight) return;

            _resizingTop = false;
            _resizingRight = false;
            ((UIElement)sender).ReleaseMouseCapture();

            _config.WindowWidth = Width;
            _config.WindowHeight = Height;
            PersistConfig();
        }

        private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));

        private void PersistConfig() => ConfigChanged?.Invoke(_config);
    }
}
