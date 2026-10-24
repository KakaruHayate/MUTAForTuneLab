using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MutaForTuneLab.Synthesis;

/// <summary>One phone-level slot.</summary>
public sealed class LabelSlot
{
    public required string Phone { get; init; }
    /// <summary>-1 break, 0 consonant, 6 vowel.</summary>
    public required int SlotClass { get; init; }
    /// <summary>Absolute note number; 0 on breaks.</summary>
    public required int Pitch { get; init; }
    public long StartTick { get; set; }
    /// <summary>Rewritten by the consonant pass.</summary>
    public long LengthTick { get; set; }
    public int StartFrame { get; set; }
    public int LengthFrame { get; set; }
}

/// <summary>Note-level score: the input to the slot expansion.</summary>
public sealed class NoteScore
{
    /// <summary>Pinyin syllables per note, breaks ("sil"/"sp") included.</summary>
    public required IReadOnlyList<string> Phones { get; init; }
    public required IReadOnlyList<int> Starts { get; init; }
    public required IReadOnlyList<int> Durations { get; init; }
    public required IReadOnlyList<int> Pitches { get; init; }
    public int Tempo { get; init; } = 120;
}

/// <summary>
/// Full-context HTS labels for a MUTA song, one per phone slot.
///
/// The format is fixed and unforgiving: a single field off by one byte changes the
/// model's durations and F0. Everything here is integer arithmetic on 100 ns ticks
/// (frames × 50000), and the branch order of the syllable splitter and the phone
/// classifiers is part of the specification, not a detail.
///
/// Two behaviours are deliberately odd because the engine is:
///   * the final slot reads one slot past the end (undefined in the original) — we
///     define it to the value the reference produces;
///   * the in-note position counters wrap over a 15-slot ring.
/// </summary>
public static class LabelBuilder
{
    public const string Pad = "xx";
    /// <summary>Frames to 100 ns ticks.</summary>
    public const double TickScale = 50000.0;
    public const int MaxNoteSlots = 15;
    public const double ConsonantLengthRatio = 0.5;
    public const double NoteStartBlend = 0.2;
    /// <summary>mcore pitch codes are MIDI - 12.</summary>
    public const int PitchCodeOffset = 12;

    // ---- phone classifiers -------------------------------------------------

    /// <summary>0 pause, 1 glide, 2 nasal/liquid, 3 stop, 4 fricative, 5 affricate, -1 other.</summary>
    public static int PhoneClass(string phone) => phone switch
    {
        "sil" or "sp" => 0,
        _ when StartsWithAny(phone, "ga", "go", "ge", "ger", "w", "y") => 1,
        _ when StartsWithAny(phone, "m", "n", "l", "r") => 2,
        _ when StartsWithAny(phone, "b", "p", "d", "t", "g", "k") => 3,
        _ when StartsWithAny(phone, "f", "h", "j", "q", "x") => 4,
        _ when StartsWithAny(phone, "zh", "ch", "sh", "z", "c", "s") => 5,
        _ => -1,
    };

    /// <summary>Minimum slot duration in ticks; -1 when the phone is unrecognised.</summary>
    public static long MinPhoneDuration(string phone) => phone switch
    {
        "sil" or "sp" => 0,
        _ when StartsWithAny(phone, "ga", "go", "ge", "ger") => 500000,
        _ when StartsWithAny(phone, "a", "o", "e", "er", "i", "u", "v", "w", "y") => 500000,
        _ when phone.StartsWith('m') => 860000,
        _ when phone.StartsWith('n') => 800000,
        _ when phone.StartsWith('l') => 750000,
        _ when phone.StartsWith('r') => 710000,
        _ when phone.StartsWith('b') => 470000,
        _ when phone.StartsWith('p') => 640000,
        _ when phone.StartsWith('d') => 500000,
        _ when phone.StartsWith('t') => 700000,
        _ when phone.StartsWith('g') => 500000,
        _ when phone.StartsWith('k') => 780000,
        _ when phone.StartsWith('f') => 940000,
        _ when phone.StartsWith('h') => 880000,
        _ when phone.StartsWith('j') => 840000,
        _ when phone.StartsWith('q') => 0x122870,
        _ when phone.StartsWith('x') => 0x15AAE0,
        _ when StartsWithAny(phone, "zh") => 800000,
        _ when StartsWithAny(phone, "ch") => 0x10EFF0,
        _ when StartsWithAny(phone, "sh") => 0x16BC50,
        _ when phone.StartsWith('z') => 750000,
        _ when phone.StartsWith('c') => 0x100590,
        _ when phone.StartsWith('s') => 1400000,
        _ => -1,
    };

