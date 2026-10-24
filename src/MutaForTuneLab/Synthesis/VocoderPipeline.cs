using System;

namespace MutaForTuneLab.Synthesis;

/// <summary>Which vocoder renders a span.</summary>
public enum VocoderMode
{
    /// <summary>WORLDLINE R1.1 — built in, 48 kHz.</summary>
    WorldlineR11,
    /// <summary>WORLDLINE R2 — the user's neural vocoder, 44.1 kHz.</summary>
    WorldlineR2,
}

/// <summary>Rendered audio plus the rate it came out at.</summary>
public readonly record struct VocoderOutput(double[] Samples, int SampleRate);

/// <summary>
/// The single entry point from parameters to sound.
///
/// Everything before this stage works on the model's grid (48 kHz, 5 ms). A backend may
/// want a different grid, and this is where that gets decided: R1.1 renders in place, R2
/// converts the parameters first, and both report the rate they produced so the host can
/// take each segment at its own rate.
///
/// The mode falls back rather than failing: a missing R2 package, or a mode that this
/// build cannot serve, renders on R1.1 instead of leaving a hole in the song.
/// </summary>
public static class VocoderPipeline
{
    public const int ModelRate = 48000;
    public const double ModelFrameMs = 5.0;
    public const int Bins = 1025;

    public static VocoderMode ParseMode(string? value) => value switch
    {
        "r2" => VocoderMode.WorldlineR2,
        _ => VocoderMode.WorldlineR11,
    };

    /// <summary>
    /// Renders one span. <paramref name="alpha"/> is the warping factor (see the timbre
    /// control); <paramref name="r2"/> may be null when nothing is installed.
    /// </summary>
    public static VocoderOutput Render(
        double[][] mgc, double[][] bap, double[] f0, bool[] silence,
        double[] alphas, VocoderExpression expression, VocoderMode mode,
        R2Vocoder? r2, out VocoderMode used)
    {
        int frames = f0.Length;
        if (frames == 0 || mgc.Length != frames || bap.Length != frames)
        {
            used = mode;
            return new VocoderOutput([], ModelRate);
        }

        if (mode == VocoderMode.WorldlineR2 && r2 is not null)
        {
            var spGrid = Power(mgc, alphas, silence);
            var apGrid = Aperiodicity(bap);
            used = mode;
            // R2 builds its own spectra: it does the grid conversion itself.
            return new VocoderOutput(r2.Synthesize(spGrid, apGrid, f0, silence, ModelFrameMs, ModelRate), r2.SampleRate);
        }

        used = mode == VocoderMode.WorldlineR2 ? VocoderMode.WorldlineR11 : mode;
        var sp = Power(mgc, alphas, silence);
        var ap = Aperiodicity(bap);
        var settings = new VocoderSettings
        {
            FftSize = 2048,
            FramePeriodMs = ModelFrameMs,
            SampleRate = ModelRate,
            Backend = VocoderBackend.ContinuousNoise,
        };
        var samples = WorldlineVocoder.Synthesize(sp, ap, f0, settings, expression);

        // The vocoder keeps going through a rest; the host must hear nothing there.
        int frameSamples = (int)Math.Round(settings.SampleRate * settings.FramePeriodMs / 1000.0);
        OutputGate.ApplySilence(samples, silence, frameSamples);
        return new VocoderOutput(samples, ModelRate);
    }

    /// <summary>MGC to power spectrum, with rests held at the sentinel value.</summary>
    public static double[][] Power(double[][] mgc, double[] alphas, ReadOnlySpan<bool> silence)
    {
        var sp = MgcSpectrum.Power(mgc, alphas, Bins);
        for (int f = 0; f < sp.Length; f++)
            if (silence[f])
                Array.Fill(sp[f], 1e-12);
        return sp;
    }

    /// <summary>26-band aperiodicity to per-bin weights.</summary>
    public static double[][] Aperiodicity(double[][] bap)
    {
        var ap = BapExpand.ExpandAny(bap, Bins);
        for (int f = 0; f < ap.Length; f++)
            for (int k = 0; k < ap[f].Length; k++)
                ap[f][k] = BapExpand.ToWeight(ap[f][k]);
        return ap;
    }

    /// <summary>Rests, as the model marks them: the engine writes a log-gain of -9.</summary>
    public static bool[] DetectSilence(double[][] mgc)
    {
        var silence = new bool[mgc.Length];
        for (int f = 0; f < mgc.Length; f++)
        {
            double max = 0.0;
            foreach (var value in mgc[f])
                max = Math.Max(max, Math.Abs(value));
            silence[f] = max < 1e-6 || mgc[f][0] <= -8.5;
        }
        return silence;
    }
}
