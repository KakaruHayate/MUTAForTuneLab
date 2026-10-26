using System;
using System.Collections.Generic;
using MutaForTuneLab.Assets;

namespace MutaForTuneLab.Synthesis;

/// <summary>One note as the assembler needs it: on the model's frame grid, already phonetic.</summary>
/// <param name="Phone">The label front end's own text: a pinyin syllable, or kana for Japanese.</param>
/// <param name="ModelInitial">The onset the labels will sing (null for a bare vowel/nasal).</param>
/// <param name="ModelFinal">The nucleus those same labels will sing.</param>
/// <param name="BankInitial">The onset rewritten onto the bank's phone names (Japanese only).</param>
/// <param name="BankFinal">The nucleus rewritten onto the bank's phone names (Japanese only).</param>
/// <param name="BankLeft">A left context to score the nucleus against instead of the previous
/// phone: Japanese ん is the recorded unit 恩 (en after the zero-initial "ge"), not a bare vowel.</param>
public readonly record struct RenderNote(
    int StartFrame, int Frames, int Midi, string Phone, bool Rest = false, string Lyric = "",
    string? ModelInitial = null, string? ModelFinal = null,
    string? BankInitial = null, string? BankFinal = null,
    string? BankLeft = null,
    // How many phones the labels will emit for this note. A geminate adds a "cl" in front of
    // the next mora, so the count is not always "onset ? 2 : 1".
    int ModelPhoneCount = 0,
    // Non-null when the user pinned this note's phonemes: they decide what is reported and
    // which recordings are selected, not the G2P guess.
    PinnedPhones? Pinned = null,
    // A melisma passenger: the head note's vowel is stretched over this note and the labels
    // emit nothing for it (MUTA's `-`/`+` behaviour). It still has its own pitch, so the F0
    // mechanism glides into it.
    bool Continuation = false,
    // A breath note: silence in the acoustic model, with a breath sample mixed in afterwards.
    bool Breath = false);

/// <summary>What the assembler needs from a bank; lets tests feed it directly.</summary>
public interface IBankSource
{
    int MgcOrder { get; }
    int BapOrder { get; }
    IReadOnlyDictionary<string, List<InfItem>> Groups { get; }
    (double[][] Mgc, double[][] Bap) Streams(InfItem item);
}
/// <summary>The assembled acoustic parameters for one span.</summary>
public sealed record BankResult(double[][] Mgc, double[][] Bap, bool[] Silence);

/// <summary>
/// Builds a span out of recorded contexts.
///
/// Each note becomes one or two slots: the onset consonant, then the vowel. Each slot picks
/// the recorded context whose neighbours and pitch resemble its own, stretches that context
/// into the space the note has, and is blended into the buffers at a slight overlap — the
/// same three steps the engine performs, in the same order.
///
/// The durations that drive the stretch are our own estimates rather than the model's
/// alignment: the engine gets them from a front end we have not ported. That makes the
/// timing approximate while the selection and blending are exact.
/// </summary>
public static class BankRenderer
{
    static readonly HashSet<string> Plosives = new(StringComparer.Ordinal) { "b", "d", "g", "p", "t", "k" };
    static readonly HashSet<string> Fricatives = new(StringComparer.Ordinal)
        { "c", "ch", "f", "h", "j", "q", "s", "sh", "x", "z", "zh" };
    static readonly HashSet<string> Nasals = new(StringComparer.Ordinal) { "m", "n", "l", "r", "w", "y", "v" };
    static readonly HashSet<string> LongFinals = new(StringComparer.Ordinal)
        { "ang", "eng", "iang", "uang", "iong", "ong" };

    /// <summary>Nominal length of a consonant or vowel, in frames.</summary>
    public static int TargetFrames(string phone)
    {
        if (Plosives.Contains(phone))
            return 10;
        if (Fricatives.Contains(phone))
            return 18;
        if (Nasals.Contains(phone))
            return 16;
        if (LongFinals.Contains(phone))
            return 62;
        return 54;
    }

