using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MasterArbete;

/// <summary>
/// Draws a waveform (amplitude over time) with a time axis in seconds.
///
/// A file usually has far more samples than the control has pixels
/// (e.g. 365,000 samples across 1,000 pixels), so each pixel column shows
/// the minimum and maximum of the samples that fall in it, drawn as a
/// vertical line. When there are fewer samples than pixels, the samples
/// are joined with a line instead.
///
/// It also has a cursor and a selection, for picking times by eye:
///   - click: place the cursor;
///   - drag: select a stretch (dragging an edge of the selection moves that edge);
///   - ← / →: move the cursor 1 ms, with Shift 10 ms, with Ctrl one sample;
///     Home / End: to the start / end;
///   - F / T: set the selection's start (From) / end (To) at the cursor.
/// The keys work once the waveform has keyboard focus (after a click), shown
/// by a frame around it.
/// </summary>
/// Creating own WPF visual element called WavformView
/// FrameworkElement is a WPF base class. By inheriting it gets, ActualWidth , ActualHeight, InvalidateVisual(), OnRender().
public class WaveformView : FrameworkElement
{
    private const double AxisHeight = 24;    // space reserved at the bottom for the time axis. Reserve 24 pixels for the time axis.
    private const double MinTickSpacing = 80; // aim for at least this many pixels between time labels, so the lables don`t become overcrowded.
    private const double YAxisWidth = 52;     // space reserved on the left for the y-axis labels
    private const double MinYTickSpacing = 36; // aim for at least this many pixels between y-axis labels

    private static readonly Brush BackgroundBrush = Brushes.White; //these define the colors and thicknesses used when drawing
    private static readonly Pen WavePen = MakePen(Color.FromRgb(0x1F, 0x5F, 0xAF), 1);
    private static readonly Pen ZeroLinePen = MakePen(Color.FromRgb(0xCC, 0xCC, 0xCC), 1);
    private static readonly Pen AxisPen = MakePen(Color.FromRgb(0x66, 0x66, 0x66), 1);
    private static readonly Brush LabelBrush = MakeBrush(Color.FromRgb(0x44, 0x44, 0x44));
    private static readonly Pen CursorPen = MakePen(Color.FromRgb(0xD0, 0x30, 0x30), 1);
    private static readonly Brush SelectionBrush = MakeBrush(Color.FromArgb(0x30, 0xF0, 0xA0, 0x00));
    private static readonly Pen SelectionEdgePen = MakePen(Color.FromRgb(0xE0, 0x80, 0x00), 1);
    private static readonly Pen FocusPen = MakePen(Color.FromRgb(0x1F, 0x5F, 0xAF), 2);

    private const double EdgeGrabPixels = 4;    // how close to a selection edge a press grabs that edge
    private const double DragThresholdPixels = 3;   // a press that moves less than this is a click, not a drag

    private float[] _samples = Array.Empty<float>(); //where the actual audio samples stored: _samples contain the amplitudes.
    private int _sampleRate; //_sampleRate tells you ow many samples represent one second, for example _sampleRate=44100

    // Vertical scale: the sample values at the bottom and top of the plot,
    // and the numbers the y-axis shows for them.
    private double _sampleMin = -1, _sampleMax = 1;
    private double _axisMin = -1, _axisMax = 1;
    private string? _axisUnit;   // e.g. "Pa" for a calibrated recording

    // Cursor and selection, as sample indexes. The cursor is on a sample
    // (0..length−1); the selection's edges are between samples (0..length),
    // and either can be missing.
    private int? _cursor;
    private int? _selectionStart, _selectionEnd;

    // Mouse dragging: a press first counts as a click, and becomes a drag
    // (a new selection, or moving one edge) once it has moved a few pixels.
    private enum DragMode { None, Click, Selecting }
    private DragMode _drag;
    private int _dragAnchor;      // the selection edge that stays put while dragging
    private double _pressX;

