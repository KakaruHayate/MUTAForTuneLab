using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// <summary>False forces the model's own timbre, for comparing the two paths.</summary>
    public bool UseBank { get; init; } = true;

    /// <summary>Energy in decibels per frame; null leaves the model's own dynamics.</summary>
    public double[]? Energy { get; init; }
    /// <summary>Timbre in −4..4 per frame; null means the default warping factor.</summary>
    public double[]? Timbre { get; init; }
    /// <summary>Husky in decibels per frame; null leaves the model's aperiodicity.</summary>
    public double[]? Husky { get; init; }

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
    /// <summary>
    /// One engine per voice file, kept for the life of the process.
    ///
    /// Loading a voice costs a 7 MB decode, and doing it once per block was also the last
    /// thing that happened before the engine started crashing on a real project. Reuse is
    /// both faster and removes that whole lifecycle from the hot path; the call below resets
    /// the speed it needs each time.
    /// </summary>
    static readonly Dictionary<string, MutaHts> sEngines = new(StringComparer.Ordinal);

    static MutaHts EngineFor(string path)
    {
        if (sEngines.TryGetValue(path, out var engine))
            return engine;
        engine = MutaHts.Load(path);
        sEngines[path] = engine;
        return engine;
    }
    static readonly object RenderGate = new();

    public static RenderResult Render(RenderRequest request, CancellationToken cancellation)
    {
        lock (RenderGate)
            return RenderCore(request, cancellation);
    }

    static RenderResult RenderCore(RenderRequest request, CancellationToken cancellation)
    {
        var voice = request.Voice;
        var notes = request.Notes;
        if (notes.Count == 0)
            return new RenderResult([], [], notes, []);

        cancellation.ThrowIfCancellationRequested();

        // Two front ends feed the same acoustic model: MUTA's label builder for Chinese,
        // Sinsy's for Japanese. Both end at the same place -- a list of label lines.
        IReadOnlyList<string> labels;
        string enginePath;
        string? japaneseLabels = null;
        if (voice.IsJapanese)
        {
            var scoreNotes = new List<ScoreNote>(notes.Count);
            foreach (var note in notes)
            {
                // The phone, not the lyric: the session has already checked it against the
                // kana table, so a lyric the voice cannot pronounce has become a vowel by now.
                // Handing Sinsy the raw lyric instead let 'w' through, and a score line it
                // cannot read produced labels that killed the native engine.
                scoreNotes.Add(new ScoreNote(
                    note.StartFrame * FrameSeconds, note.Frames * FrameSeconds, note.Midi,
                    MutaSinsy.ToKana(note.Phone)));
            }
            MutaDiagnostics.Log(
                $"[MUTA] jp note0 phone=[{notes[0].Phone}] lyric=[{notes[0].Lyric}] " +
                $"kana=[{MutaSinsy.ToKana(notes[0].Phone)}] notes={notes.Count}");
            japaneseLabels = MutaSinsy.Labels(scoreNotes, request.TotalFrames * FrameSeconds,
                MutaSinsy.DictionaryDirectory);
            if (japaneseLabels is null)
                throw new InvalidOperationException("the Japanese label builder refused the score");
            labels = japaneseLabels.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            enginePath = voice.VoiceFile;
        }
        else
        {
            labels = LabelBuilder.Build(BuildScore(notes, request.TotalFrames));
            enginePath = HtsVoice.Load(voice.VoiceFile).EnginePath;
        }

        var engine = EngineFor(enginePath);
        engine.Speed = request.Speed;

        cancellation.ThrowIfCancellationRequested();

        // If the native engine ever dies, this line is the last thing written: the exact
        // phones and lyrics it was asked to sing.
        if (notes.Count > 0)
            MutaDiagnostics.Log(
                "[MUTA] synth " + string.Join("|", notes.Select(n => n.Phone + "@" + n.Midi + ":" + n.Lyric + ":" + n.Frames)));

        int frames = engine.Synthesize(labels);
        if (frames <= 0)
            throw new InvalidOperationException("the acoustic model produced no frames");

        // The model's own durations, in the order the labels sang them. The bank assembler
        // stretches recorded contexts to fit these, which is what the original does -- and
        // what makes the speed control reach the sampled path at all.
        var phoneFrames = PhoneDurations(labels, engine.StateDurations(), engine.States);
        MutaDiagnostics.Log($"[MUTA] model frames {frames} speed {request.Speed:F2} phones {phoneFrames.Count} " +
            $"sum {phoneFrames.Sum()} first {(phoneFrames.Count > 0 ? phoneFrames[0] : 0)}");

        // Where each phoneme actually sits is the host's own layout's business, so that what
        // is drawn and what is heard cannot drift apart.
        var geometry = ResolveGeometry(notes, phoneFrames, request.BlockStart);

        int mgcWidth = engine.VectorLength(0);
        int bapWidth = engine.VectorLength(2);
        var mgc = new double[frames][];
        var bap = new double[frames][];
        var lf0 = new double[frames];
        ReadStream(engine, 0, mgcWidth, mgc);
        ReadStream(engine, 2, bapWidth, bap);
        ReadStream(engine, 1, engine.VectorLength(1), lf0);

        // Sinsy pads the phrase with pauses it adds itself; the labels say where the singing
        // actually is, so cut to that and the span matches the block again.
        if (japaneseLabels is not null)
        {
            var probe = MutaSinsy.ContentWindow(japaneseLabels, FrameSeconds);
            MutaDiagnostics.Log(
                $"[MUTA] jp: {frames} frames, window {(probe is null ? "null" : $"{probe.Value.StartFrame}..{probe.Value.EndFrame}")} " +
                $"lines={japaneseLabels.Split((char)10).Length} nonpau={CountContent(japaneseLabels)}");
        }

        // Where the kept slice begins, once Sinsy's content window has trimmed it.
        int contentStartFrame = 0;

        if (japaneseLabels is not null
            && MutaSinsy.ContentWindow(japaneseLabels, FrameSeconds) is { } window)
        {
            int from = Math.Clamp(window.StartFrame, 0, frames - 1);
            int to = Math.Clamp(window.EndFrame, from + 1, frames);
            if (to - from >= 2 && (from > 0 || to < frames))
            {
                mgc = mgc[from..to];
                bap = bap[from..to];
                lf0 = lf0[from..to];
                contentStartFrame = from;
                frames = to - from;
            }
        }

        cancellation.ThrowIfCancellationRequested();

        // The bank, when the voice has one, replaces the model's own timbre: the model still
        // supplies the pitch contour, but the frames come from recorded contexts.
        BankResult? bank = null;
        if (request.UseBank)
        {
            try
            {
                var assets = BankAssets.Load(voice);
                bank = BankRenderer.Assemble(assets, notes, request.TotalFrames,
                    request.BankPitchMode, request.BankPitchOffset, request.BankPitchNote, AssemblyFrames(geometry));
            }
            catch (Exception)
            {
                bank = null;
            }
        }

        double[][] spectrumMgc;
        double[][] spectrumBap;
        bool[] silence;
        double[] pitch;

        if (bank is not null && bank.Mgc.Length == request.TotalFrames)
        {
            // The recorded layer replaces the model's parameters outright, which is what the
            // original does. No level or tone correction: the recordings carry their own,
            // and matching the two layers would be inventing a behaviour the original never
            // had -- it never plays the model's timbre once a bank is present.
            spectrumMgc = bank.Mgc;
            spectrumBap = bank.Bap;
            silence = bank.Silence;
            pitch = CarrierFromNotes(notes, request.TotalFrames);
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
        // edge. Count them here rather than guess.
        {
            int holes = 0;
            for (int f = 0; f < spectrumMgc.Length; f++)
                if (!silence[f] && spectrumMgc[f][0] == 0.0) holes++;
            if (holes > 0)
                MutaDiagnostics.Log($"[MUTA] unwritten voiced frames: {holes} of {spectrumMgc.Length}");
        }

        // Controls, applied where they belong in the chain: energy scales the frame's log
        // gain, husky its aperiodicity, timbre the warping factor used to read the spectrum.
        var alphas = ApplyControls(request, spectrumMgc, spectrumBap, frames);

        // Bend the contour onto the notes the user actually wrote.
        var f0Notes = MapNotes(notes, frames);
        pitch = F0Mechanism.Build(pitch, f0Notes, frames,
            exactP4: true, startRamp: true, p5: true);

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
            audio[origin + i] = (float)SoftLimit(output.Samples[i] * gain);

        // A boundary lands inside a note whenever the song has no rest there, and the two sides
        // were rendered with different noise, so their phases do not meet. A short equal-power
        // taper at each edge turns a click into a step nobody hears. Longer blocks make these
        // rare; this keeps the rare ones quiet.
        FadeEdges(audio, frameSamples * 2, origin);

        if (contentStartFrame == 0)
            return new RenderResult(audio, pitch, notes, geometry);

        var blockPitch = new double[request.TotalFrames];
        for (int f = 0; f < pitch.Length && contentStartFrame + f < blockPitch.Length; f++)
            blockPitch[contentStartFrame + f] = pitch[f];
        return new RenderResult(audio, blockPitch, notes, geometry);
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
        double magnitude = Math.Abs(value);
        if (magnitude <= LimitKnee)
            return value;
        double over = (magnitude - LimitKnee) / (1.0 - LimitKnee);
        double shaped = LimitKnee + (1.0 - LimitKnee) * Math.Tanh(over);
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
        var phones = new string[notes.Count];
        var starts = new int[notes.Count];
        var durations = new int[notes.Count];
        var pitches = new int[notes.Count];
        for (int i = 0; i < notes.Count; i++)
        {
            phones[i] = notes[i].Phone;
            starts[i] = notes[i].StartFrame;
            durations[i] = notes[i].Frames;
            pitches[i] = notes[i].Midi;
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
        var alphas = new double[frames];
        if (request.Timbre is { Length: > 0 } timbre)
        {
            for (int f = 0; f < frames; f++)
            {
                double value = timbre[Math.Min(f, timbre.Length - 1)];
                alphas[f] = Math.Clamp(request.Alpha - 0.05 * value, 0.30, 0.80);
            }
        }
        else
        {
            Array.Fill(alphas, request.Alpha);
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

        static double[]? Fit(double[]? curve, int count)
        {
            if (curve is null || curve.Length == count)
                return curve;
            var fitted = new double[count];
            for (int i = 0; i < count; i++)
                fitted[i] = curve[Math.Min(i, curve.Length - 1)];
            return fitted;
        }

        return new VocoderExpression
        {
            Gender = Fit(expression.Gender, frames),
            Tension = Fit(expression.Tension, frames),
            Breathiness = Fit(expression.Breathiness, frames),
            Voicing = Fit(expression.Voicing, frames),
        };
    }
    /// <summary>The frame counts the assembler consumes, in the order it asks for them.</summary>
    static List<int> AssemblyFrames(IReadOnlyList<PhonemeGeometry> geometry)
    {
        var frames = new List<int>(geometry.Count * 2);
        foreach (var item in geometry)
        {
            if (item.Final == "sil")
                continue;
            if (item.Initial is not null)
                frames.Add(item.LeadingFrames);
            frames.Add(item.BodyFrames);
        }
        return frames;
    }
    /// <summary>One note's phoneme geometry, in frames, once the host's layout has placed it.</summary>
    public readonly record struct PhonemeGeometry(
        string? Initial, string Final, int LeadingFrames, int BodyFrames, double BodyOffset);

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
        int cursor = 0;

        for (int i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            double start = blockStart + note.StartFrame * FrameSeconds;
            double end = start + note.Frames * FrameSeconds;

            if (note.Rest)
            {
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

            var split = LabelBuilder.SplitSyllable(note.Phone);
            string? initial = split?.Initial;
            string final = split?.Final ?? note.Phone;
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
                FillEnd = end,
                LeadingPhonemes = leading,
                BodyPhonemes = body,
                // The junction sits one onset-length after the note head.
                BodyOffset = leadingFrames * FrameSeconds,
            };
        }

        var timings = PhonemeLayout.Resolve(layout);
        var result = new PhonemeGeometry[notes.Count];
        for (int i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            double noteStart = blockStart + note.StartFrame * FrameSeconds;
            var (initial, final) = splits[i];
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

    static double[] CarrierFromNotes(IReadOnlyList<RenderNote> notes, int frames)
    {
        var carrier = new double[frames];
        foreach (var note in notes)
        {
            if (note.Rest)
                continue;
            double hz = 440.0 * Math.Pow(2.0, (note.Midi - 69) / 12.0);
            for (int f = note.StartFrame; f < Math.Min(frames, note.StartFrame + note.Frames); f++)
                carrier[f] = hz;
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
