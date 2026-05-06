using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrightnessTrayAppWPF.Localization;
using BrightnessTrayAppWPF.Models;
using BrightnessTrayAppWPF.SunriseSunset;
using BrightnessTrayAppWPF.Utils;
using BrightnessTrayAppWPF.Visuals;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;
using UserControl = System.Windows.Controls.UserControl;

namespace BrightnessTrayAppWPF.WPF.Settings.Pages.EnvironmentalPageAddons;

/// <summary>
/// Canvas-based 24h curve editor.
/// Two independent series (brightness, night-light) sampled across the day - each visible
/// only when its toggle is on. Click an empty point on the canvas to add a control point,
/// drag a thumb to reshape, right-click a thumb to remove. The first and last points are
/// pinned at t=0 and t=1 (they can move vertically but not horizontally) so the curve
/// always wraps cleanly across midnight.
/// </summary>
public partial class CurveEditor : UserControl
{
    // Series identity used for hit-testing and command routing - the visual layer doesn't care
    // which curve a point belongs to, but mouse handlers do.
    private enum Series
    {
        Brightness,
        NightLight,
    }

    // Distinguishes "min clamp line" from "max clamp line" inside a single series,
    // used so a horizontal-line drag knows which value on the model to write back.
    private enum LimitKind
    {
        Min,
        Max,
    }

    private const double ThumbSize = 14.0;
    // Invisible halo that doubles the thumb's effective hit area in both dimensions
    // (10x10 visible -> 20x20 grabbable). Half the thumb size on each side does the trick.
    private const double ThumbHitPadding = ThumbSize / 2.0;
    private const double TimeAxisLabelFontSize = 11.0;
    private const int VerticalGridDivisions = 4;
    private const int HorizontalGridDivisions = 8; // every 3 hours
    private const double LimitLineHitTolerance = 5.0; // pixels above/below a clamp line that still register as a hit

    // Margin around the plottable area inside the editor's chrome.
    // Buys room for thumbs sitting at t=0 / t=1 / v=0 / v=1 so they don't get clipped against the canvas edge,
    // and lets the leftmost / rightmost hour labels sit centred on their gridlines
    // instead of biased inward to dodge the wall.
    private const double PlotInsetX = 10.0;
    private const double PlotInsetYBase = 8.0;
    // Extra top inset reserved for the disabled-period pins when that feature is on.
    // Keeps the pin row clear of the grid so it reads as "above the graph"
    // rather than overlapping the topmost gridline.
    // Bottom inset stays at the base value so the chart doesn't shrink twice over.
    private const double DisabledPeriodPinAreaHeight = 14.0;

    // Backing model. The editor keeps a reference to the per-profile EnvironmentalCurve
    // and re-points _brightness/_nightLight at the absolute or offset list whenever _offsetMode flips
    // - mutations through the active list flow back into the model automatically
    // because they share the underlying storage.
    private EnvironmentalCurve? _curveData;
    private List<EnvironmentalCurvePoint> _brightness = EnvironmentalCurve.CreateDefaultBrightness();
    private List<EnvironmentalCurvePoint> _nightLight = EnvironmentalCurve.CreateDefaultNightLight();
    private bool _showBrightness = true;
    private bool _showNightLight;
    private bool _offsetMode;

    // Blend factor between linear (0) and full monotonic cubic Hermite (1).
    // Driven by the global "smoothness" number box in the settings UI.
    private double _smoothness = 1.0;

    // Min / max manual-slider brightness across the active monitor set, used to draw the "degeneration" lines
    // (where the brightest active monitor first pins at 100 and the dimmest at 0) in either curve mode.
    // Null = no active monitors -> don't draw.
    // Both modes preserve relative monitor offsets, so the gap (max - min) is the only quantity needed.
    private double? _activeMinBrightness;
    private double? _activeMaxBrightness;

    // Drag state. Set on MouseDown over a thumb, cleared on MouseUp / capture loss.
    private EnvironmentalCurvePoint? _dragPoint;
    private Series _dragSeries;

    // Limit-line drag state. Active only in offset mode; mutually exclusive with _dragPoint
    // - the mouse handler picks one or the other depending on what was hit.
    private bool _draggingLimit;
    private Series _limitDragSeries;
    private LimitKind _limitDragKind;

    // Hover highlighting for the dashed clamp lines. Lit whenever the cursor enters the hit-test band around a line
    // and not engaged in any drag; cleared on MouseLeave.
    // Stored as a nullable tuple so we can cheaply check "did the hover state change" before triggering a redraw.
    private (Series Series, LimitKind Kind)? _hoveredLimit;

    // Same idea, applied to curve thumbs. Set whenever the cursor enters a thumb's expanded hit halo
    // so the ring around that node thickens to give the user feedback matching what the hit-test will accept.
    private (Series Series, EnvironmentalCurvePoint Point)? _hoveredThumb;

    // Keyboard-driven selection. Tab cycles through visible-curve nodes (brightness first, then night light);
    // arrow keys nudge value/time; space inserts a node 4% to the left/right of the current one;
    // delete/backspace removes interior nodes.
    // Cleared on focus loss or any operation that invalidates the reference (curve swap, hide).
    private EnvironmentalCurvePoint? _selectedPoint;
    private Series _selectedSeries;

    // Adjustment magnitudes for arrow-key edits.
    // Y steps live on the same 0..100 scale as the curve points;
    // 1.0 = "1 point" on the displayed 0..100 axis,
    // Ctrl multiplies to 6 points so a held Ctrl+arrow sweeps the curve faster
    // without exiting the keyboard editing mode.
    // The 0.04 spacebar offset is on the time axis (still 0..1) and matches "4 points" in displayed time.
    private const double KeyboardStepFine = 1.0;
    private const double KeyboardStepCoarse = 6.0;
    private const double KeyboardSpacebarOffset = 0.04;

    // Sub-point Shift+arrow steps: 1 minute on the X axis (1/1440 of the 24h cycle) for precise clock-time placement,
    // and 1 displayed-unit on Y
    // (mode-aware - 1 in absolute, 0.5 in offset since the displayed range doubles to -100..+100
    // so a single displayed-unit move corresponds to half a curve-Y unit).
    private const double KeyboardStepOneMinute = 1.0 / (24.0 * 60.0);
    private const double KeyboardStepOneYUnitAbsolute = 1.0;
    private const double KeyboardStepOneYUnitOffset = 0.5;

    // Sun overlay state. Geo coords come in from settings;
    // when both are zero we treat them as unset and skip drawing entirely.
    // The overlay is also skipped when the SPA can't produce any meaningful boundary times
    // (computation failure / extreme polar input).
    private bool _showSunOverlay = true;
    private double _latitude;
    private double _longitude;

    // Optional override for the sun overlay's reference date. Null means "use today";
    // a non-null value reroutes the SPA call to that calendar date so the editor can preview twilight / night bands
    // for an arbitrary day. Not persisted; the host resets it when the Environmental tab is re-entered.
    private DateTime? _sunOverlayDate;

    // When true (default), sun calculations use the wall-clock UTC offset (DST-aware).
    // When false, the timezone's BaseUtcOffset is used so sun events stay in standard time year-round.
    // Mirrors the per-profile EnvironmentalCurve.UseDaylightSavings flag
    // - the host pushes the active profile's value in via SetUseDaylightSavings.
    private bool _useDaylightSavings = true;

    // Disabled-period state: pins above the chart mark the start/end of a window where the curve does not apply.
    // Off by default. Start > End wraps midnight
    // - rendering and hit-testing both treat the two segments [Start, 1] U [0, End] as "disabled" in that case.
    // The host owns persistence; this struct is presentation state only.
    private bool _disabledPeriodEnabled;
    private double _disabledPeriodStart = 0.25;
    private double _disabledPeriodEnd = 0.75;

    // Drag state for the start / end pin. Mutually exclusive with thumb / limit drags
    // because the pins live in the top inset where curve thumbs and limit lines never sit,
    // so a single hit-test order resolves the priority cleanly.
    private enum DisabledPin
    {
        Start,
        End,
    }
    private DisabledPin? _dragDisabledPin;
    private DisabledPin? _hoveredDisabledPin;

    // Preview mode locks the editor against edits and shows a translucent tint plus an "Exit Preview Mode" button.
    // Set when the host pushes a non-current date so the user is viewing a hypothetical curve
    // (e.g. how Follow-the-Sun will reanchor it on a future day) rather than editing the live one.
    private bool _previewMode;

    // Cursor-readout overlay state. Persistent elements live on OverlayCanvas so they can be repositioned
    // on every MouseMove without touching the heavy PlotCanvas redraw path.
    private bool _showCursorReadout;
    private Point? _cursorPos;
    private TextBlock? _cursorReadoutText;
    private Border? _cursorReadoutBackground;
    private Line? _cursorScrubberLine;
    private Ellipse? _brightnessCursorMarker;
    private TextBlock? _brightnessCursorLabel;
    private Ellipse? _nightLightCursorMarker;
    private TextBlock? _nightLightCursorLabel;

    // Selected-node readout. Sits just below the cursor readout pill
    // and only renders when a node is currently selected.
    // Foreground is the curve's brush so the user can tell at a glance which series the selected node belongs to
    // without inferring it from position alone.
    private TextBlock? _nodeReadoutText;
    private Border? _nodeReadoutBackground;

    // Current-time vertical line. Persistent overlay element on OverlayCanvas;
    // the per-minute timer below pushes its X position to ScreenX(CurrentDayFraction()).
    // While the preview sweep is running, _isSweeping is true and the position is driven by SetPreviewSweepCursor
    // instead of the timer.
    private Line? _currentTimeLine;
    private DispatcherTimer? _currentTimeTimer;
    private bool _isSweeping;


    /// <summary>
    /// Raised whenever a control point is added, removed, or moved. The hosting view
    /// listens to this to write the updated curve back into the active profile and persist.
    /// </summary>
    public event Action? CurveChanged;

    /// <summary>
    /// Raised when the user clicks the in-canvas "Exit Preview Mode" button. The host
    /// is responsible for clearing whatever drove the preview state (e.g. resetting the
    /// preview date back to today and rebinding the live curve).
    /// </summary>
    public event Action? ExitPreviewModeRequested;

    /// <summary>
    /// Raised whenever the disabled-period pins are dragged. Carries the new (start, end)
    /// pair on the same normalised 0..1 cycle the curve points use. Start may exceed End -
    /// that's a wrap-midnight selection, valid and persisted as-is.
    /// </summary>
    public event Action<double, double>? DisabledPeriodChanged;

