using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MasterArbete;

/// <summary>
/// Draws one decay analysis: level in dB over time, with
///   - the energy envelope (10 ms averages) in grey,
///   - the Schroeder curve in blue,
///   - the lines fitted for EDT, T20 and T30, thick over the range each is
///     fitted on and thin where they are extended,
///   - the background noise level and the cut-off point (Lundeby) in red.
/// The time axis is in seconds from the start of the file.
/// </summary>
public class DecayCurveView : FrameworkElement
{
    private const double LeftMargin = 44, RightMargin = 12, TopMargin = 8, BottomMargin = 24;
    private const double MinTickSpacing = 70;

    private static readonly Pen GridPen = MakePen(Color.FromRgb(0xE4, 0xE4, 0xE4), 1);
    private static readonly Pen AxisPen = MakePen(Color.FromRgb(0x66, 0x66, 0x66), 1);
    private static readonly Pen EnvelopePen = MakePen(Color.FromRgb(0xB0, 0xB0, 0xB0), 1);
    private static readonly Pen CurvePen = MakePen(Color.FromRgb(0x1F, 0x5F, 0xAF), 2);
    private static readonly Pen NoisePen = MakePen(Color.FromRgb(0xB2, 0x22, 0x22), 1, dashed: true);
    private static readonly Brush NoiseBrush = MakeBrush(Color.FromRgb(0xB2, 0x22, 0x22));
    private static readonly Brush LabelBrush = MakeBrush(Color.FromRgb(0x44, 0x44, 0x44));

    private static readonly (string Name, Color Color)[] FitStyles =
    {
        ("EDT", Color.FromRgb(0xE0, 0x80, 0x00)),
        ("T20", Color.FromRgb(0x2E, 0x8B, 0x57)),
        ("T30", Color.FromRgb(0x93, 0x70, 0xDB)),
    };

    private DecayAnalysis? _decay;
    private string _title = "";
    private double _startSeconds;

    /// <summary>
    /// Shows a decay. <paramref name="startSeconds"/> is where the analysed
    /// stretch starts in the file, so the time axis matches the waveform.
    /// </summary>
    public void Show(DecayAnalysis? decay, string title, double startSeconds)
    {
        _decay = decay;
        _title = title;
        _startSeconds = startSeconds;
        InvalidateVisual();
    }

