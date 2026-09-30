using System;
using System.IO;

namespace MasterArbete;

/// <summary>
/// Basic information read from a .wav file's header chunks.
/// Kept as a plain data class so it's easy to reuse later
/// (e.g. when wiring this up to a native audio engine).
/// </summary>
public class WavInfo
{
    public int SampleRate { get; init; }
    public short Channels { get; init; }
    public short BitsPerSample { get; init; }
    public uint DataSizeBytes { get; init; }
    public string FormatName { get; init; } = "Unknown";

    public double DurationSeconds
    {
        get
        {
            int blockAlign = Channels * (BitsPerSample / 8);
            if (blockAlign <= 0 || SampleRate <= 0) return 0;
            return (double)DataSizeBytes / blockAlign / SampleRate;
        }
    }
}

/// <summary>
/// Minimal RIFF/WAVE header parser. Reads just enough of the file
/// to report format info without loading the whole file into memory.
/// Supports the common PCM / IEEE float "fmt " chunk layout.
/// </summary>
public static class WavFileReader
{
    public static WavInfo ReadHeader(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        // RIFF header
        string riffId = new string(reader.ReadChars(4));      // "RIFF"
        reader.ReadUInt32();                                   // overall file size (ignored)
        string waveId = new string(reader.ReadChars(4));      // "WAVE"

        if (riffId != "RIFF" || waveId != "WAVE")
            throw new InvalidDataException("Not a valid WAV file (missing RIFF/WAVE header).");

        int sampleRate = 0;
        short channels = 0;
        short bitsPerSample = 0;
        short audioFormat = 0;
        uint dataSize = 0;
        bool haveFmt = false;
        bool haveData = false;

        // Walk the chunks until we've found "fmt " and "data", or hit EOF.
        while (stream.Position < stream.Length && !(haveFmt && haveData))
        {
            if (stream.Length - stream.Position < 8) break; // not enough left for a chunk header

            string chunkId = new string(reader.ReadChars(4));
            uint chunkSize = reader.ReadUInt32();
            long chunkDataStart = stream.Position;

            if (chunkId == "fmt ")
            {
                audioFormat = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();          // byte rate (ignored)
                reader.ReadInt16();          // block align (ignored)
                bitsPerSample = reader.ReadInt16();
                haveFmt = true;
            }
            else if (chunkId == "data")
            {
                dataSize = chunkSize;
                haveData = true;
                // Don't read the actual audio data here; just note where/how big it is.
            }

            // Move to the start of the next chunk (chunks are word-aligned).
            long next = chunkDataStart + chunkSize + (chunkSize % 2);
            if (next <= stream.Position) break; // safety net against malformed files
            stream.Position = Math.Min(next, stream.Length);
        }

        if (!haveFmt)
            throw new InvalidDataException("WAV file has no 'fmt ' chunk.");

        return new WavInfo
        {
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = bitsPerSample,
            DataSizeBytes = dataSize,
            FormatName = audioFormat switch
            {
                1 => "PCM",
                3 => "IEEE Float",
                _ => $"Format code {audioFormat}"
            }
        };
    }
}
