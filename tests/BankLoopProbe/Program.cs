using System.Text.Json;
using MutaForTuneLab.Synthesis;

// Replays the end-to-end assembly fixtures: block building on a synthetic context, then
// a short write loop over a global buffer. usage: BankLoopProbe <fixtures dir>
var directory = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "bank");
var path = Path.Combine(directory, "bank_loop.json");
if (!File.Exists(path))
{
    Console.Error.WriteLine($"fixtures not found: {path}");
    return 2;
}

var options = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
};
var data = JsonSerializer.Deserialize<Fixtures>(File.ReadAllText(path), options)!;

int failures = 0;
double worst = 0.0;

foreach (var item in data.Blocks)
{
    var mgc = ToArray(item.Mgc);
    var bap = ToArray(item.Bap);
    long[]? states = item.States?.Select(s => (long)s).ToArray();

    var block = BankAssembler.BuildBlock(mgc, bap, item.SourcePrefix, item.SourceCore, item.SourceSuffix,
        item.TargetCore, item.LeftExtension, item.RightExtension, states);

    double diff = Math.Max(MaxDiff(block.Mgc, ToArray(item.ExpectedMgc)),
                           MaxDiff(block.Bap, ToArray(item.ExpectedBap)));
    worst = Math.Max(worst, diff);
    if (diff <= 0.0)
    {
        Console.WriteLine($"block {item.Name,-14} pass  bit-identical");
    }
    else
    {
        failures++;
        Console.WriteLine($"block {item.Name,-14} FAIL  {diff:E3}");
    }
}

foreach (var loop in data.Loops)
{
    var mgcGlobal = ToArray(loop.InitialMgc);
    var bapGlobal = ToArray(loop.InitialBap);

    foreach (var step in loop.Steps)
    {
        if (step.Branch == "rest")
        {
            BankAssembler.FillRestMgc(mgcGlobal, step.Start, step.RestDuration);
            continue;
        }

        var plan = new NoteAssemblyPlan
        {
            Branch = step.Branch,
            SlotClass = 0,
            Duration = 0,
            TargetCore = 0,
            LeftExtension = step.LeftExtension,
            RightExtension = 0,
            HeadOverlap = 0,
            PreviousOverlap = step.PreviousOverlap,
            PendingVowelOverlap = 0,
            NextState = default,
        };
        var block = new NoteBlock
        {
            Mgc = ToArray(step.Mgc),
            Bap = ToArray(step.Bap),
            LeftExtension = step.LeftExtension,
        };
        BankAssembler.WriteNoteBlock(mgcGlobal, bapGlobal, block, step.Start, plan);
    }

    double diff = Math.Max(MaxDiff(mgcGlobal, ToArray(loop.ExpectedMgc)),
                           MaxDiff(bapGlobal, ToArray(loop.ExpectedBap)));
    worst = Math.Max(worst, diff);
    if (diff <= 0.0)
    {
        Console.WriteLine($"loop  {loop.Name,-14} pass  bit-identical ({loop.Steps.Count} steps)");
    }
    else
    {
        failures++;
        Console.WriteLine($"loop  {loop.Name,-14} FAIL  {diff:E3}");
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0
    ? $"bank loop: all passed (worst {worst:E2})"
    : $"bank loop: {failures} failed");
return failures == 0 ? 0 : 1;

static double[][] ToArray(List<List<double>> rows)
    => rows.Select(r => r.ToArray()).ToArray();

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
    public List<BlockCase> Blocks { get; set; } = new();
    public List<LoopCase> Loops { get; set; } = new();
}

sealed class BlockCase
{
    public string Name { get; set; } = "";
    public List<List<double>> Mgc { get; set; } = new();
    public List<List<double>> Bap { get; set; } = new();
    public int SourcePrefix { get; set; }
    public int SourceCore { get; set; }
    public int SourceSuffix { get; set; }
    public int TargetCore { get; set; }
    public int LeftExtension { get; set; }
    public int RightExtension { get; set; }
    public List<long>? States { get; set; }
    public List<List<double>> ExpectedMgc { get; set; } = new();
    public List<List<double>> ExpectedBap { get; set; } = new();
}

sealed class LoopCase
{
    public string Name { get; set; } = "";
    public List<List<double>> InitialMgc { get; set; } = new();
    public List<List<double>> InitialBap { get; set; } = new();
    public List<StepCase> Steps { get; set; } = new();
    public List<List<double>> ExpectedMgc { get; set; } = new();
    public List<List<double>> ExpectedBap { get; set; } = new();
}

sealed class StepCase
{
    public string Branch { get; set; } = "";
    public int Start { get; set; }
    public int PreviousOverlap { get; set; }
    public List<List<double>> Mgc { get; set; } = new();
    public List<List<double>> Bap { get; set; } = new();
    public int LeftExtension { get; set; }
    public int RestDuration { get; set; }
}
