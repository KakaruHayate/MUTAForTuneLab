using System;
using System.Collections.Generic;

namespace MutaForTuneLab.Synthesis;

/// <summary>One note for the F0 assembly: which frames it covers and the pitch it wants.</summary>
public readonly record struct F0Note(int StartFrame, int Frames, int Midi, bool Rest = false);

/// <summary>
/// Mechanism-level F0 assembly: takes the carrier F0 the acoustic model produced and
/// bends each note onto its own pitch.
///
/// Per note:
///   P2  the note's voiced frames are sorted and a trimmed mean is taken — trim
///       <c>scoop</c> values from each end of the *sorted* array, not a contiguous
///       window of the note (the trimming is by value, which is what the engine does);
///   P3  every voiced frame is shifted by (target − that mean);
///   P4  the result is pulled into a ±0.22 semitone band around the target, with a
///       ramp at the note's onset and at its end when another note follows;
///   P5  the first frames are replaced by a line from the previous frame's value to
///       f0[start + b4], so a jump glides in instead of stepping.
///
/// The carrier is never modified: several stages read it while writing the result
/// (the band is extended while the carrier is still voiced past the note end).
/// </summary>
public static class F0Mechanism
{
    public const double ClampSemitones = 0.22;
    const int ScoopShortDiv = 3;
    const int ScoopLongNum = 2;
    const int ScoopLongDen = 5;
    const int ScoopSplit = 7;
    /// <summary>The engine keeps the band up to this many frames past the note end.</summary>
    const int ExtendLimit = 10;

    public static double MidiHz(double midi) => 440.0 * Math.Pow(2.0, (midi - 69.0) / 12.0);

    static int Scoop(int length) =>
        length / 5 < ScoopSplit ? length / ScoopShortDiv : length * ScoopLongNum / ScoopLongDen;

