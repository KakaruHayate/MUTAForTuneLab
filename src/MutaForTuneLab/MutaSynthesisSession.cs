using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MutaForTuneLab.Assets;
using MutaForTuneLab.Synthesis;
using TuneLab.Foundation;
using TuneLab.SDK;

namespace MutaForTuneLab;

/// <summary>
/// One session per part.
///
/// The host drives synthesis step by step: it peeks for the next dirty block, then commits
/// that block with the same window. Chunking therefore has to be a pure function of the
/// part -- the peek and the commit run at different moments and must agree -- which is why
/// the plan is rebuilt from the live notes on every call and blocks are identified by what
/// they contain rather than by list position.
///
/// A block is dirty when no audio was produced for it yet, or when the notes it covers
/// changed since. That covers edits, and it is the whole invalidation story for now: the
/// automation curves are read inside the render, so a curve edit marks nothing. Those need
/// a signature over the sampled values in the same way the notes get one.
/// </summary>
public sealed class MutaSynthesisSession : IVoiceSynthesisSession
{
    /// <summary>Longest block, in seconds. Short enough that one block renders quickly.</summary>
    const double MaxBlockSeconds = 1.2;
    /// <summary>A gap wider than this starts a new block.</summary>
    const double GapSeconds = 0.4;
    const int MaxNotesPerBlock = 12;

    readonly IVoiceSynthesisContext mContext;
    readonly MutaVoice mVoice;
    readonly string? mR2Directory;
    readonly string mMelModelPath;
    readonly PinyinDictionary? mDictionary;
    readonly KanaTable? mKana;

    readonly ActionEvent mStatusChanged = new();
    readonly ActionEvent mPhonemesChanged = new();
    readonly ActionEvent mPitchChanged = new();
    readonly ActionEvent mParametersChanged = new();

    readonly Dictionary<string, Product> mProducts = new(StringComparer.Ordinal);
    readonly HashSet<string> mRendering = new(StringComparer.Ordinal);
    IReadOnlyMap<string, SynthesizedSyllable> mPhonemes = new Map<string, SynthesizedSyllable>();

    sealed class Product
    {
        public required string Signature { get; init; }
        public IAudioSegment? Segment { get; init; }
        public required IReadOnlyMap<string, SynthesizedSyllable> Phonemes { get; init; }
        public required double StartTime { get; init; }
        public required double EndTime { get; init; }
    }

    sealed class Block
    {
        public required string Key { get; init; }
        public required string Signature { get; init; }
        public required double StartTime { get; init; }
        public required double EndTime { get; init; }
        public required IReadOnlyList<IVoiceSynthesisNote> Notes { get; init; }
    }

    public MutaSynthesisSession(IVoiceSynthesisContext context, MutaVoice voice,
        string? r2Directory = null, string melModelPath = "")
    {
        mContext = context;
        mVoice = voice;
        TuneLabContext.Global.GetLogger().Debug($"[MUTA] session created for {voice.Id}");
        mR2Directory = r2Directory;
        mMelModelPath = melModelPath;
        // Lyrics are pinyin for the Chinese chain; the tables live in the MUTA install.
        mDictionary = PinyinDictionary.TryLoadForVoice(voice.Directory);
        // Japanese morae come from the table shipped beside the Sinsy dictionaries.
        mKana = KanaTable.TryLoad(System.IO.Path.Combine(
            Native.NativeLibraries.PackageDirectory, "sinsy", "dic"));

        // A block re-renders when its notes change. These make it re-render when a curve or
        // a part property changes too, because those change the audio just as much.
        foreach (var pair in context.Automations)
        {
            if (pair.Value is null)
                continue;
            pair.Value.RangeModified.Subscribe((_, _) => mCurveVersion++);
        }

        // Part properties change the render as well, and the host reports those per field.
        var settings = context.PartProperties;
        if (settings is not null)
        {
            settings.StringField(Declarations.KeyVocoderMode, "worldline").Modified.Subscribe(() => mCurveVersion++);
            settings.StringField(Declarations.KeyBankPitchMode, "auto").Modified.Subscribe(() => mCurveVersion++);
            settings.DoubleField(Declarations.KeyPhonemeSpeed, 1.0).Modified.Subscribe(() => mCurveVersion++);
            settings.DoubleField(Declarations.KeyOutputGain, 0.0).Modified.Subscribe(() => mCurveVersion++);
            settings.DoubleField(Declarations.KeyBankPitchOffset, 0.0).Modified.Subscribe(() => mCurveVersion++);
            settings.DoubleField(Declarations.KeyBankPitchNote, 0.0).Modified.Subscribe(() => mCurveVersion++);
        }
    }

