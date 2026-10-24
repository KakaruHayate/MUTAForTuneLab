using System;

namespace MutaForTuneLab.Synthesis;

/// <summary>
/// Moving acoustic parameters onto another vocoder grid.
///
/// Our parameters are 48 kHz with 5 ms frames because that is what the model and the bank
/// are in. A vocoder that wants a different rate needs two separate conversions, and
/// confusing them is the classic mistake:
///
///   * the time axis: frame k covers 5 ms here and 11.6 ms there;
///   * the frequency axis: bin k is a different number of hertz, so a bin-for-bin copy
///     would slide every formant by the rate ratio (48/44.1 ≈ 8.8%, about 1.5 semitones).
///     Each destination bin is therefore evaluated at its own physical frequency.
///
/// F0 moves in the log domain, and frames only count as voiced when most of the source
/// frames around them were voiced — a half-voiced frame would otherwise become a very
/// low pitch instead of a rest.
/// </summary>
public static class RateConversion
{
    /// <summary>Linear interpolation with the reference's out-of-range behaviour: clamp.</summary>
    public static double Interp(double x, ReadOnlySpan<double> xs, ReadOnlySpan<double> ys)
    {
        int n = xs.Length;
        if (n == 0)
            return 0.0;
        if (n == 1)
            return ys[0];
        if (x <= xs[0])
            return ys[0];
        if (x >= xs[n - 1])
            return ys[n - 1];

        int low = 0, high = n - 1;
        while (high - low > 1)
        {
            int middle = (low + high) >> 1;
            if (xs[middle] <= x)
                low = middle;
            else
                high = middle;
        }
        double span = xs[high] - xs[low];
        if (span == 0.0)
            return ys[low];
        double weight = (x - xs[low]) / span;
        return ys[low] * (1.0 - weight) + ys[high] * weight;
    }

    /// <summary>Source frame times in milliseconds.</summary>
    public static double[] FrameTimes(int frames, double frameMs)
    {
        var times = new double[frames];
        for (int i = 0; i < frames; i++)
            times[i] = i * frameMs;
        return times;
    }

    /// <summary>Pitch onto the new grid: logarithmic inside, and unvoiced stays unvoiced.</summary>
    public static double[] ResampleF0(double[] f0, double sourceFrameMs, double targetFrameMs)
    {
        int frames = f0.Length;
        double totalMs = frames * sourceFrameMs;
        int targetFrames = (int)Math.Round(totalMs / targetFrameMs);
        var result = new double[Math.Max(0, targetFrames)];
        if (result.Length == 0)
            return result;

        var sourceTimes = FrameTimes(frames, sourceFrameMs);
        var targetTimes = FrameTimes(result.Length, targetFrameMs);

        int voicedCount = 0;
        for (int i = 0; i < frames; i++)
            if (f0[i] > 0.0)
                voicedCount++;
        if (voicedCount < 2)
            return result;   // all zeros

        var voicedTimes = new double[voicedCount];
        var voicedLog = new double[voicedCount];
        var voicedMask = new double[frames];
        int index = 0;
        for (int i = 0; i < frames; i++)
        {
            voicedMask[i] = f0[i] > 0.0 ? 1.0 : 0.0;
            if (f0[i] > 0.0)
            {
                voicedTimes[index] = sourceTimes[i];
                voicedLog[index] = Math.Log(f0[i]);
                index++;
            }
        }

        for (int i = 0; i < result.Length; i++)
        {
            double pitch = Math.Exp(Interp(targetTimes[i], voicedTimes, voicedLog));
            double mask = Interp(targetTimes[i], sourceTimes, voicedMask);
            result[i] = mask > 0.5 ? pitch : 0.0;
        }
        return result;
    }

