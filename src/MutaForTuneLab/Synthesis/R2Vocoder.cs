using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using System.Threading;
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
public sealed class R2Vocoder : IR2Vocoder
{
    /// <summary>Frames of context added on each side before the models see anything.</summary>
    const int PaddingFrames = 4;
    /// <summary>The network wants a multiple of 16 frames.</summary>
    const int FrameMultiple = 16;

    /// <summary>
    /// Serialises everything DirectML touches. The Windows DirectML build of ONNX Runtime
    /// terminates the process natively if session creation, Run and disposal overlap, so
    /// the plugin takes this lock around all three — the same rule OpenUtau follows.
    /// </summary>
    static readonly object DmlLock = new();

    /// <summary>Set once a DirectML session exists: caching keeps it live after a switch.</summary>
    static bool sDmlInUse;

    /// <summary>Devices ONNX Runtime reports for this platform's GPU provider.</summary>
    static readonly Lazy<IReadOnlyList<OrtEpDevice>> GpuDevices =
        new(FindGpuDevices, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Windows ships the DirectML build; Linux and macOS use the 1.31 package with WebGPU.
    /// The lookup is by name so a build without that provider simply finds nothing and the
    /// session falls back to CPU.
    /// </summary>
    static string GpuEpName => OperatingSystem.IsWindows() ? "dml" : "webgpu";

    static IReadOnlyList<OrtEpDevice> FindGpuDevices()
    {
        try
        {
            return OrtEnv.Instance().GetEpDevices()
                .Where(device => device.EpName.Contains(GpuEpName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<OrtEpDevice>();
        }
    }

    /// <summary>Which execution provider a session created now would use.</summary>
    public static string PreferredProvider
        => GpuDevices.Value.Count > 0 ? (OperatingSystem.IsWindows() ? "DirectML" : "WebGPU") : "CPU";

    /// <summary>Provider this instance actually got.</summary>
    public string Provider { get; }
    readonly InferenceSession mMel;
    InferenceSession mVocoder;
    readonly string mVocoderPath;
    readonly string mName;
    readonly int mSampleRate, mHopSize, mFftSize, mMelBins;
    readonly string mInputName;
    readonly string mOutputName;

    public int SampleRate { get; }
    public int HopSize { get; }
    public int FftSize { get; }
    public int MelBins { get; }
    public string Name { get; }

    R2Vocoder(InferenceSession mel, InferenceSession vocoder, int sampleRate, int hopSize,
              int fftSize, int melBins, string name, string provider)
    {
        mMel = mel;
        mVocoder = vocoder;
        mVocoderPath = string.Empty;
        mName = name;
        mSampleRate = sampleRate; mHopSize = hopSize; mFftSize = fftSize; mMelBins = melBins;
        SampleRate = sampleRate;
        HopSize = hopSize;
        FftSize = fftSize;
        MelBins = melBins;
        Name = name;
        Provider = provider;
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

        int sampleRate = int.Parse(sampleRateText);
        int hop = int.Parse(hopText);
        int fft = config.TryGetValue("fft_size", out var fftText) ? int.Parse(fftText) : 2048;
        int melBins = config.TryGetValue("num_mel_bins", out var binsText) ? int.Parse(binsText) : 128;
        string name = config.TryGetValue("name", out var nameText) ? nameText : Path.GetFileName(vocoderDirectory);

        // A GPU that cannot run this graph must not cost the whole render: build on
        // DirectML when it is there, and fall back to CPU if creating the session fails.
        if (GpuDevices.Value.Count > 0)
        {
            lock (DmlLock)
            {
                try
                {
                    // Only claim the GPU once a session actually exists: a driver that refuses
                    // the graph must leave the flag alone, so later renders go straight to CPU.
                    var created = Create(melModelPath, modelPath, sampleRate, hop, fft, melBins, name, dml: true);
                    sDmlInUse = true;
                    return created;
                }
                catch (Exception)
                {
                    // fall through to the CPU session below
                }
            }
        }
        return Create(melModelPath, modelPath, sampleRate, hop, fft, melBins, name, dml: false);
    }

    static readonly IDisposable NoScope = new NoopScope();

    sealed class NoopScope : IDisposable
    {
        public void Dispose() { }
    }

    sealed class Unlocker : IDisposable
    {
        public void Dispose() => Monitor.Exit(DmlLock);
    }

    /// <summary>Held across session creation, inference and disposal while DirectML is in use.</summary>
    static IDisposable EnterDmlScope()
    {
        if (!Volatile.Read(ref sDmlInUse))
            return NoScope;
        Monitor.Enter(DmlLock);
        return new Unlocker();
    }
    static R2Vocoder Create(string melModelPath, string modelPath, int sampleRate, int hop,
                            int fft, int melBins, string name, bool dml)
    {
        // The options must outlive the native session construction: the binding hands the
        // pointer to native code and never reads it again.
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        if (dml)
        {
            var device = GpuDevices.Value[0];
            options.AppendExecutionProvider(
                OrtEnv.Instance(),
                new List<OrtEpDevice> { device },
                new Dictionary<string, string>());
        }

        // The mel graph uses a Resize node that DirectML rejects with E_INVALIDARG, so it
        // stays on CPU -- it is 85 KB and costs nothing. The 56 MB vocoder is what needs
        // the GPU, and it is the one given the DirectML provider.
        using var cpuOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        return new R2Vocoder(
            new InferenceSession(melModelPath, cpuOptions),
            new InferenceSession(modelPath, options),
            sampleRate, hop, fft, melBins, name, dml ? PreferredProvider : "CPU");
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
        using var dmlScope = EnterDmlScope();

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
        using var dmlScope = EnterDmlScope();
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
