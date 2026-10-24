using System;
using System.Collections.Generic;
using System.IO;
using MutaForTuneLab.Assets;
using MutaForTuneLab.Native;


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
    /// <summary>False forces the model's own timbre, for comparing the two paths.</summary>
    public bool UseBank { get; init; } = true;
}

public sealed record RenderResult(float[] Audio, double[] FramePitch, IReadOnlyList<RenderNote> Notes);

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

    public static RenderResult Render(RenderRequest request, CancellationToken cancellation)
    {
        var voice = request.Voice;
        var notes = request.Notes;
        if (notes.Count == 0)
            return new RenderResult([], [], notes);

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
                scoreNotes.Add(new ScoreNote(
                    note.StartFrame * FrameSeconds, note.Frames * FrameSeconds, note.Midi,
                    MutaSinsy.ToKana(note.Lyric)));
            }
            MutaDiagnostics.Log(
                $"[MUTA] jp note0 phone=[{notes[0].Phone}] lyric=[{notes[0].Lyric}] " +
                $"kana=[{MutaSinsy.ToKana(notes[0].Lyric)}] notes={notes.Count}");
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

        using var engine = MutaHts.Load(enginePath);
        engine.Speed = request.Speed;

        cancellation.ThrowIfCancellationRequested();
        int frames = engine.Synthesize(labels);
        if (frames <= 0)
            throw new InvalidOperationException("the acoustic model produced no frames");

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
                bank = BankRenderer.Assemble(assets, notes, request.TotalFrames);
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
        double[]? spectralGain = null;

        if (bank is not null && bank.Mgc.Length == request.TotalFrames)
        {
            spectrumMgc = bank.Mgc;
            spectrumBap = bank.Bap;
            silence = bank.Silence;
            pitch = CarrierFromNotes(notes, request.TotalFrames);
            frames = request.TotalFrames;

            // The bank answers at its recorded level, several dB under what the model
            // produces for the same notes. The model defines the level the rest of the chain
            // was tuned against, so the bank is shifted onto it by the difference of the two
            // mean log-gains. The timbre still comes from the recordings.
            double bankGain = 0.0, modelGain = 0.0;
            int bankFrames = 0, modelFrames = 0;
            for (int f = 0; f < spectrumMgc.Length; f++)
                if (!silence[f]) { bankGain += spectrumMgc[f][0]; bankFrames++; }
            for (int f = 0; f < mgc.Length; f++)
                if (mgc[f][0] > -8.5) { modelGain += mgc[f][0]; modelFrames++; }

            double offset = bankFrames > 0 && modelFrames > 0
                ? modelGain / modelFrames - bankGain / bankFrames
                : 0.0;
            if (Math.Abs(offset) > 0.01)
                for (int f = 0; f < spectrumMgc.Length; f++)
                    spectrumMgc[f][0] += offset;

            MutaDiagnostics.Log(
                $"[MUTA] bank level offset {offset:F3} " +
                $"(model {modelGain / Math.Max(1, modelFrames):F3}, bank {bankGain / Math.Max(1, bankFrames):F3})");

            // Level is only half of it: the recordings and the model disagree about the
            // shape too, by several dB around the first formant. The model runs anyway for
            // the pitch, so its average spectrum is already here to match against.
            spectralGain = SpectralMatch.BuildGain(mgc, spectrumMgc, request.Alpha, Bins);
            MutaDiagnostics.Log(spectralGain is null
                ? "[MUTA] spectral match unavailable"
                : $"[MUTA] spectral match applied, gain {spectralGain.Min():F3}..{spectralGain.Max():F3}");

            // The other half of the same character: how much of each band is noise. The
            // recordings run several dB more aperiodic through the middle than the model,
            // and the vocoder splits each band on this curve, so an envelope match alone
            // leaves the layers sounding different.
            var aperiodicityOffset = SpectralMatch.BuildAperiodicityOffset(bap, spectrumBap);
            if (aperiodicityOffset is not null)
            {
                for (int f = 0; f < spectrumBap.Length; f++)
                {
                    var frame = spectrumBap[f];
                    for (int band = 0; band < frame.Length && band < aperiodicityOffset.Length; band++)
                        frame[band] += aperiodicityOffset[band];
                }
                MutaDiagnostics.Log(
                    $"[MUTA] aperiodicity match {aperiodicityOffset.Min():F2}..{aperiodicityOffset.Max():F2} dB");
            }
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

        // Bend the contour onto the notes the user actually wrote.
        var f0Notes = MapNotes(notes, frames);
        pitch = F0Mechanism.Build(pitch, f0Notes, frames,
            exactP4: true, startRamp: true, p5: true);

        cancellation.ThrowIfCancellationRequested();

        var r2 = request.Mode == VocoderMode.WorldlineR2
            ? R2Registry.Get(request.R2Directory, request.MelModelPath)
            : null;
        var output = VocoderPipeline.Render(
            spectrumMgc, spectrumBap, pitch, silence, request.Alpha, request.Expression,
            request.Mode, r2, out _, spectralGain);

        cancellation.ThrowIfCancellationRequested();

        var audio = new float[output.Samples.Length];
        double gain = Math.Pow(10.0, request.GainDecibels / 20.0);
        for (int i = 0; i < audio.Length; i++)
            audio[i] = (float)Math.Clamp(output.Samples[i] * gain, -1.0, 1.0);

        return new RenderResult(audio, pitch, notes);
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

    /// <summary>The model writes an unvoiced sentinel rather than zero.</summary>
    static double[] PitchFromLf0(double[] lf0)
    {
        var pitch = new double[lf0.Length];
        for (int f = 0; f < lf0.Length; f++)
            pitch[f] = lf0[f] <= -1e9 ? 0.0 : Math.Exp(lf0[f]);
        return pitch;
    }

    /// <summary>A flat pitch contour from the notes, for when the bank supplies the frames.</summary>
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