    /// <summary>Bumped when something other than the notes changes what the render produces.</summary>
    int mCurveVersion;

    public string DefaultLyric => "a";

    public IActionEvent StatusChanged => mStatusChanged;
    public IActionEvent SynthesizedPhonemesChanged => mPhonemesChanged;
    public IActionEvent SynthesizedPitchChanged => mPitchChanged;
    public IActionEvent SynthesizedParametersChanged => mParametersChanged;

    public IReadOnlyMap<string, SynthesizedSyllable> SynthesizedPhonemes => mPhonemes;

    public SynthesizedPitch SynthesizedPitch { get; } = new() { Segments = [] };

    public IReadOnlyMap<string, SynthesizedParameter> SynthesizedParameters { get; } =
        new Map<string, SynthesizedParameter>();

    /// <summary>One status entry per block, in time order.</summary>
    public IReadOnlyList<SynthesisStatusSegment> Status
    {
        get
        {
            var blocks = Plan();
            if (blocks.Count == 0)
                return [];

            var entries = new List<SynthesisStatusSegment>(blocks.Count);
            foreach (var block in blocks)
            {
                var status = SynthesisSegmentStatus.Pending;
                double progress = 0.0;
                if (mProducts.TryGetValue(block.Key, out var product) && product.Signature == block.Signature)
                    status = SynthesisSegmentStatus.Synthesized;
                else if (mRendering.Contains(block.Key))
                    status = SynthesisSegmentStatus.Synthesizing;

                entries.Add(new SynthesisStatusSegment
                {
                    StartTime = block.StartTime,
                    EndTime = block.EndTime,
                    Status = status,
                    Progress = progress,
                });
            }
            return entries;
        }
    }

    public SynthesisRange? GetNextPendingSynthesisRange(double startTime, double endTime)
    {
        var planned = Plan();
        foreach (var block in planned)
        {
            if (!IsDirty(block))
                continue;
            if (block.EndTime <= startTime || block.StartTime >= endTime)
                continue;
            return new SynthesisRange(block.StartTime, block.EndTime);
        }
        return null;
    }

    public async Task SynthesizeNext(double startTime, double endTime, CancellationToken cancellation = default)
    {
        // Same pure plan as the peek, so the same block comes out of it.
        var block = Plan().FirstOrDefault(b =>
            IsDirty(b) && b.EndTime > startTime && b.StartTime < endTime);
        if (block is null)
            return;

        var notes = block.Notes;
        var snapshot = mContext.GetSnapshot(notes);

        // Freeze the continuation decision with the snapshot: the worker must not touch
        // live notes, and the decision domain is live data.
        var phone = new string[notes.Count];
        var lyrics = new string[notes.Count];
        for (int i = 0; i < notes.Count; i++)
        {
            phone[i] = PhoneFor(notes[i].Lyric.Value);
            lyrics[i] = notes[i].Lyric.Value;
        }

        mRendering.Add(block.Key);
        mStatusChanged.Invoke();
        try
        {
            var result = await Task.Run(
                () => Render(snapshot, phone, lyrics, block, cancellation), CancellationToken.None);
            if (result is null)
                return;   // cancelled: keep the previous product, say nothing

            // Back on the data thread: swap the product in.
            if (mProducts.TryGetValue(block.Key, out var previous))
                previous.Segment?.Dispose();

            int rate = MutaRenderer.SampleRate;
            long offset = (long)Math.Round(block.StartTime * rate);
            var segment = mContext.CreateAudioSegment(offset, result.Audio.Length, rate);
            if (result.Audio.Length > 0)
                segment.Write(0, result.Audio);
            segment.Commit();

            mProducts[block.Key] = new Product
            {
                Signature = block.Signature,
                Segment = segment,
                Phonemes = result.Phonemes,
                StartTime = block.StartTime,
                EndTime = block.EndTime,
            };
            double peak = 0.0, energy = 0.0;
            int peakAt = -1;
            for (int i = 0; i < result.Audio.Length; i++)
            {
                double magnitude = Math.Abs(result.Audio[i]);
                if (magnitude > peak) { peak = magnitude; peakAt = i; }
                energy += result.Audio[i] * result.Audio[i];
            }
            double rms = result.Audio.Length > 0 ? Math.Sqrt(energy / result.Audio.Length) : 0.0;
            TuneLabContext.Global.GetLogger().Debug(
                $"[MUTA] rendered {block.StartTime:F3}..{block.EndTime:F3}s: {result.Audio.Length} samples " +
                $"peak {peak:F4} at {peakAt} rms {rms:F4}");
            mPhonemes = MergePhonemes();
            mStatusChanged.Invoke();
            mPhonemesChanged.Invoke();
        }
        finally
        {
            mRendering.Remove(block.Key);
        }
    }

