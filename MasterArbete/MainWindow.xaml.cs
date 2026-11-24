using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MasterArbete;

public partial class MainWindow : Window
{
    // MediaPlayer (instead of System.Media.SoundPlayer) so playback isn't
    // limited to uncompressed PCM if this ever needs to handle more formats.
    private readonly MediaPlayer _player = new();
    private string? _currentFilePath;
    private string? _playbackCopy;   // temporary little-endian copy of a RIFX file, deleted when no longer needed

    // Channel 0 of the open file in pascals, or null if the file isn't
    // calibrated (or couldn't be read). Used for the background-noise measurement.
    private float[]? _pressure;
    private int _sampleRate;

    // Channel 0 of the open file as read (-1..1), for the reverberation time,
    // which doesn't need calibration. Null if the samples couldn't be read.
    private float[]? _samples;
    private double _reverbStartSeconds;   // where the analysed stretch starts, for the decay plot's time axis
    private (double From, double To)? _analysedReverbStretch;   // the From/To of the last analysis, null before one
    private string _reverbAnalysedText = "";
    private const double MinReverbSeconds = 0.1;   // SchroederDecay needs at least ten 10 ms intervals
    // The LOJ limits start at 250 Hz, and a handclap has too little energy at 125 Hz to measure it anyway.
    private const double LowestReverbBandHz = LojReverbLimit.LowestBandHz;

    public MainWindow()
    {
        InitializeComponent();

        _player.MediaEnded += (_, _) => StopPlayback();
        _playheadTimer.Tick += PlayheadTimer_Tick;

        Waveform.CursorMoved += (_, _) => UpdateCursorText();
        Waveform.SelectionEdited += Waveform_SelectionEdited;
        Waveform.ViewChanged += (_, _) => UpdateWaveformScrollBar();
        Waveform.PlayPauseRequested += (_, _) =>
        {
            if (_playing) PausePlayback();
            else StartPlayback(Waveform.CursorSeconds ?? 0, null);
        };
        UpdateCursorText();
        ShowLojResult();
        ShowNoiseResult();
    }

    // ---- Zoom: the scrollbar under the waveform ----
    //
    // In seconds: the thumb is as wide as the part shown, and its position is
    // where that part starts.

    private void UpdateWaveformScrollBar()
    {
        double total = Waveform.TotalSeconds, view = Waveform.ViewSeconds;
        WaveformScrollBar.IsEnabled = Waveform.IsZoomed;
        WaveformScrollBar.Minimum = 0;
        WaveformScrollBar.Maximum = Math.Max(total - view, 0);
        WaveformScrollBar.ViewportSize = view;
        WaveformScrollBar.LargeChange = view * 0.9;
        WaveformScrollBar.SmallChange = view * 0.1;
        WaveformScrollBar.Value = Waveform.ViewStartSeconds;
    }

    private void WaveformScrollBar_Scroll(object sender, System.Windows.Controls.Primitives.ScrollEventArgs e) =>
        Waveform.ScrollTo(WaveformScrollBar.Value);

    // ---- Playback ----
    //
    // Play starts at the cursor (or the beginning without one). Pause moves the
    // cursor to where playback got to, so Play continues from there; Stop
    // leaves the cursor where playback started. A timer moves the playhead on
    // the waveform, and stops "Play selection" at To.

    private readonly DispatcherTimer _playheadTimer = new() { Interval = TimeSpan.FromMilliseconds(20) };
    private bool _playing;
    private double? _playUntilSeconds;   // the end of the selection, when playing just the selection

    private void StartPlayback(double fromSeconds, double? untilSeconds)
    {
        if (_currentFilePath == null || !PlayButton.IsEnabled) return;
        _player.Position = TimeSpan.FromSeconds(fromSeconds);
        _player.Play();
        _playing = true;
        _playUntilSeconds = untilSeconds;
        Waveform.SetPlayhead(fromSeconds);
        _playheadTimer.Start();
    }

    private void PausePlayback()
    {
        if (!_playing) return;
        _player.Pause();
        double at = _player.Position.TotalSeconds;
        EndPlayback();
        Waveform.SetCursor(at);
    }