    public WaveformView()
    {
        Focusable = true;
        FocusVisualStyle = null;   // the focus frame is drawn in OnRender instead
    }

    /// <summary>The cursor moved (click, drag or arrow keys).</summary>
    public event EventHandler? CursorMoved;

    /// <summary>
    /// The user changed the selection (drag, or F / T). Not raised by
    /// <see cref="SetSelection"/>, so the From/To boxes and the waveform can
    /// update each other without going round in circles.
    /// </summary>
    public event EventHandler? SelectionEdited;

    public double? CursorSeconds => _cursor / (double?)_sampleRate;

    /// <summary>The value at the cursor in the y-axis's units (see <see cref="AxisUnit"/>), as Swell's Visa → Mät shows it.</summary>
    public double? CursorAmplitude => _cursor is int i ? ToAxisValue(_samples[i]) : null;

    /// <summary>The y-axis's unit, e.g. "Pa", or null for plain sample values (−1..1).</summary>
    public string? AxisUnit => _axisUnit;

    public double? SelectionStartSeconds => _selectionStart / (double?)_sampleRate;
    public double? SelectionEndSeconds => _selectionEnd / (double?)_sampleRate;

    /// <summary>Shows a selection, e.g. from typed From/To times. Either may be null. Doesn't raise <see cref="SelectionEdited"/>.</summary>
    public void SetSelection(double? fromSeconds, double? toSeconds)
    {
        int? ToIndex(double? seconds) => seconds is double s && _sampleRate > 0
            ? (int)Math.Clamp(Math.Round(s * _sampleRate), 0, _samples.Length)
            : null;
        (_selectionStart, _selectionEnd) = (ToIndex(fromSeconds), ToIndex(toSeconds));
        InvalidateVisual();
    }

