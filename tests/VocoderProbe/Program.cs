using System.Text.Json;
using MutaForTuneLab.Synthesis;

// Replays the vocoder fixtures: recomputes the spectra from MGC/BAP, runs both backends,
// and compares the samples and the output gate. usage: VocoderProbe <fixtures dir>
var directory = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "vocoder");
var metaPath = Path.Combine(directory, "wl.json");
if (!File.Exists(metaPath))
{
    Console.Error.WriteLine($"fixtures not found: {metaPath}");
    return 2;
}

var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(metaPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;

int frames = meta.Frames, bins = meta.Bins;
var mgc = ReadMatrix(Path.Combine(directory, "wl_mgc.f64"), frames, 50);
var bap = ReadMatrix(Path.Combine(directory, "wl_bap.f64"), frames, 26);
var f0 = ReadDoubles(Path.Combine(directory, "wl_f0.f64"), frames);

var silent = new bool[frames];
for (int f = 0; f < frames; f++)
{
    double max = 0.0;
    foreach (var v in mgc[f])
        max = Math.Max(max, Math.Abs(v));
    silent[f] = max < 1e-6 || mgc[f][0] <= -8.5;
}

var sp = MgcSpectrum.Power(mgc, new[] { 0.55 }, bins);
for (int f = 0; f < frames; f++)
    if (silent[f])
        Array.Fill(sp[f], 1e-12);
var ap = BapExpand.Expand(bap);
for (int f = 0; f < frames; f++)
    for (int k = 0; k < bins; k++)
        ap[f][k] = BapExpand.ToWeight(ap[f][k]);

var settings = new VocoderSettings
{
    FftSize = meta.FftSize,
    FramePeriodMs = meta.FramePeriodMs,
    SampleRate = meta.SampleRate,
    Backend = VocoderBackend.World,
};
var expression = new VocoderExpression();

int failures = 0;

double Compare(string what, double[] actual, string expectedFile)
{
    var expected = ReadDoubles(Path.Combine(directory, expectedFile), meta.Samples);
    double worst = 0.0;
    for (int i = 0; i < actual.Length && i < expected.Length; i++)
        worst = Math.Max(worst, Math.Abs(actual[i] - expected[i]));
    if (worst > 1e-12)
        Console.WriteLine($"  actual[0..3] {actual[0]:E4} {actual[1]:E4} {actual[2]:E4} {actual[3]:E4}`n  expect[0..3] {expected[0]:E4} {expected[1]:E4} {expected[2]:E4} {expected[3]:E4}");
    string verdict = worst <= 0 ? "pass  bit-identical" : worst <= 1e-12 ? $"pass  {worst:E2}" : "FAIL";
    Console.WriteLine($"{what,-22} {verdict}  max|diff| {worst:E3}");
    if (worst > 1e-12)
        failures++;
    return worst;
}

var world = WorldlineVocoder.Synthesize(sp, ap, f0, settings, expression);
Compare("world backend", world, "wl_world_pcm.f64");

var continuousSettings = new VocoderSettings
{
    FftSize = meta.FftSize,
    FramePeriodMs = meta.FramePeriodMs,
    SampleRate = meta.SampleRate,
    Backend = VocoderBackend.ContinuousNoise,
    Seed = (ulong)meta.Seed,
};
var continuous = WorldlineVocoder.Synthesize(sp, ap, f0, continuousSettings, expression);
Compare("continuous backend", continuous, "wl_continuous_pcm.f64");

// The gate envelope, built the way the engine builds it.
var envelope = OutputGate.BuildSilenceEnvelope(meta.Samples, silent, meta.FramesSamples);
OutputGate.ApplyVoicingNotch(envelope, f0, meta.FramesSamples);
Compare("gate envelope", envelope, "wl_envelope.f64");

// And the property that matters: a rest must come out as digital silence.
var gated = (double[])world.Clone();
for (int i = 0; i < gated.Length; i++)
    gated[i] *= envelope[i];
double loudestRest = 0.0;
for (int f = 0; f < frames; f++)
{
    // Only the interior of a rest must be silent: the ramp touches its edges.
    // Only the interior of a rest must be silent: the ramp is two frames wide.
    if (!silent[f] || f < 2 || f + 2 >= frames
        || !silent[f - 1] || !silent[f + 1] || !silent[f - 2] || !silent[f + 2])
        continue;
    for (int i = f * meta.FramesSamples; i < (f + 1) * meta.FramesSamples && i < gated.Length; i++)
        loudestRest = Math.Max(loudestRest, Math.Abs(gated[i]));
}
Console.WriteLine($"rest after gating      {(loudestRest == 0.0 ? "pass  digital silence" : "FAIL")}  peak {loudestRest:E2}");
if (loudestRest != 0.0)
    failures++;

Console.WriteLine();
Console.WriteLine(failures == 0 ? "vocoder fixtures: all passed" : $"vocoder fixtures: {failures} failed");
return failures == 0 ? 0 : 1;

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
    for (int r = 0; r < rows; r++)
    {
        matrix[r] = new double[columns];
        Array.Copy(flat, r * columns, matrix[r], 0, columns);
    }
    return matrix;
}

sealed class Meta
{
    public int Frames { get; set; }
    public int FramesSamples { get; set; }
    public int FftSize { get; set; }
    public int Bins { get; set; }
    public double FramePeriodMs { get; set; }
    public int SampleRate { get; set; }
    public int Samples { get; set; }
    public int Seed { get; set; }
    public int SilentFrames { get; set; }
}
