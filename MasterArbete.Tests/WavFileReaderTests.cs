using System.Text;
using Xunit;

namespace MasterArbete.Tests;

public class WavFileReaderTests
{
    // Writes a little-endian WAV file with a WAVE_FORMAT_EXTENSIBLE "fmt " chunk
    // (40 bytes) and the given 24-bit frames, and returns its path.
    private static string WriteExtensible24(int sampleRate, int[][] frames)
    {
        short channels = (short)frames[0].Length;
        const short bits = 24;
        int blockAlign = channels * bits / 8;
        var data = new MemoryStream();
        foreach (var frame in frames)
            foreach (int v in frame)
                data.Write(new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16) });

        string path = Path.Combine(Path.GetTempPath(), $"MasterArbete-test-{Guid.NewGuid():N}.wav");
        using var w = new BinaryWriter(File.Create(path));
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write((uint)(4 + (8 + 40) + (8 + data.Length)));
        w.Write(Encoding.ASCII.GetBytes("WAVE"));

        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(40u);
        w.Write((ushort)0xFFFE);                 // WAVE_FORMAT_EXTENSIBLE
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(sampleRate * blockAlign);
        w.Write((short)blockAlign);
        w.Write(bits);
        w.Write((short)22);                      // cbSize
        w.Write(bits);                           // valid bits per sample
        w.Write(3u);                             // channel mask: front left and right
        w.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());   // KSDATAFORMAT_SUBTYPE_PCM

        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write((uint)data.Length);
        w.Write(data.ToArray());
        return path;
    }

    [Fact]
    public void An_extensible_24_bit_stereo_file_is_read_as_PCM()
    {
        string path = WriteExtensible24(48000, new[]
        {
            new[] { 0, 4194304 },                // 0 and +0.5
            new[] { -4194304, -8388608 },        // −0.5 and −1
            new[] { 8388607, 1 },
        });
        try
        {
            var info = WavFileReader.ReadHeader(path);
            Assert.Equal(1, info.AudioFormat);
            Assert.Equal("PCM (extensible)", info.FormatName);
            Assert.Equal(2, info.Channels);
            Assert.Equal(48000, info.SampleRate);
            Assert.Equal(24, info.BitsPerSample);
            Assert.Equal(8388608, info.FullScale);

            Assert.Equal(new[] { 0f, -0.5f, 8388607 / 8388608f }, WavFileReader.ReadSamples(path, info, channel: 0));
            Assert.Equal(new[] { 0.5f, -1f, 1 / 8388608f }, WavFileReader.ReadSamples(path, info, channel: 1));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
