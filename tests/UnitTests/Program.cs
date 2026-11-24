using MutaForTuneLab.Assets;
using MutaForTuneLab.Synthesis;

// A tiny assert harness rather than a test framework: it needs no NuGet, so it runs in CI
// with nothing but the .NET SDK. Exit code 0 = every case passed.

int passed = 0, failed = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) { passed++; return; }
    failed++;
    Console.WriteLine($"FAIL {name} {detail}");
}

void Equal<T>(string name, T expected, T actual)
    => Check(name, EqualityComparer<T>.Default.Equals(expected, actual), $"expected [{expected}] got [{actual}]");

// ---- the table is data, and the data is the contract -------------------------
var map = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ja-zh-phone-map.tsv"));
Check("table ships beside the tests", map.Length > 0);

bool TryMap(string onset, string vowel, out string? bankOnset, out string bankFinal)
    => JapanesePhones.TryMap(onset, vowel, out bankOnset, out bankFinal);
string? bo; string bf;

TryMap("k", "e", out bo, out bf);
Equal("ke onset", "k", bo);
Equal("ke final", "ei", bf);

TryMap("k", "o", out bo, out bf);
Equal("ko final", "ou", bf);

TryMap("ky", "o", out bo, out bf);
Equal("kyo onset keeps k", "k", bo);
Equal("kyo final", "iao", bf);

TryMap(null, "N", out bo, out bf);
Equal("N onset", null, bo);
Equal("N final", "en", bf);

// ---- Chinese front end ------------------------------------------------------
var zh = new ChineseFrontend(null, new HashSet<string> { "n", "i", "ge", "en", "a", "o", "e", "u", "h", "ao" }, "a");

var ni = zh.Analyze("ni");
Equal("ni label", "ni", ni.LabelText);
Equal("ni phones", "n,i", string.Join(",", ni.ModelPhones));
Equal("ni bank", "n,i", $"{ni.BankInitial},{ni.BankFinal}");

var en = zh.Analyze("en");
Equal("en is zero-initial", "ge,en", $"{en.ModelInitial},{en.ModelFinal}");

var n = zh.Analyze("n");
Equal("n normalises to en", "ge,en", $"{n.ModelInitial},{n.ModelFinal}");

var bad = zh.Analyze("zzz");
Equal("unsingable falls back", "a", bad.BankFinal);

// ---- Japanese front end -----------------------------------------------------
string dic = Path.Combine(AppContext.BaseDirectory, "sinsy", "dic");
var kana = KanaTable.TryLoad(dic);
if (kana is null)
{
    Console.WriteLine("SKIP japanese front end: kana table not beside the tests");
}
else
{
    var ja = new JapaneseFrontend(kana, "a");

    var jaNi = ja.Analyze("\u306B");            // に
    Equal("ni label is kana", "\u306B", jaNi.LabelText);
    Equal("ni phones", "n,i", string.Join(",", jaNi.ModelPhones));

    var jaN = ja.Analyze("\u3093");             // ん
    Equal("N phones", "N", string.Join(",", jaN.ModelPhones));
    Equal("N bank final", "en", jaN.BankFinal);
    Equal("N left context", "ge", jaN.BankLeft);

    var jaKya = ja.Analyze("\u304D\u3083");     // きゃ
    Equal("kya phones", "ky,a", string.Join(",", jaKya.ModelPhones));

    var jaSokuon = ja.Analyze("\u3063");        // っ
    Equal("sokuon gives one phone", 1, jaSokuon.ModelPhones.Count);
    Equal("sokuon is silent in the bank", "cl", jaSokuon.BankFinal);

    var jaKa = ja.Analyze("\u304B");            // か after っ
    Equal("post-sokuon phones", "cl,k,a", string.Join(",", jaKa.ModelPhones));

    var jaLong = ja.Analyze("\u30FC");          // ー
    Equal("long vowel holds the previous vowel", "a", jaLong.BankFinal);

    Equal("reverse lookup ni", "\u306B", kana.KanaForPhones("n", "i"));
    Equal("reverse lookup kya", "\u304D\u3083", kana.KanaForPhones("ky", "a"));
    Equal("reverse lookup kya from two leading phones",
        "\u304D\u3083", kana.KanaForPhones(new[] { "k", "y" }, "a"));
}

