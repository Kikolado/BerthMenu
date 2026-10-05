using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StartDock.Models;

namespace StartDock.Views
{
    /// <summary>
    /// Lays out its children densely, left to right, wrapping to as many rows as
    /// it needs for the available width — functionally the same packing a plain
    /// WrapPanel already does, but as a Panel of our own so it can also answer
    /// "what would a drag hovering at this point do" (see GetInsertIndexAt/
    /// GetZoneAt) and paint the live insertion-line/merge-outline preview while a
    /// drag is over it (see OnRender/Feedback) — the same drag feedback the
    /// earlier free-placement grid (SparseGridPanel, since removed) had, just
    /// driven by a plain list index now instead of a sparse grid slot. Because
    /// layout here is dense (no deliberate gaps — see StartDock.Models.Category),
    /// "index" and "visual position" are always the same thing: child N is simply
    /// InternalChildren[N], with no lookup needed.
    ///
    /// A cell is split, left-to-right, into three drop zones — Left/Right near the
    /// edges mean "insert alongside this tile" (the classic Start-Menu insertion-
    /// line gesture), Center over an occupied tile means "merge/group with it" —
    /// see GetZoneAt and MainWindow's ResolveDropTarget, which turns this plus
    /// GetInsertIndexAt into an actual drop decision.
    ///
    /// Used for each category's own row of icons (see MainWindow.xaml's
    /// CategoryIconsPanelTemplate). Search results and a folder's own contents
    /// still use a plain WrapPanel — free reordering by drag has only ever been a
    /// top-level (now: within-a-category) feature.
    /// </summary>
    public class FlowGridPanel : Panel
    {
        /// <summary>How much of a cell's width, from each edge, counts as an
        /// "insert alongside" zone rather than "merge with what's here" — see
        /// GetZoneAt.</summary>
        private const double EdgeZoneFraction = 0.3;

        public enum CellZone { Left, Center, Right }

        /// <summary>What a drop would currently do, for MainWindow to paint via
        /// OnRender while a drag is in progress (see Feedback) — an insertion
        /// line at an index boundary, or an outline around a whole cell (used both
        /// for "merge with what's here" and "land in this empty trailing cell",
        /// which look the same since either way this is where it lands).</summary>
        public sealed class DropFeedback
        {
            public bool IsLine { get; private init; }
            public int Index { get; private init; }

            public static DropFeedback Line(int beforeIndex) => new() { IsLine = true, Index = beforeIndex };
            public static DropFeedback Cell(int index) => new() { IsLine = false, Index = index };
        }

