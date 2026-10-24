using System.Text.Json;
using MutaForTuneLab.Synthesis;

// Replays the aperiodicity fixtures. usage: BapProbe <fixtures dir>
var directory = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "vocoder");
var path = Path.Combine(directory, "bap.json");
if (!File.Exists(path))
{
    Console.Error.WriteLine($"fixtures not found: {path}");
    return 2;
}

var data = JsonSerializer.Deserialize<Fixtures>(File.ReadAllText(path),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

var bap = data.Bap.Select(r => r.ToArray()).ToArray();
var expanded = BapExpand.Expand(bap);

double expandDiff = MaxDiff(expanded, data.Expanded.Select(r => r.ToArray()).ToArray());

double weightDiff = 0.0, linearDiff = 0.0;
for (int f = 0; f < bap.Length; f++)
{
    for (int b = 0; b < bap[f].Length; b++)
    {
        weightDiff = Math.Max(weightDiff, Math.Abs(BapExpand.ToWeight(bap[f][b]) - data.Weight[f][b]));
        linearDiff = Math.Max(linearDiff, Math.Abs(BapExpand.ToLinear(bap[f][b]) - data.Linear[f][b]));
    }
}

// The band table must cover exactly the bin count, or every spectrum is shifted.
int total = 0;
foreach (var run in BapExpand.BandRunLengths)
    total += run;

Console.WriteLine($"band table       {(total == BapExpand.BinCount ? "pass" : "FAIL")}  sum {total}");
Console.WriteLine($"expand           {Report(expandDiff)}");
Console.WriteLine($"weight           {Report(weightDiff)}");
Console.WriteLine($"linear           {Report(linearDiff)}");

bool ok = total == BapExpand.BinCount && expandDiff <= 0 && weightDiff <= 1e-12 && linearDiff <= 1e-12;
Console.WriteLine();
Console.WriteLine(ok ? "bap fixtures: all passed" : "bap fixtures: FAILED");
return ok ? 0 : 1;

static string Report(double diff) => diff <= 0 ? "pass  bit-identical" : $"diff {diff:E2}";

static double MaxDiff(double[][] a, double[][] b)
{
    if (a.Length != b.Length)
        return double.PositiveInfinity;
    double diff = 0.0;
    for (int i = 0; i < a.Length; i++)
    {
        if (a[i].Length != b[i].Length)
            return double.PositiveInfinity;
        for (int k = 0; k < a[i].Length; k++)
            diff = Math.Max(diff, Math.Abs(a[i][k] - b[i][k]));
    }
    return diff;
}

sealed class Fixtures
{
    public List<List<double>> Bap { get; set; } = new();
    public List<List<double>> Expanded { get; set; } = new();
    public List<List<double>> Weight { get; set; } = new();
    public List<List<double>> Linear { get; set; } = new();
}
