using System;
using System.Collections.Generic;
using System.IO;
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
                    note.StartFrame * FrameSeconds, note.Frames * FrameSeconds, note.Midi, note.Lyric));
            }
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
        try
        {
            var assets = BankAssets.Load(voice);
            bank = BankRenderer.Assemble(assets, notes, request.TotalFrames);
        }
        catch (Exception)
        {
            bank = null;
        }

        double[][] spectrumMgc;
        double[][] spectrumBap;
        bool[] silence;
        double[] pitch;

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

            TuneLabContext.Global.GetLogger().Debug(
                $"[MUTA] bank level offset {offset:F3} " +
                $"(model {modelGain / Math.Max(1, modelFrames):F3}, bank {bankGain / Math.Max(1, bankFrames):F3})");
        }
        else
        {
            spectrumMgc = mgc;
            spectrumBap = bap;
            pitch = PitchFromLf0(lf0);
            silence = new bool[pitch.Length];
            for (int f = 0; f < pitch.Length; f++)
                silence[f] = pitch[f] <= 0.0;
        }

        {
            double sum = 0.0; int voiced = 0;
            for (int f = 0; f < spectrumMgc.Length; f++)
                if (!silence[f]) { sum += spectrumMgc[f][0]; voiced++; }
            double modelSum = 0.0;
            for (int f = 0; f < mgc.Length; f++) modelSum += mgc[f][0];
            TuneLabContext.Global.GetLogger().Debug(
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
            request.Mode, r2, out _);

        cancellation.ThrowIfCancellationRequested();

        var audio = new float[output.Samples.Length];
        double gain = Math.Pow(10.0, request.GainDecibels / 20.0);
        for (int i = 0; i < audio.Length; i++)
            audio[i] = (float)Math.Clamp(output.Samples[i] * gain, -1.0, 1.0);

        return new RenderResult(audio, pitch, notes);
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