    public static BankResult Assemble(
        IBankSource assets, IReadOnlyList<RenderNote> notes, int totalFrames, string bankPitchMode = "auto", double bankPitchOffset = 0.0, int bankPitchNote = 0,
        IReadOnlyList<int>? phoneFrames = null)
    {
        int mgcOrder = assets.MgcOrder;
        int bapOrder = assets.BapOrder;
        var mgc = NewBuffer(totalFrames, mgcOrder);
        var bap = NewBuffer(totalFrames, bapOrder);
        var silence = new bool[totalFrames];
        Array.Fill(silence, true);

        int consSlots = 0, vowelSlots = 0, missedSlots = 0;
        int phoneCursor = 0;
        int substitutedSlots = 0;
        var picks = new List<string>();
        var state = new NoteAssemblyState();
        string previousPhone = "sil";

        for (int index = 0; index < notes.Count; index++)
        {
            var note = notes[index];
            if (note.StartFrame >= totalFrames)
                break;

            // A melisma passenger has no slot of its own: MUTA lets the head vowel's slot
            // run over it, so the recording simply continues instead of a new vowel attack.
            // Its duration was added to the head's vowel below, and the labels emit no
            // phones for it either, so there is nothing to consume here.
            if (note.Continuation)
                continue;

            if (note.Rest)
            {
                int frames = Math.Min(note.Frames, totalFrames - note.StartFrame);
                if (frames > 0)
                    BankAssembler.FillRestMgc(mgc, note.StartFrame, frames);
                previousPhone = "sil";
                state = new NoteAssemblyState(PreviousWasRest: true);
                continue;
            }

            // The splits come from the front end, once: the model's own onset/nucleus and
            // the bank's rewritten pair are different questions, and only the caller knows
            // both. Re-splitting a Sinsy phone with the Chinese rules here is what used to
            // hand the selector an onset of "k" and a nucleus of "ya".
            string? initial = string.IsNullOrEmpty(note.BankInitial) ? null : note.BankInitial;
            string final = string.IsNullOrEmpty(note.BankFinal) ? note.Phone : note.BankFinal!;

            // A closure is silence by definition. Without this it fell into the "mapped vowel
            // has no recording" fallback, which sings an "a" -- so a sokuon made a vowel.
            if (final is "cl" or "sil" or "sp")
            {
                // Its model phones still have to be consumed, or the next note's slot lengths
                // are read one phone late.
                int closurePhones = note.ModelPhoneCount > 0 ? note.ModelPhoneCount : 1;
                for (int phone = 0; phone < closurePhones; phone++)
                    TakePhoneFrames(phoneFrames, ref phoneCursor);
                previousPhone = "sil";
                state = new NoteAssemblyState(PreviousWasRest: true);
                continue;
            }

            // The onset takes the duration the model gave that phone; the vowel fills the
            // rest of the note, because a note is as long as the score says it is. Falling
            // back to the table keeps voices that give us no durations working.
            // Consume exactly the phones the labels emitted for this note. The closure a
            // geminate puts in front of the consonant belongs to the previous (silent) note --
            // adding its frames here ate the whole note, leaving the vowel a stub. So the
            // onset length is the phone right before the vowel, not the sum.
            int modelCosonantCount = note.ModelPhoneCount > 0
                ? note.ModelPhoneCount
                : (initial is not null ? 2 : 1);
            int modelConsonant = 0;
            for (int phone = 0; phone < modelCosonantCount; phone++)
            {
                int frames = TakePhoneFrames(phoneFrames, ref phoneCursor);
                if (phone < modelCosonantCount - 1)
                    modelConsonant = frames;
            }

            // And never let the onset take more than most of the note. After a sokuon the model
            // stretches the following consonant a lot (it carries the closure), so that case
            // gets a tighter cap; everywhere else the model's own duration is kept.
            RenderNote? previousContent = null;
            for (int back = index - 1; back >= 0; back--)
            {
                if (notes[back].Continuation)
                    continue;
                previousContent = notes[back];
                break;
            }
            bool afterClosure = previousContent is { BankFinal: "cl" };
            int cap = Math.Max(1, (int)(note.Frames * (afterClosure ? 0.3 : 0.6)));
            int consonantFrames = initial is null
                ? 0
                : Math.Max(1, Math.Min(modelConsonant > 0 ? modelConsonant : TargetFrames(initial), cap));

            // The vowel owns the whole held span: MUTA does not open a new slot at a
            // continuation mark, it stretches the head's final across it. Skipping those
            // notes in the loop and adding their frames here is that same shape.
            int heldFrames = 0;
            for (int ahead = index + 1; ahead < notes.Count && notes[ahead].Continuation; ahead++)
                heldFrames += notes[ahead].Frames;
            int vowelFrames = Math.Max(10, note.Frames + heldFrames);

            // Pinned phoneme durations are the user's explicit choice. The bank path can
            // honour them exactly (the model path would need HTS phoneme alignment), so use
            // the host's seconds instead of the model/table guesses when they are present.
            if (note.Pinned is { } pinned)
            {
                if (initial is not null && pinned.LeadingDurations.Length > 0)
                    consonantFrames = Math.Max(1,
                        FramesOf(pinned.LeadingDurations, pinned.LeadingWeights));
                if (pinned.BodyDurations.Length > 0)
                    vowelFrames = Math.Max(10,
                        FramesOf(pinned.BodyDurations, pinned.BodyWeights) + heldFrames);
            }

            // What the vowel runs into is still the next *content* note's onset: a run of
            // continuation marks sits between the two and its own onset never sounds.
            RenderNote? next = null;
            for (int ahead = index + 1; ahead < notes.Count; ahead++)
            {
                next = notes[ahead];
                if (!next.Value.Continuation)
                    break;
            }
            int nextConsonantFrames = 0;
            string? nextConsonant = null;
            if (next is { } following && !following.Rest && !following.Continuation)
            {
                if (!string.IsNullOrEmpty(following.BankInitial))
                {
                    nextConsonantFrames = Math.Min(TargetFrames(following.BankInitial), following.Frames);
                    nextConsonant = following.BankInitial;
                }
            }

            // A vowel has no left-context class of its own, so the selector refuses it as a
            // right context. What the vowel runs into is the next consonant, or a break.
            string rightContext = nextConsonant ?? "sil";

            int cursor = note.StartFrame;
            int previousDuration = 0;

            if (initial is not null)
            {
                int nextSlotClass = 1;
                var plan = BankAssembler.Plan(
                    slotClass: 0, duration: consonantFrames,
                    previousDuration: previousDuration,
                    nextDuration: vowelFrames,
                    nextSlotClass: nextSlotClass,
                    sourceSuffix: 0,
                    state: state,
                    leftContextNonZero: previousPhone != "sil");

                var item = BankContext.SelectNoteItem(assets.Groups, initial, previousPhone, final,
                    plan.TargetCore, PitchFor(note, bankPitchMode, bankPitchOffset, bankPitchNote), 0);
                if (item is not null)
                {
                    var (itemMgc, itemBap) = assets.Streams(item);
                    var resized = BankAssembler.Plan(
                        slotClass: 0, duration: consonantFrames, previousDuration: previousDuration,
                        nextDuration: vowelFrames, nextSlotClass: 1,
                        sourceSuffix: item.SuffixFrames, state: state,
                        leftContextNonZero: previousPhone != "sil");
                    var block = BankAssembler.BuildBlock(itemMgc, itemBap,
                        item.PrefixFrames, item.CoreFrames, item.SuffixFrames,
                        resized.TargetCore, resized.LeftExtension, resized.RightExtension);
                    int consonantStart = Math.Max(0, cursor - consonantFrames);
                    BankAssembler.WriteNoteBlock(mgc, bap, block, consonantStart, resized);
                    consSlots++;
                    if (picks.Count < 8) picks.Add($"{initial}@c{item.PitchCode}#{item.BankOffset}/{consonantFrames}f");
                    MarkVoiced(silence, consonantStart, consonantStart + consonantFrames);
                    previousDuration = consonantFrames;
                    state = resized.NextState;
                }
                previousPhone = initial;
            }

            // The vowel.
            int vowelClass = Math.Max(1, BankContext.RightClass(final));
            var vowelPlan = BankAssembler.Plan(
                slotClass: vowelClass, duration: vowelFrames,
                previousDuration: previousDuration,
                nextDuration: nextConsonantFrames,
                nextSlotClass: nextConsonantFrames > 0 ? 0 : -1,
                sourceSuffix: 0,
                state: state);

            // A note with no onset of its own may still have one in the bank: ん is recorded as
            // 恩 (en after the zero-initial "ge"), and scoring against that left context is what
            // makes the selector find it instead of refusing a bare vowel after a vowel.
            string vowelLeft = note.BankLeft ?? previousPhone;
            var vowelItem = BankContext.SelectNoteItem(assets.Groups, final, vowelLeft,
                rightContext, vowelPlan.TargetCore, PitchFor(note, bankPitchMode, bankPitchOffset, bankPitchNote), vowelClass)
                // A missed slot is silence in the middle of a word. Any recording of the same
                // phone is better than a hole, even when its left context does not match.
                ?? (assets.Groups.TryGetValue(final, out var anyVowel) && anyVowel.Count > 0 ? anyVowel[0] : null);
            if (vowelItem is not null)
            {
                var (itemMgc, itemBap) = assets.Streams(vowelItem);
                var block = BankAssembler.BuildBlock(itemMgc, itemBap,
                    vowelItem.PrefixFrames, vowelItem.CoreFrames, vowelItem.SuffixFrames,
                    vowelPlan.TargetCore, vowelPlan.LeftExtension, vowelPlan.RightExtension);
                BankAssembler.WriteNoteBlock(mgc, bap, block, cursor, vowelPlan);
                vowelSlots++;
                if (picks.Count < 8)
                    picks.Add($"{vowelItem.Current}@c{vowelItem.PitchCode}#{vowelItem.BankOffset}");
                MarkVoiced(silence, cursor, Math.Min(totalFrames, note.StartFrame + note.Frames + heldFrames));
                state = vowelPlan.NextState;
            }
            else
            {
                // The mapped vowel has no recording behind it -- Mandarin has no bo, no fi and
                // no ia after b. A hole here plays as silence in the middle of a word, so the
                // note sings on a plain vowel the bank certainly has instead.
                var fallback = BankContext.SelectNoteItem(assets.Groups, "a", previousPhone,
                    rightContext, vowelPlan.TargetCore,
                    PitchFor(note, bankPitchMode, bankPitchOffset, bankPitchNote), vowelClass);
                if (fallback is not null)
                {
                    substitutedSlots++;
                    var (fallbackMgc, fallbackBap) = assets.Streams(fallback);
                    var fallbackBlock = BankAssembler.BuildBlock(fallbackMgc, fallbackBap,
                        fallback.PrefixFrames, fallback.CoreFrames, fallback.SuffixFrames,
                        vowelPlan.TargetCore, vowelPlan.LeftExtension, vowelPlan.RightExtension);
                    BankAssembler.WriteNoteBlock(mgc, bap, fallbackBlock, cursor, vowelPlan);
                    MarkVoiced(silence, cursor, Math.Min(totalFrames, note.StartFrame + note.Frames + heldFrames));
                    state = vowelPlan.NextState;
                }
                else
                {
                    missedSlots++;
                    MutaDiagnostics.Log(
                        $"[MUTA] bank miss: {note.Phone} final={final} left={vowelLeft} " +
                        $"right={rightContext} class={vowelClass} group={(assets.Groups.ContainsKey(final) ? assets.Groups[final].Count : 0)}");
                }
            }
            previousPhone = final;
        }

        MutaDiagnostics.Log(
            $"[MUTA] bank slots {consSlots} consonant, {vowelSlots} vowel, {missedSlots} missed, {substitutedSlots} substituted; " +
            $"pitch mode {bankPitchMode} offset {bankPitchOffset:F1} fixed {(bankPitchNote > 0 ? bankPitchNote : 0)}; " +
            $"picks {string.Join(" ", picks)}");
        return new BankResult(mgc, bap, silence);
    }

