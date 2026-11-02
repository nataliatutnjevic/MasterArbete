using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace MasterArbete;

/// <summary>
/// One octave band. The exact frequencies follow IEC 61260-1 (base 10):
/// the centre is 1000 · 10^(3x/10) Hz for whole numbers x, and the edges are
/// the centre times 10^(±3/20), about √2. The nominal frequency (e.g. 125
/// for 125.9 Hz) is the name the band is known by.
/// </summary>
public record OctaveBand(double NominalHz, double CentreHz, double LowerHz, double UpperHz)
{
    public string Name => NominalHz >= 1000 ? $"{NominalHz / 1000:0} kHz" : $"{NominalHz:0} Hz";

    /// <summary>The octave bands from 125 Hz to 8 kHz, as far as the sample rate allows.</summary>
    public static IReadOnlyList<OctaveBand> For(int sampleRate) =>
        new[] { 125.0, 250, 500, 1000, 2000, 4000, 8000 }
            .Select(FromNominal)
            .Where(b => b.UpperHz < sampleRate / 2.0)   // the whole band must be below half the sample rate
            .ToList();

    public static OctaveBand FromNominal(double nominalHz)
    {
        double x = Math.Round(10 / 3.0 * Math.Log10(nominalHz / 1000));
        double centre = 1000 * Math.Pow(10, 0.3 * x);
        double edge = Math.Pow(10, 0.15);
        return new OctaveBand(nominalHz, centre, centre / edge, centre * edge);
    }
}

