using System;

namespace MutaForTuneLab.Synthesis;

/// <summary>
/// Corrects the recorded layer's tone in the audio domain.
///
/// Matching in the parameter domain ran out of road: it equalises the average *envelope*,
/// but the vocoder samples that envelope through its own harmonic structure, and averaging
/// does not survive that map. Measured on the rendered signal, about half the tilt was
/// still there.
///
/// So the difference is measured where it is heard. Both layers are rendered once, their
/// average spectra are compared, and the ratio is applied to the recorded layer's samples
/// as an overlap-add filter. That is exact by construction: it is the difference of the two
/// signals, not of their inputs.
/// </summary>
public static class AudioEqualizer
{
    public const int FftSize = 2048;
    public const int Hop = FftSize / 2;
    public const double MaxCorrectionDecibels = 9.0;

    /// <summary>
    /// Average magnitude spectrum over the loud parts, so silence does not dominate.
    /// </summary>
    public static double[]? AverageSpectrum(double[] audio, double thresholdRatio = 0.25)
    {
        int bins = FftSize / 2 + 1;
        if (audio.Length < FftSize)
            return null;

        double energy = 0.0;
        foreach (var sample in audio)
            energy += (double)sample * sample;
        double rms = Math.Sqrt(energy / audio.Length);
        if (rms <= 0.0)
            return null;

        var total = new double[bins];
        var window = Window();
        int used = 0;
        var re = new double[FftSize];
        var im = new double[FftSize];

        for (int start = 0; start + FftSize <= audio.Length; start += Hop)
        {
            double here = 0.0;
            for (int i = 0; i < FftSize; i++)
                here += (double)audio[start + i] * audio[start + i];
            if (Math.Sqrt(here / FftSize) < rms * thresholdRatio)
                continue;

            for (int i = 0; i < FftSize; i++)
            {
                re[i] = audio[start + i] * window[i];
                im[i] = 0.0;
            }
            Fft.Forward(re, im);
            for (int bin = 0; bin < bins; bin++)
                total[bin] += Math.Sqrt(re[bin] * re[bin] + im[bin] * im[bin]);
            used++;
        }

        if (used == 0)
            return null;
        for (int bin = 0; bin < bins; bin++)
            total[bin] /= used;
        return total;
    }

    /// <summary>
    /// Per-bin gain that turns <paramref name="source"/>'s average spectrum into
    /// <paramref name="target"/>'s. Both are normalised by their own mean, so this carries
    /// tone only and never level.
    /// </summary>
    public static double[]? BuildCurve(double[] target, double[] source, double maxDecibels = MaxCorrectionDecibels)
    {
        int bins = Math.Min(target.Length, source.Length);
        if (bins < 8)
            return null;

        double meanTarget = Mean(target, bins);
        double meanSource = Mean(source, bins);
        if (meanTarget <= 0.0 || meanSource <= 0.0)
            return null;

        var curve = new double[bins];
        for (int bin = 0; bin < bins; bin++)
        {
            double ratio = (target[bin] / meanTarget) / Math.Max(1e-30, source[bin] / meanSource);
            double decibels = Math.Clamp(20.0 * Math.Log10(ratio), -maxDecibels, maxDecibels);
            curve[bin] = Math.Pow(10.0, decibels / 20.0);
        }
        return Smooth(curve, Math.Max(1, bins / 48.0));
    }

    /// <summary>Applies the curve to the signal in place, with a weighted overlap-add.</summary>
    public static void Apply(double[] audio, double[] curve)
    {
        if (audio.Length < FftSize || curve.Length < FftSize / 2 + 1)
            return;

        int bins = FftSize / 2 + 1;
        var window = Window();
        var output = new double[audio.Length];
        var weight = new double[audio.Length];
        var re = new double[FftSize];
        var im = new double[FftSize];

        for (int start = 0; start + FftSize <= audio.Length; start += Hop)
        {
            for (int i = 0; i < FftSize; i++)
            {
                re[i] = audio[start + i] * window[i];
                im[i] = 0.0;
            }
            Fft.Forward(re, im);

            for (int bin = 0; bin < bins; bin++)
            {
                double gain = curve[bin];
                re[bin] *= gain;
                im[bin] *= gain;
                if (bin > 0 && bin < FftSize - bin)
                {
                    int mirror = FftSize - bin;
                    re[mirror] *= gain;
                    im[mirror] *= gain;
                }
            }

            Fft.Inverse(re, im);
            for (int i = 0; i < FftSize; i++)
            {
                double w = window[i];
                output[start + i] += re[i] * w;
                weight[start + i] += w * w;
            }
        }

        // The first and last half window are covered by one frame only, so the overlap-add
        // cannot reconstruct them: cross-fade back to the original there instead of dividing
        // by a tiny weight, which would ring.
        for (int i = 0; i < audio.Length; i++)
        {
            double value = weight[i] > 1e-9 ? output[i] / weight[i] : audio[i];
            int distance = Math.Min(i, audio.Length - 1 - i);
            if (distance < Hop)
            {
                double alpha = (double)distance / Hop;
                value = alpha * value + (1.0 - alpha) * audio[i];
            }
            audio[i] = Math.Clamp(value, -1.0, 1.0);
        }
    }

    static double Mean(double[] values, int count)
    {
        double sum = 0.0;
        for (int i = 1; i < count; i++)   // bin 0 is the mean level, not a band
            sum += values[i];
        return count > 1 ? sum / (count - 1) : 0.0;
    }

    static double[] Smooth(double[] values, double radiusBins)
    {
        int radius = Math.Max(1, (int)Math.Round(radiusBins));
        var smoothed = new double[values.Length];
        for (int bin = 0; bin < values.Length; bin++)
        {
            double sum = 0.0;
            int count = 0;
            for (int k = Math.Max(0, bin - radius); k <= Math.Min(values.Length - 1, bin + radius); k++)
            {
                sum += values[k];
                count++;
            }
            smoothed[bin] = count > 0 ? sum / count : 1.0;
        }
        return smoothed;
    }

    static double[] Window()
    {
        var window = new double[FftSize];
        for (int i = 0; i < FftSize; i++)
            window[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (FftSize - 1));
        return window;
    }
}
