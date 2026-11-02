# MasterArbete — WAV file prototype

A minimal Windows desktop app (WPF, .NET 8) for the first step of the project:
pick a `.wav` file, see its basic format info, and play it back.

## What it does

- **Open WAV file...** — opens a file picker, reads the file's `RIFF`/`WAVE`
  header (sample rate, channel count, bit depth, format, and computed
  duration) and displays it.
- **Play / Stop** — plays the selected file using `MediaPlayer`.

## Project layout

```
MasterArbete.Analysis/       # the calculations, no user interface (net8.0)
  WavFileReader.cs           # RIFF/WAVE chunk parser (WavInfo + WavFileReader)
  SwelHeader.cs              # parser for Soundswell's text header in the "swel" chunk
  PressureCalibration.cs     # sample values to pascals from the swel header
  BackgroundNoise.cs, Fft.cs # background noise levels against the LOJ limits
  SchroederDecay.cs          # Schroeder decay curve, EDT, T20, T30
  Lundeby.cs                 # where the decay meets the background noise
  OctaveBandFilter.cs        # octave-band filters
  ReverberationAnalysis.cs   # reverberation time per octave band
MasterArbete/                # WPF app (net8.0-windows)
  MasterArbete.slnx          # the solution, with all three projects
  App.xaml / .cs             # application entry point
  MainWindow.xaml / .cs      # the window: file info, background noise, reverberation time
  WaveformView.cs            # waveform plot
  DecayCurveView.cs          # decay curve plot
MasterArbete.Tests/          # xUnit tests of the calculations (net8.0)
```

`MasterArbete.Analysis` has no dependency on WPF or Windows. That's
deliberate: it's the part that could be built into other programs later,
e.g. Soundswell, while the WPF app is only one user interface for it.

## Building and running

The app needs to be built on Windows, since WPF is Windows-only (the
analysis library and the tests also build on macOS and Linux). With the
[.NET 8 SDK](https://dotnet.microsoft.com/download) installed:

```powershell
cd MasterArbete
dotnet run
```

Or open `MasterArbete/MasterArbete.slnx` in Visual Studio and press F5.
Run the tests with `dotnet test MasterArbete.Tests`.

## Next steps

Some natural follow-ups once this is running:

- Show a waveform preview instead of just header info.
- Support dragging a file onto the window, not just the file picker.
- Swap `MediaPlayer` for a lower-level API if you need sample-accurate
  playback control later.
