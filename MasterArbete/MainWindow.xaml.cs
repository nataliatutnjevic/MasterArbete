using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private const double MinReverbSeconds = 0.1;   // SchroederDecay needs at least ten 10 ms intervals

    public MainWindow()
    {
        InitializeComponent();

        // At the end of the recording the player stays at the end, so Play
        // would do nothing. Stop rewinds it, so the next Play starts over.
        _player.MediaEnded += (_, _) => _player.Stop();
    }

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
        }
        catch (Exception ex)
        {
            _currentFilePath = null;
            PlayButton.IsEnabled = false;
            PauseButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            ClearInfo();
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
        NoiseResultText.Text = canMeasure
            ? "Enter the part of the recording that is only background noise, then press Measure."
            : samples == null
                ? "The samples couldn't be read."
                : "This file isn't calibrated (its swel \"view\" isn't in Pa), so levels in dB SPL can't be calculated.";
    }

    private void MeasureNoiseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pressure == null) return;

        if (!TryParseSeconds(NoiseFromBox.Text, out double from) || !TryParseSeconds(NoiseToBox.Text, out double to))
        {
            NoiseResultText.Text = "Enter the start and end in seconds, e.g. 12.5.";
            return;
        }

        double duration = _pressure.Length / (double)_sampleRate;
        from = Math.Clamp(from, 0, duration);
        to = Math.Clamp(to, 0, duration);
        int start = (int)Math.Round(from * _sampleRate);
        int end = (int)Math.Round(to * _sampleRate);
        if (end - start < BackgroundNoise.MinSamples)
        {
            NoiseResultText.Text = $"The part to measure must be at least " +
                $"{BackgroundNoise.MinSamples / (double)_sampleRate:0.00} s long, and end after it starts.";
            return;
        }

        var levels = BackgroundNoise.Measure(_pressure, _sampleRate, start, end - start);
        NoiseResultText.Text = FormatNoiseResult(levels, from, to);
    }

    private static string FormatNoiseResult(NoiseLevels levels, double from, double to)
    {
        var culture = CultureInfo.CurrentCulture;
        var lines = new List<string>
        {
            string.Format(culture, "LAeq  {0,5:0.0} dBA", levels.LAeq),
            string.Format(culture, "LCeq  {0,5:0.0} dBC", levels.LCeq),
            string.Format(culture, "LZeq  {0,5:0.0} dB (unweighted)", levels.LZeq),
            string.Format(culture, "over {0:0.00}–{1:0.00} s ({2:0.00} s)", from, to, levels.DurationSeconds),
            "",
            "LOJ Kravnivå   limit              result",
        };
        foreach (var limit in BackgroundNoise.LojLimits)
        {
            lines.Add(string.Format(culture, "{0,-14} <{1} dBA, <{2} dBC   {3}",
                limit.Level, limit.MaxDbA, limit.MaxDbC, limit.IsMetBy(levels) ? "meets" : "fails"));
        }
        return string.Join("\n", lines);
    }

    // The reverberation time only depends on how fast the level falls, so it
    // works on any recording, calibrated or not.
    private void SetUpReverbAnalysis(float[]? samples)
    {
        _samples = samples;
        bool canAnalyse = samples != null;
        ReverbFromBox.IsEnabled = ReverbToBox.IsEnabled = AnalyseReverbButton.IsEnabled = canAnalyse;
        ReverbFromBox.Text = canAnalyse ? "0" : "";
        ReverbToBox.Text = canAnalyse ? (samples!.Length / (double)_sampleRate).ToString("0.00", CultureInfo.CurrentCulture) : "";
        ReverbStatusText.Text = canAnalyse ? "" : "The samples couldn't be read.";
        ReverbTable.ItemsSource = null;
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
                .Concat(result.Bands.Select(b => ReverbRow.From(b.Band.Name, b.Decay)))
                .ToList();
            _reverbStartSeconds = from;
            ReverbTable.ItemsSource = rows;
            // Keep showing the same band after a new analysis, the 1 kHz band at first.
            ReverbTable.SelectedItem = rows.FirstOrDefault(r => r.Band == selectedBand)
                ?? rows.FirstOrDefault(r => r.Band == "1 kHz") ?? rows[0];

            ReverbStatusText.Text = string.Format(CultureInfo.CurrentCulture, "Analysed {0:0.00}–{1:0.00} s{2}.",
                from, to, timeReversed ? ", time-reversed band filters" : "");
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

    private void ReverbTable_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReverbTable.SelectedItem is ReverbRow row)
            DecayCurve.Show(row.Decay, row.Band, _reverbStartSeconds);
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
        NoiseFromBox.Text = NoiseToBox.Text = NoiseResultText.Text = "";

        _samples = null;
        ReverbFromBox.IsEnabled = ReverbToBox.IsEnabled = AnalyseReverbButton.IsEnabled = false;
        ReverbFromBox.Text = ReverbToBox.Text = ReverbStatusText.Text = "";
        ReverbTable.ItemsSource = null;
        DecayCurve.Clear();
    }

    // Play starts from the current position: the beginning after Stop, or
    // where playback was paused after Pause.
    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFilePath == null) return;
        _player.Play();
    }

    // Pause keeps the position, so the next Play continues from here.
    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _player.Pause();
    }

    // Stop rewinds to the beginning.
    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _player.Stop();
    }

    protected override void OnClosed(EventArgs e)
    {
        _player.Close();
        DeletePlaybackCopy();
        base.OnClosed(e);
    }
}

/// <summary>One row of the reverberation table: a band's results as text.</summary>
public record ReverbRow(string Band, string Edt, string T20, string T30, string DynamicRange,
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
            Time(decay.Edt, true),
            Time(decay.T20, decay.T20IsReliable),
            Time(decay.T30, decay.T30IsReliable),
            decay.Lundeby == null ? "–" : decay.DynamicRangeDb.ToString("0", culture),
            decay.CurvaturePercent is { } c ? (Math.Round(c) + 0.0).ToString("0", culture) : "–",   // + 0.0 turns −0 into 0
            decay.T20?.NonLinearityPermille.ToString("0.0", culture) ?? "–",
            decay);
    }
}
