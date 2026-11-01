using System;
using System.Collections.Generic;

namespace MasterArbete;

/// <summary>
/// Where a decay disappears into the background noise, found with Lundeby's
/// method. Times are in seconds from the onset of the decay.
/// </summary>
/// <param name="CrossingSeconds">Where the late decay line meets the noise level. The integration stops here.</param>
/// <param name="NoiseMeanSquare">The background noise, as a mean square (in the signal's units squared).</param>
/// <param name="LateSlopeDbPerSecond">Slope of the late decay, just above the noise.</param>
/// <param name="TailEnergy">
/// The energy the decay would still have had after the crossing point if
/// there were no noise: ∫ from the crossing to ∞ of the late decay line.
/// Added to the Schroeder integral so its end doesn't bend down.
/// </param>
/// <param name="Iterations">How many times the crossing point was re-estimated.</param>
/// <param name="Converged">Whether the crossing point stopped moving.</param>
public record LundebyResult(
    double CrossingSeconds,
    double NoiseMeanSquare,
    double LateSlopeDbPerSecond,
    double TailEnergy,
    int Iterations,
    bool Converged);

/// <summary>
/// Lundeby's method for finding the noise level and the point where a
/// decay meets it (Lundeby, Vigran, Bietz &amp; Vorländer, "Uncertainties of
/// measurements in room acoustics", Acustica 81, 1995):
///
///   1. Square the impulse response and average it in short intervals.
///   2. Estimate the noise from the last 10 % of the signal.
///   3. Fit a line to the decay from its peak down to 10 dB above the noise.
///   4. The crossing point is where that line meets the noise level.
///   5. Choose new intervals: a few per 10 dB of decay.
///   6. Average the squared response again with the new intervals.
///   7. Estimate the noise again, from a point 10 dB of decay after the
///      crossing point (but at least the last 10 % of the signal).
///   8. Fit the late decay: 20 dB of it, starting 5 dB above the noise.
///   9. New crossing point. Repeat 7–9 until it stops moving.
///
/// The tail energy after the crossing point is the integral of the late
/// decay line, e(t) = e(tc) · e^(−k·(t − tc)) with k = −slope · ln(10)/10:
///
///   C = e(tc) / k
///
/// The numbers chosen within the ranges the paper gives (10 ms, 10 dB,
/// 5 intervals per 10 dB, 5 dB and 20 dB) are marked below.
/// </summary>
public static class Lundeby
{
    private const double InitialIntervalSeconds = 0.010;   // paper: 10–50 ms
    private const double FirstFitAboveNoiseDb = 10;        // paper: 5–10 dB
    private const int IntervalsPer10Db = 5;                // paper: 3–10
    private const double NoiseAfterCrossingDb = 10;        // paper: 5–10 dB
    private const double LateFitAboveNoiseDb = 5;          // paper: 5–10 dB
    private const double LateFitRangeDb = 20;              // paper: 10–20 dB
    private const double MinNoiseFraction = 0.1;           // noise from at least the last 10 %
    private const int MaxIterations = 5;

