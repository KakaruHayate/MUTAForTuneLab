using System.Text;
using static Native;
using MutaForTuneLab.Assets;
using MutaForTuneLab.Native;
using MutaForTuneLab.Synthesis;

// Measures how the two front ends line up with the acoustic model: how many phones each
// one asks for, how many frames the model gives back, and whether the state durations add
// up to the frame count. No guessing: everything printed here is read from the engine.
MutaDiagnostics.Sink = message => Console.WriteLine("  [log] " + message);

string package = args.Length > 0
    ? args[0]
    : Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "package");
string voiceRoot = args.Length > 1
    ? args[1]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Muta", "voice", "song");

var voices = VoiceLibrary.Scan(new[] { voiceRoot });
Console.WriteLine($"voices under {voiceRoot}: {string.Join(", ", voices.Select(v => v.Id))}");
var bankVoice = voices.FirstOrDefault(v => v.HasBank);

// ---- Chinese -----------------------------------------------------------------
if (bankVoice is not null)
{
    Console.WriteLine();
    Console.WriteLine($"== Chinese: {bankVoice.Id} ==");
    string[] lyrics = { "chen", "a", "ni", "hao" };
    int[] midi = { 60, 62, 64, 65 };
    const int framesPerNote = 200;
    var score = new NoteScore
    {
        Phones = lyrics,
        Starts = Enumerable.Range(0, lyrics.Length).Select(i => i * framesPerNote).ToArray(),
        Durations = Enumerable.Repeat(framesPerNote, lyrics.Length).ToArray(),
        Pitches = midi,
        Tempo = 120,
    };
    var labels = LabelBuilder.Build(score);
    Report("zh", HtsVoice.Load(bankVoice.VoiceFile).EnginePath, labels, lyrics.Length, 2);

    {
        var header = HtsVoice.Load(bankVoice.VoiceFile);
        Console.WriteLine($"  muta voice: {string.Join(",", header.StreamTypes)} " +
                          $"lengths {string.Join(",", header.VectorLengths)}");
        foreach (var line in header.Header.Split('\n'))
            if (line.Contains("OPTION", StringComparison.Ordinal) || line.Contains("STREAM_TYPE", StringComparison.Ordinal))
                Console.WriteLine("    " + line.Trim());
    }

    {
        var dict = PinyinDictionary.TryLoadForVoice(bankVoice.Directory);
        Console.WriteLine($"  dictionary: {dict?.CharacterCount ?? 0} characters");
        foreach (string lyric in new[] { "恩", "嗯", "n", "ng", "en" })
            Console.WriteLine($"    {lyric} -> {dict?.SyllableFor(lyric) ?? "(none)"}");
    }

    Console.WriteLine();
    Console.WriteLine($"  bank groups around a nasal coda ({bankVoice.Id}):");    var inf = InfFile.Load(bankVoice.InfFile);
    {
        var dump = new System.Text.StringBuilder();
        foreach (var g in inf.Groups)
        {
            var lefts = string.Join(",", g.Items.GroupBy(x => x.Left)
                .OrderByDescending(x => x.Count())
                .Select(x => $"{x.Key}:{x.Count()}"));
            dump.AppendLine($"{g.Name}\t{g.Items.Count}\t{lefts}");
        }
        string inventory = Path.Combine(Path.GetTempPath(), $"bank-{bankVoice.Id}-inventory.txt");
        File.WriteAllText(inventory, dump.ToString());
        Console.WriteLine($"  inventory: {inventory} ({inf.Groups.Count} groups)");
    }

    foreach (string phone in new[] { "en", "eng", "ang", "ong", "ing", "n", "m", "a", "o" })
    {
        var group = inf.Groups.FirstOrDefault(g => g.Name == phone);
        if (group is null)
        {
            Console.WriteLine($"    {phone,-4} (no group)");
            continue;
        }
        var pairs = group.Items.Take(6).Select(i => $"{i.Left}|{i.Right}").ToArray();
        Console.WriteLine($"    {phone,-4} {group.Items.Count,4} items  {string.Join(" ", pairs)}");
        if (phone == "en")
        {
            var lefts = group.Items.GroupBy(i => i.Left)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}:{g.Count()}");
            Console.WriteLine("         left contexts: " + string.Join(" ", lefts));
            foreach (var item in group.Items.Where(i => i.Left is "ge" or "n" or "m").Take(4))
                Console.WriteLine($"         {item.Left}|{item.Right} core {item.CoreFrames} prefix {item.PrefixFrames} suffix {item.SuffixFrames}");
        }
    }
}

