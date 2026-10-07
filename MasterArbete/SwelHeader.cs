using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MasterArbete;

/// <summary>
/// The "SwellDisplay" value: the stretch of the file to show (start and
/// duration in seconds) and which channels, as a bit mask where bit 0 is
/// the first channel (e.g. 5 = channels 0 and 2).
/// </summary>
public record SwelDisplay(double Start, double Duration, int ChannelMask)
{
    public bool ShowsChannel(int channel) =>
        channel is >= 0 and < 16 && (ChannelMask & (1 << channel)) != 0;
}

/// <summary>
/// Soundswell's file header ("filhuvud"), stored in a WAV file's "swel" chunk.
/// It's plain text: one key=value pair per line (CR LF line endings), ended
/// by a line containing just "=" and then zero padding to the chunk size.
/// Example:
///   msb=last
///   file=wave
///   nchans=1
///   sftot=16000
///   proc0=SwellMPC Record: ...
///   length=365391
///   =
/// Keys and their order vary between files, so every pair is kept, and the
/// known keys are exposed as convenience properties that return null when
/// the key is missing.
/// </summary>
public class SwelHeader
{
    private readonly Dictionary<string, string> _lookup;

    /// <summary>All key=value pairs, in file order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Fields { get; }

    private SwelHeader(List<KeyValuePair<string, string>> fields)
    {
        Fields = fields;
        _lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
            _lookup[field.Key] = field.Value;   // if a key repeats, the last one wins
    }

    /// <summary>The value for a key (case-insensitive), or null if it isn't there.</summary>
    public string? Get(string key) => _lookup.TryGetValue(key, out var value) ? value : null;

    /// <summary>From "msb": "last" = little-endian (false), "first" = big-endian (true).</summary>
    public bool? IsBigEndian => Get("msb")?.ToLowerInvariant() switch
    {
        "last" => false,
        "first" => true,
        _ => null
    };

    /// <summary>From "file": file type, e.g. "samp" or "wave".</summary>
    public string? FileType => Get("file");

    /// <summary>From "head": header size in bytes. Only given when it isn't the usual 1024.</summary>
    public int? HeadSize => int.TryParse(Get("head"), out var n) ? n : null;

    /// <summary>
    /// From "sftot": total sampling frequency in Hz for all channels together.
    /// When every channel has the same rate, each one runs at sftot / nchans.
    /// </summary>
    public int? TotalSampleRate => int.TryParse(Get("sftot"), out var n) ? n : null;

    /// <summary>From "nchans": number of channels.</summary>
    public int? Channels => int.TryParse(Get("nchans"), out var n) ? n : null;

    /// <summary>From "length": number of samples (per channel).</summary>
    public long? Length => long.TryParse(Get("length"), out var n) ? n : null;

    /// <summary>From "data": sample type, e.g. "int16". Not present in every file.</summary>
    public string? DataType => Get("data");

    /// <summary>From "range": the lowest and highest sample value to display.</summary>
    public (double Min, double Max)? Range => GetPair("range");

    /// <summary>From "view": the values shown at the bottom and top of the y-axis.</summary>
    public (double Min, double Max)? View => GetPair("view");

    /// <summary>
    /// From "scan": the channel sequence. Only used in multichannel files
    /// whose channels have different sampling rates.
    /// </summary>
    public IReadOnlyList<int>? Scan
    {
        get
        {
            double[]? numbers = GetNumbers("scan");
            return numbers == null ? null : Array.ConvertAll(numbers, n => (int)n);
        }
    }

    /// <summary>
    /// From "synch": true if the channels were sampled at the same instant,
    /// false if they were sampled one at a time.
    /// </summary>
    public bool? IsSynchronous => Get("synch") switch
    {
        "1" => true,
        "0" => false,
        _ => null
    };

    /// <summary>
    /// From "proc0", "proc1", ...: how the file was created, followed by each
    /// time it was processed (usually the command line of the program used).
    /// </summary>
    public IReadOnlyList<string> ProcessingHistory
    {
        get
        {
            var steps = new List<string>();
            while (Get($"proc{steps.Count}") is string step)
                steps.Add(step);
            return steps;
        }
    }

    /// <summary>From "SwellDisplay": the part of the file and the channels to show.</summary>
    public SwelDisplay? Display =>
        GetNumbers("SwellDisplay") is { Length: 3 } n ? new SwelDisplay(n[0], n[1], (int)n[2]) : null;

    // In multichannel files a channel can have its own settings: the key
    // with the channel number added to it. The first channel is 0.

    /// <summary>From "channelX": descriptive title for a channel.</summary>
    public string? GetChannelTitle(int channel) => Get($"channel{channel}");

    /// <summary>From "rangeX", or "range" if the channel doesn't have its own.</summary>
    public (double Min, double Max)? GetRange(int channel) => GetPair($"range{channel}") ?? Range;

    /// <summary>From "viewX", or "view" if the channel doesn't have its own.</summary>
    public (double Min, double Max)? GetView(int channel) => GetPair($"view{channel}") ?? View;

    // A "min,max" value as two numbers, or null if it isn't exactly that.
    private (double Min, double Max)? GetPair(string key) =>
        GetNumbers(key) is { Length: 2 } n ? (n[0], n[1]) : null;

    // A comma-separated value as numbers, or null if the key is missing or
    // any part isn't a number. Invariant culture so "2.5" is read the same
    // way on a Swedish system, where the decimal separator is a comma.
    private double[]? GetNumbers(string key)
    {
        string? value = Get(key);
        if (value == null) return null;

        string[] parts = value.Split(',');
        var numbers = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
                return null;
        }
        return numbers;
    }
    //SwelHeader takes the raw bytes from the swel chunk in the wav file and converts them into readable info, organized as key value  pairs.
    public static SwelHeader Parse(byte[] bytes)
    {
        // The text ends at the first zero byte; what follows is padding.
        int end = Array.IndexOf(bytes, (byte)0);
        if (end < 0) end = bytes.Length;

        // Latin-1 maps every byte to a character, so decoding never fails
        // and Swedish letters (å, ä, ö) come through if Soundswell writes any.
        string text = Encoding.Latin1.GetString(bytes, 0, end);

        var fields = new List<KeyValuePair<string, string>>();
        foreach (string line in text.Split('\n'))
        {
            // Split at the first '=' only: values may contain spaces, commas,
            // colons and so on. Lines with no key (the closing "=") are skipped.
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();   // Trim also removes the '\r' of CR LF
            if (key.Length > 0)
                fields.Add(new KeyValuePair<string, string>(key, value));
        }

        return new SwelHeader(fields);
    }
}
