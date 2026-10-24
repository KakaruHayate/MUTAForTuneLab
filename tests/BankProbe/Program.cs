using System.Text.Json;
using MutaForTuneLab.Synthesis;

// Replays the bank-assembly arithmetic fixtures and reports the worst deviation.
// usage: BankProbe <fixtures dir>   (default tests/fixtures/bank)
var directory = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "bank");
var path = Path.Combine(directory, "bank.json");
if (!File.Exists(path))
{
    Console.Error.WriteLine($"fixtures not found: {path}");
    return 2;
}

var data = JsonSerializer.Deserialize<Fixtures>(File.ReadAllText(path),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;

int failures = 0;
double worst = 0.0;

void Report(string what, bool ok, double diff, string detail = "")
{
    if (ok)
    {
        Console.WriteLine($"{what,-28} pass  {diff:E2}");
        return;
    }
    failures++;
    Console.WriteLine($"{what,-28} FAIL  {diff:E3} {detail}");
}

// ---- resample ----
foreach (var (caseIndex, item) in data.Resample.Select((v, i) => (i, v)))
{
    var source = item.Source.Select(row => row.ToArray()).ToArray();
    var actual = BankAssembler.ResampleFrames(source, item.Target);
    double diff = MaxDiff(actual, item.Expected.Select(r => r.ToArray()).ToArray());
    worst = Math.Max(worst, diff);
    Report($"resample[{caseIndex}] {item.Source.Count}->{item.Target}", diff <= 1e-12, diff);
}

// ---- three-part split ----
foreach (var (caseIndex, item) in data.Split.Select((v, i) => (i, v)))
{
    var actual = BankAssembler.ThreePartSplit(item.Total, item.Head, item.Tail);
    bool ok = actual.Head == item.Expected[0] && actual.Middle == item.Expected[1] && actual.Tail == item.Expected[2];
    Report($"split[{caseIndex}] {item.Total}", ok, ok ? 0 : 1,
        $"expected {string.Join(',', item.Expected)} actual {actual.Head},{actual.Middle},{actual.Tail}");
}

// ---- fades ----
foreach (var (caseIndex, item) in data.Fades.Select((v, i) => (i, v)))
{
    var outWeights = BankAssembler.FadeOutWeights(item.Length);
    var inWeights = BankAssembler.FadeInWeights(item.Length);
    double diff = 0.0;
    for (int i = 0; i < item.Length; i++)
    {
        diff = Math.Max(diff, Math.Abs(outWeights[i] - item.Out[i]));
        diff = Math.Max(diff, Math.Abs(inWeights[i] - item.In[i]));
    }
    worst = Math.Max(worst, diff);
    Report($"fades[{caseIndex}] n={item.Length}", diff <= 1e-6, diff);
}

// ---- state ratios ----
foreach (var (caseIndex, item) in data.Ratios.Select((v, i) => (i, v)))
{
    var states = item.States.Select(s => (long)s).ToArray();
    var (head, tail) = BankAssembler.StateBoundaryRatios(states);
    double diff = Math.Max(Math.Abs(head - item.Head), Math.Abs(tail - item.Tail));
    worst = Math.Max(worst, diff);
    Report($"ratios[{caseIndex}]", diff <= 1e-12, diff);
}

// ---- planning ----
foreach (var (caseIndex, item) in data.Plans.Select((v, i) => (i, v)))
{
    var state = new NoteAssemblyState(
        item.State.PreviousWasRest, item.State.PendingVowelOverlap,
        item.State.PreviousConsonantRightExtension, item.State.HasPreviousConsonant);
    var plan = BankAssembler.Plan(item.SlotClass, item.Duration, item.PreviousDuration,
        item.NextDuration, item.NextSlotClass, item.SourceSuffix, state);

    bool ok = plan.Branch == item.Expected.Branch
              && plan.TargetCore == item.Expected.TargetCore
              && plan.LeftExtension == item.Expected.LeftExtension
              && plan.RightExtension == item.Expected.RightExtension
              && plan.HeadOverlap == item.Expected.HeadOverlap
              && plan.PreviousOverlap == item.Expected.PreviousOverlap
              && plan.PendingVowelOverlap == item.Expected.PendingVowelOverlap;
    Report($"plan[{caseIndex}] {item.Name}", ok, ok ? 0 : 1,
        ok ? "" : $"branch {plan.Branch} core {plan.TargetCore} left {plan.LeftExtension} right {plan.RightExtension}");
}

Console.WriteLine();
Console.WriteLine(failures == 0
    ? $"bank fixtures: all passed (worst {worst:E2})"
    : $"bank fixtures: {failures} failed");
return failures == 0 ? 0 : 1;

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
    public List<ResampleCase> Resample { get; set; } = new();
    public List<SplitCase> Split { get; set; } = new();
    public List<FadeCase> Fades { get; set; } = new();
    public List<RatioCase> Ratios { get; set; } = new();
    public List<PlanCase> Plans { get; set; } = new();
}

sealed class ResampleCase
{
    public List<List<double>> Source { get; set; } = new();
    public int Target { get; set; }
    public List<List<double>> Expected { get; set; } = new();
}

sealed class SplitCase
{
    public int Total { get; set; }
    public double Head { get; set; }
    public double Tail { get; set; }
    public List<int> Expected { get; set; } = new();
}

sealed class FadeCase
{
    public int Length { get; set; }
    public List<double> Out { get; set; } = new();
    public List<double> In { get; set; } = new();
}

sealed class RatioCase
{
    public List<int> States { get; set; } = new();
    public double Head { get; set; }
    public double Tail { get; set; }
}

sealed class PlanCase
{
    public string Name { get; set; } = "";
    public int SlotClass { get; set; }
    public int Duration { get; set; }
    public int PreviousDuration { get; set; }
    public int NextDuration { get; set; }
    public int NextSlotClass { get; set; }
    public int SourceSuffix { get; set; }
    public StateDto State { get; set; } = new();
    public ExpectedDto Expected { get; set; } = new();
}

sealed class StateDto
{
    public bool PreviousWasRest { get; set; }
    public int PendingVowelOverlap { get; set; }
    public int PreviousConsonantRightExtension { get; set; }
    public bool HasPreviousConsonant { get; set; }
}

sealed class ExpectedDto
{
    public string Branch { get; set; } = "";
    public int TargetCore { get; set; }
    public int LeftExtension { get; set; }
    public int RightExtension { get; set; }
    public int HeadOverlap { get; set; }
    public int PreviousOverlap { get; set; }
    public int PendingVowelOverlap { get; set; }
}
