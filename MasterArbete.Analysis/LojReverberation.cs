using System;
using System.Collections.Generic;
using System.Linq;

namespace MasterArbete;

/// <summary>The outcome of comparing a measurement with an LOJ limit.</summary>
public enum LojVerdict
{
    Meets,
    Fails,

    /// <summary>
    /// No band fails, but at least one has no reliable T20 (too little
    /// dynamic range, or no decay found), so the level can't be confirmed.
    /// </summary>
    CantTell,
}

/// <summary>One octave band compared with its LOJ limit.</summary>
/// <param name="T20Seconds">The band's T20, or null if there is none.</param>
/// <param name="IsReliable">Whether ISO 3382 would trust the T20 (at least 35 dB above the noise).</param>
public record LojBandCheck(OctaveBand Band, double LimitSeconds, double? T20Seconds, bool IsReliable, LojVerdict Verdict);

/// <summary>A reverberation result compared with one LOJ requirement level.</summary>
public record LojReverbCheck(LojReverbLimit Limit, IReadOnlyList<LojBandCheck> Bands, LojVerdict Verdict)
{
    /// <summary>
    /// The bands that decide the verdict: those that fail, or for
    /// <see cref="LojVerdict.CantTell"/> those without a reliable T20. Empty
    /// when the level is met.
    /// </summary>
    public IEnumerable<LojBandCheck> DecidingBands => Bands.Where(b => b.Verdict == Verdict && Verdict != LojVerdict.Meets);
}

/// <summary>
/// The LOJ reverberation-time limits for one requirement level (Kravnivå).
/// LOJ R1B gives two limits per level, "@&gt;250Hz" and "@&gt;500Hz", read
/// here as a stepped requirement on the octave bands:
///   - the 250 Hz band must be below <see cref="MaxBelow500HzSeconds"/>;
///   - the 500 Hz band and every band above it must be below <see cref="MaxFrom500HzSeconds"/>.
/// The 250 Hz band (177–355 Hz) is counted as "above 250 Hz", since
/// otherwise the first limit would apply to no band at all. LOJ says
/// nothing about averaging, so every band must pass on its own, and it
/// doesn't say which reverberation time to use, so T20 is compared: the
/// one a handclap can usually give with enough dynamic range.
/// </summary>
public record LojReverbLimit(int Level, double MaxBelow500HzSeconds, double MaxFrom500HzSeconds)
{
    public const double LowestBandHz = 250;
    public const double UpperLimitFromHz = 500;

    /// <summary>LOJ R1B reverberation-time limits for Kravnivå 1, 2 and 3.</summary>
    public static readonly IReadOnlyList<LojReverbLimit> All = new[]
    {
        new LojReverbLimit(1, 0.60, 0.50),
        new LojReverbLimit(2, 0.30, 0.25),
        new LojReverbLimit(3, 0.15, 0.10),
    };

    /// <summary>The limit for a band in seconds, or null for a band LOJ doesn't cover (below 250 Hz).</summary>
    public double? LimitFor(OctaveBand band) =>
        band.NominalHz >= UpperLimitFromHz ? MaxFrom500HzSeconds
        : band.NominalHz >= LowestBandHz ? MaxBelow500HzSeconds
        : null;

    /// <summary>
    /// Compares every band from 250 Hz up with its limit. A band meets it if
    /// its T20 is reliable and below the limit, and fails if its T20 is
    /// reliable and at or above it; otherwise it can't tell. The level fails
    /// if any band fails, and can't tell if no band fails but one can't tell.
    /// </summary>
    public LojReverbCheck Check(ReverberationResult result)
    {
        var bands = new List<LojBandCheck>();
        foreach (var (band, decay) in result.Bands)
        {
            if (LimitFor(band) is not { } limit) continue;

            double? t20 = decay.T20?.ReverberationTime;
            bool reliable = decay.T20IsReliable;
            var verdict = t20 is not { } t || !reliable ? LojVerdict.CantTell
                : t < limit ? LojVerdict.Meets
                : LojVerdict.Fails;
            bands.Add(new LojBandCheck(band, limit, t20, reliable, verdict));
        }

        var overall = bands.Count == 0 ? LojVerdict.CantTell
            : bands.Any(b => b.Verdict == LojVerdict.Fails) ? LojVerdict.Fails
            : bands.Any(b => b.Verdict == LojVerdict.CantTell) ? LojVerdict.CantTell
            : LojVerdict.Meets;
        return new LojReverbCheck(this, bands, overall);
    }
}
