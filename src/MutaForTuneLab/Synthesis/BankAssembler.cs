using System;
using System.Collections.Generic;

namespace MutaForTuneLab.Synthesis;

/// <summary>Cross-slot state carried through the assembly loop.</summary>
public readonly record struct NoteAssemblyState(
    bool PreviousWasRest = false,
    int PendingVowelOverlap = 0,
    int PreviousConsonantRightExtension = 0,
    bool HasPreviousConsonant = false);

/// <summary>Resolved branch geometry for one slot.</summary>
public sealed class NoteAssemblyPlan
{
    public required string Branch { get; init; }
    public required int SlotClass { get; init; }
    public required int Duration { get; init; }
    public required int TargetCore { get; init; }
    public required int LeftExtension { get; init; }
    public required int RightExtension { get; init; }
    public required int HeadOverlap { get; init; }
    public required int PreviousOverlap { get; init; }
    public required int PendingVowelOverlap { get; init; }
    public required NoteAssemblyState NextState { get; init; }
}

/// <summary>One stretched bank context: [left extension | core | right extension].</summary>
public sealed class NoteBlock
{
    public required double[][] Mgc { get; init; }
    public required double[][] Bap { get; init; }
    public int Frames => Mgc.Length;
    public int LeftExtension { get; init; }
    public int CoreFrames { get; init; }
    public int RightExtension { get; init; }
}

/// <summary>
/// Per-slot assembly of the acoustic parameters: pick a recorded context, stretch its
/// three parts into the space the note needs, and blend it into the song buffers.
///
/// The stretching is where precision matters: the engine accumulates frame positions
/// in <c>float</c>, so a double accumulator would drift by a frame after a couple of
/// dozen frames and shift every later boundary. The same applies to the split ratios,
/// which are multiplied as float before being truncated to frame counts.
/// </summary>
public static class BankAssembler
{
    public const double RestMgcLogGain = -9.0;
    public const int MaxOverlapFrames = 20;
    public const double HeadRatioLimit = 0.2;
    public const double TailRatioLimit = 0.8;
    public const double LeftExtensionRatio = 0.2;

    // ---- geometry ----------------------------------------------------------

    /// <summary>Half of the next note, capped at 20 frames.</summary>
    public static int RightOverlapFrames(int nextDuration)
        => Math.Min(nextDuration / 2, MaxOverlapFrames);

    /// <summary>20% of the previous note, truncated.</summary>
    public static int LeftExtensionFrames(int previousDuration)
        => (int)(previousDuration * LeftExtensionRatio);

    /// <summary>Consonant right extension; it never exceeds the source suffix.</summary>
    public static int ConsonantRightExtensionFrames(int nextDuration, int sourceSuffix, int nextSlotClass)
    {
        int requested = (int)(nextDuration * LeftExtensionRatio);
        if (sourceSuffix < requested)
            return nextSlotClass >= 1 ? sourceSuffix : 0;
        return requested;
    }

    /// <summary>Head/tail boundary ratios from the five state durations, with the engine's clamps.</summary>
    public static (double Head, double Tail) StateBoundaryRatios(ReadOnlySpan<long> states)
    {
        if (states.Length != 5)
            throw new ArgumentException("expected exactly five state durations");
        double total = 0;
        for (int i = 0; i < 5; i++)
            total += states[i];
        if (total <= 0)
            throw new ArgumentException("state durations must sum to a positive value");

        double head = (states[0] + states[1]) / total;
        double tail = (states[0] + states[1] + states[2] + states[3]) / total;
        if (HeadRatioLimit < head)
            head = HeadRatioLimit;
        if (tail < TailRatioLimit)
            tail = TailRatioLimit;
        return (head, tail);
    }

    /// <summary>Split a source extent into head/middle/tail frames (float32 products).</summary>
    public static (int Head, int Middle, int Tail) ThreePartSplit(int sourceTotal, double headRatio, double tailRatio)
    {
        if (sourceTotal <= 0)
            throw new ArgumentException("source extent must be positive");

        int head = (int)((float)sourceTotal * (float)headRatio);
        int middle = (int)((float)sourceTotal * (float)tailRatio - (float)head);
        int tail = (sourceTotal - head) - middle;
        if (middle < 0 || tail < 0)
            throw new ArgumentException($"state ratios produced a negative split for {sourceTotal} frames");
        return (head, middle, tail);
    }

