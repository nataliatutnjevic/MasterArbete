using System;

namespace MasterArbete;

/// <summary>
/// A straight line fitted to part of a decay curve, and the reverberation
/// time it gives: the time the level would take to fall 60 dB at that slope.
/// </summary>
public record DecayFit(double FromDb, double ToDb, double SlopeDbPerSecond, double InterceptDb, double CorrelationSquared)
{
    /// <summary>Reverberation time in seconds, −60 dB divided by the slope.</summary>
    public double ReverberationTime => -60 / SlopeDbPerSecond;

    /// <summary>
    /// ISO 3382-2 non-linearity ξ in per mille, 1000 · (1 − r²). 0 for a
    /// perfectly straight decay.
    /// </summary>
    public double NonLinearityPermille => 1000 * (1 - CorrelationSquared);
}

/// <summary>The result of analysing one decay, e.g. after a handclap.</summary>
public record DecayAnalysis(
    int SampleRate,
    int OnsetIndex,               // first sample of the decay, in the analysed signal
    int CutoffIndex,              // the integration stops here (exclusive)
    double[] CurveDb,             // the Schroeder curve from the onset to the cut-off, 0 dB at the onset
    double NoiseLevelDb,          // background noise, in dB relative to the peak of the envelope
    LundebyResult? Lundeby,       // null if Lundeby's method found no decay; then there is no curve or fit
    DecayFit? Edt,
    DecayFit? T20,
    DecayFit? T30)
{
    /// <summary>How far the decay falls before it reaches the noise, in dB.</summary>
    public double DynamicRangeDb => -NoiseLevelDb;

    /// <summary>ISO 3382 asks for at least 35 dB above the noise for T20, and 45 dB for T30.</summary>
    public bool T20IsReliable => T20 != null && DynamicRangeDb >= 35;
    public bool T30IsReliable => T30 != null && DynamicRangeDb >= 45;

    /// <summary>ISO 3382-2 curvature C in percent, 100 · (T30/T20 − 1).</summary>
    public double? CurvaturePercent => T20 != null && T30 != null
        ? 100 * (T30.ReverberationTime / T20.ReverberationTime - 1)
        : null;
}

/// <summary>
/// Reverberation time from an impulse response (e.g. a handclap) with
/// Schroeder's backward integration (ISO 3382-2):
///
///   E(t) = ∫ from t to the cut-off of (h²(τ) − N) dτ + C,   L(t) = 10 · log10(E(t) / E(0))
///
/// and a least-squares line fitted to L(t) over −5 to −25 dB (T20),
/// −5 to −35 dB (T30) and 0 to −10 dB (EDT).
///
/// Background noise makes the reverberation time too long if it is
/// integrated, so three things keep it out (all from Lundeby's method, see
/// <see cref="Lundeby"/>):
///   - the integration stops where the decay meets the noise (the cut-off);
///   - C is the energy the decay would still have had after the cut-off
///     without noise. Without it the end of the curve bends down;
///   - N, the noise's mean square, is subtracted from h² before the cut-off,
///     where the noise is mixed into the decay (Chu, JASA 63, 1978). With
///     noise 45 dB down this takes T30 from 3–4 % too long to within 0.5 %.
/// If Lundeby's method finds no decay above the noise, there is no curve and
/// no reverberation time.
/// </summary>
public static class SchroederDecay
{
    // Length of the short averages that make up the energy envelope.
    private const double EnvelopeWindowSeconds = 0.010;

    // The decay starts where the signal first comes within this many dB of its
    // peak. ISO 3382-1 allows the start at most 20 dB below the peak.
    private const double OnsetBelowPeakDb = 20;

    // Without a decay, the background noise is estimated from the last part of the signal.
    private const double NoiseTailFraction = 0.1;

    /// <summary>
    /// Analyses one decay in <paramref name="count"/> samples starting at
    /// <paramref name="start"/>. The stretch should start before the impulse and
    /// end with some background noise after the decay. Indexes in the result
    /// are relative to <paramref name="start"/>.
    /// </summary>
    public static DecayAnalysis Analyse(float[] signal, int sampleRate, int start, int count)
    {
        if (start < 0 || count < 0 || start + count > signal.Length)
            throw new ArgumentOutOfRangeException(nameof(start));

        int window = Math.Max(1, (int)Math.Round(EnvelopeWindowSeconds * sampleRate));
        if (count < 10 * window)
            throw new ArgumentException($"Need at least {10 * EnvelopeWindowSeconds:0.0} s to analyse a decay.", nameof(count));

        var h = new double[count];
        for (int i = 0; i < count; i++) h[i] = signal[start + i];

        int onset = FindOnset(h);
        double[] envelope = Envelope(h, window);
        double peak = 0;
        for (int b = onset / window; b < envelope.Length; b++) peak = Math.Max(peak, envelope[b]);
        double NoiseLevelDb(double noise) => 10 * Math.Log10(Math.Max(noise, 1e-30) / Math.Max(peak, 1e-30));

        var lundeby = Lundeby.Find(h, onset, sampleRate);
        if (lundeby == null)
        {
            // No decay above the noise: nothing to integrate or fit.
            return new DecayAnalysis(sampleRate, onset, onset, Array.Empty<double>(),
                NoiseLevelDb(NoiseMeanSquare(h)), null, null, null, null);
        }

        int cutoff = Math.Clamp(onset + (int)Math.Round(lundeby.CrossingSeconds * sampleRate), onset + 1, count);
        double[] curve = Curve(h, onset, cutoff - onset, sampleRate, lundeby.TailEnergy, lundeby.NoiseMeanSquare);
        return new DecayAnalysis(
            sampleRate, onset, cutoff, curve,
            NoiseLevelDb(lundeby.NoiseMeanSquare),
            lundeby,
            Edt: Fit(curve, sampleRate, 0, -10),
            T20: Fit(curve, sampleRate, -5, -25),
            T30: Fit(curve, sampleRate, -5, -35));
    }

