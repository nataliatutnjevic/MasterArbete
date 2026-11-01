using Xunit;

namespace MasterArbete.Tests;

public class LundebyTests
{
    private const int Rate = SyntheticDecay.SampleRate;

    private static LundebyResult? Find(float[] h) =>
        Lundeby.Find(h.Select(v => (double)v).ToArray(), 0, Rate);

    // The synthetic decay starts at a mean square of 1 (0 dB) and falls 60 dB
    // in T, so it meets noise X dB down at t = T · X / 60.
    [Theory]
    [InlineData(0.10, 45)]
    [InlineData(0.30, 45)]
    [InlineData(0.60, 45)]
    [InlineData(0.30, 60)]
    [InlineData(0.30, 35)]
    public void The_noise_level_and_crossing_point_are_found(double t, double noiseDb)
    {
        var result = Find(SyntheticDecay.Generate(t, noiseBelowPeakDb: noiseDb))!;

        Assert.True(result.Converged, $"didn't converge in {result.Iterations} iterations");
        Assert.InRange(10 * Math.Log10(result.NoiseMeanSquare), -noiseDb - 0.3, -noiseDb + 0.3);

        double expected = t * noiseDb / 60;
        Assert.True(Math.Abs(result.CrossingSeconds - expected) <= 0.08 * expected,
            $"crossing at {result.CrossingSeconds:0.000} s, expected {expected:0.000} s");
    }

    [Theory]
    [InlineData(0.10)]
    [InlineData(0.30)]
    [InlineData(0.60)]
    public void The_late_slope_matches_the_reverberation_time(double t)
    {
        var result = Find(SyntheticDecay.Generate(t, noiseBelowPeakDb: 50))!;
        double lateT = -60 / result.LateSlopeDbPerSecond;
        Assert.True(Math.Abs(lateT - t) <= 0.1 * t, $"late slope gives {lateT:0.000} s, expected {t} s");
    }

    [Fact]
    public void The_tail_energy_is_what_the_decay_has_left_after_the_crossing()
    {
        // ∫ from tc to ∞ of e^(−k·t) dt = e^(−k·tc) / k, with k = 13.8155 / T.
        const double t = 0.3;
        var result = Find(SyntheticDecay.Generate(t, noiseBelowPeakDb: 45))!;
        double k = 6 * Math.Log(10) / t;
        double expected = Math.Exp(-k * result.CrossingSeconds) / k;
        // About 1.3 dB too high: the late fit reaches down to 5 dB above the
        // noise, where the noise lifts the envelope a little, so the line and
        // the crossing come out slightly late. The tail is only a small part of
        // the curve above −35 dB, so T30 changes by less than 0.5 %.
        double differenceDb = 10 * Math.Log10(result.TailEnergy / expected);
        Assert.True(Math.Abs(differenceDb) <= 2, $"tail energy {result.TailEnergy:E3}, expected {expected:E3} ({differenceDb:+0.00;-0.00} dB)");
    }

    [Fact]
    public void The_end_of_the_curve_no_longer_bends_down()
    {
        // With the tail energy and noise subtraction, the curve ends near
        // where the straight decay would be at the crossing point, instead of
        // dropping towards −∞.
        const double t = 0.3;
        var h = SyntheticDecay.Generate(t, noiseBelowPeakDb: 45);
        var result = SchroederDecay.Analyse(h, Rate, 0, h.Length);

        double end = result.CurveDb[^1];
        double straight = -60 * (result.CurveDb.Length - 1) / (double)Rate / t;
        Assert.InRange(end, straight - 2, straight + 2);
        Assert.True(Math.Abs(result.CurvaturePercent!.Value) < 5, $"C = {result.CurvaturePercent:0.0} %");
    }

    [Fact]
    public void Pure_noise_has_no_decay()
    {
        var rng = new Random(1);
        var noise = Enumerable.Range(0, Rate).Select(_ => (float)(rng.NextDouble() - 0.5)).ToArray();

        Assert.Null(Find(noise));
        var result = SchroederDecay.Analyse(noise, Rate, 0, noise.Length);
        Assert.Null(result.Lundeby);
        Assert.Null(result.T20);
    }

    [Fact]
    public void A_decay_without_noise_is_integrated_to_almost_the_end()
    {
        var result = Find(SyntheticDecay.Generate(0.3, lengthSeconds: 2))!;
        Assert.True(result.CrossingSeconds > 1.4, $"crossing at {result.CrossingSeconds:0.000} s");
    }
}