// ---- label parsing and the timing timeline ----------------------------------
Equal("phone of a label with a negative start",
    "ch", PhoneTimeline.PhoneOf("-1 5000000 xx^xx-ch+en=xx/A:xx"));

var score = new NoteScore
{
    Phones = new[] { "ni" },
    Starts = new[] { 0 },
    Durations = new[] { 100 },
    Pitches = new[] { 60 },
};
var labels = LabelBuilder.Build(score);
var states = new int[labels.Count * 5];
Array.Fill(states, 2);
var timeline = PhoneTimeline.Build(labels, states, 5);
Equal("timeline phones", string.Join(",", labels.Select(PhoneTimeline.PhoneOf)),
    string.Join(",", timeline.Select(s => s.Phone)));
Equal("timeline totals", labels.Count * 10, timeline.Sum(s => s.Frames));
Check("content window covers every phone",
    PhoneTimeline.Content(timeline) == (timeline[0].StartFrame, timeline[^1].EndFrame));

// ---- F0 mechanism -----------------------------------------------------------
var carrier = Enumerable.Repeat(200.0, 100).ToArray();
var bent = F0Mechanism.Build(carrier, new[] { new F0Note(0, 100, 69) }, 100, exactP4: true, startRamp: true, p5: true);
double median = bent.OrderBy(v => v).ElementAt(50);
Check("note 69 lands near 440 Hz", Math.Abs(median - 440.0) < 2.0, $"median {median:F1}");

var withRest = F0Mechanism.Build(carrier, new[] { new F0Note(0, 100, 69, Rest: true) }, 100);
Check("rest clears the carrier", withRest.All(v => v == 0.0));

// ---- label splitter ---------------------------------------------------------
var split = LabelBuilder.SplitSyllable("zhi");
Equal("zhi onset", "zh", split?.Initial);
Equal("zhi final", "ib", split?.Final);

// ---- the consonant table is MUTA's, not the model's ---------------------------
// The original engine's consonant table: a string -> 100 ns tick table, capped at half the note.
Equal("q consonant frames", 24, LabelBuilder.ConsonantFrames("q"));
Equal("x consonant frames", 28, LabelBuilder.ConsonantFrames("x"));
Equal("ch consonant frames", 22, LabelBuilder.ConsonantFrames("ch"));
Equal("m consonant frames", 17, LabelBuilder.ConsonantFrames("m"));
Equal("a consonant frames", 10, LabelBuilder.ConsonantFrames("a"));
Equal("sil has no consonant", 0, LabelBuilder.ConsonantFrames("sil"));
Equal("an unknown phone has no consonant", 0, LabelBuilder.ConsonantFrames(""));
Equal("a 222 ms note caps q at half", 22, LabelBuilder.ConsonantFrames("q", 44));
Equal("a long note keeps the table value", 24, LabelBuilder.ConsonantFrames("q", 200));

// ---- bank assembly against a fake bank --------------------------------------
InfItem Item(string current, string left, string right, int core) => new()
{
    Current = current,
    Left = left,
    Right = right,
    Values = new[]
    {
        BankContext.LeftClass(left), BankContext.RightClass(right),
        core + 10, 5, core, 5, 57, 57, 57, 0, 4,
    },
};

var groups = new Dictionary<string, List<InfItem>>
{
    ["k"] = new() { Item("k", "sp", "a", 30) },
    ["a"] = new() { Item("a", "k", "sp", 60) },
};
var fake = new FakeBank(groups);

var ka = new RenderNote(0, 100, 60, "ka", false, "ka", "k", "a", "k", "a", null, 2);
var bank = BankRenderer.Assemble(fake, new[] { ka }, 100, phoneFrames: new[] { 20, 80 });
Check("ka sounds", bank.Silence.All(v => !v));

var log = new List<string>();
MutaDiagnostics.Sink = log.Add;
var closure = new RenderNote(0, 100, 60, "cl", false, "cl", null, "cl", null, "cl", null, 1);
var after = new RenderNote(100, 100, 60, "ka", false, "ka", "k", "a", "k", "a", null, 3);
var bank2 = BankRenderer.Assemble(fake, new[] { closure, after }, 200, phoneFrames: new[] { 40, 20, 80 });
MutaDiagnostics.Sink = null;
Check("closure stays silent", bank2.Silence[50]);
Check("note after the closure sounds", !bank2.Silence[150]);
Check("post-sokuon onset capped at 30%", log.Any(l => l.Contains("/30f")));