    private void StopPlayback()
    {
        _player.Stop();
        EndPlayback();
    }

    private void EndPlayback()
    {
        _playing = false;
        _playUntilSeconds = null;
        _playheadTimer.Stop();
        Waveform.SetPlayhead(null);
    }

    private void PlayheadTimer_Tick(object? sender, EventArgs e)
    {
        double at = _player.Position.TotalSeconds;
        if (_playUntilSeconds is double until && at >= until)
            StopPlayback();
        else
            Waveform.SetPlayhead(at);
    }

    // ---- Cursor and selection on the waveform ----
    //
    // The waveform's selection always shows the From/To of the open tab
    // (background noise or reverberation time): dragging or F / T on the
    // waveform writes the boxes, and typing in the boxes moves the selection.

    private bool _writingStretchBoxes;   // true while the waveform writes the boxes, so they don't write back

    // The From and To boxes of the open tab, or null on a tab without them.
    private (TextBox From, TextBox To)? ActiveStretchBoxes()
    {
        if (Tabs == null) return null;   // during InitializeComponent
        var tab = Tabs.SelectedItem;
        return tab == NoiseTab ? (NoiseFromBox, NoiseToBox)
            : tab == ReverbTab ? (ReverbFromBox, ReverbToBox)
            : null;
    }

    private void Waveform_SelectionEdited(object? sender, EventArgs e)
    {
        if (ActiveStretchBoxes() is { } boxes && boxes.From.IsEnabled)
        {
            _writingStretchBoxes = true;
            if (Waveform.SelectionStartSeconds is double from) boxes.From.Text = FormatSeconds(from);
            if (Waveform.SelectionEndSeconds is double to) boxes.To.Text = FormatSeconds(to);
            _writingStretchBoxes = false;
        }
        UpdateCursorText();
        UpdateReverbMarkers();
    }

