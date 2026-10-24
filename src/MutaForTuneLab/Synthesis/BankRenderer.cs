using System;
using System.Collections.Generic;
using MutaForTuneLab.Assets;

namespace MutaForTuneLab.Synthesis;

/// <summary>One note as the assembler needs it: on the model's frame grid, already phonetic.</summary>
public readonly record struct RenderNote(int StartFrame, int Frames, int Midi, string Phone, bool Rest = false, string Lyric = "");

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
        IBankSource assets, IReadOnlyList<RenderNote> notes, int totalFrames, int bankPitchCode = 0)
    {
        int mgcOrder = assets.MgcOrder;
        int bapOrder = assets.BapOrder;
        var mgc = NewBuffer(totalFrames, mgcOrder);
        var bap = NewBuffer(totalFrames, bapOrder);
        var silence = new bool[totalFrames];
        Array.Fill(silence, true);

        int consSlots = 0, vowelSlots = 0, missedSlots = 0;
        var state = new NoteAssemblyState();
        string previousPhone = "sil";

        for (int index = 0; index < notes.Count; index++)
        {
            var note = notes[index];
            if (note.StartFrame >= totalFrames)
                break;

            if (note.Rest)
            {
                int frames = Math.Min(note.Frames, totalFrames - note.StartFrame);
                if (frames > 0)
                    BankAssembler.FillRestMgc(mgc, note.StartFrame, frames);
                previousPhone = "sil";
                state = new NoteAssemblyState(PreviousWasRest: true);
                continue;
            }

            var split = LabelBuilder.SplitSyllable(note.Phone);
            string? initial = split?.Initial;
            string final = split?.Final ?? note.Phone;

            int consonantFrames = initial is null ? 0 : Math.Min(TargetFrames(initial), note.Frames);
            int vowelFrames = Math.Max(10, note.Frames - consonantFrames);

            var next = index + 1 < notes.Count ? notes[index + 1] : (RenderNote?)null;
            int nextConsonantFrames = 0;
            string? nextConsonant = null;
            if (next is { } following && !following.Rest)
            {
                var nextSplit = LabelBuilder.SplitSyllable(following.Phone);
                if (nextSplit?.Initial is { } nextInitial)
                {
                    nextConsonantFrames = Math.Min(TargetFrames(nextInitial), following.Frames);
                    nextConsonant = nextInitial;
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
                    plan.TargetCore, PitchFor(note, bankPitchCode), 0);
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
                    BankAssembler.WriteNoteBlock(mgc, bap, block, cursor, resized);
                    consSlots++;
                    MarkVoiced(silence, cursor, cursor + consonantFrames);
                    cursor += consonantFrames;
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

            var vowelItem = BankContext.SelectNoteItem(assets.Groups, final, previousPhone,
                rightContext, vowelPlan.TargetCore, PitchFor(note, bankPitchCode), vowelClass);
            if (vowelItem is not null)
            {
                var (itemMgc, itemBap) = assets.Streams(vowelItem);
                var block = BankAssembler.BuildBlock(itemMgc, itemBap,
                    vowelItem.PrefixFrames, vowelItem.CoreFrames, vowelItem.SuffixFrames,
                    vowelPlan.TargetCore, vowelPlan.LeftExtension, vowelPlan.RightExtension);
                BankAssembler.WriteNoteBlock(mgc, bap, block, cursor, vowelPlan);
                vowelSlots++;
                MarkVoiced(silence, cursor, Math.Min(totalFrames, note.StartFrame + note.Frames));
                state = vowelPlan.NextState;
            }
            else
            {
                missedSlots++;
            }
            previousPhone = final;
        }

        TuneLab.SDK.TuneLabContext.Global.GetLogger().Debug(
            $"[MUTA] bank slots {consSlots} consonant, {vowelSlots} vowel, {missedSlots} missed");
        return new BankResult(mgc, bap, silence);
    }

    /// <summary>The pitch the bank should answer in, honouring the 换采样 setting.</summary>
    static double PitchFor(RenderNote note, int bankPitchCode)
    {
        double hz = 440.0 * Math.Pow(2.0, (note.Midi - 69) / 12.0);
        return bankPitchCode > 0
            ? 440.0 * Math.Pow(2.0, (bankPitchCode - 57) / 12.0)
            : hz;
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
