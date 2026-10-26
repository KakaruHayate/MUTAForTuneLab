using System;
using System.Collections.Generic;

namespace MutaForTuneLab.Synthesis;

/// <summary>One phone's real place in the model's output.</summary>
public readonly record struct PhoneSpan(string Phone, int StartFrame, int Frames)
{
    public int EndFrame => StartFrame + Frames;
}

/// <summary>
/// Where each phoneme actually landed, read from the engine's state durations.
///
/// HTS gives one duration per state, five states per phone, and the durations add up to
/// the frame count exactly (measured: 8 labels x 5 states = 40 states, 738 frames =
/// 738). That makes it the only trustworthy timing in the chain: the label's own time
/// fields are not (see plan 13.19 -- they do not scale with the note, and a 4 s score
/// produced a 2 s window).
///
/// Everything downstream reads this: the content window, the durations the bank is asked
/// to stretch, and the per-note alignment.
/// </summary>
public static class PhoneTimeline
{
    /// <summary>Phones that carry no sound; Sinsy and MUTA spell them differently.</summary>
    public static bool IsPause(string phone)
        => phone is "sil" or "sp" or "pau" or "xx" || phone.Length == 0;

    /// <summary>
    /// One span per label, in label order, including pauses. Labels and states must agree
    /// (labels.Count * statesPerPhone == stateDurations.Count), which is checked here
    /// rather than assumed further down.
    /// </summary>
    public static List<PhoneSpan> Build(
        IReadOnlyList<string> labels, IReadOnlyList<int> stateDurations, int statesPerPhone)
    {
        var spans = new List<PhoneSpan>(labels.Count);
        if (statesPerPhone <= 0 || labels.Count == 0)
            return spans;
        if (stateDurations.Count < labels.Count * statesPerPhone)
            throw new InvalidOperationException(
                $"state durations ({stateDurations.Count}) cover {stateDurations.Count / statesPerPhone} " +
                $"of {labels.Count} labels");

        int frame = 0;
        for (int i = 0; i < labels.Count; i++)
        {
            int length = 0;
            for (int state = 0; state < statesPerPhone; state++)
                length += stateDurations[i * statesPerPhone + state];
            spans.Add(new PhoneSpan(PhoneOf(labels[i]), frame, length));
            frame += length;
        }
        return spans;
    }

    /// <summary>The span the singing occupies: first to last non-pause phone, in frames.</summary>
    public static (int Start, int End)? Content(IReadOnlyList<PhoneSpan> spans)
    {
        int start = -1, end = -1;
        for (int i = 0; i < spans.Count; i++)
        {
            if (IsPause(spans[i].Phone) || spans[i].Frames <= 0)
                continue;
            if (start < 0)
                start = spans[i].StartFrame;
            end = spans[i].EndFrame;
        }
        return start >= 0 && end > start ? (start, end) : null;
    }

    /// <summary>Only the sounding phones, in order: what the bank assembles slots from.</summary>
    public static List<int> SungDurations(IReadOnlyList<PhoneSpan> spans)
    {
        var durations = new List<int>(spans.Count);
        foreach (var span in spans)
        {
            if (IsPause(span.Phone) || span.Frames <= 0)
                continue;
            durations.Add(span.Frames);
        }
        return durations;
    }

    /// <summary>
    /// The phone of one full-context label: "start end ll^l-ph+ph2=rr/..." -- the phone sits
    /// between the dash and the plus of the third field.
    ///
    /// The search starts at the third field, not at the line: a label may begin with a
    /// negative start time ("-1 ..."), and looking for the first dash in the whole line
    /// finds that minus instead of the phone.
    /// </summary>
    public static string PhoneOf(string label)
    {
        int first = label.IndexOf(' ');
        int second = first >= 0 ? label.IndexOf(' ', first + 1) : -1;
        if (second < 0)
            return string.Empty;

        int caret = label.IndexOf('^', second + 1);
        int dash = label.IndexOf('-', caret >= 0 ? caret + 1 : second + 1);
        int plus = dash >= 0 ? label.IndexOf('+', dash + 1) : -1;
        return dash >= 0 && plus > dash ? label[(dash + 1)..plus] : string.Empty;
    }
}
