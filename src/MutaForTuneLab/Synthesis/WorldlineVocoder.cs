using System;
using System.Runtime.InteropServices;
using MutaForTuneLab.Native;

namespace MutaForTuneLab.Synthesis;

public enum VocoderBackend
{
    /// <summary>worldline R1.1: WORLD's periodic half plus time-domain continuous noise.</summary>
    ContinuousNoise = 0,
    /// <summary>WORLD's own synthesis; kept as an A/B reference.</summary>
    World = 1,
}

/// <summary>Everything the vocoder needs beyond the acoustic parameters.</summary>
public sealed class VocoderSettings
{
    public required int FftSize { get; init; }
    public required double FramePeriodMs { get; init; }
    public required int SampleRate { get; init; }
    public VocoderBackend Backend { get; init; } = VocoderBackend.ContinuousNoise;
    /// <summary>Noise seed (continuous-noise backend only); same seed, same noise.</summary>
    public ulong Seed { get; init; } = 1;
    /// <summary>Output milliseconds per source millisecond, per frame. 1 flattens the noise.</summary>
    public double[]? Stretch { get; init; }
}

/// <summary>Per-frame expression curves passed straight to the vocoder (0..1).</summary>
public sealed class VocoderExpression
{
    public double[]? Gender { get; init; }
    public double[]? Tension { get; init; }
    public double[]? Breathiness { get; init; }
    public double[]? Voicing { get; init; }

    public static double[] Fill(int frames, double value)
    {
        var array = new double[frames];
        Array.Fill(array, value);
        return array;
    }
}

/// <summary>
/// The grid a backend produces. The host accepts a sample rate per audio segment, so the
/// plugin never resamples to the project rate: it declares what it made and lets the host
/// mix. That is why R1.1 can stay at 48 kHz while R2 comes out at 44.1 kHz.
/// </summary>
public readonly record struct VocoderFormat(int SampleRate, double FramePeriodMs, int FftSize)
{
    public int FrameSamples => (int)Math.Round(SampleRate * FramePeriodMs / 1000.0);

    /// <summary>48 kHz, 5 ms — the model's own rate, used by the continuous-noise backend.</summary>
    public static VocoderFormat ModelRate { get; } = new(48000, 5.0, 2048);

    /// <summary>44.1 kHz, hop 512 — the neural vocoder's rate.</summary>
    public static VocoderFormat NeuralRate { get; } = new(44100, 512 * 1000.0 / 44100.0, 2048);
}

/// <summary>
/// The vocoder: power spectrum + aperiodicity + F0 in, samples out.
///
/// This is the only place that produces audio. Both backends take the parameters as
/// full spectra (not as mel-cepstra or 26-band BAP), because the warping factor and the
/// band expansion are ours to control, not the vocoder's.
/// </summary>
public static class WorldlineVocoder
{
    const string Library = "worldline";

    static WorldlineVocoder() => NativeLibraries.EnsureResolver();

    /// <summary>Sample count the backend will write for a given frame count.</summary>
    public static int SampleCount(int frames, double framePeriodMs, int sampleRate)
        => frames <= 0 ? 0 : muta_count(frames, framePeriodMs, sampleRate);

