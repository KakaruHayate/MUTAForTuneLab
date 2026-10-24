using System;
using System.Collections.Generic;
using System.IO;
using MutaForTuneLab.Assets;
using MutaForTuneLab.Native;

namespace MutaForTuneLab.Synthesis;

/// <summary>One note as the renderer needs it: on the model's frame grid, already phonetic.</summary>
public readonly record struct RenderNote(int StartFrame, int Frames, int Midi, string Phone, bool Rest = false);

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

        var score = BuildScore(notes, request.TotalFrames);
        var labels = LabelBuilder.Build(score);

        var container = HtsVoice.Load(voice.VoiceFile);
        using var engine = MutaHts.Load(container.EnginePath);
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

        cancellation.ThrowIfCancellationRequested();

        var pitch = PitchFromLf0(lf0);
        var silence = new bool[frames];
        for (int f = 0; f < frames; f++)
            silence[f] = pitch[f] <= 0.0;

        // Bend the model's pitch onto the notes the user actually wrote.
        var f0Notes = MapNotes(notes, frames);
        pitch = F0Mechanism.Build(pitch, f0Notes, frames,
            exactP4: true, startRamp: true, p5: true);

        cancellation.ThrowIfCancellationRequested();

        var r2 = request.Mode == VocoderMode.WorldlineR2
            ? R2Registry.Get(request.R2Directory, request.MelModelPath)
            : null;
        var output = VocoderPipeline.Render(
            mgc, bap, pitch, silence, request.Alpha, request.Expression, request.Mode, r2, out _);

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
