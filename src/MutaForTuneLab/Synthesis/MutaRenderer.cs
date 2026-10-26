using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using MutaForTuneLab.Assets;
using MutaForTuneLab.Native;
using TuneLab.SDK;


namespace MutaForTuneLab.Synthesis;

/// <summary>One note as the renderer needs it: on the model's frame grid, already phonetic.</summary>
/// <summary>Everything one render needs, pinned before the worker starts.</summary>
public sealed class RenderRequest
{
    public required MutaVoice Voice { get; init; }
    public required IReadOnlyList<RenderNote> Notes { get; init; }
    public int TotalFrames { get; init; }
    public double Speed { get; init; } = 1.0;
    /// <summary>Warping factor from the timbre control.</summary>
    public double Alpha { get; init; } = 0.55;
    public VocoderMode Mode { get; init; } = VocoderMode.WorldlineR11;
    public string? R2Directory { get; init; }
    public string MelModelPath { get; init; } = "";
    public VocoderExpression Expression { get; init; } = new();
    public double GainDecibels { get; init; }
    /// <summary>Where this block starts, in seconds; used for the noise seed.</summary>
    public double BlockStart { get; init; }
    /// <summary>True when the part sings Japanese: YOKO's labels and model, bank's timbre.</summary>
    public bool Japanese { get; init; }
    /// <summary>False forces the model's own timbre, for comparing the two paths.</summary>
    public bool UseBank { get; init; } = true;

    /// <summary>Energy in decibels per frame; null leaves the model's own dynamics.</summary>
    public double[]? Energy { get; init; }
    /// <summary>Timbre in -4..4 per frame; null means the default warping factor.</summary>
    public double[]? Timbre { get; init; }
    /// <summary>Husky in decibels per frame; null leaves the model's aperiodicity.</summary>
    public double[]? Husky { get; init; }

    /// <summary>Absolute pitch per frame in semitones; NaN follows the note.</summary>
    public double[]? PitchCurve { get; init; }
    /// <summary>Additive pitch deviation per frame in semitones; null means none.</summary>
    public double[]? PitchDeviation { get; init; }

    /// <summary>Noise seed for this block; 0 derives one from the voice and the block start.</summary>
    public ulong Seed { get; init; }

    /// <summary>"auto" follows each note, "offset" shifts it, "fixed" pins one region.</summary>
    public string BankPitchMode { get; init; } = "auto";
    /// <summary>Semitone shift applied to the pitch the bank is asked for.</summary>
    public double BankPitchOffset { get; init; }
    /// <summary>MIDI note the bank region is pinned to, when the mode is "fixed".</summary>
    public int BankPitchNote { get; init; }
}

public sealed record RenderResult(
    float[] Audio, double[] FramePitch, IReadOnlyList<RenderNote> Notes,
    IReadOnlyList<MutaRenderer.PhonemeGeometry> Geometry);

/// <summary>
/// Runs the chain for one chunk: labels, the acoustic model, the pitch mechanism, the
/// vocoder.
///
/// The bank assembly stage is not in this path yet, so what it renders is the model's own
/// voice rather than a selected recording. That is deliberate: it is the shortest route to
/// real audio through the host, and the bank stage slots in between the model and the
/// vocoder without changing anything around it.
/// </summary>
public static class MutaRenderer
{
    /// <summary>Everything in this chain works on the model's grid.</summary>
    public const int SampleRate = 48000;
    public const double FrameSeconds = 0.005;
    /// <summary>One render block is capped at 60 s; beyond that the block fails with a reason.</summary>
    public const int MaxRenderFrames = 12000;           // 60 s in one piece
    public const int Bins = 1025;
    /// <summary>The log gain the rest of the chain was tuned against.</summary>
    public const double ReferenceLogGain = -4.5;

    /// <summary>
    /// Native synthesis runs one call at a time.
    ///
    /// The HTS wrapper keeps the engine it is feeding in a process-global while it runs, so
    /// two overlapping calls hand the WORLD hooks the wrong engine and corrupt its buffers.
    /// Blocks render in about half their own duration, so serialising them costs nothing.
    /// </summary>
    static readonly object RenderGate = new();

    // The voice file and its indexed bank do not change between blocks. Re-reading 6.8 MB of
    // .voice and 1.4 MB of .inf per block was most of the LOH churn the audit measured.
    static readonly object CacheGate = new();
    static readonly Dictionary<string, (long Length, DateTime Stamp, HtsVoice Voice)> VoiceCache = new();
    static readonly Dictionary<string, (long Inf, long Bank, DateTime InfStamp, DateTime BankStamp, BankAssets Assets)> BankCache = new();

    static HtsVoice VoiceFor(string path)
    {
        var info = new FileInfo(path);
        string key = info.FullName;
        lock (CacheGate)
        {
            if (VoiceCache.TryGetValue(key, out var cached)
                && cached.Length == info.Length && cached.Stamp == info.LastWriteTimeUtc)
                return cached.Voice;
            if (VoiceCache.Count > 4)
                VoiceCache.Clear();
            var voice = HtsVoice.Load(path);
            VoiceCache[key] = (info.Length, info.LastWriteTimeUtc, voice);
            return voice;
        }
    }

    static BankAssets AssetsFor(MutaVoice voice)
    {
        var inf = new FileInfo(voice.InfFile);
        var bank = new FileInfo(voice.BankFile);
        string key = inf.FullName + "|" + bank.FullName;
        lock (CacheGate)
        {
            if (BankCache.TryGetValue(key, out var cached)
                && cached.Inf == inf.Length && cached.Bank == bank.Length
                && cached.InfStamp == inf.LastWriteTimeUtc && cached.BankStamp == bank.LastWriteTimeUtc)
                return cached.Assets;
            if (BankCache.Count > 4)
                BankCache.Clear();
            var assets = BankAssets.Load(voice);
            BankCache[key] = (inf.Length, bank.Length, inf.LastWriteTimeUtc, bank.LastWriteTimeUtc, assets);
            return assets;
        }
    }
    /// <summary>
    /// A process-wide named mutex, in addition to the object lock. The object lock only
    /// serialises one loaded copy of this assembly; if the host ever holds two (an old
    /// session from before a reload plus the new one), both would drive the same native HTS
    /// library at once -- HTS keeps process-global state while parsing labels, and two calls
    /// there corrupt the heap instead of failing politely.
    /// </summary>
    static readonly System.Threading.Mutex RenderMutex =
        new(false, @"Local\MutaForTuneLab.HtsRender");

    public static RenderResult Render(RenderRequest request, CancellationToken cancellation)
    {
        bool taken;
        try
        {
            taken = RenderMutex.WaitOne(TimeSpan.FromMinutes(5));
        }
        catch (AbandonedMutexException)
        {
            taken = true;   // the previous owner died; the native side is ours again
        }
        // Running without the process-wide mutex is exactly the two-assemblies-at-once
        // case it exists for: HTS keeps global state and corrupts its heap instead of
        // failing. A block that cannot get the lock must fail, never proceed unlocked.
        if (!taken)
            throw new TimeoutException("another MUTA render held the native HTS lock for 5 minutes");
        try
        {
            lock (RenderGate)
            {
                return RenderCore(request, cancellation);
            }
        }
        finally
        {
            if (taken)
                RenderMutex.ReleaseMutex();
        }
    }

