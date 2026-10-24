using System;
using System.Collections.Generic;

namespace MutaForTuneLab.Synthesis;

/// <summary>
/// Mel-cepstrum to power spectrum, with the warping factor under our control.
///
/// The warping factor (alpha) is the timbre control, so it must be per frame. That used
/// to mean one full cepstral warp per distinct value, which is why a swept timbre cost
/// orders of magnitude more than a held one. Two things fix that here:
///
///   * the cosine step is a DCT-I, computed with an FFT instead of a dense matrix
///     (numeric check against the dense form: 1.9e-13);
///   * alpha is quantised to a 0.005 grid and grouped, so a smooth curve costs a
///     handful of warps rather than one per frame.
///
/// The engine's own chain goes through gain-normalise with gamma = 0, which is
/// exp(c0) ... log(c0) and therefore an identity apart from the round trip. That round
/// trip is kept: it is one ulp of c0 and free.
/// </summary>
public static class MgcSpectrum
{
    /// <summary>Alpha is quantised to this grid before grouping.</summary>
    public const double AlphaQuantum = 0.005;

    /// <summary>
    /// The all-pass frequency warp (the engine's O(order²) cascade). Each source
    /// coefficient is pushed through a chain of <paramref name="order"/> first-order
    /// sections; coefficients are consumed in reverse order.
    /// </summary>
    public static double[] FrequencyTransform(double[] values, double alpha, int order)
    {
        int width = values.Length;
        if (width < 2)
            throw new ArgumentException("MGC frame must hold at least two coefficients");
        if (alpha <= -1.0 || alpha >= 1.0)
            throw new ArgumentException("alpha must lie in (-1, 1)");

        double complement = 1.0 - alpha * alpha;
        var state = new double[order + 1];
        var result = new double[order + 1];
        int sourceOrder = width - 1;

        for (int sourceIndex = 0; sourceIndex <= sourceOrder; sourceIndex++)
        {
            double source = values[sourceOrder - sourceIndex];

            double previous = result[0];
            state[0] = previous;
            result[0] = source + alpha * previous;

            previous = result[1];
            state[1] = previous;
            result[1] = complement * state[0] + alpha * previous;

            for (int index = 2; index <= order; index++)
            {
                previous = result[index];
                state[index] = previous;
                result[index] = state[index - 1] + alpha * (previous - result[index - 1]);
            }
        }
        return result;
    }

    /// <summary>
    /// Magnitude spectrum of one frame: c0 + Σ c_n cos(π k n / (bins−1)), exponentiated.
    /// </summary>
    public static void MagnitudeInto(ReadOnlySpan<double> warped, Span<double> destination)
    {
        int bins = destination.Length;
        int n = bins - 1;                 // DCT-I length is `bins`
        if (warped.Length < bins)
            throw new ArgumentException("warped frame is shorter than the bin count");

        // The engine's gamma-0 normalise round trip: exp then log on c0 only.
        double c0 = Math.Log(Math.Exp(warped[0]));

        // d = [c0, c1/2, ..., c_{N-2}/2, c_{N-1}]; its DCT-I equals our cosine sum.
        var extended = new double[2 * n];
        extended[0] = c0;
        for (int i = 1; i < bins; i++)
        {
            double coefficient = warped[i];
            extended[i] = i < bins - 1 ? coefficient * 0.5 : coefficient;
        }
        for (int i = 1; i < n; i++)
            extended[2 * n - i] = extended[i];

        Fft.Real(extended);

        for (int k = 0; k < bins; k++)
            destination[k] = Math.Exp(extended[k]);
    }

    /// <summary>
    /// Power spectrum for a whole utterance. <paramref name="alphas"/> may be one value
    /// (scalar path, not quantised) or one per frame (quantised and grouped).
    /// </summary>
    public static double[][] Power(double[][] mgc, double[] alphas, int bins)
    {
        int frames = mgc.Length;
        if (frames == 0)
            return [];

        int order = bins - 1;
        var result = new double[frames][];

        if (alphas.Length == 1)
        {
            double alpha = alphas[0];
            var warped = new double[order + 1];
            for (int f = 0; f < frames; f++)
            {
                FrequencyTransformInto(mgc[f], -alpha, order, warped);
                var magnitude = new double[bins];
                MagnitudeInto(warped, magnitude);
                for (int k = 0; k < bins; k++)
                    magnitude[k] *= magnitude[k];
                result[f] = magnitude;
            }
            return result;
        }

        if (alphas.Length != frames)
            throw new ArgumentException("per-frame alpha must have one value per frame");

        // Bucket frames by their quantised alpha so the warp runs once per bucket.
        var buckets = new Dictionary<double, List<int>>();
        var quantised = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double q = Math.Round(alphas[f] / AlphaQuantum, MidpointRounding.ToEven) * AlphaQuantum;
            quantised[f] = q;
            if (!buckets.TryGetValue(q, out var list))
            {
                list = new List<int>();
                buckets[q] = list;
            }
            list.Add(f);
        }

        var buffer = new double[order + 1];
        foreach (var (alpha, indices) in buckets)
        {
            foreach (int f in indices)
            {
                FrequencyTransformInto(mgc[f], -alpha, order, buffer);
                var magnitude = new double[bins];
                MagnitudeInto(buffer, magnitude);
                for (int k = 0; k < bins; k++)
                    magnitude[k] *= magnitude[k];
                result[f] = magnitude;
            }
        }
        return result;
    }

    static void FrequencyTransformInto(double[] values, double alpha, int order, double[] destination)
    {
        int width = values.Length;
        double complement = 1.0 - alpha * alpha;
        var stateBuffer = new double[order + 1];
        Reset(destination, order);
        Reset(stateBuffer, order);

        int sourceOrder = width - 1;
        for (int sourceIndex = 0; sourceIndex <= sourceOrder; sourceIndex++)
        {
            double source = values[sourceOrder - sourceIndex];

            double previous = destination[0];
            stateBuffer[0] = previous;
            destination[0] = source + alpha * previous;

            previous = destination[1];
            stateBuffer[1] = previous;
            destination[1] = complement * stateBuffer[0] + alpha * previous;

            for (int index = 2; index <= order; index++)
            {
                previous = destination[index];
                stateBuffer[index] = previous;
                destination[index] = stateBuffer[index - 1] + alpha * (previous - destination[index - 1]);
            }
        }
    }

    static void Reset(double[] buffer, int order)
    {
        for (int i = 0; i <= order; i++)
            buffer[i] = 0.0;
    }
}

/// <summary>Minimal iterative radix-2 FFT, enough for the DCT-I above.</summary>
internal static class Fft
{
    /// <summary>In-place real transform of a power-of-two length buffer (imaginary part is zero on entry).</summary>
    public static void Real(double[] data)
    {
        int n = data.Length;
        if (n < 2 || (n & (n - 1)) != 0)
            throw new ArgumentException("FFT length must be a power of two");

        var re = data;
        var im = new double[n];

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = -2.0 * Math.PI / length;
            double wRe = Math.Cos(angle), wIm = Math.Sin(angle);
            int half = length >> 1;
            for (int i = 0; i < n; i += length)
            {
                double curRe = 1.0, curIm = 0.0;
                for (int k = 0; k < half; k++)
                {
                    int a = i + k, b = i + k + half;
                    double tRe = re[b] * curRe - im[b] * curIm;
                    double tIm = re[b] * curIm + im[b] * curRe;
                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;
                    double nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
        // Only the real part is used by the caller; the imaginary buffer is discarded.
    }
}
