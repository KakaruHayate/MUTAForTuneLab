using System.Text.Json;
using MutaForTuneLab.Synthesis;

// Runs the R2 chain on the fixture slice and compares with the reference.
// usage: R2Probe <fixtures dir> <mel.onnx> <vocoder package dir>
var directory = args.Length > 2 ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "vocoder");
string melPath = args.Length > 2 ? args[1] : Path.Combine(AppContext.BaseDirectory, "mel.onnx");
string vocoderDir = args.Length > 2 ? args[2] : "";

var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(Path.Combine(directory, "r2.json")),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;

int frames = meta.Frames, bins = meta.FftSize / 2 + 1;
var mgc = ReadMatrix(Path.Combine(directory, "wl_mgc.f64"), frames, 50);
var bap = ReadMatrix(Path.Combine(directory, "wl_bap.f64"), frames, 26);
var f0 = ReadDoubles(Path.Combine(directory, "wl_f0.f64"), frames);

var silent = new bool[frames];
for (int f = 0; f < frames; f++)
{
    double max = 0.0;
    foreach (var v in mgc[f]) max = Math.Max(max, Math.Abs(v));
    silent[f] = max < 1e-6 || mgc[f][0] <= -8.5;
}

var sp = MgcSpectrum.Power(mgc, new[] { 0.55 }, bins);
for (int f = 0; f < frames; f++) if (silent[f]) Array.Fill(sp[f], 1e-12);
var ap = BapExpand.Expand(bap);
for (int f = 0; f < frames; f++)
    for (int k = 0; k < bins; k++) ap[f][k] = BapExpand.ToWeight(ap[f][k]);

using var vocoder = R2Vocoder.TryLoad(vocoderDir, melPath);
if (vocoder is null) { Console.Error.WriteLine("cannot load the R2 package"); return 2; }
Console.WriteLine($"package {vocoder.Name}  {vocoder.SampleRate} Hz  hop {vocoder.HopSize}");

var actual = vocoder.Synthesize(sp, ap, f0, silent, 5.0, 48000);
var expectedBytes = File.ReadAllBytes(Path.Combine(directory, "r2_expected.f32"));
var expected = new float[expectedBytes.Length / 4];
Buffer.BlockCopy(expectedBytes, 0, expected, 0, expected.Length * 4);

if (actual.Length != expected.Length)
{
    Console.WriteLine($"FAIL length {actual.Length} vs {expected.Length}");
    return 1;
}

double worst = 0.0, energyA = 0.0, energyB = 0.0, dot = 0.0, peakA = 0.0;
for (int i = 0; i < actual.Length; i++)
{
    worst = Math.Max(worst, Math.Abs(actual[i] - expected[i]));
    energyA += actual[i] * actual[i];
    energyB += (double)expected[i] * expected[i];
    dot += actual[i] * expected[i];
    peakA = Math.Max(peakA, Math.Abs(actual[i]));
}
double correlation = dot / Math.Sqrt(energyA * energyB);

Console.WriteLine($"samples {actual.Length}  peak {peakA:F5}");
Console.WriteLine($"correlation {correlation:F9}");
Console.WriteLine($"max|diff|   {worst:E3}");
bool ok = correlation > 0.99999 && worst < 1e-4;
Console.WriteLine(ok ? "r2 fixtures: passed" : "r2 fixtures: FAILED");
return ok ? 0 : 1;

static double[] ReadDoubles(string path, int expectedLength)
{
    var bytes = File.ReadAllBytes(path);
    var values = new double[bytes.Length / 8];
    Buffer.BlockCopy(bytes, 0, values, 0, values.Length * 8);
    if (expectedLength > 0 && values.Length != expectedLength)
        throw new InvalidDataException($"{path}: {values.Length} values, expected {expectedLength}");
    return values;
}

static double[][] ReadMatrix(string path, int rows, int columns)
{
    var flat = ReadDoubles(path, rows * columns);
    var matrix = new double[rows][];
    for (int r = 0; r < rows; r++) { matrix[r] = new double[columns]; Array.Copy(flat, r * columns, matrix[r], 0, columns); }
    return matrix;
}

sealed class Meta
{
    public int Frames { get; set; }
    public int TargetFrames { get; set; }
    public int MelBins { get; set; }
    public int SampleRate { get; set; }
    public int HopSize { get; set; }
    public int FftSize { get; set; }
    public double TargetFrameMs { get; set; }
    public int Samples { get; set; }
    public int PaddedFrames { get; set; }
}