// ---- bank context selection -------------------------------------------------
var exact = BankContext.ChooseFirstItem(
    new[] { Item("k", "sp", "a", 30), Item("k", "a", "a", 30) }, "sp", "a", 261.6);
Equal("exact left context wins", "sp", exact?.Left);

// ---- MUTA 1.0 index (version 100) -------------------------------------------
// Two revisions of the .inf ship in the wild: 2.0 names each row's phone and both
// neighbours, 1.0 names the row after the context it was recorded in. Both must parse to
// the same eleven-field view, and 1.0's context names must come back as the phone-keyed
// groups the selector asks for.
var v1 = InfFile.ParseDecoded(V1Index(), "synthetic v1");
Equal("v1 version", 100, v1.Version);
Equal("v1 items", 6, v1.ItemCount);
Equal("v1 groups", 6, v1.GroupCount);
Equal("v1 finals", 1, v1.FinalCount);
Equal("v1 orders", 50 * 1000 + 26, v1.MgcOrder * 1000 + v1.BapOrder);

InfItem? V1(string raw) => v1.Groups
    .SelectMany(g => g.Items).FirstOrDefault(i => i.Current == raw);

var bA = V1("b_a")!;
Equal("v1 syllable keeps its consonant as core", 5, bA.CoreFrames);
Equal("v1 syllable keeps the vowel onset as suffix", 20, bA.SuffixFrames);
Equal("v1 syllable prefix is empty", 0, bA.PrefixFrames);
Equal("v1 frame count", 25, bA.FrameCount);
Equal("v1 pitch code", 48, bA.PitchCode);
Equal("v1 bank offset", 0, bA.BankOffset);
Equal("v1 bank size", 7600, bA.BankSize);

var boundary = V1("a-b")!;
Equal("v1 boundary keeps the previous vowel as prefix", 20, boundary.PrefixFrames);
Equal("v1 boundary keeps the consonant as core", 24, boundary.CoreFrames);
Equal("v1 boundary has no suffix", 0, boundary.SuffixFrames);

var bare = V1("a")!;
Equal("v1 bare final is all core", 116, bare.CoreFrames);

v1.ValidateBank(1_000_000);
bool rejectedShortBank = false;
try
{
    v1.ValidateBank(1000);
}
catch (InvalidDataException)
{
    rejectedShortBank = true;
}
Check("v1 bank ranges are checked", rejectedShortBank);

// The group mapping: syllable rows become onsets, bare finals stay finals, and the coda
// rows are reachable from the finals that end in the coda letter.
var v1Groups = BankLayout.Build(v1);
Check("v1 onset group", v1Groups.ContainsKey("b"));
Check("v1 vowel group", v1Groups.ContainsKey("a"));
Check("v1 empty-looking group is a vowel", v1Groups.ContainsKey("ib"));

var onsets = v1Groups["b"];
Check("v1 syllable is an onset", onsets.Any(i => i.Left == "sil" && i.Right == "a"));
Check("v1 nucleus alternate is registered", onsets.Any(i => i.Left == "sil" && i.Right == "ib"));
Check("v1 coda alias reaches the final", onsets.Any(i => i.Left == "an"));
Check("v1 stem matches the nucleus",
    BankContext.SelectNoteItem(v1Groups, "b", "sil", "ib", 20, 261.6, 0)?.Right == "ib");
Check("v1 coda transition wins after a nasal final",
    BankContext.SelectNoteItem(v1Groups, "b", "an", "ai", 20, 261.6, 0)?.Left == "an");
Check("v1 phrase start uses the syllable",
    BankContext.SelectNoteItem(v1Groups, "b", "sil", "ai", 20, 261.6, 0)?.Current == "b_a");
Equal("v1 vowel body keeps its own final",
    "a", BankContext.SelectNoteItem(v1Groups, "a", "b", "sil", 40, 261.6, 1)?.Current);