/// <summary>
/// A digital Butterworth band-pass filter, e.g. for an octave band. A
/// Butterworth band-pass filter of order 6 (from a 3rd-order low-pass
/// prototype) is the usual way to meet IEC 61260-1 class 1.
///
/// Design, as in e.g. Oppenheim &amp; Schafer, Discrete-Time Signal Processing:
///   1. Pre-warp the band edges, ω = 2·fs·tan(π·f/fs), so that after the
///      bilinear transform they land exactly on the right frequencies.
///   2. The poles of the N-th order Butterworth low-pass prototype,
///      p_k = e^(jπ(2k+N−1)/(2N)), k = 1..N.
///   3. Low-pass to band-pass, s → (s² + ω0²)/(B·s) with ω0 = √(ω1·ω2) and
///      B = ω2 − ω1: each prototype pole p gives two band-pass poles
///      s = (p·B ± √(p²B² − 4ω0²)) / 2, and there are N zeros at s = 0 and N at ∞.
///   4. Bilinear transform, z = (2fs + s)/(2fs − s): the zeros go to z = 1
///      and z = −1, and the poles come in complex-conjugate pairs.
///   5. One second-order section (biquad) per pole pair, each with one zero
///      at z = 1 and one at z = −1. Running the sections one after another is
///      numerically much safer than one 6th-order filter, especially for
///      narrow low-frequency bands.
///   6. Scale for 0 dB at the centre frequency.
/// </summary>
public sealed class BandPassFilter
{
    // y[n] = b0·x[n] + b1·x[n−1] + b2·x[n−2] − a1·y[n−1] − a2·y[n−2]
    private readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2);

    private readonly Biquad[] _sections;

    private BandPassFilter(Biquad[] sections) => _sections = sections;

    /// <summary>The filter for an octave band (order 6).</summary>
    public static BandPassFilter ForBand(OctaveBand band, int sampleRate) =>
        Butterworth(band.LowerHz, band.UpperHz, sampleRate);

    /// <param name="prototypeOrder">N; the band-pass filter has order 2N.</param>
    public static BandPassFilter Butterworth(double lowerHz, double upperHz, int sampleRate, int prototypeOrder = 3)
    {
        if (!(0 < lowerHz && lowerHz < upperHz && upperHz < sampleRate / 2.0))
            throw new ArgumentException("The band must lie between 0 Hz and half the sample rate.");

        // 1. Pre-warped edges (rad/s).
        double fs2 = 2.0 * sampleRate;
        double w1 = fs2 * Math.Tan(Math.PI * lowerHz / sampleRate);
        double w2 = fs2 * Math.Tan(Math.PI * upperHz / sampleRate);
        double w0 = Math.Sqrt(w1 * w2), bandwidth = w2 - w1;

        var sections = new List<Biquad>();
        int n = prototypeOrder;
        for (int k = 1; k <= n; k++)
        {
            // 2. Prototype pole.
            Complex p = Complex.FromPolarCoordinates(1, Math.PI * (2 * k + n - 1) / (2 * n));

            // 3. Two band-pass poles from it.
            Complex root = Complex.Sqrt(p * p * bandwidth * bandwidth - 4 * w0 * w0);
            foreach (Complex s in new[] { (p * bandwidth + root) / 2, (p * bandwidth - root) / 2 })
            {
                // 4. To the z-plane. Each conjugate pair is met twice; keep the
                // one in the upper half plane.
                Complex z = (fs2 + s) / (fs2 - s);
                if (z.Imaginary <= 0) continue;

                // 5. Biquad with that pole pair and zeros at z = ±1: (1 − z⁻²) / (1 − 2Re(z)z⁻¹ + |z|²z⁻²).
                sections.Add(new Biquad(1, 0, -1, -2 * z.Real, z.Magnitude * z.Magnitude));
            }
        }

        // 6. 0 dB at the centre: ω0 maps back to the digital frequency 2·atan(ω0/(2fs)).
        var filter = new BandPassFilter(sections.ToArray());
        double centreHz = sampleRate / Math.PI * Math.Atan(w0 / fs2);
        double gain = filter.Response(centreHz, sampleRate).Magnitude;
        var first = filter._sections[0];
        filter._sections[0] = first with { B0 = first.B0 / gain, B1 = first.B1 / gain, B2 = first.B2 / gain };
        return filter;
    }

    /// <summary>The filter's gain in dB at frequency f.</summary>
    public double GainDb(double frequencyHz, int sampleRate) =>
        20 * Math.Log10(Response(frequencyHz, sampleRate).Magnitude);

    private Complex Response(double frequencyHz, int sampleRate)
    {
        Complex zInv = Complex.FromPolarCoordinates(1, -2 * Math.PI * frequencyHz / sampleRate);
        Complex total = 1;
        foreach (var q in _sections)
            total *= (q.B0 + q.B1 * zInv + q.B2 * zInv * zInv) / (1 + q.A1 * zInv + q.A2 * zInv * zInv);
        return total;
    }

    /// <summary>
    /// Filters a signal. With <paramref name="timeReversed"/>, the signal is
    /// reversed, filtered and reversed back (Jacobsen &amp; Rindel, J. Sound
    /// Vib. 117, 1987). The magnitude response is the same, but the filter's
    /// own ringing then comes before an impulse instead of after it, where it
    /// would add to the room's decay. This matters for short reverberation
    /// times in narrow bands: it relaxes the requirement on bandwidth B times
    /// reverberation time T from about B·T > 16 to about B·T > 4.
    /// </summary>
    public double[] Apply(IReadOnlyList<double> signal, bool timeReversed)
    {
        int n = signal.Count;
        var y = new double[n];
        for (int i = 0; i < n; i++) y[i] = signal[timeReversed ? n - 1 - i : i];

        // Direct form II transposed, one section after another.
        foreach (var q in _sections)
        {
            double s1 = 0, s2 = 0;
            for (int i = 0; i < n; i++)
            {
                double x = y[i];
                double output = q.B0 * x + s1;
                s1 = q.B1 * x - q.A1 * output + s2;
                s2 = q.B2 * x - q.A2 * output;
                y[i] = output;
            }
        }

        if (timeReversed) Array.Reverse(y);
        return y;
    }
}
