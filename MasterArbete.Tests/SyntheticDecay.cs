namespace MasterArbete.Tests;

/// <summary>
/// Synthetic impulse responses with a known reverberation time, for testing:
/// white noise under an exponential envelope,
///
///   h(t) = n(t) · e^(−6.908 · t / T)
///
/// The amplitude falls by e^(−6.908) = 1/1000 in T seconds, so the energy
/// falls exactly 60 dB in T. Silence (or noise) before the decay and steady
/// background noise can be added.
/// </summary>
public static class SyntheticDecay
{
    public const int SampleRate = 16000;

    /// <param name="reverberationTime">T in seconds.</param>
    /// <param name="lengthSeconds">Length of the decay part.</param>
    /// <param name="noiseBelowPeakDb">Background noise level below the decay's starting level (e.g. 45), or null for none.</param>
    /// <param name="preDelaySeconds">Background noise only, before the decay starts.</param>
    public static float[] Generate(double reverberationTime, double lengthSeconds = 2,
        double? noiseBelowPeakDb = null, double preDelaySeconds = 0, int seed = 1)
    {
        var rng = new Random(seed);
        int pre = (int)Math.Round(preDelaySeconds * SampleRate);
        int length = (int)Math.Round(lengthSeconds * SampleRate);
        var h = new float[pre + length];

        for (int i = 0; i < length; i++)
        {
            double t = (double)i / SampleRate;
            h[pre + i] = (float)(Gaussian(rng) * Math.Exp(-6.908 * t / reverberationTime));
        }

        // The decay starts at a mean square of 1, so the noise has a mean square of 10^(−dB/10).
        if (noiseBelowPeakDb is { } db)
        {
            double rms = Math.Pow(10, -db / 20);
            for (int i = 0; i < h.Length; i++) h[i] += (float)(rms * Gaussian(rng));
        }
        return h;
    }

    /// <summary>
    /// A decay with its own reverberation time in each octave band. Each
    /// band's part is white noise filtered to the middle third of the octave
    /// (centre · 2^(±1/6)), so the neighbouring octave filters hardly pick it
    /// up, scaled to a mean square of 1 and given its band's exponential decay.
    /// </summary>
    public static float[] PerBand(IReadOnlyDictionary<double, double> reverberationTimeByNominalHz, double lengthSeconds = 2,
        double? noiseBelowPeakDb = null, int sampleRate = SampleRate, int seed = 1)
    {
        var rng = new Random(seed);
        int length = (int)Math.Round(lengthSeconds * sampleRate);
        var sum = new double[length];

        foreach (var (nominal, t) in reverberationTimeByNominalHz)
        {
            var band = OctaveBand.FromNominal(nominal);
            var white = Enumerable.Range(0, length).Select(_ => Gaussian(rng)).ToArray();
            var carrier = BandPassFilter.Butterworth(band.CentreHz / Math.Pow(2, 1 / 6.0), band.CentreHz * Math.Pow(2, 1 / 6.0), sampleRate)
                .Apply(white, timeReversed: false);
            double rms = Math.Sqrt(carrier.Average(v => v * v));
            for (int i = 0; i < length; i++)
                sum[i] += carrier[i] / rms * Math.Exp(-6.908 * i / sampleRate / t);
        }

        if (noiseBelowPeakDb is { } db)
        {
            double rms = Math.Pow(10, -db / 20);
            for (int i = 0; i < length; i++) sum[i] += rms * Gaussian(rng);
        }
        return sum.Select(v => (float)v).ToArray();
    }

    /// <summary>Two decays added together: a fast one and a slow one starting lower (a curved decay).</summary>
    public static float[] DoubleSlope(double fastT, double slowT, double slowBelowDb, double lengthSeconds = 2, int seed = 1)
    {
        var fast = Generate(fastT, lengthSeconds, seed: seed);
        var slow = Generate(slowT, lengthSeconds, seed: seed + 1000);
        double gain = Math.Pow(10, -slowBelowDb / 20);
        for (int i = 0; i < fast.Length; i++) fast[i] += (float)(gain * slow[i]);
        return fast;
    }

    // Box–Muller: a standard normal value.
    private static double Gaussian(Random rng)
    {
        double u1 = 1 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
