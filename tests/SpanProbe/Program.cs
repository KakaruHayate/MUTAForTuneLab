using MutaForTuneLab.Synthesis;

// Checks the span plan's invariants on shapes that have broken such code before.
int failures = 0;

void Check(string name, bool[] silence, int frameSamples, int sampleRate)
{
    var spans = SpanPlanner.Plan(silence, frameSamples, sampleRate);

    // 1. every frame appears in at least one span
    var covered = new bool[silence.Length];
    foreach (var span in spans)
        for (int f = span.StartFrame; f < span.StartFrame + span.FrameCount; f++)
            covered[f] = true;
    bool full = Array.TrueForAll(covered, v => v);

    // 2. audible frames are covered by audible spans, rests by silent ones
    var kindOk = new bool[silence.Length];
    foreach (var span in spans)
        for (int f = span.StartFrame; f < span.StartFrame + span.FrameCount; f++)
        {
            if (!span.Silent) kindOk[f] = true;
            else if (silence[f]) kindOk[f] = true;
        }
    bool kinds = Array.TrueForAll(kindOk, v => v);

    // 3. sample ranges agree with the frame ranges
    bool samples = true;
    foreach (var span in spans)
        samples &= span.StartSample == span.StartFrame * frameSamples
                   && span.SampleCount == span.FrameCount * frameSamples
                   && span.SampleRate == sampleRate;

    // 4. no chunk exceeds the limit
    bool bounded = true;
    foreach (var span in spans)
        if (!span.Silent) bounded &= span.FrameCount <= SpanPlanner.MaxChunkFrames + 2 * SpanPlanner.ContextFrames;

    bool ok = full && kinds && samples && bounded;
    if (!ok) failures++;
    Console.WriteLine($"{name,-22} {(ok ? "pass" : "FAIL")}  {spans.Length,3} spans  " +
                      $"cover {full} kinds {kinds} samples {samples} bounded {bounded}");
}

Check("all voiced", Enumerable.Repeat(false, 500).ToArray(), 240, 48000);
Check("all rest", Enumerable.Repeat(true, 40).ToArray(), 240, 48000);
Check("rest, note, rest", Build(20, 60, 30), 240, 48000);
Check("single frame", new[] { false }, 240, 48000);
Check("one frame rest", new[] { true }, 240, 48000);
Check("long note", Build(2, 600, 2), 512, 44100);
Check("alternating", Enumerable.Range(0, 40).Select(i => i % 2 == 0).ToArray(), 240, 48000);

Console.WriteLine(failures == 0 ? "span probe: passed" : $"span probe: {failures} failed");
return failures == 0 ? 0 : 1;

static bool[] Build(int rest, int note, int tail)
{
    var list = new List<bool>();
    for (int i = 0; i < rest; i++) list.Add(true);
    for (int i = 0; i < note; i++) list.Add(false);
    for (int i = 0; i < tail; i++) list.Add(true);
    return list.ToArray();
}