    public static bool IsBreak(string phone) => phone is "sil" or "sp";

    public static bool IsDash(string phone) => phone == "-";

    /// <summary>Splits a pinyin syllable into initial and final; null when not splittable.</summary>
    public static (string Initial, string Final)? SplitSyllable(string phone)
    {
        if (string.IsNullOrEmpty(phone) || char.IsWhiteSpace(phone[0]) && phone.Trim().Length == 0)
            return null;
        switch (phone)
        {
            case "sil": return ("sil", "");
            case "sp": return ("sp", "");
            case "-": return ("-", "");
        }

        // The order is the specification: longer/diacritic cases must precede their
        // prefixes (zhi before zi, ju before yu, you before ye, wa* before wen).
        if (phone.StartsWith("er", StringComparison.Ordinal)) return ("ger", phone);
        if (phone.StartsWith('a')) return ("ga", phone);
        if (phone.StartsWith('o')) return ("go", phone);
        if (phone.StartsWith('e')) return ("ge", phone);
        if (phone.StartsWith("zhi", StringComparison.Ordinal)) return ("zh", "ib" + phone[3..]);
        if (phone.StartsWith("chi", StringComparison.Ordinal)) return ("ch", "ib" + phone[3..]);
        if (phone.StartsWith("shi", StringComparison.Ordinal)) return ("sh", "ib" + phone[3..]);
        if (phone.StartsWith("ri", StringComparison.Ordinal)) return ("r", "ib" + phone[2..]);
        if (phone.StartsWith("zi", StringComparison.Ordinal)) return ("z", "if" + phone[2..]);
        if (phone.StartsWith("ci", StringComparison.Ordinal)) return ("c", "if" + phone[2..]);
        if (!phone.StartsWith("si", StringComparison.Ordinal))
        {
            if (phone.StartsWith("ju", StringComparison.Ordinal)) return ("j", "v" + phone[2..]);
            if (phone.StartsWith("qu", StringComparison.Ordinal)) return ("q", "v" + phone[2..]);
            if (phone.StartsWith("xu", StringComparison.Ordinal)) return ("x", "v" + phone[2..]);
            if (!phone.StartsWith("yu", StringComparison.Ordinal))
            {
                if (StartsWithAny(phone, "ye", "yan", "yang", "ya", "yao", "yong"))
                    return ("y", "i" + phone[1..]);
                if (phone.StartsWith("you", StringComparison.Ordinal)) return ("y", "iu" + phone[3..]);
                if (!phone.StartsWith("wei", StringComparison.Ordinal))
                {
                    if (StartsWithAny(phone, "wa", "wai", "wan", "wang", "weng", "wo"))
                        return ("w", "u" + phone[1..]);
                    if (!phone.StartsWith("wen", StringComparison.Ordinal))
                    {
                        if (phone.Length > 1 && phone[1] == 'h')
                            return (phone[..2], phone[2..]);
                        return (phone[..1], phone[1..]);
                    }
                    return ("w", "un" + phone[3..]);
                }
                return ("w", "ui" + phone[3..]);
            }
            return ("y", "v" + phone[2..]);
        }
        return ("s", "if" + phone[2..]);
    }