    static RenderResult RenderCore(RenderRequest request, CancellationToken cancellation)
    {
        var voice = request.Voice;
        var notes = request.Notes;
        if (notes.Count == 0)
            return new RenderResult([], [], notes, []);

        // One block is rendered as a whole spectrum matrix: frames x 1025 doubles at every
        // stage. The audit measured about 17 MB of peak per second of audio, so a note
        // dragged to a minute would ask for a gigabyte in one allocation chain. Fail the
        // block before that rather than letting HTS or worldline run out of memory (both
        // are native and an allocation failure there is an access violation, not an
        // OutOfMemoryException we could catch).
        if (request.TotalFrames > MaxRenderFrames)
            throw new InvalidOperationException(
                $"block is {request.TotalFrames * FrameSeconds:F1} s ({request.TotalFrames} frames); " +
                $"the render limit is {MaxRenderFrames * FrameSeconds:F0} s -- shorten or split the note");
        long estimateBytes = (long)request.TotalFrames * 1025 * sizeof(double) * 12;
        long availableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes
                              - System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
        if (availableBytes > 0 && estimateBytes > availableBytes / 2)
            throw new InvalidOperationException(
                $"block needs about {estimateBytes / 1048576} MB but only " +
                $"{availableBytes / 1048576} MB of the memory budget is free");

        cancellation.ThrowIfCancellationRequested();

        // Two front ends feed the same acoustic model: MUTA's label builder for Chinese,
        // Sinsy's for Japanese. Both end at the same place -- a list of label lines.
        IReadOnlyList<string> labels;
        string enginePath;
        string? japaneseLabels = null;
        if (request.Japanese)
        {
            var scoreNotes = new List<ScoreNote>(notes.Count);
            foreach (var note in notes)
            {
                // A continuation is one held mora, not a second one: Sinsy gets a single
                // longer note, so its vowel is not re-articulated. A breath note is left out
                // on purpose -- the gap becomes Sinsy's rest, and the breath sample is mixed
                // in after the vocoder.
                if (note.Continuation)
                {
                    if (scoreNotes.Count > 0)
                    {
                        var previous = scoreNotes[^1];
                        scoreNotes[^1] = previous with
                        {
                            Duration = previous.Duration + note.Frames * FrameSeconds,
                        };
                    }
                    continue;
                }
                if (note.Breath)
                    continue;
                // The front end already resolved the text: kana for a mora, a vowel only for a
                // fallback. That vowel still has to become kana for Sinsy's dictionary.
                string lyric = KanaTable.IsKana(note.Phone) ? note.Phone : MutaSinsy.ToKana(note.Phone);
                scoreNotes.Add(new ScoreNote(
                    note.StartFrame * FrameSeconds, note.Frames * FrameSeconds, note.Midi, lyric));
            }
            MutaDiagnostics.Log(
                $"[MUTA] jp note0 phone=[{notes[0].Phone}] lyric=[{notes[0].Lyric}] notes={notes.Count}");
            japaneseLabels = MutaSinsy.Labels(scoreNotes, request.TotalFrames * FrameSeconds,
                MutaSinsy.DictionaryDirectory);
            if (japaneseLabels is null)
                throw new InvalidOperationException("the Japanese label builder refused the score");
            labels = japaneseLabels.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            // The model that knows Japanese, whatever bank is singing: this is the substitution
            // that makes a Chinese voice sing Japanese at all.
            enginePath = MutaSinsy.VoiceFile;
        }
        else
        {
            labels = LabelBuilder.Build(BuildScore(notes, request.TotalFrames));
            enginePath = VoiceFor(voice.VoiceFile).EnginePath;
        }

        // A malformed label makes HTS read past whatever it built; check the timestamps here
        // so a bad slot fails the block with a message instead of taking the process down.
        ValidateLabels(labels);
        if (enginePath.Length == 0)
            throw new InvalidOperationException("no acoustic model path for this voice");

        // A fresh engine per block. HTS's synthesis path allocates from its own state and
        // does not check every allocation; reusing one engine across thousands of blocks is
        // what turned a bad allocation into a write through NULL (the voice-switch and
        // tension crashes). Loading a 7 MB model costs far less than rendering the block.
        using var engine = MutaHts.Load(enginePath);
        // Speed is the only knob that can make HTS allocate durations it cannot honour.
        engine.Speed = double.IsFinite(request.Speed)
            ? Math.Clamp(request.Speed, 0.25, 4.0)
            : 1.0;

        cancellation.ThrowIfCancellationRequested();

        // If the native engine ever dies, this line is the last thing written: the exact
        // phones and lyrics it was asked to sing.
        if (notes.Count > 0)
            MutaDiagnostics.Log(
                "[MUTA] synth " + string.Join("|", notes.Select(n => n.Phone + "@" + n.Midi + ":" + n.Lyric + ":" + n.Frames)));
        MutaDiagnostics.Log(
            $"[MUTA] memory {System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / 1048576} MB " +
            $"labels {labels.Count}");

        int frames = engine.Synthesize(labels);
        if (frames <= 0)
            throw new InvalidOperationException("the acoustic model produced no frames");

        // Where each phone really landed, from the state durations: the only timing that
        // agrees with the frame count. Pauses included, so the content window is exact.
        var timeline = PhoneTimeline.Build(labels, engine.StateDurations(), engine.States);
        VerifyAlignment(notes, timeline);
        if (timeline.Count > 0 && timeline[^1].EndFrame != frames)
            MutaDiagnostics.Log(
                $"[MUTA] timeline ends at {timeline[^1].EndFrame} but the model made {frames} frames");
        var phoneFrames = PhoneTimeline.SungDurations(timeline);
        MutaDiagnostics.Log($"[MUTA] model frames {frames} speed {request.Speed:F2} phones {phoneFrames.Count} " +
            $"sum {phoneFrames.Sum()} first {(phoneFrames.Count > 0 ? phoneFrames[0] : 0)}");
        CheckHeap("hts");

        // Where each phoneme actually sits is the host's own layout's business, so that what
        // is drawn and what is heard cannot drift apart.
        var geometry = ResolveGeometry(notes, phoneFrames, request.BlockStart);

        // The consonant does not take the acoustic model's word for its length.
        //
        // The original engine measures every consonant with its own table
        // (MinPhoneDuration, a string -> 100 ns tick table) and caps it at half the note.
        // The model's state durations run 50-80% longer, which is what made a
        // 222 ms note sing 175 ms of "q" and squeeze the vowel into what was left. Using the
        // table value here, in the geometry itself, also means the phoneme band the editor
        // shows and the audio the bank renders are the same numbers -- the bank assembles
        // from this geometry too.
        if (!request.Japanese)
        {
            for (int i = 0; i < geometry.Length && i < notes.Count; i++)
            {
                var note = notes[i];
                if (note.Pinned is not null || note.ModelInitial is null)
                    continue;
                int wanted = LabelBuilder.ConsonantFrames(note.ModelInitial, note.Frames);
                if (wanted <= 0)
                    continue;
                if (wanted != geometry[i].LeadingFrames)
                    geometry[i] = geometry[i] with { LeadingFrames = wanted };
            }
        }

        int mgcWidth = engine.VectorLength(0);
        int bapWidth = engine.VectorLength(2);
        var mgc = new double[frames][];
        var bap = new double[frames][];
        var lf0 = new double[frames];
        ReadStream(engine, 0, mgcWidth, mgc);
        ReadStream(engine, 2, bapWidth, bap);
        ReadStream(engine, 1, engine.VectorLength(1), lf0);

        if (japaneseLabels is not null)
        {
            var content = PhoneTimeline.Content(timeline);
            MutaDiagnostics.Log(
                $"[MUTA] jp: {frames} frames, content " +
                $"{(content is null ? "null" : $"{content.Value.Start}..{content.Value.End}")} " +
                $"phones {timeline.Count} lines {labels.Count}");
        }

        cancellation.ThrowIfCancellationRequested();

        // A voice with no aperiodicity at all -- the Sinsy model is one -- leaves the stream
        // at zero, and zero in this scale means "entirely noise": the vocoder then washes out
        // every harmonic and the voice sounds like a hiss with no pitch in it. Detected here and
        // replaced with a plausible curve: periodic at the bottom, breathier towards the top.
        {
            bool silent = true;
            double biggest = 0.0;
            for (int f = 0; f < bap.Length && silent; f++)
                for (int b = 0; b < bap[f].Length; b++)
                {
                    biggest = Math.Max(biggest, Math.Abs(bap[f][b]));
                    if (biggest > 0.5) { silent = false; break; }
                }

            if (silent && bap.Length > 0)
            {
                int bands = bap[0].Length;
                for (int f = 0; f < bap.Length; f++)
                    for (int b = 0; b < bands; b++)
                        bap[f][b] = -30.0 + 22.0 * b / Math.Max(1, bands - 1);
                MutaDiagnostics.Log($"[MUTA] voice has no aperiodicity, using a default curve ({bands} bands)");
            }
        }
        // What the model says about voicing: mean aperiodicity per band tells a spread of
        // harmonics from a noise wash at a glance.
        if (frames > 0 && bap.Length > 0)
        {
            int bands = bap[0].Length;
            double lo = double.MaxValue, hi = double.MinValue, sum = 0.0;
            int count = 0;
            for (int f = 0; f < bap.Length; f++)
                for (int b = 0; b < bands; b++)
                {
                    double v = bap[f][b];
                    // 0 dB already means fully aperiodic; the model emits positive values on
                    // pause frames at phrase edges, and expanded they become louder than the
                    // signal itself (measured: +9.9 dB where the output peaked at 0.99).
                    if (v > 0.0) { v = 0.0; bap[f][b] = 0.0; }
                    lo = Math.Min(lo, v); hi = Math.Max(hi, v); sum += v; count++;
                }
            MutaDiagnostics.Log($"[MUTA] bap bands={bands} range={lo:F1}..{hi:F1} mean={(count > 0 ? sum / count : 0):F1}");
        }
        // The bank, when the voice has one, replaces the model's own timbre: the model still
        // supplies the pitch contour, but the frames come from recorded contexts.
        BankResult? bank = null;
        BankAssets? assets = null;
        if (request.UseBank)
        {
            try
            {
                assets = AssetsFor(voice);
                bank = BankRenderer.Assemble(assets, notes, request.TotalFrames,
                    request.BankPitchMode, request.BankPitchOffset, request.BankPitchNote, AssemblyFrames(geometry));
            }
            catch (Exception)
            {
                bank = null;
            }
        }
        CheckHeap("bank");

        // The model's own frames become the audio only when no bank took over. Then Sinsy's
        // own leading/trailing pauses have to go, and the slice starts where singing starts
        // -- read from the state durations, never from the label's time fields. When the bank
        // did take over, its frames already sit on the block's timeline and must not move.
        bool useBank = bank is not null && bank.Mgc.Length == request.TotalFrames;
        int contentStartFrame = 0;
        if (!useBank && japaneseLabels is not null && PhoneTimeline.Content(timeline) is { } window)
        {
            int from = Math.Clamp(window.Start, 0, frames - 1);
            int to = Math.Clamp(window.End, from + 1, frames);
            if (to - from >= 2 && (from > 0 || to < frames))
            {
                mgc = mgc[from..to];
                bap = bap[from..to];
                lf0 = lf0[from..to];
                contentStartFrame = from;
                frames = to - from;
            }
        }

        double[][] spectrumMgc;
        double[][] spectrumBap;
        bool[] silence;
        double[] pitch;

        if (useBank)
        {
            // The recorded layer replaces the model's parameters outright, which is what the
            // original does. No level or tone correction: the recordings carry their own,
            // and matching the two layers would be inventing a behaviour the original never
            // had -- it never plays the model's timbre once a bank is present.
            spectrumMgc = bank!.Mgc;
            spectrumBap = bank.Bap;
            silence = bank.Silence;
            // The carrier is the model's own F0 shape, resampled note by note onto the score
            // timeline; the bank only supplies timbre, so without this the contour is flat.
            pitch = CarrierFromModel(notes, timeline, lf0, request.TotalFrames);
            frames = request.TotalFrames;
        }
        else
        {
            spectrumMgc = mgc;
            spectrumBap = bap;
            pitch = PitchFromLf0(lf0);
            silence = new bool[pitch.Length];
            for (int f = 0; f < pitch.Length; f++)
                silence[f] = pitch[f] <= 0.0;

            // Voices do not agree on what zero means: MUTA's sits near a log gain of -4.5,
            // the Japanese one near +3, which is a factor of three thousand in amplitude.
            // The vocoder expects one reference, so each voice is placed on it.
            double sum = 0.0;
            int voiced = 0;
            for (int f = 0; f < spectrumMgc.Length; f++)
            {
                if (silence[f])
                    continue;
                sum += spectrumMgc[f][0];
                voiced++;
            }
            if (voiced > 0)
            {
                double correction = ReferenceLogGain - sum / voiced;
                if (Math.Abs(correction) > 0.01)
                    for (int f = 0; f < spectrumMgc.Length; f++)
                        spectrumMgc[f][0] += correction;
                MutaDiagnostics.Log(
                    $"[MUTA] level correction {correction:F3} (voice {sum / voiced:F3})");
            }
        }

        {
            double sum = 0.0; int voiced = 0;
            for (int f = 0; f < spectrumMgc.Length; f++)
                if (!silence[f]) { sum += spectrumMgc[f][0]; voiced++; }
            double modelSum = 0.0;
            for (int f = 0; f < mgc.Length; f++) modelSum += mgc[f][0];
            MutaDiagnostics.Log(
                $"[MUTA] frames {spectrumMgc.Length} voiced {voiced} c0 {(voiced > 0 ? sum / voiced : 0):F3} " +
                $"model c0 {(mgc.Length > 0 ? modelSum / mgc.Length : 0):F3} source {(bank is null ? "model" : "bank")}");
        }

        // Frames marked voiced that nothing wrote would play as holes with a click at each
        // edge. The crossfade's first frame is a no-op by construction (fade-out weight 1),
        // so a consonant slot that starts on a rest leaves exactly one such frame, and an
        // unwritten MGC row means a log gain of 0 -- full scale. That was the "huge noise at
        // the start of a phrase". Treat them as silence here as well, so a later change to
        // the assembler cannot bring the burst back.
        {
            int holes = 0;
            var where = new List<int>();
            for (int f = 0; f < spectrumMgc.Length; f++)
            {
                if (silence[f] || spectrumMgc[f][0] != 0.0)
                    continue;
                holes++;
                silence[f] = true;
                if (where.Count < 64)
                    where.Add(f);
            }
            if (holes > 0)
                MutaDiagnostics.Log(
                    $"[MUTA] unwritten voiced frames: {holes} of {spectrumMgc.Length} " +
                    $"at frames [{string.Join(",", where)}] " +
                    $"({string.Join(",", where.Select(f => (request.BlockStart + f * FrameSeconds).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)))})s " +
                    $"block start {request.BlockStart:F3}s total {request.TotalFrames}f");
        }

        // Controls, applied where they belong in the chain: energy scales the frame's log
        // gain, husky its aperiodicity, timbre the warping factor used to read the spectrum.
        var alphas = ApplyControls(request, spectrumMgc, spectrumBap, frames);

        // Bend the contour onto the notes the user actually wrote.
        var f0Notes = MapNotes(notes, frames);
        pitch = F0Mechanism.Build(pitch, f0Notes, frames,
            exactP4: true, startRamp: true, p5: true);

        // The host's pitch channels. Where the user drew a line, that line is the answer:
        // it is absolute MIDI, so the frame is set to it outright -- no clamp, no glide, no
        // model residue. Where the line has no value (NaN), the mechanism's shaped note
        // stands, and only the additive deviation is applied on top.
        if (request.PitchCurve is not null || request.PitchDeviation is not null)
        {
            for (int f = 0; f < frames && f < pitch.Length; f++)
            {
                if (pitch[f] <= 0.0)
                    continue;
                double deviation = 0.0;
                if (request.PitchDeviation is { } offset && f < offset.Length)
                    deviation = offset[f];
                double curve = double.NaN;
                if (request.PitchCurve is { } line && f < line.Length)
                    curve = line[f];

                if (!double.IsNaN(curve))
                    pitch[f] = 440.0 * Math.Pow(2.0, (curve + deviation - 69.0) / 12.0);
                else if (deviation != 0.0)
                    pitch[f] *= Math.Pow(2.0, deviation / 12.0);
            }
        }

        // Never hand the native vocoder a non-finite or absurd contour. A user-drawn line or
        // a vibrato that lands on a NaN is not worth a crash inside WORLD's pulse placement;
        // silence is the honest answer for a frame with no usable pitch.
        for (int f = 0; f < pitch.Length; f++)
        {
            double hz = pitch[f];
            if (!double.IsFinite(hz) || hz <= 0.0)
                pitch[f] = 0.0;
            else
                pitch[f] = Math.Clamp(hz, 8.0, 4000.0);
        }

        cancellation.ThrowIfCancellationRequested();

        var r2 = request.Mode == VocoderMode.WorldlineR2
            ? R2Registry.Get(request.R2Directory, request.MelModelPath)
            : null;
        ulong seed = request.Seed != 0UL ? request.Seed : VocoderPipeline.DeriveSeed(voice.Id, request.BlockStart);
        MutaDiagnostics.Log($"[MUTA] block {request.BlockStart:F3}s seed {seed}");
        var output = VocoderPipeline.Render(
            spectrumMgc, spectrumBap, pitch, silence, alphas,
            FitExpression(request.Expression, pitch.Length),
            request.Mode, r2, out _, seed);
        CheckHeap("vocoder");

        cancellation.ThrowIfCancellationRequested();

        // The Japanese chain trims the model's output to where the singing is, so what comes
        // back is a slice that starts later than the block does. Returning it as if it began
        // at the block start shifted every Japanese block earlier by up to a second and left a
        // hard cut at its end -- one click per block, 165 of them across a song. The slice goes
        // back at its own offset, and the block keeps its full length.
        int frameSamples = (int)Math.Round(SampleRate * FrameSeconds);
        int totalSamples = Math.Max(output.Samples.Length, request.TotalFrames * frameSamples);
        var audio = new float[totalSamples];
        double gain = Math.Pow(10.0, request.GainDecibels / 20.0);
        int origin = contentStartFrame * frameSamples;
        for (int i = 0; i < output.Samples.Length && origin + i < totalSamples; i++)
            audio[origin + i] = (float)(output.Samples[i] * gain);

        // Breath notes are rests to the model; what makes them audible is this mix. The level
        // is relative to the singing in the same block (MUTA measures ~-13 dB), so it follows
        // the bank and the output gain instead of being a fixed amplitude.
        if (notes.Any(note => note.Breath))
        {
            double sum = 0.0;
            long count = 0;
            for (int f = 0; f < pitch.Length; f++)
            {
                if (pitch[f] <= 0.0)
                    continue;
                int at = origin + f * frameSamples;
                if (at < 0 || at + frameSamples > totalSamples)
                    continue;
                for (int i = 0; i < frameSamples; i++)
                    sum += audio[at + i] * audio[at + i];
                count += frameSamples;
            }
            double singing = count > 0 ? Math.Sqrt(sum / count) : 0.0;
            var breath = BreathRenderer.Build(notes, totalSamples, frameSamples,
                assets?.Finals, seed);
            // A block of nothing but breaths has no singing level to be relative to; use
            // MUTA's absolute breath RMS (0.011-0.012) so a lone breath is still audible.
            double target = singing > 1e-4 ? BreathRenderer.LevelRatio * singing : 0.012 * gain;
            float scale = (float)target;
            for (int i = 0; i < totalSamples; i++)
                audio[i] += breath[i] * scale;
        }

        for (int i = 0; i < totalSamples; i++)
            audio[i] = (float)SoftLimit(audio[i]);

        // A boundary lands inside a note whenever the song has no rest there, and the two sides
        // were rendered with different noise, so their phases do not meet. A short equal-power
        // taper at each edge turns a click into a step nobody hears. Longer blocks make these
        // rare; this keeps the rare ones quiet.
        FadeEdges(audio, frameSamples * 2, origin);

        // The readback line should not break at every unvoiced consonant. The audio keeps its
        // real f0=0 there, but the contour shown to the editor bridges short holes *inside
        // voiced material*; a real rest (silence) still breaks the line.
        var readback = BridgeReadback(pitch, silence);

        if (contentStartFrame == 0)
            return new RenderResult(audio, readback, notes, geometry);

        var blockPitch = new double[request.TotalFrames];
        for (int f = 0; f < readback.Length && contentStartFrame + f < blockPitch.Length; f++)
            blockPitch[contentStartFrame + f] = readback[f];
        return new RenderResult(audio, blockPitch, notes, geometry);
    }

