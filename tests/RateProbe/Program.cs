using System.Text.Json;
using MutaForTuneLab.Synthesis;

// Replays the rate-conversion fixtures: 48 kHz / 5 ms parameters onto the 44.1 kHz grid.
// usage: RateProbe <fixtures dir>
var directory = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "vocoder");
var metaPath = Path.Combine(directory, "rate.json");
if (!File.Exists(metaPath))
{
    Console.Error.WriteLine($"fixtures not found: {metaPath}");
    return 2;
}

var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(metaPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;

int frames = meta.Frames, bins = meta.Bins, targetFrames = meta.TargetFrames;
var mgc = ReadMatrix(Path.Combine(directory, "rate_mgc.f64"), frames, 50);
var bap = ReadMatrix(Path.Combine(directory, "rate_bap.f64"), frames, 26);
var f0 = ReadDoubles(Path.Combine(directory, "rate_f0.f64"), frames);

var silent = new bool[frames];
for (int f = 0; f < frames; f++)
{
    double max = 0.0;
    foreach (var v in mgc[f])
        max = Math.Max(max, Math.Abs(v));
    silent[f] = max < 1e-6 || mgc[f][0] <= -8.5;
}

var power = MgcSpectrum.Power(mgc, new[] { 0.55 }, bins);
for (int f = 0; f < frames; f++)
    if (silent[f])
        Array.Fill(power[f], 1e-12);
var ap = BapExpand.Expand(bap);
for (int f = 0; f < frames; f++)
    for (int k = 0; k < bins; k++)
        ap[f][k] = BapExpand.ToWeight(ap[f][k]);

var (sp, apOut, f0Out) = RateConversion.Convert(
    power, ap, f0, silent,
    meta.SourceFrameMs, meta.TargetFrameMs,
    meta.SourceRate, meta.TargetRate, meta.TargetFft);

int failures = 0;

void Compare(string what, double[][] actual, string expectedFile)
{
    var expected = ReadDoubles(Path.Combine(directory, expectedFile), targetFrames * bins);
    double worst = 0.0;
    int index = 0;
    for (int t = 0; t < targetFrames; t++)
        for (int k = 0; k < bins; k++)
            worst = Math.Max(worst, Math.Abs(actual[t][k] - expected[index++]));
    bool ok = worst <= 1e-12;
    if (!ok) failures++;
    Console.WriteLine($"{what,-14} {(ok ? "pass" : "FAIL")}  max|diff| {worst:E3}");
}

void CompareVector(string what, double[] actual, string expectedFile)
{
    var expected = ReadDoubles(Path.Combine(directory, expectedFile), targetFrames);
    double worst = 0.0;
    for (int i = 0; i < targetFrames; i++)
        worst = Math.Max(worst, Math.Abs(actual[i] - expected[i]));
    bool ok = worst <= 1e-9;
    if (!ok) failures++;
    Console.WriteLine($"{what,-14} {(ok ? "pass" : "FAIL")}  max|diff| {worst:E3}");
}

Compare("spectrum", sp, "rate_sp.f64");
Compare("aperiodicity", apOut, "rate_ap.f64");
CompareVector("f0", f0Out, "rate_f0d.f64");

Console.WriteLine();
Console.WriteLine(failures == 0 ? "rate fixtures: all passed" : $"rate fixtures: {failures} failed");
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
    public int Bins { get; set; }
    public int SourceRate { get; set; }
    public double SourceFrameMs { get; set; }
    public int SourceFft { get; set; }
    public int TargetRate { get; set; }
    public int TargetHop { get; set; }
    public int TargetFft { get; set; }
    public double TargetFrameMs { get; set; }
    public int TargetFrames { get; set; }
    public int SilentFrames { get; set; }
}
