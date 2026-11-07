using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CTC.Wpf.Layout;
using CTC.Wpf.ViewModels;
using TrainControl.Contracts.Enums;

namespace CTC.Wpf.Controls;

/// <summary>
/// Interactive schematic of the selected line's territory. Blocks are drawn as track
/// segments coloured by occupancy, with maintenance, selection, switch, signal, crossing and
/// station markings. Clicking a block sets <see cref="SelectedBlock"/> (two-way bound to the
/// view model's selection). Geometry comes from <see cref="TerritoryLayoutEngine"/>; the
/// control redraws whenever a displayed <see cref="BlockViewModel"/> raises PropertyChanged.
/// </summary>
/// <remarks>
/// Zoom is a purely visual concern, so Fit / Zoom In / Zoom Out are routed commands handled
/// here. Place the map inside a ScrollViewer to pan when zoomed in.
/// </remarks>
public sealed class CtcTerritoryMap : FrameworkElement
{
    public const double MinZoom = 0.4;
    public const double MaxZoom = 3.0;
    private const double ZoomStep = 1.25;

    // Below this zoom, block labels and badge text are hidden (tooltips still show everything).
    private const double DetailZoom = 0.65;

    // Layout units.
    private const double TrackThickness = 7;
    private const double HitTolerance = 9;
    private const double LabelFontSize = 10;
    private const double BadgeFontSize = 8.5;
    private const double StationFontSize = 10.5;

    public static readonly RoutedUICommand FitCommand = new RoutedUICommand("Fit", nameof(FitCommand), typeof(CtcTerritoryMap));
    public static readonly RoutedUICommand ZoomInCommand = new RoutedUICommand("Zoom In", nameof(ZoomInCommand), typeof(CtcTerritoryMap));
    public static readonly RoutedUICommand ZoomOutCommand = new RoutedUICommand("Zoom Out", nameof(ZoomOutCommand), typeof(CtcTerritoryMap));

    public static readonly DependencyProperty BlocksProperty = DependencyProperty.Register(
        nameof(Blocks), typeof(IEnumerable), typeof(CtcTerritoryMap),
        new FrameworkPropertyMetadata(null, OnBlocksChanged));

    public static readonly DependencyProperty SelectedBlockProperty = DependencyProperty.Register(
        nameof(SelectedBlock), typeof(BlockViewModel), typeof(CtcTerritoryMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedBlockChanged));

