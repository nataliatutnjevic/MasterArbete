using System;

namespace MasterArbete;

/// <summary>
/// Converts samples to sound pressure in pascals, using the calibration
/// Soundswell stores in the swel header. The sample values in "range" are
/// shown as the values in "view", and a calibrated file gives "view" the
/// unit Pa, e.g. range=-65534,65534 and view=-31.55,31.55,Pa.
///
/// Swell maps between the two linearly. This was checked against Swell's own
/// display: raw sample 15681 in such a file is shown as 7.5493 Pa
/// (15681 × 31.55 / 65534).
/// </summary>
public record PressureCalibration(double PascalPerRawUnit, double OffsetPascal)
{
    /// <summary>
    /// The calibration for a channel, or null if the file isn't calibrated:
    /// no swel header, no "range", or a "view" that isn't in Pa.
    /// </summary>
    public static PressureCalibration? FromHeader(WavInfo info, int channel = 0)
    {
        if (info.Swel is not { } swel) return null;
        if (!string.Equals(swel.GetViewUnit(channel), "Pa", StringComparison.OrdinalIgnoreCase)) return null;
        if (swel.GetRange(channel) is not { } range || swel.GetView(channel) is not { } view) return null;
        if (range.Max <= range.Min) return null;

        // Straight line through (range.Min, view.Min) and (range.Max, view.Max).
        double scale = (view.Max - view.Min) / (range.Max - range.Min);
        return new PressureCalibration(scale, view.Min - range.Min * scale);
    }

    /// <summary>
    /// Converts samples as returned by WavFileReader.ReadSamples (raw value
    /// divided by full scale) to pascals.
    /// </summary>
    public float[] ToPascal(float[] samples, WavInfo info)
    {
        var pascal = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            double raw = samples[i] * info.FullScale;   // back to the raw sample value, as in the file
            pascal[i] = (float)(raw * PascalPerRawUnit + OffsetPascal);
        }
        return pascal;
    }
}
