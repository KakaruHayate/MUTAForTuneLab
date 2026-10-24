using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MutaForTuneLab.Synthesis;

/// <summary>
/// WORLDLINE R2: a neural vocoder the user installs themselves.
///
/// The plugin ships none of it — the model is not ours to redistribute — so this reads a
/// folder the user points at, expecting the same layout the OpenUtau package uses:
/// a vocoder.yaml with the model's file name and its grid, next to the model itself.
///
/// The chain is two models: ours maps WORLD features (F0, spectral envelope,
/// aperiodicity) to a log mel spectrogram, then the user's model turns that into a
/// waveform. Both want the parameters on a 44.1 kHz / hop 512 grid, which is not the grid
/// the rest of the pipeline works on — converting is <see cref="RateConversion"/>'s job.
/// </summary>
public sealed class R2Vocoder : IDisposable
{
    /// <summary>Frames of context added on each side before the models see anything.</summary>
    const int PaddingFrames = 4;
    /// <summary>The network wants a multiple of 16 frames.</summary>
    const int FrameMultiple = 16;

    readonly InferenceSession mMel;
    readonly InferenceSession mVocoder;
    readonly string mInputName;
    readonly string mOutputName;

    public int SampleRate { get; }
    public int HopSize { get; }
    public int FftSize { get; }
    public int MelBins { get; }
    public string Name { get; }

    R2Vocoder(InferenceSession mel, InferenceSession vocoder, int sampleRate, int hopSize,
              int fftSize, int melBins, string name)
    {
        mMel = mel;
        mVocoder = vocoder;
        SampleRate = sampleRate;
        HopSize = hopSize;
        FftSize = fftSize;
        MelBins = melBins;
        Name = name;
        mInputName = vocoder.InputMetadata.Keys.First();
        mOutputName = vocoder.OutputMetadata.Keys.First();
    }

    /// <summary>
    /// Loads the user's package plus our mel model. Returns null when nothing is
    /// installed, so the caller can fall back instead of failing the render.
    /// </summary>
    public static R2Vocoder? TryLoad(string? vocoderDirectory, string melModelPath)
    {
        if (string.IsNullOrWhiteSpace(vocoderDirectory) || !Directory.Exists(vocoderDirectory))
            return null;

        string configPath = Path.Combine(vocoderDirectory, "vocoder.yaml");
        if (!File.Exists(configPath))
            return null;

        var config = ParseYaml(File.ReadAllText(configPath));
        if (!config.TryGetValue("model", out var modelName)
            || !config.TryGetValue("sample_rate", out var sampleRateText)
            || !config.TryGetValue("hop_size", out var hopText))
            return null;

        string modelPath = Path.Combine(vocoderDirectory, modelName);
        if (!File.Exists(modelPath))
            return null;
        if (!File.Exists(melModelPath))
            return null;

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // One sample must be reproducible from the same input.
            EnableCpuMemArena = false,
        };

        int sampleRate = int.Parse(sampleRateText);
        int hop = int.Parse(hopText);
        int fft = config.TryGetValue("fft_size", out var fftText) ? int.Parse(fftText) : 2048;
        int melBins = config.TryGetValue("num_mel_bins", out var binsText) ? int.Parse(binsText) : 128;
        string name = config.TryGetValue("name", out var nameText) ? nameText : Path.GetFileName(vocoderDirectory);

        return new R2Vocoder(
            new InferenceSession(melModelPath, options),
            new InferenceSession(modelPath, options),
            sampleRate, hop, fft, melBins, name);
    }

    /// <summary>
    /// Renders one span. The parameters come in on the model's grid (48 kHz / 5 ms) and
    /// leave as samples at this backend's rate.
    /// </summary>
    public double[] Synthesize(
        double[][] sp, double[][] ap, double[] f0, bool[] silence,
        double modelFrameMs, int modelRate)
    {
        int frames = f0.Length;
        if (frames == 0)
            return [];

        double targetFrameMs = HopSize * 1000.0 / SampleRate;
        var (targetSp, targetAp, targetF0) = RateConversion.Convert(
            sp, ap, f0, silence, modelFrameMs, targetFrameMs, modelRate, SampleRate, FftSize);

        int bodyFrames = targetF0.Length;
        int padded = (int)Math.Ceiling((bodyFrames + PaddingFrames * 2) / (double)FrameMultiple) * FrameMultiple;
        int rightPadding = padded - bodyFrames - PaddingFrames;
        int bins = FftSize / 2 + 1;

        var f0Tensor = new DenseTensor<float>(new[] { 1, padded });
        var spTensor = new DenseTensor<float>(new[] { 1, padded, bins });
        var apTensor = new DenseTensor<float>(new[] { 1, padded, bins });

        for (int i = 0; i < padded; i++)
        {
            int source = i - PaddingFrames;
            bool inside = source >= 0 && source < bodyFrames;
            f0Tensor[0, i] = inside ? (float)targetF0[source] : 0f;
            for (int k = 0; k < bins; k++)
            {
                spTensor[0, i, k] = inside ? (float)targetSp[source][k] : 0f;
                apTensor[0, i, k] = inside ? (float)targetAp[source][k] : 1f;
            }
        }

        using var melResult = mMel.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("f0", f0Tensor),
            NamedOnnxValue.CreateFromTensor("sp_env", spTensor),
            NamedOnnxValue.CreateFromTensor("ap", apTensor),
        });
        var mel = melResult.First().AsTensor<float>();

        var melTensor = new DenseTensor<float>(new[] { 1, padded, MelBins });
        for (int i = 0; i < padded; i++)
            for (int m = 0; m < MelBins; m++)
                melTensor[0, i, m] = mel[0, i, m];

        using var audioResult = mVocoder.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor(mInputName, melTensor),
            NamedOnnxValue.CreateFromTensor("f0", f0Tensor),
        });
        var waveform = audioResult.First().AsTensor<float>();

        int total = bodyFrames * HopSize;
        int offset = PaddingFrames * HopSize;
        var samples = new double[total];
        int available = waveform.Dimensions[1];
        for (int i = 0; i < total; i++)
        {
            int index = offset + i;
            samples[i] = index < available ? waveform[0, index] : 0.0;
        }

        // The models see context they should not emit; fade the first and last hop so the
        // seam to the neighbouring span does not click.
        int ease = Math.Min(HopSize, samples.Length / 2);
        for (int i = 0; i < ease; i++)
        {
            samples[i] *= (double)i / ease;
            samples[samples.Length - 1 - i] *= (double)i / ease;
        }
        return samples;
    }

    public void Dispose()
    {
        mMel.Dispose();
        mVocoder.Dispose();
    }

    /// <summary>Reads the flat key/value subset of vocoder.yaml that we need.</summary>
    static Dictionary<string, string> ParseYaml(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim().Trim('"', '\'');
            if (value.Length > 0)
                values[key] = value;
        }
        return values;
    }
}
