using System;
using System.Buffers.Binary;
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

    /// <summary>
    /// True for a "RIFX" file, where every number and sample is stored
    /// big-endian (most significant byte first); false for a normal "RIFF" file.
    /// </summary>
    public bool IsBigEndian { get; init; }

    /// <summary>
    /// The raw sample value that counts as full scale (1.0 once converted to
    /// float): 32768 for 16-bit PCM, 1 for IEEE float.
    /// </summary>
    public double FullScale => AudioFormat == 3 ? 1 : Math.Pow(2, BitsPerSample - 1);

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
        //creates an empty list 
        var problems = new List<string>();
        //Checks whether the WAV file contains parsed Soundswell metadata, otherwise
        if (Swel == null) return problems;

        // The byte order in swel should match the file's: RIFF is little-endian, RIFX big-endian.
        if (Swel.IsBigEndian is bool swelBigEndian && swelBigEndian != IsBigEndian)
            problems.Add(swelBigEndian
                ? "swel says msb=first (big-endian), but the file is RIFF (little-endian)."
                : "swel says msb=last (little-endian), but the file is RIFX (big-endian).");

        if (Swel.Channels is int ch && ch != Channels)
            problems.Add($"swel says {ch} channel(s), fmt says {Channels}.");

        // sftot is the total rate for all channels together, while fmt stores
        // the rate of one channel, so compare per channel.
        int swelChannels = Swel.Channels ?? Channels;
        if (Swel.TotalSampleRate is int total && swelChannels > 0 && total != (long)SampleRate * swelChannels)
            problems.Add($"swel says sample rate {total / swelChannels} Hz per channel " +
                         $"(sftot={total} over {swelChannels} channel(s)), fmt says {SampleRate} Hz.");

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

    /// <summary>
    /// Reads one channel of the audio in the "data" chunk, converted to
    /// floats in the range -1..1 (full scale), one value per sample.
    /// Supports PCM 8/16/24/32-bit and 32-bit IEEE float.
    /// </summary>
    public static float[] ReadSamples(string path, WavInfo info, int channel = 0)
    {
        var dataChunk = info.Chunks.FirstOrDefault(c => c.Id == "data")
            ?? throw new InvalidDataException("WAV file has no 'data' chunk.");

        // A RIFX file stores each sample's bytes in the opposite order (big-endian).
        Func<byte[], int, float> decode = (info.AudioFormat, info.BitsPerSample, info.IsBigEndian) switch
        {
            (1, 8, _)      => (b, i) => (b[i] - 128) / 128f,                     // 8-bit PCM is unsigned; one byte, so no byte order
            (1, 16, false) => (b, i) => BitConverter.ToInt16(b, i) / 32768f,
            (1, 16, true)  => (b, i) => BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(i)) / 32768f,
            (1, 24, false) => (b, i) => ((b[i] | b[i + 1] << 8 | (sbyte)b[i + 2] << 16)) / 8388608f,
            (1, 24, true)  => (b, i) => (((sbyte)b[i] << 16 | b[i + 1] << 8 | b[i + 2])) / 8388608f,
            (1, 32, false) => (b, i) => BitConverter.ToInt32(b, i) / 2147483648f,
            (1, 32, true)  => (b, i) => BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(i)) / 2147483648f,
            (3, 32, false) => (b, i) => BitConverter.ToSingle(b, i),
            (3, 32, true)  => (b, i) => BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(i)),
            _ => throw new NotSupportedException(
                $"Can't read samples of this type yet ({info.FormatName}, {info.BitsPerSample}-bit).")
        };

        int bytesPerSample = info.BitsPerSample / 8;
        int blockAlign = info.Channels * bytesPerSample;   // bytes per frame (one sample from every channel)
        if (blockAlign <= 0 || channel < 0 || channel >= info.Channels)
            throw new InvalidDataException("Invalid channel count in 'fmt ' chunk.");

        using var stream = File.OpenRead(path);
        stream.Position = dataChunk.Offset;

        // Only whole frames; the file may be shorter than the data chunk claims.
        long available = Math.Min(dataChunk.Size, stream.Length - dataChunk.Offset);
        int frames = (int)(available / blockAlign);
        var bytes = new byte[frames * blockAlign];
        stream.ReadExactly(bytes);

        var samples = new float[frames];
        int channelOffset = channel * bytesPerSample;
        for (int i = 0; i < frames; i++)
            samples[i] = decode(bytes, i * blockAlign + channelOffset);

        return samples;
    }

    /// <summary>
    /// Writes the audio as a normal little-endian ("RIFF") WAV file with just
    /// a "fmt " and a "data" chunk, swapping the bytes of every sample if the
    /// source is RIFX. Used to play RIFX files, which Windows can't play.
    /// </summary>
    public static void WriteLittleEndianCopy(string path, WavInfo info, string destination)
    {
        var dataChunk = info.Chunks.FirstOrDefault(c => c.Id == "data")
            ?? throw new InvalidDataException("WAV file has no 'data' chunk.");

        int bytesPerSample = info.BitsPerSample / 8;
        int blockAlign = info.Channels * bytesPerSample;
        if (blockAlign <= 0 || info.BitsPerSample % 8 != 0)
            throw new InvalidDataException("Invalid channel count or bit depth in 'fmt ' chunk.");

        // Same rule as ReadSamples: only whole frames that are really in the file.
        byte[] data;
        using (var source = File.OpenRead(path))
        {
            source.Position = dataChunk.Offset;
            long available = Math.Min(dataChunk.Size, source.Length - dataChunk.Offset);
            data = new byte[available / blockAlign * blockAlign];
            source.ReadExactly(data);
        }

        if (info.IsBigEndian && bytesPerSample > 1)
        {
            for (int i = 0; i < data.Length; i += bytesPerSample)
                Array.Reverse(data, i, bytesPerSample);
        }

        // BinaryWriter always writes little-endian.
        using var writer = new BinaryWriter(File.Create(destination));
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write((uint)(4 + (8 + 16) + (8 + data.Length + data.Length % 2)));   // file size after this field
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16u);
        writer.Write(info.AudioFormat);
        writer.Write(info.Channels);
        writer.Write(info.SampleRate);
        writer.Write(info.SampleRate * blockAlign);                // byte rate
        writer.Write((short)blockAlign);
        writer.Write(info.BitsPerSample);

        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write((uint)data.Length);
        writer.Write(data);
        if (data.Length % 2 == 1) writer.Write((byte)0);           // chunks are word-aligned
    }

    public static WavInfo ReadHeader(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        // RIFF header
        string riffId = ReadId(reader);      // "RIFF", or "RIFX" for a big-endian file
        reader.ReadUInt32();                                   // overall file size (ignored)
        string waveId = ReadId(reader);      // "WAVE"

        if ((riffId != "RIFF" && riffId != "RIFX") || waveId != "WAVE")
            throw new InvalidDataException("Not a valid WAV file (missing RIFF/RIFX and WAVE header).");

        // BinaryReader always reads little-endian, so in a RIFX file every
        // number has to have its bytes swapped. The 4-letter IDs are text and
        // look the same in both.
        bool bigEndian = riffId == "RIFX";
        short ReadInt16() => bigEndian ? BinaryPrimitives.ReverseEndianness(reader.ReadInt16()) : reader.ReadInt16();
        int ReadInt32() => bigEndian ? BinaryPrimitives.ReverseEndianness(reader.ReadInt32()) : reader.ReadInt32();
        uint ReadUInt32() => bigEndian ? BinaryPrimitives.ReverseEndianness(reader.ReadUInt32()) : reader.ReadUInt32();

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
            if (stream.Length - stream.Position < 8) break; // not enough left for a chunk header, prevents the program from trying to read beyond the end of the file.

            string chunkId = ReadId(reader);
            uint chunkSize = ReadUInt32();
            long chunkDataStart = stream.Position;
            chunks.Add(new WavChunk(chunkId, chunkDataStart, chunkSize));

            if (chunkId == "fmt ")
            {
                audioFormat = ReadInt16();
                channels = ReadInt16();
                sampleRate = ReadInt32();
                ReadInt32();                 // byte rate (ignored)
                ReadInt16();                 // block align (ignored)
                bitsPerSample = ReadInt16();
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
            IsBigEndian = bigEndian,
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
