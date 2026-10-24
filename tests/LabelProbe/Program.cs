using System.Text.Json;
using MutaForTuneLab.Synthesis;

// Compares the C# label builder against the fixtures the Python reference produced.
//
// usage: LabelProbe <fixtures dir>          (default: tests/fixtures/labels next to the repo)
var directory = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "labels");

if (!Directory.Exists(directory))
{
    Console.Error.WriteLine($"fixtures not found: {Path.GetFullPath(directory)}");
    return 2;
}

int passed = 0, failed = 0;
foreach (var jsonPath in Directory.GetFiles(directory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
{
    string name = Path.GetFileNameWithoutExtension(jsonPath);
    string expectedPath = Path.ChangeExtension(jsonPath, ".labels");
    if (!File.Exists(expectedPath))
    {
        Console.WriteLine($"{name,-12} SKIP (no .labels)");
        continue;
    }

    List<string> actual;
    try
    {
        actual = BuildFromFixture(jsonPath);
    }
    catch (Exception e)
    {
        Console.WriteLine($"{name,-12} FAIL {e.GetType().Name}: {e.Message}");
        failed++;
        continue;
    }

    var expected = File.ReadAllLines(expectedPath)
        .Where(line => line.Length > 0)
        .ToArray();

    if (actual.Count == expected.Length && actual.SequenceEqual(expected, StringComparer.Ordinal))
    {
        Console.WriteLine($"{name,-12} pass  {actual.Count} labels");
        passed++;
        continue;
    }

    failed++;
    Console.WriteLine($"{name,-12} FAIL  {actual.Count} vs {expected.Length} labels");
    for (int i = 0; i < Math.Min(actual.Count, expected.Length); i++)
    {
        if (!string.Equals(actual[i], expected[i], StringComparison.Ordinal))
        {
            Console.WriteLine($"  [{i}] expected {expected[i]}");
            Console.WriteLine($"  [{i}] actual   {actual[i]}");
            break;
        }
    }
}

Console.WriteLine();
Console.WriteLine(failed == 0 ? $"label fixtures: all {passed} passed" : $"label fixtures: {failed} failed");
return failed == 0 ? 0 : 1;

static List<string> BuildFromFixture(string path)
{
    using var stream = File.OpenRead(path);
    var fixture = JsonSerializer.Deserialize<Fixture>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                  ?? throw new InvalidDataException("empty fixture");

    var score = new NoteScore
    {
        Phones = fixture.Notes.Select(n => n.Phone).ToArray(),
        Starts = fixture.Notes.Select(n => n.Start).ToArray(),
        Durations = fixture.Notes.Select(n => n.Frames).ToArray(),
        Pitches = fixture.Notes.Select(n => n.Pitch).ToArray(),
        Tempo = fixture.Tempo,
    };
    return LabelBuilder.Build(score);
}

sealed class Fixture
{
    public string Name { get; set; } = "";
    public int Tempo { get; set; } = 120;
    public List<FixtureNote> Notes { get; set; } = new();
}

sealed class FixtureNote
{
    public string Phone { get; set; } = "";
    public int Start { get; set; }
    public int Frames { get; set; }
    public int Pitch { get; set; }
}