    private void StretchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_writingStretchBoxes) ShowActiveStretch();
    }

    // TabControl.SelectionChanged also bubbles up from controls inside the
    // tabs (e.g. the reverberation table), so only react to the tabs themselves.
    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == Tabs) ShowActiveStretch();
    }

    // Shows the open tab's From/To as the waveform's selection.
    private void ShowActiveStretch()
    {
        if (Waveform == null) return;   // during InitializeComponent
        if (ActiveStretchBoxes() is { } boxes && boxes.From.IsEnabled)
        {
            double? from = TryParseSeconds(boxes.From.Text, out double f) ? f : null;
            double? to = TryParseSeconds(boxes.To.Text, out double t) ? t : null;
            Waveform.SetSelection(from, to);
        }
        else
        {
            Waveform.SetSelection(null, null);
        }
        UpdateCursorText();
        UpdateReverbMarkers();
    }

    private void UpdateCursorText()
    {
        if (CursorText == null) return;   // during InitializeComponent
        PlaySelectionButton.IsEnabled = PlayButton.IsEnabled && HasSelection;
        var culture = CultureInfo.CurrentCulture;
        if (Waveform.CursorSeconds is not double t)
        {
            CursorText.Text = _samples != null ? "Click the waveform for a cursor, or drag to select From–To." : "";
            return;
        }

        string amplitude = Waveform.CursorAmplitude is double a
            ? a.ToString("0.0000", culture) + (Waveform.AxisUnit is { } unit ? " " + unit : "")
            : "–";
        string text = $"Cursor {FormatSeconds(t)} s   amplitude {amplitude}";
        if (Waveform.SelectionStartSeconds is double from && Waveform.SelectionEndSeconds is double to && to > from)
            text += $"   selection {FormatSeconds(from)}–{FormatSeconds(to)} s ({FormatSeconds(to - from)} s)";
        CursorText.Text = text;
    }

    private bool HasSelection => Waveform.SelectionStartSeconds is double from
        && Waveform.SelectionEndSeconds is double to && to > from;

    // Milliseconds, so the arrow keys' 1 ms steps show.
    private static string FormatSeconds(double seconds) => seconds.ToString("0.000", CultureInfo.CurrentCulture);

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "WAV audio files (*.wav)|*.wav|All files (*.*)|*.*",
            Title = "Select a WAV file"
        };

        if (dialog.ShowDialog() != true)
            return;

        LoadFile(dialog.FileName);
    }

    private void LoadFile(string path)
    {
        StopPlayback();
        StatusText.Text = "";
        try
        {
            var info = WavFileReader.ReadHeader(path);

            _currentFilePath = path;
            FilePathText.Text = path;
            FormatText.Text = $"Format: {info.FormatName}";
            ByteOrderText.Text = info.IsBigEndian ? "Byte order: big-endian (RIFX)" : "Byte order: little-endian (RIFF)";
            SampleRateText.Text = $"Sample rate: {info.SampleRate} Hz";
            ChannelsText.Text = $"Channels: {info.Channels}";
            BitDepthText.Text = $"Bit depth: {info.BitsPerSample}-bit";
            DurationText.Text = $"Duration: {info.DurationSeconds:0.00} s";
            ChunksText.Text = "Chunks: " + string.Join(", ",
                info.Chunks.Select(c => $"{c.Id.TrimEnd()} ({c.Size:N0} B)"));
            ShowSwelInfo(info);
            float[]? samples = ShowWaveform(path, info);
            SetUpNoiseMeasurement(info, samples);
            SetUpReverbAnalysis(samples);

            bool canPlay = OpenForPlayback(path, info);
            PlayButton.IsEnabled = canPlay;
            PauseButton.IsEnabled = canPlay;
            StopButton.IsEnabled = canPlay;
            UpdateCursorText();   // after the buttons, since it also enables Play selection
        }
        catch (Exception ex)
        {
            _currentFilePath = null;
            PlayButton.IsEnabled = false;
            PauseButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            ClearInfo();   // also disables Play selection, through UpdateCursorText
            FilePathText.Text = path;
            StatusText.Text = $"Couldn't read this file: {ex.Message}";
        }
    }

    private void ShowSwelInfo(WavInfo info)
    {
        var swel = info.SwelChunk;
        if (swel == null)
        {
            SwelText.Text = "swel: not found";
            SwelFieldsText.Visibility = Visibility.Collapsed;
            return;
        }

        var dataChunk = info.Chunks.FirstOrDefault(c => c.Id == "data");
        string position = dataChunk == null ? ""
            : swel.Offset < dataChunk.Offset ? ", before data" : ", after data";

        SwelText.Text = $"swel: found, {swel.Size:N0} bytes at offset {swel.Offset}{position}";

        if (info.Swel == null || info.Swel.Fields.Count == 0)
        {
            SwelFieldsText.Text = "(header is empty)";
        }
        else
        {
            // Line the values up in a column after the longest key.
            int keyWidth = info.Swel.Fields.Max(f => f.Key.Length);
            SwelFieldsText.Text = string.Join("\n",
                info.Swel.Fields.Select(f => $"{f.Key.PadRight(keyWidth)} = {f.Value}"));
        }
        SwelFieldsText.Visibility = Visibility.Visible;

        var problems = info.CheckSwel();
        if (problems.Count > 0)
            StatusText.Text = "Header mismatch:\n" + string.Join("\n", problems);
    }

    // A waveform problem (e.g. an unsupported sample type) shouldn't stop the
    // file from opening and playing, so it's reported in the status line instead.
    // Returns the samples of channel 0, or null if they couldn't be read.
    private float[]? ShowWaveform(string path, WavInfo info)
    {
        try
        {
            // The waveform shows channel 0, so use that channel's swel settings.
            // "range" is in raw sample values, while the waveform works in -1..1.
            (double Min, double Max)? sampleRange = info.Swel?.GetRange(0) is { } range
                ? (range.Min / info.FullScale, range.Max / info.FullScale)
                : null;

            float[] samples = WavFileReader.ReadSamples(path, info);
            Waveform.SetSamples(samples, info.SampleRate,
                sampleRange, info.Swel?.GetView(0), info.Swel?.GetViewUnit(0));
            return samples;
        }
        catch (Exception ex)
        {
            Waveform.Clear();
            AppendStatus($"Couldn't draw the waveform: {ex.Message}");
            return null;
        }
    }

    // Background noise needs levels in dB SPL, so it can only be measured in
    // a calibrated file (one whose swel "view" is in Pa).
    private void SetUpNoiseMeasurement(WavInfo info, float[]? samples)
    {
        var calibration = PressureCalibration.FromHeader(info);
        _pressure = samples != null ? calibration?.ToPascal(samples, info) : null;
        _sampleRate = info.SampleRate;

        bool canMeasure = _pressure != null;
        NoiseFromBox.IsEnabled = NoiseToBox.IsEnabled = MeasureNoiseButton.IsEnabled = canMeasure;
        NoiseFromBox.Text = canMeasure ? "0" : "";
        NoiseToBox.Text = canMeasure ? (_pressure!.Length / (double)_sampleRate).ToString("0.00", CultureInfo.CurrentCulture) : "";
        _noiseMeasurement = null;
        _noiseMessage = canMeasure
            ? null
            : samples == null
                ? "The samples couldn't be read."
                : "This file isn't calibrated (its swel \"view\" isn't in Pa), so levels in dB SPL can't be calculated.";
        ShowNoiseResult();
    }

    private void MeasureNoiseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pressure == null) return;

        if (!TryParseSeconds(NoiseFromBox.Text, out double from) || !TryParseSeconds(NoiseToBox.Text, out double to))
        {
            ShowNoiseProblem("Enter the start and end in seconds, e.g. 12.5.");
            return;
        }

        double duration = _pressure.Length / (double)_sampleRate;
        from = Math.Clamp(from, 0, duration);
        to = Math.Clamp(to, 0, duration);
        int start = (int)Math.Round(from * _sampleRate);
        int end = (int)Math.Round(to * _sampleRate);
        if (end - start < BackgroundNoise.MinSamples)
        {
            ShowNoiseProblem($"The part to measure must be at least " +
                $"{BackgroundNoise.MinSamples / (double)_sampleRate:0.00} s long, and end after it starts.");
            return;
        }

        _noiseMeasurement = (BackgroundNoise.Measure(_pressure, _sampleRate, start, end - start), from, to);
        _noiseMessage = null;
        ShowNoiseResult();
    }

    // The last measurement, so switching level needs no new one, and a message
    // to show instead when the file can't be measured.
    private (NoiseLevels Levels, double From, double To)? _noiseMeasurement;
    private string? _noiseMessage;

    // A problem with the typed stretch: shown instead of the result, which no longer matches the boxes.
    private void ShowNoiseProblem(string message)
    {
        _noiseMeasurement = null;
        _noiseMessage = message;
        ShowNoiseResult();
    }

    // A coloured verdict for the picked level, and below it the levels with their limits.
    private void ShowNoiseResult()
    {
        if (NoiseVerdictText == null || NoiseResultText == null) return;   // during InitializeComponent
        var culture = CultureInfo.CurrentCulture;
        var limit = BackgroundNoise.LojLimits.Single(l => l.Level == _lojLevel);
        string limitsText = string.Format(culture, "Kravnivå {0}: below {1} dB(A) and below {2} dB(C).",
            limit.Level, limit.MaxDbA, limit.MaxDbC);

        if (_noiseMeasurement is not { } measurement)
        {
            SetVerdict(NoiseVerdictBorder, NoiseVerdictText,
                _noiseMessage ?? $"Select a stretch with only background noise and press Measure to compare with Kravnivå {limit.Level}.",
                NoResultColors);
            NoiseResultText.Text = limitsText;
            return;
        }

        var levels = measurement.Levels;
        bool aMet = levels.LAeq < limit.MaxDbA, cMet = levels.LCeq < limit.MaxDbC;
        if (aMet && cMet)
        {
            SetVerdict(NoiseVerdictBorder, NoiseVerdictText, $"✓ Meets Kravnivå {limit.Level}", MeetsColors);
        }
        else
        {
            string tooHigh = !aMet && !cMet ? "dB(A) and dB(C)" : !aMet ? "dB(A)" : "dB(C)";
            SetVerdict(NoiseVerdictBorder, NoiseVerdictText, $"✗ Fails Kravnivå {limit.Level}: {tooHigh} too high", FailsColors);
        }

        NoiseResultText.Text = string.Join("\n",
            "Level              measured   limit      result",
            string.Format(culture, "LAeq (A-weighted)  {0,5:0.0} dB   < {1} dB    {2}", levels.LAeq, limit.MaxDbA, aMet ? "meets" : "fails"),
            string.Format(culture, "LCeq (C-weighted)  {0,5:0.0} dB   < {1} dB    {2}", levels.LCeq, limit.MaxDbC, cMet ? "meets" : "fails"),
            string.Format(culture, "LZeq (unweighted)  {0,5:0.0} dB", levels.LZeq),
            "",
            string.Format(culture, "Measured over {0:0.000}–{1:0.000} s ({2:0.00} s). Both levels must be below their limits.",
                measurement.From, measurement.To, levels.DurationSeconds));
    }

    // The reverberation time only depends on how fast the level falls, so it
    // works on any recording, calibrated or not.
    private void SetUpReverbAnalysis(float[]? samples)
    {
        _samples = samples;
        _analysedReverbStretch = null;
        bool canAnalyse = samples != null;
        ReverbFromBox.IsEnabled = ReverbToBox.IsEnabled = AnalyseReverbButton.IsEnabled = canAnalyse;
        ReverbFromBox.Text = canAnalyse ? "0" : "";
        ReverbToBox.Text = canAnalyse ? (samples!.Length / (double)_sampleRate).ToString("0.00", CultureInfo.CurrentCulture) : "";
        ReverbStatusText.Text = canAnalyse ? "" : "The samples couldn't be read.";
        ReverbTable.ItemsSource = null;
        _reverbResult = null;
        ShowLojResult();
        DecayCurve.Clear();
    }

    private async void AnalyseReverbButton_Click(object sender, RoutedEventArgs e)
    {
        if (_samples is not { } samples) return;

        if (!TryParseSeconds(ReverbFromBox.Text, out double from) || !TryParseSeconds(ReverbToBox.Text, out double to))
        {
            ReverbStatusText.Text = "Enter the start and end in seconds, e.g. 12.5.";
            return;
        }

        int sampleRate = _sampleRate;
        double duration = samples.Length / (double)sampleRate;
        from = Math.Clamp(from, 0, duration);
        to = Math.Clamp(to, 0, duration);
        int start = (int)Math.Round(from * sampleRate);
        int count = (int)Math.Round(to * sampleRate) - start;
        if (count < MinReverbSeconds * sampleRate)
        {
            ReverbStatusText.Text = $"The stretch must be at least {MinReverbSeconds:0.0} s long, and end after it starts.";
            return;
        }

        // Filtering a long stretch into all the bands takes a moment, so it
        // runs in the background and the window stays responsive.
        bool timeReversed = TimeReversedBox.IsChecked == true;
        AnalyseReverbButton.IsEnabled = false;
        ReverbStatusText.Text = "Analysing…";
        try
        {
            var result = await Task.Run(() => ReverberationAnalysis.Analyse(samples, sampleRate, start, count, timeReversed));
            if (!ReferenceEquals(samples, _samples)) return;   // another file was opened in the meantime

            string? selectedBand = (ReverbTable.SelectedItem as ReverbRow)?.Band;
            var rows = new[] { ReverbRow.From("Broadband", result.Broadband) }
                .Concat(result.Bands
                    .Where(b => b.Band.NominalHz >= LowestReverbBandHz)
                    .Select(b => ReverbRow.From(b.Band.Name, b.Decay)))
                .ToList();
            _reverbStartSeconds = from;
            _analysedReverbStretch = (from, to);
            _reverbAnalysedText = string.Format(CultureInfo.CurrentCulture, "Analysed {0:0.000}–{1:0.000} s{2}.",
                from, to, timeReversed ? ", time-reversed band filters" : "");
            ReverbStatusText.Text = _reverbAnalysedText;
            ReverbTable.ItemsSource = rows;
            _reverbResult = result;
            ShowLojResult();
            // Keep showing the same band after a new analysis, the 1 kHz band at first.
            ReverbTable.SelectedItem = rows.FirstOrDefault(r => r.Band == selectedBand)
                ?? rows.FirstOrDefault(r => r.Band == "1 kHz") ?? rows[0];
            UpdateReverbMarkers();
        }
        catch (Exception ex)
        {
            ReverbStatusText.Text = $"Couldn't analyse: {ex.Message}";
        }
        finally
        {
            AnalyseReverbButton.IsEnabled = _samples != null;
        }
    }

    // ---- LOJ: the analysis compared with the Kravnivå picked with the buttons ----

    private ReverberationResult? _reverbResult;   // the last analysis, so switching level needs no new one

    private static readonly (Color Background, Color Text) MeetsColors = (Color.FromRgb(0xDF, 0xF2, 0xDF), Color.FromRgb(0x1E, 0x6B, 0x1E));
    private static readonly (Color Background, Color Text) FailsColors = (Color.FromRgb(0xF8, 0xDE, 0xDE), Color.FromRgb(0xA0, 0x1C, 0x1C));
    private static readonly (Color Background, Color Text) CantTellColors = (Color.FromRgb(0xFC, 0xF0, 0xD4), Color.FromRgb(0x8A, 0x5A, 0x00));
    private static readonly (Color Background, Color Text) NoResultColors = (Color.FromRgb(0xEE, 0xEE, 0xEE), Color.FromRgb(0x55, 0x55, 0x55));

    // The Kravnivå picked, shared by the background-noise and reverberation tabs: picking one on either tab picks it on both.
    private int _lojLevel = 3;

    private LojReverbLimit SelectedLojLimit() => LojReverbLimit.All.Single(l => l.Level == _lojLevel);

    private void LojLevelButton_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        _lojLevel = int.Parse(tag, CultureInfo.InvariantCulture);
        if (LojLevel1Button == null || NoiseLojLevel1Button == null) return;   // during InitializeComponent

        // Check the same level on the other tab. Checking an already checked button raises nothing, so this doesn't go round in circles.
        var reverbButtons = new[] { LojLevel1Button, LojLevel2Button, LojLevel3Button };
        var noiseButtons = new[] { NoiseLojLevel1Button, NoiseLojLevel2Button, NoiseLojLevel3Button };
        reverbButtons[_lojLevel - 1].IsChecked = true;
        noiseButtons[_lojLevel - 1].IsChecked = true;

        ShowLojResult();
        ShowNoiseResult();
    }

    // A coloured verdict for the picked level, and below it every band with its T20 and limit.
    private void ShowLojResult()
    {
        if (LojVerdictText == null || LojReverbText == null) return;   // during InitializeComponent
        var culture = CultureInfo.CurrentCulture;
        var limit = SelectedLojLimit();

        if (_reverbResult is not { } result)
        {
            SetLojVerdict($"Press Analyse to compare with Kravnivå {limit.Level}.", NoResultColors);
            LojReverbText.Text = string.Format(culture, "Kravnivå {0}: T20 below {1:0.00} s at 250 Hz, below {2:0.00} s from 500 Hz up.",
                limit.Level, limit.MaxBelow500HzSeconds, limit.MaxFrom500HzSeconds);
            return;
        }

        var check = limit.Check(result);
        switch (check.Verdict)
        {
            case LojVerdict.Meets:
                SetLojVerdict($"✓ Meets Kravnivå {limit.Level}", MeetsColors);
                break;
            case LojVerdict.Fails:
                SetLojVerdict($"✗ Fails Kravnivå {limit.Level}: "
                    + string.Join(", ", check.DecidingBands.Select(b => b.Band.Name)) + " too long", FailsColors);
                break;
            default:
                SetLojVerdict($"? Can't tell for Kravnivå {limit.Level}: "
                    + string.Join(", ", check.DecidingBands.Select(b => b.Band.Name)) + " not reliable", CantTellColors);
                break;
        }

        var lines = new List<string> { "Band       T20 (s)   limit (s)   result" };
        foreach (var band in check.Bands)
        {
            string t20 = band.T20Seconds is not double t ? "–"
                : band.IsReliable ? t.ToString("0.00", culture)
                : "(" + t.ToString("0.00", culture) + ")";
            string verdict = band.Verdict switch
            {
                LojVerdict.Meets => "meets",
                LojVerdict.Fails => "fails",
                _ => band.T20Seconds == null ? "can't tell: no decay" : "can't tell: under 35 dB",
            };
            lines.Add(string.Format(culture, "{0,-10} {1,-9} < {2,-9:0.00} {3}", band.Band.Name, t20, band.LimitSeconds, verdict));
        }
        lines.Add("");
        lines.Add("Every band from 250 Hz up must be below its limit. A T20 in parentheses has under 35 dB of dynamic range and isn't trusted.");
        LojReverbText.Text = string.Join("\n", lines);
    }

    private void SetLojVerdict(string text, (Color Background, Color Text) colors) =>
        SetVerdict(LojVerdictBorder, LojVerdictText, text, colors);

    private static void SetVerdict(Border border, TextBlock textBlock, string text, (Color Background, Color Text) colors)
    {
        textBlock.Text = text;
        textBlock.Foreground = new SolidColorBrush(colors.Text);
        border.Background = new SolidColorBrush(colors.Background);
    }

    private void ReverbTable_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReverbTable.SelectedItem is ReverbRow row)
            DecayCurve.Show(row.Decay, row.Band, _reverbStartSeconds);
        UpdateReverbMarkers();
    }

    private static readonly Color ClapStartColor = Color.FromRgb(0x2E, 0x6B, 0x2E);
    private static readonly Color NoiseCutoffColor = Color.FromRgb(0xB2, 0x22, 0x22);   // as the cut-off in the decay plot

    // Whether the Reverberation time tab's From/To are still the stretch that
    // was analysed (to within half a millisecond, since the boxes show ms).
    private bool ReverbStretchIsAnalysed()
    {
        if (_analysedReverbStretch is not { } analysed || _samples == null) return false;
        if (!TryParseSeconds(ReverbFromBox.Text, out double from) || !TryParseSeconds(ReverbToBox.Text, out double to)) return false;
        double duration = _samples.Length / (double)_sampleRate;
        return Math.Abs(Math.Clamp(from, 0, duration) - analysed.From) < 0.0005
            && Math.Abs(Math.Clamp(to, 0, duration) - analysed.To) < 0.0005;
    }

    // On the Reverberation time tab, marks on the waveform where the analysis
    // of the band selected in the table found the clap and where the decay
    // reached the noise (Lundeby's cut-off), so it's easy to see that it looked
    // at the right thing. Once From/To change, the analysis no longer matches
    // the stretch, so the marks go until Analyse is pressed again.
    private void UpdateReverbMarkers()
    {
        if (Waveform == null || Tabs == null) return;   // during InitializeComponent
        bool analysed = ReverbStretchIsAnalysed();
        if (_analysedReverbStretch != null)
            ReverbStatusText.Text = analysed ? _reverbAnalysedText : "From/To have changed since the analysis: press Analyse to update.";

        if (Tabs.SelectedItem != ReverbTab || !analysed || ReverbTable.SelectedItem is not ReverbRow row)
        {
            Waveform.SetMarkers(null);
            return;
        }

        // Both markers are times for the band picked in the table, so the label names the band first:
        // "1 kHz band: decay starts", not "clap start (1 kHz)", which reads as if the clap were at 1 kHz.
        var decay = row.Decay;
        string band = row.Band == "Broadband" ? "Broadband" : $"{row.Band} band";
        var markers = new List<WaveformMarker>
        {
            new(_reverbStartSeconds + decay.OnsetIndex / (double)decay.SampleRate, $"{band}: decay starts", ClapStartColor),
        };
        if (decay.Lundeby != null)
            markers.Add(new(_reverbStartSeconds + decay.CutoffIndex / (double)decay.SampleRate, $"{band}: decay reaches the noise", NoiseCutoffColor));
        Waveform.SetMarkers(markers);
    }

    // Accepts both "12.5" and "12,5", since Swedish Windows uses a decimal comma.
    private static bool TryParseSeconds(string text, out double seconds) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);

    // Windows can't play RIFX (big-endian) files, so those are played from a
    // temporary little-endian copy. Like the waveform, a problem here shouldn't
    // stop the file from being shown, so it's reported in the status line.
    private bool OpenForPlayback(string path, WavInfo info)
    {
        DeletePlaybackCopy();
        if (!info.IsBigEndian)
        {
            _player.Open(new Uri(path));
            return true;
        }

        try
        {
            _playbackCopy = Path.Combine(Path.GetTempPath(), $"MasterArbete-{Guid.NewGuid():N}.wav");
            WavFileReader.WriteLittleEndianCopy(path, info, _playbackCopy);
            _player.Open(new Uri(_playbackCopy));
            return true;
        }
        catch (Exception ex)
        {
            DeletePlaybackCopy();
            AppendStatus($"Couldn't prepare this RIFX file for playback: {ex.Message}");
            return false;
        }
    }

    private void DeletePlaybackCopy()
    {
        if (_playbackCopy == null) return;
        _player.Close();   // let go of the file before deleting it
        try { File.Delete(_playbackCopy); }
        catch (IOException) { }                    // still in use: it's in the temp folder, so leaving it is harmless
        catch (UnauthorizedAccessException) { }
        _playbackCopy = null;
    }

    private void AppendStatus(string message)
    {
        if (StatusText.Text.Length > 0) StatusText.Text += "\n";
        StatusText.Text += message;
    }

    private void ClearInfo()
    {
        Waveform.Clear();
        FormatText.Text = "Format: -";
        ByteOrderText.Text = "Byte order: -";
        SampleRateText.Text = "Sample rate: -";
        ChannelsText.Text = "Channels: -";
        BitDepthText.Text = "Bit depth: -";
        DurationText.Text = "Duration: -";
        ChunksText.Text = "Chunks: -";
        SwelText.Text = "swel: -";
        SwelFieldsText.Visibility = Visibility.Collapsed;

        _pressure = null;
        NoiseFromBox.IsEnabled = NoiseToBox.IsEnabled = MeasureNoiseButton.IsEnabled = false;
        NoiseFromBox.Text = NoiseToBox.Text = "";
        _noiseMeasurement = null;
        _noiseMessage = null;
        ShowNoiseResult();

        _samples = null;
        _analysedReverbStretch = null;
        ReverbFromBox.IsEnabled = ReverbToBox.IsEnabled = AnalyseReverbButton.IsEnabled = false;
        ReverbFromBox.Text = ReverbToBox.Text = ReverbStatusText.Text = "";
        ReverbTable.ItemsSource = null;
        _reverbResult = null;
        ShowLojResult();
        DecayCurve.Clear();
        UpdateCursorText();
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_playing) StartPlayback(Waveform.CursorSeconds ?? 0, null);
    }

    private void PlaySelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (Waveform.SelectionStartSeconds is double from && Waveform.SelectionEndSeconds is double to && to > from)
            StartPlayback(from, to);
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e) => PausePlayback();

    private void StopButton_Click(object sender, RoutedEventArgs e) => StopPlayback();

    protected override void OnClosed(EventArgs e)
    {
        _playheadTimer.Stop();
        _player.Close();
        DeletePlaybackCopy();
        base.OnClosed(e);
    }
}

/// <summary>
/// One row of the reverberation table: a band's results as text. Only T20 is
/// shown, since the LOJ limits are on T20; C still uses T30, as a check that
/// the decay is straight enough for T20 to describe it.
/// </summary>
public record ReverbRow(string Band, string T20, string DynamicRange,
    string Curvature, string NonLinearity, DecayAnalysis Decay)
{
    public static ReverbRow From(string band, DecayAnalysis decay)
    {
        var culture = CultureInfo.CurrentCulture;
        // A result ISO 3382 wouldn't trust is shown in parentheses.
        string Time(DecayFit? fit, bool reliable) => fit == null ? "–"
            : reliable ? fit.ReverberationTime.ToString("0.00", culture)
            : "(" + fit.ReverberationTime.ToString("0.00", culture) + ")";

        return new ReverbRow(band,
            Time(decay.T20, decay.T20IsReliable),
            decay.Lundeby == null ? "–" : decay.DynamicRangeDb.ToString("0", culture),
            decay.CurvaturePercent is { } c ? (Math.Round(c) + 0.0).ToString("0", culture) : "–",   // + 0.0 turns −0 into 0
            decay.T20?.NonLinearityPermille.ToString("0.0", culture) ?? "–",
            decay);
    }
}