    /// <summary>Linear interpolation of each column onto the new time grid.</summary>
    public static double[][] ResampleFrames(double[][] values, double sourceFrameMs, double targetFrameMs)
    {
        int frames = values.Length;
        if (frames == 0)
            return [];
        int width = values[0].Length;

        double totalMs = frames * sourceFrameMs;
        int targetFrames = Math.Max(0, (int)Math.Round(totalMs / targetFrameMs));
        var result = new double[targetFrames][];
        if (targetFrames == 0)
            return result;

        var sourceTimes = FrameTimes(frames, sourceFrameMs);
        var targetTimes = FrameTimes(targetFrames, targetFrameMs);
        var column = new double[frames];

        for (int t = 0; t < targetFrames; t++)
            result[t] = new double[width];

        for (int c = 0; c < width; c++)
        {
            for (int f = 0; f < frames; f++)
                column[f] = values[f][c];
            for (int t = 0; t < targetFrames; t++)
                result[t][c] = Interp(targetTimes[t], sourceTimes, column);
        }
        return result;
    }

    /// <summary>
    /// Re-evaluates a spectrum on the destination rate's bin frequencies. Values above the
    /// destination's Nyquist clamp to the last source bin.
    /// </summary>
    public static double[][] RemapFrequency(
        double[][] values, int sourceRate, int targetRate, int fftSize)
    {
        int bins = values.Length == 0 ? 0 : values[0].Length;
        if (bins == 0)
            return values;

        double sourceStep = (double)sourceRate / fftSize;
        var positions = new double[bins];
        var lower = new int[bins];
        var upper = new int[bins];
        var fraction = new double[bins];

        for (int k = 0; k < bins; k++)
        {
            double hertz = k * ((double)targetRate / fftSize);
            double position = Math.Clamp(hertz / sourceStep, 0.0, bins - 1);
            positions[k] = position;
            lower[k] = (int)Math.Floor(position);
            upper[k] = Math.Min(lower[k] + 1, bins - 1);
            fraction[k] = position - lower[k];
        }

        var result = new double[values.Length][];
        for (int f = 0; f < values.Length; f++)
        {
            var row = new double[bins];
            for (int k = 0; k < bins; k++)
                row[k] = values[f][lower[k]] * (1.0 - fraction[k]) + values[f][upper[k]] * fraction[k];
            result[f] = row;
        }
        return result;
    }

    /// <summary>
    /// Whole conversion: 48 kHz / 5 ms parameters onto another vocoder grid, in the order
    /// the reference uses (time first, then frequency, then the silence sentinel).
    /// </summary>
    public static (double[][] Sp, double[][] Ap, double[] F0) Convert(
        double[][] power, double[][] aperiodicity, double[] f0, bool[] silence,
        double sourceFrameMs, double targetFrameMs,
        int sourceRate, int targetRate, int fftSize)
    {
        int frames = f0.Length;
        int bins = power.Length == 0 ? 0 : power[0].Length;

        var logPower = new double[frames][];
        for (int f = 0; f < frames; f++)
        {
            logPower[f] = new double[bins];
            for (int k = 0; k < bins; k++)
                logPower[f][k] = Math.Log(Math.Max(power[f][k], 1e-20));
        }

        var targetF0 = ResampleF0(f0, sourceFrameMs, targetFrameMs);
        var logPowerTarget = ResampleFrames(logPower, sourceFrameMs, targetFrameMs);
        var apTarget = ResampleFrames(aperiodicity, sourceFrameMs, targetFrameMs);
        logPowerTarget = RemapFrequency(logPowerTarget, sourceRate, targetRate, fftSize);
        apTarget = RemapFrequency(apTarget, sourceRate, targetRate, fftSize);

        var silenceColumn = new double[frames][];
        for (int f = 0; f < frames; f++)
            silenceColumn[f] = new[] { silence[f] ? 1.0 : 0.0 };
        var silenceTarget = ResampleFrames(silenceColumn, sourceFrameMs, targetFrameMs);

        int targetFrames = targetF0.Length;
        var sp = new double[targetFrames][];
        var ap = new double[targetFrames][];
        for (int t = 0; t < targetFrames; t++)
        {
            sp[t] = new double[bins];
            ap[t] = new double[bins];
            bool muted = silenceTarget[t][0] > 0.5;
            for (int k = 0; k < bins; k++)
            {
                sp[t][k] = muted ? 1e-12 : Math.Exp(logPowerTarget[t][k]);
                ap[t][k] = Math.Clamp(apTarget[t][k], 0.001, 1.0);
            }
        }
        return (sp, ap, targetF0);
    }
}
