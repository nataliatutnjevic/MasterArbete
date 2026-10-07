using System;
using System.Globalization;
using System.Windows;
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

    private float[] _samples = Array.Empty<float>(); //where the actual audio samples stored: _samples contain the amplitudes.
    private int _sampleRate; //_sampleRate tells you ow many samples represent one second, for example _sampleRate=44100

    // Vertical scale: the sample values at the bottom and top of the plot,
    // and the numbers the y-axis shows for them.
    private double _sampleMin = -1, _sampleMax = 1;
    private double _axisMin = -1, _axisMax = 1;

    /// <summary>
    /// Shows these samples (values in -1..1) recorded at the given rate.
    /// sampleRange is the sample values at the bottom and top of the plot
    /// (full scale, -1..1, if left out). axisValues is the numbers the y-axis
    /// shows for those two (the sample values themselves if left out).
    /// </summary>
    /// This gices the waveform component new audio to display.
    public void SetSamples(float[] samples, int sampleRate,
        (double Min, double Max)? sampleRange = null, (double Min, double Max)? axisValues = null)
    {
        _samples = samples;
        _sampleRate = sampleRate;

        // A range that is empty or upside down can't be drawn, so fall back.
        (_sampleMin, _sampleMax) = sampleRange is { } r && r.Max > r.Min ? r : (-1, 1);
        (_axisMin, _axisMax) = axisValues is { } a && a.Max > a.Min ? a : (_sampleMin, _sampleMax);
        InvalidateVisual();
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
            DrawWaveform(dc, width, waveHeight);
            DrawTimeAxis(dc, width, waveHeight);
        }
        else //if there is no audio.
        {
            DrawText(dc, "No waveform", new Point(8, 8), TextAlignment.Left);
        }

        dc.Pop();
    }

    // Sample value converted into y coordinate: _sampleMax at the top, _sampleMin at the bottom.
    private double ToY(double value, double waveHeight) =>
        (_sampleMax - Math.Clamp(value, _sampleMin, _sampleMax)) / (_sampleMax - _sampleMin) * waveHeight;

    private void DrawYAxis(DrawingContext dc, double waveHeight)
    {
        double axisX = YAxisWidth - 0.5;
        dc.DrawLine(AxisPen, new Point(axisX, 0), new Point(axisX, waveHeight));

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
    private static double NiceStep(double rawStep)
    {
        if (rawStep <= 0) return 1;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        foreach (double m in new[] { 1.0, 2.0, 5.0, 10.0 })
            if (m * magnitude >= rawStep) return m * magnitude;
        return 10 * magnitude;
    }

    // How many decimals a label needs to show a step exactly (1 -> 0, 0.5 -> 1, 0.02 -> 2).
    private static int Decimals(double step) =>
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
