using Xunit;

namespace MasterArbete.Tests;

public class LojReverberationTests
{
    // A decay whose only content that matters here is its T20 and dynamic range.
    private static DecayAnalysis Decay(double? t20, double dynamicRangeDb = 50) => new(
        SampleRate: 16000, OnsetIndex: 0, CutoffIndex: 0, CurveDb: [], EnvelopeDb: [], EnvelopeIntervalSeconds: 0.01,
        NoiseLevelDb: -dynamicRangeDb, Lundeby: null, Edt: null,
        T20: t20 is { } t ? new DecayFit(-5, -25, -60 / t, 0, 1) : null,
        T30: null);

    // A result with the given T20 per nominal band; every other band from 125 Hz to 4 kHz gets `others`.
    private static ReverberationResult Result(double others, Dictionary<double, DecayAnalysis>? bands = null) => new(
        Decay(others),
        new[] { 125.0, 250, 500, 1000, 2000, 4000 }
            .Select(f => new BandDecay(OctaveBand.FromNominal(f), bands?.GetValueOrDefault(f) ?? Decay(others)))
            .ToList(),
        TimeReversedFiltering: true);

    private static LojReverbLimit Level(int level) => LojReverbLimit.All.Single(l => l.Level == level);

    [Fact]
    public void The_limits_are_those_of_LOJ_R1B()
    {
        Assert.Equal(new[] { (1, 0.60, 0.50), (2, 0.30, 0.25), (3, 0.15, 0.10) },
            LojReverbLimit.All.Select(l => (l.Level, l.MaxBelow500HzSeconds, l.MaxFrom500HzSeconds)));
    }

    [Fact]
    public void The_250_Hz_band_gets_the_first_limit_and_500_Hz_and_up_the_second()
    {
        var level2 = Level(2);
        Assert.Null(level2.LimitFor(OctaveBand.FromNominal(125)));
        Assert.Equal(0.30, level2.LimitFor(OctaveBand.FromNominal(250)));
        Assert.Equal(0.25, level2.LimitFor(OctaveBand.FromNominal(500)));
        Assert.Equal(0.25, level2.LimitFor(OctaveBand.FromNominal(8000)));
    }

    [Fact]
    public void The_125_Hz_band_is_not_checked()
    {
        var check = Level(2).Check(Result(0.20, new() { [125] = Decay(2.0) }));
        Assert.Equal(LojVerdict.Meets, check.Verdict);
        Assert.Equal(new[] { 250.0, 500, 1000, 2000, 4000 }, check.Bands.Select(b => b.Band.NominalHz));
    }

    [Theory]
    [InlineData(1, 0.59, 0.49, LojVerdict.Meets)]
    [InlineData(1, 0.61, 0.49, LojVerdict.Fails)]
    [InlineData(1, 0.59, 0.51, LojVerdict.Fails)]
    [InlineData(2, 0.29, 0.24, LojVerdict.Meets)]
    [InlineData(2, 0.31, 0.24, LojVerdict.Fails)]
    [InlineData(2, 0.29, 0.26, LojVerdict.Fails)]
    [InlineData(3, 0.14, 0.09, LojVerdict.Meets)]
    [InlineData(3, 0.16, 0.09, LojVerdict.Fails)]
    [InlineData(3, 0.14, 0.11, LojVerdict.Fails)]
    public void Each_level_is_checked_just_below_and_above_its_limits(int level, double t250, double tFrom500, LojVerdict expected)
    {
        var check = Level(level).Check(Result(tFrom500, new() { [250] = Decay(t250) }));
        Assert.Equal(expected, check.Verdict);
    }

    [Fact]
    public void A_T20_exactly_at_the_limit_fails_since_LOJ_asks_for_below()
    {
        Assert.Equal(LojVerdict.Fails, Level(2).Check(Result(0.25)).Verdict);
    }

    [Fact]
    public void A_250_Hz_T20_between_the_two_limits_is_allowed_but_not_at_500_Hz()
    {
        // 0.28 s is under the 250 Hz limit (0.30 s) but over the ≥500 Hz one (0.25 s).
        Assert.Equal(LojVerdict.Meets, Level(2).Check(Result(0.20, new() { [250] = Decay(0.28) })).Verdict);
        Assert.Equal(LojVerdict.Fails, Level(2).Check(Result(0.20, new() { [500] = Decay(0.28) })).Verdict);
    }

    [Fact]
    public void One_failing_band_fails_the_level_and_is_named()
    {
        var check = Level(2).Check(Result(0.20, new() { [2000] = Decay(0.27) }));
        Assert.Equal(LojVerdict.Fails, check.Verdict);
        Assert.Equal(new[] { 2000.0 }, check.DecidingBands.Select(b => b.Band.NominalHz));
    }

    [Fact]
    public void An_unreliable_band_gives_cant_tell_unless_another_band_fails()
    {
        // 30 dB of dynamic range is under the 35 dB T20 needs.
        var unreliable = Level(2).Check(Result(0.20, new() { [250] = Decay(0.20, dynamicRangeDb: 30) }));
        Assert.Equal(LojVerdict.CantTell, unreliable.Verdict);
        Assert.Equal(new[] { 250.0 }, unreliable.DecidingBands.Select(b => b.Band.NominalHz));

        var alsoFailing = Level(2).Check(Result(0.20, new() { [250] = Decay(0.20, dynamicRangeDb: 30), [1000] = Decay(0.40) }));
        Assert.Equal(LojVerdict.Fails, alsoFailing.Verdict);
        Assert.Equal(new[] { 1000.0 }, alsoFailing.DecidingBands.Select(b => b.Band.NominalHz));
    }

    [Fact]
    public void An_unreliable_T20_over_the_limit_is_cant_tell_not_fails()
    {
        var check = Level(2).Check(Result(0.20, new() { [500] = Decay(0.40, dynamicRangeDb: 30) }));
        Assert.Equal(LojVerdict.CantTell, check.Verdict);
    }

    [Fact]
    public void A_band_without_a_T20_gives_cant_tell()
    {
        var check = Level(1).Check(Result(0.20, new() { [4000] = Decay(null) }));
        Assert.Equal(LojVerdict.CantTell, check.Verdict);
    }

    [Fact]
    public void A_real_analysis_of_a_dry_synthetic_room_meets_level_2_but_not_3()
    {
        // T = 0.2 s at all frequencies, 60 dB above the noise.
        var h = SyntheticDecay.Generate(0.2, lengthSeconds: 1, noiseBelowPeakDb: 60);
        var result = ReverberationAnalysis.Analyse(h, SyntheticDecay.SampleRate, 0, h.Length);

        Assert.Equal(LojVerdict.Meets, Level(1).Check(result).Verdict);
        Assert.Equal(LojVerdict.Meets, Level(2).Check(result).Verdict);
        Assert.Equal(LojVerdict.Fails, Level(3).Check(result).Verdict);
    }
}
