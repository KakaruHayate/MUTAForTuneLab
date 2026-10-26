using System;

namespace MutaForTuneLab.Synthesis;

/// <summary>
/// Post-processing that the vocoder itself has no notion of.
///
/// The vocoder keeps synthesising through a rest, because a rest is just a very quiet
/// spectrum with full aperiodicity — and "very quiet noise" is still audible in a silent
/// room (about −71 dBFS here). The engine avoids that by zeroing those spans in the PCM
/// domain after synthesis, which is what this does, with a short ramp so the edge does
/// not click.
///
/// The second job is the opposite case: where voicing starts or stops mid-utterance the
/// parameters step and the vocoder rings briefly. A short dip at the crossing hides it.
/// </summary>
public static class OutputGate
{
    /// <summary>Half-length of the ramp, in frames.</summary>
    public const int RampFrames = 2;
    /// <summary>Half-length of the voicing notch, in frames.</summary>
    public const int NotchFrames = 2;

    /// <summary>
    /// Multiplies <paramref name="samples"/> in place by an envelope that is 0 across
    /// silent frames and 1 elsewhere, ramped over <paramref name="rampFrames"/> frames.
    /// </summary>
    public static void ApplySilence(
        double[] samples, ReadOnlySpan<bool> silence, int frameSamples, int rampFrames = RampFrames)
    {
        if (samples.Length == 0 || frameSamples <= 0)
            return;

        var envelope = BuildSilenceEnvelope(samples.Length, silence, frameSamples, rampFrames);
        for (int i = 0; i < samples.Length; i++)
            samples[i] *= envelope[i];
    }

    /// <summary>The envelope <see cref="ApplySilence"/> uses, exposed for inspection and tests.</summary>
    public static double[] BuildSilenceEnvelope(
        int sampleCount, ReadOnlySpan<bool> silence, int frameSamples, int rampFrames = RampFrames)
    {
        var gate = new double[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            int frame = i / frameSamples;
            gate[i] = frame < silence.Length && silence[frame] ? 0.0 : 1.0;
        }
        if (rampFrames <= 0)
            return gate;

        // Smooth the hard 0/1 edge; the kernel is normalised so a lone 1 stays a 1.
        int span = rampFrames * frameSamples;
        var window = new double[2 * span + 1];
        double total = 0.0;
        for (int i = 0; i < window.Length; i++)
        {
            window[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (window.Length - 1));
            total += window[i];
        }
        for (int i = 0; i < window.Length; i++)
            window[i] /= total;

        var smoothed = new double[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            double sum = 0.0;
            for (int k = 0; k < window.Length; k++)
            {
                int index = i + k - span;
                if (index >= 0 && index < sampleCount)
                    sum += gate[index] * window[k];
            }
            smoothed[i] = Math.Clamp(sum, 0.0, 1.0);
        }
        return smoothed;
    }

    /// <summary>Dips the envelope at every voicing transition.</summary>
    public static void ApplyVoicingNotch(
        double[] envelope, ReadOnlySpan<double> f0, int frameSamples, int notchFrames = NotchFrames)
    {
        if (envelope.Length == 0 || f0.Length < 2 || frameSamples <= 0 || notchFrames <= 0)
            return;

        int span = notchFrames * frameSamples;
        var notch = new double[2 * span + 1];
        for (int i = 0; i < notch.Length; i++)
            notch[i] = 1.0 - (0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (notch.Length - 1)));

        for (int frame = 1; frame < f0.Length; frame++)
        {
            bool previous = f0[frame - 1] > 0.0;
            bool current = f0[frame] > 0.0;
            if (previous == current)
                continue;

            int centre = frame * frameSamples;
            int from = Math.Max(0, centre - span);
            int to = Math.Min(envelope.Length, centre + span + 1);
            int length = to - from;
            if (length <= 1)
                continue;

            for (int i = 0; i < length; i++)
            {
                int k = Math.Min(notch.Length - 1, i * notch.Length / length);
                envelope[from + i] *= notch[k];
            }
        }
    }
}
