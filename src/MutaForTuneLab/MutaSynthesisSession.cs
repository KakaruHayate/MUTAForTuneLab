using System;
using System.Collections.Concurrent;
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
    /// <summary>Silence kept before the first note of a block.</summary>
    const double LeadSeconds = 0.05;
    /// <summary>Silence kept after the last note of a block.</summary>
    const double TailSeconds = 0.15;
    /// <summary>Gaps up to this wide are split down the middle instead of left uncovered.</summary>
    const double TileGapSeconds = 1.0;

    readonly IVoiceSynthesisContext mContext;
    readonly MutaVoice mVoice;
    readonly string? mR2Directory;
    readonly string mMelModelPath;
    readonly PinyinDictionary? mDictionary;
    /// <summary>The phones this voice can actually sing, from its bank index; null for Japanese.</summary>
    readonly HashSet<string>? mPhones;
    /// <summary>The vowel sung in place of a lyric the voice cannot pronounce.</summary>
    readonly string mLyricFallback = "a";
    readonly KanaTable? mKana;

    readonly ActionEvent mStatusChanged = new();
    readonly ActionEvent mPhonemesChanged = new();
    readonly ActionEvent mPitchChanged = new();
    readonly ActionEvent mParametersChanged = new();

    // The host reads Status and the products while a render is running on another thread, so
    // these cannot be plain dictionaries: a concurrent read during a resize hands out garbage,
    // and a real project turned that into a native crash. Rendering is serialised inside
    // MutaRenderer; these keep the bookkeeping around it safe.
    readonly ConcurrentDictionary<string, Product> mProducts = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, byte> mRendering = new(StringComparer.Ordinal);
    IReadOnlyMap<string, SynthesizedSyllable> mPhonemes = new Map<string, SynthesizedSyllable>();

    sealed class Product
    {
        public required string Signature { get; init; }
        public IAudioSegment? Segment { get; init; }
        public required IReadOnlyMap<string, SynthesizedSyllable> Phonemes { get; init; }
        /// <summary>Per-frame pitch the render produced, for the readback line.</summary>
        public required double[] FramePitch { get; init; }
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

        // The whitelist a lyric has to satisfy: the phone names the bank index records, which
        // are exactly the contexts this voice has. Without it a lyric the model cannot place
        // reaches the native engine, and that does not fail politely.
        if (voice.HasBank && File.Exists(voice.InfFile))
        {
            try
            {
                var phones = new HashSet<string>(StringComparer.Ordinal);
                foreach (var group in Assets.InfFile.Load(voice.InfFile).Groups)
                    foreach (var item in group.Items)
                        phones.Add(item.Current);
                if (phones.Count > 0)
                {
                    mPhones = phones;
                    foreach (string vowel in new[] { "a", "o", "e", "i", "u" })
                    {
                        if (!phones.Contains(vowel))
                            continue;
                        mLyricFallback = vowel;
                        break;
                    }
                }
            }
            catch (Exception error)
            {
                // A broken index is not worth a session: fall back to the dictionary route.
                TuneLabContext.Global.GetLogger().Warning(
                    $"[MUTA] phone inventory unavailable: {error.GetType().Name}: {error.Message}");
            }
        }

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

    public SynthesizedPitch SynthesizedPitch => mPitch;

    SynthesizedPitch mPitch = new() { Segments = [] };

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
                else if (mRendering.ContainsKey(block.Key))
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

        mRendering[block.Key] = 0;
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
                FramePitch = result.FramePitch,
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
            mPitch = new SynthesizedPitch { Segments = MergePitch() };
            TuneLabContext.Global.GetLogger().Debug(
                $"[MUTA] pitch readback {mPitch.Segments.Count} segments " +
                $"{mPitch.Segments.Sum(s => s.Count)} points");
            mStatusChanged.Invoke();
            mPhonemesChanged.Invoke();
            mPitchChanged.Invoke();
        }
        finally
        {
            mRendering.TryRemove(block.Key, out _);
        }
    }

    /// <summary>No melisma: every note sings its own words.</summary>
    public bool IsContinuation(IVoiceSynthesisNote note) => false;

    public void Dispose()
    {
        // A render runs on the thread pool. If the host gives up and tears the extension
        // down while one is in flight, the next native call lands in an unloaded module and
        // takes the process with it -- which is what a real project did once the sandbox's
        // dispatch budget ran out. Give those renders a moment to finish first.
        for (int waited = 0; waited < 3000 && !mRendering.IsEmpty; waited += 25)
            Thread.Sleep(25);

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

        // First group the notes; then decide where the audio of each group begins and ends.
        var groups = new List<List<IVoiceSynthesisNote>>();
        var noteStarts = new List<double>();
        var noteEnds = new List<double>();

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

            groups.Add(members);
            noteStarts.Add(start);
            noteEnds.Add(end);
        }

        // A block's audio is cut in the middle of the silence that follows it, not against
        // its last frame: a cut against the frame leaves the decay and the gate's ramp with
        // nowhere to go. Between two blocks close enough to share a silence the midpoint is
        // used, so the two meet exactly once; across a long rest each keeps a margin and the
        // rest itself is left to the host as silence.
        for (int i = 0; i < groups.Count; i++)
        {
            double start = i == 0 ? Math.Max(0.0, noteStarts[0] - LeadSeconds) : noteStarts[i] - LeadSeconds;
            double end = noteEnds[i] + TailSeconds;

            if (i > 0)
            {
                double previousEnd = noteEnds[i - 1];
                double gap = noteStarts[i] - previousEnd;
                if (gap <= TileGapSeconds)
                    start = (previousEnd + noteStarts[i]) / 2.0;
            }
            if (i + 1 < groups.Count)
            {
                double nextStart = noteStarts[i + 1];
                double gap = nextStart - noteEnds[i];
                if (gap <= TileGapSeconds)
                    end = (noteEnds[i] + nextStart) / 2.0;
            }

            var members = groups[i];
            blocks.Add(new Block
            {
                Key = KeyOf(members),
                Signature = SignatureOf(members),
                StartTime = start,
                EndTime = Math.Max(start + 0.02, end),
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

    sealed record BlockRender(
        float[] Audio,
        IReadOnlyMap<string, SynthesizedSyllable> Phonemes,
        double[] FramePitch,
        double StartTime);

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

        return new BlockRender(rendered.Audio, BuildPhonemes(block.Notes, rendered.Geometry),
            rendered.FramePitch, block.StartTime);
    }

    /// <summary>
    /// The phoneme product, published with the lengths the layout actually used.
    ///
    /// The consonant is rigid and the vowel stretches, and the body offset is the onset's
    /// own length -- which is what puts the consonant inside the note rather than before it.
    /// </summary>
    static IReadOnlyMap<string, SynthesizedSyllable> BuildPhonemes(
        IReadOnlyList<IVoiceSynthesisNote> notes,
        IReadOnlyList<MutaRenderer.PhonemeGeometry> geometry)
    {
        var map = new Map<string, SynthesizedSyllable>();
        for (int i = 0; i < notes.Count && i < geometry.Count; i++)
        {
            var item = geometry[i];
            var leading = new List<SynthesizedPhoneme>();
            var body = new List<SynthesizedPhoneme>();
            if (item.Initial is { Length: > 0 } initial)
            {
                leading.Add(new SynthesizedPhoneme
                {
                    Symbol = initial,
                    Duration = item.LeadingFrames * MutaRenderer.FrameSeconds,
                    StretchWeight = 0.0,
                });
            }
            body.Add(new SynthesizedPhoneme
            {
                Symbol = item.Final,
                Duration = item.BodyFrames * MutaRenderer.FrameSeconds,
                StretchWeight = 1.0,
            });
            map.Add(notes[i].Id, new SynthesizedSyllable(leading, body, item.BodyOffset));
        }
        return map;
    }

    /// <summary>
    /// Pitch readback for the editor: points are (seconds, semitones), and a rest breaks the
    /// line rather than being drawn as a drop to zero.
    ///
    /// Decimated to one point per 20 ms, which is finer than anyone can draw and a tenth of
    /// the points the frames would produce.
    /// </summary>
    IReadOnlyList<IReadOnlyList<Point>> MergePitch()
    {
        var segments = new List<IReadOnlyList<Point>>();
        int step = Math.Max(1, (int)Math.Round(0.020 / MutaRenderer.FrameSeconds));

        foreach (var product in mProducts.Values.OrderBy(p => p.StartTime))
        {
            List<Point>? current = null;
            for (int f = 0; f < product.FramePitch.Length; f++)
            {
                double hz = product.FramePitch[f];
                bool last = f == product.FramePitch.Length - 1;
                if (hz <= 0.0)
                {
                    if (current is { Count: > 1 })
                        segments.Add(current);
                    current = null;
                    continue;
                }

                if (current is null)
                {
                    current = new List<Point>();
                    if (f > 0 && product.FramePitch[f - 1] > 0.0)
                        f = Math.Max(0, f - 1);   // start the line where voicing starts
                }

                if (f % step != 0 && !last)
                    continue;

                double time = product.StartTime + f * MutaRenderer.FrameSeconds;
                double semitones = 69.0 + 12.0 * Math.Log2(hz / 440.0);
                current.Add(new Point(time, semitones));
            }
            if (current is { Count: > 1 })
                segments.Add(current);
        }
        return segments;
    }
    IReadOnlyMap<string, SynthesizedSyllable> MergePhonemes()    {
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
    /// <summary>
    /// Turns a lyric into the phone the model is asked for, and refuses anything that is not
    /// a phone this voice actually has.
    ///
    /// The native engine does not validate: a phone it cannot place reads past its own arrays
    /// and takes the process down. That is how a real project with a note sung as "h" killed
    /// the host. So the rule here is a whitelist -- dictionary syllable, kana from the table,
    /// or a romaji syllable whose onset and final are both in the voice's inventory -- and
    /// everything else becomes a vowel the voice can sing.
    /// </summary>
    string PhoneFor(string lyric)
    {
        string cleaned = Clean(lyric);
        if (cleaned.Length == 0)
            return FallbackPhone(lyric);

        // Japanese first: its phones come from the kana table, not from an inventory.
        if (mPhones is null && mKana is not null)
        {
            // The table writes a mora as its phones separated by spaces ("k i" for き), and a
            // space inside a phone name makes the label unsingable -- the model takes the
            // process down. Joining them gives ki, kyo: the shapes the splitter expects.
            if (mKana.PhonemesFor(cleaned) is { } fromKana)
                return fromKana.Replace(" ", string.Empty);
            if (cleaned.All(c => c < 128))
            {
                string romaji = new string(cleaned.Where(char.IsLetter).ToArray()).ToLowerInvariant();
                if (romaji.Length > 0 && mKana.KanaFor(romaji) is { } kana
                    && mKana.PhonemesFor(kana) is { } fromRomaji)
                    return fromRomaji.Replace(" ", string.Empty);
            }
            return FallbackPhone(lyric);
        }

        if (mDictionary?.SyllableFor(cleaned) is { } syllable && IsSingable(syllable))
            return syllable;

        string ascii = new string(cleaned.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        if (ascii.Length > 0)
        {
            if (IsSingable(ascii))
                return ascii;

            // A bare consonant has no final; give it the neutral vowel.
            string withVowel = ascii + "a";
            if (IsSingable(withVowel))
            {
                TuneLabContext.Global.GetLogger().Debug(
                    $"[MUTA] lyric [{lyric}] has no vowel, singing [{withVowel}]");
                return withVowel;
            }
        }

        return FallbackPhone(lyric);
    }

    /// <summary>Letters and kana only: spaces, punctuation and digits cannot be sung.</summary>
    static string Clean(string lyric)
    {
        if (string.IsNullOrEmpty(lyric))
            return string.Empty;

        var kept = new System.Text.StringBuilder(lyric.Length);
        foreach (char c in lyric)
        {
            if (char.IsLetter(c))
                kept.Append(c);
        }
        return kept.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Whether the model can be asked for this syllable: both of its parts have to exist in
    /// the voice. With no inventory (the Japanese chain) the kana table has already decided.
    /// </summary>
    bool IsSingable(string phone)
    {
        if (phone.Length == 0)
            return false;
        if (mPhones is null)
            return LabelBuilder.SplitSyllable(phone) is { Final.Length: > 0 };

        if (mPhones.Contains(phone))
            return true;
        if (LabelBuilder.SplitSyllable(phone) is not { } parts)
            return false;
        bool onset = parts.Initial.Length == 0 || mPhones.Contains(parts.Initial);
        return onset && mPhones.Contains(parts.Final);
    }

    /// <summary>The vowel to sing instead, and a note in the log that the lyric was replaced.</summary>
    string FallbackPhone(string lyric)
    {
        TuneLabContext.Global.GetLogger().Debug(
            $"[MUTA] lyric [{lyric}] is not singable by this voice, singing [{mLyricFallback}]");
        return mLyricFallback;
    }
}
