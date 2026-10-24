using System;
using System.Collections.Generic;
using System.IO;
using MutaForTuneLab.Assets;

namespace MutaForTuneLab.Synthesis;

/// <summary>
/// The recording bank: a flat float file plus the index that says which frames belong to
/// which context.
///
/// The file is not read whole — it is around 94 MB per voice and a song touches a fraction
/// of it. Items are read on demand and cached by identity, so the same syllable in the same
/// context costs one seek per session rather than one per note.
/// </summary>
public sealed class BankAssets
{
    /// <summary>Items kept before the cache is cleared. A song reuses contexts heavily.</summary>
    const int CacheLimit = 512;

    readonly string mBankPath;
    readonly Dictionary<InfItem, (double[][] Mgc, double[][] Bap)> mCache = new();

    public InfFile Info { get; }
    public IReadOnlyDictionary<string, List<InfItem>> Groups { get; }
    public int FrameBytes => (Info.MgcOrder + Info.BapOrder) * 4;

    BankAssets(InfFile info, string bankPath)
    {
        Info = info;
        mBankPath = bankPath;

        var groups = new Dictionary<string, List<InfItem>>(StringComparer.Ordinal);
        foreach (var group in info.Groups)
        {
            if (groups.TryGetValue(group.Name, out var existing))
                existing.AddRange(group.Items);
            else
                groups[group.Name] = new List<InfItem>(group.Items);
        }
        Groups = groups;
    }

    public static BankAssets Load(MutaVoice voice)
    {
        var info = InfFile.Load(voice.InfFile);
        info.ValidateBank(new FileInfo(voice.BankFile).Length);
        return new BankAssets(info, voice.BankFile);
    }

    /// <summary>The MGC and BAP frames of one recorded context.</summary>
    public (double[][] Mgc, double[][] Bap) Streams(InfItem item)
    {
        if (mCache.TryGetValue(item, out var cached))
            return cached;
        if (mCache.Count >= CacheLimit)
            mCache.Clear();

        int frames = item.FrameCount;
        int mgcOrder = Info.MgcOrder;
        int bapOrder = Info.BapOrder;
        int count = frames * (mgcOrder + bapOrder);

        var buffer = new double[count];
        using (var stream = new FileStream(mBankPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stream.Seek(item.BankOffset, SeekOrigin.Begin);
            var bytes = new byte[count * 4];
            int read = 0;
            while (read < bytes.Length)
            {
                int got = stream.Read(bytes, read, bytes.Length - read);
                if (got <= 0)
                    throw new InvalidDataException($"bank ends inside item {item.Current}");
                read += got;
            }
            // The file is float32; block-copying those bytes into a double[] would
            // reinterpret the bit patterns rather than convert them.
            var floats = new float[count];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            for (int i = 0; i < count; i++)
                buffer[i] = floats[i];
        }

        var mgc = new double[frames][];
        var bap = new double[frames][];
        for (int f = 0; f < frames; f++)
        {
            mgc[f] = new double[mgcOrder];
            Array.Copy(buffer, f * mgcOrder, mgc[f], 0, mgcOrder);
            bap[f] = new double[bapOrder];
            Array.Copy(buffer, frames * mgcOrder + f * bapOrder, bap[f], 0, bapOrder);
        }

        var result = (mgc, bap);
        mCache[item] = result;
        return result;
    }
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
        BankAssets assets, IReadOnlyList<RenderNote> notes, int totalFrames, int bankPitchCode = 0)
    {
        int mgcOrder = assets.Info.MgcOrder;
        int bapOrder = assets.Info.BapOrder;
        var mgc = NewBuffer(totalFrames, mgcOrder);
        var bap = NewBuffer(totalFrames, bapOrder);
        var silence = new bool[totalFrames];
        Array.Fill(silence, true);

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
            if (next is { } following && !following.Rest)
            {
                var nextSplit = LabelBuilder.SplitSyllable(following.Phone);
                if (nextSplit?.Initial is { } nextInitial)
                    nextConsonantFrames = Math.Min(TargetFrames(nextInitial), following.Frames);
            }

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
                next is { } after ? after.Phone : "sil",
                vowelPlan.TargetCore, PitchFor(note, bankPitchCode), vowelClass);
            if (vowelItem is not null)
            {
                var (itemMgc, itemBap) = assets.Streams(vowelItem);
                var block = BankAssembler.BuildBlock(itemMgc, itemBap,
                    vowelItem.PrefixFrames, vowelItem.CoreFrames, vowelItem.SuffixFrames,
                    vowelPlan.TargetCore, vowelPlan.LeftExtension, vowelPlan.RightExtension);
                BankAssembler.WriteNoteBlock(mgc, bap, block, cursor, vowelPlan);
                MarkVoiced(silence, cursor, Math.Min(totalFrames, note.StartFrame + note.Frames));
                state = vowelPlan.NextState;
            }
            previousPhone = final;
        }

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
