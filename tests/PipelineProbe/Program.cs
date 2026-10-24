using MutaForTuneLab.Synthesis;

// Drives both tiers through the one entry point and compares each with its reference.
// usage: PipelineProbe <fixtures dir> <mel.onnx> <vocoder package dir>
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: PipelineProbe <fixtures dir> <mel.onnx> <vocoder dir>");
    return 2;
}
string directory = args[0], melPath = args[1], vocoderDir = args[2];

const int frames = 40, frameSamples = 240;
var mgc = ReadMatrix(Path.Combine(directory, "wl_mgc.f64"), frames, 50);
var bap = ReadMatrix(Path.Combine(directory, "wl_bap.f64"), frames, 26);
var f0 = ReadDoubles(Path.Combine(directory, "wl_f0.f64"), frames);
var silence = VocoderPipeline.DetectSilence(mgc);
int failures = 0;

// --- R1.1 -----------------------------------------------------------------
var r11 = VocoderPipeline.Render(mgc, bap, f0, silence, 0.55, new VocoderExpression(),
    VocoderMode.WorldlineR11, null, out var used11);

// The reference is the raw vocoder output; the pipeline also gates, so apply the same
// envelope the pipeline uses before comparing.
var expected11 = ReadDoubles(Path.Combine(directory, "wl_continuous_pcm.f64"), 0);
var envelope = OutputGate.BuildSilenceEnvelope(expected11.Length, silence, frameSamples);
for (int i = 0; i < expected11.Length; i++) expected11[i] *= envelope[i];

double diff11 = MaxDiff(r11.Samples, expected11);
Console.WriteLine($"R1.1 ({used11}) {r11.SampleRate} Hz  max|diff| {diff11:E2}");
if (diff11 > 1e-12) failures++;

// --- R2 -------------------------------------------------------------------
using var r2 = R2Vocoder.TryLoad(vocoderDir, melPath);
if (r2 is null)
{
    Console.WriteLine("R2   no package installed; the pipeline falls back:");
    var fallback = VocoderPipeline.Render(mgc, bap, f0, silence, 0.55, new VocoderExpression(),
        VocoderMode.WorldlineR2, null, out var usedFallback);
    Console.WriteLine($"     {fallback.Samples.Length} samples at {fallback.SampleRate} Hz via {usedFallback}");
    if (usedFallback != VocoderMode.WorldlineR11) failures++;
}
else
{
    var output = VocoderPipeline.Render(mgc, bap, f0, silence, 0.55, new VocoderExpression(),
        VocoderMode.WorldlineR2, r2, out var used2);
    double diff2 = MaxDiff(output.Samples, ReadFloats(Path.Combine(directory, "r2_expected.f32")));
    Console.WriteLine($"R2   ({r2.Provider}) {output.SampleRate} Hz  max|diff| {diff2:E2}  samples {output.Samples.Length}");
    // DirectML reassociates float32 ops; ~1e-5 on a 0.43 peak is about -95 dB.
    if (diff2 > 1e-4 || output.SampleRate != 44100) failures++;
}

Console.WriteLine(failures == 0 ? "pipeline probe: passed" : $"pipeline probe: {failures} failed");
return failures == 0 ? 0 : 1;

static double MaxDiff(double[] a, double[] b)
{
    if (a.Length != b.Length) return double.PositiveInfinity;
    double worst = 0.0;
    for (int i = 0; i < a.Length; i++) worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
    return worst;
}

static double[] ReadDoubles(string path, int expected)
{
    var bytes = File.ReadAllBytes(path);
    var values = new double[bytes.Length / 8];
    Buffer.BlockCopy(bytes, 0, values, 0, values.Length * 8);
    if (expected > 0 && values.Length != expected)
        throw new InvalidDataException($"{path}: {values.Length} values, expected {expected}");
    return values;
}

static double[] ReadFloats(string path)
{
    var bytes = File.ReadAllBytes(path);
    var floats = new float[bytes.Length / 4];
    Buffer.BlockCopy(bytes, 0, floats, 0, floats.Length * 4);
    return floats.Select(v => (double)v).ToArray();
}

static double[][] ReadMatrix(string path, int rows, int columns)
{
    var flat = ReadDoubles(path, rows * columns);
    var matrix = new double[rows][];
    for (int r = 0; r < rows; r++)
    {
        matrix[r] = new double[columns];
        Array.Copy(flat, r * columns, matrix[r], 0, columns);
    }
    return matrix;
}
