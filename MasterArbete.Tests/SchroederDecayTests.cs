using Xunit;

namespace MasterArbete.Tests;

public class SchroederDecayTests
{
    private const int Rate = SyntheticDecay.SampleRate;

    private static DecayAnalysis Analyse(float[] h) => SchroederDecay.Analyse(h, Rate, 0, h.Length);

    private static void AssertClose(double expected, double actual, double relativeTolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= relativeTolerance * expected,
            $"{what}: {actual:0.0000} s, expected {expected:0.0000} s ± {relativeTolerance:P0}");

    // Each synthetic decay is random noise, so one decay doesn't give exactly
    // T: the estimate scatters, more for short T and short fit ranges (EDT).
    // So the tests check over many decays that the mean is right (no bias)
    // and that the scatter is small. 0.1 s is the Kravnivå 3 limit.
    private const int Seeds = 100;

    private static (double Bias, double Spread) Scatter(double t, double? noiseBelowPeakDb, Func<DecayAnalysis, DecayFit?> fit)
    {
        var errors = Enumerable.Range(1, Seeds)
            .Select(seed => fit(Analyse(SyntheticDecay.Generate(t, noiseBelowPeakDb: noiseBelowPeakDb, seed: seed)))!.ReverberationTime / t - 1)
            .ToList();
        double mean = errors.Average();
        return (mean, Math.Sqrt(errors.Average(e => (e - mean) * (e - mean))));
    }

    private static void AssertScatter(double t, double? noise, Func<DecayAnalysis, DecayFit?> fit, string name,
        double maxBias, double maxSpread)
    {
        var (bias, spread) = Scatter(t, noise, fit);
        Assert.True(Math.Abs(bias) <= maxBias && spread <= maxSpread,
            $"{name} at T = {t} s: mean error {bias:+0.0%;-0.0%} (max ±{maxBias:0.0%}), scatter {spread:0.0%} (max {maxSpread:0.0%})");
    }

    [Theory]
    [InlineData(0.10, 0.08, 0.045, 0.03)]
    [InlineData(0.30, 0.05, 0.025, 0.015)]
    [InlineData(0.60, 0.035, 0.02, 0.012)]
    public void Without_noise_EDT_T20_and_T30_equal_the_true_reverberation_time(double t,
        double maxEdtSpread, double maxT20Spread, double maxT30Spread)
    {
        AssertScatter(t, null, r => r.Edt, "EDT", 0.02, maxEdtSpread);
        AssertScatter(t, null, r => r.T20, "T20", 0.01, maxT20Spread);
        AssertScatter(t, null, r => r.T30, "T30", 0.01, maxT30Spread);
    }

    [Theory]
    [InlineData(0.10)]
    [InlineData(0.30)]
    [InlineData(0.60)]
    public void With_noise_60_dB_down_T20_and_T30_are_accurate_and_reliable(double t)
    {
        var result = Analyse(SyntheticDecay.Generate(t, noiseBelowPeakDb: 60));
        Assert.True(result.T20IsReliable && result.T30IsReliable, $"dynamic range {result.DynamicRangeDb:0.0} dB");
        AssertScatter(t, 60, r => r.T20, "T20", 0.01, 0.045);
        AssertScatter(t, 60, r => r.T30, "T30", 0.01, 0.03);
    }

    [Theory]
    [InlineData(0.10)]
    [InlineData(0.30)]
    [InlineData(0.60)]
    public void With_noise_45_dB_down_T20_is_accurate_but_T30_is_not_reliable(double t)
    {
        var result = Analyse(SyntheticDecay.Generate(t, noiseBelowPeakDb: 45));
        Assert.InRange(result.DynamicRangeDb, 35, 45);
        Assert.True(result.T20IsReliable);
        Assert.False(result.T30IsReliable);
        AssertScatter(t, 45, r => r.T20, "T20", 0.015, 0.045);
    }

    [Fact]
    public void With_noise_30_dB_down_T20_is_not_reliable()
    {
        var result = Analyse(SyntheticDecay.Generate(0.3, noiseBelowPeakDb: 30));
        Assert.True(result.DynamicRangeDb < 35, $"dynamic range {result.DynamicRangeDb:0.0} dB");
        Assert.False(result.T20IsReliable);
        Assert.False(result.T30IsReliable);
    }

    [Fact]
    public void The_cutoff_keeps_the_background_noise_out_of_the_integral()
    {
        // Integrating the whole 2 s including the noise would make T20 far too long.
        var h = SyntheticDecay.Generate(0.3, noiseBelowPeakDb: 45);
        var whole = SchroederDecay.Fit(SchroederDecay.Curve(h.Select(v => (double)v).ToArray(), 0, h.Length, Rate), Rate, -5, -25);
        var result = Analyse(h);

        Assert.True(whole!.ReverberationTime > 0.35, $"without a cut-off: {whole.ReverberationTime:0.000} s");
        Assert.True(result.CutoffIndex < h.Length / 2, $"cut-off at {result.CutoffIndex / (double)Rate:0.000} s");
        AssertClose(0.3, result.T20!.ReverberationTime, 0.04, "T20 with the cut-off");
    }