// 2.0 must keep parsing exactly as before, including several rows per phone group.
var v2 = InfFile.ParseDecoded(V2Index(), "synthetic v2");
Equal("v2 version", 200, v2.Version);
Equal("v2 items", 2, v2.ItemCount);
Equal("v2 groups", 2, v2.GroupCount);
var v2Groups = BankLayout.Build(v2);
Equal("v2 keeps the phone groups", 2, v2Groups.Count);
Equal("v2 keeps the row's own neighbours", "sp", v2Groups["k"][0].Left);

// ---- a continuation is a held vowel, not a second attack --------------------
// MUTA's ExpandNotes skips a "-" slot; the head vowel's length is then derived from the
// next slot, so it runs across the whole held span.
var dashScore = new NoteScore
{
    Phones = new[] { "a", "-", "a" },
    Starts = new[] { 0, 100, 200 },
    Durations = new[] { 100, 100, 100 },
    Pitches = new[] { 60, 60, 60 },
};
var dashSlots = LabelBuilder.ExpandNotes(dashScore);
Equal("dash emits no slot", 4, dashSlots.Count);
Equal("head vowel covers the dash", 200, dashSlots[1].LengthFrame);

var heldBank = new FakeBank(new Dictionary<string, List<InfItem>>
{
    ["ga"] = new() { Item("ga", "sp", "a", 10) },
    ["a"] = new() { Item("a", "ga", "sp", 60) },
});
var heldNotes = new[]
{
    new RenderNote(0, 100, 60, "a", false, "a", "ga", "a", "ga", "a", null, 2),
    new RenderNote(100, 100, 60, "-", false, "-", null, "a", null, "a", null, 0, null, true),
    new RenderNote(200, 100, 60, "a", false, "a", "ga", "a", "ga", "a", null, 2),
};
log.Clear();
MutaDiagnostics.Sink = log.Add;
var heldResult = BankRenderer.Assemble(heldBank, heldNotes, 300, phoneFrames: new[] { 20, 80, 20, 80 });
MutaDiagnostics.Sink = null;
Check("a continuation opens no slot of its own",
    log.Any(l => l.Contains("bank slots 2 consonant, 2 vowel")), string.Join(" | ", log));
Check("the head vowel sounds across the continuation", heldResult.Silence.All(v => !v));

// ---- portamento straddles the beat -----------------------------------------
// 69 -> 64 at frame 100: the glide has to have left 440 Hz before the boundary and
// arrived at 329.6 Hz shortly after it, not jump on the beat and sag into the ramp.
var glideCarrier = Enumerable.Repeat(440.0, 200).ToArray();
var glide = F0Mechanism.Build(glideCarrier,
    new[] { new F0Note(0, 100, 69), new F0Note(100, 100, 64) }, 200,
    exactP4: true, startRamp: true, p5: true);
Check("portamento starts before the beat", glide[99] < 420.0 && glide[99] > 340.0, $"{glide[99]:F1}");
Check("portamento lands after the beat", Math.Abs(glide[105] - 329.63) < 6.0, $"{glide[105]:F1}");
Check("portamento is monotonic across the beat", glide[95] > glide[99] && glide[99] > glide[105],
    $"{glide[95]:F1} > {glide[99]:F1} > {glide[105]:F1}");

// ---- breath: a breath note is noise, never pitch ---------------------------
var breathNote = new RenderNote(10, 100, 60, "sp", true, "br", null, "sil",
    null, "sil", null, 0, null, false, true);
const int frameSamples = 240;
var breathTrack = BreathRenderer.Build(new[] { breathNote }, 200 * frameSamples, frameSamples, null, 12345);
Equal("breath track spans the block", 200 * frameSamples, breathTrack.Length);
double breathSum = 0.0;
for (int i = 10 * frameSamples; i < 110 * frameSamples; i++)
    breathSum += breathTrack[i] * breathTrack[i];
double breathRms = Math.Sqrt(breathSum / (100 * frameSamples));
Check("breath is normalised to unit RMS", Math.Abs(breathRms - 1.0) < 1e-3, $"{breathRms:F4}");
Check("breath stays inside its note", breathTrack.Take(10 * frameSamples).All(v => v == 0.0)
    && breathTrack.Skip(110 * frameSamples).All(v => v == 0.0));