    /// <summary>
    /// The pitch the bank is asked for: the note itself, shifted, or pinned to one region.
    /// The note still sounds at its own pitch; this chooses which recorded register the
    /// frames come from, which is what 换采样 means.
    /// </summary>
    static double PitchFor(RenderNote note, string mode, double offset, int fixedNote)
    {
        if (mode == "fixed" && fixedNote > 0)
            return 440.0 * Math.Pow(2.0, (fixedNote - 69) / 12.0);

        double midi = note.Midi + (mode == "offset" ? offset : 0.0);
        return 440.0 * Math.Pow(2.0, (midi - 69) / 12.0);
    }

    /// <summary>One phone's duration from the model, or 0 when the list ran out.</summary>
    static int FramesOf(IReadOnlyList<double> seconds, IReadOnlyList<double> weights)
    {
        // Duration is the host's absolute length; StretchWeight is its relative stretch.
        // Use both so either way of dragging a phoneme changes the rendered slot.
        double total = 0.0;
        for (int i = 0; i < seconds.Count; i++)
        {
            double weight = i < weights.Count && double.IsFinite(weights[i]) && weights[i] > 0.0
                ? Math.Clamp(weights[i], 0.05, 20.0)
                : 1.0;
            total += seconds[i] * weight;
        }
        const double frameSeconds = 0.005;
        return Math.Max(1, (int)Math.Round(total / frameSeconds));
    }

    static int TakePhoneFrames(IReadOnlyList<int>? frames, ref int cursor)
    {
        if (frames is null || cursor >= frames.Count)
            return 0;
        return frames[cursor++];
    }
    static void MarkVoiced(bool[] silence, int start, int end)
    {
        for (int f = Math.Max(0, start); f < Math.Min(end, silence.Length); f++)
            silence[f] = false;
    }

    static double[][] NewBuffer(int frames, int width)
    {
        var buffer = new double[frames][];
        for (int f = 0; f < frames; f++)
            buffer[f] = new double[width];
        return buffer;
    }
}