    public void Clear() => Show(null, "", 0);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));
        double width = ActualWidth - LeftMargin - RightMargin;
        double height = ActualHeight - TopMargin - BottomMargin;
        if (width < 20 || height < 20) return;

        if (_decay is not { EnvelopeDb.Length: > 0 } decay)
        {
            DrawText(dc, "Choose a stretch with one handclap and press Analyse.", new Point(8, 8), TextAlignment.Left);
            return;
        }

        double duration = decay.EnvelopeDb.Length * decay.EnvelopeIntervalSeconds;
        // Down to at least 10 dB below the noise, in whole steps of 10 dB.
        double bottomDb = Math.Clamp(Math.Floor((decay.NoiseLevelDb - 10) / 10) * 10, -120, -40);
        double X(double seconds) => LeftMargin + seconds / duration * width;   // seconds from the start of the stretch
        double Y(double db) => TopMargin + Math.Clamp(db, bottomDb, 0) / bottomDb * height;

        DrawGridAndAxes(dc, duration, bottomDb, width, height, X, Y);

        dc.PushClip(new RectangleGeometry(new Rect(LeftMargin, TopMargin, width, height)));

        // Energy envelope.
        var envelope = new List<Point>();
        for (int i = 0; i < decay.EnvelopeDb.Length; i++)
            envelope.Add(new Point(X((i + 0.5) * decay.EnvelopeIntervalSeconds), Y(decay.EnvelopeDb[i])));
        DrawPolyline(dc, EnvelopePen, envelope);

        // Noise level and cut-off.
        double noiseY = Y(decay.NoiseLevelDb);
        dc.DrawLine(NoisePen, new Point(LeftMargin, noiseY), new Point(LeftMargin + width, noiseY));
        if (decay.Lundeby != null)
        {
            double cutoffX = X((double)decay.CutoffIndex / decay.SampleRate);
            dc.DrawLine(NoisePen, new Point(cutoffX, TopMargin), new Point(cutoffX, TopMargin + height));
            // At the bottom, out of the way of the legend in the top-right corner.
            DrawText(dc, $"cut-off {_startSeconds + (double)decay.CutoffIndex / decay.SampleRate:0.00} s",
                new Point(cutoffX + 4, TopMargin + height - 18), TextAlignment.Left, NoiseBrush);
        }

        // Schroeder curve, one point per pixel column is plenty since it is smooth.
        double onset = (double)decay.OnsetIndex / decay.SampleRate;
        var curve = new List<Point>();
        int step = Math.Max(1, (int)(decay.CurveDb.Length / width));
        for (int i = 0; i < decay.CurveDb.Length; i += step)
            if (!double.IsNegativeInfinity(decay.CurveDb[i]))
                curve.Add(new Point(X(onset + (double)i / decay.SampleRate), Y(decay.CurveDb[i])));
        DrawPolyline(dc, CurvePen, curve);

        // Fitted lines. The curve's time starts at the onset.
        var fits = new[] { decay.Edt, decay.T20, decay.T30 };
        for (int f = 0; f < fits.Length; f++)
        {
            if (fits[f] is not { } fit) continue;
            var color = FitStyles[f].Color;
            double TimeAt(double db) => onset + (db - fit.InterceptDb) / fit.SlopeDbPerSecond;
            dc.DrawLine(MakePen(color, 1, dashed: true),
                new Point(X(onset), Y(fit.InterceptDb)), new Point(X(TimeAt(bottomDb)), Y(bottomDb)));
            dc.DrawLine(MakePen(color, 3),
                new Point(X(TimeAt(fit.FromDb)), Y(fit.FromDb)), new Point(X(TimeAt(fit.ToDb)), Y(fit.ToDb)));
        }
        dc.Pop();

        DrawLegend(dc, decay, width);
        DrawText(dc, $"noise {decay.NoiseLevelDb:0.0} dB", new Point(LeftMargin + width - 4, noiseY - 16),
            TextAlignment.Right, NoiseBrush);
    }

    private void DrawGridAndAxes(DrawingContext dc, double duration, double bottomDb, double width, double height,
        Func<double, double> x, Func<double, double> y)
    {
        // Level: a line every 10 dB.
        for (double db = 0; db >= bottomDb - 1e-9; db -= 10)
        {
            double py = Math.Round(y(db)) + 0.5;
            dc.DrawLine(GridPen, new Point(LeftMargin, py), new Point(LeftMargin + width, py));
            DrawText(dc, db.ToString("0", CultureInfo.CurrentCulture), new Point(LeftMargin - 6, py - 7), TextAlignment.Right);
        }
        DrawText(dc, "dB", new Point(4, TopMargin), TextAlignment.Left);

        // Time: round numbers of seconds from the start of the file.
        double tickStep = WaveformView.NiceStep(duration * MinTickSpacing / width);
        int decimals = WaveformView.Decimals(tickStep);
        double axisY = Math.Round(TopMargin + height) + 0.5;
        for (double t = Math.Ceiling(_startSeconds / tickStep - 1e-9) * tickStep; t <= _startSeconds + duration + 1e-9; t += tickStep)
        {
            double px = Math.Round(x(t - _startSeconds)) + 0.5;
            dc.DrawLine(GridPen, new Point(px, TopMargin), new Point(px, axisY));
            double label = Math.Abs(t) < tickStep / 2 ? 0 : t;   // avoid "-0 s" from rounding
            if (px < LeftMargin + width - 20)
                DrawText(dc, label.ToString("F" + decimals, CultureInfo.CurrentCulture) + " s", new Point(px, axisY + 4), TextAlignment.Center);
        }
        dc.DrawLine(AxisPen, new Point(LeftMargin - 0.5, TopMargin), new Point(LeftMargin - 0.5, axisY));
        dc.DrawLine(AxisPen, new Point(LeftMargin, axisY), new Point(LeftMargin + width, axisY));
    }

    private void DrawLegend(DrawingContext dc, DecayAnalysis decay, double width)
    {
        var lines = new List<(string Text, Brush Brush)> { (_title, LabelBrush) };
        if (decay.Lundeby == null)
            lines.Add(("No decay above the background noise", NoiseBrush));

        var fits = new[] { decay.Edt, decay.T20, decay.T30 };
        var reliable = new[] { true, decay.T20IsReliable, decay.T30IsReliable };
        for (int f = 0; f < fits.Length; f++)
        {
            if (fits[f] is not { } fit) continue;
            string note = reliable[f] ? "" : "  (not reliable)";
            lines.Add(($"{FitStyles[f].Name} = {fit.ReverberationTime:0.00} s  ({fit.FromDb:0} to {fit.ToDb:0} dB){note}",
                MakeBrush(FitStyles[f].Color)));
        }
        lines.Add(($"dynamic range {decay.DynamicRangeDb:0.0} dB", LabelBrush));

        double right = LeftMargin + width - 6, top = TopMargin + 4;
        foreach (var (text, brush) in lines)
        {
            DrawText(dc, text, new Point(right, top), TextAlignment.Right, brush, bold: brush == LabelBrush && text == _title);
            top += 16;
        }
    }

    private static void DrawPolyline(DrawingContext dc, Pen pen, List<Point> points)
    {
        if (points.Count < 2) return;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            for (int i = 1; i < points.Count; i++) ctx.LineTo(points[i], true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private void DrawText(DrawingContext dc, string text, Point at, TextAlignment alignment,
        Brush? brush = null, bool bold = false)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            12, brush ?? LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            TextAlignment = alignment
        };
        dc.DrawText(formatted, at);
    }

    private static Brush MakeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen MakePen(Color color, double thickness, bool dashed = false)
    {
        var pen = new Pen(MakeBrush(color), thickness);
        if (dashed) pen.DashStyle = DashStyles.Dash;
        pen.Freeze();
        return pen;
    }
}