    /// <summary>
    /// Shows these samples (values in -1..1) recorded at the given rate.
    /// sampleRange is the sample values at the bottom and top of the plot
    /// (full scale, -1..1, if left out). axisValues is the numbers the y-axis
    /// shows for those two (the sample values themselves if left out), and
    /// axisUnit the unit they are in.
    /// </summary>
    /// This gices the waveform component new audio to display.
    public void SetSamples(float[] samples, int sampleRate,
        (double Min, double Max)? sampleRange = null, (double Min, double Max)? axisValues = null,
        string? axisUnit = null)
    {
        _samples = samples;
        _sampleRate = sampleRate;

        // A range that is empty or upside down can't be drawn, so fall back.
        (_sampleMin, _sampleMax) = sampleRange is { } r && r.Max > r.Min ? r : (-1, 1);
        bool validAxis = axisValues is { } a && a.Max > a.Min;
        (_axisMin, _axisMax) = validAxis ? axisValues!.Value : (_sampleMin, _sampleMax);
        _axisUnit = validAxis ? axisUnit : null;   // the unit only applies to the values it came with

        // A new recording: the old cursor and selection don't apply to it.
        _cursor = _selectionStart = _selectionEnd = null;
        _drag = DragMode.None;
        InvalidateVisual();
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    public void Clear() => SetSamples(Array.Empty<float>(), 0);

    // WPF calls this whenever the control needs repainting, including after a resize.
    //OnRender is the main drawing function.
    //dc means DrawingContext, use it to raw things, like dc.DrawLine(..)
    protected override void OnRender(DrawingContext dc)
    {

        double width = ActualWidth - YAxisWidth;   // the plot starts to the right of the y-axis labels
        double waveHeight = ActualHeight - AxisHeight;
        //draws the white background
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (width < 1 || waveHeight < 1) return;

        bool hasAudio = _samples.Length > 0 && _sampleRate > 0;
        if (hasAudio) DrawYAxis(dc, waveHeight);

        // Everything below is drawn with x = 0 at the left edge of the plot.
        dc.PushTransform(new TranslateTransform(YAxisWidth, 0));

        // Zero line (zero aplitude line), if zero is inside the range shown.
        if (_sampleMin <= 0 && _sampleMax >= 0)
        {
            double zeroY = Math.Round(ToY(0, waveHeight)) + 0.5;   // +0.5 keeps 1px lines sharp
            dc.DrawLine(ZeroLinePen, new Point(0, zeroY), new Point(width, zeroY));
        }

        if (hasAudio)
        {
            DrawSelection(dc, width, waveHeight);
            DrawWaveform(dc, width, waveHeight);
            DrawCursor(dc, width, waveHeight);
            DrawTimeAxis(dc, width, waveHeight);
        }
        else //if there is no audio.
        {
            DrawText(dc, "No waveform", new Point(8, 8), TextAlignment.Left);
        }

        dc.Pop();

        if (IsKeyboardFocused)
            dc.DrawRectangle(null, FocusPen, new Rect(1, 1, Math.Max(ActualWidth - 2, 0), Math.Max(ActualHeight - 2, 0)));
    }

    // The x of a sample index, 0 at the left edge of the plot (same scale as the time axis).
    private double ToX(int index, double width) => (double)index / _samples.Length * width;

    // Sample value converted into the y-axis's units: a straight line from
    // (_sampleMin, _axisMin) to (_sampleMax, _axisMax), as Swell maps range to view.
    private double ToAxisValue(double sample) =>
        _axisMin + (sample - _sampleMin) / (_sampleMax - _sampleMin) * (_axisMax - _axisMin);

    private void DrawSelection(DrawingContext dc, double width, double waveHeight)
    {
        if (_selectionStart is int s && _selectionEnd is int e && e > s)
            dc.DrawRectangle(SelectionBrush, null, new Rect(ToX(s, width), 0, ToX(e, width) - ToX(s, width), waveHeight));

        foreach (int? edge in new[] { _selectionStart, _selectionEnd })
        {
            if (edge is not int i) continue;
            double x = Math.Round(ToX(i, width)) + 0.5;
            dc.DrawLine(SelectionEdgePen, new Point(x, 0), new Point(x, waveHeight));
        }
    }

    private void DrawCursor(DrawingContext dc, double width, double waveHeight)
    {
        if (_cursor is not int i) return;
        double x = Math.Round(ToX(i, width)) + 0.5;
        dc.DrawLine(CursorPen, new Point(x, 0), new Point(x, waveHeight));
    }

    // ---- Mouse and keyboard ----

    private bool HasAudio => _samples.Length > 0 && _sampleRate > 0;
    private double PlotWidth => ActualWidth - YAxisWidth;

    // The selection edge (0..length) nearest to x, x measured from the left edge of the plot.
    private int IndexAt(double x) =>
        (int)Math.Round(Math.Clamp(x / PlotWidth, 0, 1) * _samples.Length);

    private int ClampCursor(int index) => Math.Clamp(index, 0, _samples.Length - 1);

    // The selection edge within EdgeGrabPixels of x, if both edges are set: true for the start, false for the end.
    private bool? EdgeAt(double x)
    {
        if (_selectionStart is not int s || _selectionEnd is not int e) return null;
        double ds = Math.Abs(x - ToX(s, PlotWidth)), de = Math.Abs(x - ToX(e, PlotWidth));
        if (Math.Min(ds, de) > EdgeGrabPixels) return null;
        return ds <= de;
    }

    private void MoveCursor(int index)
    {
        _cursor = ClampCursor(index);
        InvalidateVisual();
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!HasAudio || PlotWidth < 1) return;
        Focus();

        double x = e.GetPosition(this).X - YAxisWidth;
        _pressX = x;
        if (EdgeAt(x) is bool isStart)
        {
            // Grab an edge: the other edge stays put, and this one follows the mouse.
            _dragAnchor = isStart ? _selectionEnd!.Value : _selectionStart!.Value;
            _drag = DragMode.Selecting;
        }
        else
        {
            _dragAnchor = IndexAt(x);
            _drag = DragMode.Click;
        }
        CaptureMouse();
        MoveCursor(IndexAt(x));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!HasAudio || PlotWidth < 1) return;
        double x = e.GetPosition(this).X - YAxisWidth;

