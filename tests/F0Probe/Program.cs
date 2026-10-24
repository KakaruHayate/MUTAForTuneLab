using System.Text.Json;
using MutaForTuneLab.Synthesis;

// Replays the F0 fixtures: carrier curve + notes in, assembled curve out, compared
// against the curve the reference produced.
//
// usage: F0Probe <fixtures dir> [tolerance Hz]   (default tolerance 1e-6)
var directory = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "f0");
double tolerance = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1e-6;

if (!Directory.Exists(directory))
{
    Console.Error.WriteLine($"fixtures not found: {Path.GetFullPath(directory)}");
    return 2;
}

int passed = 0, failed = 0;
double worst = 0.0;

foreach (var jsonPath in Directory.GetFiles(directory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
{
    string name = Path.GetFileNameWithoutExtension(jsonPath);
    string carrierPath = Path.Combine(directory, name + ".carrier.f64");
    string expectedPath = Path.Combine(directory, name + ".f0.f64");
    if (!File.Exists(carrierPath) || !File.Exists(expectedPath))
    {
        Console.WriteLine($"{name,-8} SKIP (missing data files)");
        continue;
    }

    var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(jsonPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException($"empty fixture: {jsonPath}");

    var carrier = ReadDoubles(carrierPath);
    var expected = ReadDoubles(expectedPath);
    var notes = fixture.Notes
        .Select(n => new F0Note(n.StartFrame, n.Frames, n.Midi, n.Rest))
        .ToArray();

    var actual = F0Mechanism.Build(
        carrier, notes, fixture.Total,
        exactP4: true, startRamp: true, p5: true);

    if (actual.Length != expected.Length)
    {
        Console.WriteLine($"{name,-8} FAIL length {actual.Length} vs {expected.Length}");
        failed++;
        continue;
    }

    double maxDiff = 0.0;
    int worstIndex = -1;
    for (int i = 0; i < actual.Length; i++)
    {
        double diff = Math.Abs(actual[i] - expected[i]);
        if (diff > maxDiff)
        {
            maxDiff = diff;
            worstIndex = i;
        }
    }
    worst = Math.Max(worst, maxDiff);

    if (maxDiff <= tolerance)
    {
        Console.WriteLine($"{name,-8} pass  max|diff| {maxDiff:E2} Hz");
        passed++;
    }
    else
    {
        failed++;
        Console.WriteLine($"{name,-8} FAIL  max|diff| {maxDiff:E3} Hz at frame {worstIndex} " +
                          $"(expected {expected[worstIndex]:F4}, actual {actual[worstIndex]:F4})");
    }
}

Console.WriteLine();
Console.WriteLine(failed == 0
    ? $"f0 fixtures: all {passed} passed (worst {worst:E2} Hz)"
    : $"f0 fixtures: {failed} failed");
return failed == 0 ? 0 : 1;

static double[] ReadDoubles(string path)
{
    var bytes = File.ReadAllBytes(path);
    var values = new double[bytes.Length / 8];
    Buffer.BlockCopy(bytes, 0, values, 0, values.Length * 8);
    return values;
}

sealed class Fixture
{
    public string Name { get; set; } = "";
    public int Total { get; set; }
    public List<FixtureNote> Notes { get; set; } = new();
}

sealed class FixtureNote
{
    // The fixture spells this with an underscore; case-insensitive matching alone
    // would leave every note at frame 0.
    [System.Text.Json.Serialization.JsonPropertyName("start_frame")]
    public int StartFrame { get; set; }
    public int Frames { get; set; }
    public int Midi { get; set; }
    public bool Rest { get; set; }
}
