using System;
using System.Collections.Generic;
using System.Linq;

namespace MasterArbete;

/// <summary>The decay in one octave band.</summary>
public record BandDecay(OctaveBand Band, DecayAnalysis Decay);

/// <summary>The reverberation of one decay: broadband and per octave band.</summary>
public record ReverberationResult(DecayAnalysis Broadband, IReadOnlyList<BandDecay> Bands, bool TimeReversedFiltering);

/// <summary>
/// Reverberation time per octave band (ISO 3382-2): the stretch is filtered
/// into octave bands (IEC 61260-1, see <see cref="BandPassFilter"/>) and
/// each band is analysed on its own with <see cref="SchroederDecay"/>.
/// Rooms reverberate differently at different frequencies, and the LOJ
/// limits are per frequency range (above 250 Hz and above 500 Hz).
/// </summary>
public static class ReverberationAnalysis
{    /// <summary>
    /// Analyses one decay in <paramref name="count"/> samples starting at
    /// <paramref name="start"/>, in all the octave bands the sample rate allows.
    /// </summary>
    /// <param name="timeReversed">Filter time-reversed, so the filters' ringing doesn't add to short decays (see <see cref="BandPassFilter.Apply"/>).</param>
    public static ReverberationResult Analyse(float[] signal, int sampleRate, int start, int count, bool timeReversed = true)
    {
        if (start < 0 || count < 0 || start + count > signal.Length)
            throw new ArgumentOutOfRangeException(nameof(start));

        var stretch = new double[count];
        for (int i = 0; i < count; i++) stretch[i] = signal[start + i];

        var bands = OctaveBand.For(sampleRate)
            .Select(band => new BandDecay(band,
                SchroederDecay.Analyse(BandPassFilter.ForBand(band, sampleRate).Apply(stretch, timeReversed), sampleRate)))
            .ToList();

        return new ReverberationResult(SchroederDecay.Analyse(stretch, sampleRate), bands, timeReversed);
    }
}