        if (_drag == DragMode.None)
        {
            // Show that an edge can be grabbed.
            Cursor = EdgeAt(x) != null ? Cursors.SizeWE : null;
            return;
        }

        if (_drag == DragMode.Click && Math.Abs(x - _pressX) >= DragThresholdPixels)
            _drag = DragMode.Selecting;

        int index = IndexAt(x);
        if (_drag == DragMode.Selecting)
        {
            (_selectionStart, _selectionEnd) = (Math.Min(_dragAnchor, index), Math.Max(_dragAnchor, index));
            SelectionEdited?.Invoke(this, EventArgs.Empty);
        }
        MoveCursor(index);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _drag = DragMode.None;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!HasAudio || e.Handled) return;

        int cursor = _cursor ?? 0;
        var modifiers = Keyboard.Modifiers;
        // Steps in time are rounded to whole samples from the time, so that
        // e.g. 1 ms at 44.1 kHz alternates 44 and 45 samples instead of drifting.
        int Step(int direction) => (modifiers & ModifierKeys.Control) != 0
            ? cursor + direction
            : (int)Math.Round((cursor / (double)_sampleRate
                + direction * ((modifiers & ModifierKeys.Shift) != 0 ? 0.010 : 0.001)) * _sampleRate);

        switch (e.Key)
        {
            case Key.Left: MoveCursor(Step(-1)); break;
            case Key.Right: MoveCursor(Step(+1)); break;
            case Key.Home: MoveCursor(0); break;
            case Key.End: MoveCursor(_samples.Length - 1); break;
            case Key.F when _cursor != null && modifiers == ModifierKeys.None:
                _selectionStart = _cursor;
                InvalidateVisual();
                SelectionEdited?.Invoke(this, EventArgs.Empty);
                break;
            case Key.T when _cursor != null && modifiers == ModifierKeys.None:
                _selectionEnd = _cursor;
                InvalidateVisual();
                SelectionEdited?.Invoke(this, EventArgs.Empty);
                break;
            default: return;
        }
        e.Handled = true;   // also keeps the arrow keys from moving the focus to another control
    }

    protected override void OnIsKeyboardFocusedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusedChanged(e);
        InvalidateVisual();   // draw or remove the focus frame
    }

    // Sample value converted into y coordinate: _sampleMax at the top, _sampleMin at the bottom.
    private double ToY(double value, double waveHeight) =>
        (_sampleMax - Math.Clamp(value, _sampleMin, _sampleMax)) / (_sampleMax - _sampleMin) * waveHeight;

    private void DrawYAxis(DrawingContext dc, double waveHeight)
    {
        double axisX = YAxisWidth - 0.5;
        dc.DrawLine(AxisPen, new Point(axisX, 0), new Point(axisX, waveHeight));

        // The unit goes in the top-left corner of the plot, next to the axis.
        if (_axisUnit != null)
            DrawText(dc, _axisUnit, new Point(YAxisWidth + 4, 2), TextAlignment.Left);

        double span = _axisMax - _axisMin;
        double step = NiceStep(span * MinYTickSpacing / waveHeight);
        int decimals = Decimals(step);

        // Ticks at whole multiples of the step, so the labels are round numbers.
        long first = (long)Math.Ceiling(_axisMin / step - 1e-9);
        long last = (long)Math.Floor(_axisMax / step + 1e-9);
        for (long n = first; n <= last; n++)
        {
            double value = n * step;
            double y = Math.Round((_axisMax - value) / span * waveHeight) + 0.5;
            dc.DrawLine(AxisPen, new Point(axisX - 4, y), new Point(axisX, y));

            var label = MakeText(value.ToString("F" + decimals, CultureInfo.CurrentCulture), TextAlignment.Right);
            // Centre the label on its tick, but keep it inside the waveform area.
            double labelY = Math.Clamp(y - label.Height / 2, 0, Math.Max(waveHeight - label.Height, 0));
            dc.DrawText(label, new Point(axisX - 6, labelY));
        }
    }

    private void DrawWaveform(DrawingContext dc, double width, double waveHeight)
    {
        double samplesPerPixel = _samples.Length / width; //samples per pixel is extremely important

        double ToY(float value) => this.ToY(value, waveHeight);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            if (samplesPerPixel >= 1) //there are more audio samples than horizontal pixels.
            {
                // Many samples per pixel: one min-max line per pixel column.
                int columns = (int)Math.Ceiling(width);
                for (int x = 0; x < columns; x++)
                {
                    int start = (int)(x * samplesPerPixel);
                    int end = Math.Min((int)((x + 1) * samplesPerPixel), _samples.Length);
                    if (start >= end) continue;

                    float min = _samples[start], max = _samples[start];
                    for (int i = start + 1; i < end; i++)
                    {
                        if (_samples[i] < min) min = _samples[i];
                        if (_samples[i] > max) max = _samples[i];
                    }

                    double top = ToY(max), bottom = ToY(min);
                    if (bottom - top < 1) { top -= 0.5; bottom += 0.5; }   // silence still shows as a thin line

                    ctx.BeginFigure(new Point(x + 0.5, top), false, false);
                    ctx.LineTo(new Point(x + 0.5, bottom), true, false);
                }
            }
            else
            {
                // Fewer samples than pixels: connect the samples with lines.
                double pixelsPerSample = width / Math.Max(_samples.Length - 1, 1);
                ctx.BeginFigure(new Point(0, ToY(_samples[0])), false, false);
                for (int i = 1; i < _samples.Length; i++)
                    ctx.LineTo(new Point(i * pixelsPerSample, ToY(_samples[i])), true, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, WavePen, geometry);
    }

    private void DrawTimeAxis(DrawingContext dc, double width, double waveHeight)
    {
        double duration = (double)_samples.Length / _sampleRate;
        double axisY = Math.Round(waveHeight) + 0.5;
        dc.DrawLine(AxisPen, new Point(0, axisY), new Point(width, axisY));

        double step = NiceStep(duration * MinTickSpacing / width);
        int decimals = Decimals(step);

        for (int n = 0; n * step <= duration + 1e-9; n++)
        {
            double t = n * step;
            double x = Math.Round(t / duration * width) + 0.5;
            if (n > 0 && x > width - 25) break;   // too close to the right edge for its label to fit

            dc.DrawLine(AxisPen, new Point(x, axisY), new Point(x, axisY + 4));
            var alignment = n == 0 ? TextAlignment.Left : TextAlignment.Center;
            DrawText(dc, t.ToString("F" + decimals, CultureInfo.CurrentCulture) + " s",
                     new Point(x, axisY + 5), alignment);
        }
    }

    /// <summary>Rounds up to 1, 2 or 5 times a power of ten (0.1, 0.2, 0.5, 1, 2, 5, 10, ...).</summary>
    internal static double NiceStep(double rawStep)
    {
        if (rawStep <= 0) return 1;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        foreach (double m in new[] { 1.0, 2.0, 5.0, 10.0 })
            if (m * magnitude >= rawStep) return m * magnitude;
        return 10 * magnitude;
    }

    // How many decimals a label needs to show a step exactly (1 -> 0, 0.5 -> 1, 0.02 -> 2).
    internal static int Decimals(double step) =>
        step >= 1 ? 0 : (int)Math.Ceiling(-Math.Log10(step) - 1e-9);

    private void DrawText(DrawingContext dc, string text, Point at, TextAlignment alignment) =>
        dc.DrawText(MakeText(text, alignment), at);

    private FormattedText MakeText(string text, TextAlignment alignment) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            TextAlignment = alignment
        };

    // Frozen brushes and pens are read-only, which lets WPF share them cheaply.
    private static Brush MakeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen MakePen(Color color, double thickness)
    {
        var pen = new Pen(MakeBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
}