    /// <summary>
    /// A display-only contour: pitch holes shorter than <see cref="ReadbackBridgeFrames"/>
    /// are filled with a log-linear interpolation between the surrounding voiced frames,
    /// unless the frames between them are a real silence. The vocoder never sees this.
    /// </summary>
    const int ReadbackBridgeFrames = 50;   // 250 ms

    static double[] BridgeReadback(double[] pitch, bool[] silence)
    {
        var readback = (double[])pitch.Clone();
        int last = -1;
        for (int f = 0; f < readback.Length; f++)
        {
            if (readback[f] > 0.0)
            {
                if (last >= 0 && f - last > 1 && f - last - 1 <= ReadbackBridgeFrames)
                {
                    bool silent = false;
                    for (int k = last + 1; k < f && !silent; k++)
                        if (k < silence.Length && silence[k])
                            silent = true;
                    if (!silent)
                    {
                        double a = Math.Log(readback[last]);
                        double b = Math.Log(readback[f]);
                        for (int k = last + 1; k < f; k++)
                        {
                            double t = (double)(k - last) / (f - last);
                            readback[k] = Math.Exp(a + (b - a) * t);
                        }
                    }
                }
                last = f;
            }
        }
        return readback;
    }

    /// <summary>Equal-power taper over the first and last few milliseconds of a block.</summary>
    static void FadeEdges(float[] audio, int ramp, int skip)
    {
        if (ramp <= 0 || audio.Length <= ramp * 2 + skip)
            return;
        int start = Math.Max(0, skip);
        for (int i = 0; i < ramp; i++)
        {
            double gain = Math.Sin(Math.PI / 2.0 * i / ramp);
            audio[start + i] *= (float)gain;
            audio[audio.Length - 1 - i] *= (float)gain;
        }
    }
    /// <summary>
    /// Diagnostic heap check. HTS crashes in malloc/memset when the process heap has already
    /// been damaged by an earlier stage; validating after each stage names the culprit in the
    /// log before the crash happens.
    /// </summary>
    static void CheckHeap(string stage)
    {
        try
        {
            if (!HeapValidate(GetProcessHeap(), 0, IntPtr.Zero))
                MutaDiagnostics.Log($"[MUTA] process heap is CORRUPT after {stage}");
        }
        catch (Exception)
        {
            // HeapValidate is diagnostic only; never let it take a render down.
        }
    }