    /// <summary>No melisma: every note sings its own words.</summary>
    public bool IsContinuation(IVoiceSynthesisNote note) => false;

    public void Dispose()
    {
        foreach (var product in mProducts.Values)
            product.Segment?.Dispose();
        mProducts.Clear();
    }

    // ---- planning ----------------------------------------------------------

    /// <summary>
    /// Splits the part into blocks. Pure: same notes, same blocks, in the same order.
    /// </summary>
    List<Block> Plan()
    {
        var notes = mContext.Notes.ToList();
        var blocks = new List<Block>();
        if (notes.Count == 0)
            return blocks;

        int index = 0;
        while (index < notes.Count)
        {
            var first = notes[index];
            double start = first.StartTime.Value;
            var members = new List<IVoiceSynthesisNote> { first };
            double end = first.EndTime.Value;
            index++;

            while (index < notes.Count)
            {
                var next = notes[index];
                double nextStart = next.StartTime.Value;
                if (nextStart - end > GapSeconds || nextStart - start > MaxBlockSeconds
                    || members.Count >= MaxNotesPerBlock)
                    break;
                members.Add(next);
                end = Math.Max(end, next.EndTime.Value);
                index++;
            }

            blocks.Add(new Block
            {
                Key = KeyOf(members),
                Signature = SignatureOf(members),
                StartTime = start,
                EndTime = end,
                Notes = members,
            });
        }
        return blocks;
    }

    bool IsDirty(Block block)
        => !mProducts.TryGetValue(block.Key, out var product) || product.Signature != block.Signature;

    static string KeyOf(IReadOnlyList<IVoiceSynthesisNote> notes)
        => $"{(notes.Count > 0 ? notes[0].Id : "")}:{notes.Count}";

    /// <summary>What the block's audio depends on. Any change here invalidates it.</summary>
    string SignatureOf(IReadOnlyList<IVoiceSynthesisNote> notes)
    {
        var builder = new System.Text.StringBuilder();

        // A bump of the curve version means an automation moved, which changes the audio of
        // every block even though no note did.
        builder.Append(mCurveVersion).Append(';');

        foreach (var note in notes)
        {
            builder.Append(note.Id).Append('|')
                   .Append(note.StartTime.Value.ToString("F6", CultureInfo.InvariantCulture)).Append('|')
                   .Append(note.EndTime.Value.ToString("F6", CultureInfo.InvariantCulture)).Append('|')
                   .Append(note.Pitch.Value).Append('|')
                   .Append(note.Lyric.Value).Append(';');
        }
        return builder.ToString();
    }

    // ---- rendering ---------------------------------------------------------

    sealed record BlockRender(float[] Audio, IReadOnlyMap<string, SynthesizedSyllable> Phonemes);