    public CurveEditor()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            InitializeOverlay();
            StartCurrentTimeTimer();
            UpdateCurrentTimeIndicator();
        };
        Unloaded += (_, _) => StopCurrentTimeTimer();
    }

    /// <summary>
    /// Builds the persistent overlay elements on first load.
    /// Deferred to <see cref="FrameworkElement.Loaded"/> so theme dynamic resources are resolvable;
    /// everything goes onto OverlayCanvas where Redraw never reaches,
    /// so per-mousemove updates don't fight the static layer.
    /// </summary>
    private void InitializeOverlay()
    {
        if (_cursorReadoutText is not null) return;

        Brush brightnessBrush = GetBrightnessBrush();
        Brush nightLightBrush = GetNightLightBrush();

        // Current-time indicator: persistent vertical line at "now,"
        // driven by the per-minute DispatcherTimer (and by SetPreviewSweepCursor while sweeping).
        // Added before any of the readout pills / cursor markers
        // so the dashed line sits at the bottom of the overlay z-stack
        // and never punches through the legible content of an upper-right readout.
        // Stroke binds to the themed brush so the picker's live edits in ThemePage
        // flow through to the line without rebuilding the overlay - a one-shot brush snapshot
        // would freeze the color at construction time.
        _currentTimeLine = new Line
        {
            StrokeThickness = 1.25,
            StrokeDashArray = [3.0, 2.0],
            Opacity = 0.85,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _currentTimeLine.SetResourceReference(Shape.StrokeProperty, "EnvironmentalCurrentTimeBrush");
        OverlayCanvas.Children.Add(_currentTimeLine);

        // Top-right "time / value" readout. Background gives it just enough contrast to read over a curve
        // when the cursor passes near the corner.
        // SetResourceReference (rather than a one-shot FindResource cast) keeps the lookup a DynamicResource,
        // matching the ThemeForeground binding below
        // - the XAML designer renders this control without App.xaml.cs running,
        // so ThemeBackground isn't registered until runtime.
        _cursorReadoutBackground = new Border
        {
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            Opacity = 0.85,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _cursorReadoutBackground.SetResourceReference(Border.BackgroundProperty, "ThemeBackground");
        _cursorReadoutText = new TextBlock
        {
            FontSize = 12,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Segoe UI"),
            IsHitTestVisible = false,
        };
        _cursorReadoutText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeForeground");
        _cursorReadoutBackground.Child = _cursorReadoutText;
        OverlayCanvas.Children.Add(_cursorReadoutBackground);

        // Vertical scrubber - drawn behind the markers but above the readout pill.
        _cursorScrubberLine = new Line
        {
            Stroke = Brushes.LightGray,
            StrokeThickness = 1,
            StrokeDashArray = [2.0, 3.0],
            Opacity = 0.6,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        OverlayCanvas.Children.Add(_cursorScrubberLine);

        // Per-curve markers + value labels. Created up-front so MouseMove only needs to update positions,
        // never construct + add to the visual tree.
        _brightnessCursorMarker = BuildCursorMarker(brightnessBrush);
        _brightnessCursorLabel = BuildCursorLabel(brightnessBrush);
        _nightLightCursorMarker = BuildCursorMarker(nightLightBrush);
        _nightLightCursorLabel = BuildCursorLabel(nightLightBrush);
        OverlayCanvas.Children.Add(_brightnessCursorMarker);
        OverlayCanvas.Children.Add(_brightnessCursorLabel);
        OverlayCanvas.Children.Add(_nightLightCursorMarker);
        OverlayCanvas.Children.Add(_nightLightCursorLabel);

        // Selected-node readout pill. Shape mirrors the cursor readout above it so the two read as a stacked pair;
        // foreground is set per-frame from the selected curve's brush in UpdateNodeReadout,
        // so the literal here is just a placeholder.
        _nodeReadoutBackground = new Border
        {
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            Opacity = 0.85,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _nodeReadoutBackground.SetResourceReference(Border.BackgroundProperty, "ThemeBackground");
        _nodeReadoutText = new TextBlock
        {
            FontSize = 12,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Segoe UI"),
            IsHitTestVisible = false,
        };
        _nodeReadoutBackground.Child = _nodeReadoutText;
        OverlayCanvas.Children.Add(_nodeReadoutBackground);
    }

    private static Ellipse BuildCursorMarker(Brush brush) => new()
    {
        Width = 8,
        Height = 8,
        Fill = brush,
        Stroke = Brushes.White,
        StrokeThickness = 1.5,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    private static TextBlock BuildCursorLabel(Brush brush) => new()
    {
        FontSize = 11,
        FontFamily = new FontFamily("Consolas, Cascadia Mono, Segoe UI"),
        Foreground = brush,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    /// <summary>
    /// Toggles whether the per-curve scrubber + value markers appear while the cursor is inside the editor.
    /// The top-right time/value readout is always shown on hover and is unaffected by this setting.
    /// </summary>
    public void SetShowCursorReadout(bool show)
    {
        _showCursorReadout = show;
        UpdateCursorOverlay();
    }

    /// <summary>
    /// Toggles the twilight / night background bands. When off, no sun overlay is drawn.
    /// </summary>
    public void SetShowSunOverlay(bool show)
    {
        if (_showSunOverlay == show) return;

        _showSunOverlay = show;
        Redraw();
    }

    /// <summary>
    /// Toggles whether sun-position calculations follow daylight savings (DST-aware local offset)
    /// or pin to the timezone's standard offset year-round.
    /// Off means twilight / night bands and Follow-the-Sun anchors stay in standard time,
    /// so the chart doesn't jump an hour at DST boundaries.
    /// </summary>
    public void SetUseDaylightSavings(bool useDaylightSavings)
    {
        if (_useDaylightSavings == useDaylightSavings) return;

        _useDaylightSavings = useDaylightSavings;
        Redraw();
    }

    /// <summary>
    /// Overrides the date used to compute the sun overlay's twilight / night bands.
    /// Pass null to fall back to today's date. The override is in-memory only.
    /// </summary>
    public void SetSunOverlayDate(DateTime? date)
    {
        DateTime? normalized = date?.Date;
        if (_sunOverlayDate == normalized) return;

        _sunOverlayDate = normalized;
        Redraw();
    }

    /// <summary>
    /// Locks the editor against edits and shows the preview tint + "Exit Preview Mode" button.
    /// Also tears down any in-flight drag / hover state so flipping into preview mid-interaction
    /// can't leave the editor with a stuck thumb or held mouse capture.
    /// </summary>
    public void SetPreviewMode(bool preview)
    {
        if (_previewMode == preview) return;

        _previewMode = preview;
        PreviewTintOverlay.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        ExitPreviewButton.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;

        if (preview)
        {
            // Drop any drag / hover that was in flight when the host flipped us into preview
            // - otherwise a thumb can stay "held" with no way to release it.
            _dragPoint = null;
            _draggingLimit = false;
            _dragDisabledPin = null;
            if (PlotCanvas.IsMouseCaptured) PlotCanvas.ReleaseMouseCapture();
            _hoveredLimit = null;
            _hoveredThumb = null;
            _hoveredDisabledPin = null;
            PlotCanvas.Cursor = Cursors.Arrow;
        }

        Redraw();
    }

    private void ExitPreviewButton_Click(object sender, RoutedEventArgs e) =>
        ExitPreviewModeRequested?.Invoke();

    /// <summary>
    /// Tells the editor whether a 24h preview sweep is in flight.
    /// While true, the per-minute current-time tick stops fighting the cursor positions the host pushes
    /// via <see cref="SetPreviewSweepCursor"/>; the indicator snaps back to real now when the flag clears.
    /// The button itself lives in the host (next to the Preview Date selector), so the host handles its label flip.
    /// </summary>
    public void SetPreviewSweepRunning(bool running)
    {
        if (_isSweeping == running) return;

        _isSweeping = running;

        if (!running)
        {
            // Snap back to real "now."
            // The host will run its own EvaluateCurves on finish to resync the monitors;
            // the indicator just needs to land on the right pixel column before the next per-minute tick fires.
            UpdateCurrentTimeIndicator();
        }
    }

    /// <summary>
    /// Drives the current-time indicator's X to the simulated <paramref name="t"/> (0..1 day fraction)
    /// while a preview sweep is running.
    /// Ignored when not sweeping so a stray call from the host can't drag the line off real-now.
    /// </summary>
    public void SetPreviewSweepCursor(double t)
    {
        if (!_isSweeping) return;
        if (_currentTimeLine is null) return;

        double w = PlotCanvas.ActualWidth;
        double h = PlotCanvas.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            _currentTimeLine.Visibility = Visibility.Collapsed;
            return;
        }

        double clamped = Math.Clamp(t, 0.0, 1.0);
        double x = ScreenX(clamped, w);
        _currentTimeLine.X1 = x;
        _currentTimeLine.X2 = x;
        _currentTimeLine.Y1 = TopInset;
        _currentTimeLine.Y2 = h - PlotInsetYBase;
        _currentTimeLine.Visibility = Visibility.Visible;
    }

    private void StartCurrentTimeTimer()
    {
        if (_currentTimeTimer != null) return;

        _currentTimeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(TimeConstants.CurveEditorClockIndicatorRefreshIntervalMs),
        };
        _currentTimeTimer.Tick += (_, _) => UpdateCurrentTimeIndicator();
        _currentTimeTimer.Start();
    }

    private void StopCurrentTimeTimer()
    {
        if (_currentTimeTimer == null) return;

        _currentTimeTimer.Stop();
        _currentTimeTimer = null;
    }

    /// <summary>
    /// Repositions the persistent current-time vertical line to whatever the sampler reports for "now."
    /// Skipped while a preview sweep is active - the sweep loop owns the line during the animation
    /// and snaps it back to real-now when it ends.
    /// </summary>
    private void UpdateCurrentTimeIndicator()
    {
        if (_currentTimeLine is null) return;
        if (_isSweeping) return;

        double w = PlotCanvas.ActualWidth;
        double h = PlotCanvas.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            _currentTimeLine.Visibility = Visibility.Collapsed;
            return;
        }

        double t = EnvironmentalCurveSampler.CurrentDayFraction();
        double x = ScreenX(t, w);
        _currentTimeLine.X1 = x;
        _currentTimeLine.X2 = x;
        _currentTimeLine.Y1 = TopInset;
        _currentTimeLine.Y2 = h - PlotInsetYBase;
        _currentTimeLine.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Updates the geo coordinates used to compute the twilight / night bands.
    /// Both zeros are treated as "unset" and suppress the overlay. Out-of-range values likewise.
    /// </summary>
    public void SetGeoLocation(double latitude, double longitude)
    {
        if (_latitude == latitude && _longitude == longitude) return;

        _latitude = latitude;
        _longitude = longitude;
        Redraw();
    }

    /// <summary>
    /// Pushes the disabled-period state into the editor.
    /// Toggling enabled drives whether the pins / grey verticals / translucent overlay render at all;
    /// start and end are in the same [0,1] day-fraction space the curve points use,
    /// and start > end is a valid wrap-midnight selection.
    /// No-op when the values match the current state to avoid unnecessary redraws on profile-load round trips.
    /// </summary>
    public void SetDisabledPeriod(bool enabled, double start, double end)
    {
        double newStart = Math.Clamp(start, 0.0, 1.0);
        double newEnd = Math.Clamp(end, 0.0, 1.0);
        if (_disabledPeriodEnabled == enabled
            && _disabledPeriodStart == newStart
            && _disabledPeriodEnd == newEnd)
            return;

        // If the feature was on and the user just turned it off, drop any in-flight pin drag / hover
        // so a follow-up redraw doesn't leave the cursor "holding" a no-longer-rendered pin.
        if (_disabledPeriodEnabled && !enabled)
        {
            _dragDisabledPin = null;
            _hoveredDisabledPin = null;
            if (PlotCanvas.IsMouseCaptured) PlotCanvas.ReleaseMouseCapture();
        }

        _disabledPeriodEnabled = enabled;
        _disabledPeriodStart = newStart;
        _disabledPeriodEnd = newEnd;
        Redraw();
    }

    public void SetCurves(EnvironmentalCurve curve)
    {
        _curveData = curve;
        // Drop the keyboard selection on curve swap - the previous reference belongs to the prior profile / clone
        // and would render as a stale ghost ring on the wrong curve.
        _selectedPoint = null;
        ApplyCurveSelection();
        Redraw();
    }

    public void SetVisibility(bool showBrightness, bool showNightLight)
    {
        _showBrightness = showBrightness;
        _showNightLight = showNightLight;
        // If the user just hid the curve our selection lives on, drop it
        // so arrow keys don't silently mutate a now-invisible curve.
        if (_selectedPoint != null)
        {
            bool seriesVisible = _selectedSeries == Series.Brightness ? showBrightness : showNightLight;
            if (!seriesVisible) _selectedPoint = null;
        }
        Redraw();
    }

    /// <summary>
    /// Toggles the editor between absolute mode (0..100 brightness/night-light curves)
    /// and offset mode (-100..+100 deltas with draggable clamp lines).
    /// Each mode reads/writes its own list on the active <see cref="EnvironmentalCurve"/>,
    /// so flipping back and forth is non-destructive.
    /// </summary>
    public void SetOffsetMode(bool offsetMode)
    {
        if (_offsetMode == offsetMode) return;

        _offsetMode = offsetMode;
        // Mode flip swaps which list backs each series,
        // so the previously selected node now lives on the other (now-hidden) list.
        // Drop selection so arrow keys land on a node that's actually rendered.
        _selectedPoint = null;
        ApplyCurveSelection();
        Redraw();
    }

    /// <summary>
    /// Pushes the slider brightness range of the currently-active monitor set into the editor.
    /// The editor uses these to draw informational "degeneration" lines marking the curve value
    /// at which the brightest monitor first saturates at 100 and the dimmest at 0.
    /// Pass nulls when no monitors are active to suppress the lines.
    /// </summary>
    public void SetActiveBrightnessRange(double? minBrightness, double? maxBrightness)
    {
        // Always redraw - the caller fires this on every slider move (master or individual)
        // and skipping when the rounded extremes look unchanged hides movement that the user expects to see
        // (e.g. interior sliders crossing past an extreme, or sub-rounded shifts during a master drag
        // where the visible thumb has moved).
        _activeMinBrightness = minBrightness;
        _activeMaxBrightness = maxBrightness;
        Redraw();
    }

    /// <summary>
    /// Re-points the live brightness / night-light list references at whichever set (absolute vs. offset)
    /// corresponds to the current mode. Called whenever either the backing model or the mode changes.
    /// </summary>
    private void ApplyCurveSelection()
    {
        if (_curveData == null) return;

        _brightness = _offsetMode ? _curveData.BrightnessOffset : _curveData.Brightness;
        _nightLight = _offsetMode ? _curveData.NightLightOffset : _curveData.NightLight;
    }

    /// <summary>
    /// Sets the blend factor (0..1) between piecewise-linear and full monotonic cubic Hermite (PCHIP) rendering.
    /// Values in between linearly interpolate the two curves so the user can dial the curvature
    /// without losing the polyline reference.
    /// </summary>
    public void SetSmoothness(double smoothness)
    {
        _smoothness = Math.Clamp(smoothness, 0.0, 1.0);
        Redraw();
    }

    private void PlotCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    internal void Redraw()
    {
        PlotCanvas.Children.Clear();
        HourLabelCanvas.Children.Clear();
        ValueLabelCanvas.Children.Clear();
        ValueLabelCanvasRight.Children.Clear();

        double w = PlotCanvas.ActualWidth;
        double h = PlotCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Sun overlay is drawn first so the grid lines, curves, and limit lines all sit on top
        // of the translucent twilight / night bands.
        DrawSunOverlay(w, h);

        DrawGrid(w, h);
        DrawHourLabels(w);
        DrawValueLabels(h);

        if (_showBrightness) DrawSeries(_brightness, Series.Brightness, GetBrightnessBrush(), w, h);

        if (_showNightLight) DrawSeries(_nightLight, Series.NightLight, GetNightLightBrush(), w, h);

        // Limit lines (offset-mode only) sit *above* the curves so the user can always grab them,
        // even when they overlap a curve thumb at the same Y.
        // Labels are deferred so we can resolve their pairwise X overlap once all line positions are known.
        if (_offsetMode && _curveData != null)
        {
            List<(Series series, LimitKind kind, double lineY, bool active)> labelSpecs = [];
            if (_showBrightness)
            {
                DrawLimitLine(Series.Brightness, LimitKind.Min, _curveData.BrightnessOffsetMin, w, h, labelSpecs);
                DrawLimitLine(Series.Brightness, LimitKind.Max, _curveData.BrightnessOffsetMax, w, h, labelSpecs);
            }

            if (_showNightLight)
            {
                DrawLimitLine(Series.NightLight, LimitKind.Min, _curveData.NightLightOffsetMin, w, h, labelSpecs);
                DrawLimitLine(Series.NightLight, LimitKind.Max, _curveData.NightLightOffsetMax, w, h, labelSpecs);
            }

            DrawLimitLabels(labelSpecs, w);
        }

        // Degeneration lines: informational marks at the curve value where the brightest active monitor
        // first saturates at 100 and the dimmest at 0.
        // Past those values, further curve travel can't move the pinned monitor - the dynamic range
        // of the monitor set degenerates.
        // Both modes preserve relative monitor offsets,
        // so the saturation points are determined by the slider values themselves;
        // only the curve-Y interpretation differs (offset shift vs. master anchor).
        if (_curveData != null && _showBrightness
            && _activeMinBrightness is { } minB
            && _activeMaxBrightness is { } maxB)
        {
            double? upperSample;
            double? lowerSample;
            if (_offsetMode)
            {
                // Offset mode: actual_i = slider_i + offsetPercent.
                // Saturation when the shift first equals 100 - max(slider) (upper) or -min(slider) (lower).
                // Curve sample is 0..100 with 50 = +0 offset; one offsetPercent unit = half a curve-Y unit
                // (the +/-100 display range spans 100 curve-Y units).
                upperSample = 50.0 + (100.0 - maxB) / 2.0;
                lowerSample = 50.0 + (0.0 - minB) / 2.0;
            }
            else
            {
                // Absolute mode: the curve master shifts the whole monitor block up/down
                // while preserving relative offsets.
                // Read each line as "the curve value at which the *non-saturating* extreme monitor sits":
                // at the upper line, the brightest just hits 100 and the dimmest reads (100 - gap);
                // at the lower line, the dimmest just hits 0 and the brightest reads (gap).
                // Master-independent - the gap (max - min) eats from both ends symmetrically.
                double gap = maxB - minB;
                upperSample = 100.0 - gap;
                lowerSample = gap;
            }

            if (upperSample is { } u && lowerSample is { } l)
            {
                List<(LimitKind kind, double lineY)> degSpecs = [];
                DrawDegenerationLine(u, LimitKind.Max, w, h, degSpecs);
                DrawDegenerationLine(l, LimitKind.Min, w, h, degSpecs);
                DrawDegenerationLabels(degSpecs, w);
            }
        }

        // Disabled-period chrome (pins, vertical guides, translucent overlay) draws after the curves
        // so the pins read as foreground controls.
        // Skipped wholesale when the feature is off; the top-inset shrinks back to the base value in that case.
        if (_disabledPeriodEnabled) DrawDisabledPeriod(w, h);

        // The cursor overlay lives on a sibling canvas, so it survives a Redraw
        // - but the curves under the cursor may have moved (e.g. mode flip, smoothness change),
        // so give the markers a chance to track the new shape.
        UpdateCursorOverlay();

        // Same overlay layer holds the persistent current-time line;
        // re-pin it after the plot's size or insets may have shifted
        // (offset-mode flip changes TopInset via the disabled-period reservation).
        UpdateCurrentTimeIndicator();
    }

    /// <summary>
    /// Draws the disabled-period overlay band, the two pin thumbs, and the grey vertical guides
    /// descending from below the pins to the bottom of the chart.
    /// Start may be greater than End - that's a wrap-midnight selection,
    /// rendered as two band segments flanking the visible range.
    /// The pins themselves stay tagged with their role (Start / End)
    /// so hit-testing can route a drag to the right field.
    /// </summary>
    private void DrawDisabledPeriod(double w, double h)
    {
        double bandTop = TopInset;
        double bandBottom = h - PlotInsetYBase;
        if (bandBottom <= bandTop) return;

        // Translucent white fill marks the disabled segment(s).
        // The user reads "between the pins" as the inactive period,
        // so when Start <= End the fill is a single rectangle;
        // when Start > End the fill wraps around midnight as two rectangles flanking the visible (active) middle.
        AppTheme? theme = AppServices.Theme;
        Brush bandBrush = new SolidColorBrush(
            theme?.CurveDisabledBandOverlay.Light
            ?? System.Windows.Media.Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF));
        if (_disabledPeriodStart <= _disabledPeriodEnd)
            DrawDisabledBand(_disabledPeriodStart, _disabledPeriodEnd, bandTop, bandBottom, w, bandBrush);
        else
        {
            DrawDisabledBand(_disabledPeriodStart, 1.0, bandTop, bandBottom, w, bandBrush);
            DrawDisabledBand(0.0, _disabledPeriodEnd, bandTop, bandBottom, w, bandBrush);
        }

        // Pin centres sit vertically centred in the reserved top inset so the body of each pin
        // clears the topmost gridline by a consistent margin regardless of how the chart resizes.
        // Grey guide lines descend from just below the pin to the bottom of the chart
        // so the pin's exact X is unambiguous against the grid.
        double pinCenterY = TopInset / 2.0;
        DrawDisabledPin(DisabledPin.Start, _disabledPeriodStart, pinCenterY, bandBottom, w);
        DrawDisabledPin(DisabledPin.End, _disabledPeriodEnd, pinCenterY, bandBottom, w);
    }

    private void DrawDisabledBand(double startT, double endT, double top, double bottom, double w, Brush brush)
    {
        if (endT <= startT) return;

        double x1 = ScreenX(startT, w);
        double x2 = ScreenX(endT, w);
        double bandWidth = x2 - x1;
        if (bandWidth <= 0) return;

        Rectangle band = new()
        {
            Width = bandWidth,
            Height = bottom - top,
            Fill = brush,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(band, x1);
        Canvas.SetTop(band, top);
        PlotCanvas.Children.Add(band);
    }

    private void DrawDisabledPin(DisabledPin pin, double t, double centerY, double guideBottom, double w)
    {
        double x = ScreenX(t, w);

        // Vertical guide first so the pin sits on top of it.
        // Dropped from just below the pin's bottom edge to the chart's bottom inset;
        // opacity tuned to match the grid chrome so the line is legible without competing with the curves.
        double guideTop = centerY + ThumbSize / 2.0;
        Line guide = new()
        {
            X1 = x, X2 = x, Y1 = guideTop, Y2 = guideBottom,
            Stroke = Brushes.LightGray,
            StrokeThickness = 1,
            Opacity = 0.5,
            IsHitTestVisible = false,
        };
        PlotCanvas.Children.Add(guide);

        // Pin uses the same Ellipse shape as curve thumbs so it inherits the visual weight
        // users already associate with grabbable nodes.
        // White fill distinguishes it from the coloured curve thumbs; hover / drag thicken the ring the same way.
        bool active =
            (_hoveredDisabledPin is { } hov && hov == pin) ||
            (_dragDisabledPin is { } drag && drag == pin);
        Ellipse thumb = new()
        {
            Width = ThumbSize,
            Height = ThumbSize,
            Fill = Brushes.White,
            Stroke = active ? Brushes.White : null,
            StrokeThickness = active ? 1.5 : 0,
            Cursor = Cursors.SizeWE,
            Tag = pin,
        };
        Canvas.SetLeft(thumb, x - ThumbSize / 2.0);
        Canvas.SetTop(thumb, centerY - ThumbSize / 2.0);
        PlotCanvas.Children.Add(thumb);
    }

    private bool TryHitDisabledPin(Point mouse, out DisabledPin hit)
    {
        // Walk children in reverse so the topmost pin wins when both pins land on the same X
        // (the wrap-midnight case where Start and End coincide).
        // Same hit-padding halo as curve thumbs so the grab area is consistent with the rest of the editor.
        for (int i = PlotCanvas.Children.Count - 1; i >= 0; i--)
        {
            if (PlotCanvas.Children[i] is Ellipse { Tag: DisabledPin tag } thumb)
            {
                double left = Canvas.GetLeft(thumb);
                double top = Canvas.GetTop(thumb);
                if (mouse.X >= left - ThumbHitPadding && mouse.X <= left + thumb.Width + ThumbHitPadding &&
                    mouse.Y >= top - ThumbHitPadding && mouse.Y <= top + thumb.Height + ThumbHitPadding)
                {
                    hit = tag;
                    return true;
                }
            }
        }
        hit = default;
        return false;
    }

    private void DrawLimitLine(Series series, LimitKind kind, double value, double w, double h,
        List<(Series series, LimitKind kind, double lineY, bool active)> labelSpecs)
    {
        double y = ScreenY(value, h);

        // Highlight when hovered or actively dragged - a noticeable visual cue that the line is grabbable,
        // since otherwise the dashed pattern can read as inert grid art.
        bool active =
            (_hoveredLimit is { } hov && hov.Series == series && hov.Kind == kind) ||
            (_draggingLimit && _limitDragSeries == series && _limitDragKind == kind);

        Line line = new()
        {
            X1 = PlotInsetX, X2 = w - PlotInsetX, Y1 = y, Y2 = y,
            Stroke = active ? Brushes.White : Brushes.LightGray,
            StrokeThickness = 1.5,
            // 4-on / 3-off dash pattern is the standard light-grey grid look used elsewhere in the codebase;
            // tight enough to read as a single line without obscuring curves.
            StrokeDashArray = [4.0, 3.0],
            Opacity = active ? 1.0 : 0.7,
            // Hit-tested explicitly via TryHitLimitLine; leaving IsHitTestVisible default would let the line
            // eat clicks meant for the curve thumbs sitting on top.
            IsHitTestVisible = false,
            Tag = (series, kind),
        };
        PlotCanvas.Children.Add(line);

        labelSpecs.Add((series, kind, y, active));
    }

    /// <summary>
    /// Draws the "minimum brightness" / "maximum night light" captions for every active clamp line.
    /// Min sits above its line, max below it.
    /// Each caption is centered across the plot by default;
    /// when two captions land on overlapping vertical bands they get fanned out left/right so they never collide.
    /// Each label is tagged with its (series, kind) so <see cref="TryHitLimitLine"/> can use the caption itself
    /// as a hit-test box - the text is then a grabbable extension of the dashed line.
    /// </summary>
    private void DrawLimitLabels(List<(Series series, LimitKind kind, double lineY, bool active)> specs, double w)
    {
        if (specs.Count == 0) return;

        Brush fg = (Brush)FindResource("ThemeSecondaryForeground");
        // 2px line-to-label gap matches the existing visual rhythm;
        // 6px is the minimum space we leave between two laterally-fanned labels so they read as separate.
        const double gap = 2.0;
        const double horizontalGap = 6.0;
        double plotMid = PlotInsetX + (w - 2 * PlotInsetX) / 2;

        List<(TextBlock tb, double left, double top, double width, double height, int specIdx)> entries = [];
        for (int i = 0; i < specs.Count; i++)
        {
            (Series series, LimitKind kind, double lineY, bool active) spec = specs[i];
            string limitLabelKey = (spec.series, spec.kind) switch
            {
                (Series.Brightness, LimitKind.Min) => "Settings_CurveEditor_LimitLabel_MinBrightness",
                (Series.Brightness, LimitKind.Max) => "Settings_CurveEditor_LimitLabel_MaxBrightness",
                (Series.NightLight, LimitKind.Min) => "Settings_CurveEditor_LimitLabel_MinNightLight",
                _ => "Settings_CurveEditor_LimitLabel_MaxNightLight",
            };
            TextBlock tb = new()
            {
                Text = LocalizationManager.Instance[limitLabelKey],
                FontSize = TimeAxisLabelFontSize,
                // Match the line: brighten when hovered/dragged, otherwise blend with grid chrome.
                Foreground = spec.active ? Brushes.White : fg,
                Opacity = spec.active ? 1.0 : 0.7,
                // Click flows through to PlotCanvas; TryHitLimitLine reads the label's bounds back out
                // so the caption stays a grabbable extension of the line.
                IsHitTestVisible = false,
                Tag = (spec.series, spec.kind),
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double topY = spec.kind == LimitKind.Min
                ? spec.lineY - tb.DesiredSize.Height - gap
                : spec.lineY + gap;
            double leftDefault = plotMid - tb.DesiredSize.Width / 2;
            entries.Add((tb, leftDefault, topY, tb.DesiredSize.Width, tb.DesiredSize.Height, i));
        }

        // Cluster labels by transitive Y-overlap, then fan each multi-label cluster across plotMid
        // in (series, kind) order. Single-label clusters keep their centred default.
        bool[] visited = new bool[entries.Count];
        for (int seed = 0; seed < entries.Count; seed++)
        {
            if (visited[seed]) continue;

            List<int> cluster = [seed];
            visited[seed] = true;
            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int j = 0; j < entries.Count; j++)
                {
                    if (visited[j]) continue;
                    foreach (int k in cluster)
                    {
                        (TextBlock tb, double left, double top, double width, double height, int specIdx) ek
                            = entries[k];
                        (TextBlock tb, double left, double top, double width, double height, int specIdx) ej
                            = entries[j];
                        if (!(ek.top + ek.height <= ej.top || ej.top + ej.height <= ek.top))
                        {
                            cluster.Add(j);
                            visited[j] = true;
                            grew = true;
                            break;
                        }
                    }
                }
            }

            if (cluster.Count <= 1) continue;

            cluster.Sort((a, b) =>
            {
                (Series series, LimitKind kind, double lineY, bool active) sa = specs[entries[a].specIdx];
                (Series series, LimitKind kind, double lineY, bool active) sb = specs[entries[b].specIdx];
                int c = sa.series.CompareTo(sb.series);
                return c != 0 ? c : sa.kind.CompareTo(sb.kind);
            });

            double total = 0;
            foreach (int idx in cluster) total += entries[idx].width;
            total += (cluster.Count - 1) * horizontalGap;

            double cursor = plotMid - total / 2;
            foreach (int idx in cluster)
            {
                (TextBlock tb, double left, double top, double width, double height, int specIdx) e = entries[idx];
                e.left = cursor;
                entries[idx] = e;
                cursor += e.width + horizontalGap;
            }
        }

        foreach ((TextBlock tb, double left, double top, double width, double height, int specIdx) entry in entries)
        {
            Canvas.SetLeft(entry.tb, entry.left);
            Canvas.SetTop(entry.tb, entry.top);
            PlotCanvas.Children.Add(entry.tb);
        }
    }

    /// <summary>
    /// Draws a single dotted "degeneration" line. Same X span as the user-clamp lines but thinner, dimmer,
    /// and dotted instead of dashed - reads as informational chrome rather than a draggable control.
    /// Skips drawing when the sample falls outside the plot.
    /// </summary>
    private void DrawDegenerationLine(double sample, LimitKind kind, double w, double h,
        List<(LimitKind kind, double lineY)> labelSpecs)
    {
        if (sample is < 0.0 or > 100.0) return;
        double y = ScreenY(sample, h);
        Line line = new()
        {
            X1 = PlotInsetX, X2 = w - PlotInsetX, Y1 = y, Y2 = y,
            Stroke = Brushes.LightGray,
            StrokeThickness = 1.0,
            // Tight 1-on / 2.5-off pattern reads as dotted, distinct from the user-clamp line's 4/3 dash.
            // Visually subordinate so it doesn't compete with the clamps.
            StrokeDashArray = [1.0, 2.5],
            Opacity = 0.45,
            IsHitTestVisible = false,
        };
        PlotCanvas.Children.Add(line);
        labelSpecs.Add((kind, y));
    }

    /// <summary>
    /// Draws "brightness offset degeneration" captions for the degeneration lines.
    /// Same font and gap rhythm as <see cref="DrawLimitLabels"/>, but no clustering or fan-out
    /// - the two degeneration lines won't share a Y row in any sane configuration,
    /// and minor overlap with a user-clamp label is acceptable for an informational mark.
    /// </summary>
    private void DrawDegenerationLabels(List<(LimitKind kind, double lineY)> specs, double w)
    {
        if (specs.Count == 0) return;
        Brush fg = (Brush)FindResource("ThemeSecondaryForeground");
        const double gap = 2.0;
        double plotMid = PlotInsetX + (w - 2 * PlotInsetX) / 2;
        foreach ((LimitKind kind, double lineY) in specs)
        {
            TextBlock tb = new()
            {
                Text = LocalizationManager.Instance[kind == LimitKind.Max
                    ? "Settings_CurveEditor_DegenerationLabel_UpperBrightnessOffset"
                    : "Settings_CurveEditor_DegenerationLabel_LowerBrightnessOffset"],
                FontSize = TimeAxisLabelFontSize,
                Foreground = fg,
                Opacity = 0.45,
                IsHitTestVisible = false,
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, plotMid - tb.DesiredSize.Width / 2);
            Canvas.SetTop(tb, kind == LimitKind.Min
                ? lineY - tb.DesiredSize.Height - gap
                : lineY + gap);
            PlotCanvas.Children.Add(tb);
        }
    }

    /// <summary>
    /// Shades the plot background with twilight (orange) and night (greyish-blue) bands
    /// for the configured geo coordinates and today's date. Daytime is left clear.
    /// No-ops when the overlay toggle is off, when coordinates are unset (both zero / out of range),
    /// or when the SPA can't yield any usable boundary times.
    /// </summary>
    private void DrawSunOverlay(double w, double h)
    {
        if (!_showSunOverlay) return;

        // Both-zero is the "null island" sentinel for "unset" - the persisted defaults are a real
        // Pacific-NW pin so a user who never opens the geo card still gets sensible overlays.
        // Out-of-range values are likewise treated as unset rather than passed to the SPA.
        if ((_latitude == 0.0 && _longitude == 0.0)
            || _latitude < -90.0 || _latitude > 90.0
            || _longitude < -180.0 || _longitude > 180.0)
            return;

        SunTimes sun;
        DateTimeOffset reference = GetOverlayReferenceTime();
        try
        {
            sun = SPACalculator.GetSunTimes(_latitude, _longitude, reference);
        }
        catch
        {
            // SPA computation can throw for pathological inputs;
            // silently skip the overlay rather than break the editor.
            return;
        }

        double? sunriseT = ToDayFraction(sun.Sunrise);
        double? sunsetT = ToDayFraction(sun.Sunset);
        double? astroDawnT = ToDayFraction(sun.Twilight?.AstronomicalDawn);
        double? astroDuskT = ToDayFraction(sun.Twilight?.AstronomicalDusk);

        // Polar case (no horizon crossing today): fall back to noon solar elevation to decide whether
        // the whole plot is daytime (skip overlay), twilight, or night.
        if (sunriseT is null && sunsetT is null)
        {
            DrawWholeDayPolarOverlay(w, h);
            return;
        }

        // Mid-latitude normal case: night -> twilight -> day -> twilight -> night, with each boundary
        // potentially missing if the sun never descends that far (e.g. mid-summer near the polar circle
        // has no astronomical night, so astroDawn/astroDusk are null but sunrise/sunset are present).
        if (sunriseT is { } sr && sunsetT is { } ss && sr <= ss)
        {
            // Morning night: 0 -> astroDawn (only if the sun gets that low).
            if (astroDawnT is { } dawn)
            {
                AddOverlayBand(0.0, dawn, GetNightBackdropBrush(), w, h);
                AddOverlayBand(dawn, sr, GetTwilightBackdropBrush(), w, h);
            }
            else
            {
                // No astronomical night this morning - everything before sunrise is twilight.
                AddOverlayBand(0.0, sr, GetTwilightBackdropBrush(), w, h);
            }

            // Daytime gap (sr -> ss) intentionally has no overlay.
            if (astroDuskT is { } dusk)
            {
                AddOverlayBand(ss, dusk, GetTwilightBackdropBrush(), w, h);
                AddOverlayBand(dusk, 1.0, GetNightBackdropBrush(), w, h);
            }
            else
                AddOverlayBand(ss, 1.0, GetTwilightBackdropBrush(), w, h);
        }
    }

    /// <summary>
    /// Polar fallback: with no sunrise/sunset today, classify the entire day by the noon solar elevation.
    /// Above the horizon -> midnight sun (no overlay); above -18 deg -> all twilight; otherwise -> all night.
    /// </summary>
    private void DrawWholeDayPolarOverlay(double w, double h)
    {
        SolarPosition? noonPos;
        try
        {
            DateTime baseDay = _sunOverlayDate ?? DateTime.Today;
            DateTime localNoon = DateTime.SpecifyKind(baseDay.Date.AddHours(12), DateTimeKind.Unspecified);
            TimeSpan offset = _useDaylightSavings
                ? TimeZoneInfo.Local.GetUtcOffset(localNoon)
                : TimeZoneInfo.Local.BaseUtcOffset;
            DateTimeOffset noon = new(localNoon, offset);
            noonPos = SPACalculator.GetSolarPosition(_latitude, _longitude, noon);
        }
        catch
        {
            return;
        }

        if (noonPos is null) return;

        if (noonPos.Elevation > 0.0)
        {
            // Midnight sun - daytime everywhere, no overlay.
            return;
        }

        Brush brush = noonPos.Elevation > -18.0 ? GetTwilightBackdropBrush() : GetNightBackdropBrush();
        AddOverlayBand(0.0, 1.0, brush, w, h);
    }

    private void AddOverlayBand(double startT, double endT, Brush brush, double w, double h)
    {
        if (endT <= startT) return;

        double x1 = ScreenX(startT, w);
        double x2 = ScreenX(endT, w);
        double bandWidth = x2 - x1;
        if (bandWidth <= 0) return;

        double top = TopInset;
        double bottom = h - PlotInsetYBase;
        double bandHeight = bottom - top;
        if (bandHeight <= 0) return;

        Rectangle band = new()
        {
            Width = bandWidth,
            Height = bandHeight,
            Fill = brush,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(band, x1);
        Canvas.SetTop(band, top);
        PlotCanvas.Children.Add(band);
    }

    /// <summary>
    /// Picks the DateTimeOffset to feed to the SPA.
    /// With no override, that's "now";
    /// with one set, it's noon of the chosen calendar date in that day's local offset
    /// (noon avoids any midnight-edge ambiguity around DST transitions).
    ///
    /// When DST is disabled on the curve, the timezone's BaseUtcOffset is used in both branches
    /// so sun-event clock-times don't jump an hour at the DST boundaries.
    /// </summary>
    private DateTimeOffset GetOverlayReferenceTime()
    {
        if (_sunOverlayDate is { } overrideDate)
        {
            // Strip Kind so the DateTimeOffset ctor doesn't reject the offset when the override falls
            // in a different DST period than "now" (e.g. picking a Nov date while today is in summer time).
            // Then look up the offset that was actually in effect at the override's local noon,
            // or pin to standard time if DST is disabled for this curve.
            DateTime localNoon = DateTime.SpecifyKind(overrideDate.Date.AddHours(12), DateTimeKind.Unspecified);
            TimeSpan offset = _useDaylightSavings
                ? TimeZoneInfo.Local.GetUtcOffset(localNoon)
                : TimeZoneInfo.Local.BaseUtcOffset;
            return new DateTimeOffset(localNoon, offset);
        }

        if (!_useDaylightSavings)
        {
            // Reinterpret the current wall-clock as standard time:
            // the SPA emits sun events using the supplied offset, so feeding it the base offset puts
            // sunrise/sunset on the chart's clock-axis at their standard-time positions year-round.
            // The user's wall-clock cursor lines up with the same axis.
            DateTime nowLocal = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified);
            return new DateTimeOffset(nowLocal, TimeZoneInfo.Local.BaseUtcOffset);
        }

        return DateTimeOffset.Now;
    }

    /// <summary>
    /// Converts a sun event timestamp into a [0, 1] fraction of the day,
    /// expressed in the offset that was supplied when the event was computed.
    /// Use <see cref="DateTimeOffset.DateTime"/> rather than <c>LocalDateTime</c> here:
    /// <c>LocalDateTime</c> reinterprets the instant in the system's actual timezone (DST-aware),
    /// which would silently undo any explicit offset choice (e.g. our standard-time-only mode).
    /// Returns null when the event is missing or falls outside the [0, 24]h window.
    /// </summary>
    private static double? ToDayFraction(DateTimeOffset? when)
    {
        if (when is not { } t) return null;

        double hours = t.DateTime.TimeOfDay.TotalHours;
        if (hours is < 0.0 or > 24.0) return null;

        return Math.Clamp(hours / 24.0, 0.0, 1.0);
    }

    private void DrawGrid(double w, double h)
    {
        // Pull from the dedicated grid-line brush so the picker on the Theme page's Environmental section
        // can override it independently of the global ThemeSeparator (which would otherwise affect chrome
        // throughout the app, not just the chart).
        Brush gridBrush = (Brush)FindResource("EnvironmentalGridLineBrush");
        double left = PlotInsetX;
        double right = w - PlotInsetX;
        double top = TopInset;
        double bottom = h - PlotInsetYBase;

        for (int i = 0; i <= VerticalGridDivisions; i++)
        {
            double y = ScreenY(100.0 * (VerticalGridDivisions - i) / VerticalGridDivisions, h);
            // The middle horizontal gridline is special in offset mode (the "+0" no-offset baseline).
            // Bump its opacity so the neutral line is obvious at a glance.
            bool isZeroLineInOffset = _offsetMode && i == VerticalGridDivisions / 2;
            Line line = new()
            {
                X1 = left, X2 = right, Y1 = y, Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 1,
                Opacity = isZeroLineInOffset ? 0.85 : 0.4,
                IsHitTestVisible = false,
            };
            PlotCanvas.Children.Add(line);
        }

        for (int i = 0; i <= HorizontalGridDivisions; i++)
        {
            double x = ScreenX((double)i / HorizontalGridDivisions, w);
            Line line = new()
            {
                X1 = x, X2 = x, Y1 = top, Y2 = bottom,
                Stroke = gridBrush,
                StrokeThickness = 1,
                Opacity = 0.4,
                IsHitTestVisible = false,
            };
            PlotCanvas.Children.Add(line);
        }
    }

    // Coordinate helpers - time t in [0, 1] and value v in [0, 100] map onto a viewport inset by PlotInsetX
    // horizontally and TopInset / PlotInsetYBase vertically.
    // Top inset grows by DisabledPeriodPinAreaHeight when the disabled period feature is on
    // so the pins above the graph aren't clipped against the editor chrome.
    // Inverses go the other direction for mouse handlers - same inset value on both sides.
    private double TopInset => PlotInsetYBase + (_disabledPeriodEnabled ? DisabledPeriodPinAreaHeight : 0.0);
    private static double ScreenX(double t, double w) => PlotInsetX + t * (w - 2 * PlotInsetX);
    private double ScreenY(double v, double h) =>
        TopInset + (1.0 - v / 100.0) * (h - TopInset - PlotInsetYBase);
    private static double FromScreenX(double x, double w) => (x - PlotInsetX) / (w - 2 * PlotInsetX);
    private double FromScreenY(double y, double h) =>
        (1.0 - (y - TopInset) / (h - TopInset - PlotInsetYBase)) * 100.0;

    private void DrawValueLabels(double h)
    {
        Brush fg = (Brush)FindResource("ThemeSecondaryForeground");
        for (int i = 0; i <= VerticalGridDivisions; i++)
        {
            // Absolute mode runs 100..0 top-to-bottom; offset mode runs +100..-100 with 0 at the midline.
            // Both share the same Value=0..100 storage - only the displayed scale differs -
            // so the user can switch modes without the underlying numbers changing.
            int value = _offsetMode
                ? 100 - (int)Math.Round(200.0 * i / VerticalGridDivisions)
                : 100 - (int)Math.Round(100.0 * i / VerticalGridDivisions);
            string text = _offsetMode && value > 0 ? $"+{value}" : value.ToString();
            // Match the gridline Y exactly so each label sits on its own horizontal rule.
            double y = ScreenY(100.0 * (VerticalGridDivisions - i) / VerticalGridDivisions, h);

            // Left gutter label: right-aligned so it hugs the plot edge
            // (closest distance possible without intruding into the plot area).
            // Wider labels like "+100" overflow leftward into the chrome rather than encroaching on the gridlines.
            TextBlock leftLabel = BuildValueLabel(text, fg);
            leftLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(leftLabel, ValueLabelCanvas.ActualWidth - leftLabel.DesiredSize.Width);
            Canvas.SetTop(leftLabel, y - leftLabel.DesiredSize.Height / 2);
            ValueLabelCanvas.Children.Add(leftLabel);

            // Right gutter mirrors the left set; left-aligned so labels hug the plot's right edge.
            // Same value at the same Y - the duplication is intentional;
            // mirroring the axis on both sides keeps wide curves readable
            // without the cursor having to travel across the plot to read off a value.
            TextBlock rightLabel = BuildValueLabel(text, fg);
            rightLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(rightLabel, 0);
            Canvas.SetTop(rightLabel, y - rightLabel.DesiredSize.Height / 2);
            ValueLabelCanvasRight.Children.Add(rightLabel);
        }
    }

    private static TextBlock BuildValueLabel(string text, Brush fg) => new()
    {
        Text = text,
        FontSize = TimeAxisLabelFontSize,
        Foreground = fg,
        Opacity = 0.7,
        IsHitTestVisible = false,
    };

    private void DrawHourLabels(double w)
    {
        Brush fg = (Brush)FindResource("ThemeSecondaryForeground");
        bool use24Hour = SystemUses24HourClock();
        for (int i = 0; i <= HorizontalGridDivisions; i++)
        {
            int hour = (int)Math.Round(24.0 * i / HorizontalGridDivisions);
            double x = ScreenX((double)i / HorizontalGridDivisions, w);
            TextBlock label = new()
            {
                Text = FormatHourLabel(hour, use24Hour),
                FontSize = TimeAxisLabelFontSize,
                Foreground = fg,
                Opacity = 0.7,
                IsHitTestVisible = false,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            // Centre every label on its gridline.
            // The PlotInsetX breathing room keeps the first / last label from clipping the chrome
            // without needing edge-specific bias.
            Canvas.SetLeft(label, x - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, 2);
            HourLabelCanvas.Children.Add(label);
        }
    }

    /// <summary>
    /// Detects whether the user's current short-time pattern is 24-hour.
    /// The pattern uses 'H' / 'HH' for 24-hour and 'h' / 'hh' for 12-hour,
    /// so the presence of a lowercase 'h' is the canonical marker.
    /// Reads from <see cref="System.Globalization.CultureInfo.CurrentCulture"/>
    /// rather than the system registry directly
    /// so user overrides applied to .NET's culture (e.g. via Region settings) are honoured automatically.
    /// </summary>
    private static bool SystemUses24HourClock()
    {
        string pattern = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern;
        return !pattern.Contains('h');
    }

    private static string FormatHourLabel(int hour, bool use24Hour)
    {
        if (use24Hour)
        {
            // Pad to two digits (24:00 at end of day stays as a literal "24:00"
            // so the cycle's closing midnight is visually distinct from the opening one).
            return $"{hour:D2}:00";
        }

        // 12-hour: 0/24 -> 12am, 12 -> 12pm, otherwise hour mod 12 with am/pm suffix.
        // Minutes are dropped on this side since the gridlines only ever land on whole hours
        // - the ":00" was redundant noise next to "5am" / "6pm".
        string suffix = hour is < 12 or 24 ? "am" : "pm";
        int display = hour % 12;
        if (display == 0) display = 12;
        return $"{display}{suffix}";
    }

    private void DrawSeries(List<EnvironmentalCurvePoint> points, Series series, Brush brush, double w, double h)
    {
        if (points.Count == 0) return;

        List<EnvironmentalCurvePoint> ordered = [.. points.OrderBy(p => p.Time)];
        int n = ordered.Count;

        if (n >= 2)
        {
            double[] xs = new double[n];
            double[] ys = new double[n];
            for (int i = 0; i < n; i++)
            {
                xs[i] = ordered[i].Time;
                ys[i] = ordered[i].Value;
            }

            double[] tangents = EnvironmentalCurveSampler.ComputeMonotonicTangents(xs, ys);

            // Sample roughly once per pixel of plot width (excluding inset)
            // so the cubic shape stays smooth without oversampling the inset margin.
            // Tangents and arrays are built once outside the loop and reused per sample
            // - calling EnvironmentalCurveSampler.Sample inside the loop would recompute tangents every iteration,
            // so the per-sample math calls the lower-level primitives directly.
            // Same shape the runtime evaluator produces - both paths share the same internal statics.
            double plotW = w - 2 * PlotInsetX;
            int samples = Math.Max(2, (int)Math.Ceiling(plotW));
            Polyline line = new()
            {
                Stroke = brush,
                StrokeThickness = 2,
                IsHitTestVisible = false,
            };
            for (int i = 0; i < samples; i++)
            {
                double t = (double)i / (samples - 1);
                double linear = EnvironmentalCurveSampler.InterpolateLinear(xs, ys, t);
                double cubic = EnvironmentalCurveSampler.InterpolateMonotonicCubic(xs, ys, tangents, t);
                double v = linear + (cubic - linear) * _smoothness;
                line.Points.Add(new Point(ScreenX(t, w), ScreenY(v, h)));
            }
            PlotCanvas.Children.Add(line);
        }

        // Node ring uses the foreground theme brush so it contrasts with the plot background
        // in both modes (black on light, white on dark) instead of staying invisible against a near-white wash.
        Brush nodeBorderBrush = (Brush)FindResource("ThemeForeground");
        foreach (EnvironmentalCurvePoint p in ordered)
        {
            // Show a contrasting ring only while the cursor is inside this thumb's halo,
            // or while it's the active drag target - same affordance the limit lines use.
            bool active =
                (_hoveredThumb is { } hov && hov.Series == series && ReferenceEquals(hov.Point, p)) ||
                (_dragPoint != null && _dragSeries == series && ReferenceEquals(_dragPoint, p));
            // Keyboard selection draws a thicker ring
            // so the focused node reads as distinct from a transient mouse-over highlight
            // - the keyboard user needs a stable cue for which node arrow keys / delete will hit.
            bool selected =
                _selectedPoint != null && _selectedSeries == series && ReferenceEquals(_selectedPoint, p);
            Ellipse thumb = new()
            {
                Width = ThumbSize,
                Height = ThumbSize,
                Fill = brush,
                Stroke = (active || selected) ? nodeBorderBrush : null,
                StrokeThickness = selected ? 2.5 : (active ? 1.5 : 0),
                Cursor = Cursors.Hand,
                Tag = (series, p),
            };
            Canvas.SetLeft(thumb, ScreenX(p.Time, w) - ThumbSize / 2);
            Canvas.SetTop(thumb, ScreenY(p.Value, h) - ThumbSize / 2);
            PlotCanvas.Children.Add(thumb);
        }
    }

    /// <summary>
    /// Samples the rendered (linear+cubic blend) value of a series at a given normalised time.
    /// Returns NaN for an empty series so callers can treat it as "no curve here"
    /// - used by the click-routing logic to skip empty curves when picking the closest one.
    /// Delegates to <see cref="EnvironmentalCurveSampler.Sample"/>
    /// so the editor's hit-test math, the editor's render math, and the runtime curve evaluator
    /// all read the same shape from the same primitives.
    /// </summary>
    private double SampleCurveAt(List<EnvironmentalCurvePoint> series, double t)
    {
        if (series.Count == 0) return double.NaN;
        return EnvironmentalCurveSampler.Sample(series, t, _smoothness);
    }

    /// <summary>
    /// Claim mouse capture for an in-progress drag,
    /// retrying on the next dispatcher pump if the immediate call fails.
    /// The first click after the host SettingsWindow is activated by the click itself can land mid-WM_MOUSEACTIVATE,
    /// when the WPF input system is in a transitional state and <c>Mouse.Capture</c> silently fails.
    /// Without capture, MouseMove stops firing the moment the cursor leaves PlotCanvas bounds and the drag dies.
    /// The deferred retry runs after activation has settled, so capture takes and the drag tracks.
    /// </summary>
    private void CaptureForDrag()
    {
        if (PlotCanvas.CaptureMouse()) return;
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (_dragPoint != null || _dragDisabledPin != null || _draggingLimit) PlotCanvas.CaptureMouse();
            }),
            DispatcherPriority.Input);
    }

    private void PlotCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Preview mode locks editing (no thumb drag, no limit drag, no point insertion). The cursor
        // readout still updates from MouseMove so the user can scrub values.
        if (_previewMode) return;

        double w = PlotCanvas.ActualWidth;
        double h = PlotCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Take keyboard focus so subsequent arrow / tab / delete keystrokes route through the curve-edit
        // handler instead of bubbling up to the SettingsWindow.
        Focus();

        Point pos = e.GetPosition(PlotCanvas);

        // Disabled-period pins live above the chart, where curve thumbs and limit lines never sit,
        // so hit-test them first.
        // A successful hit starts a horizontal drag and the click never falls through to "add a new point".
        if (_disabledPeriodEnabled && TryHitDisabledPin(pos, out DisabledPin pinHit))
        {
            _dragDisabledPin = pinHit;
            CaptureForDrag();
            Redraw();
            e.Handled = true;
            return;
        }

        // Hit-test thumbs first - clicking on a thumb starts a drag rather than spawning a new point.
        if (TryHitThumb(pos, out (Series series, EnvironmentalCurvePoint point) hit))
        {
            _dragPoint = hit.point;
            _dragSeries = hit.series;
            _selectedPoint = hit.point;
            _selectedSeries = hit.series;
            CaptureForDrag();
            Redraw();
            e.Handled = true;
            return;
        }

        // Limit lines (offset mode only). Hit-tested after thumbs so a thumb sitting on a limit line still wins
        // - thumbs are smaller and harder to grab, so they get priority.
        if (_offsetMode && TryHitLimitLine(pos, out (Series series, LimitKind kind) limitHit))
        {
            _draggingLimit = true;
            _limitDragSeries = limitHit.series;
            _limitDragKind = limitHit.kind;
            CaptureForDrag();
            e.Handled = true;
            return;
        }

        // Empty space - add a new point.
        // When both curves are visible we route the click to whichever curve is closer to the cursor (in pixel space)
        // so the user only edits one line at a time. If only one curve is visible the click goes there unconditionally.
        Point mouse = e.GetPosition(PlotCanvas);
        double t = Math.Clamp(FromScreenX(mouse.X, w), 0.0, 1.0);
        double v = Math.Clamp(FromScreenY(mouse.Y, h), 0.0, 100.0);

        List<EnvironmentalCurvePoint>? target = PickClosestVisibleSeries(t, mouse.Y, h);
        if (target == null) return;

        if (AddPoint(target, t, v))
        {
            // Promote the new (or snapped-to-existing) node to the keyboard selection
            // so a follow-up arrow/space stroke acts on what the user just clicked.
            _selectedSeries = ReferenceEquals(target, _brightness) ? Series.Brightness : Series.NightLight;
            _selectedPoint = FindNearestByTime(target, t);
            Redraw();
            CurveChanged?.Invoke();
        }
    }

    private static EnvironmentalCurvePoint? FindNearestByTime(List<EnvironmentalCurvePoint> series, double t)
    {
        EnvironmentalCurvePoint? best = null;
        double bestDt = double.PositiveInfinity;
        foreach (EnvironmentalCurvePoint p in series)
        {
            double dt = Math.Abs(p.Time - t);
            if (dt < bestDt)
            {
                bestDt = dt;
                best = p;
            }
        }
        return best;
    }

    /// <summary>
    /// Returns whichever currently-visible series renders closest to the click in pixel space,
    /// or null if neither is visible.
    /// NaN samples (empty series) lose the comparison
    /// so a freshly emptied curve doesn't out-rank a populated one for a stray click.
    /// </summary>
    private List<EnvironmentalCurvePoint>? PickClosestVisibleSeries(double t, double clickY, double h)
    {
        bool brightnessVisible = _showBrightness;
        bool nightVisible = _showNightLight;
        if (!brightnessVisible && !nightVisible) return null;

        if (brightnessVisible && !nightVisible) return _brightness;

        if (!brightnessVisible && nightVisible) return _nightLight;

        double brightnessDist = DistanceToCurve(_brightness, t, clickY, h);
        double nightDist = DistanceToCurve(_nightLight, t, clickY, h);
        if (double.IsNaN(brightnessDist) && double.IsNaN(nightDist))
        {
            // Both empty - default to brightness so the user always lands somewhere.
            return _brightness;
        }

        if (double.IsNaN(brightnessDist)) return _nightLight;

        if (double.IsNaN(nightDist)) return _brightness;

        return brightnessDist <= nightDist ? _brightness : _nightLight;
    }

    private double DistanceToCurve(List<EnvironmentalCurvePoint> series, double t, double clickY, double h)
    {
        double v = SampleCurveAt(series, t);
        if (double.IsNaN(v)) return double.NaN;

        double curveY = ScreenY(v, h);
        return Math.Abs(clickY - curveY);
    }

    private void PlotCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_previewMode) return;

        if (!TryHitThumb(e.GetPosition(PlotCanvas), out (Series series, EnvironmentalCurvePoint point) hit)) return;

        // The leftmost / rightmost nodes anchor the curve at the edges
        // - removing them would leave the cubic interpolator
        // with no defined value over a leading or trailing slice of the day, so they're protected from deletion.
        List<EnvironmentalCurvePoint> series = GetSeries(hit.series);
        if (IsEndpoint(hit.point, series))
        {
            e.Handled = true;
            return;
        }

        series.Remove(hit.point);
        // Right-click delete invalidates the selection if it was sitting on this node;
        // promote the nearest neighbour so a follow-up keystroke still has somewhere to land.
        if (_selectedPoint != null && ReferenceEquals(_selectedPoint, hit.point))
            _selectedPoint = PickNeighbourAfterRemoval(series, hit.point.Time);
        Redraw();
        CurveChanged?.Invoke();
        e.Handled = true;
    }

    private static EnvironmentalCurvePoint? PickNeighbourAfterRemoval(
        List<EnvironmentalCurvePoint> series,
        double removedTime)
    {
        if (series.Count == 0) return null;

        EnvironmentalCurvePoint? best = null;
        double bestDt = double.PositiveInfinity;
        foreach (EnvironmentalCurvePoint p in series)
        {
            double dt = Math.Abs(p.Time - removedTime);
            if (dt < bestDt)
            {
                bestDt = dt;
                best = p;
            }
        }
        return best;
    }

    private static bool IsEndpoint(EnvironmentalCurvePoint point, List<EnvironmentalCurvePoint> series)
    {
        // Identified by stable position (first / last after a Time-sort) rather than by Time value,
        // so when two nodes share the smallest (or largest) Time exactly only one of them counts as the edge anchor
        // - the other behaves like a regular interior node that can be dragged horizontally and deleted.
        if (series.Count == 0) return false;

        List<EnvironmentalCurvePoint> ordered = [.. series.OrderBy(p => p.Time)];
        return ReferenceEquals(point, ordered[0]) || ReferenceEquals(point, ordered[^1]);
    }

    // Edge nodes at t=0 and t=1 represent the same midnight instant on the wrapped 24h cycle.
    // Whenever one's Value is mutated, mirror it to the other so the curve stays continuous across midnight.
    // Identifies the edges by the same stable reference rule IsEndpoint uses (first / last after a Time-sort).
    private static void SyncEdgeYIfEdge(
        EnvironmentalCurvePoint mutated,
        List<EnvironmentalCurvePoint> series)
    {
        if (series.Count < 2) return;

        List<EnvironmentalCurvePoint> ordered = [.. series.OrderBy(p => p.Time)];
        EnvironmentalCurvePoint first = ordered[0];
        EnvironmentalCurvePoint last = ordered[^1];

        if (ReferenceEquals(mutated, first) && !ReferenceEquals(mutated, last))
            last.Value = mutated.Value;
        else if (ReferenceEquals(mutated, last) && !ReferenceEquals(mutated, first)) first.Value = mutated.Value;
    }

    private void PlotCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        Point mouse = e.GetPosition(PlotCanvas);
        double w = PlotCanvas.ActualWidth;
        double h = PlotCanvas.ActualHeight;

        // Cursor-readout overlay tracks the mouse continuously while it's inside the canvas,
        // even during a drag (so the time/value display stays live).
        // UpdateCursorOverlay mutates persistent OverlayCanvas children instead of triggering a Redraw.
        _cursorPos = mouse;
        UpdateCursorOverlay();

        // Cursor + thumb-ring hover + limit-line hover share a single thumb hit-test;
        // a node hit suppresses the limit-line highlight entirely, since clicking there would target the node.
        // All three hover states roll up into one redraw at the end
        // so a transition that flips multiple flags only repaints once.
        // Skipped in preview mode - thumbs and limit lines are non-interactive then.
        if (!_previewMode && !_draggingLimit && _dragPoint is null && _dragDisabledPin is null)
        {
            // Disabled-period pins win priority over thumbs
            // because they live in the top inset where curve thumbs never reach
            // - the precedence is purely architectural, but it gives the cursor a deterministic resting state.
            DisabledPin? nextPinHover = null;
            bool overPin = false;
            if (_disabledPeriodEnabled && TryHitDisabledPin(mouse, out DisabledPin pinHover))
            {
                overPin = true;
                nextPinHover = pinHover;
            }

            (Series series, EnvironmentalCurvePoint point) thumbHit = default;
            bool overThumb = !overPin && TryHitThumb(mouse, out thumbHit);

            // The Ellipse's own Cursor=Hand only fires over the visible 10x10 dot,
            // but the grab region extends ThumbHitPadding past every edge - update the canvas cursor
            // so the hand glyph appears throughout the entire grabbable halo, matching what the hit-test will accept.
            // Pins use the resize-horizontal glyph so the user can tell at a glance the drag is X-only.
            System.Windows.Input.Cursor wanted = overPin
                ? Cursors.SizeWE
                : overThumb ? Cursors.Hand : Cursors.Arrow;
            if (PlotCanvas.Cursor != wanted) PlotCanvas.Cursor = wanted;

            bool needsRedraw = false;

            (Series Series, EnvironmentalCurvePoint Point)? nextThumb = overThumb ? thumbHit : null;
            if (!Equals(nextThumb, _hoveredThumb))
            {
                _hoveredThumb = nextThumb;
                needsRedraw = true;
            }

            if (!Equals(nextPinHover, _hoveredDisabledPin))
            {
                _hoveredDisabledPin = nextPinHover;
                needsRedraw = true;
            }

            // Hover highlighting for clamp lines. Skipped when the cursor is over a thumb halo
            // so the user gets a clean "click goes to the node" affordance instead of a confusing dual highlight.
            if (_offsetMode)
            {
                (Series Series, LimitKind Kind)? next
                    = !overThumb && !overPin && TryHitLimitLine(mouse, out (Series series, LimitKind kind) hov)
                        ? hov
                        : null;
                if (!Equals(next, _hoveredLimit))
                {
                    _hoveredLimit = next;
                    needsRedraw = true;
                }
            }

            if (needsRedraw) Redraw();
        }

        if (_dragDisabledPin is { } draggedPin)
        {
            if (w <= 0) return;

            // Pin drag is X-only: Y is fixed in the top inset.
            // Clamping to [0, 1] keeps the pin inside the chart;
            // Start crossing past End (or vice versa) is explicitly allowed
            // because that's how the user expresses an inverted / wrap-midnight selection.
            double t = Math.Clamp(FromScreenX(mouse.X, w), 0.0, 1.0);
            if (draggedPin == DisabledPin.Start)
                _disabledPeriodStart = t;
            else
                _disabledPeriodEnd = t;
            Redraw();
            return;
        }

        if (_draggingLimit && _curveData != null)
        {
            if (h <= 0) return;

            // Clamp against the *other* limit so min and max can't pass through each other.
            // A small epsilon keeps them at least one pixel apart at any reasonable canvas height
            // - prevents a hit-test ambiguity where two coincident lines would steal each other's clicks.
            double v = Math.Clamp(FromScreenY(mouse.Y, h), 0.0, 100.0);
            const double eps = 0.5;
            switch (_limitDragSeries, _limitDragKind)
            {
                case (Series.Brightness, LimitKind.Min):
                    _curveData.BrightnessOffsetMin = Math.Min(v, _curveData.BrightnessOffsetMax - eps);
                    break;
                case (Series.Brightness, LimitKind.Max):
                    _curveData.BrightnessOffsetMax = Math.Max(v, _curveData.BrightnessOffsetMin + eps);
                    break;
                case (Series.NightLight, LimitKind.Min):
                    _curveData.NightLightOffsetMin = Math.Min(v, _curveData.NightLightOffsetMax - eps);
                    break;
                case (Series.NightLight, LimitKind.Max):
                    _curveData.NightLightOffsetMax = Math.Max(v, _curveData.NightLightOffsetMin + eps);
                    break;
            }

            Redraw();
            // Fire mid-drag too so the runtime brightness tracks the limit-line drag in real-time.
            // The host handler is throttle-safe: it restarts a debounced save and pings the flyout's
            // per-BrightnessUpdateRateMs throttle, so a 60Hz drag collapses to one DDC write per slider rate.
            CurveChanged?.Invoke();
            return;
        }

        if (_dragPoint is null) return;

        if (w <= 0 || h <= 0) return;

        _dragPoint.Value = Math.Clamp(FromScreenY(mouse.Y, h), 0.0, 100.0);

        // Edge nodes (current min-Time / max-Time) anchor the curve to the boundaries; only their value moves.
        // Interior nodes can also slide along the time axis but never past their neighbours,
        // so the polyline can't cross itself.
        List<EnvironmentalCurvePoint> series = GetSeries(_dragSeries);
        SyncEdgeYIfEdge(_dragPoint, series);
        if (!IsEndpoint(_dragPoint, series))
        {
            double t = Math.Clamp(FromScreenX(mouse.X, w), 0.0, 1.0);
            List<EnvironmentalCurvePoint> ordered = [.. series.OrderBy(p => p.Time)];
            int idx = ordered.IndexOf(_dragPoint);
            if (idx > 0) t = Math.Max(t, ordered[idx - 1].Time + 0.001);

            if (idx < ordered.Count - 1) t = Math.Min(t, ordered[idx + 1].Time - 0.001);

            _dragPoint.Time = t;
        }

        Redraw();
        // Live notification on every drag move so the runtime apply path tracks the curve edit in real-time
        // instead of waiting for mouse-up.
        // The host handler debounces the disk save and the flyout throttles the actual evaluation,
        // so 60Hz mouse-move events collapse to one save per idle interval and one DDC write per slider rate.
        CurveChanged?.Invoke();
    }

    private void PlotCanvas_MouseEnter(object sender, MouseEventArgs e)
    {
        // The first MouseMove will populate _cursorPos; this handler exists so the overlay can be primed
        // once the cursor crosses into the canvas, even before the user moves the mouse further
        // (e.g. they tab back into the window with the pointer parked).
        _cursorPos = e.GetPosition(PlotCanvas);
        UpdateCursorOverlay();
    }

    private void PlotCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        // Clear hover highlights when the cursor leaves the plot area.
        // Only fires when the mouse isn't captured (drags don't generate MouseLeave),
        // so an in-progress drag keeps its highlight even if the cursor wanders out.
        bool needsRedraw = false;
        if (_hoveredLimit is not null)
        {
            _hoveredLimit = null;
            needsRedraw = true;
        }

        if (_hoveredThumb is not null)
        {
            _hoveredThumb = null;
            needsRedraw = true;
        }

        if (_hoveredDisabledPin is not null)
        {
            _hoveredDisabledPin = null;
            needsRedraw = true;
        }

        if (needsRedraw) Redraw();

        _cursorPos = null;
        UpdateCursorOverlay();
    }

    /// <summary>
    /// Repositions every persistent overlay element based on the current cursor pos and editor mode.
    /// Called from MouseMove / MouseEnter / MouseLeave plus after a Redraw
    /// (the curves may have shifted under the cursor without the cursor moving).
    /// Designed to be cheap so MouseMove can call it on every event without burning frames.
    /// </summary>
    private void UpdateCursorOverlay()
    {
        UpdateCursorOverlayCore();
        // Run after the cursor pill's position/visibility is settled so the node pill can stack underneath it
        // without using a one-frame-stale position.
        UpdateNodeReadout();
    }

    private void UpdateCursorOverlayCore()
    {
        if (_cursorReadoutText is null
            || _cursorReadoutBackground is null
            || _cursorScrubberLine is null
            || _brightnessCursorMarker is null
            || _brightnessCursorLabel is null
            || _nightLightCursorMarker is null
            || _nightLightCursorLabel is null)
            return;

        double w = PlotCanvas.ActualWidth;
        double h = PlotCanvas.ActualHeight;
        if (_cursorPos is not { } pos || w <= 0 || h <= 0)
        {
            HideCursorOverlay();
            return;
        }

        double t = Math.Clamp(FromScreenX(pos.X, w), 0.0, 1.0);
        double v = Math.Clamp(FromScreenY(pos.Y, h), 0.0, 100.0);

        // Top-right time / value pill - always visible while the cursor is over the canvas.
        bool use24 = SystemUses24HourClock();
        _cursorReadoutText.Text = $"{FormatCursorTime(t, use24)}  {FormatCursorValue(v)}";
        _cursorReadoutText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _cursorReadoutBackground.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double readoutW = _cursorReadoutBackground.DesiredSize.Width;
        // Default placement is top-right;
        // if the cursor wanders into the corner the readout would otherwise sit underneath,
        // flip it to the top-left so the pill never covers the position it's reporting on.
        const double cursorAvoidTop = 24.0;
        const double cursorAvoidRight = 100.0;
        bool nearTopRight = pos.Y < TopInset + cursorAvoidTop
            && pos.X > w - PlotInsetX - cursorAvoidRight;
        double readoutX = nearTopRight
            ? PlotInsetX
            : w - PlotInsetX - readoutW;
        Canvas.SetLeft(_cursorReadoutBackground, readoutX);
        Canvas.SetTop(_cursorReadoutBackground, TopInset);
        _cursorReadoutBackground.Visibility = Visibility.Visible;


        // Per-curve scrubber + markers only show under the toggle.
        // Skipping the work when it's off saves the per-curve sample evaluations during routine mouse movement.
        if (!_showCursorReadout)
        {
            _cursorScrubberLine.Visibility = Visibility.Collapsed;
            _brightnessCursorMarker.Visibility = Visibility.Collapsed;
            _brightnessCursorLabel.Visibility = Visibility.Collapsed;
            _nightLightCursorMarker.Visibility = Visibility.Collapsed;
            _nightLightCursorLabel.Visibility = Visibility.Collapsed;
            return;
        }

        double cursorX = ScreenX(t, w);
        _cursorScrubberLine.X1 = cursorX;
        _cursorScrubberLine.X2 = cursorX;
        _cursorScrubberLine.Y1 = TopInset;
        _cursorScrubberLine.Y2 = h - PlotInsetYBase;
        _cursorScrubberLine.Visibility = Visibility.Visible;

        UpdateCurveCursorMarker(_brightness, _showBrightness, t, w, h, _brightnessCursorMarker, _brightnessCursorLabel);
        UpdateCurveCursorMarker(_nightLight, _showNightLight, t, w, h, _nightLightCursorMarker, _nightLightCursorLabel);
    }

    /// <summary>
    /// Repositions the selected-node readout pill. Shows when a node is selected, hides otherwise.
    /// Sits directly below the cursor readout pill (or in its corner when the cursor isn't over the canvas),
    /// with foreground set to the curve's brush so the user can pick out which series the selected node belongs to
    /// without inferring it from the readout's screen position.
    /// </summary>
    private void UpdateNodeReadout()
    {
        if (_nodeReadoutText is null || _nodeReadoutBackground is null) return;

        if (_selectedPoint is null)
        {
            _nodeReadoutBackground.Visibility = Visibility.Collapsed;
            return;
        }

        double w = PlotCanvas.ActualWidth;
        if (w <= 0)
        {
            _nodeReadoutBackground.Visibility = Visibility.Collapsed;
            return;
        }

        bool use24 = SystemUses24HourClock();
        _nodeReadoutText.Text = string.Format(
            LocalizationManager.Instance["Settings_CurveEditor_NodeReadout_Format"],
            FormatCursorTime(_selectedPoint.Time, use24),
            FormatCursorValue(_selectedPoint.Value));
        _nodeReadoutText.Foreground = _selectedSeries == Series.Brightness
            ? GetBrightnessBrush()
            : GetNightLightBrush();

        _nodeReadoutBackground.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double readoutW = _nodeReadoutBackground.DesiredSize.Width;
        // double readoutH = _nodeReadoutBackground.DesiredSize.Height;

        // Default placement: stacked under the cursor readout in the top-right corner.
        // When the cursor readout is visible we mirror its X
        // (so the two pills line up even after the cursor flips it to top-left);
        // when it's hidden we anchor at the top-right ourselves so the node pill still has a stable home.
        double pillTop;
        double pillLeft;
        if (_cursorReadoutBackground is { Visibility: Visibility.Visible } cursorPill)
        {
            double cursorTop = Canvas.GetTop(cursorPill);
            double cursorLeft = Canvas.GetLeft(cursorPill);
            pillTop = cursorTop + cursorPill.DesiredSize.Height + 2;
            // Re-derive left so a wider node pill doesn't overflow the right inset
            // when the cursor pill is anchored at the right
            // - keep it on the same side, just slid in if needed.
            bool leftAnchored = cursorLeft < (w / 2.0);
            pillLeft = leftAnchored
                ? cursorLeft
                : Math.Max(PlotInsetX, w - PlotInsetX - readoutW);
        }
        else
        {
            pillTop = TopInset;
            pillLeft = w - PlotInsetX - readoutW;
        }

        Canvas.SetLeft(_nodeReadoutBackground, pillLeft);
        Canvas.SetTop(_nodeReadoutBackground, pillTop);
        _nodeReadoutBackground.Visibility = Visibility.Visible;
    }

    private void UpdateCurveCursorMarker(
        List<EnvironmentalCurvePoint> series,
        bool seriesVisible,
        double t,
        double w,
        double h,
        Ellipse marker,
        TextBlock label)
    {
        if (!seriesVisible)
        {
            marker.Visibility = Visibility.Collapsed;
            label.Visibility = Visibility.Collapsed;
            return;
        }

        double sample = SampleCurveAt(series, t);
        if (double.IsNaN(sample))
        {
            marker.Visibility = Visibility.Collapsed;
            label.Visibility = Visibility.Collapsed;
            return;
        }

        double markerX = ScreenX(t, w);
        double markerY = ScreenY(sample, h);
        Canvas.SetLeft(marker, markerX - marker.Width / 2);
        Canvas.SetTop(marker, markerY - marker.Height / 2);
        marker.Visibility = Visibility.Visible;

        label.Text = FormatCursorValue(sample);
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Place the label on the OUTSIDE of the curve's local slope so it sits in the empty quadrant
        // away from the curve's trajectory rather than overlapping the line.
        // Concretely: sample the curve a small dt on each side of t to estimate slope direction.
        //   - rising slope (dy/dt < 0 in screen space, since +Y is down) -> the curve is heading up-and-right;
        //     the empty quadrant is upper-left, so place label there.
        //   - falling slope (dy/dt > 0 in screen space) -> curve heading down-and-right;
        //     empty quadrant is upper-right, place label there.
        //   - near-flat -> default to upper-right.
        // Boundary rules win over the slope preference: clamp X into [PlotInsetX, w - PlotInsetX - width]
        // and Y into [TopInset, h - PlotInsetYBase - height], and if the preferred Y would clip we flip
        // the label below the marker so an extreme curve value stays legible.
        const double labelGap = 6.0;
        const double slopeProbe = 0.01;
        double labelW = label.DesiredSize.Width;
        double labelH = label.DesiredSize.Height;
        double left = SampleCurveAt(series, Math.Max(0.0, t - slopeProbe));
        double right = SampleCurveAt(series, Math.Min(1.0, t + slopeProbe));
        // Slope sign in DATA space (Y is 0..100 with 100 at top of curve);
        // edges of the curve fall back to a neutral 0 so the default upper-right placement applies.
        double slope = (double.IsNaN(left) || double.IsNaN(right)) ? 0.0 : (right - left);

        double preferredX = slope > 0
            ? markerX - labelGap - labelW
            : markerX + labelGap;
        double labelX = Math.Clamp(preferredX, PlotInsetX, Math.Max(PlotInsetX, w - PlotInsetX - labelW));
        double labelY = markerY - labelH - 2;
        // Flip the label below the marker if placing it above would clip past the top inset
        // (curve sample is near the top of the canvas).
        if (labelY < TopInset) labelY = markerY + marker.Height / 2 + 2;
        // And clamp the bottom edge so a near-floor sample (curve near 0) doesn't punch through the
        // bottom inset either.
        labelY = Math.Clamp(labelY, TopInset, Math.Max(TopInset, h - PlotInsetYBase - labelH));
        Canvas.SetLeft(label, labelX);
        Canvas.SetTop(label, labelY);
        label.Visibility = Visibility.Visible;
    }

    private void HideCursorOverlay()
    {
        if (_cursorReadoutBackground is not null) _cursorReadoutBackground.Visibility = Visibility.Collapsed;

        if (_cursorScrubberLine is not null) _cursorScrubberLine.Visibility = Visibility.Collapsed;

        if (_brightnessCursorMarker is not null) _brightnessCursorMarker.Visibility = Visibility.Collapsed;

        if (_brightnessCursorLabel is not null) _brightnessCursorLabel.Visibility = Visibility.Collapsed;

        if (_nightLightCursorMarker is not null) _nightLightCursorMarker.Visibility = Visibility.Collapsed;

        if (_nightLightCursorLabel is not null) _nightLightCursorLabel.Visibility = Visibility.Collapsed;
    }

    private static string FormatCursorTime(double t, bool use24Hour)
    {
        // Snap to the nearest minute - sub-minute precision isn't useful here and the resulting jitter on the readout
        // would be noisy.
        int totalMinutes = (int)Math.Round(t * 24 * 60);
        totalMinutes = Math.Clamp(totalMinutes, 0, 24 * 60);
        // 24:00 collapses to 12:00am / 00:00 so the wrap reads as the start of the next day.
        if (totalMinutes == 24 * 60) totalMinutes = 0;
        int hour = totalMinutes / 60;
        int minute = totalMinutes % 60;

        if (use24Hour) return $"{hour:D2}:{minute:D2}";

        (int displayHour, string suffix) = hour switch
        {
            0     => (12, "am"),
            < 12  => (hour, "am"),
            12    => (12, "pm"),
            _     => (hour - 12, "pm"),
        };

        return $"{displayHour}:{minute:D2}{suffix}";
    }

    private string FormatCursorValue(double v)
    {
        if (_offsetMode)
        {
            // Offset mode runs -100..+100 with v=50 the neutral midline.
            // The 200-unit display range maps onto a 100-unit storage range, so multiply the centred deviation
            // by 2 to match the Y axis labels (and the runtime mapping in EnvironmentalCurveService).
            // Preserve the sign explicitly so a "+0" reads as no-offset rather than a missing value.
            int offset = (int)Math.Round((v - 50.0) * 2.0);
            return offset > 0 ? $"+{offset}" : offset.ToString();
        }

        int pct = (int)Math.Round(v);
        return pct.ToString();
    }

    private void PlotCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingLimit)
        {
            _draggingLimit = false;
            PlotCanvas.ReleaseMouseCapture();
            CurveChanged?.Invoke();
            return;
        }

        if (_dragDisabledPin is not null)
        {
            _dragDisabledPin = null;
            PlotCanvas.ReleaseMouseCapture();
            // Notify the host of the new (start, end) so it can sync the time boxes and persist.
            // Mid-drag updates would push the user-typed text out from under them,
            // so the live signal fires only on release.
            DisabledPeriodChanged?.Invoke(_disabledPeriodStart, _disabledPeriodEnd);
            return;
        }

        if (_dragPoint is null) return;

        _dragPoint = null;
        PlotCanvas.ReleaseMouseCapture();
        CurveChanged?.Invoke();
    }

    private bool TryHitThumb(Point mouse, out (Series series, EnvironmentalCurvePoint point) hit)
    {
        // Walk children in reverse so the topmost thumb wins when curves overlap.
        // The visible thumb is ThumbSize square but the hit region is inflated by ThumbHitPadding on each side,
        // giving twice the grabbable area without changing the rendered dot.
        for (int i = PlotCanvas.Children.Count - 1; i >= 0; i--)
        {
            if (PlotCanvas.Children[i] is Ellipse { Tag: ValueTuple<Series, EnvironmentalCurvePoint> tag } thumb)
            {
                double left = Canvas.GetLeft(thumb);
                double top = Canvas.GetTop(thumb);
                if (mouse.X >= left - ThumbHitPadding && mouse.X <= left + thumb.Width + ThumbHitPadding &&
                    mouse.Y >= top - ThumbHitPadding && mouse.Y <= top + thumb.Height + ThumbHitPadding)
                {
                    hit = tag;
                    return true;
                }
            }
        }
        hit = default;
        return false;
    }

    private bool TryHitLimitLine(Point mouse, out (Series series, LimitKind kind) hit)
    {
        // Picks the visually-closest limit line within ~5 pixels vertically.
        // Iterating in reverse matches the thumb hit-testing convention
        // so the topmost line wins when two sit at the same Y
        // - which can happen when a series' min and max have just been dragged together.
        // Limit-line captions tagged with the same (series, kind) extend the hit area:
        // a direct click inside a caption's bounding box short-circuits the line search
        // and resolves to that caption's clamp, which is the affordance the user is aiming at
        // when they click on text instead of dashes.
        double best = double.PositiveInfinity;
        (Series series, LimitKind kind) bestHit = default;
        for (int i = PlotCanvas.Children.Count - 1; i >= 0; i--)
        {
            UIElement child = PlotCanvas.Children[i];

            switch (child)
            {
                case TextBlock { Tag: ValueTuple<Series, LimitKind> labelTag } label:
                {
                    double left = Canvas.GetLeft(label);
                    double top = Canvas.GetTop(label);
                    if (mouse.X >= left && mouse.X <= left + label.DesiredSize.Width
                                        && mouse.Y >= top && mouse.Y <= top + label.DesiredSize.Height)
                    {
                        hit = labelTag;
                        return true;
                    }
                    continue;
                }
                case Line { Tag: ValueTuple<Series, LimitKind> lineTag } line:
                {
                    double dy = Math.Abs(mouse.Y - line.Y1);
                    if (dy <= LimitLineHitTolerance && dy < best)
                    {
                        best = dy;
                        bestHit = lineTag;
                    }

                    break;
                }
            }
        }

        if (double.IsInfinity(best))
        {
            hit = default;
            return false;
        }

        hit = bestHit;
        return true;
    }

    private List<EnvironmentalCurvePoint> GetSeries(Series series) =>
        series == Series.Brightness ? _brightness : _nightLight;

    private static bool AddPoint(List<EnvironmentalCurvePoint> series, double t, double v)
    {
        // Snap-add: if the user clicks within ~5 minutes of an existing point's time
        // we just move that point instead of accumulating a stack of near-duplicates.
        const double snapWindow = 5.0 / (24.0 * 60.0);
        EnvironmentalCurvePoint? near = series.FirstOrDefault(p => Math.Abs(p.Time - t) < snapWindow);
        if (near != null)
        {
            near.Value = v;
            SyncEdgeYIfEdge(near, series);
            return true;
        }

        series.Add(new EnvironmentalCurvePoint { Time = t, Value = v });
        return true;
    }

    // Curve colors live at the Application scope. App.xaml.cs/UpdateThemeResources writes them in at
    // startup and again whenever the user changes a theme picker, so a direct lookup on Application.Resources
    // is always current. At design time the VS designer's stub Application doesn't have our keys, so the
    // brush comes back null and the canvases render unstyled - acceptable since CurveEditor's curves are
    // drawn from code, not XAML, and the designer doesn't reliably run Loaded handlers anyway.
    private static Brush GetBrightnessBrush() => GetThemedBrush("EnvironmentalBrightnessCurveBrush");
    private static Brush GetNightLightBrush() => GetThemedBrush("EnvironmentalNightLightCurveBrush");
    private static Brush GetCurrentTimeBrush() => GetThemedBrush("EnvironmentalCurrentTimeBrush");
    private static Brush GetTwilightBackdropBrush() => GetThemedBrush("EnvironmentalTwilightBackdropBrush");
    private static Brush GetNightBackdropBrush() => GetThemedBrush("EnvironmentalNightBackdropBrush");

    private static Brush GetThemedBrush(string key) =>
        System.Windows.Application.Current?.Resources[key] as Brush ?? Brushes.Transparent;

    /// <summary>
    /// Builds the tab-cycle ordering of nodes: brightness curve left-to-right, then night light curve left-to-right,
    /// with hidden curves omitted entirely.
    /// The list mirrors what the user sees, so a hidden curve doesn't trap selection on an invisible node.
    /// </summary>
    private List<(Series series, EnvironmentalCurvePoint point)> GetNavigableNodes()
    {
        List<(Series, EnvironmentalCurvePoint)> nodes = [];
        if (_showBrightness)
            foreach (EnvironmentalCurvePoint p in _brightness.OrderBy(p => p.Time)) nodes.Add((Series.Brightness, p));

        if (_showNightLight)
            foreach (EnvironmentalCurvePoint p in _nightLight.OrderBy(p => p.Time)) nodes.Add((Series.NightLight, p));

        return nodes;
    }

    /// <summary>
    /// Cyclic Tab navigation: walks <paramref name="direction"/> (+1 / -1) through every visible-curve node
    /// and wraps around at either end.
    /// The user is expected to back out of the editor with Escape - Tab itself never escapes the control.
    /// </summary>
    private void NavigateSelection(int direction)
    {
        List<(Series series, EnvironmentalCurvePoint point)> nodes = GetNavigableNodes();
        if (nodes.Count == 0) return;

        int currentIndex = -1;
        if (_selectedPoint is { } sel)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].series == _selectedSeries && ReferenceEquals(nodes[i].point, sel))
                {
                    currentIndex = i;
                    break;
                }
            }
        }

        int nextIndex;
        if (currentIndex < 0)
            nextIndex = direction >= 0 ? 0 : nodes.Count - 1;
        else
        {
            int n = nodes.Count;
            nextIndex = ((currentIndex + direction) % n + n) % n;
        }

        _selectedSeries = nodes[nextIndex].series;
        _selectedPoint = nodes[nextIndex].point;
        Redraw();
    }

    /// <summary>
    /// Arrow-key edits to the currently selected node. <paramref name="dx"/> shifts the node's normalised time,
    /// <paramref name="dy"/> its value.
    /// Edge nodes can only move vertically (their Time is pinned to 0 / 1 by the wrap-around invariant);
    /// interior nodes are clamped against their immediate neighbours so the polyline can't cross itself.
    /// </summary>
    private void AdjustSelected(double dx, double dy)
    {
        if (_selectedPoint is null) return;

        List<EnvironmentalCurvePoint> series = GetSeries(_selectedSeries);

        if (dy != 0.0)
        {
            _selectedPoint.Value = Math.Clamp(_selectedPoint.Value + dy, 0.0, 100.0);
            SyncEdgeYIfEdge(_selectedPoint, series);
        }

        if (dx != 0.0 && !IsEndpoint(_selectedPoint, series))
        {
            List<EnvironmentalCurvePoint> ordered = [.. series.OrderBy(p => p.Time)];
            int idx = ordered.IndexOf(_selectedPoint);
            double newT = Math.Clamp(_selectedPoint.Time + dx, 0.0, 1.0);
            if (idx > 0) newT = Math.Max(newT, ordered[idx - 1].Time + 0.001);

            if (idx < ordered.Count - 1) newT = Math.Min(newT, ordered[idx + 1].Time - 0.001);

            _selectedPoint.Time = newT;
        }

        Redraw();
        CurveChanged?.Invoke();
    }

    /// <summary>
    /// Spacebar handler: spawns a new node 4 points (0.04 in normalised time) inward from the selected one
    /// - left when the cursor is past the midline, right when at or before it.
    /// The new node inherits the selected node's value (so the inserted node sits on the same horizontal as its anchor)
    /// and becomes the new selection.
    /// </summary>
    private void InsertNodeNearSelected()
    {
        if (_selectedPoint is null) return;

        List<EnvironmentalCurvePoint> series = GetSeries(_selectedSeries);
        double offset = _selectedPoint.Time > 0.5 ? -KeyboardSpacebarOffset : KeyboardSpacebarOffset;
        double newT = Math.Clamp(_selectedPoint.Time + offset, 0.0, 1.0);
        double newV = _selectedPoint.Value;

        if (AddPoint(series, newT, newV))
        {
            _selectedPoint = FindNearestByTime(series, newT);
            Redraw();
            CurveChanged?.Invoke();
        }
    }

    /// <summary>
    /// Delete / backspace handler: removes the currently selected interior node.
    /// Endpoint nodes (t=0 / t=1) anchor the cyclic curve and are not deletable
    /// - matches the right-click delete invariant.
    /// Selection promotes to the nearest surviving neighbour so a follow-up keystroke still has a target.
    /// </summary>
    private void DeleteSelected()
    {
        if (_selectedPoint is null) return;

        List<EnvironmentalCurvePoint> series = GetSeries(_selectedSeries);
        if (IsEndpoint(_selectedPoint, series)) return;

        double removedTime = _selectedPoint.Time;
        series.Remove(_selectedPoint);
        _selectedPoint = PickNeighbourAfterRemoval(series, removedTime);
        Redraw();
        CurveChanged?.Invoke();
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Preview mode locks editing - the same lockout the mouse handlers honour,
        // so keyboard edits can't sneak past it.
        if (_previewMode) return;

        // Don't fight a live drag. The mouse handler is mutating the same node;
        // keystrokes would race with the drag's coordinate updates and produce unpredictable output.
        if (_dragPoint is not null || _draggingLimit) return;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        // Step magnitudes layered by modifier:
        // Shift = sub-point precision (1 minute / 1 displayed-Y-unit), Ctrl = coarse 6-point sweep, plain = 1 point.
        // Shift wins when both modifiers are held so the user can always reach the finest step.
        // Tab handles Shift separately (reverse direction), so this calc is irrelevant for that branch.
        double xStep;
        double yStep;
        if (shift)
        {
            xStep = KeyboardStepOneMinute;
            yStep = _offsetMode ? KeyboardStepOneYUnitOffset : KeyboardStepOneYUnitAbsolute;
        }
        else if (ctrl)
        {
            xStep = KeyboardStepCoarse;
            yStep = KeyboardStepCoarse;
        }
        else
        {
            xStep = KeyboardStepFine;
            yStep = KeyboardStepFine;
        }

        switch (e.Key)
        {
            case Key.Tab:
                NavigateSelection(shift ? -1 : 1);
                e.Handled = true;
                break;
            case Key.Up:
                AdjustSelected(0.0, yStep);
                e.Handled = true;
                break;
            case Key.Down:
                AdjustSelected(0.0, -yStep);
                e.Handled = true;
                break;
            case Key.Left:
                AdjustSelected(-xStep, 0.0);
                e.Handled = true;
                break;
            case Key.Right:
                AdjustSelected(xStep, 0.0);
                e.Handled = true;
                break;
            case Key.Space:
                InsertNodeNearSelected();
                e.Handled = true;
                break;
            case Key.Delete:
            case Key.Back:
                DeleteSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                // "Back out" of the editor: clear the selection ring and yield focus to the next focusable element
                // so the user can resume normal Tab navigation through the surrounding settings page.
                _selectedPoint = null;
                Redraw();
                MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                e.Handled = true;
                break;
        }
    }

    private void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Tab-into from outside lands on the first visible-curve node
        // so the user has an immediate target for arrow keys.
        // A click on a thumb arrives here too, but the mouse handler has already set _selectedPoint by then,
        // so this branch no-ops.
        if (_selectedPoint != null) return;

        List<(Series series, EnvironmentalCurvePoint point)> nodes = GetNavigableNodes();
        if (nodes.Count == 0) return;

        _selectedSeries = nodes[0].series;
        _selectedPoint = nodes[0].point;
        Redraw();
    }

    private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Drop the selection ring when focus leaves the editor
        // so an inactive editor doesn't display a phantom highlight on a node the user can no longer act on.
        if (_selectedPoint is null) return;

        _selectedPoint = null;
        Redraw();
    }
}
