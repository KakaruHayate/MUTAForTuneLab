using System.Text.Json;
using MutaForTuneLab.Synthesis;

// Replays the MGC -> power spectrum fixtures. usage: MgcProbe <fixtures dir>
var directory = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "vocoder");
var metaPath = Path.Combine(directory, "mgc.json");
if (!File.Exists(metaPath))
{
    Console.Error.WriteLine($"fixtures not found: {metaPath}");
    return 2;
}

var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(metaPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;

int frames = meta.Frames, order = meta.Order, bins = meta.Bins;
var flat = ReadDoubles(Path.Combine(directory, "mgc.f64"), frames * order);
var mgc = new double[frames][];
for (int f = 0; f < frames; f++)
{
    mgc[f] = new double[order];
    Array.Copy(flat, f * order, mgc[f], 0, order);
}

double Compare(string what, double[][] actual, string expectedFile)
{
    var expected = ReadDoubles(Path.Combine(directory, expectedFile), frames * bins);
    double worst = 0.0;
    int index = 0;
    for (int f = 0; f < frames; f++)
    {
        for (int k = 0; k < bins; k++)
        {
            worst = Math.Max(worst, Math.Abs(actual[f][k] - expected[index++]));
        }
    }
    Console.WriteLine($"{what,-16} max|diff| {worst:E3}  typical {Typical(actual):E3}");
    return worst;
}

static double Typical(double[][] values)
{
    // Power spans many decades; a relative figure is the only readable summary.
    double sum = 0.0;
    int count = 0;
    foreach (var row in values)
        foreach (var v in row)
        {
            sum += v;
            count++;
        }
    return count == 0 ? 0 : sum / count;
}

var scalar = MgcSpectrum.Power(mgc, new[] { meta.AlphaScalar }, bins);
double scalarDiff = Compare("scalar alpha", scalar, "power_scalar.f64");

var alphas = meta.AlphaPerFrame.ToArray();
var framed = MgcSpectrum.Power(mgc, alphas, bins);
double framedDiff = Compare("per-frame alpha", framed, "power_perframe.f64");

// A relative tolerance: power values differ in scale across the spectrum, and the
// reference's dense cosine sum accumulates in a different order than an FFT.
double relative = 0.0;
{
    var expected = ReadDoubles(Path.Combine(directory, "power_scalar.f64"), frames * bins);
    int index = 0;
    for (int f = 0; f < frames; f++)
        for (int k = 0; k < bins; k++)
        {
            double e = expected[index++];
            double a = scalar[f][k];
            relative = Math.Max(relative, Math.Abs(a - e) / Math.Max(1e-30, Math.Abs(e)));
        }
}

Console.WriteLine();
Console.WriteLine($"relative worst {relative:E2}  (absolute {scalarDiff:E2} / {framedDiff:E2})");
bool ok = relative <= 1e-9 && framedDiff <= 1e-9;
Console.WriteLine(ok ? "mgc fixtures: all passed" : "mgc fixtures: FAILED");
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

sealed class Meta
{
    public int Bins { get; set; }
    public int Frames { get; set; }
    public int Order { get; set; }
    public string Source { get; set; } = "";
    public double AlphaScalar { get; set; }
    public List<double> AlphaPerFrame { get; set; } = new();
}