    public static double[] Synthesize(
        double[][] sp, double[][] ap, double[] f0,
        VocoderSettings settings, VocoderExpression expression)
    {
        int frames = f0.Length;
        if (frames == 0)
            return [];
        if (sp.Length != frames || ap.Length != frames)
            throw new ArgumentException("sp, ap and f0 must have the same frame count");

        int bins = sp[0].Length;
        // The native side indexes sp/ap by fft_size/2+1. If a caller hands us a different
        // bin count, that is an out-of-bounds read, not a length mismatch we can recover.
        if (bins != settings.FftSize / 2 + 1)
            throw new ArgumentException(
                $"spectra have {bins} bins but FftSize {settings.FftSize} needs {settings.FftSize / 2 + 1}");
        var gender = expression.Gender ?? VocoderExpression.Fill(frames, 0.5);
        var tension = expression.Tension ?? VocoderExpression.Fill(frames, 0.5);
        var breathiness = expression.Breathiness ?? VocoderExpression.Fill(frames, 0.5);
        var voicing = expression.Voicing ?? VocoderExpression.Fill(frames, 1.0);
        if (gender.Length != frames || tension.Length != frames
            || breathiness.Length != frames || voicing.Length != frames)
            throw new ArgumentException("expression curves must have one value per frame");

        var f0Copy = (double[])f0.Clone();
        var spectral = Flatten(sp);
        var aperiodicity = Flatten(ap);
        // The native function re-derives the frame period from the integer hop it is given
        // (hop * 1000 / fs), so sizing the managed buffer from the unrounded milliseconds
        // can make the native write past the end. Use the same hop-derived period.
        int hop = Math.Max(1, (int)Math.Round(settings.SampleRate * settings.FramePeriodMs / 1000.0));
        double derivedPeriodMs = hop * 1000.0 / settings.SampleRate;
        // WorldSynthesis receives the unrounded period; the continuous backend receives hop.
        double periodMs = settings.Backend == VocoderBackend.World
            ? settings.FramePeriodMs
            : derivedPeriodMs;
        int samples = SampleCount(frames, periodMs, settings.SampleRate);
        var output = new double[samples];

        if (settings.Backend == VocoderBackend.World)
        {
            int written = WorldSynthesis(
                f0Copy, frames, spectral, 0, bins, aperiodicity, 0,
                settings.FftSize, settings.FramePeriodMs, settings.SampleRate, output,
                gender, tension, breathiness, voicing);
            if (written != samples)
                throw new InvalidOperationException($"WorldSynthesis wrote {written} of {samples} samples");
            return output;
        }

        // The continuous-noise backend wants the harmonic half separated out. Our
        // aperiodicity already is the noise fraction, so the harmonic envelope is
        // simply the total minus the noise part.
        var harmonic = new double[spectral.Length];
        for (int i = 0; i < spectral.Length; i++)
            harmonic[i] = Math.Max(spectral[i] * (1.0 - aperiodicity[i]), 1e-12);

        var stretch = settings.Stretch ?? VocoderExpression.Fill(frames, 1.0);
        if (stretch.Length != frames)
            throw new ArgumentException("stretch must have one value per frame");

        int writtenContinuous = WorldSynthesisContinuousNoise(
            f0Copy, frames, spectral, harmonic, aperiodicity, stretch,
            settings.FftSize, hop, settings.SampleRate, settings.Seed, output,
            gender, tension, breathiness, voicing);
        if (writtenContinuous != samples)
            throw new InvalidOperationException(
                $"WorldSynthesisContinuousNoise wrote {writtenContinuous} of {samples} samples");
        return output;
    }

    static double[] Flatten(double[][] matrix)
    {
        int width = matrix.Length == 0 ? 0 : matrix[0].Length;
        var flat = new double[matrix.Length * width];
        for (int f = 0; f < matrix.Length; f++)
            Array.Copy(matrix[f], 0, flat, f * width, width);
        return flat;
    }

    [DllImport(Library, EntryPoint = "WorldSynthesisSampleCount", CallingConvention = CallingConvention.Cdecl)]
    static extern int muta_count(int frames, double framePeriodMs, int sampleRate);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int WorldSynthesis(
        double[] f0, int f0Length,
        double[] spOrMgc, [MarshalAs(UnmanagedType.U1)] byte isMgc, int mgcSize,
        double[] apOrBap, [MarshalAs(UnmanagedType.U1)] byte isBap,
        int fftSize, double framePeriod, int sampleRate, double[] output,
        double[] gender, double[] tension, double[] breathiness, double[] voicing);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int WorldSynthesisContinuousNoise(
        double[] f0, int f0Length,
        double[] sp, double[] harmonicSp, double[] ap, double[] stretch,
        int fftSize, int hopSize, int sampleRate, ulong seed, double[] output,
        double[] gender, double[] tension, double[] breathiness, double[] voicing);
}