    public static readonly DependencyProperty LineIdProperty = DependencyProperty.Register(
        nameof(LineId), typeof(string), typeof(CtcTerritoryMap),
        new FrameworkPropertyMetadata(null, (d, _) => ((CtcTerritoryMap)d).InvalidateLayoutGeometry()));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(CtcTerritoryMap),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, null, CoerceZoom));

    private static readonly Brush BackgroundBrush = Frozen(Brushes.White);
    private static readonly Brush ClearBrush = Hex("#368653");
    private static readonly Brush OccupiedBrush = Hex("#C84036");
    private static readonly Brush UnknownBrush = Hex("#9BA8B8");
    private static readonly Brush MaintenanceBrush = Hex("#D97706");
    private static readonly Brush SelectionBrush = Hex("#2563EB");
    private static readonly Brush ConnectionBrush = Hex("#A3ADBA");
    private static readonly Brush LabelBrush = Hex("#374151");
    private static readonly Brush StationBrush = Hex("#111827");
    private static readonly Brush PlatformBrush = Hex("#4B5563");
    private static readonly Brush BadgeBackground = Hex("#F9FAFB");
    private static readonly Brush BadgeBorder = Hex("#CBD5E1");
    private static readonly Brush SwitchBrush = Hex("#1D4ED8");
    private static readonly Brush SignalRedBrush = Hex("#C84036");
    private static readonly Brush SignalYellowBrush = Hex("#D4A017");
    private static readonly Brush SignalGreenBrush = Hex("#368653");
    private static readonly Brush SignalSuperGreenBrush = Hex("#00C46A");
    private static readonly Brush CrossingBrush = Hex("#7C3AED");
    private static readonly Brush YardBrush = Hex("#E5E7EB");

    private static readonly Typeface LabelTypeface = new Typeface("Segoe UI");
    private static readonly Typeface BoldTypeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private readonly HashSet<INotifyPropertyChanged> _subscribedBlocks = new HashSet<INotifyPropertyChanged>();
    private readonly ToolTip _toolTip = new ToolTip { Placement = PlacementMode.Mouse, HasDropShadow = true };
    private INotifyCollectionChanged? _observedCollection;
    private TerritoryLayout? _layout;
    private BlockViewModel? _hoveredBlock;
    private bool _isFitted = true;
    private bool _fitPending;
    private bool _isSelectingFromMap;

    static CtcTerritoryMap()
    {
        FocusableProperty.OverrideMetadata(typeof(CtcTerritoryMap), new FrameworkPropertyMetadata(true));
        ClipToBoundsProperty.OverrideMetadata(typeof(CtcTerritoryMap), new FrameworkPropertyMetadata(true));

        CommandManager.RegisterClassCommandBinding(typeof(CtcTerritoryMap), new CommandBinding(FitCommand, (s, _) => ((CtcTerritoryMap)s).FitToView()));
        CommandManager.RegisterClassCommandBinding(typeof(CtcTerritoryMap), new CommandBinding(ZoomInCommand, (s, _) => ((CtcTerritoryMap)s).ZoomBy(ZoomStep), (s, e) => e.CanExecute = ((CtcTerritoryMap)s).Zoom < MaxZoom));
        CommandManager.RegisterClassCommandBinding(typeof(CtcTerritoryMap), new CommandBinding(ZoomOutCommand, (s, _) => ((CtcTerritoryMap)s).ZoomBy(1 / ZoomStep), (s, e) => e.CanExecute = ((CtcTerritoryMap)s).Zoom > ((CtcTerritoryMap)s).MinimumZoom + 0.001));
    }

    public CtcTerritoryMap()
    {
        _toolTip.PlacementTarget = this;
        Loaded += (_, _) =>
        {
            AttachToBlocks(Blocks);
            if (FindScrollViewer() is { } viewer)
            {
                viewer.SizeChanged += OnViewportSizeChanged;
            }

            RequestFit();
        };
        Unloaded += (_, _) =>
        {
            DetachFromBlocks();
            if (FindScrollViewer() is { } viewer)
            {
                viewer.SizeChanged -= OnViewportSizeChanged;
            }

            _toolTip.IsOpen = false;
        };
    }

    /// <summary>Blocks of the displayed line (BlockViewModel items).</summary>
    public IEnumerable? Blocks
    {
        get => (IEnumerable?)GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    /// <summary>The dispatcher's selected block; binds two-way by default.</summary>
    public BlockViewModel? SelectedBlock
    {
        get => (BlockViewModel?)GetValue(SelectedBlockProperty);
        set => SetValue(SelectedBlockProperty, value);
    }

    /// <summary>Line ID (or name) used to choose the line's schematic.</summary>
    public string? LineId
    {
        get => (string?)GetValue(LineIdProperty);
        set => SetValue(LineIdProperty, value);
    }

    /// <summary>Visual scale of the schematic, between the fit size and <see cref="MaxZoom"/>.</summary>
    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    /// <summary>The layout currently drawn (computed on demand).</summary>
    public TerritoryLayout CurrentLayout => _layout ??= TerritoryLayoutEngine.Compute(LineId, BlockItems());

    // Zooming out stops at MinZoom, or lower when that is what fitting a large line takes.
    private double MinimumZoom => Math.Min(MinZoom, FitZoom() ?? MinZoom);

    private static object CoerceZoom(DependencyObject d, object value)
    {
        double zoom = (double)value;
        return double.IsFinite(zoom) ? Math.Clamp(zoom, 0.05, MaxZoom) : 1.0;
    }

    private static void OnBlocksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var map = (CtcTerritoryMap)d;
        map.AttachToBlocks(e.NewValue as IEnumerable);
        map.InvalidateLayoutGeometry();
    }

    private static void OnSelectedBlockChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var map = (CtcTerritoryMap)d;

        // Selected elsewhere (the block selector): scroll the block into view.
        if (!map._isSelectingFromMap && e.NewValue is BlockViewModel block)
        {
            map.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => map.BringBlockIntoView(block));
        }
    }

    private IEnumerable<BlockViewModel> BlockItems() => Blocks?.OfType<BlockViewModel>() ?? [];

    #region Block subscriptions

    private void AttachToBlocks(IEnumerable? blocks)
    {
        DetachFromBlocks();

        if (blocks is INotifyCollectionChanged observable)
        {
            _observedCollection = observable;
            observable.CollectionChanged += OnBlocksCollectionChanged;
        }

        SubscribeAll(blocks);
    }

    private void DetachFromBlocks()
    {
        if (_observedCollection is not null)
        {
            _observedCollection.CollectionChanged -= OnBlocksCollectionChanged;
            _observedCollection = null;
        }

        foreach (var block in _subscribedBlocks)
        {
            block.PropertyChanged -= OnBlockPropertyChanged;
        }

        _subscribedBlocks.Clear();
    }

    private void SubscribeAll(IEnumerable? blocks)
    {
        foreach (var block in blocks?.OfType<INotifyPropertyChanged>() ?? [])
        {
            if (_subscribedBlocks.Add(block))
            {
                block.PropertyChanged += OnBlockPropertyChanged;
            }
        }
    }

    private void OnBlocksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // Reset carries no OldItems, so drop every subscription and re-read the collection.
            foreach (var block in _subscribedBlocks)
            {
                block.PropertyChanged -= OnBlockPropertyChanged;
            }

            _subscribedBlocks.Clear();
            SubscribeAll(Blocks);
        }
        else
        {
            foreach (var block in e.OldItems?.OfType<INotifyPropertyChanged>() ?? [])
            {
                if (_subscribedBlocks.Remove(block))
                {
                    block.PropertyChanged -= OnBlockPropertyChanged;
                }
            }

            SubscribeAll(e.NewItems);
        }

        InvalidateLayoutGeometry();
    }

    private void OnBlockPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Live status only changes colours and badges; the geometry stays.
        InvalidateVisual();

        if (_toolTip.IsOpen && ReferenceEquals(sender, _hoveredBlock))
        {
            _toolTip.Content = BuildToolTip(_hoveredBlock!);
        }
    }

    /// <summary>A different line or layout: recompute geometry and return to the fit view.</summary>
    private void InvalidateLayoutGeometry()
    {
        _layout = null;
        _hoveredBlock = null;
        _toolTip.IsOpen = false;
        _isFitted = true;
        InvalidateMeasure();
        InvalidateVisual();
        RequestFit();
    }

    #endregion

    #region Zoom

    private ScrollViewer? FindScrollViewer()
    {
        DependencyObject? current = this;
        while (current is not null and not ScrollViewer)
        {
            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }

        return current as ScrollViewer;
    }

    /// <summary>Zoom at which the whole schematic fits the viewport; null when unknown yet.</summary>
    private double? FitZoom()
    {
        var viewer = FindScrollViewer();
        var bounds = CurrentLayout.Bounds;
        double width = viewer?.ViewportWidth > 0 ? viewer.ViewportWidth : viewer?.ActualWidth ?? 0;
        double height = viewer?.ViewportHeight > 0 ? viewer.ViewportHeight : viewer?.ActualHeight ?? 0;
        if (width <= 0 || height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return null;
        }

        // Slightly under the exact fit so no scrollbars appear.
        return Math.Min(width / bounds.Width, height / bounds.Height) * 0.98;
    }

    public void FitToView()
    {
        if (FitZoom() is { } zoom)
        {
            SetCurrentValue(ZoomProperty, Math.Min(zoom, MaxZoom));
            _isFitted = true;
            if (FindScrollViewer() is { } viewer)
            {
                viewer.ScrollToHome();
            }
        }
    }

    private void ZoomBy(double factor)
    {
        var viewer = FindScrollViewer();
        double oldZoom = Zoom;
        double newZoom = Math.Clamp(oldZoom * factor, MinimumZoom, MaxZoom);
        if (Math.Abs(newZoom - oldZoom) < 0.0001)
        {
            return;
        }

        // Keep the centre of the viewport where it was.
        double centreX = viewer is null ? 0 : (viewer.HorizontalOffset + viewer.ViewportWidth / 2) / oldZoom;
        double centreY = viewer is null ? 0 : (viewer.VerticalOffset + viewer.ViewportHeight / 2) / oldZoom;

        SetCurrentValue(ZoomProperty, newZoom);
        _isFitted = false;

        if (viewer is not null)
        {
            viewer.UpdateLayout();
            viewer.ScrollToHorizontalOffset(centreX * newZoom - viewer.ViewportWidth / 2);
            viewer.ScrollToVerticalOffset(centreY * newZoom - viewer.ViewportHeight / 2);
        }
    }

    private void RequestFit()
    {
        if (_fitPending)
        {
            return;
        }

        _fitPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _fitPending = false;
            if (_isFitted)
            {
                FitToView();
            }
        });
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep "fit" meaning fit while the window is resized; a manual zoom is left alone.
        if (_isFitted)
        {
            RequestFit();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ZoomBy(e.Delta > 0 ? ZoomStep : 1 / ZoomStep);
            e.Handled = true;
        }
    }

    private void BringBlockIntoView(BlockViewModel block)
    {
        if (!CurrentLayout.Placements.TryGetValue(block.BlockId, out var placement))
        {
            return;
        }

        var origin = ContentOrigin();
        var points = placement.Points.Select(point => new Point(origin.X + point.X * Zoom, origin.Y + point.Y * Zoom)).ToList();
        double left = points.Min(p => p.X), top = points.Min(p => p.Y);
        var rect = new Rect(left, top, points.Max(p => p.X) - left, points.Max(p => p.Y) - top);
        rect.Inflate(60, 60);
        BringIntoView(rect);
    }

    #endregion

    #region Layout and hit testing

    protected override Size MeasureOverride(Size availableSize)
    {
        var bounds = CurrentLayout.Bounds;
        return new Size(bounds.Width * Zoom, bounds.Height * Zoom);
    }

    /// <summary>Top-left of the drawn schematic; centred when the viewport is larger.</summary>
    private Point ContentOrigin()
    {
        var bounds = CurrentLayout.Bounds;
        return new Point(
            Math.Max(0, (ActualWidth - bounds.Width * Zoom) / 2),
            Math.Max(0, (ActualHeight - bounds.Height * Zoom) / 2));
    }

    private Point ToLayout(Point screen)
    {
        var origin = ContentOrigin();
        return new Point((screen.X - origin.X) / Zoom, (screen.Y - origin.Y) / Zoom);
    }

    /// <summary>The block whose track segment is under <paramref name="screenPoint"/>, if any.</summary>
    public BlockViewModel? HitTestBlock(Point screenPoint)
    {
        var point = ToLayout(screenPoint);
        double tolerance = Math.Max(HitTolerance, 6 / Zoom);
        BlockViewModel? best = null;
        double bestDistance = double.MaxValue;

        foreach (var block in BlockItems())
        {
            if (CurrentLayout.Placements.TryGetValue(block.BlockId, out var placement))
            {
                double distance = placement.DistanceTo(point);
                if (distance <= tolerance && distance < bestDistance)
                {
                    best = block;
                    bestDistance = distance;
                }
            }
        }

        return best;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        if (HitTestBlock(e.GetPosition(this)) is { } block)
        {
            _isSelectingFromMap = true;
            try
            {
                SetCurrentValue(SelectedBlockProperty, block);
            }
            finally
            {
                _isSelectingFromMap = false;
            }

            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var block = HitTestBlock(e.GetPosition(this));
        Cursor = block is null ? null : Cursors.Hand;

        if (ReferenceEquals(block, _hoveredBlock))
        {
            return;
        }

        _hoveredBlock = block;
        _toolTip.IsOpen = false;
        if (block is not null)
        {
            _toolTip.Content = BuildToolTip(block);
            _toolTip.IsOpen = true;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoveredBlock = null;
        _toolTip.IsOpen = false;
    }

    #endregion

    #region Tooltip

    private static object BuildToolTip(BlockViewModel block)
    {
        var panel = new StackPanel { MinWidth = 180 };
        panel.Children.Add(new TextBlock
        {
            Text = block.HasStation ? $"Block {block.DisplayName} — {block.StationName}" : $"Block {block.DisplayName}",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        });

        void Row(string label, string value) => panel.Children.Add(new TextBlock { Text = $"{label}: {value}" });

        Row("Section", block.Section);
        Row("Length", $"{block.LengthMeters:0.#} m ({block.LengthMeters * 3.28084:0} ft)");
        Row("Speed Limit", $"{block.SpeedLimitMilesPerHour:0.#} mph");
        Row("Occupancy", block.Occupancy);
        Row("Maintenance", MaintenanceText(block));
        if (block.HasStation)
        {
            Row("Station", block.StationName);
        }

        if (block.HasSwitch)
        {
            Row("Switch", block.SwitchPosition.ToString());
        }

        if (block.HasSignal)
        {
            Row("Signal", block.SignalStatus ?? "Unknown");
        }

        if (block.HasCrossing)
        {
            Row("Crossing", block.Crossing.ToString());
        }

        return panel;
    }

    private static string MaintenanceText(BlockViewModel block) => block.IsMaintenanceRequestPending
        ? $"{block.MaintenanceStatus} (CTC requested {block.RequestedMaintenanceState}, awaiting Track Controller)"
        : block.MaintenanceStatus;

    #endregion

    #region Rendering

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));

        var layout = CurrentLayout;
        var blocks = BlockItems().Where(block => layout.Placements.ContainsKey(block.BlockId)).ToList();
        if (blocks.Count == 0)
        {
            DrawText(dc, "No track layout loaded for this line.", new Point(16, 16), 13, LabelBrush, LabelTypeface, 1);
            return;
        }

        var origin = ContentOrigin();
        dc.PushTransform(new MatrixTransform(Zoom, 0, 0, Zoom, origin.X, origin.Y));

        bool detailed = Zoom >= DetailZoom;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        DrawConnections(dc, layout);
        DrawTerminals(dc, layout, pixelsPerDip);

        // The selection is drawn first so neighbouring blocks stay on top of its outline.
        if (SelectedBlock is { } selected && layout.Placements.TryGetValue(selected.BlockId, out var selectedPlacement))
        {
            DrawSelection(dc, selectedPlacement);
        }

        foreach (var block in blocks)
        {
            DrawBlockTrack(dc, block, layout.Placements[block.BlockId]);
        }

        foreach (var block in blocks)
        {
            DrawBlockAnnotations(dc, block, layout.Placements[block.BlockId], detailed, pixelsPerDip);
        }

        dc.Pop();
    }

    private void DrawConnections(DrawingContext dc, TerritoryLayout layout)
    {
        var pen = Frozen(new Pen(ConnectionBrush, TrackWidth * 0.45) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        foreach (var connection in layout.Connections)
        {
            var (from, to) = ClosestEnds(layout.Placements[connection.FromBlockId], layout.Placements[connection.ToBlockId]);
            dc.DrawLine(pen, from, to);
        }
    }

    private static (Point From, Point To) ClosestEnds(TerritoryBlockPlacement a, TerritoryBlockPlacement b)
    {
        Point[] aEnds = [a.Start, a.End], bEnds = [b.Start, b.End];
        return aEnds.SelectMany(p => bEnds.Select(q => (From: p, To: q)))
            .MinBy(pair => (pair.From - pair.To).LengthSquared);
    }

    private void DrawTerminals(DrawingContext dc, TerritoryLayout layout, double pixelsPerDip)
    {
        var linkPen = Frozen(new Pen(ConnectionBrush, TrackWidth * 0.45) { DashStyle = DashStyles.Dash });
        foreach (var terminal in layout.Terminals)
        {
            foreach (var blockId in terminal.BlockIds)
            {
                var placement = layout.Placements[blockId];
                var end = (placement.Start - terminal.Position).LengthSquared <= (placement.End - terminal.Position).LengthSquared ? placement.Start : placement.End;
                dc.DrawLine(linkPen, terminal.Position, end);
            }

            var text = Text(terminal.Label, Scaled(BadgeFontSize + 1, 8), BoldTypeface, LabelBrush, pixelsPerDip);
            var box = new Rect(terminal.Position.X - text.Width / 2 - 6, terminal.Position.Y - text.Height / 2 - 3, text.Width + 12, text.Height + 6);
            dc.DrawRoundedRectangle(YardBrush, new Pen(PlatformBrush, 1 / Zoom), box, 3, 3);
            dc.DrawText(text, new Point(box.X + 6, box.Y + 3));
        }
    }

    private void DrawBlockTrack(DrawingContext dc, BlockViewModel block, TerritoryBlockPlacement placement)
    {
        var geometry = PolylineGeometry(placement.Points);
        bool isClosed = block.MaintenanceState == MaintenanceState.Closed;
        bool isPending = block.IsMaintenanceRequestPending;

        // The maintenance outline is a wider stroke underneath the occupancy fill.
        if (isClosed || isPending)
        {
            var pen = TrackPen(MaintenanceBrush, TrackWidth + Math.Max(5, 2.5 / Zoom));
            if (!isClosed)
            {
                // Requested by CTC but not yet confirmed by the Track Controller.
                pen = new Pen(MaintenanceBrush, TrackWidth + Math.Max(5, 2.5 / Zoom)) { DashStyle = new DashStyle([0.6, 0.4], 0) };
            }

            dc.DrawGeometry(null, pen, geometry);
        }

        var fill = block.OccupancyState switch
        {
            OccupancyState.Occupied => OccupiedBrush,
            OccupancyState.Clear => ClearBrush,
            _ => UnknownBrush,
        };
        dc.DrawGeometry(null, TrackPen(fill, TrackWidth), geometry);
    }

    /// <summary>Track width in layout units; never thinner than about 3 px when zoomed far out.</summary>
    private double TrackWidth => Math.Max(TrackThickness, 3.2 / Zoom);

    /// <summary>Total width of the selection outline around a track.</summary>
    private double SelectionWidth => TrackWidth + Math.Max(15, 9 / Zoom);

    /// <summary>A blue capsule outside the block (and its maintenance outline), with a white gap.</summary>
    private void DrawSelection(DrawingContext dc, TerritoryBlockPlacement placement)
    {
        var geometry = PolylineGeometry(placement.Points);
        dc.DrawGeometry(null, SelectionPen(SelectionBrush, SelectionWidth), geometry);
        dc.DrawGeometry(null, SelectionPen(BackgroundBrush, SelectionWidth - Math.Max(6, 3.5 / Zoom)), geometry);
    }

    private static Pen SelectionPen(Brush brush, double thickness) =>
        Frozen(new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });

    private void DrawBlockAnnotations(DrawingContext dc, BlockViewModel block, TerritoryBlockPlacement placement, bool detailed, double pixelsPerDip)
    {
        bool isSelected = ReferenceEquals(block, SelectedBlock);

        var label = detailed || isSelected
            ? Text(block.DisplayName, Scaled(LabelFontSize, 8), isSelected ? BoldTypeface : LabelTypeface, isSelected ? SelectionBrush : LabelBrush, pixelsPerDip)
            : null;
        bool labelWithBadges = placement.LabelSide == placement.BadgeSide;

        if (label is not null && !labelWithBadges)
        {
            double labelDistance = isSelected ? SelectionWidth / 2 + 2 : TrackWidth + 3;
            DrawAligned(dc, label, placement.Midpoint + placement.LabelNormal * labelDistance, placement.LabelNormal);
        }

        // Station platform, then (the label,) status badges and the station name, stacked outward on the badge side.
        var normal = placement.BadgeNormal;
        double distance = isSelected ? SelectionWidth / 2 + 2 : TrackWidth / 2 + 3;

        if (block.HasStation)
        {
            var offset = normal * (distance + 1.5);
            var platformPen = Frozen(new Pen(PlatformBrush, 3) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat });
            dc.DrawGeometry(null, platformPen, PolylineGeometry(Inset(placement.Points, 0.15).Select(p => p + offset).ToList()));
            distance += 6;
        }

        if (label is not null && labelWithBadges)
        {
            DrawAligned(dc, label, placement.Midpoint + normal * distance, normal);
            distance += (Math.Abs(normal.Y) >= Math.Abs(normal.X) ? label.Height : label.Width) + 3;
        }

        foreach (var badge in Badges(block, detailed))
        {
            distance += DrawBadge(dc, badge, placement.Midpoint + normal * distance, normal, pixelsPerDip) + 2;
        }

        if (block.HasStation)
        {
            var name = Text(block.StationName, Scaled(StationFontSize, 8.5), BoldTypeface, StationBrush, pixelsPerDip);
            DrawAligned(dc, name, placement.Midpoint + normal * (distance + 1), normal);
        }
    }

    private readonly record struct Badge(string Text, Brush Accent, Geometry? Symbol, Brush? SymbolFill);

    private IEnumerable<Badge> Badges(BlockViewModel block, bool detailed)
    {
        if (block.HasSwitch)
        {
            string position = block.SwitchPosition switch
            {
                SwitchPosition.Normal => "N",
                SwitchPosition.Reverse => "R",
                _ => "?",
            };
            yield return new Badge(detailed ? $"SW {position}" : position, SwitchBrush, Diamond(), SwitchBrush);
        }

        if (block.HasSignal)
        {
            var (brush, shortText) = block.Signal switch
            {
                SignalState.Red => (SignalRedBrush, "R"),
                SignalState.Yellow => (SignalYellowBrush, "Y"),
                SignalState.Green => (SignalGreenBrush, "G"),
                SignalState.SuperGreen => (SignalSuperGreenBrush, "SG"),
                _ => (UnknownBrush, "?"),
            };
            yield return new Badge(detailed ? shortText : string.Empty, LabelBrush, new EllipseGeometry(new Point(0, 0), 0.5, 0.5), brush);
        }

        if (block.HasCrossing)
        {
            string state = block.Crossing switch
            {
                CrossingState.Open => "OPEN",
                CrossingState.Closed => "CLOSED",
                _ => "?",
            };
            yield return new Badge(detailed ? $"XING {state}" : string.Empty, CrossingBrush, Cross(), null);
        }

        if (block.MaintenanceState == MaintenanceState.Closed)
        {
            yield return new Badge("M", MaintenanceBrush, null, null);
        }
    }

    /// <summary>Draws a pill badge outward from <paramref name="anchor"/>; returns its extent along the normal.</summary>
    private double DrawBadge(DrawingContext dc, Badge badge, Point anchor, Vector normal, double pixelsPerDip)
    {
        double fontSize = Scaled(BadgeFontSize, 7);
        double symbolSize = fontSize * 0.85;
        var text = string.IsNullOrEmpty(badge.Text) ? null : Text(badge.Text, fontSize, BoldTypeface, badge.Accent, pixelsPerDip);
        double padding = fontSize * 0.3;
        double height = (text?.Height ?? symbolSize) + padding;
        double width = padding * 2 + (badge.Symbol is null ? 0 : symbolSize) + (text is null ? 0 : text.Width) + (badge.Symbol is not null && text is not null ? padding : 0);

        var box = AlignedBox(new Size(width, height), anchor, normal);
        dc.DrawRoundedRectangle(BadgeBackground, new Pen(BadgeBorder, 1 / Zoom), box, height / 3, height / 3);

        double x = box.X + padding;
        if (badge.Symbol is not null)
        {
            var symbolCentre = new Point(x + symbolSize / 2, box.Y + box.Height / 2);
            var transform = new TransformGroup();
            transform.Children.Add(new ScaleTransform(symbolSize, symbolSize));
            transform.Children.Add(new TranslateTransform(symbolCentre.X, symbolCentre.Y));
            var symbol = badge.Symbol.Clone();
            symbol.Transform = transform;
            dc.DrawGeometry(badge.SymbolFill, badge.SymbolFill is null ? new Pen(badge.Accent, symbolSize * 0.22) : null, symbol);
            x += symbolSize + padding;
        }

        if (text is not null)
        {
            dc.DrawText(text, new Point(x, box.Y + padding / 2));
        }

        return Math.Abs(normal.Y) >= Math.Abs(normal.X) ? height : width;
    }

    // Unit-size symbols centred on the origin; scaled when drawn.
    private static Geometry Diamond() => Geometry.Parse("M 0,-0.5 L 0.5,0 L 0,0.5 L -0.5,0 Z");

    private static Geometry Cross() => Geometry.Parse("M -0.45,-0.45 L 0.45,0.45 M -0.45,0.45 L 0.45,-0.45");

    /// <summary>Font size in layout units that never renders below <paramref name="minPixels"/>.</summary>
    private double Scaled(double size, double minPixels) => Math.Max(size, minPixels / Zoom);

    private Pen TrackPen(Brush brush, double thickness) =>
        Frozen(new Pen(brush, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat, LineJoin = PenLineJoin.Round });

    private static StreamGeometry PolylineGeometry(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], false, false);
            context.PolyLineTo(points.Skip(1).ToList(), true, true);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>The polyline shortened by <paramref name="fraction"/> of its length at each end.</summary>
    private static List<Point> Inset(IReadOnlyList<Point> points, double fraction)
    {
        double length = PolylineMath.Length(points);
        return PolylineMath.SubPath(points, length * fraction, length * (1 - fraction));
    }

    private static FormattedText Text(string text, double size, Typeface typeface, Brush brush, double pixelsPerDip) =>
        new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush, pixelsPerDip);

    private static void DrawText(DrawingContext dc, string text, Point point, double size, Brush brush, Typeface typeface, double pixelsPerDip) =>
        dc.DrawText(Text(text, size, typeface, brush, pixelsPerDip), point);

    private static void DrawAligned(DrawingContext dc, FormattedText text, Point anchor, Vector normal) =>
        dc.DrawText(text, AlignedBox(new Size(text.Width, text.Height), anchor, normal).TopLeft);

    /// <summary>Places a box of <paramref name="size"/> beside <paramref name="anchor"/> on the side the normal points to.</summary>
    private static Rect AlignedBox(Size size, Point anchor, Vector normal)
    {
        double x = normal.X > 0.35 ? anchor.X : normal.X < -0.35 ? anchor.X - size.Width : anchor.X - size.Width / 2;
        double y = normal.Y > 0.35 ? anchor.Y : normal.Y < -0.35 ? anchor.Y - size.Height : anchor.Y - size.Height / 2;
        return new Rect(new Point(x, y), size);
    }

    private static Brush Hex(string color) => Frozen((Brush)new BrushConverter().ConvertFromString(color)!);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze && !freezable.IsFrozen)
        {
            freezable.Freeze();
        }

        return freezable;
    }

    #endregion
}
