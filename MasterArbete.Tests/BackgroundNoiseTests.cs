using Xunit;

namespace MasterArbete.Tests;

public class BackgroundNoiseTests
{
    private const int SampleRate = 16000;

    // 1 Pa RMS is 93.98 dB SPL (20·log10(1 / 20 µPa)).
    private const double OnePascalDb = 93.979;

    private static float[] Sine(double frequency, double rmsPascal, double seconds, int sampleRate = SampleRate, double offset = 0)
    {
        var samples = new float[(int)(seconds * sampleRate)];
        double amplitude = rmsPascal * Math.Sqrt(2);
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)(offset + amplitude * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
        return samples;
    }

    // Gaussian white noise with the given RMS, the same every run.
    private static float[] WhiteNoise(double rmsPascal, double seconds, int seed = 1)
    {
        var random = new Random(seed);
        var samples = new float[(int)(seconds * SampleRate)];
        for (int i = 0; i < samples.Length; i++)
        {
            double u1 = 1 - random.NextDouble(), u2 = random.NextDouble();
            samples[i] = (float)(rmsPascal * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }
        return samples;
    }

    // The level the samples actually have, straight from their mean square.
    private static double Db(float[] samples)
    {
        double sum = 0;
        foreach (float s in samples) sum += (double)s * s;
        return 10 * Math.Log10(sum / samples.Length / (20e-6 * 20e-6));
    }

    private static NoiseLevels MeasureAll(float[] pascal, int sampleRate = SampleRate) =>
        BackgroundNoise.Measure(pascal, sampleRate, 0, pascal.Length);

    // IEC 61672-1 Table 3, at the exact octave frequencies 1000·10^(0.3·k); the table is rounded to 0.1 dB.
    [Theory]
    [InlineData(-5, -39.4, -3.0)]   // 31.5 Hz
    [InlineData(-4, -26.2, -0.8)]   // 63 Hz
    [InlineData(-3, -16.1, -0.2)]   // 125 Hz
    [InlineData(-2, -8.6, 0.0)]     // 250 Hz
    [InlineData(-1, -3.2, 0.0)]     // 500 Hz
    [InlineData(0, 0.0, 0.0)]       // 1 kHz
    [InlineData(1, 1.2, -0.2)]      // 2 kHz
    [InlineData(2, 1.0, -0.8)]      // 4 kHz
    [InlineData(3, -1.1, -3.0)]     // 8 kHz
    [InlineData(4, -6.6, -8.5)]     // 16 kHz
    public void The_weighting_curves_match_the_IEC_61672_1_table(int octave, double expectedA, double expectedC)
    {
        double f = 1000 * Math.Pow(10, 0.3 * octave);
        Assert.Equal(expectedA, FrequencyWeighting.A(f), 0.051);
        Assert.Equal(expectedC, FrequencyWeighting.C(f), 0.051);
    }

    [Fact]
    public void Both_weightings_are_0_dB_at_1_kHz()
    {
        Assert.Equal(0, FrequencyWeighting.A(1000), 0.005);
        Assert.Equal(0, FrequencyWeighting.C(1000), 0.005);
    }

    [Fact]
    public void A_1_kHz_tone_of_1_Pa_is_94_dB_in_all_weightings()
    {
        // What a 94 dB calibrator gives.
        var levels = MeasureAll(Sine(1000, 1, seconds: 5));
        Assert.Equal(OnePascalDb, levels.LZeq, 0.02);
        Assert.Equal(OnePascalDb, levels.LAeq, 0.02);
        Assert.Equal(OnePascalDb, levels.LCeq, 0.02);
    }

    [Theory]
    [InlineData(1003.7)]
    [InlineData(437.1)]
    [InlineData(2961.3)]
    public void A_tone_between_frequency_bins_keeps_its_level(double frequency)
    {
        var levels = MeasureAll(Sine(frequency, 1, seconds: 5));
        Assert.Equal(OnePascalDb, levels.LZeq, 0.05);
    }

    [Theory]
    [InlineData(125)]
    [InlineData(250)]
    [InlineData(4000)]
    public void A_tone_is_weighted_by_the_curves_at_its_frequency(double frequency)
    {
        var levels = MeasureAll(Sine(frequency, 1, seconds: 5));
        Assert.Equal(OnePascalDb + FrequencyWeighting.A(frequency), levels.LAeq, 0.05);
        Assert.Equal(OnePascalDb + FrequencyWeighting.C(frequency), levels.LCeq, 0.05);
    }

    [Fact]
    public void A_tone_of_114_dB_is_20_dB_above_one_of_94_dB()
    {
        // The other level calibrators give: 10 Pa RMS.
        double at94 = MeasureAll(Sine(1000, 1, seconds: 5)).LAeq;
        double at114 = MeasureAll(Sine(1000, 10, seconds: 5)).LAeq;
        Assert.Equal(20, at114 - at94, 0.01);
    }

    [Fact]
    public void Quiet_levels_like_the_Kravniva_3_limit_are_measured_correctly()
    {
        // 24 dB at 1 kHz is 0.000317 Pa RMS, far below anything that rounds away in a float.
        double rms = 20e-6 * Math.Pow(10, 24.0 / 20);
        var levels = MeasureAll(Sine(1000, rms, seconds: 5));
        Assert.Equal(24, levels.LAeq, 0.02);
    }

    [Fact]
    public void White_noise_gets_its_true_unweighted_level()
    {
        var noise = WhiteNoise(0.01, seconds: 10);
        Assert.Equal(Db(noise), MeasureAll(noise).LZeq, 0.05);
    }

    [Fact]
    public void White_noise_is_weighted_by_the_average_of_the_curves_up_to_half_the_sample_rate()
    {
        var noise = WhiteNoise(0.01, seconds: 10);
        var levels = MeasureAll(noise);

        // White noise has the same power at every frequency, so weighting it
        // scales the power by the curve's average over 0 to fs/2.
        double meanA = 0, meanC = 0;
        const int steps = 100000;
        for (int i = 1; i <= steps; i++)
        {
            double f = (i - 0.5) * (SampleRate / 2.0) / steps;
            meanA += Math.Pow(10, FrequencyWeighting.A(f) / 10) / steps;
            meanC += Math.Pow(10, FrequencyWeighting.C(f) / 10) / steps;
        }
        Assert.Equal(levels.LZeq + 10 * Math.Log10(meanA), levels.LAeq, 0.1);
        Assert.Equal(levels.LZeq + 10 * Math.Log10(meanC), levels.LCeq, 0.1);
    }

    [Fact]
    public void Low_frequency_noise_counts_much_more_in_C_than_in_A()
    {
        // Ventilation-like rumble at 63 Hz: LOJ's dB(C) limit is there to catch this.
        var levels = MeasureAll(Sine(63, 0.01, seconds: 5));
        Assert.True(levels.LCeq - levels.LAeq > 25, $"LCeq − LAeq = {levels.LCeq - levels.LAeq:0.0} dB");
    }

    [Fact]
    public void A_DC_offset_does_not_change_the_levels()
    {
        var clean = MeasureAll(Sine(1000, 0.01, seconds: 2));
        var offset = MeasureAll(Sine(1000, 0.01, seconds: 2, offset: 0.5));
        Assert.Equal(clean.LZeq, offset.LZeq, 0.01);
        Assert.Equal(clean.LAeq, offset.LAeq, 0.01);
        Assert.Equal(clean.LCeq, offset.LCeq, 0.01);
    }

    [Fact]
    public void Silence_gives_a_very_low_level_not_an_error()
    {
        var levels = MeasureAll(new float[SampleRate]);
        Assert.True(levels.LAeq < -100);
        Assert.False(double.IsNaN(levels.LAeq) || double.IsInfinity(levels.LAeq));
    }

    [Fact]
    public void Only_the_chosen_stretch_is_measured()
    {
        // 1 s of silence, 2 s of a 1 Pa tone, 1 s of a much louder tone.
        var quiet = new float[SampleRate];
        var tone = Sine(1000, 1, seconds: 2);
        var loud = Sine(1000, 100, seconds: 1);
        float[] signal = [.. quiet, .. tone, .. loud];

        var levels = BackgroundNoise.Measure(signal, SampleRate, quiet.Length, tone.Length);
        Assert.Equal(OnePascalDb, levels.LZeq, 0.02);
        Assert.Equal(2.0, levels.DurationSeconds, 1e-9);
    }

    [Fact]
    public void Half_silence_half_tone_is_3_dB_below_the_tone()
    {
        // Leq is an energy average over the whole stretch. 2 × 16384 samples is a
        // whole number of half windows, so no extra window is added at the end.
        float[] signal = [.. new float[16384], .. Sine(1000, 1, seconds: 16384.0 / SampleRate)];
        Assert.Equal(OnePascalDb - 3.01, MeasureAll(signal).LZeq, 0.05);
    }

    [Fact]
    public void A_stretch_that_is_not_a_whole_number_of_half_windows_counts_its_end_a_little_more()
    {
        // Known limitation: the extra window that reaches the end of the stretch
        // counts the last 0.256 s once more. With 2 s of silence then 2 s of tone
        // that adds about 0.1 dB; for steady background noise it makes no difference.
        float[] signal = [.. new float[2 * SampleRate], .. Sine(1000, 1, seconds: 2)];
        double error = MeasureAll(signal).LZeq - (OnePascalDb - 3.01);
        Assert.InRange(error, 0, 0.15);
    }

    [Theory]
    [InlineData(1024)]   // the shortest stretch allowed
    [InlineData(5000)]   // not a whole number of windows
    [InlineData(4096 * 3 + 1)]
    public void Stretches_of_any_length_give_the_right_level(int count)
    {
        var tone = Sine(1000, 1, seconds: 1);
        var levels = BackgroundNoise.Measure(tone, SampleRate, 0, count);
        Assert.Equal(OnePascalDb, levels.LZeq, 0.1);
    }

    [Fact]
    public void Other_sample_rates_work_too()
    {
        var levels = MeasureAll(Sine(1000, 1, seconds: 2, sampleRate: 48000), 48000);
        Assert.Equal(OnePascalDb, levels.LAeq, 0.02);
    }

    [Fact]
    public void Too_short_a_stretch_is_refused()
    {
        var tone = Sine(1000, 1, seconds: 1);
        Assert.Throws<ArgumentException>(() => BackgroundNoise.Measure(tone, SampleRate, 0, BackgroundNoise.MinSamples - 1));
    }

    [Theory]
    [InlineData(-1, 2000)]
    [InlineData(15000, 2000)]
    public void A_stretch_outside_the_recording_is_refused(int start, int count)
    {
        var tone = Sine(1000, 1, seconds: 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => BackgroundNoise.Measure(tone, SampleRate, start, count));
    }

    [Fact]
    public void The_limits_are_those_of_LOJ_R1B()
    {
        Assert.Equal(new[] { (1, 40.0, 60.0), (2, 30.0, 50.0), (3, 24.0, 44.0) },
            BackgroundNoise.LojLimits.Select(l => (l.Level, l.MaxDbA, l.MaxDbC)));
    }

    [Theory]
    [InlineData(23.9, 43.9, true)]
    [InlineData(24.0, 43.9, false)]   // LOJ asks for below the limit
    [InlineData(23.9, 44.0, false)]
    [InlineData(20.0, 50.0, false)]   // quiet in dB(A) but too much low-frequency noise
    [InlineData(30.0, 30.0, false)]
    public void Kravniva_3_needs_both_levels_below_their_limits(double laeq, double lceq, bool expected)
    {
        var level3 = BackgroundNoise.LojLimits.Single(l => l.Level == 3);
        Assert.Equal(expected, level3.IsMetBy(new NoiseLevels(laeq, lceq, LZeq: lceq, DurationSeconds: 60)));
    }
}
