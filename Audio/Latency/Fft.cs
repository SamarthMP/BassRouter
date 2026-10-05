using System.Numerics;

namespace BassRouter.Audio.Latency;

/// <summary>
/// In-place radix-2 FFT on separate real and imaginary arrays.
/// </summary>
internal static class Fft
{
    public static int GetSize(int minimumLength)
    {
        return (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(2, minimumLength));
    }

    /// <summary>
    /// Transforms in place. The inverse transform isn't scaled by 1/N.
    /// </summary>
    public static void Transform(double[] real, double[] imaginary, bool inverse)
    {
        int n = real.Length;
        if (!BitOperations.IsPow2(n) || imaginary.Length != n)
            throw new ArgumentException("Both arrays must have the same power of two length.");

        // Bit reversal permutation
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;

            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        // Twiddle factors for the largest stage, smaller stages use every n/size-th one
        int half = n / 2;
        double[] cos = new double[half];
        double[] sin = new double[half];
        double sign = inverse ? 1 : -1;
        for (int k = 0; k < half; k++)
        {
            double angle = sign * 2 * Math.PI * k / n;
            cos[k] = Math.Cos(angle);
            sin[k] = Math.Sin(angle);
        }

        for (int size = 2; size <= n; size <<= 1)
        {
            int halfSize = size / 2;
            int step = n / size;

            for (int start = 0; start < n; start += size)
            {
                for (int k = 0; k < halfSize; k++)
                {
                    int even = start + k;
                    int odd = even + halfSize;
                    double wr = cos[k * step];
                    double wi = sin[k * step];

                    double oddReal = real[odd] * wr - imaginary[odd] * wi;
                    double oddImaginary = real[odd] * wi + imaginary[odd] * wr;

                    real[odd] = real[even] - oddReal;
                    imaginary[odd] = imaginary[even] - oddImaginary;
                    real[even] += oddReal;
                    imaginary[even] += oddImaginary;
                }
            }
        }
    }
}
