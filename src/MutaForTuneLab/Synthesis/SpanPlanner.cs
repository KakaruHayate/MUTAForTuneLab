using System;

namespace MutaForTuneLab.Synthesis;

/// <summary>One contiguous piece of the song that is rendered in a single call.</summary>
public readonly record struct RenderSpan(
    int StartFrame, int FrameCount, int StartSample, int SampleCount, int SampleRate, bool Silent);

/// <summary>
/// Cuts a song into render spans.
///
/// Two constraints shape the cut. A span must be short enough to keep one synthesis call
/// responsive, and it must be long enough to include the frames the gate ramps across --
/// otherwise the ramp at a chunk boundary eats the neighbouring chunk's audio and the seam
/// clicks. Spans therefore carry a little context on each side, and the gate is applied to
/// the whole span before the host takes its slice.
///
/// Rests are their own spans: there is nothing to render, only silence to emit, and
/// keeping them separate means a small edit does not re-render the notes around it.
/// </summary>
public static class SpanPlanner
{
    /// <summary>Frames of context added on each side of a chunk.</summary>
    public const int ContextFrames = 2;

    /// <summary>Longest chunk, in frames (5 ms each, so about a second).</summary>
    public const int MaxChunkFrames = 200;

    /// <summary>
    /// Splits <paramref name="silence"/> into spans. Frame indices are the model's own
    /// grid; samples are derived from them, so a span's rate is the rate it was rendered at.
    /// </summary>
    public static RenderSpan[] Plan(
        ReadOnlySpan<bool> silence, int frameSamples, int sampleRate)
    {
        if (silence.Length == 0)
            return [];
        if (frameSamples <= 0)
            throw new ArgumentException("frame size must be positive", nameof(frameSamples));

        var spans = new System.Collections.Generic.List<RenderSpan>();
        int index = 0;
        while (index < silence.Length)
        {
            bool state = silence[index];
            int runEnd = index;
            while (runEnd < silence.Length && silence[runEnd] == state)
                runEnd++;

            // A rest is emitted as one silent span; audible stretches are chunked.
            if (state)
            {
                spans.Add(MakeSpan(index, runEnd - index, frameSamples, sampleRate, true));
                index = runEnd;
                continue;
            }

            int position = index;
            while (position < runEnd)
            {
                int count = Math.Min(MaxChunkFrames, runEnd - position);

                // Extend by the context the gate needs, without crossing into the rest.
                int from = Math.Max(0, position - ContextFrames);
                int to = Math.Min(silence.Length, position + count + ContextFrames);
                spans.Add(MakeSpan(from, to - from, frameSamples, sampleRate, false));

                position += count;
            }
            index = runEnd;
        }
        return spans.ToArray();
    }

    static RenderSpan MakeSpan(int startFrame, int frameCount, int frameSamples, int sampleRate, bool silent)
        => new(startFrame, frameCount, startFrame * frameSamples, frameCount * frameSamples, sampleRate, silent);
}