    /// <summary>(1 + cos(pi t / n)) / 2, computed in double and narrowed to float.</summary>
    public static float[] FadeOutWeights(int length)
    {
        var weights = new float[Math.Max(0, length)];
        for (int i = 0; i < length; i++)
            weights[i] = (float)((1.0 + Math.Cos(Math.PI * i / length)) / 2.0);
        return weights;
    }

    /// <summary>1 − fadeOut, subtracted in float like the engine does.</summary>
    public static float[] FadeInWeights(int length)
    {
        var fadeOut = FadeOutWeights(length);
        for (int i = 0; i < fadeOut.Length; i++)
            fadeOut[i] = 1.0f - fadeOut[i];
        return fadeOut;
    }

    // ---- resampling --------------------------------------------------------

    /// <summary>
    /// FUN_1002AE70: stretch a frame range to a new length. Positions accumulate in
    /// float; compression replicates the nearest lower frame, expansion interpolates
    /// on a hundredth-of-a-frame grid.
    /// </summary>
    public static double[][] ResampleFrames(double[][] values, int target)
    {
        target = Math.Max(1, target);
        int sourceFrames = values.Length;
        int width = values.Length > 0 ? values[0].Length : 0;

        if (sourceFrames == target)
            return Copy(values);
        if (sourceFrames < 2)
        {
            var repeated = new double[target][];
            for (int i = 0; i < target; i++)
                repeated[i] = (double[])values[0].Clone();
            return repeated;
        }

        float ratio = (float)sourceFrames / (float)target;
        float position = 0.2f;
        var result = new double[target][];

        for (int destination = 0; destination < target; destination++)
        {
            int current = Math.Min((int)position, sourceFrames - 1);
            bool interpolate = ratio < 1.0f && sourceFrames > (position + 1.0f);

            if (!interpolate || current == sourceFrames - 1)
            {
                result[destination] = (double[])values[current].Clone();
            }
            else
            {
                int fixedPart = (int)(position * 100.0f) % 100;
                var row = new double[width];
                for (int k = 0; k < width; k++)
                {
                    double delta = (float)((values[current + 1][k] - values[current][k]) / 100.0 * fixedPart);
                    row[k] = values[current][k] + delta;
                }
                result[destination] = row;
            }

            position = (float)(position + ratio);
        }
        return result;
    }

    static double[][] Copy(double[][] values)
    {
        var copy = new double[values.Length][];
        for (int i = 0; i < values.Length; i++)
            copy[i] = (double[])values[i].Clone();
        return copy;
    }

    // ---- planning ----------------------------------------------------------

    public static NoteAssemblyPlan Plan(
        int slotClass, int duration, int previousDuration, int nextDuration,
        int nextSlotClass, int sourceSuffix, NoteAssemblyState state, bool leftContextNonZero = true)
    {
        if (slotClass < 0)
        {
            return new NoteAssemblyPlan
            {
                Branch = "rest", SlotClass = slotClass, Duration = duration,
                TargetCore = duration, LeftExtension = 0, RightExtension = 0,
                HeadOverlap = 0, PreviousOverlap = 0, PendingVowelOverlap = 0,
                NextState = new NoteAssemblyState(true, 0, 0, false),
            };
        }

        if (slotClass == 0)
        {
            int leftExtension = !state.PreviousWasRest && leftContextNonZero
                ? LeftExtensionFrames(previousDuration)
                : 0;
            int rightExtension = ConsonantRightExtensionFrames(nextDuration, sourceSuffix, nextSlotClass);
            int previousOverlap = state.PendingVowelOverlap;
            int headOverlap = leftExtension > 0 ? leftExtension : previousOverlap;

            return new NoteAssemblyPlan
            {
                Branch = "consonant", SlotClass = slotClass, Duration = duration,
                TargetCore = duration, LeftExtension = leftExtension, RightExtension = rightExtension,
                HeadOverlap = headOverlap, PreviousOverlap = previousOverlap, PendingVowelOverlap = 0,
                NextState = new NoteAssemblyState(false, 0, rightExtension, true),
            };
        }

        int pending = nextSlotClass == 0 ? RightOverlapFrames(nextDuration) : 0;
        int targetCore = duration + pending;
        int overlapFromConsonant = state.HasPreviousConsonant ? state.PreviousConsonantRightExtension : 0;

        return new NoteAssemblyPlan
        {
            Branch = "vowel", SlotClass = slotClass, Duration = duration,
            TargetCore = targetCore, LeftExtension = 0, RightExtension = 0,
            HeadOverlap = overlapFromConsonant, PreviousOverlap = overlapFromConsonant,
            PendingVowelOverlap = pending,
            NextState = new NoteAssemblyState(false, pending, 0, false),
        };
    }

