using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MasterArbete;

/// <summary>
/// One RIFF chunk as found in the file: its 4-character ID, where its
/// data starts (byte offset, just after the 8-byte chunk header) and how
/// many bytes of data the header says it has.
/// </summary>
public record WavChunk(string Id, long Offset, uint Size);

/// <summary>
/// Basic information read from a .wav file's header chunks.
/// Kept as a plain data class so it's easy to reuse later
/// (e.g. when wiring this up to a native audio engine).
/// </summary>
public class WavInfo
{
    public short AudioFormat { get; init; }
    public int SampleRate { get; init; }
    public short Channels { get; init; }
    public short BitsPerSample { get; init; }
    public uint DataSizeBytes { get; init; }
    public string FormatName { get; init; } = "Unknown";

    /// <summary>Every chunk in the file, in the order it appears.</summary>
    public IReadOnlyList<WavChunk> Chunks { get; init; } = Array.Empty<WavChunk>();

    /// <summary>The Soundswell "swel" chunk, or null if the file doesn't have one.</summary>
    public WavChunk? SwelChunk => Chunks.FirstOrDefault(c => WavFileReader.IsSwel(c.Id));

    /// <summary>The parsed Soundswell header from the swel chunk, or null if there is none.</summary>
    public SwelHeader? Swel { get; init; }

    public double DurationSeconds
    {
        get
        {
            int blockAlign = Channels * (BitsPerSample / 8);
            if (blockAlign <= 0 || SampleRate <= 0) return 0;
            return (double)DataSizeBytes / blockAlign / SampleRate;
        }
    }

    /// <summary>
    /// Compares the Soundswell header with the standard WAV information and
    /// returns a message for each mismatch (empty list = all consistent).
    /// Keys missing from the header are skipped, not reported. Mismatches are
    /// warnings, not errors: the file can still be opened and played.
    /// </summary>
    public List<string> CheckSwel()
    {
        var problems = new List<string>();
        if (Swel == null) return problems;

        // This reader only accepts "RIFF" files, which are always little-endian.
        if (Swel.IsBigEndian == true)
            problems.Add("swel says msb=first (big-endian), but the file is RIFF (little-endian).");

        if (Swel.Channels is int ch && ch != Channels)
            problems.Add($"swel says {ch} channel(s), fmt says {Channels}.");

        if (Swel.SampleRate is int sr && sr != SampleRate)
            problems.Add($"swel says sample rate {sr} Hz, fmt says {SampleRate} Hz.");

        if (Swel.DataType is string type && type.Equals("int16", StringComparison.OrdinalIgnoreCase)
            && (AudioFormat != 1 || BitsPerSample != 16))
            problems.Add($"swel says int16 samples, fmt says {FormatName} {BitsPerSample}-bit.");

        int blockAlign = Channels * (BitsPerSample / 8);
        if (Swel.Length is long length && blockAlign > 0 && length != DataSizeBytes / blockAlign)
            problems.Add($"swel says length={length} samples, the data chunk holds {DataSizeBytes / blockAlign}.");

        return problems;
    }
}

/// <summary>
/// Minimal RIFF/WAVE header parser. Reads just enough of the file
/// to report format info without loading the whole file into memory.
/// Supports the common PCM / IEEE float "fmt " chunk layout.
/// </summary>
public static class WavFileReader
{
    // Soundswell writes the chunk ID as lowercase "swel". RIFF IDs are
    // case-sensitive, but accept any casing to be safe.
    internal static bool IsSwel(string chunkId) =>
        string.Equals(chunkId, "swel", StringComparison.OrdinalIgnoreCase);

    // Reads a 4-byte RIFF ID. Uses ReadBytes rather than ReadChars so exactly
    // 4 bytes are consumed even if the bytes aren't valid text (damaged file);
    // non-ASCII bytes simply become '?'.
    private static string ReadId(BinaryReader reader) =>
        Encoding.ASCII.GetString(reader.ReadBytes(4));

    public static WavInfo ReadHeader(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        // RIFF header
        string riffId = ReadId(reader);      // "RIFF"
        reader.ReadUInt32();                                   // overall file size (ignored)
        string waveId = ReadId(reader);      // "WAVE"

        if (riffId != "RIFF" || waveId != "WAVE")
            throw new InvalidDataException("Not a valid WAV file (missing RIFF/WAVE header).");

        int sampleRate = 0;
        short channels = 0;
        short bitsPerSample = 0;
        short audioFormat = 0;
        uint dataSize = 0;
        bool haveFmt = false;
        var chunks = new List<WavChunk>();
        SwelHeader? swel = null;

        // Walk every chunk to the end of the file. Extra chunks such as
        // Soundswell's "swel" can come before or after "data", so we can't
        // stop as soon as "fmt " and "data" have been seen.
        while (stream.Position < stream.Length)
        {
            if (stream.Length - stream.Position < 8) break; // not enough left for a chunk header

            string chunkId = ReadId(reader);
            uint chunkSize = reader.ReadUInt32();
            long chunkDataStart = stream.Position;
            chunks.Add(new WavChunk(chunkId, chunkDataStart, chunkSize));

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
                // Don't read the actual audio data here; just note where/how big it is.
            }
            else if (IsSwel(chunkId))
            {
                // Read the whole chunk (capped at the end of the file in case the size is damaged).
                int length = (int)Math.Min(chunkSize, stream.Length - stream.Position);
                swel = SwelHeader.Parse(reader.ReadBytes(length));
            }

            // Move to the start of the next chunk (chunks are word-aligned).
            // Every pass reads an 8-byte header, so the loop always advances;
            // a size that runs past the end of the file just ends the walk.
            long next = chunkDataStart + chunkSize + (chunkSize % 2);
            stream.Position = Math.Min(next, stream.Length);
        }

        if (!haveFmt)
            throw new InvalidDataException("WAV file has no 'fmt ' chunk.");

        return new WavInfo
        {
            AudioFormat = audioFormat,
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = bitsPerSample,
            DataSizeBytes = dataSize,
            Chunks = chunks,
            Swel = swel,
            FormatName = audioFormat switch
            {
                1 => "PCM",
                3 => "IEEE Float",
                _ => $"Format code {audioFormat}"
            }
        };
    }
}