        /// <summary>Width and height of one cell — every tile is always square
        /// (see MainWindow.xaml's IconTileTemplate, whose Button Width and Height
        /// are both bound to the same IconSize), so one value covers both axes.
        /// Must match the icon tile's own Width/Height plus its Margin in
        /// MainWindow.xaml's icon DataTemplate (92 + 4*2 = 100 by default) — see
        /// MainWindow.xaml.cs's IconCellSize.</summary>
        public static readonly DependencyProperty CellSizeProperty =
            DependencyProperty.Register(
                nameof(CellSize),
                typeof(double),
                typeof(FlowGridPanel),
                new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        public double CellSize
        {
            get => (double)GetValue(CellSizeProperty);
            set => SetValue(CellSizeProperty, value);
        }

        /// <summary>Visual height reserved for an entirely empty category's own
        /// droppable area — see MeasureOverride's doc comment for why this is
        /// deliberately much shorter than a real tile row (CellSize).</summary>
        private const double EmptyRowHeight = 40.0;

        /// <summary>Brush used to paint the live drop indicator (set from XAML via
        /// a DynamicResource so it follows the light/dark theme automatically).</summary>
        public static readonly DependencyProperty IndicatorBrushProperty =
            DependencyProperty.Register(
                nameof(IndicatorBrush),
                typeof(Brush),
                typeof(FlowGridPanel),
                new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush IndicatorBrush
        {
            get => (Brush)GetValue(IndicatorBrushProperty);
            set => SetValue(IndicatorBrushProperty, value);
        }

        /// <summary>Left-packed (the original behavior) or horizontally centered
        /// within the panel's own width — applies per row, so a category that
        /// wraps to several full rows only visibly changes on its last, partial
        /// row (a full row's width already equals the panel's width either way).
        /// Every per-category FlowGridPanel instance gets this via an explicit
        /// RelativeSource AncestorType binding to MainWindow.IconAlignment (see
        /// MainWindow.xaml's CategoryIconsPanelTemplate) rather than WPF property-
        /// value inheritance — an inheritance-based version of this was tried
        /// first, but didn't reliably reach these dynamically-templated panels in
        /// practice.</summary>
        public static readonly DependencyProperty AlignmentProperty =
            DependencyProperty.Register(
                nameof(Alignment),
                typeof(RowAlignment),
                typeof(FlowGridPanel),
                new FrameworkPropertyMetadata(RowAlignment.Left,
                    FrameworkPropertyMetadataOptions.AffectsArrange |
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public RowAlignment Alignment
        {
            get => (RowAlignment)GetValue(AlignmentProperty);
            set => SetValue(AlignmentProperty, value);
        }

        /// <summary>What to draw right now to preview where an in-progress drag
        /// would land — null when nothing's being dragged over this panel. Set
        /// continuously from MainWindow's CategoryIcons_DragOver/DragLeave.</summary>
        public static readonly DependencyProperty FeedbackProperty =
            DependencyProperty.Register(
                nameof(Feedback),
                typeof(DropFeedback),
                typeof(FlowGridPanel),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public DropFeedback? Feedback
        {
            get => (DropFeedback?)GetValue(FeedbackProperty);
            set => SetValue(FeedbackProperty, value);
        }

        /// <summary>
        /// False (the original layout): icons run left to right and wrap onto more
        /// rows. True (AppConfig.LayoutDirection.Columns): icons run top to bottom
        /// and wrap into more columns, filling the height the panel is given.
        ///
        /// Everything below is written once, in terms of a "main" axis (the way
        /// icons run: X normally, Y when vertical) and a "cross" axis (the way the
        /// lines stack: Y normally, X when vertical). In the original layout main
        /// is X and cross is Y, so it behaves exactly as before; vertical just swaps
        /// the two. Names like "columns", "row" and "RowOffsetX" in the helpers
        /// further down mean "icons per line", "line" and "offset along the main
        /// axis" in vertical mode. Alignment follows: Left/Right become Top/Bottom.
        /// The drop zones swap the same way, so CellZone.Left means "before this
        /// icon" (above it, when vertical) and Right "after" (below it), and
        /// MainWindow's drop handling works unchanged in both.
        /// </summary>
        public static readonly DependencyProperty IsVerticalProperty =
            DependencyProperty.Register(
                nameof(IsVertical),
                typeof(bool),
                typeof(FlowGridPanel),
                new FrameworkPropertyMetadata(false,
                    FrameworkPropertyMetadataOptions.AffectsMeasure |
                    FrameworkPropertyMetadataOptions.AffectsArrange |
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public bool IsVertical
        {
            get => (bool)GetValue(IsVerticalProperty);
            set => SetValue(IsVerticalProperty, value);
        }

        private double MainOf(Size size) => IsVertical ? size.Height : size.Width;
        private double ActualMain => IsVertical ? ActualHeight : ActualWidth;
        private Size SizeFrom(double main, double cross) => IsVertical ? new Size(cross, main) : new Size(main, cross);
        private Rect RectFrom(double main, double cross, double mainLength, double crossLength) =>
            IsVertical ? new Rect(cross, main, crossLength, mainLength) : new Rect(main, cross, mainLength, crossLength);
        private (double Main, double Cross) Split(Point p) => IsVertical ? (p.Y, p.X) : (p.X, p.Y);

        protected override Size MeasureOverride(Size availableSize)
        {
            var cell = new Size(CellSize, CellSize);
            foreach (UIElement child in InternalChildren)
                child.Measure(cell);

            double availableMain = MainOf(availableSize);
            int columns = ColumnsFor(availableMain);

            // A completely empty category (freshly created, or emptied out by
            // dragging its last icon elsewhere) still needs *some* visible,
            // droppable area — but reserving a full tile row's worth of height
            // for it, the same as a row that actually has icons in it, made a
            // page of empty categories read as mostly dead space rather than a
            // tight, Windows-10-Start-Menu-style list of groups. EmptyRowHeight
            // is deliberately much shorter than CellSize for exactly that case;
            // a category with real icons in it is unaffected; and hit-testing
            // for a drop still works the same regardless of this height, since
            // GetInsertIndexAt/GetOccupiedIndexAt already clamp to index 0 the
            // instant there are zero children, whatever Y the pointer is at.
            // (Vertical: the same, as a narrow empty column.)
            double cross = InternalChildren.Count == 0 ? EmptyRowHeight : RowsNeeded(columns) * CellSize;

            double main = double.IsPositiveInfinity(availableMain)
                ? columns * CellSize
                : availableMain;
            return SizeFrom(main, cross);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double mainExtent = MainOf(finalSize);
            int columns = ColumnsFor(mainExtent);

            for (int i = 0; i < InternalChildren.Count; i++)
            {
                int row = i / columns;
                int col = i % columns;
                double offset = RowOffsetX(row, columns, mainExtent);
                InternalChildren[i].Arrange(RectFrom(offset + col * CellSize, row * CellSize, CellSize, CellSize));
            }

            return finalSize;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            var feedback = Feedback;
            if (feedback == null)
                return;

            double mainExtent = ActualMain;
            int columns = ColumnsFor(mainExtent);
            int row = feedback.Index / columns;
            int col = feedback.Index % columns;
            double offset = RowOffsetX(row, columns, mainExtent);

            // An empty category renders its (only ever one) row at EmptyRowHeight
            // rather than a full CellSize — see MeasureOverride — so the feedback
            // painted here needs to scale down with it instead of assuming every
            // row is exactly CellSize tall. Padding is proportional (rather than a
            // fixed 8px/4px) so it still looks reasonable at either height.
            double rowHeight = InternalChildren.Count == 0 ? EmptyRowHeight : CellSize;

            if (feedback.IsLine)
            {
                // A thin bar at the boundary this index's leading edge sits on —
                // matches the Windows Start Menu's own "drop it here, between these
                // two tiles" cue. Vertical in the original layout, horizontal when
                // the icons run top to bottom.
                double pad = rowHeight * 0.08;
                double at = offset + col * CellSize;
                dc.DrawRectangle(IndicatorBrush, null, RectFrom(at - 1.5, row * rowHeight + pad, 3, rowHeight - pad * 2));
            }
            else
            {
                double pad = rowHeight * 0.04;
                var cell = RectFrom(offset + col * CellSize + pad, row * rowHeight + pad, CellSize - pad * 2, rowHeight - pad * 2);
                dc.DrawRoundedRectangle(null, new Pen(IndicatorBrush, 2), cell, 6, 6);
            }
        }

        /// <summary>Translates a point in this panel's own coordinate space (e.g.
        /// from DragEventArgs.GetPosition(this)) into the index a drop there would
        /// insert before — clamped to [0, ChildCount], so hovering past the last
        /// tile (including in the row's own trailing dead space, or below the
        /// last row entirely) still resolves to "append at the end" rather than
        /// an out-of-range index.</summary>
        public int GetInsertIndexAt(Point position)
        {
            var (main, cross) = Split(position);
            int columns = ColumnsFor(ActualMain);
            int row = Math.Max(0, (int)(cross / CellSize));
            double offset = RowOffsetX(row, columns, ActualMain);
            int col = ClampedColumn(main - offset, columns);
            int index = row * columns + col;
            return Math.Max(0, Math.Min(InternalChildren.Count, index));
        }

        /// <summary>Same idea as <see cref="GetInsertIndexAt"/>, but clamped to an
        /// existing child's index (never ChildCount) — for resolving which tile a
        /// drop landed on, before deciding its zone. Returns -1 if there are no
        /// children at all.</summary>
        public int GetOccupiedIndexAt(Point position)
        {
            if (InternalChildren.Count == 0)
                return -1;

            var (main, cross) = Split(position);
            int columns = ColumnsFor(ActualMain);
            int row = Math.Max(0, (int)(cross / CellSize));
            double offset = RowOffsetX(row, columns, ActualMain);
            int col = ClampedColumn(main - offset, columns);
            int index = row * columns + col;
            return Math.Max(0, Math.Min(InternalChildren.Count - 1, index));
        }

        /// <summary>Where within its cell a point falls — the left/right ~30% of
        /// a cell (see EdgeZoneFraction) means "insert alongside", the middle
        /// means "merge with whatever's here" (see MainWindow's
        /// ResolveDropTarget, which turns this plus GetOccupiedIndexAt/
        /// GetInsertIndexAt into an actual drop decision). When vertical, it's the
        /// top/bottom ~30% instead, reported as Left (before) / Right (after).</summary>
        public CellZone GetZoneAt(Point position)
        {
            var (main, cross) = Split(position);
            int columns = ColumnsFor(ActualMain);
            int row = Math.Max(0, (int)(cross / CellSize));
            double offset = RowOffsetX(row, columns, ActualMain);
            double adjusted = main - offset;
            int col = ClampedColumn(adjusted, columns);

            // Measured from the same clamped column GetInsertIndexAt/
            // GetOccupiedIndexAt resolved to — not a raw position.X % CellSize —
            // so a drop in the sliver of dead space past the last real column
            // (near-certain at most window widths, since CellSize rarely divides
            // the dock's resizable width evenly) still reads as being in *that*
            // column rather than some column past the edge of the grid.
            double inCell = adjusted - col * CellSize;
            if (inCell < CellSize * EdgeZoneFraction) return CellZone.Left;
            if (inCell > CellSize * (1 - EdgeZoneFraction)) return CellZone.Right;
            return CellZone.Center;
        }

        private int ClampedColumn(double x, int columns) =>
            Math.Max(0, Math.Min(columns - 1, (int)(x / CellSize)));

        private int ColumnsFor(double width)
        {
            double w = double.IsPositiveInfinity(width) ? CellSize : width;
            return Math.Max(1, (int)Math.Floor(w / CellSize));
        }

        /// <summary>How many children actually sit in the given row — equal to
        /// <see cref="ColumnsFor"/>'s column count for every row except possibly
        /// the last one, which may be partial (or, past the last row entirely,
        /// zero).</summary>
        private int ItemsInRow(int row, int columns)
        {
            int start = row * columns;
            if (start >= InternalChildren.Count)
                return 0;

            return Math.Min(columns, InternalChildren.Count - start);
        }

        /// <summary>How far a given row's tiles are shifted right of the panel's
        /// left edge — always 0 in the original left-packed mode (see Alignment).
        /// In centered mode, a full row (ItemsInRow == columns) computes to 0
        /// here too, since its own width already equals the panel's — only a
        /// partial row (normally just the last one in a category) actually
        /// shifts. Right mode shifts a partial row by the whole leftover gap
        /// instead of half of it, packing it flush against the right edge —
        /// the mirror image of Left.</summary>
        private double RowOffsetX(int row, int columns, double totalWidth)
        {
            if (Alignment == RowAlignment.Left)
                return 0;

            int itemsInRow = ItemsInRow(row, columns);
            if (itemsInRow <= 0 || double.IsPositiveInfinity(totalWidth))
                return 0;

            double rowWidth = itemsInRow * CellSize;
            double leftover = Math.Max(0, totalWidth - rowWidth);

            return Alignment == RowAlignment.Right ? leftover : leftover / 2;
        }

        // Only ever called with at least one child — MeasureOverride special-cases
        // zero children itself (see EmptyRowHeight) rather than asking this for a
        // row count that wouldn't mean anything yet.
        private int RowsNeeded(int columns) => ((InternalChildren.Count - 1) / columns) + 1;
    }
}