    /// <summary>
    /// Runs Lundeby's method on h[onset..]. Returns null when there is no
    /// decay clearly above the noise to work with.
    /// </summary>
    public static LundebyResult? Find(double[] h, int onset, int sampleRate)
    {
        int length = h.Length - onset;
        double duration = (double)length / sampleRate;
        if (length < 10) return null;

        // 1–2. Envelope in 10 ms intervals, and the noise in the last 10 %.
        int interval = Math.Max(1, (int)Math.Round(InitialIntervalSeconds * sampleRate));
        var envelope = Envelope(h, onset, interval, sampleRate);
        double noiseDb = Db(MeanSquare(h, onset + (int)(length * (1 - MinNoiseFraction)), h.Length));

        // 3. First line: from the peak down to 10 dB above the noise.
        int peak = IndexOfMax(envelope);
        int end = FirstAtOrBelow(envelope, peak + 1, noiseDb + FirstFitAboveNoiseDb);
        if (FitLine(envelope, peak, end) is not { } first || first.Slope >= 0) return null;
        var (intercept, slope) = first;

        // 4. Preliminary crossing point.
        double crossing = Math.Clamp((noiseDb - intercept) / slope, 0, duration);

        int iterations = 0;
        bool converged = false;
        while (iterations < MaxIterations)
        {
            iterations++;

            // 5–6. New intervals: IntervalsPer10Db of them in the time the decay takes to fall 10 dB.
            double intervalSeconds = 10 / -slope / IntervalsPer10Db;
            interval = Math.Clamp((int)Math.Round(intervalSeconds * sampleRate), 1, Math.Max(1, length / 10));
            envelope = Envelope(h, onset, interval, sampleRate);

            // 7. Noise from 10 dB of decay after the crossing point, but at least the last 10 %.
            double noiseFrom = Math.Min(crossing + NoiseAfterCrossingDb / -slope, duration * (1 - MinNoiseFraction));
            noiseDb = Db(MeanSquare(h, onset + (int)(noiseFrom * sampleRate), h.Length));

            // 8. The late decay: from 25 dB above the noise down to 5 dB above it.
            peak = IndexOfMax(envelope);
            int lateStart = FirstAtOrBelow(envelope, peak, noiseDb + LateFitAboveNoiseDb + LateFitRangeDb);
            int lateEnd = FirstAtOrBelow(envelope, lateStart, noiseDb + LateFitAboveNoiseDb);
            if (FitLine(envelope, lateStart, lateEnd) is not { } late || late.Slope >= 0)
                break;   // keep the previous line

            // 9. New crossing point.
            (intercept, slope) = late;
            double newCrossing = Math.Clamp((noiseDb - intercept) / slope, 0, duration);
            bool settled = Math.Abs(newCrossing - crossing) < (double)interval / sampleRate;
            crossing = newCrossing;
            if (settled) { converged = true; break; }
        }

        // Tail energy after the crossing point: e(tc) / k.
        double k = -slope * Math.Log(10) / 10;
        double levelAtCrossing = Math.Pow(10, (intercept + slope * crossing) / 10);
        return new LundebyResult(crossing, Math.Pow(10, noiseDb / 10), slope, levelAtCrossing / k, iterations, converged);
    }

    // Mean square of h in consecutive intervals from the onset, in dB, with
    // the time (s from the onset) of each interval's middle.
    private static List<(double Time, double Db)> Envelope(double[] h, int onset, int interval, int sampleRate)
    {
        var envelope = new List<(double, double)>();
        for (int from = onset; from < h.Length; from += interval)
        {
            int to = Math.Min(h.Length, from + interval);
            double middle = (from - onset + (to - from) / 2.0) / sampleRate;
            envelope.Add((middle, Db(MeanSquare(h, from, to))));
        }
        return envelope;
    }

    // Least-squares line through envelope[from..to), in dB against seconds.
    // Null if there are fewer than two points.
    private static (double Intercept, double Slope)? FitLine(List<(double Time, double Db)> envelope, int from, int to)
    {
        int n = to - from;
        if (n < 2) return null;
        double meanT = 0, meanL = 0;
        for (int i = from; i < to; i++) { meanT += envelope[i].Time; meanL += envelope[i].Db; }
        meanT /= n; meanL /= n;
        double stt = 0, stl = 0;
        for (int i = from; i < to; i++)
        {
            double dt = envelope[i].Time - meanT;
            stt += dt * dt; stl += dt * (envelope[i].Db - meanL);
        }
        double slope = stl / stt;
        return (meanL - slope * meanT, slope);
    }

    private static int IndexOfMax(List<(double Time, double Db)> envelope)
    {
        int best = 0;
        for (int i = 1; i < envelope.Count; i++)
            if (envelope[i].Db > envelope[best].Db) best = i;
        return best;
    }

    // The first index from `from` whose level is at or below `db`, or the end.
    private static int FirstAtOrBelow(List<(double Time, double Db)> envelope, int from, double db)
    {
        for (int i = from; i < envelope.Count; i++)
            if (envelope[i].Db <= db) return i;
        return envelope.Count;
    }

    private static double MeanSquare(double[] h, int from, int to)
    {
        if (to <= from) return 0;
        double sum = 0;
        for (int i = from; i < to; i++) sum += h[i] * h[i];
        return sum / (to - from);
    }

    private static double Db(double meanSquare) => 10 * Math.Log10(Math.Max(meanSquare, 1e-30));
}
