using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace MasterArbete;

public partial class MainWindow : Window
{
    // MediaPlayer (instead of System.Media.SoundPlayer) so playback isn't
    // limited to uncompressed PCM if this ever needs to handle more formats.
    private readonly MediaPlayer _player = new();
    private string? _currentFilePath;

    public MainWindow()
    {
        InitializeComponent();
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
            SampleRateText.Text = $"Sample rate: {info.SampleRate} Hz";
            ChannelsText.Text = $"Channels: {info.Channels}";
            BitDepthText.Text = $"Bit depth: {info.BitsPerSample}-bit";
            DurationText.Text = $"Duration: {info.DurationSeconds:0.00} s";
            ChunksText.Text = "Chunks: " + string.Join(", ",
                info.Chunks.Select(c => $"{c.Id.TrimEnd()} ({c.Size:N0} B)"));
            ShowSwelInfo(info);
            ShowWaveform(path, info);

            _player.Open(new Uri(path));
            PlayButton.IsEnabled = true;
            StopButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _currentFilePath = null;
            PlayButton.IsEnabled = false;
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
    private void ShowWaveform(string path, WavInfo info)
    {
        try
        {
            Waveform.SetSamples(WavFileReader.ReadSamples(path, info), info.SampleRate);
        }
        catch (Exception ex)
        {
            Waveform.Clear();
            if (StatusText.Text.Length > 0) StatusText.Text += "\n";
            StatusText.Text += $"Couldn't draw the waveform: {ex.Message}";
        }
    }

    private void ClearInfo()
    {
        Waveform.Clear();
        FormatText.Text = "Format: -";
        SampleRateText.Text = "Sample rate: -";
        ChannelsText.Text = "Channels: -";
        BitDepthText.Text = "Bit depth: -";
        DurationText.Text = "Duration: -";
        ChunksText.Text = "Chunks: -";
        SwelText.Text = "swel: -";
        SwelFieldsText.Visibility = Visibility.Collapsed;
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFilePath == null) return;
        _player.Play();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _player.Stop();
    }

    protected override void OnClosed(EventArgs e)
    {
        _player.Close();
        base.OnClosed(e);
    }
}