    [Fact]
    public void The_onset_is_found_after_a_stretch_of_background_noise()
    {
        var h = SyntheticDecay.Generate(0.3, noiseBelowPeakDb: 60, preDelaySeconds: 0.5);
        var result = Analyse(h);

        int expected = (int)(0.5 * Rate);
        Assert.InRange(result.OnsetIndex, expected, expected + 16);   // within 1 ms
        AssertClose(0.3, result.T20!.ReverberationTime, 0.03, "T20");
    }

    [Fact]
    public void The_analysis_respects_start_and_count()
    {
        // Two decays in a row: analysing the second one alone gives its own T.
        var first = SyntheticDecay.Generate(0.6, lengthSeconds: 2, noiseBelowPeakDb: 60, seed: 1);
        var second = SyntheticDecay.Generate(0.15, lengthSeconds: 1, noiseBelowPeakDb: 60, seed: 2);
        var both = first.Concat(second).ToArray();

        var result = SchroederDecay.Analyse(both, Rate, first.Length, second.Length);
        Assert.InRange(result.OnsetIndex, 0, 16);
        AssertClose(0.15, result.T20!.ReverberationTime, 0.03, "T20 of the second decay");
    }

    [Fact]
    public void A_curve_starts_at_0_dB_and_only_falls()
    {
        var h = SyntheticDecay.Generate(0.3).Select(v => (double)v).ToArray();
        var curve = SchroederDecay.Curve(h, 0, h.Length, Rate);

        Assert.Equal(0, curve[0], 9);
        for (int i = 1; i < curve.Length; i++) Assert.True(curve[i] <= curve[i - 1]);
    }

    [Fact]
    public void The_fit_of_a_straight_line_is_exact()
    {
        // L(t) = −120 dB/s · t, i.e. 60 dB in 0.5 s.
        var curve = Enumerable.Range(0, Rate).Select(i => -120.0 * i / Rate).ToArray();
        var fit = SchroederDecay.Fit(curve, Rate, -5, -25)!;

        Assert.Equal(-120, fit.SlopeDbPerSecond, 6);
        Assert.Equal(0.5, fit.ReverberationTime, 6);
        Assert.Equal(0, fit.InterceptDb, 6);
        Assert.Equal(0, fit.NonLinearityPermille, 6);
    }

    [Fact]
    public void The_fit_uses_only_the_given_range()
    {
        // 0 to −10 dB falls fast (T = 0.2 s), after that slowly (T = 0.8 s).
        var curve = Enumerable.Range(0, 2 * Rate).Select(i =>
        {
            double t = (double)i / Rate, knee = 10 / 300.0;
            return t < knee ? -300 * t : -10 - 75 * (t - knee);
        }).ToArray();

        Assert.Equal(0.2, SchroederDecay.Fit(curve, Rate, 0, -10)!.ReverberationTime, 2);
        Assert.Equal(0.8, SchroederDecay.Fit(curve, Rate, -15, -35)!.ReverberationTime, 3);
    }

    [Fact]
    public void There_is_no_fit_when_the_curve_does_not_fall_far_enough()
    {
        var curve = Enumerable.Range(0, Rate).Select(i => -20.0 * i / Rate).ToArray();   // reaches only −20 dB
        Assert.Null(SchroederDecay.Fit(curve, Rate, -5, -25));
        Assert.NotNull(SchroederDecay.Fit(curve, Rate, 0, -10));
    }

    [Fact]
    public void A_straight_decay_has_low_non_linearity_and_curvature()
    {
        var result = Analyse(SyntheticDecay.Generate(0.3, noiseBelowPeakDb: 60));
        Assert.True(result.T20!.NonLinearityPermille < 10, $"ξ(T20) = {result.T20.NonLinearityPermille:0.0} ‰");
        Assert.True(result.T30!.NonLinearityPermille < 10, $"ξ(T30) = {result.T30.NonLinearityPermille:0.0} ‰");
        Assert.True(Math.Abs(result.CurvaturePercent!.Value) < 5, $"C = {result.CurvaturePercent:0.0} %");
    }

    [Fact]
    public void A_double_slope_decay_shows_up_as_curvature()
    {
        // A fast decay (0.2 s) with a slow one (0.8 s) 20 dB below it: the curve bends.
        var result = Analyse(SyntheticDecay.DoubleSlope(0.2, 0.8, slowBelowDb: 20));
        Assert.True(result.CurvaturePercent > 10, $"C = {result.CurvaturePercent:0.0} %");
        Assert.True(result.T30!.NonLinearityPermille > 10, $"ξ(T30) = {result.T30.NonLinearityPermille:0.0} ‰");
    }

    [Fact]
    public void Too_short_a_stretch_is_rejected()
    {
        var h = SyntheticDecay.Generate(0.3, lengthSeconds: 0.05);
        Assert.Throws<ArgumentException>(() => Analyse(h));
    }
}