    /// <summary>
    /// Schroeder's backward integration of <paramref name="count"/> samples
    /// from <paramref name="start"/>, in dB with 0 dB at the first sample.
    /// Computed backwards in one pass: E[n] = E[n+1] + (h²[n] − N) / fs, starting
    /// from <paramref name="tailEnergy"/> (the C above) after the last sample,
    /// with N = <paramref name="noiseMeanSquare"/>.
    /// </summary>
    public static double[] Curve(double[] h, int start, int count, int sampleRate, double tailEnergy = 0, double noiseMeanSquare = 0)
    {
        var energy = new double[count];
        double sum = tailEnergy;
        for (int i = count - 1; i >= 0; i--)
        {
            double v = h[start + i];
            sum += (v * v - noiseMeanSquare) / sampleRate;
            energy[i] = sum;
        }

        var curve = new double[count];
        double total = count > 0 ? energy[0] : 0;
        for (int i = 0; i < count; i++)
            curve[i] = total > 0 && energy[i] > 0 ? 10 * Math.Log10(energy[i] / total) : double.NegativeInfinity;
        return curve;
    }

    /// <summary>
    /// Least-squares line through the part of the curve between
    /// <paramref name="fromDb"/> and <paramref name="toDb"/> (e.g. −5 and −25),
    /// from where the curve first falls to fromDb to where it first falls
    /// below toDb. Null if the curve doesn't reach toDb.
    /// </summary>
    public static DecayFit? Fit(double[] curveDb, int sampleRate, double fromDb, double toDb)
    {
        int first = Array.FindIndex(curveDb, v => v <= fromDb);
        int last = Array.FindIndex(curveDb, v => v < toDb) - 1;
        if (first < 0 || last - first < 2) return null;

        // Slope m = Σ(t − t̄)(L − L̄) / Σ(t − t̄)², with t in seconds.
        int n = last - first + 1;
        double meanT = 0, meanL = 0;
        for (int i = first; i <= last; i++) { meanT += (double)i / sampleRate; meanL += curveDb[i]; }
        meanT /= n; meanL /= n;

        double stt = 0, stl = 0, sll = 0;
        for (int i = first; i <= last; i++)
        {
            double dt = (double)i / sampleRate - meanT, dl = curveDb[i] - meanL;
            stt += dt * dt; stl += dt * dl; sll += dl * dl;
        }
        if (stt == 0 || sll == 0 || stl >= 0) return null;   // not a decay

        double slope = stl / stt;
        return new DecayFit(fromDb, toDb, slope, meanL - slope * meanT, stl * stl / (stt * sll));
    }

    // The first sample whose square comes within OnsetBelowPeakDb of the largest one.
    private static int FindOnset(double[] h)
    {
        double max = 0;
        foreach (double v in h) max = Math.Max(max, v * v);
        double threshold = max * Math.Pow(10, -OnsetBelowPeakDb / 10);
        for (int i = 0; i < h.Length; i++)
            if (h[i] * h[i] >= threshold) return i;
        return 0;
    }

    // Mean square in consecutive blocks of `window` samples.
    private static double[] Envelope(double[] h, int window)
    {
        var envelope = new double[(h.Length + window - 1) / window];
        for (int b = 0; b < envelope.Length; b++)
        {
            int from = b * window, to = Math.Min(h.Length, from + window);
            double sum = 0;
            for (int i = from; i < to; i++) sum += h[i] * h[i];
            envelope[b] = sum / (to - from);
        }
        return envelope;
    }

    // Mean square of the last NoiseTailFraction of the signal.
    private static double NoiseMeanSquare(double[] h)
    {
        int from = (int)(h.Length * (1 - NoiseTailFraction));
        double sum = 0;
        for (int i = from; i < h.Length; i++) sum += h[i] * h[i];
        return sum / (h.Length - from);
    }
}