    [DllImport("kernel32.dll")]
    static extern IntPtr GetProcessHeap();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool HeapValidate(IntPtr heap, uint flags, IntPtr block);

    static int CountContent(string text)
    {
        int count = 0;
        foreach (var line in text.Split(new[] { (char)10, (char)13 }, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ');
            if (fields.Length < 3) continue;
            var field = fields[2];
            int at = field.IndexOf('@');
            var context = at >= 0 ? field[(at + 1)..] : field;
            int dash = context.IndexOf('-');
            int plus = dash >= 0 ? context.IndexOf('+', dash + 1) : -1;
            var symbol = dash >= 0 && plus > dash ? context[(dash + 1)..plus] : context;
            if (symbol is not ("pau" or "sil" or "xx")) count++;
        }
        return count;
    }

    /// <summary>
    /// Keeps a transient inside the rails without flattening it.
    ///
    /// Below the knee this is the identity; above it the curve approaches 1 asymptotically,
    /// so a peak that would have clipped instead rounds off. Clamping would have held the
    /// sample at full scale and added harmonics; this does not.
    /// </summary>
    const double LimitKnee = 0.8;

    static double SoftLimit(double value)
    {
        // The ceiling is 0.9: the host resamples 48 kHz to the project rate on the way out, and
        // a saturated signal overshoots that filter by several percent -- 0.95 came back as
        // 0.9988 in the exported file, which is a hair below full scale and far too hot.
        const double ceiling = 0.9;
        double magnitude = Math.Abs(value);
        if (magnitude <= LimitKnee)
            return value;
        double over = (magnitude - LimitKnee) / (1.0 - LimitKnee);
        double shaped = LimitKnee + (ceiling - LimitKnee) * Math.Tanh(over);
        return value < 0.0 ? -shaped : shaped;
    }

    static string FirstLine(string text)
    {
        var lines = text.Split(new[] { (char)10, (char)13 }, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 ? lines[0][..Math.Min(60, lines[0].Length)] : "";
    }

    static string LastLine(string text)
    {
        var lines = text.Split(new[] { (char)10, (char)13 }, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 ? lines[^1][..Math.Min(60, lines[^1].Length)] : "";
    }

    static NoteScore BuildScore(IReadOnlyList<RenderNote> notes, int totalFrames)
    {
        // A continuation mark emits no slot of its own (MUTA: IsDash -> continue). The head
        // note's final simply runs to the next content note, so its duration is extended by
        // the passengers' frames here; the slot builder derives every length from the next
        // slot's start, which then already lands after the hold.
        var phones = new List<string>(notes.Count);
        var starts = new List<int>(notes.Count);
        var durations = new List<int>(notes.Count);
        var pitches = new List<int>(notes.Count);
        foreach (var note in notes)
        {
            if (note.Continuation)
            {
                if (durations.Count > 0)
                    durations[^1] += note.Frames;
                continue;
            }
            phones.Add(note.Breath ? "sp" : note.Phone);
            starts.Add(note.StartFrame);
            durations.Add(note.Frames);
            pitches.Add(note.Midi);
        }
        return new NoteScore
        {
            Phones = phones,
            Starts = starts,
            Durations = durations,
            Pitches = pitches,
            Tempo = 120,
        };
    }

    /// <summary>
    /// The labels decide how many phones the model sings. If that number differs from what the
    /// front end promised, every slot after the difference lands on the wrong phone -- the old
    /// code shifted silently and the bank picked neighbouring units. This fails the block with
    /// a message naming both sequences instead.
    ///
    /// Names are only logged: a label may spell a phone differently while the count still holds,
    /// and that does not move anything.
    /// </summary>
    static void VerifyAlignment(IReadOnlyList<RenderNote> notes, IReadOnlyList<PhoneSpan> timeline)
    {
        var expected = new List<string>(notes.Count * 2);
        foreach (var note in notes)
        {
            // A continuation emits no slot at all (its head's vowel covers the span), so it
            // must not be counted as an expected phone either.
            if (note.Continuation || note.Rest || note.ModelFinal is "sil" or "")
                continue;
            bool hasOnset = !string.IsNullOrEmpty(note.ModelInitial);
            int count = note.ModelPhoneCount > 0 ? note.ModelPhoneCount : (hasOnset ? 2 : 1);
            if (count >= 3)
            {
                expected.Add("cl");
                expected.Add(note.ModelInitial!);
                expected.Add(note.ModelFinal!);
            }
            else if (count == 2 && !hasOnset)
            {
                expected.Add("cl");
                expected.Add(note.ModelFinal!);
            }
            else
            {
                if (hasOnset)
                    expected.Add(note.ModelInitial!);
                expected.Add(note.ModelFinal!);
            }
        }

        var actual = new List<string>(expected.Count);
        foreach (var span in timeline)
            if (!PhoneTimeline.IsPause(span.Phone) && span.Frames > 0)
                actual.Add(span.Phone);

        if (actual.Count != expected.Count)
            throw new InvalidOperationException(
                $"phone alignment: {notes.Count} notes expect {expected.Count} phones, the labels gave {actual.Count}; " +
                $"expected [{Preview(expected)}], labels [{Preview(actual)}]");

        for (int i = 0; i < expected.Count; i++)
            if (!string.Equals(expected[i], actual[i], StringComparison.Ordinal))
                MutaDiagnostics.Log($"[MUTA] phone name differs at {i}: expected {expected[i]}, labels {actual[i]}");
    }

    /// <summary>
    /// HTS parses "start end ..." as 100 ns integers and trusts them. A slot whose end is
    /// before its start (or a line that does not parse) is not something the engine reports
    /// -- it indexes out of its own frame arrays. Refuse the block instead.
    /// </summary>
    static void ValidateLabels(IReadOnlyList<string> labels)
    {
        if (labels.Count == 0)
            throw new InvalidOperationException("the label builder produced no lines");
        // A block is at most 30 s; anything past two minutes is a corrupt timestamp, and HTS
        // would turn it into a multi-gigabyte allocation that it then memsets without
        // checking (the access violation this guard exists for).
        const long MaxBlockTicks = 120L * 10_000_000L;
        long totalTicks = 0;
        for (int i = 0; i < labels.Count; i++)
        {
            var fields = labels[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3
                || !long.TryParse(fields[0], out long start)
                || !long.TryParse(fields[1], out long end))
                throw new InvalidOperationException($"label {i} does not start with two timestamps: [{labels[i]}]");
            // HTS copies each token into a fixed 1024-byte stack buffer without a length
            // check; a pinned phoneme longer than this would smash its stack.
            for (int field = 2; field < fields.Length; field++)
                if (fields[field].Length > 900)
                    throw new InvalidOperationException(
                        $"label {i} has a {fields[field].Length}-character token; the limit is 900");
            if (end < start)
                throw new InvalidOperationException(
                    $"label {i} ends before it starts ({start}..{end}): [{labels[i]}]");
            long length = end - start;
            if (length > MaxBlockTicks)
                throw new InvalidOperationException(
                    $"label {i} is {length / 10_000_000.0:F1} s long: [{labels[i]}]");
            totalTicks += length;
        }
        if (totalTicks > MaxBlockTicks)
            throw new InvalidOperationException(
                $"labels describe {totalTicks / 10_000_000.0:F1} s of audio in one block");
    }

    static string Preview(IReadOnlyList<string> phones)
    {
        const int shown = 24;
        var text = string.Join(" ", phones.Take(shown));
        return phones.Count > shown ? text + " ..." : text;
    }

    /// <summary>Reads a whole stream: the native getter fills every frame in one call.</summary>
    static void ReadStream(MutaHts engine, int stream, int width, double[][] destination)
    {
        var buffer = new double[destination.Length * width];
        engine.GetStream(stream, buffer);
        for (int f = 0; f < destination.Length; f++)
        {
            destination[f] = new double[width];
            Array.Copy(buffer, f * width, destination[f], 0, width);
        }
    }

    static void ReadStream(MutaHts engine, int stream, int width, double[] destination)
    {
        var buffer = new double[destination.Length * width];
        engine.GetStream(stream, buffer);
        for (int f = 0; f < destination.Length; f++)
            destination[f] = buffer[f * width];
    }

    /// <summary>
    /// The three parameter-domain controls, applied where the chain needs them.
    ///
    /// An absent curve is not applied at all, so a project that never draws one renders
    /// exactly as before -- that equality is the acceptance test for these controls.
    /// </summary>
    static double[] ApplyControls(RenderRequest request, double[][] mgc, double[][] bap, int frames)
    {
        // Energy: decibels added to each frame's log gain.
        if (request.Energy is { Length: > 0 } energy)
        {
            double perDecibel = Math.Log(10.0) / 20.0;
            for (int f = 0; f < mgc.Length; f++)
                mgc[f][0] += energy[Math.Min(f, energy.Length - 1)] * perDecibel;
        }

        // Husky: decibels of aperiodicity, weighted up towards the higher bands, where
        // breath and rasp live.
        if (request.Husky is { Length: > 0 } husky)
        {
            // The plan called for a ramp leaning on the higher bands, on the theory that
            // breath lives there. Measured through the vocoder it made the control nearly
            // inaudible: at the top of its range the lowest band moved 8 dB, from a weight
            // of 0.006 to 0.016, and the output changed by a tenth of a decibel. A uniform
            // lift moves the bands that carry the energy.
            for (int f = 0; f < bap.Length; f++)
            {
                double value = husky[Math.Min(f, husky.Length - 1)];
                if (value == 0.0)
                    continue;
                var frame = bap[f];
                for (int band = 0; band < frame.Length; band++)
                    frame[band] += value;
            }
        }

        // Timbre: the warping factor each frame is read with. Built as an array so the
        // spectrum stage can bucket equal values instead of warping every frame alone.
        double baseAlpha = double.IsFinite(request.Alpha)
            ? Math.Clamp(request.Alpha, 0.30, 0.80)
            : 0.55;
        var alphas = new double[frames];
        if (request.Timbre is { Length: > 0 } timbre)
        {
            for (int f = 0; f < frames; f++)
            {
                double value = timbre[Math.Min(f, timbre.Length - 1)];
                alphas[f] = double.IsFinite(value)
                    ? Math.Clamp(baseAlpha - 0.05 * value, 0.30, 0.80)
                    : baseAlpha;
            }
        }
        else
        {
            Array.Fill(alphas, baseAlpha);
        }
        return alphas;
    }

    /// <summary>
    /// Per-phone durations from the model's state durations, in the order the labels sang
    /// them. Pauses are dropped because the bank never assembles one.
    /// </summary>
    static List<int> PhoneDurations(IReadOnlyList<string> labels, int[] states, int statesPerPhone)
    {
        var durations = new List<int>(labels.Count);
        if (statesPerPhone <= 0 || states.Length < labels.Count * statesPerPhone)
            return durations;

        for (int i = 0; i < labels.Count; i++)
        {
            // "start end <left>-<phone>+<right>=..." -- the phone sits between the first
            // dash and the plus that follows it.
            string label = labels[i];
            int dash = label.IndexOf('-');
            int plus = dash >= 0 ? label.IndexOf('+', dash + 1) : -1;
            string phone = dash >= 0 && plus > dash ? label[(dash + 1)..plus] : string.Empty;
            if (phone.Length == 0 || phone is "sil" or "sp" or "xx")
                continue;

            int total = 0;
            int start = i * statesPerPhone;
            for (int s = 0; s < statesPerPhone; s++)
                total += states[start + s];
            if (total > 0)
                durations.Add(total);
        }
        return durations;
    }
    /// <summary>
    /// Expression curves sized to the frames actually rendered.
    ///
    /// The session evaluates them on the block's frames, but a block does not always render
    /// that many: the Japanese chain trims its output to where the singing is. Both share the
    /// same 5 ms origin, so the curve only has to be clipped or held to the new length.
    /// </summary>
    static VocoderExpression FitExpression(VocoderExpression expression, int frames)
    {
        if (expression.Gender is null && expression.Tension is null
            && expression.Breathiness is null && expression.Voicing is null)
            return expression;

        // The host's tracks are additive, so a curve can leave 0..1 even in normal use; the
        // worldline expression maps that range to integer shifts, and a non-finite value
        // there is meaningless. Clamp at the boundary instead of inside the vocoder.
        static double[]? Fit(double[]? curve, int count, double fallback)
        {
            if (curve is null)
                return null;
            var fitted = new double[count];
            for (int i = 0; i < count; i++)
            {
                double value = curve[Math.Min(i, curve.Length - 1)];
                fitted[i] = double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : fallback;
            }
            return fitted;
        }

        return new VocoderExpression
        {
            Gender = Fit(expression.Gender, frames, 0.5),
            Tension = Fit(expression.Tension, frames, 0.5),
            Breathiness = Fit(expression.Breathiness, frames, 0.5),
            Voicing = Fit(expression.Voicing, frames, 1.0),
        };
    }
    /// <summary>The frame counts the assembler consumes, in the order it asks for them.</summary>
    static List<int> AssemblyFrames(IReadOnlyList<PhonemeGeometry> geometry)
    {
        var frames = new List<int>(geometry.Count * 2);
        foreach (var item in geometry)
        {
            // Rests, breaths and continuations emit no phone slot, so the bank's phone cursor
            // must not advance for them either.
            if (item.Final is "sil" or "sp" || (item.Initial is null && item.BodyFrames == 0))
                continue;
            if (item.Initial is not null)
                frames.Add(item.LeadingFrames);
            frames.Add(item.BodyFrames);
        }
        return frames;
    }
    /// <summary>One note's phoneme geometry, in frames, once the host's layout has placed it.</summary>
    public readonly record struct PhonemeGeometry(
        string? Initial, string Final, int LeadingFrames, int BodyFrames, double BodyOffset,
        // Set only for a pinned note: the exact lists the user pinned, so a note with more
        // than one leading phoneme reports all of them instead of only the last.
        IReadOnlyList<(string Symbol, int Frames, double Weight)>? PinnedLeading = null,
        IReadOnlyList<(string Symbol, int Frames, double Weight)>? PinnedBody = null);

    /// <summary>
    /// Places the phonemes with the host's own layout instead of our arithmetic.
    ///
    /// The onset sits inside the note -- body offset equal to the onset's length -- which is
    /// MUTA's semantics: the consonant is sung on the beat, not before it. Everything else
    /// (how a phoneme is split across a beat, what a note borrows from its neighbour) is
    /// decided by the same code the host draws with, so the audio cannot disagree with the
    /// display.
    /// </summary>
    static PhonemeGeometry[] ResolveGeometry(
        IReadOnlyList<RenderNote> notes, List<int> phoneFrames, double blockStart)
    {
        var layout = new PhonemeLayoutNote[notes.Count];
        var splits = new (string? Initial, string Final)[notes.Count];
        var pinnedGeom = new (IReadOnlyList<(string Symbol, int Frames, double Weight)> Leading,
                              IReadOnlyList<(string Symbol, int Frames, double Weight)> Body)?[notes.Count];
        int cursor = 0;

        for (int i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            double start = blockStart + note.StartFrame * FrameSeconds;
            double end = start + note.Frames * FrameSeconds;

            // A held vowel fills its passengers: the layout's FillEnd is what makes the
            // editor draw the head's vowel across the continuation notes, and it is the same
            // span the bank stretches, so the picture and the audio agree.
            double fillEnd = end;
            for (int ahead = i + 1; ahead < notes.Count && notes[ahead].Continuation; ahead++)
                fillEnd += notes[ahead].Frames * FrameSeconds;

            if (note.Continuation)
            {
                // Transparent by definition: it owns no phoneme, the head's vowel runs over
                // it. Reporting anything here would draw a second attack the audio does not
                // have (and the SDK contract forbids phonemes on a continuation).
                splits[i] = (null, "sil");
                layout[i] = new PhonemeLayoutNote
                {
                    FillStart = start,
                    FillEnd = end,
                    LeadingPhonemes = Array.Empty<SynthesizedPhoneme>(),
                    BodyPhonemes = Array.Empty<SynthesizedPhoneme>(),
                    BodyOffset = 0.0,
                };
                continue;
            }

            if (note.Rest)
            {
                // A breath note is silence to the model (the "sp" slot keeps the previous
                // vowel from running into it) and carries a static "sp" phoneme so the
                // editor can show where the breath sits.
                splits[i] = (null, note.Breath ? "sp" : "sil");
                layout[i] = new PhonemeLayoutNote
                {
                    FillStart = start,
                    FillEnd = end,
                    LeadingPhonemes = Array.Empty<SynthesizedPhoneme>(),
                    BodyPhonemes = Array.Empty<SynthesizedPhoneme>(),
                    BodyOffset = 0.0,
                };
                continue;
            }

            var (initial, final) = (string.IsNullOrEmpty(note.ModelInitial) ? null : note.ModelInitial,
                                    string.IsNullOrEmpty(note.ModelFinal) ? note.Phone : note.ModelFinal!);

            if (note.Pinned is { } pin && pin.Body.Length > 0)
            {
                // Pinned by the user: report the pinned symbols and their own durations. The
                // model's phones are still consumed so later notes' slot lengths stay in step.
                int pinnedPhones = note.ModelPhoneCount > 0
                    ? note.ModelPhoneCount
                    : Math.Max(1, pin.Leading.Length + pin.Body.Length);
                for (int k = 0; k < pinnedPhones && cursor < phoneFrames.Count; k++)
                    cursor++;

                var pinnedLeading = pin.Leading.Select((s, k) => new SynthesizedPhoneme
                {
                    Symbol = s,
                    Duration = pin.LeadingDurations[k],
                    StretchWeight = pin.LeadingWeights[k],
                }).ToArray();
                var pinnedBody = pin.Body.Select((s, k) => new SynthesizedPhoneme
                {
                    Symbol = s,
                    Duration = pin.BodyDurations[k],
                    StretchWeight = pin.BodyWeights[k],
                }).ToArray();

                splits[i] = (pin.Leading.Length > 0 ? pin.Leading[^1] : null, pin.Body[0]);

                // Scale the pinned durations onto the note so the reported geometry is the
                // pinned shape, whatever the note's own length is.
                double totalSeconds = 0.0;
                foreach (double seconds in pin.LeadingDurations) totalSeconds += seconds;
                foreach (double seconds in pin.BodyDurations) totalSeconds += seconds;
                double scale = totalSeconds > 1e-6 ? note.Frames * FrameSeconds / totalSeconds : 0.0;
                int ToFrames(double seconds) => Math.Max(1, (int)Math.Round(seconds * scale / FrameSeconds));

                var reportLeading = pin.Leading
                    .Select((s, k) => (Symbol: s, Frames: ToFrames(pin.LeadingDurations[k]), Weight: pin.LeadingWeights[k]))
                    .ToArray();
                var reportBody = pin.Body
                    .Select((s, k) => (Symbol: s, Frames: ToFrames(pin.BodyDurations[k]), Weight: pin.BodyWeights[k]))
                    .ToArray();
                pinnedGeom[i] = (reportLeading, reportBody);

                layout[i] = new PhonemeLayoutNote
                {
                    FillStart = start,
                    FillEnd = fillEnd,
                    LeadingPhonemes = pinnedLeading,
                    BodyPhonemes = pinnedBody,
                    BodyOffset = pin.BodyOffset,
                };
                continue;
            }

            splits[i] = (initial, final);

            int leadingFrames = 0;
            if (initial is not null && cursor < phoneFrames.Count)
                leadingFrames = phoneFrames[cursor++];
            int bodyFrames = cursor < phoneFrames.Count ? phoneFrames[cursor++] : 0;
            if (bodyFrames <= 0)
                bodyFrames = Math.Max(1, note.Frames - leadingFrames);

            var leading = initial is null
                ? (IReadOnlyList<SynthesizedPhoneme>)Array.Empty<SynthesizedPhoneme>()
                : new[]
                {
                    new SynthesizedPhoneme
                    {
                        Symbol = initial,
                        Duration = leadingFrames * FrameSeconds,
                        StretchWeight = 0.0,
                    },
                };
            var body = new[]
            {
                new SynthesizedPhoneme
                {
                    Symbol = final,
                    Duration = bodyFrames * FrameSeconds,
                    StretchWeight = 1.0,
                },
            };

            layout[i] = new PhonemeLayoutNote
            {
                FillStart = start,
                FillEnd = fillEnd,
                LeadingPhonemes = leading,
                BodyPhonemes = body,
                // The vowel is the beat: the onset is sung ahead of it, which is what the
                // editor draws when a note is placed on the grid.
                BodyOffset = 0.0,
            };
        }

        var timings = PhonemeLayout.Resolve(layout);
        var result = new PhonemeGeometry[notes.Count];
        for (int i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            double noteStart = blockStart + note.StartFrame * FrameSeconds;
            var (initial, final) = splits[i];

            if (note.Continuation)
            {
                // No phoneme of its own to report: the SDK forbids a continuation from
                // returning phonemes, the head's vowel covers the span.
                result[i] = new PhonemeGeometry(null, "sil", 0, 0, 0.0);
                continue;
            }
            if (note.Rest)
            {
                // A breath note reports its static "sp" so the editor can show it; a plain
                // rest reports nothing.
                result[i] = new PhonemeGeometry(null, final, 0, note.Breath ? note.Frames : 0, 0.0);
                continue;
            }

            if (pinnedGeom[i] is { } pinnedLayout)
            {
                // Report the pinned lists exactly, in order, instead of collapsing them to
                // one leading and one body.
                result[i] = new PhonemeGeometry(
                    pinnedLayout.Leading.Count > 0 ? pinnedLayout.Leading[^1].Symbol : null,
                    pinnedLayout.Body[0].Symbol,
                    pinnedLayout.Leading.Sum(p => p.Frames),
                    pinnedLayout.Body.Sum(p => p.Frames),
                    note.Pinned!.BodyOffset,
                    pinnedLayout.Leading,
                    pinnedLayout.Body);
                continue;
            }
            var rows = timings[i];
            int index = 0;
            int leadingFrames = 0;
            if (initial is not null && index < rows.Length)
            {
                leadingFrames = Math.Max(0, (int)Math.Round(rows[index].Duration / FrameSeconds));
                index++;
            }
            int bodyFrames = index < rows.Length
                ? Math.Max(1, (int)Math.Round(rows[index].Duration / FrameSeconds))
                : Math.Max(1, note.Frames - leadingFrames);
            double bodyOffset = index < rows.Length ? rows[index].Start - noteStart : leadingFrames * FrameSeconds;
            result[i] = new PhonemeGeometry(initial, final, leadingFrames, bodyFrames, bodyOffset);
        }
        return result;
    }
    /// <summary>The model writes an unvoiced sentinel rather than zero.</summary>
    static double[] PitchFromLf0(double[] lf0)
    {
        var pitch = new double[lf0.Length];
        for (int f = 0; f < lf0.Length; f++)
            pitch[f] = lf0[f] <= -1e9 ? 0.0 : Math.Exp(lf0[f]);
        return pitch;
    }

    /// <summary>
    /// The model's F0 as a carrier on the block's timeline.
    ///
    /// The model's frames and the score's frames are not the same axis, so the mapping is per
    /// note: each note owns the model phone spans its labels emitted (one to three of them),
    /// and those frames are resampled onto the note's own frame count. Where the model has no
    /// F0 (a consonant, a closure) the note's pitch is used instead, so a note never goes
    /// silent just because its first frame was unvoiced.
    /// </summary>
    static double[] CarrierFromModel(
        IReadOnlyList<RenderNote> notes, IReadOnlyList<PhoneSpan> timeline, double[] modelF0, int frames)
    {
        var carrier = new double[frames];
        int cursor = 0;

        for (int noteIndex = 0; noteIndex < notes.Count; noteIndex++)
        {
            var note = notes[noteIndex];
            // A rest or breath has no model span of its own: consuming one here would hand
            // the next note's phones to the wrong note. Continuations are written by their
            // head, which owns the span that now covers the whole held duration.
            if (note.Rest || note.Continuation)
                continue;

            // The labels gave the head a single vowel slot covering the continuation notes,
            // so its F0 is resampled over the whole span, not just the head's own frames.
            int heldFrames = 0;
            for (int ahead = noteIndex + 1; ahead < notes.Count && notes[ahead].Continuation; ahead++)
                heldFrames += notes[ahead].Frames;
            int noteFrames = note.Frames + heldFrames;

            int count = note.ModelPhoneCount > 0
                ? note.ModelPhoneCount
                : (!string.IsNullOrEmpty(note.ModelInitial) ? 2 : 1);

            int start = -1, end = -1, used = 0;
            while (cursor < timeline.Count && used < count)
            {
                var span = timeline[cursor++];
                if (PhoneTimeline.IsPause(span.Phone) || span.Frames <= 0)
                    continue;
                if (start < 0)
                    start = span.StartFrame;
                end = span.EndFrame;
                used++;
            }
            if (start < 0 || end <= start)
                continue;

            int length = end - start;
            for (int f = 0; f < noteFrames; f++)
            {
                int destination = note.StartFrame + f;
                if (destination < 0 || destination >= frames)
                    break;
                int source = start + (int)Math.Round((length - 1) * (double)f / Math.Max(1, noteFrames - 1));
                if (source < 0 || source >= modelF0.Length)
                    continue;
                double hz = modelF0[source];
                carrier[destination] = hz > 0.0 ? hz : 440.0 * Math.Pow(2.0, (note.Midi - 69) / 12.0);
            }
        }
        return carrier;
    }

    static List<F0Note> MapNotes(IReadOnlyList<RenderNote> notes, int frames)
    {
        var mapped = new List<F0Note>(notes.Count);
        foreach (var note in notes)
        {
            if (note.StartFrame >= frames)
                break;
            mapped.Add(new F0Note(note.StartFrame, note.Frames, note.Midi, note.Rest));
        }
        return mapped;
    }
}