    public static double[] Build(
        double[] carrierHz,
        IReadOnlyList<F0Note> notes,
        int? total = null,
        double clampSemitones = ClampSemitones,
        bool ramp = false,
        bool smooth = false,
        bool exactP4 = false,
        bool startRamp = true,
        bool p5 = false,
        bool extendVoiced = true)
    {
        int totalFrames = total ?? carrierHz.Length;
        var f0 = new double[totalFrames];
        Array.Copy(carrierHz, f0, Math.Min(carrierHz.Length, totalFrames));

        int? previousCode = null;
        int previousEnd = -1;

        for (int noteIndex = 0; noteIndex < notes.Count; noteIndex++)
        {
            var note = notes[noteIndex];
            int start = note.StartFrame;
            int length = note.Frames;
            int stop = Math.Min(start + length, totalFrames);
            if (stop <= start)
                continue;

            if (note.Rest)
            {
                Array.Clear(f0, start, stop - start);
                continue;
            }

            if (extendVoiced)
            {
                int limit = Math.Min(stop + ExtendLimit, totalFrames);
                int nextStart = noteIndex + 1 < notes.Count ? notes[noteIndex + 1].StartFrame : totalFrames;
                while (stop < limit && Carrier(carrierHz, stop) > 0.0 && stop < nextStart)
                    stop++;
            }

            double target = MidiHz(note.Midi);
            int code = note.Midi - 12;
            int segmentLength = stop - start;
            if (segmentLength <= 0)
                continue;

            bool hasNext = noteIndex + 1 < notes.Count
                           && notes[noteIndex + 1].StartFrame <= start + length
                           && notes[noteIndex + 1].Midi != note.Midi;

            // Voiced frame indices, relative to the note's segment.
            var voiced = new List<int>(segmentLength);
            for (int i = 0; i < segmentLength; i++)
                if (f0[start + i] > 0.0)
                    voiced.Add(i);
            if (voiced.Count == 0)
                continue;

            var ordered = new double[voiced.Count];
            for (int k = 0; k < voiced.Count; k++)
                ordered[k] = f0[start + voiced[k]];
            Array.Sort(ordered);

            int width = ordered.Length;
            int scoop = Scoop(width);
            double sum = 0.0;
            int middleCount;
            int middleStart;
            if (width > 2 * scoop)
            {
                middleStart = scoop;
                middleCount = width - 2 * scoop;
            }
            else
            {
                middleStart = 0;
                middleCount = width;
            }
            if (middleCount <= 0)
                continue;
            for (int k = 0; k < middleCount; k++)
                sum += ordered[middleStart + k];
            double shift = target - sum / middleCount;

            foreach (int i in voiced)
            {
                double value = f0[start + i] + shift;
                if (value > 0.0)
                    f0[start + i] = value;
            }

            double lo = target * Math.Pow(2.0, -clampSemitones / 12.0);
            double hi = target * Math.Pow(2.0, clampSemitones / 12.0);

            if (!exactP4)
            {
                foreach (int i in voiced)
                    if (f0[start + i] > 0.0)
                        f0[start + i] = Math.Min(Math.Max(f0[start + i], lo), hi);
                if (ramp)
                    ApplyRamp(f0, start, segmentLength, previousCode, code);
                if (smooth)
                    ApplySmoothing(f0, start, segmentLength, Math.Min((int)(segmentLength * 0.2), 5));
                previousEnd = stop;
                previousCode = code;
                continue;
            }

            double maximum = double.MinValue, minimum = double.MaxValue;
            foreach (int i in voiced)
            {
                double value = f0[start + i];
                maximum = Math.Max(maximum, value);
                minimum = Math.Min(minimum, value);
            }

            int b4 = Math.Min((int)(segmentLength * 0.2), 60);
            if (previousCode is { } pc0 && pc0 <= code + 2 && code <= pc0)
                b4 /= 2;
            int c0 = Math.Min((int)(segmentLength * 0.1), 60);

            foreach (int i in voiced)
            {
                double f = f0[start + i];
                if (f <= 0.0)
                    continue;

                double t;
                if (startRamp && i < b4 && previousCode is { } pc && pc != code && previousEnd >= start - 1)
                    t = (double)i / b4;
                else if (hasNext && i >= segmentLength - c0 && c0 > 0)
                    t = (double)(segmentLength - i) / c0;
                else
                    t = 1.0;

                if (f <= target || maximum <= hi)
                {
                    if (f <= target && f != target && minimum < lo)
                    {
                        double ratio = ((target - lo) + (lo - minimum) * (1.0 - t)) / (target - minimum);
                        f0[start + i] = target - (target - f) * ratio;
                    }
                }
                else
                {
                    double ratio = ((hi + (maximum - hi) * (1.0 - t)) - target) / (maximum - target);
                    f0[start + i] = (f - target) * ratio + target;
                }
            }

            if (p5 && previousCode is { } previous && previous != code && start > 0 && previousEnd >= start - 1)
            {
                // Glide in: replace the first b4 frames by a line from the previous
                // frame's value to f0[start + b4]. Frames already outside the band are
                // left alone, so b4 shrinks from the end until it starts inside.
                b4 = Math.Min((int)(segmentLength * 0.2), 60);
                if (previous <= code + 2 && code <= previous)
                    b4 /= 2;
                int scan = start + b4 - 1;
                while (scan >= start)
                {
                    double value = scan < totalFrames ? f0[scan] : 0.0;
                    if (value <= lo || value > hi)
                        break;
                    b4--;
                    scan--;
                }
                if (b4 > 1 && start + b4 < totalFrames)
                {
                    double anchor = f0[start - 1];
                    if (anchor > 0.0)
                    {
                        f0[start] = anchor;
                        double endValue = f0[start + b4];
                        for (int offset = 1; offset < b4; offset++)
                            f0[start + offset] = (endValue - f0[start]) * ((double)offset / b4) + f0[start];
                    }
                }
            }

            if (ramp)
                ApplyRamp(f0, start, segmentLength, previousCode, code);
            if (smooth)
                ApplySmoothing(f0, start, segmentLength, Math.Min((int)(segmentLength * 0.2), 5));

            previousEnd = stop;
            previousCode = code;
        }

        return f0;
    }

    static double Carrier(double[] carrier, int index)
        => index >= 0 && index < carrier.Length ? carrier[index] : 0.0;

    /// <summary>Linear scoop over the first frames (the engine's onset ramp).</summary>
    static void ApplyRamp(double[] f0, int start, int length, int? previousCode, int code)
    {
        int b4 = Math.Min((int)(length * 0.2), 60);
        if (previousCode is { } previous && previous <= code + 2 && code <= previous)
            b4 /= 2;
        if (b4 <= 1 || b4 >= length)
            return;

        double startValue = f0[start];
        double endValue = f0[start + b4];
        for (int f = 1; f < b4; f++)
            f0[start + f] = (endValue - startValue) * ((double)f / b4) + startValue;
    }

    /// <summary>Moving average over ±radius voiced frames; unvoiced frames stay put.</summary>
    static void ApplySmoothing(double[] f0, int start, int length, int radius)
    {
        if (radius <= 0)
            return;

        var source = new double[length];
        Array.Copy(f0, start, source, 0, length);

        for (int i = 0; i < length; i++)
        {
            if (source[i] <= 0.0)
                continue;
            double total = 0.0;
            int count = 0;
            int from = Math.Max(0, i - radius);
            int to = Math.Min(length, i + radius + 1);
            for (int k = from; k < to; k++)
            {
                if (source[k] > 0.0)
                {
                    total += source[k];
                    count++;
                }
            }
            if (count > 0)
                f0[start + i] = total / count;
        }
    }
}
