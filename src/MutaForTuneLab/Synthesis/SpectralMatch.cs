using System;

namespace MutaForTuneLab.Synthesis;

/// <summary>
/// Matches the recorded layer's long-term spectrum to the model's.
///
/// The two layers come from the same voice but not from the same process: the model
/// predicts a smoothed envelope, the bank holds recordings, and they disagree by about
/// ±7 dB around the first formant — the bank is bass heavy, the model mid heavy — while
/// agreeing above 4 kHz. Left alone the two paths sound like different singers.
///
/// What is removed here is only that systematic part: the average spectrum of each layer
/// over the same span, with the overall level divided out. Note-to-note detail, formant
/// movement and the recorded character all survive, because they average out of both sides.
/// </summary>
public static class SpectralMatch
{
    /// <summary>Correction never exceeds this, so a pathological frame cannot reshape the voice.</summary>
    public const double MaxCorrectionDecibels = 9.0;

    /// <summary>
    /// Per-bin gain that turns <paramref name="source"/>'s average spectrum into
    /// <paramref name="target"/>'s, both normalised to their own mean so the result carries
    /// shape only and never level.
    /// </summary>
    public static double[]? BuildGain(
        double[][] target, double[][] source, double alpha, int bins, double maxDecibels = MaxCorrectionDecibels)
    {
        var averageTarget = Average(target, alpha, bins);
        var averageSource = Average(source, alpha, bins);
        if (averageTarget is null || averageSource is null)
            return null;

        double meanTarget = Mean(averageTarget);
        double meanSource = Mean(averageSource);
        if (meanTarget <= 0.0 || meanSource <= 0.0)
            return null;

        var gain = new double[bins];
        for (int bin = 0; bin < bins; bin++)
        {
            double ratio = (averageTarget[bin] / meanTarget) / Math.Max(1e-30, averageSource[bin] / meanSource);
            double decibels = Math.Clamp(10.0 * Math.Log10(ratio), -maxDecibels, maxDecibels);
            gain[bin] = Math.Pow(10.0, decibels / 20.0);
        }

        // Smooth over a third of an octave: the averages are noisy frame to frame, and a
        // jagged correction would be audible as a filter ring.
        return Smooth(gain, thirdOctaveBins: Math.Max(1, bins / 96.0));
    }

    static double[]? Average(double[][] mgc, double alpha, int bins)
    {
        var total = new double[bins];
        int frames = 0;
        foreach (var frame in mgc)
        {
            if (frame.Length == 0 || frame[0] <= -8.5)
                continue;
            var spectrum = MgcSpectrum.Power(new[] { frame }, new[] { alpha }, bins)[0];
            for (int bin = 0; bin < bins; bin++)
                total[bin] += spectrum[bin];
            frames++;
        }
        if (frames == 0)
            return null;
        for (int bin = 0; bin < bins; bin++)
            total[bin] /= frames;
        return total;
    }

    static double Mean(double[] values)
    {
        double sum = 0.0;
        for (int i = 1; i < values.Length; i++)   // bin 0 is the mean level, not a band
            sum += values[i];
        return values.Length > 1 ? sum / (values.Length - 1) : 0.0;
    }

    static double[] Smooth(double[] values, double thirdOctaveBins)
    {
        int radius = Math.Max(1, (int)Math.Round(thirdOctaveBins));
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
}