    static bool StartsWithAny(string phone, params string[] prefixes)
    {
        foreach (var prefix in prefixes)
            if (phone.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        return false;
    }

    static bool StartsWithAny(string phone, string a, string b, string c, string d, string e, string f)
        => StartsWithAny(phone, new[] { a, b, c, d, e, f });

    // ---- helpers -----------------------------------------------------------

    public static long FramesToTicks(int frames) => (long)Math.Round(frames * TickScale, MidpointRounding.ToEven);

    /// <summary>C integer division: truncating toward zero, not flooring.</summary>
    public static long Half(long value) => value >= 0 ? value / 2 : -((-value) / 2);

    static int PitchCode(int value) => value > 0 ? value - PitchCodeOffset : value;

    // ---- expansion ---------------------------------------------------------

    /// <summary>Expands notes into phone slots (the note-boundary overrides are optional).</summary>
    public static List<LabelSlot> ExpandNotes(
        NoteScore score,
        IReadOnlyList<int>? controlStarts = null,
        IReadOnlyList<int>? controlLengths = null,
        int? controlCount = null)
    {
        if (score.Phones.Count != score.Starts.Count ||
            score.Phones.Count != score.Durations.Count ||
            score.Phones.Count != score.Pitches.Count)
            throw new ArgumentException("note arrays must share one length");

        var slots = new List<LabelSlot>();
        for (int index = 0; index < score.Phones.Count; index++)
        {
            string syllable = score.Phones[index];
            int start = score.Starts[index];
            int duration = score.Durations[index];
            int pitch = score.Pitches[index];

            if (IsBreak(syllable))
            {
                slots.Add(new LabelSlot
                {
                    Phone = syllable, SlotClass = -1, Pitch = 0,
                    StartTick = FramesToTicks(start), LengthTick = 0,
                    StartFrame = start, LengthFrame = 0,
                });
                if (duration > 200)
                {
                    int spStart = start + duration - 200;
                    slots.Add(new LabelSlot
                    {
                        Phone = "sp", SlotClass = -1, Pitch = 0,
                        StartTick = FramesToTicks(spStart), LengthTick = 0,
                        StartFrame = spStart, LengthFrame = 0,
                    });
                }
                continue;
            }
            if (IsDash(syllable))
                continue;

            var split = SplitSyllable(syllable);
            if (split is null)
                continue;

            // A voiced note becomes two slots sharing one start: consonant then vowel.
            slots.Add(new LabelSlot
            {
                Phone = split.Value.Initial, SlotClass = 0, Pitch = pitch,
                StartTick = FramesToTicks(start), LengthTick = 0,
                StartFrame = start, LengthFrame = 0,
            });
            slots.Add(new LabelSlot
            {
                Phone = split.Value.Final, SlotClass = 6, Pitch = pitch,
                StartTick = FramesToTicks(start), LengthTick = 0,
                StartFrame = start, LengthFrame = 0,
            });
        }

        if (controlStarts is not null && controlLengths is not null)
        {
            int limit = Math.Min(controlStarts.Count, controlLengths.Count);
            if (controlCount is { } count)
                limit = Math.Min(limit, Math.Max(0, count));

            int controlIndex = 0;
            for (int index = 1; index < slots.Count - 1; index++)
            {
                if (slots[index].SlotClass != 0)
                    continue;
                if (controlIndex >= limit)
                    break;

                var following = slots[index + 1];
                int lengthDelta = controlLengths[controlIndex];
                if (lengthDelta != 0)
                {
                    following.StartFrame += lengthDelta;
                    following.StartTick = FramesToTicks(following.StartFrame);
                }
                int startDelta = controlStarts[controlIndex];
                if (startDelta != 0)
                {
                    int candidate = slots[index].StartFrame - startDelta;
                    int previousStart = slots[index - 1].StartFrame;
                    int span = following.StartFrame - previousStart;
                    int blended = (int)(span * NoteStartBlend) + previousStart;
                    slots[index].StartFrame = blended < candidate ? blended : candidate;
                    slots[index].StartTick = FramesToTicks(slots[index].StartFrame);
                }
                controlIndex++;
            }
        }

        for (int index = 0; index < slots.Count - 1; index++)
        {
            slots[index].LengthFrame = slots[index + 1].StartFrame - slots[index].StartFrame;
            slots[index].LengthTick = slots[index + 1].StartTick - slots[index].StartTick;
        }
        if (slots.Count > 0)
        {
            var last = slots[^1];
            int tail = score.Starts[^1] + score.Durations[^1];
            last.LengthFrame = tail - last.StartFrame;
            last.LengthTick = FramesToTicks(last.LengthFrame);
        }
        return slots;
    }

    public static int NoteBoundaryCount(List<LabelSlot> slots)
    {
        int total = 0;
        for (int index = 0; index < slots.Count - 1; index++)
            if (slots[index].SlotClass == -1 && slots[index + 1].SlotClass > -1)
                total++;
        return total;
    }

    /// <summary>
    /// Each consonant borrows its minimum duration from the break in front of it.
    /// Returns the borrowed amount per slot, -1 marking an unrecognised phone class.
    /// </summary>
    public static long[] ApplyConsonantDurations(List<LabelSlot> slots)
    {
        var borrowed = new long[slots.Count];
        for (int index = 0; index < slots.Count - 1; index++)
        {
            var current = slots[index];
            if (current.SlotClass < 0 || current.SlotClass != 0)
            {
                borrowed[index] = 0;
                continue;
            }
            if (current.LengthTick != 0)
            {
                borrowed[index] = current.LengthTick;
                continue;
            }
            int phoneClass = PhoneClass(current.Phone);
            if (phoneClass is not (1 or 2 or 3 or 4 or 5))
            {
                borrowed[index] = -1;
                continue;
            }

            long amount = MinPhoneDuration(current.Phone);
            borrowed[index] = amount;
            var previous = index > 0 ? slots[index - 1] : null;
            if (index < 1 || previous!.SlotClass < 0 || amount <= previous.LengthTick * ConsonantLengthRatio)
            {
                if (index > 0 && previous!.SlotClass < 0 && previous.LengthTick < amount)
                    borrowed[index] = previous.LengthTick;
            }
            else
            {
                // Unreachable in practice: a consonant slot is always preceded by a break.
                borrowed[index] = (long)(previous!.LengthTick * ConsonantLengthRatio);
            }

            current.StartTick -= borrowed[index];
            current.LengthTick = borrowed[index];
            if (index > 0)
                previous!.LengthTick -= borrowed[index];
        }
        return borrowed;
    }

    public static int[] NoteCounts(List<LabelSlot> slots)
    {
        int total = NoteBoundaryCount(slots);
        var counts = new int[total];
        int note = -1;
        for (int index = 0; index < slots.Count - 1; index++)
        {
            if (slots[index].SlotClass == -1 && slots[index + 1].SlotClass > -1)
                note++;
            if (note >= total)
                break;
            if (note >= 0 && slots[index].SlotClass > 0)
                counts[note]++;
        }
        for (int i = 0; i < counts.Length; i++)
            counts[i] = Math.Min(counts[i], MaxNoteSlots);
        return counts;
    }

    /// <summary>Notes that expand to two slots (breaks and dashes excluded).</summary>
    public static int VoicedNoteCount(NoteScore score)
    {
        int total = 0;
        foreach (var phone in score.Phones)
            if (!IsBreak(phone) && !IsDash(phone))
                total++;
        return total;
    }

    /// <summary>Notes per second across the phrase, floored at 1.</summary>
    public static int SpeedValue(NoteScore score)
    {
        if (score.Phones.Count == 0)
            return 1;
        int span = ((score.Starts[^1] + score.Durations[^1]) * 5) / 1000;
        if (span < 1)
            return 1;
        return Math.Max(1, VoicedNoteCount(score) / span);
    }

    /// <summary>
    /// Per-slot (previous count, current count, next count, forward position, backward
    /// position). The three count strings carry across slots and are only rewritten at
    /// a note boundary.
    /// </summary>
    public static List<(string Previous, string Current, string Following, int Forward, int Backward)>
        WalkPositions(List<LabelSlot> slots, int[] counts, IReadOnlyList<int>? postSlotClasses = null)
    {
        int total = counts.Length;
        string previous = Pad;
        string current = Pad;
        string following = total < 1 ? Pad : counts[0].ToString(CultureInfo.InvariantCulture);
        int forward = 0;
        int backward = 0;
        int note = -1;
        var result = new List<(string, string, string, int, int)>(slots.Count);
        int index = 0;

        while (index < slots.Count && note < total)
        {
            int slotClass = slots[index].SlotClass;
            int nextClass;
            if (index + 1 < slots.Count)
                nextClass = slots[index + 1].SlotClass;
            else if (postSlotClasses is { Count: > 0 })
                nextClass = postSlotClasses[0];
            else
                nextClass = -1;

            if (slotClass == -1 && nextClass > -1)
            {
                int advanced = note + 1;
                forward = 0;
                backward = advanced < total ? counts[advanced] + 1 : 0;
                if (advanced < 1)
                {
                    note = advanced;
                    previous = Pad;
                }
                else
                {
                    previous = counts[note].ToString(CultureInfo.InvariantCulture);
                    note = advanced;
                }
                following = note < total - 1
                    ? counts[note + 1].ToString(CultureInfo.InvariantCulture)
                    : Pad;
            }
            else if (slotClass == -1 && index + 1 >= slots.Count && note >= 0)
            {
                // The original reads one slot past the end here. We define that read to
                // "a transition fired", which is the value it produces in practice.
                previous = counts[note].ToString(CultureInfo.InvariantCulture);
            }
            else if (note > -1 && slotClass == 0)
            {
                forward = (forward + 1) % 15;
                backward = (((backward - 1) % 15) + 15) % 15;
                if (backward < 1)
                    backward += 15;
            }

            current = slotClass < 0
                ? Pad
                : (note >= 0 && note < total ? counts[note].ToString(CultureInfo.InvariantCulture) : Pad);

            result.Add((previous, current, following, forward, backward));
            index++;
        }
        return result;
    }

    /// <summary>Formats one slot's label.</summary>
    public static string BuildLabel(
        List<LabelSlot> slots, int index, int[] counts, int speed,
        long[] borrowed,
        List<(string Previous, string Current, string Following, int Forward, int Backward)> positions)
    {
        int size = slots.Count;
        if (index < 0 || index >= size)
            throw new ArgumentOutOfRangeException(nameof(index));

        var slot = slots[index];
        int slotClass = slot.SlotClass;
        var (previous, current, following, forward, backward) = positions[index];

        int ClassAt(int offset)
        {
            int position = index + offset;
            return position < 0 || position >= size ? -1 : slots[position].SlotClass;
        }
        int PitchAt(int offset)
        {
            int position = index + offset;
            return position < 0 || position >= size ? 0 : slots[position].Pitch;
        }
        string PhoneAt(int offset)
        {
            int position = index + offset;
            return position < 0 || position >= size ? Pad : slots[position].Phone;
        }
        int LengthAt(int offset)
        {
            int position = index + offset;
            return position < 0 || position >= size ? 0 : slots[position].LengthFrame;
        }
        string Delta(int target) => slot.Pitch < target
            ? "m" + (target - slot.Pitch).ToString(CultureInfo.InvariantCulture)
            : "p" + (slot.Pitch - target).ToString(CultureInfo.InvariantCulture);

        string p2 = PhoneAt(-2), p1 = PhoneAt(-1), cur = slot.Phone;
        string n1 = PhoneAt(1), n2 = PhoneAt(2);

        // /D: previous note pitch
        string dNote;
        if (index < 1)
            dNote = Pad;
        else if (slotClass <= 0)
            dNote = ClassAt(-1) < 0 ? Pad : Str(PitchCode(PitchAt(-1)));
        else
            dNote = ClassAt(-2) < 0 ? Pad : Str(PitchCode(PitchAt(-2)));

        // !: previous note length, halved, in frames
        string dLen;
        if (index < 1)
            dLen = Pad;
        else if (slotClass < 0)
            dLen = Str(Half(LengthAt(-1)));
        else if (slotClass == 0)
            dLen = Str(Half(LengthAt(-1) + LengthAt(0)));
        else
            dLen = Str(Half(LengthAt(-2) + LengthAt(-1)));

        // /E: current note pitch
        string e1 = slotClass < 0 ? Pad : Str(PitchCode(slot.Pitch));

        // &: next note length, halved
        string e2;
        if (slotClass is < 0 or > 0)
            e2 = index + 1 < size && ClassAt(1) == 0
                ? Str(Half(LengthAt(0) + LengthAt(1)))
                : Str(Half(LengthAt(0)));
        else
            e2 = index + 2 < size && ClassAt(2) == 0
                ? Str(Half(LengthAt(1) + LengthAt(2)))
                : Str(Half(LengthAt(1)));

        // [ and @: in-note position counters
        string e3 = slotClass < 0 ? Pad : Str(forward);
        string e4 = slotClass < 0 ? Pad : Str(backward);

        // ] previous pitch delta, + next pitch delta
        string e5, e6;
        if (index < 1 || index >= size - 1 || slotClass < 0)
        {
            e5 = Pad;
            e6 = Pad;
        }
        else
        {
            if (slotClass == 0)
                e5 = ClassAt(-1) < 0 ? Pad : Delta(PitchAt(-1));
            else
                e5 = ClassAt(-2) < 0 ? Pad : Delta(PitchAt(-2));

            if (slotClass == 0)
                e6 = ClassAt(2) < 0 ? Pad : Delta(PitchAt(2));
            else
                e6 = ClassAt(1) < 0 ? Pad : Delta(PitchAt(1));
        }

        // /F: next note pitch
        string f1;
        if (index >= size - 1)
            f1 = Pad;
        else if (slotClass is < 0 or > 0)
            f1 = ClassAt(1) < 0 ? Pad : Str(PitchCode(PitchAt(1)));
        else
            f1 = ClassAt(2) < 0 ? Pad : Str(PitchCode(PitchAt(2)));

        // #: following note length, halved
        string f2;
        if (index + 1 >= size)
            f2 = Pad;
        else if (slotClass is < 0 or > 0)
        {
            if (ClassAt(1) < 0)
                f2 = index + 2 < size && ClassAt(2) == 0
                    ? Str(Half(LengthAt(1) + LengthAt(2)))
                    : Str(Half(LengthAt(1)));
            else if (index + 3 < size && ClassAt(3) == 0)
                f2 = Str(Half(LengthAt(2) + LengthAt(3)));
            else if (index + 2 < size)
                f2 = Str(Half(LengthAt(2)));
            else
                f2 = Str(Half(LengthAt(1)));
        }
        else if (ClassAt(2) < 0)
        {
            f2 = index + 3 < size && ClassAt(3) == 0
                ? Str(Half(LengthAt(2) + LengthAt(3)))
                : Str(Half(LengthAt(2)));
        }
        else if (index + 4 < size && ClassAt(4) == 0)
            f2 = Str(Half(LengthAt(3) + LengthAt(4)));
        else if (index + 3 < size)
            f2 = Str(Half(LengthAt(3)));
        else
            f2 = Str(Half(LengthAt(2)));

        // start and end, in 100 ns ticks
        long start, end;
        if (slotClass == 0)
        {
            long nextStart = index + 1 < size ? slots[index + 1].StartTick : slot.StartTick;
            start = borrowed[index] == -1 ? -1 : nextStart - borrowed[index];
            end = nextStart;
        }
        else if (index + 1 < size && ClassAt(1) == 0)
        {
            start = slot.StartTick;
            end = borrowed[index + 1] == -1 ? -1 : slot.StartTick + slot.LengthTick;
        }
        else
        {
            start = slot.StartTick;
            end = slot.StartTick + slot.LengthTick;
        }

        var sb = new StringBuilder(160);
        sb.Append(start).Append(' ').Append(end).Append(' ')
          .Append(p2).Append('^').Append(p1).Append('-').Append(cur)
          .Append('+').Append(n1).Append('=').Append(n2)
          .Append("/D:").Append(dNote).Append('!').Append(dLen)
          .Append("/E:").Append(e1).Append('&').Append(e2)
          .Append('[').Append(e3).Append('@').Append(e4).Append(']')
          .Append(e5).Append('+').Append(e6)
          .Append("/F:").Append(f1).Append('#').Append(f2)
          .Append("/G:").Append(speed)
          .Append("/H:").Append(previous)
          .Append("/I:").Append(current)
          .Append("/J:").Append(following);
        return sb.ToString();
    }

    static string Str(long value) => value.ToString(CultureInfo.InvariantCulture);
    static string Str(int value) => value.ToString(CultureInfo.InvariantCulture);

    // ---- entry points ------------------------------------------------------

    public static List<string> Build(
        NoteScore score,
        IReadOnlyList<int>? controlStarts = null,
        IReadOnlyList<int>? controlLengths = null,
        int? controlCount = null)
        => BuildFromSlots(score, ExpandNotes(score, controlStarts, controlLengths, controlCount));

    /// <summary>
    /// Formats labels from already-expanded slots. Works on copies: the consonant pass
    /// mutates the slot it visits and the one before it.
    /// </summary>
    public static List<string> BuildFromSlots(
        NoteScore score, List<LabelSlot> slots, IReadOnlyList<int>? postSlotClasses = null)
    {
        if (slots.Count == 0)
            return [];

        var prepared = new List<LabelSlot>(slots.Count);
        foreach (var slot in slots)
        {
            prepared.Add(new LabelSlot
            {
                Phone = slot.Phone,
                SlotClass = slot.SlotClass,
                Pitch = slot.Pitch,
                StartTick = slot.StartTick,
                LengthTick = slot.LengthTick,
                StartFrame = slot.StartFrame,
                LengthFrame = slot.LengthFrame,
            });
        }

        var borrowed = ApplyConsonantDurations(prepared);
        var counts = NoteCounts(prepared);
        int speed = SpeedValue(score);
        var positions = WalkPositions(prepared, counts, postSlotClasses);

        var labels = new List<string>(prepared.Count);
        for (int index = 0; index < prepared.Count; index++)
            labels.Add(BuildLabel(prepared, index, counts, speed, borrowed, positions));
        return labels;
    }
}
