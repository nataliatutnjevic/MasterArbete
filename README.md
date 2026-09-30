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
MasterArbete/
  MasterArbete.csproj   # WPF app, targets net8.0-windows
  App.xaml / .cs        # application entry point
  MainWindow.xaml / .cs  # the single window: browse, info panel, play/stop
  WavFileReader.cs      # standalone RIFF/WAVE header parser (WavInfo + WavFileReader)
```

`WavFileReader.cs` has no dependency on the UI — it just takes a file path and
returns a `WavInfo`. That's deliberate: it's the piece most likely to be
reused or replaced later (e.g. if this gets wired up to a native audio
engine such as Soundswell), so it's kept separate from the WPF-specific code.

## Building and running

This needs to be built on Windows — WPF is Windows-only, so it won't build
here in this Linux environment. On your Windows machine, with the
[.NET 8 SDK](https://dotnet.microsoft.com/download) installed:

```powershell
cd MasterArbete
dotnet run
```

Or open `MasterArbete.csproj` in Visual Studio and press F5.

## Next steps

Some natural follow-ups once this is running:

- Show a waveform preview instead of just header info.
- Support dragging a file onto the window, not just the file picker.
- Swap `MediaPlayer` for a lower-level API if you need sample-accurate
  playback control later.
