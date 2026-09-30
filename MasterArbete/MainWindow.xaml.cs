using System;
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

            _player.Open(new Uri(path));
            PlayButton.IsEnabled = true;
            StopButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _currentFilePath = null;
            PlayButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            StatusText.Text = $"Couldn't read this file: {ex.Message}";
        }
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
