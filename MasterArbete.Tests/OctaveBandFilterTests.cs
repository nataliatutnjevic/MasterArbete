using Xunit;

namespace MasterArbete.Tests;

public class OctaveBandFilterTests
{
    public static TheoryData<int> SampleRates => new() { 16000, 44100 };

    [Fact]
    public void The_bands_are_those_below_half_the_sample_rate()
    {
        Assert.Equal(new[] { 125.0, 250, 500, 1000, 2000, 4000 }, OctaveBand.For(16000).Select(b => b.NominalHz));
        Assert.Equal(new[] { 125.0, 250, 500, 1000, 2000, 4000, 8000 }, OctaveBand.For(44100).Select(b => b.NominalHz));
    }

    [Fact]
    public void The_band_frequencies_follow_IEC_61260_base_10()
    {
        var k1 = OctaveBand.FromNominal(1000);
        Assert.Equal(1000, k1.CentreHz, 9);
        Assert.Equal(1000 / Math.Pow(10, 0.15), k1.LowerHz, 9);
        Assert.Equal(1000 * Math.Pow(10, 0.15), k1.UpperHz, 9);
        Assert.Equal(251.189, OctaveBand.FromNominal(250).CentreHz, 3);
        Assert.Equal(125.893, OctaveBand.FromNominal(125).CentreHz, 3);
        Assert.Equal(7943.28, OctaveBand.FromNominal(8000).CentreHz, 2);
        Assert.Equal("250 Hz", OctaveBand.FromNominal(250).Name);
        Assert.Equal("2 kHz", OctaveBand.FromNominal(2000).Name);
    }

    [Theory]
    [MemberData(nameof(SampleRates))]
    public void The_gain_is_0_dB_at_the_centre_and_minus_3_dB_at_the_edges(int fs)
    {
        foreach (var band in OctaveBand.For(fs))
        {
            var filter = BandPassFilter.ForBand(band, fs);
            Assert.Equal(0, filter.GainDb(band.CentreHz, fs), 3);
            Assert.Equal(-3.010, filter.GainDb(band.LowerHz, fs), 3);
            Assert.Equal(-3.010, filter.GainDb(band.UpperHz, fs), 3);
        }
    }

    [Theory]
    [MemberData(nameof(SampleRates))]
    public void The_response_is_an_exact_Butterworth_band_pass_after_prewarping(int fs)
    {
        // The bilinear transform maps the analogue response at ω = 2·fs·tan(π·f/fs)
        // onto the digital frequency f, so with the same pre-warping the digital
        // filter must match the analogue formula
        //   |H|² = 1 / (1 + ((ω² − ω0²) / (B·ω))^(2N)),  N = 3.
        foreach (var band in OctaveBand.For(fs))
        {
            var filter = BandPassFilter.ForBand(band, fs);
            double Warp(double f) => 2.0 * fs * Math.Tan(Math.PI * f / fs);
            double w1 = Warp(band.LowerHz), w2 = Warp(band.UpperHz), w0 = Math.Sqrt(w1 * w2), b = w2 - w1;

            for (double f = 20; f < 0.49 * fs; f *= 1.05)
            {
                double w = Warp(f), x = (w * w - w0 * w0) / (b * w);
                double expected = -10 * Math.Log10(1 + Math.Pow(x, 6));
                if (expected < -100) continue;   // too far down to compare in floating point
                Assert.True(Math.Abs(filter.GainDb(f, fs) - expected) < 0.01,
                    $"{band.Name} at {fs} Hz, {f:0} Hz: {filter.GainDb(f, fs):0.000} dB, expected {expected:0.000} dB");
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_sine_keeps_its_level_in_its_band_and_is_attenuated_an_octave_away(bool timeReversed)
    {
        const int fs = 16000;
        var band = OctaveBand.FromNominal(1000);
        var filter = BandPassFilter.ForBand(band, fs);
        double Rms(double f)
        {
            var sine = Enumerable.Range(0, 2 * fs).Select(i => Math.Sin(2 * Math.PI * f * i / fs)).ToArray();
            var y = filter.Apply(sine, timeReversed);
            return Math.Sqrt(y.Skip(fs / 2).Take(fs).Average(v => v * v) * 2);   // the middle, away from both ends
        }

        Assert.Equal(0, 20 * Math.Log10(Rms(band.CentreHz)), 2);
        Assert.Equal(filter.GainDb(2 * band.CentreHz, fs), 20 * Math.Log10(Rms(2 * band.CentreHz)), 1);
        Assert.True(20 * Math.Log10(Rms(2 * band.CentreHz)) < -19, "an octave above");
        Assert.True(20 * Math.Log10(Rms(band.CentreHz / 2)) < -19, "an octave below");
    }

    [Fact]
    public void The_narrowest_band_is_stable_at_a_high_sample_rate()
    {
        const int fs = 48000;
        var filter = BandPassFilter.ForBand(OctaveBand.FromNominal(125), fs);
        var impulse = new double[2 * fs];
        impulse[0] = 1;
        var y = filter.Apply(impulse, timeReversed: false);
        Assert.True(y.Skip(fs).Max(Math.Abs) < 1e-9, "the impulse response has died out after 1 s");
    }

    [Fact]
    public void Time_reversed_filtering_puts_the_ringing_before_the_impulse()
    {
        const int fs = 16000;
        var filter = BandPassFilter.ForBand(OctaveBand.FromNominal(125), fs);
        var impulse = new double[fs];
        impulse[fs / 2] = 1;

        double Energy(double[] y, int from, int to) => y[from..to].Sum(v => v * v);
        var forward = filter.Apply(impulse, timeReversed: false);
        var reversed = filter.Apply(impulse, timeReversed: true);

        Assert.Equal(0, Energy(forward, 0, fs / 2), 12);              // causal: nothing before the impulse
        Assert.Equal(0, Energy(reversed, fs / 2 + 1, fs), 12);        // reversed: nothing after it
        Assert.Equal(Energy(forward, 0, fs), Energy(reversed, 0, fs), 9);
    }
}
