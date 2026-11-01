using System;
using System.Numerics;

namespace MasterArbete;

/// <summary>Fast Fourier transform (radix-2, in place).</summary>
internal static class Fft
{
    /// <summary>
    /// Replaces the signal in <paramref name="data"/> with its spectrum.
    /// The length must be a power of two.
    /// </summary>
    public static void Transform(Complex[] data)
    {
        int n = data.Length;
        if (n == 0 || (n & (n - 1)) != 0)
            throw new ArgumentException("FFT length must be a power of two.", nameof(data));

        // Reorder the samples by bit-reversed index, so the butterflies below
        // can work in place.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }

        // Combine transforms of length 2, 4, 8, ... up to n.
        for (int length = 2; length <= n; length <<= 1)
        {
            var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (int start = 0; start < n; start += length)
            {
                Complex twiddle = 1;
                for (int k = 0; k < length / 2; k++)
                {
                    Complex even = data[start + k];
                    Complex odd = data[start + k + length / 2] * twiddle;
                    data[start + k] = even + odd;
                    data[start + k + length / 2] = even - odd;
                    twiddle *= step;
                }
            }
        }
    }
}