    // ---- block building ----------------------------------------------------

    /// <summary>Stretches one bank context into a [left | core | right] block.</summary>
    public static NoteBlock BuildBlock(
        double[][] mgc, double[][] bap,
        int sourcePrefix, int sourceCore, int sourceSuffix,
        int targetCore, int leftExtension, int rightExtension,
        long[]? states = null)
    {
        if (mgc.Length != bap.Length)
            throw new ArgumentException("MGC and BAP frame counts differ");
        if (sourcePrefix + sourceCore + sourceSuffix != mgc.Length)
            throw new ArgumentException("prefix + core + suffix does not match the context length");
        if (targetCore < 1)
            throw new ArgumentException("target core must be at least one frame");

        int mgcWidth = mgc.Length > 0 ? mgc[0].Length : 0;
        int bapWidth = bap.Length > 0 ? bap[0].Length : 0;
        int frames = leftExtension + targetCore + rightExtension;

        var blockMgc = NewBuffer(frames, mgcWidth);
        var blockBap = NewBuffer(frames, bapWidth);

        var coreMgc = Slice(mgc, sourcePrefix, sourcePrefix + sourceCore);
        var coreBap = Slice(bap, sourcePrefix, sourcePrefix + sourceCore);

        if (states is null)
        {
            CopyInto(blockMgc, leftExtension, ResampleFrames(coreMgc, targetCore));
            CopyInto(blockBap, leftExtension, ResampleFrames(coreBap, targetCore));
        }
        else
        {
            var (headRatio, tailRatio) = StateBoundaryRatios(states);
            if (targetCore < sourceCore)
            {
                // A core shorter than the source collapses whole; the state ratios
                // only apply when there is room to grow.
                CopyInto(blockMgc, leftExtension, ResampleFrames(coreMgc, targetCore));
                CopyInto(blockBap, leftExtension, ResampleFrames(coreBap, targetCore));
            }
            else
            {
                var (head, middle, tail) = ThreePartSplit(sourceCore, headRatio, tailRatio);
                int targetMiddle = targetCore - head - tail;
                if (targetMiddle < 0)
                    throw new ArgumentException(
                        $"target core {targetCore} is shorter than the fixed head and tail ({head} + {tail})");

                CopyInto(blockMgc, leftExtension, Slice(coreMgc, 0, head));
                CopyInto(blockBap, leftExtension, Slice(coreBap, 0, head));

                if (targetMiddle > 0)
                {
                    CopyInto(blockMgc, leftExtension + head,
                             ResampleFrames(Slice(coreMgc, head, head + middle), targetMiddle));
                    CopyInto(blockBap, leftExtension + head,
                             ResampleFrames(Slice(coreBap, head, head + middle), targetMiddle));
                }

                CopyInto(blockMgc, leftExtension + head + targetMiddle,
                         Slice(coreMgc, head + middle, head + middle + tail));
                CopyInto(blockBap, leftExtension + head + targetMiddle,
                         Slice(coreBap, head + middle, head + middle + tail));
            }
        }

        if (sourcePrefix > 0 && leftExtension > 0)
        {
            CopyInto(blockMgc, 0, ResampleFrames(Slice(mgc, 0, sourcePrefix), leftExtension));
            CopyInto(blockBap, 0, ResampleFrames(Slice(bap, 0, sourcePrefix), leftExtension));
        }
        if (sourceSuffix > 0 && rightExtension > 0)
        {
            int start = sourcePrefix + sourceCore;
            int offset = leftExtension + targetCore;
            CopyInto(blockMgc, offset, ResampleFrames(Slice(mgc, start, start + sourceSuffix), rightExtension));
            CopyInto(blockBap, offset, ResampleFrames(Slice(bap, start, start + sourceSuffix), rightExtension));
        }

        return new NoteBlock
        {
            Mgc = blockMgc, Bap = blockBap,
            LeftExtension = leftExtension, CoreFrames = targetCore, RightExtension = rightExtension,
        };
    }

    // ---- writing -----------------------------------------------------------