// ---- Japanese ----------------------------------------------------------------
string dic = Path.Combine(package, "sinsy", "dic");
string jpVoice = Path.Combine(package, "sinsy", "voice", "nitech_jp_song070_f001.htsvoice");
if (File.Exists(jpVoice) && Directory.Exists(dic))
{
    Console.WriteLine();
    Console.WriteLine("== Japanese: nitech_jp_song070_f001 ==");
    var kana = KanaTable.TryLoad(dic)!;
    string[] morae = { "\u306B", "\u307B", "\u3093", "\u3054" };   // ni ho n go
    int[] midi = { 60, 62, 64, 65 };
    const int framesPerNote = 200;
    var scoreNotes = new List<ScoreNote>();
    for (int i = 0; i < morae.Length; i++)
    {
        string phone = (kana.PhonemesFor(morae[i]) ?? "?").Replace(" ", string.Empty);
        scoreNotes.Add(new ScoreNote(i * framesPerNote * 0.005, framesPerNote * 0.005, midi[i], morae[i]));
        Console.WriteLine($"  mora {morae[i]} -> table phones [{kana.PhonemesFor(morae[i])}] -> [{phone}]");
    }

    Console.WriteLine($"  table: N -> {Map(JapanesePhones.TryMap(null, "N", out var mo, out var mf), mo, mf)}; " +
                      $"n -> {Map(JapanesePhones.TryMap(null, "n", out var no, out var nf), no, nf)}");
    double totalSeconds = scoreNotes.Count * framesPerNote * 0.005;
    {
        var header = HtsVoice.Load(jpVoice);
        Console.WriteLine($"  yoko streams: {string.Join(",", header.StreamTypes)} " +
                          $"lengths {string.Join(",", header.VectorLengths)} " +
                          $"rate {header.SampleRate} period {header.FramePeriod} states {header.States}");
    }
    string? labelText = MutaSinsy.Labels(scoreNotes, totalSeconds, dic);
    if (labelText is null)
    {
        Console.WriteLine("  label builder refused the score");
        return;
    }
    var labels = labelText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).ToList();
    Report("jp/current-xml", jpVoice, labels, morae.Length, 2);
    Console.WriteLine($"  content window (from label times): {Fmt(MutaSinsy.ContentWindow(labelText, 0.005))}");

    Console.WriteLine();
    Console.WriteLine("  same notes, written with real measures and rests:");
    string? valid = CallLabels(ValidXml(scoreNotes, 4.0), dic);
    if (valid is null)
    {
        Console.WriteLine("    refused");
    }
    else
    {
        var validLabels = valid.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        Report("jp/valid-xml", jpVoice, validLabels, morae.Length, 2);
        Console.WriteLine($"  content window (from label times): {Fmt(MutaSinsy.ContentWindow(valid, 0.005))}");
    }

    Console.WriteLine();
    Console.WriteLine("  a realistic block: 24 notes of 240 ticks (0.25 s), one line:");
    var block = new List<ScoreNote>();
    for (int i = 0; i < 24; i++)
        block.Add(new ScoreNote(i * 0.25, 0.25, 60 + i % 5, morae[i % morae.Length]));
    string? blockText = MutaSinsy.Labels(block, 6.0, dic);
    if (blockText is not null)
    {
        var blockLabels = blockText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        Report("jp/block-current", jpVoice, blockLabels, block.Count, 2);
    }
}

static string Map(bool ok, string? onset, string final) => ok ? $"[{onset}]+[{final}]" : "(no entry)";
static string Fmt((int StartFrame, int EndFrame)? window)
    => window is { } w ? $"{w.StartFrame}..{w.EndFrame}" : "null";

// expectedNotes: how many notes were handed in; each note expects one onset (optional) and
// one nucleus, so the content phones must be between notes and 2*notes.
static void Report(string tag, string enginePath, IReadOnlyList<string> labels, int expectedNotes, int perNote)
{
    using var engine = MutaHts.Load(enginePath);
    int frames = engine.Synthesize(labels);
    var states = engine.StateDurations();
    int statesPerPhone = engine.States;
    Console.WriteLine($"  [{tag}] labels {labels.Count}  notes in {expectedNotes}  " +
                      $"states/phone {statesPerPhone}  labels*states {labels.Count * statesPerPhone}  total states {engine.TotalState}");
    Console.WriteLine($"  [{tag}] model frames {frames}  sum(states) {states.Sum()}");

    int cursor = 0, index = 0;
    var rows = new List<(string Phone, int Start, int Frames)>();
    foreach (var label in labels)
    {
        string phone = PhoneTimeline.PhoneOf(label);
        int length = 0;
        for (int s = 0; s < statesPerPhone && index < states.Length; s++, index++)
            length += states[index];
        if (length > 0)
            rows.Add((phone, cursor, length));
        cursor += length;
    }

    int content = rows.Count(r => r.Phone is not ("sil" or "sp" or "pau" or "xx"));
    Console.WriteLine($"  [{tag}] phones {string.Join(" ", rows.Select(r => $"{r.Phone}:{r.Frames}@{r.Start}"))}");
    Console.WriteLine($"  [{tag}] content phones {content}  expected {expectedNotes}..{expectedNotes * perNote}  " +
                      $"{(content <= expectedNotes * perNote && content >= expectedNotes ? "plausible" : "MISMATCH")}; " +
                      $"cursor {cursor} vs frames {frames}");
}

