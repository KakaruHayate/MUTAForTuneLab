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
    const double MaxBlockSeconds = 12.0;
    /// <summary>A gap wider than this starts a new block.</summary>
    const double GapSeconds = 0.4;
    const int MaxNotesPerBlock = 240;
    /// <summary>Silence kept before the first note of a block.</summary>
    const double LeadSeconds = 0.05;
    /// <summary>Silence kept after the last note of a block.</summary>
    const double TailSeconds = 0.15;
    /// <summary>Gaps up to this wide are split down the middle instead of left uncovered.</summary>
    const double TileGapSeconds = 1.0;

    readonly IVoiceSynthesisContext mContext;
    readonly MutaVoice mVoice;
    /// <summary>This part's language. It is a property of the part, not of the voice:
    /// the same bank sings Chinese or Japanese, with a different front end.</summary>
    bool mJapanese;
    readonly string? mR2Directory;
    readonly string mMelModelPath;
    readonly PinyinDictionary? mDictionary;
    /// <summary>The phones this voice can actually sing, from its bank index; null for Japanese.</summary>
    readonly HashSet<string>? mPhones;
    /// <summary>The vowel sung in place of a lyric the voice cannot pronounce.</summary>
    readonly string mLyricFallback = "a";
    /// <summary>The kana table ships with the package, so it is available for any voice; the
    /// pinyin tables live in the MUTA install beside the bank.</summary>
    readonly KanaTable? mKana;
    ILyricFrontend mFrontend;

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
    /// <summary>Blocks whose last render failed, with the signature it failed on and the reason.
    /// A matching signature means "do not retry": the host would otherwise ask forever.</summary>
    readonly ConcurrentDictionary<string, (string Signature, string Message)> mFailed = new(StringComparer.Ordinal);
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
        mSessionId = Interlocked.Increment(ref sNextSessionId);
        TuneLabContext.Global.GetLogger().Debug($"[MUTA] session #{mSessionId} created for {voice.Id}");
        mR2Directory = r2Directory;
        mMelModelPath = melModelPath;
        // Language is a part property, and Chinese is the default. It decides which tables
        // matter, not which tables happened to load: a Japanese part on a Chinese bank is
        // the whole point of the cross-lingual mode.
        mJapanese = ResolveJapanese();
        // The kana table ships with the package, so it is available for any voice; the
        // pinyin tables live in the MUTA install beside the bank.
        mKana = KanaTable.TryLoad(System.IO.Path.Combine(Native.NativeLibraries.PackageDirectory, "sinsy", "dic"));
        mDictionary = PinyinDictionary.TryLoadForVoice(voice.Directory);

        // The whitelist a lyric has to satisfy: the phone names the bank index records, which
        // are exactly the contexts this voice has. Without it a lyric the model cannot place
        // reaches the native engine, and that does not fail politely. Japanese is checked
        // against its own kana table instead, so it does not take this path.
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

        mFrontend = CreateFrontend();

        // A block re-renders when its notes change. These make it re-render when a curve or
        // a part property changes too, because those change the audio just as much.
        foreach (var pair in context.Automations)
        {
            if (pair.Value is null)
                continue;
            // Only the blocks the edited span touches are invalidated. Bumping one global
            // version here made an edit in bar 1 re-render the whole song.
            pair.Value.RangeModified.Subscribe((start, end) => BumpCurve(start, end));
        }

        // Part properties change the render as well, and the host reports those per field.
        var settings = context.PartProperties;
        if (settings is not null)
        {
            settings.StringField(Declarations.KeyVocoderMode, "worldline").Modified.Subscribe(() => mGlobalVersion++);
            settings.StringField(Declarations.KeyLanguage, "zh").Modified.Subscribe(() =>
            {
                mJapanese = ResolveJapanese();
                mFrontend = CreateFrontend();
                mGlobalVersion++;
            });
            settings.StringField(Declarations.KeyBankPitchMode, "auto").Modified.Subscribe(() => mGlobalVersion++);
            settings.DoubleField(Declarations.KeyPhonemeSpeed, 1.0).Modified.Subscribe(() => mGlobalVersion++);
            settings.DoubleField(Declarations.KeyOutputGain, 0.0).Modified.Subscribe(() => mGlobalVersion++);
            settings.DoubleField(Declarations.KeyBankPitchOffset, 0.0).Modified.Subscribe(() => mGlobalVersion++);
            settings.DoubleField(Declarations.KeyBankPitchNote, 0.0).Modified.Subscribe(() => mGlobalVersion++);
        }
    }

    /// <summary>Bumped when a part-wide setting changes; every block is invalidated.</summary>
    int mGlobalVersion;
    /// <summary>Per-block curve versions, bumped only for blocks an automation edit touches.</summary>
    readonly Dictionary<string, int> mCurveVersions = new(StringComparer.Ordinal);
    readonly object mCurveGate = new();

    int CurveVersion(string key)
    {
        lock (mCurveGate)
            return mCurveVersions.TryGetValue(key, out int version) ? version : 0;
    }

    void BumpCurve(double start, double end)
    {
        // The event gives the edited time span; bump only the blocks that overlap it.
        foreach (var block in Plan())
        {
            if (block.EndTime <= start || block.StartTime >= end)
                continue;
            lock (mCurveGate)
                mCurveVersions[block.Key] = CurveVersion(block.Key) + 1;
        }
    }

    static int sNextSessionId;
    readonly int mSessionId;

    /// <summary>Set by Dispose. A render that is already running may finish, but it must
    /// not publish audio into a context the host has already torn down.</summary>
    volatile bool mDisposed;
    readonly CancellationTokenSource mDisposeToken = new();

    /// <summary>Chinese unless the part says Japanese. There is no automatic detection:
    /// lyrics arrive romanised, and "ni" belongs to both languages.</summary>
    bool ResolveJapanese()
    {
        string declared = mContext.PartProperties.StringField(Declarations.KeyLanguage, "zh").Value;
        return declared == "ja";
    }

    /// <summary>The front end follows the language, not the voice: one bank sings both.</summary>
    ILyricFrontend CreateFrontend()
        => mJapanese && mKana is not null
            ? new JapaneseFrontend(mKana, mLyricFallback)
            : new ChineseFrontend(mDictionary, mPhones, mLyricFallback);

    /// <summary>
    /// The user's pinned phonemes for this note, or null when it is not pinned. Both lists
    /// empty means "let the engine decide", which is the only case the G2P runs in.
    /// </summary>
    static PinnedPhones? Pinned(VoiceSynthesisNoteSnapshot note)
    {
        IReadOnlyList<VoiceSynthesisPhonemeSnapshot> leading = note.LeadingPhonemes;
        IReadOnlyList<VoiceSynthesisPhonemeSnapshot> body = note.BodyPhonemes;
        if (leading.Count == 0 && body.Count == 0)
            return null;

        static string[] Symbols(IReadOnlyList<VoiceSynthesisPhonemeSnapshot> phones)
        {
            // The label builder writes these into HTS's fixed 1024-byte stack token buffer.
            foreach (var phone in phones)
                if (phone.Symbol.Length > 128)
                    throw new InvalidOperationException(
                        $"pinned phoneme [{phone.Symbol}] is longer than 128 characters");
            return phones.Select(p => p.Symbol).ToArray();
        }
        static double[] Durations(IReadOnlyList<VoiceSynthesisPhonemeSnapshot> phones)
            => phones.Select(p => p.Duration).ToArray();
        static double[] Weights(IReadOnlyList<VoiceSynthesisPhonemeSnapshot> phones)
            => phones.Select(p => p.StretchWeight).ToArray();

        return new PinnedPhones(
            Symbols(leading), Durations(leading), Weights(leading),
            Symbols(body), Durations(body), Weights(body),
            note.BodyOffset);
    }

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
            Prune(blocks);
            if (blocks.Count == 0)
                return [];

            var entries = new List<SynthesisStatusSegment>(blocks.Count);
            foreach (var block in blocks)
            {
                var status = SynthesisSegmentStatus.Pending;
                double progress = 0.0;
                string? message = null;
                if (mFailed.TryGetValue(block.Key, out var failure) && failure.Signature == block.Signature)
                {
                    status = SynthesisSegmentStatus.Failed;
                    message = failure.Message;
                }
                else if (mProducts.TryGetValue(block.Key, out var product) && product.Signature == block.Signature)
                    status = SynthesisSegmentStatus.Synthesized;
                else if (mRendering.ContainsKey(block.Key))
                    status = SynthesisSegmentStatus.Synthesizing;

                entries.Add(new SynthesisStatusSegment
                {
                    StartTime = block.StartTime,
                    EndTime = block.EndTime,
                    Status = status,
                    Message = message,
                    Progress = progress,
                });
            }
            return entries;
        }
    }

    public SynthesisRange? GetNextPendingSynthesisRange(double startTime, double endTime)
    {
        var planned = Plan();
        Prune(planned);
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
        if (mDisposed)
            return;

        // Same pure plan as the peek, so the same block comes out of it.
        var planned = Plan();
        Prune(planned);
        var block = planned.FirstOrDefault(b =>
            IsDirty(b) && b.EndTime > startTime && b.StartTime < endTime);
        if (block is null)
            return;

        var notes = block.Notes;
        var snapshot = mContext.GetSnapshot(notes);

        // Freeze the continuation decision with the snapshot: the worker must not touch
        // live notes, and the decision domain is live data.
        // Resolved once, on the data thread: the worker gets the result, not the live lyric.
        var syllables = new SungSyllable[notes.Count];
        var lyrics = new string[notes.Count];
        var continuation = new bool[notes.Count];
        var breath = new bool[notes.Count];
        for (int i = 0; i < notes.Count; i++)
        {
            // The note's own override wins over the text the host romanised for us. It is the
            // only way to fix a polyphone the host's G2P chose differently (地 read as "de"
            // in 原地), and the property has been declared in the note panel for a while.
            string declared = notes[i].Lyric.Value;
            string? overrideText = snapshot.Notes[i].Properties.GetString(Declarations.KeyPronunciation, "");
            lyrics[i] = string.IsNullOrWhiteSpace(overrideText) ? declared : overrideText!;
            bool pinned = snapshot.Notes[i].LeadingPhonemes.Count > 0
                          || snapshot.Notes[i].BodyPhonemes.Count > 0;

            // A breath is a manual marker (br, or sp as a synonym). It is not a syllable: the
            // model gets a pause and the breath sample is mixed in afterwards.
            breath[i] = !pinned && IsBreathLyric(lyrics[i]);
            if (breath[i])
            {
                syllables[i] = SungSyllable.Rest;
                continue;
            }

            // The editor writes "-" for a pure continuation and "+" for the next syllable of
            // the same word. Both inherit the syllable of the note the chain leads back to,
            // which is what makes them transparent in both the layout and here. The head has
            // to sit in this block: splitting a chain would leave a block whose first note is
            // a passenger with nothing to continue, so such a note becomes content instead.
            bool chainHeadHere = IsContinuationMark(lyrics[i]) && !pinned
                && ContentHead(notes[i]) is { } head
                && head.StartTime.Value >= block.StartTime - 1e-6;
            continuation[i] = chainHeadHere;
            syllables[i] = chainHeadHere
                ? ContinueFrom(notes[i])
                : mFrontend.Analyze(lyrics[i]);
        }

        mRendering[block.Key] = 0;
        mStatusChanged.Invoke();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellation, mDisposeToken.Token);
            var result = await Task.Run(
                () => Render(snapshot, syllables, lyrics, continuation, breath, block, linked.Token),
                CancellationToken.None);
            // The host disposes the session (and its context) on a voice switch, often
            // while a render is still running. Publishing into that dead context is how a
            // stale render corrupts the host's state -- the crash then surfaces later.
            if (result is null || mDisposed || linked.IsCancellationRequested)
                return;   // cancelled: keep the previous product, say nothing

            // A failed block keeps no audio: the host shows the reason on the status band and
            // retries when the data changes, instead of playing audio that does not match.
            if (result.Error is { } error)
            {
                mFailed[block.Key] = (block.Signature, error);
                TuneLabContext.Global.GetLogger().Warning(
                    $"[MUTA] block {block.StartTime:F3}..{block.EndTime:F3}s failed: {error}");
                mStatusChanged.Invoke();
                return;
            }
            mFailed.TryRemove(block.Key, out _);

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
    /// <summary>Both marks mean "sing the head note's syllable and hold it": the host has no word segmentation to hand us for "+", so it inherits like "-".</summary>
    static bool IsContinuationMark(string lyric) => lyric is ContinuationMark or "+";
    const string ContinuationMark = "-";

    /// <summary>A manual breath: "br", with "sp" accepted as the familiar synonym.</summary>
    static bool IsBreathLyric(string lyric)
    {
        string text = lyric.Trim();
        return text.Equals("br", StringComparison.OrdinalIgnoreCase)
            || text.Equals("sp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The content note a chain of "-" notes leads back to, or null when the chain is
    /// orphaned: a gap breaks it, and so does running out of notes. A pinned note is content
    /// by definition, so it is a legal chain head too.
    /// </summary>
    static IVoiceSynthesisNote? ContentHead(IVoiceSynthesisNote note)
    {
        double start = note.StartTime.Value;
        var previous = note.Previous;
        while (previous is not null)
        {
            // The previous note has to reach this one; a gap ends the phrase.
            if (previous.EndTime.Value < start - 1e-3)
                return null;
            if (!IsContinuationMark(previous.Lyric.Value))
                return previous;
            start = previous.StartTime.Value;
            previous = previous.Previous;
        }
        return null;
    }

    public bool IsContinuation(IVoiceSynthesisNote note)
    {
        if (!IsContinuationMark(note.Lyric.Value))
            return false;
        // Pinned phonemes are content the user wrote, so such a note is never a passenger.
        if (note.LeadingPhonemes.Value.Count > 0 || note.BodyPhonemes.Value.Count > 0)
            return false;
        return ContentHead(note) is not null;
    }

    /// <summary>The syllable a "-" note sings: the head note's syllable, held.</summary>
    SungSyllable ContinueFrom(IVoiceSynthesisNote note)
    {
        var head = ContentHead(note);
        return head is null
            ? mFrontend.Analyze("a")          // orphan: a bare vowel rather than silence
            : Continue(mFrontend.Analyze(head.Lyric.Value));
    }

    /// <summary>The syllable a continuation of <paramref name="previous"/> sings.</summary>
    SungSyllable Continue(SungSyllable previous)
    {
        if (mJapanese)
            return mFrontend.Analyze("ー");   // long vowel: the previous mora's vowel

        // Keep the head note's own syllable: the labels then emit exactly the phones the
        // alignment check expects. Only the bank onset is dropped, so the recording
        // continues the vowel instead of re-articulating the consonant.
        return previous with { BankInitial = null };
    }

    public void Dispose()
    {
        // Stop taking new renders, then let the ones in flight run out.
        //
        // The host disposes the session -- and immediately afterwards the synthesis context
        // -- as part of switching voices. A block render can take several seconds, so the
        // old 3 s wait regularly expired first: the render then finished and published an
        // audio segment into a context that no longer existed. That kind of use-after-free
        // does not fail where it happens; it corrupts the host heap and the next native call
        // (HTS) dies with an access violation. Cancel and actually wait for the workers.
        mDisposed = true;
        TuneLabContext.Global.GetLogger().Debug(
            $"[MUTA] session #{mSessionId} disposing ({mRendering.Count} render(s) in flight)");
        mDisposeToken.Cancel();
        // A 60 s block can take tens of seconds to cancel and unwind; 10 s routinely expired
        // first, which is exactly the use-after-unload case this wait exists to prevent.
        for (int waited = 0; waited < 60000 && !mRendering.IsEmpty; waited += 25)
            Thread.Sleep(25);
        if (!mRendering.IsEmpty)
            TuneLabContext.Global.GetLogger().Warning(
                $"[MUTA] session disposed with {mRendering.Count} render(s) still running");

        foreach (var product in mProducts.Values)
            product.Segment?.Dispose();
        mProducts.Clear();
        mDisposeToken.Dispose();
    }

    // ---- planning ----------------------------------------------------------

    /// <summary>
    /// Splits the part into blocks. Pure: same notes, same blocks, in the same order.
    /// </summary>
    /// <summary>
    /// Drops products for blocks that no longer exist. Editing lyrics or drag-adding notes can
    /// regroup a block (its key is the first note id + count), and without this the old audio
    /// segments stay alive for the life of the part -- hundreds of MB over a long editing
    /// session, which is when the native allocator starts failing.
    /// </summary>
    void Prune(IReadOnlyList<Block> blocks)
    {
        var live = new HashSet<string>(blocks.Select(block => block.Key), StringComparer.Ordinal);
        foreach (var pair in mProducts)
        {
            if (live.Contains(pair.Key) || !mProducts.TryRemove(pair.Key, out var stale))
                continue;
            stale.Segment?.Dispose();
        }
        foreach (var pair in mFailed)
        {
            if (!live.Contains(pair.Key))
                mFailed.TryRemove(pair.Key, out _);
        }
    }

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
                if (nextStart - end > GapSeconds)
                    break;
                // A block must not begin with a melisma passenger: its head would be in the
                // previous block and the held vowel would have nothing to continue. Length
                // and count limits therefore wait for the chain to end.
                if ((nextStart - start > MaxBlockSeconds || members.Count >= MaxNotesPerBlock)
                    && !IsContinuation(next))
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
            string key = KeyOf(members);
            blocks.Add(new Block
            {
                Key = key,
                Signature = SignatureOf(members, key),
                StartTime = start,
                EndTime = Math.Max(start + 0.02, end),
                Notes = members,
            });
        }
        return blocks;
    }

    bool IsDirty(Block block)
    {
        // A block that already failed on this exact data stays failed: the host peeks for the
        // next dirty block and would otherwise be handed the same one forever.
        if (mFailed.TryGetValue(block.Key, out var failure) && failure.Signature == block.Signature)
            return false;
        return !mProducts.TryGetValue(block.Key, out var product) || product.Signature != block.Signature;
    }

    static string KeyOf(IReadOnlyList<IVoiceSynthesisNote> notes)
        => $"{(notes.Count > 0 ? notes[0].Id : "")}:{notes.Count}";

    /// <summary>What the block's audio depends on. Any change here invalidates it.</summary>
    string SignatureOf(IReadOnlyList<IVoiceSynthesisNote> notes, string key)
    {
        var builder = new System.Text.StringBuilder();

        // Global settings invalidate everything; an automation edit only bumps the blocks
        // its RangeModified span touched (see BumpCurve).
        builder.Append(mGlobalVersion).Append(':').Append(CurveVersion(key)).Append(';');

        foreach (var note in notes)
        {
            builder.Append(note.Id).Append('|')
                   .Append(note.StartTime.Value.ToString("F6", CultureInfo.InvariantCulture)).Append('|')
                   .Append(note.EndTime.Value.ToString("F6", CultureInfo.InvariantCulture)).Append('|')
                   .Append(note.Pitch.Value).Append('|')
                   .Append(note.Lyric.Value);
            // Dragging a phoneme boundary locks the phonemes and changes only these lists.
            // Without them in the signature the block stays "not dirty" and the host never
            // asks for a re-render at all.
            static void AppendPhonemes(System.Text.StringBuilder target,
                IReadOnlyList<SynthesizedPhoneme>? phonemes)
            {
                if (phonemes is null)
                {
                    target.Append('~');
                    return;
                }
                foreach (var phoneme in phonemes)
                    target.Append('/').Append(phoneme.Symbol).Append(':')
                          .Append(phoneme.Duration.ToString("F6", CultureInfo.InvariantCulture)).Append(':')
                          .Append(phoneme.StretchWeight.ToString("F6", CultureInfo.InvariantCulture));
            }
            AppendPhonemes(builder, note.LeadingPhonemes.Value);
            AppendPhonemes(builder, note.BodyPhonemes.Value);
            builder.Append('@').Append(note.BodyOffset.Value.ToString("F6", CultureInfo.InvariantCulture))
                   .Append(';');
        }
        return builder.ToString();
    }

    // ---- rendering ---------------------------------------------------------

    sealed record BlockRender(
        float[] Audio,
        IReadOnlyMap<string, SynthesizedSyllable> Phonemes,
        double[] FramePitch,
        double StartTime,
        string? Error = null);

    BlockRender? Render(VoiceSynthesisSnapshot snapshot, SungSyllable[] syllables, string[] lyrics,
        bool[] continuation, bool[] breath, Block block, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
            return null;

        double frameSeconds = MutaRenderer.FrameSeconds;
        if (snapshot.Notes.Count != block.Notes.Count)
            TuneLabContext.Global.GetLogger().Warning(
                $"[MUTA] snapshot has {snapshot.Notes.Count} notes but block has {block.Notes.Count}");
        var notes = new List<RenderNote>(block.Notes.Count);
        for (int i = 0; i < block.Notes.Count; i++)
        {
            var note = block.Notes[i];
            int startFrame = (int)Math.Round((note.StartTime.Value - block.StartTime) / frameSeconds);
            // A note shorter than a couple of frames (the editor lets one be dragged to a tick)
            // would be written into a single frame and effectively disappear; give it enough
            // room to be a short blip instead.
            int frames = Math.Max(3, (int)Math.Round((note.EndTime.Value - note.StartTime.Value) / frameSeconds));
            var syllable = syllables[i];
            var pinned = Pinned(snapshot.Notes[i]);
            if (pinned is not null)
            {
                static string D(double[] values) => string.Join(",",
                    values.Select(v => v.ToString("F3", CultureInfo.InvariantCulture)));
                TuneLabContext.Global.GetLogger().Debug(
                    $"[MUTA] pin lead=[{string.Join(",", pinned.Leading)}] ld=[{D(pinned.LeadingDurations)}] " +
                    $"lw=[{D(pinned.LeadingWeights)}] body=[{string.Join(",", pinned.Body)}] " +
                    $"bd=[{D(pinned.BodyDurations)}] bw=[{D(pinned.BodyWeights)}] " +
                    $"off={pinned.BodyOffset:F3} noteFrames={frames}");
            }
            string? bankInitial = syllable.BankInitial;
            string bankFinal = syllable.BankFinal;
            if (pinned is not null)
            {
                // A pinned note sings what the user pinned. Its symbols are the engine's own
                // phone names, so the bank pair comes straight from them; Japanese maps them
                // onto the bank's names, and Chinese can also rebuild the pinyin syllable so
                // the labels follow the pin rather than the lyric.
                string? onset = pinned.Onset;
                string? nucleus = pinned.Nucleus;
                if (mJapanese)
                {
                    // The pinned leading list is the whole onset: a youon arrives as "k"+"y".
                    string bankOnset = string.Concat(pinned.Leading);
                    if (JapanesePhones.TryMap(bankOnset, nucleus ?? string.Empty, out var mappedOnset, out var mappedFinal))
                    {
                        bankInitial = mappedOnset;
                        bankFinal = mappedFinal;
                    }
                    // Kana cannot be guessed from phones in general, but a pinned mora is
                    // exactly one table entry, so the reverse lookup can rebuild the lyric
                    // and the Sinsy labels then follow the pin.
                    if (mKana is not null && nucleus is not null)
                    {
                        if (mKana.KanaForPhones(pinned.Leading, nucleus) is { } kana)
                            syllable = syllable with { LabelText = kana };
                    }
                }
                else
                {
                    // The body is a vowel plus optional coda; the bank knows the joined form
                    // ("a"+"n" is the final "an"), so join before selecting.
                    string joinedBody = string.Concat(pinned.Body);
                    if (onset is not null)
                        bankInitial = onset;
                    if (joinedBody.Length > 0)
                        bankFinal = joinedBody;
                    if (joinedBody.Length > 0 && pinned.Leading.Length <= 1)
                    {
                        bool zero = onset is "ga" or "go" or "ge" or "ger";
                        string rebuilt = zero ? joinedBody : (onset ?? string.Empty) + joinedBody;
                        if (rebuilt.Length > 0)
                            syllable = syllable with { LabelText = rebuilt };
                    }
                }
            }
            bool isBreath = breath[i];
            bool isContinuation = continuation[i];
            notes.Add(new RenderNote(startFrame, frames, note.Pitch.Value,
                isBreath ? "sp" : syllable.LabelText, isBreath, lyrics[i],
                isBreath ? null : syllable.ModelInitial,
                isBreath ? "sil" : syllable.ModelFinal,
                bankInitial, bankFinal, syllable.BankLeft,
                isBreath || isContinuation ? 0 : syllable.ModelPhones.Count, pinned,
                isContinuation, isBreath));
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
            Japanese = mJapanese,
            // The host's own pitch channels: absolute (NaN = free, use the note) plus the
            // additive deviation that carries vibrato. Sampled here, on the data thread.
            PitchCurve = Evaluate(snapshot.Pitch, positions),
            PitchDeviation = Evaluate(snapshot.PitchDeviation, positions),
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
            // A failing block is surfaced, not swallowed: the status band shows why, and the
            // previous audio (if any) stays as it was until the data changes.
            return new BlockRender([], new Map<string, SynthesizedSyllable>(), [], block.StartTime,
                $"{error.GetType().Name}: {error.Message}");
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

            // A continuation owns no phoneme at all. Reporting the head's syllable again here
            // is what used to make the editor show (and the bank sing) a second attack.
            if (item.Initial is null && item.BodyFrames == 0 && item.Final is "sil" or "sp")
            {
                map.Add(notes[i].Id, new SynthesizedSyllable(
                    Array.Empty<SynthesizedPhoneme>(), Array.Empty<SynthesizedPhoneme>(), 0.0));
                continue;
            }

            // A pinned note reports its own lists, however many phonemes each side has.
            if (item.PinnedLeading is { } pinnedLeading && item.PinnedBody is { } pinnedBody)
            {
                foreach (var phone in pinnedLeading)
                    leading.Add(new SynthesizedPhoneme
                    {
                        Symbol = phone.Symbol,
                        Duration = phone.Frames * MutaRenderer.FrameSeconds,
                        StretchWeight = phone.Weight,
                    });
                foreach (var phone in pinnedBody)
                    body.Add(new SynthesizedPhoneme
                    {
                        Symbol = phone.Symbol,
                        Duration = phone.Frames * MutaRenderer.FrameSeconds,
                        StretchWeight = phone.Weight,
                    });
                map.Add(notes[i].Id, new SynthesizedSyllable(leading, body, item.BodyOffset));
                continue;
            }
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

    /// <summary>Same for a pitch channel, which is a snapshot of its own rather than a named track.</summary>
    static double[]? Evaluate(SynthesisAutomationSnapshot? automation, double[] positions)
    {
        if (automation?.Evaluator is null)
            return null;
        var values = new double[positions.Length];
        automation.Evaluator.Evaluate(positions, values);
        return values;
    }

}
