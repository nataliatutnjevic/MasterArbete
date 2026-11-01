using Xunit;

namespace MasterArbete.Tests;

public class ReverberationAnalysisTests
{
    private const int Rate = SyntheticDecay.SampleRate;

    // In an octave band a decay scatters much more than broadband: the band's
    // envelope only changes about every 1/B seconds, so few independent values
    // fall in the fit range. The scatter grows for low bands and short T, so
    // these tests average over many decays and allow more in the low bands.
    private static Dictionary<double, (double Bias, double Spread)> Scatter(
        Func<int, float[]> generate, Func<double, double> expectedT, Func<DecayAnalysis, DecayFit?> fit,
        int seeds, bool timeReversed = true)
    {
        var errors = new Dictionary<double, List<double>>();
        for (int seed = 1; seed <= seeds; seed++)
        {
            var h = generate(seed);
            foreach (var band in ReverberationAnalysis.Analyse(h, Rate, 0, h.Length, timeReversed).Bands)
            {
                double nominal = band.Band.NominalHz;
                if (double.IsNaN(expectedT(nominal))) continue;   // a band that isn't checked
                if (!errors.ContainsKey(nominal)) errors[nominal] = new();
                var result = fit(band.Decay);
                Assert.True(result != null, $"{nominal} Hz, seed {seed}: no result");
                errors[nominal].Add(result!.ReverberationTime / expectedT(nominal) - 1);
            }
        }
        return errors.ToDictionary(e => e.Key, e =>
        {
            double mean = e.Value.Average();
            return (mean, Math.Sqrt(e.Value.Average(v => (v - mean) * (v - mean))));
        });
    }

    [Fact]
    public void All_bands_and_the_broadband_result_are_reported()
    {
        var h = SyntheticDecay.Generate(0.3, lengthSeconds: 1, noiseBelowPeakDb: 60);
        var result = ReverberationAnalysis.Analyse(h, Rate, 0, h.Length);

        Assert.Equal(new[] { 125.0, 250, 500, 1000, 2000, 4000 }, result.Bands.Select(b => b.Band.NominalHz));
        Assert.True(result.TimeReversedFiltering);
        Assert.Equal(0.3, result.Broadband.T20!.ReverberationTime, 1);
    }

    [Fact]
    public void A_decay_that_is_the_same_at_all_frequencies_gives_the_same_T_in_every_band()
    {
        const double t = 0.3;
        var scatter = Scatter(seed => SyntheticDecay.Generate(t, lengthSeconds: 1, noiseBelowPeakDb: 60, seed: seed),
            _ => t, d => d.T20, seeds: 50);

        foreach (var (nominal, (bias, spread)) in scatter)
        {
            double maxBias = nominal <= 125 ? 0.12 : nominal <= 250 ? 0.07 : 0.03;
            Assert.True(Math.Abs(bias) <= maxBias,
                $"{nominal} Hz: mean error {bias:+0.0%;-0.0%} (max ±{maxBias:0%}), scatter {spread:0.0%}");
        }
    }

    [Fact]
    public void Each_band_gets_its_own_reverberation_time()
    {
        // Bands with content are two octaves apart, so their neighbours' filters hardly overlap.
        var times = new Dictionary<double, double> { [250] = 0.6, [1000] = 0.2, [4000] = 0.4 };
        var scatter = Scatter(seed => SyntheticDecay.PerBand(times, lengthSeconds: 2, noiseBelowPeakDb: 70, seed: seed),
            nominal => times.GetValueOrDefault(nominal, double.NaN), d => d.T20, seeds: 20);

        foreach (var (nominal, t) in times)
        {
            var (bias, spread) = scatter[nominal];
            Assert.True(Math.Abs(bias) <= 0.08, $"{nominal} Hz, T = {t} s: mean error {bias:+0.0%;-0.0%}, scatter {spread:0.0%}");
        }
    }

    [Fact]
    public void Time_reversed_filtering_keeps_the_filter_ringing_out_of_short_decays()
    {
        // T = 0.05 s in the 125 Hz band: B·T ≈ 4.4, far below the B·T > 16 that
        // forward filtering needs. Forward, the filter's own ringing makes the
        // early decay far too slow; time-reversed it comes before the impulse.
        const double t = 0.05;
        var forward = Scatter(Float, _ => t, d => d.Edt, seeds: 50, timeReversed: false)[125];
        var reversed = Scatter(Float, _ => t, d => d.Edt, seeds: 50, timeReversed: true)[125];

        Assert.True(forward.Bias > 0.6, $"forward: EDT {forward.Bias:+0%} too long");
        Assert.True(reversed.Bias < 0.3, $"time-reversed: EDT {reversed.Bias:+0%} too long");

        static float[] Float(int seed) => SyntheticDecay.Generate(t, lengthSeconds: 1, noiseBelowPeakDb: 60, seed: seed);
    }

    [Fact]
    public void The_analysis_respects_start_and_count()
    {
        var silence = new float[Rate];
        var decay = SyntheticDecay.Generate(0.3, lengthSeconds: 1, noiseBelowPeakDb: 60);
        var both = silence.Concat(decay).ToArray();

        var whole = ReverberationAnalysis.Analyse(decay, Rate, 0, decay.Length);
        var part = ReverberationAnalysis.Analyse(both, Rate, Rate, decay.Length);
        Assert.Equal(whole.Broadband.T20!.ReverberationTime, part.Broadband.T20!.ReverberationTime, 9);
        Assert.Equal(whole.Bands[3].Decay.T20!.ReverberationTime, part.Bands[3].Decay.T20!.ReverberationTime, 9);
    }
}
