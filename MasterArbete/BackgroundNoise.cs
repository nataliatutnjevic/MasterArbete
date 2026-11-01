using System;
using System.Collections.Generic;
using System.Numerics;

namespace MasterArbete;

/// <summary>The A- and C-frequency weighting curves from IEC 61672-1, in dB.</summary>
public static class FrequencyWeighting
{
    // Pole frequencies (Hz) of the analogue weighting filters in IEC 61672-1.
    private const double F1 = 20.598997, F2 = 107.65265, F3 = 737.86223, F4 = 12194.217;

    /// <summary>A-weighting at frequency f (Hz). 0 dB at 1 kHz.</summary>
    public static double A(double f)
    {
        double f2 = f * f;
        double response = F4 * F4 * f2 * f2 /
            ((f2 + F1 * F1) * Math.Sqrt((f2 + F2 * F2) * (f2 + F3 * F3)) * (f2 + F4 * F4));
        return 20 * Math.Log10(response) + 2.00;
    }

    /// <summary>C-weighting at frequency f (Hz). 0 dB at 1 kHz.</summary>
    public static double C(double f)
    {
        double f2 = f * f;
        double response = F4 * F4 * f2 / ((f2 + F1 * F1) * (f2 + F4 * F4));
        return 20 * Math.Log10(response) + 0.062;
    }
}

/// <summary>
/// Equivalent continuous sound levels (Leq) over a stretch of sound, in dB
/// re 20 µPa: A-weighted, C-weighted and unweighted (Z).
/// </summary>
public record NoiseLevels(double LAeq, double LCeq, double LZeq, double DurationSeconds);

/// <summary>The LOJ background-noise limits for one requirement level (Kravnivå).</summary>
public record LojNoiseLimit(int Level, double MaxDbA, double MaxDbC)
{
    /// <summary>LOJ requires both levels to be below their limits.</summary>
    public bool IsMetBy(NoiseLevels levels) => levels.LAeq < MaxDbA && levels.LCeq < MaxDbC;
}

/// <summary>
/// Measures background noise in a calibrated recording.
///
/// The frequency weighting is applied in the frequency domain: the stretch is
/// split into overlapping windows (Welch's method), each window's power
/// spectrum is weighted with the exact A and C curves, and the results are
/// averaged. This gives the equivalent level (Leq) over the stretch without
/// the errors a digital weighting filter has near half the sample rate, which
/// matters at the 16 kHz Soundswell uses.
/// </summary>
public static class BackgroundNoise
{
    /// <summary>Shortest stretch that can be measured, in samples.</summary>
    public const int MinSamples = 1024;

    // Window length: 4096 samples is 0.256 s and 3.9 Hz between frequency bins
    // at 16 kHz, fine enough for the steep low end of the A curve.
    private const int MaxWindowLength = 4096;

    private const double ReferencePressure = 20e-6;   // Pa, 0 dB SPL

    /// <summary>LOJ R1B background-noise limits for Kravnivå 1, 2 and 3.</summary>
    public static readonly IReadOnlyList<LojNoiseLimit> LojLimits = new[]
    {
        new LojNoiseLimit(1, 40, 60),
        new LojNoiseLimit(2, 30, 50),
        new LojNoiseLimit(3, 24, 44),
    };

    /// <summary>
    /// The A-, C- and unweighted Leq of <paramref name="count"/> samples of
    /// sound pressure (in Pa) starting at <paramref name="start"/>.
    /// </summary>
    public static NoiseLevels Measure(float[] pascal, int sampleRate, int start, int count)
    {
        if (count < MinSamples)
            throw new ArgumentException($"Need at least {MinSamples} samples to measure noise.", nameof(count));
        if (start < 0 || start + count > pascal.Length)
            throw new ArgumentOutOfRangeException(nameof(start));

        // Remove any DC offset, which is not sound.
        double mean = 0;
        for (int i = start; i < start + count; i++) mean += pascal[i];
        mean /= count;

        int n = Math.Min(MaxWindowLength, LargestPowerOfTwo(count));

        // Hann window. With 50% overlap, overlapping Hann windows add up to a
        // constant, so every part of the stretch counts about equally.
        var window = new double[n];
        double windowPower = 0;
        for (int i = 0; i < n; i++)
        {
            window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
            windowPower += window[i] * window[i];
        }

        // The weighting (as a power factor) for each frequency bin up to half the sample rate.
        var gainA = new double[n / 2 + 1];
        var gainC = new double[n / 2 + 1];
        for (int k = 1; k <= n / 2; k++)
        {
            double f = (double)k * sampleRate / n;
            gainA[k] = Math.Pow(10, FrequencyWeighting.A(f) / 10);
            gainC[k] = Math.Pow(10, FrequencyWeighting.C(f) / 10);
        }

        // Window start positions: every half window, plus one ending exactly
        // at the end of the stretch so the last samples are included too.
        var starts = new List<int>();
        for (int s = 0; s + n <= count; s += n / 2) starts.Add(s);
        if (starts[^1] + n < count) starts.Add(count - n);

        var spectrum = new Complex[n];
        double sumZ = 0, sumA = 0, sumC = 0;
        foreach (int s in starts)
        {
            for (int i = 0; i < n; i++)
                spectrum[i] = (pascal[start + s + i] - mean) * window[i];
            Fft.Transform(spectrum);

            // Mean square pressure (Pa²) in each bin. By Parseval's theorem the
            // bins add up to the window's mean square; bins below half the
            // sample rate count twice because the spectrum is mirrored. Bin 0
            // (DC) is left out.
            for (int k = 1; k <= n / 2; k++)
            {
                double magnitude = spectrum[k].Magnitude;
                double power = (k == n / 2 ? 1 : 2) * magnitude * magnitude / (n * windowPower);
                sumZ += power;
                sumA += power * gainA[k];
                sumC += power * gainC[k];
            }
        }

        return new NoiseLevels(
            LAeq: ToDb(sumA / starts.Count),
            LCeq: ToDb(sumC / starts.Count),
            LZeq: ToDb(sumZ / starts.Count),
            DurationSeconds: (double)count / sampleRate);
    }

    // Mean square pressure (Pa²) to dB SPL.
    private static double ToDb(double meanSquare) =>
        10 * Math.Log10(Math.Max(meanSquare, 1e-30) / (ReferencePressure * ReferencePressure));

    private static int LargestPowerOfTwo(int value)
    {
        int power = 1;
        while (power * 2 <= value) power *= 2;
        return power;
    }
}
