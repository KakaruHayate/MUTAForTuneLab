using System.Text.Json;
using MutaForTuneLab.Assets;
using MutaForTuneLab.Synthesis;

// Replays the reference's slot walk on the same synthetic item table and compares buffers.
// usage: BankDriveProbe <fixtures dir>
var directory = args.Length > 0 ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "bank");
var path = Path.Combine(directory, "bank_drive.json");
if (!File.Exists(path)) { Console.Error.WriteLine($"fixtures not found: {path}"); return 2; }

var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
var data = JsonSerializer.Deserialize<Fixtures>(File.ReadAllText(path), options)!;

var source = new SyntheticBank(data);
var notes = data.Notes.Select(n => new RenderNote(n.Start, n.Frames, n.Midi, n.Phone, n.Rest)).ToArray();
var result = BankRenderer.Assemble(source, notes, data.Total);

double mgcDiff = MaxDiff(result.Mgc, data.ExpectedMgc);
double bapDiff = MaxDiff(result.Bap, data.ExpectedBap);
int silenceDiff = 0;
for (int f = 0; f < data.Total; f++)
    if (result.Silence[f] != data.ExpectedSilence[f]) silenceDiff++;

Console.WriteLine($"mgc   max|diff| {mgcDiff:E2}");
Console.WriteLine($"bap   max|diff| {bapDiff:E2}");
Console.WriteLine($"silence disagreements {silenceDiff}");
bool ok = mgcDiff <= 1e-12 && bapDiff <= 1e-12 && silenceDiff == 0;
Console.WriteLine(ok ? "bank drive: passed" : "bank drive: FAILED");
return ok ? 0 : 1;

static double MaxDiff(double[][] actual, List<List<double>> expected)
{
    if (actual.Length != expected.Count) return double.PositiveInfinity;
    double worst = 0.0;
    for (int f = 0; f < actual.Length; f++)
    {
        if (actual[f].Length != expected[f].Count) return double.PositiveInfinity;
        for (int k = 0; k < actual[f].Length; k++)
            worst = Math.Max(worst, Math.Abs(actual[f][k] - expected[f][k]));
    }
    return worst;
}

sealed class SyntheticBank : IBankSource
{
    readonly Dictionary<string, List<(InfItem Item, double[][] Mgc, double[][] Bap)>> mTable = new();
    readonly Dictionary<InfItem, (double[][], double[][])> mByItem = new();

    public int MgcOrder { get; }
    public int BapOrder { get; }
    public IReadOnlyDictionary<string, List<InfItem>> Groups { get; }

    public SyntheticBank(Fixtures data)
    {
        MgcOrder = data.MgcOrder;
        BapOrder = data.BapOrder;
        var groups = new Dictionary<string, List<InfItem>>();
        foreach (var entry in data.Items)
        {
            var item = new InfItem
            {
                Current = entry.Current, Left = entry.Left, Right = entry.Right,
                Values = entry.Values.ToArray(),
            };
            if (!groups.TryGetValue(item.Current, out var list))
            {
                list = new List<InfItem>();
                groups[item.Current] = list;
            }
            list.Add(item);
            mByItem[item] = (ToMatrix(entry.Mgc), ToMatrix(entry.Bap));
        }
        Groups = groups;
    }

    public (double[][] Mgc, double[][] Bap) Streams(InfItem item) => mByItem[item];

    static double[][] ToMatrix(List<List<double>> rows) => rows.Select(r => r.ToArray()).ToArray();
}

sealed class Fixtures
{
    public int Total { get; set; }
    public int MgcOrder { get; set; }
    public int BapOrder { get; set; }
    public List<NoteDto> Notes { get; set; } = new();
    public List<ItemDto> Items { get; set; } = new();
    public List<List<double>> ExpectedMgc { get; set; } = new();
    public List<List<double>> ExpectedBap { get; set; } = new();
    public List<bool> ExpectedSilence { get; set; } = new();
}

sealed class NoteDto
{
    public int Start { get; set; }
    public int Frames { get; set; }
    public int Midi { get; set; }
    public string Phone { get; set; } = "";
    public bool Rest { get; set; }
}

sealed class ItemDto
{
    public string Current { get; set; } = "";
    public string Left { get; set; } = "";
    public string Right { get; set; } = "";
    public List<int> Values { get; set; } = new();
    public List<List<double>> Mgc { get; set; } = new();
    public List<List<double>> Bap { get; set; } = new();
}
