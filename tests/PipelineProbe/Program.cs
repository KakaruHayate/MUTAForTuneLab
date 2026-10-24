using MutaForTuneLab.Synthesis;

// Drives both vocoder modes through the one entry point and compares each with its reference.
string directory = args[0], melPath = args[1], vocoderDir = args[2];
int frames = 40;
var mgc = ReadMatrix(Path.Combine(directory, "wl_mgc.f64"), frames, 50);
var bap = ReadMatrix(Path.Combine(directory, "wl_bap.f64"), frames, 26);
var f0 = ReadDoubles(Path.Combine(directory, "wl_f0.f64"), frames);
var silence = VocoderPipeline.DetectSilence(mgc);
int failures = 0;

var r11 = VocoderPipeline.Render(mgc, bap, f0, silence, 0.55, new VocoderExpression(),
    VocoderMode.WorldlineR11, null, out var used11);
// The fixture is the raw vocoder output; the pipeline also gates, so compare through the gate.
// Same envelope the pipeline applies (silence only, no voicing notch).
var envelope = OutputGate.BuildSilenceEnvelope(8704, silence, 240);
var expected11 = ReadDoubles(Path.Combine(directory, "wl_continuous_pcm.f64"), 0);
for (int i = 0; i < expected11.Length; i++) expected11[i] *= envelope[i];
double diff11 = MaxDiff(r11.Samples, expected11);
Console.WriteLine($"R1.1 ({used11}) {r11.SampleRate} Hz  max|diff| {diff11:E2}");
if (diff11 > 1e-12) failures++;

using var r2 = R2Vocoder.TryLoad(vocoderDir, melPath);
if (r2 is null)
{
    var fallback = VocoderPipeline.Render(mgc, bap, f0, silence, 0.55, new VocoderExpression(),
        VocoderMode.WorldlineR2, null, out var usedFb);
    Console.WriteLine($"R2   no package; fell back to {usedFb}, {fallback.Samples.Length} samples at {fallback.SampleRate} Hz");
}
else
{
    var output = VocoderPipeline.Render(mgc, bap, f0, silence, 0.55, new VocoderExpression(),
        VocoderMode.WorldlineR2, r2, out var used2);
    double diff2 = MaxDiff(output.Samples, ReadFloats(Path.Combine(directory, "r2_expected.f32")));
    Console.WriteLine($"R2   ({r2.Provider}) {output.SampleRate} Hz  max|diff| {diff2:E2}  samples {output.Samples.Length}");
    // DirectML reassociates float ops, so a few ulp of float32 is expected; ~1e-5 on a 0.43 peak.
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
    if (expected > 0 && values.Length != expected) throw new InvalidDataException($"{path}: {values.Length}");
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
    for (int r = 0; r < rows; r++) { matrix[r] = new double[columns]; Array.Copy(flat, r * columns, matrix[r], 0, columns); }
    return matrix;
}
