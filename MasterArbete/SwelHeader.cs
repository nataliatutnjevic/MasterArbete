using System;
using System.Collections.Generic;
using System.Text;

namespace MasterArbete;

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

    /// <summary>From "sftot": sampling frequency in Hz.</summary>
    public int? SampleRate => int.TryParse(Get("sftot"), out var n) ? n : null;

    /// <summary>From "nchans": number of channels.</summary>
    public int? Channels => int.TryParse(Get("nchans"), out var n) ? n : null;

    /// <summary>From "length": number of samples (per channel).</summary>
    public long? Length => long.TryParse(Get("length"), out var n) ? n : null;

    /// <summary>From "data": sample type, e.g. "int16". Not present in every file.</summary>
    public string? DataType => Get("data");

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