static string? CallLabels(string xml, string dic)
{
    string path = Path.Combine(Path.GetTempPath(), $"probe-{Guid.NewGuid():N}.xml");
    try
    {
        File.WriteAllText(path, xml, new UTF8Encoding(false));
        int needed = muta_sinsy_labels(path, dic, IntPtr.Zero, 0);
        if (needed <= 0)
            return null;
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(needed);
        try
        {
            int written = muta_sinsy_labels(path, dic, buffer, needed);
            return written > 0 ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8(buffer) : null;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }
    finally
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}

// A score Sinsy cannot complain about: 4/4 measures, exactly four beats each, a rest at
// each end, and a rest filling whatever the last measure leaves.
static string ValidXml(IReadOnlyList<ScoreNote> notes, double totalSeconds)
{
    const int divisions = 480;
    const double tempo = 120.0;
    double beat = 60.0 / tempo;                 // one quarter note
    int measureTicks = divisions * 4;

    var events = new List<Event>();
    double cursor = beat;                        // one beat of rest first
    foreach (var note in notes)
    {
        int gap = (int)Math.Round((note.Start + beat - cursor) / beat * divisions);
        if (gap > 0)
            events.Add(new Event(gap, true, default));
        int ticks = Math.Max(1, (int)Math.Round(note.Duration / beat * divisions));
        events.Add(new Event(ticks, false, note));
        cursor = note.Start + beat + note.Duration;
    }
    int usedTicks = (int)Math.Round(cursor / beat * divisions);
    int tail = measureTicks - usedTicks % measureTicks;
    events.Add(new Event(tail == 0 ? measureTicks : tail, true, default));

    // Split events so no measure is overfull: a note longer than a measure is cut short.
    var measures = new List<List<Event>>();
    var current = new List<Event>();
    int used = 0;
    foreach (var e in events)
    {
        int remaining = e.Ticks;
        while (remaining > 0)
        {
            int room = measureTicks - used;
            int take = Math.Min(room, remaining);
            current.Add(new Event(take, e.IsRest, e.Note));
            used += take;
            remaining -= take;
            if (used == measureTicks)
            {
                measures.Add(current);
                current = new List<Event>();
                used = 0;
            }
        }
    }
    if (current.Count > 0)
    {
        current.Add(new Event(measureTicks - used, true, default));
        measures.Add(current);
    }

    var b = new StringBuilder();
    b.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
    b.AppendLine("<score-partwise version=\"2.0\" xml:lang=\"ja\">");
    b.AppendLine("  <part-list><score-part id=\"P1\"><part-name>Voice</part-name></score-part></part-list>");
    b.AppendLine("  <part id=\"P1\">");
    for (int m = 0; m < measures.Count; m++)
    {
        b.AppendLine($"    <measure number=\"{m + 1}\">");
        if (m == 0)
        {
            b.AppendLine("      <attributes>");
            b.AppendLine($"        <divisions>{divisions}</divisions>");
            b.AppendLine("        <key><fifths>0</fifths></key>");
            b.AppendLine("        <time><beats>4</beats><beat-type>4</beat-type></time>");
            b.AppendLine("        <clef><sign>G</sign><line>2</line></clef>");
            b.AppendLine("      </attributes>");
            b.AppendLine($"      <direction><sound tempo=\"{tempo}\"/></direction>");
        }
        foreach (var e in measures[m])
        {
            if (e.IsRest)
            {
                b.AppendLine($"      <note><rest/><duration>{e.Ticks}</duration><type>quarter</type></note>");
                continue;
            }
            var (step, alter, octave) = Spell(e.Note.Midi);
            b.AppendLine("      <note>");
            b.Append($"        <pitch><step>{step}</step>");
            if (alter != 0)
                b.Append($"<alter>{alter}</alter>");
            b.AppendLine($"<octave>{octave}</octave></pitch>");
            b.AppendLine($"        <duration>{e.Ticks}</duration>");
            b.AppendLine("        <type>quarter</type>");
            b.AppendLine($"        <lyric number=\"1\"><syllabic>single</syllabic><text>{e.Note.Lyric}</text></lyric>");
            b.AppendLine("      </note>");
        }
        b.AppendLine("    </measure>");
    }
    b.AppendLine("  </part>");
    b.AppendLine("</score-partwise>");
    return b.ToString();
}

static (string Step, int Alter, int Octave) Spell(int midi)
{
    string[] steps = { "C", "C", "D", "D", "E", "F", "F", "G", "G", "A", "A", "B" };
    int[] alters = { 0, 1, 0, 1, 0, 0, 1, 0, 1, 0, 1, 0 };
    return (steps[midi % 12], alters[midi % 12], midi / 12 - 1);
}

readonly record struct Event(int Ticks, bool IsRest, ScoreNote Note);

static partial class Native
{
    [System.Runtime.InteropServices.DllImport("muta_sinsy", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    public static extern int muta_sinsy_labels(string xmlPath, string dictionaryDirectory, IntPtr buffer, int bufferSize);
}