var breathAgain = BreathRenderer.Build(new[] { breathNote }, 200 * frameSamples, frameSamples, null, 12345);
Check("breath is deterministic", breathTrack.SequenceEqual(breathAgain));

var syntheticFinal = Enumerable.Range(0, 9600).Select(i => (float)((i % 7) - 3) * 0.02f).ToArray();
var fromFinal = BreathRenderer.Build(new[] { breathNote }, 200 * frameSamples, frameSamples,
    new[] { syntheticFinal }, 7);
Check("a bank final is used when present", fromFinal.Skip(10 * frameSamples).Take(100 * frameSamples)
    .Any(v => v != 0.0f));

// ---- synthetic indexes ------------------------------------------------------
// Both revisions of the .inf built in memory: CI must not need a bank-sized file to
// exercise the parsers.
byte[] V1Index()
{
    var bytes = new List<byte>();
    void I32(int value) => bytes.AddRange(BitConverter.GetBytes(value));
    void Text(string text) => bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(text));
    void Row(string name, int[] fields)
    {
        Text(name);
        bytes.Add(0x20);
        foreach (int value in fields)
            I32(value);
        bytes.Add(0x0A);
    }

    foreach (int value in new[] { 100, 6, 50, 26, 1, 6, 1, 0, 0, 1 })
        I32(value);
    bytes.Add(0x0A);

    foreach (var (name, capacity) in new[]
             {
                 ("b_a", 1), ("b_i", 1), ("a-b", 1), ("a", 1), ("n-b", 1), ("ib", 1),
             })
    {
        Text(name);
        bytes.Add(0x20);
        I32(capacity);
    }
    bytes.Add(0x0A);

    // left class, right class, pitch, frames, first segment, boundary pitches, offset, bytes
    Row("b_a", [1, 7, 48, 25, 5, 48, 43, 0, 7600]);
    Row("b_i", [0, 0, 50, 30, 10, 0, 0, 7600, 9120]);
    Row("a-b", [1, 4, 50, 44, 20, 0, 50, 16720, 13376]);
    Row("a", [4, 0, 44, 116, 0, 0, 0, 30096, 35264]);
    Row("n-b", [7, 3, 50, 41, 20, 0, 50, 65360, 12464]);
    Row("ib", [1, 0, 44, 60, 0, 0, 0, 77824, 18240]);

    I32(13796);
    I32(96064);
    I32(55184);
    bytes.Add(0x0A);
    return bytes.ToArray();
}

byte[] V2Index()
{
    var bytes = new List<byte>();
    void I32(int value) => bytes.AddRange(BitConverter.GetBytes(value));
    void Text(string text) => bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(text));
    void Row(string current, string left, string right, int[] fields)
    {
        foreach (string name in new[] { current, left, right })
        {
            Text(name);
            bytes.Add(0x20);
        }
        foreach (int value in fields)
            I32(value);
        bytes.Add(0x0A);
    }

    foreach (int value in new[] { 200, 2, 50, 26, 0, 2 })
        I32(value);
    bytes.Add(0x0A);

    foreach (string name in new[] { "k", "a" })
    {
        Text(name);
        bytes.Add(0x20);
        I32(1);
    }
    bytes.Add(0x0A);

    Row("k", "sp", "a", [0, 1, 30, 5, 20, 5, 57, 0, 0, 0, 9120]);
    Row("a", "k", "sp", [0, 0, 60, 0, 60, 0, 57, 0, 0, 9120, 18240]);
    return bytes.ToArray();
}

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class FakeBank : IBankSource
{
    public FakeBank(IReadOnlyDictionary<string, List<InfItem>> groups) => Groups = groups;

    public int MgcOrder => 50;
    public int BapOrder => 26;
    public IReadOnlyDictionary<string, List<InfItem>> Groups { get; }

    public (double[][] Mgc, double[][] Bap) Streams(InfItem item)
    {
        int frames = item.PrefixFrames + item.CoreFrames + item.SuffixFrames;
        var mgc = new double[frames][];
        var bap = new double[frames][];
        for (int f = 0; f < frames; f++)
        {
            mgc[f] = new double[50];
            bap[f] = new double[26];
        }
        return (mgc, bap);
    }
}