    BlockRender? Render(VoiceSynthesisSnapshot snapshot, string[] phones, string[] lyrics, Block block, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
            return null;

        double frameSeconds = MutaRenderer.FrameSeconds;
        var notes = new List<RenderNote>(block.Notes.Count);
        for (int i = 0; i < block.Notes.Count; i++)
        {
            var note = block.Notes[i];
            int startFrame = (int)Math.Round((note.StartTime.Value - block.StartTime) / frameSeconds);
            int frames = Math.Max(1, (int)Math.Round((note.EndTime.Value - note.StartTime.Value) / frameSeconds));
            notes.Add(new RenderNote(startFrame, frames, note.Pitch.Value, phones[i], false, lyrics[i]));
        }

        int totalFrames = Math.Max(1, (int)Math.Round((block.EndTime - block.StartTime) / frameSeconds));

        // Curves are read from the snapshot, never from the live view, and evaluated at the
        // frame times of this block.
        var positions = new double[totalFrames];
        for (int i = 0; i < totalFrames; i++)
            positions[i] = block.StartTime + i * frameSeconds;

        var properties = snapshot.PartProperties;
        var request = new RenderRequest
        {
            Voice = mVoice,
            Notes = notes,
            TotalFrames = totalFrames,
            // Where the block sits, so its noise seed is stable but not shared with others.
            BlockStart = block.StartTime,
            Mode = VocoderPipeline.ParseMode(properties.GetString(Declarations.KeyVocoderMode, "worldline")),
            Speed = properties.GetDouble(Declarations.KeyPhonemeSpeed, 1.0),
            GainDecibels = properties.GetDouble(Declarations.KeyOutputGain, 0.0),
            BankPitchMode = properties.GetString(Declarations.KeyBankPitchMode, "auto"),
            BankPitchOffset = properties.GetDouble(Declarations.KeyBankPitchOffset, 0.0),
            BankPitchNote = (int)properties.GetDouble(Declarations.KeyBankPitchNote, 0.0),
            Energy = Curve(snapshot, Declarations.KeyEnergy, positions),
            Timbre = Curve(snapshot, Declarations.KeyTimbre, positions),
            Husky = Curve(snapshot, Declarations.KeyHusky, positions),
            Expression = new VocoderExpression
            {
                Gender = Curve(snapshot, Declarations.KeyGender, positions),
                Tension = Curve(snapshot, Declarations.KeyTension, positions),
                Breathiness = Curve(snapshot, Declarations.KeyBreathiness, positions),
                Voicing = Curve(snapshot, Declarations.KeyVoicing, positions),
            },
            R2Directory = mR2Directory,
            MelModelPath = mMelModelPath,
        };

        RenderResult rendered;
        try
        {
            rendered = MutaRenderer.Render(request, cancellation);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception error)
        {
            // A failing block leaves the previous audio in place; the host shows the block
            // as pending and will ask again.
            TuneLabContext.Global.GetLogger().Warning($"[MUTA] render failed: {error.GetType().Name}: {error.Message}");
            return null;
        }

        if (cancellation.IsCancellationRequested)
            return null;

        return new BlockRender(rendered.Audio, BuildPhonemes(block.Notes, phones));
    }

    /// <summary>
    /// The phoneme product. Consonants are rigid, the vowel stretches: that is the default
    /// the host's layout expects when an engine has no stronger opinion.
    /// </summary>
    static IReadOnlyMap<string, SynthesizedSyllable> BuildPhonemes(
        IReadOnlyList<IVoiceSynthesisNote> notes, string[] phones)
    {
        var map = new Map<string, SynthesizedSyllable>();
        for (int i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            var split = LabelBuilder.SplitSyllable(phones[i]);
            var leading = new List<SynthesizedPhoneme>();
            var body = new List<SynthesizedPhoneme>();
            if (split is { } parts)
            {
                leading.Add(new SynthesizedPhoneme { Symbol = parts.Initial, Duration = 0.06, StretchWeight = 0.0 });
                body.Add(new SynthesizedPhoneme { Symbol = parts.Final, Duration = 0.20, StretchWeight = 1.0 });
            }
            else
            {
                body.Add(new SynthesizedPhoneme { Symbol = phones[i], Duration = 0.20, StretchWeight = 1.0 });
            }
            map.Add(note.Id, new SynthesizedSyllable(leading, body, 0.0));
        }
        return map;
    }

    IReadOnlyMap<string, SynthesizedSyllable> MergePhonemes()
    {
        var merged = new Map<string, SynthesizedSyllable>();
        foreach (var product in mProducts.Values.OrderBy(p => p.StartTime))
            foreach (var pair in product.Phonemes)
                merged.Add(pair.Key, pair.Value);
        return merged;
    }

    /// <summary>
    /// The lyric as a single syllable. Already-phonetic text (pinyin) is taken as written;
    /// Han goes through the install's tables; anything else falls back to a vowel rather
    /// than failing the note.
    /// </summary>
    /// <summary>
    /// One automation curve sampled at the block's frame times, or null when the project
    /// does not carry it. Null and "the curve at its default" render identically, which is
    /// what keeps an untouched project bit-identical to one without the control.
    /// </summary>
    static double[]? Curve(VoiceSynthesisSnapshot snapshot, string key, double[] positions)
    {
        if (!snapshot.Automations.TryGetValue(key, out var automation) || automation?.Evaluator is null)
            return null;
        var values = new double[positions.Length];
        automation.Evaluator.Evaluate(positions, values);
        return values;
    }
    string PhoneFor(string lyric)
    {
        if (string.IsNullOrWhiteSpace(lyric))
            return "a";

        if (lyric.All(c => c < 128))
            return lyric.ToLowerInvariant();

        if (mDictionary?.SyllableFor(lyric) is { } syllable)
            return syllable;

        if (KanaTable.IsKana(lyric) && mKana?.PhonemesFor(lyric) is { } phones)
            return phones;

        return "a";
    }
}