    /// <summary>Fades a block range into an existing range in place.</summary>
    public static void CrossfadeRangeInto(
        double[][] destination, double[][] source,
        int destinationStart, int sourceStart, int frames)
    {
        if (frames <= 0)
            return;

        var fadeOut = FadeOutWeights(frames);
        var fadeIn = FadeInWeights(frames);
        for (int i = 0; i < frames; i++)
        {
            var destinationRow = destination[destinationStart + i];
            var sourceRow = source[sourceStart + i];
            // An unwritten frame has a log gain of 0 -- amplitude 1, not silence. The first
            // frame of the fade would otherwise keep it (old weight 1, new weight 0), and a
            // consonant slot that begins on a rest would leave one full-scale frame behind:
            // the burst at the start of a phrase. "Nothing written" is not "old content", so
            // the source takes the frame outright.
            if (destinationRow[0] == 0.0)
            {
                for (int k = 0; k < destinationRow.Length; k++)
                    destinationRow[k] = sourceRow[k];
                continue;
            }
            double oldWeight = fadeOut[i];
            double newWeight = fadeIn[i];
            for (int k = 0; k < destinationRow.Length; k++)
                destinationRow[k] = destinationRow[k] * oldWeight + sourceRow[k] * newWeight;
        }
    }

    /// <summary>Copies a block range into the song buffers.</summary>
    public static void CopyBlockRangeInto(
        double[][] mgcGlobal, double[][] bapGlobal, NoteBlock block,
        int destinationStart, int sourceStart, int frames)
    {
        if (frames <= 0)
            return;
        for (int i = 0; i < frames; i++)
        {
            Array.Copy(block.Mgc[sourceStart + i], 0, mgcGlobal[destinationStart + i], 0, mgcGlobal[0].Length);
            Array.Copy(block.Bap[sourceStart + i], 0, bapGlobal[destinationStart + i], 0, bapGlobal[0].Length);
        }
    }

    /// <summary>Writes one block with the branch's overlap rule.</summary>
    public static void WriteNoteBlock(
        double[][] mgcGlobal, double[][] bapGlobal, NoteBlock block, int startFrame, NoteAssemblyPlan plan)
    {
        if (plan.Branch == "rest")
            return;
        if (startFrame < 0 || startFrame > mgcGlobal.Length)
            throw new ArgumentException("note start is outside the global buffers");

        // A consonant with a real left context fades into frames before its own start;
        // otherwise it consumes the overlap a preceding vowel reserved for it.
        int destinationStart;
        int overlap;
        if (plan.Branch == "consonant" && block.LeftExtension > 0)
        {
            destinationStart = startFrame - block.LeftExtension;
            overlap = block.LeftExtension;
        }
        else
        {
            destinationStart = startFrame;
            overlap = plan.PreviousOverlap;
        }
        if (destinationStart < 0)
            throw new ArgumentException("left extension starts before the global buffers");

        overlap = Math.Min(Math.Min(overlap, block.Frames),
                           Math.Min(mgcGlobal.Length, bapGlobal.Length) - destinationStart);
        if (overlap > 0)
        {
            CrossfadeRangeInto(mgcGlobal, block.Mgc, destinationStart, 0, overlap);
            CrossfadeRangeInto(bapGlobal, block.Bap, destinationStart, 0, overlap);
        }

        int copyFrames = Math.Min(block.Frames - overlap,
            Math.Min(mgcGlobal.Length - destinationStart - overlap,
                     bapGlobal.Length - destinationStart - overlap));
        CopyBlockRangeInto(mgcGlobal, bapGlobal, block,
            destinationStart + overlap, overlap, Math.Max(0, copyFrames));
    }

    /// <summary>An explicit rest writes the log-gain sentinel into every frame.</summary>
    public static void FillRestMgc(double[][] mgcGlobal, int startFrame, int duration)
    {
        if (duration < 0)
            throw new ArgumentException("rest duration must be non-negative");
        if (startFrame < 0 || startFrame + duration > mgcGlobal.Length)
            throw new ArgumentException("rest exceeds the MGC buffer");
        for (int i = 0; i < duration; i++)
            mgcGlobal[startFrame + i][0] = RestMgcLogGain;
    }

    // ---- helpers -----------------------------------------------------------

    static double[][] NewBuffer(int frames, int width)
    {
        var buffer = new double[frames][];
        for (int i = 0; i < frames; i++)
            buffer[i] = new double[width];
        return buffer;
    }

    static double[][] Slice(double[][] values, int start, int end)
    {
        int count = Math.Max(0, end - start);
        var slice = new double[count][];
        for (int i = 0; i < count; i++)
            slice[i] = values[start + i];
        return slice;
    }

    static void CopyInto(double[][] destination, int offset, double[][] source)
    {
        for (int i = 0; i < source.Length && offset + i < destination.Length; i++)
            Array.Copy(source[i], 0, destination[offset + i], 0, destination[offset + i].Length);
    }
}
