using System;
using System.Collections.Generic;

namespace MutaForTuneLab.Synthesis;

/// <summary>
/// The breath heard in a rest: a noise burst, never pitched, that MUTA takes from the tail
/// PCM blocks its banks carry (`.inf` finals, 48 kHz float). Those recordings are what make
/// a breath sound like the same singer; a band-limited noise burst is the fallback for banks
/// that have none (weiyang).
///
/// The buffer is normalised per note to unit RMS and mixed by the caller at a level relative
/// to the singing, so the breath keeps MUTA's measured ratio (~-13 dB) whatever the bank's
/// absolute level is.
/// </summary>
public static class BreathRenderer
{
    /// <summary>Breath level relative to the singing, as measured on MUTA (0.011 / 0.048).</summary>
    public const double LevelRatio = 0.224;

    /// <summary>One breath's amplitude starts over this many seconds.</summary>
    const double AttackSeconds = 0.030;
    /// <summary>and fades over the last fraction of its length.</summary>
    const double ReleaseFraction = 0.45;

    /// <summary>
    /// Builds one block's breath track: a float buffer the length of the block, zero except
    /// where a breath note sits. Every note is normalised to RMS 1.
    /// </summary>
    public static float[] Build(
        IReadOnlyList<RenderNote> notes, int totalSamples, int samplesPerFrame,
        IReadOnlyList<float[]>? finals, ulong seed)
    {
        var buffer = new float[Math.Max(0, totalSamples)];
        if (samplesPerFrame <= 0 || buffer.Length == 0)
            return buffer;

        int breathIndex = 0;
        foreach (var note in notes)
        {
            if (!note.Breath)
                continue;

            int start = note.StartFrame * samplesPerFrame;
            int length = note.Frames * samplesPerFrame;
            if (start >= buffer.Length || length <= 0)
                continue;
            length = Math.Min(length, buffer.Length - start);

            var random = new XorShift(Hash(seed, breathIndex, note.StartFrame));
            var samples = Source(finals, ref random, length);
            Shape(samples);
            ApplyEnvelope(samples);
            Normalize(samples);
            Add(buffer, start, samples);
            breathIndex++;
        }
        return buffer;
    }

    static float[] Source(IReadOnlyList<float[]>? finals, ref XorShift random, int length)
    {
        if (finals is { Count: > 0 })
        {
            // A contiguous window of a real final block: the breath keeps its recorded
            // spectrum. Only a block shorter than the note is stretched.
            var source = finals[random.Next(finals.Count)];
            if (source.Length >= length)
            {
                int from = random.Next(source.Length - length + 1);
                var window = new float[length];
                Array.Copy(source, from, window, 0, length);
                return window;
            }
            if (source.Length > 1)
                return Resample(source, length);
        }
        return BandNoise(length, ref random);
    }

    /// <summary>
    /// The breath's tone: the raw finals run darker than a real intake of air (their energy
    /// sits in 0.5-2 kHz), while MUTA's measured breaths peak in 2-6 kHz and carry almost
    /// nothing below 500 Hz. A high-pass and a presence shelf put the sample in that range.
    /// </summary>
    static void Shape(float[] samples)
    {
        const double rate = 48000.0;
        var high = Biquad.HighPass(500.0, rate);
        var shelf = Biquad.HighShelf(2500.0, rate, 6.0);
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)shelf.Process(high.Process(samples[i]));
    }

    static float[] Resample(float[] source, int length)
    {
        var result = new float[length];
        int last = source.Length - 1;
        for (int i = 0; i < length; i++)
        {
            double position = (double)last * i / Math.Max(1, length - 1);
            int index = (int)position;
            double fraction = position - index;
            double a = source[Math.Min(index, last)];
            double b = source[Math.Min(index + 1, last)];
            result[i] = (float)(a + (b - a) * fraction);
        }
        return result;
    }

    /// <summary>White noise through a 2-8 kHz band, for banks without finals.</summary>
    static float[] BandNoise(int length, ref XorShift random)
    {
        var result = new float[length];
        const double rate = 48000.0;
        var high = Biquad.HighPass(1600.0, rate);
        var low = Biquad.LowPass(8000.0, rate);
        for (int i = 0; i < length; i++)
        {
            double white = random.NextDouble() * 2.0 - 1.0;
            result[i] = (float)low.Process(high.Process(white));
        }
        return result;
    }

    static void ApplyEnvelope(float[] samples)
    {
        int length = samples.Length;
        int attack = Math.Min(length, (int)Math.Round(AttackSeconds * 48000.0));
        int release = Math.Min(length - attack, (int)(length * ReleaseFraction));
        int sustainEnd = length - release;
        for (int i = 0; i < length; i++)
        {
            double gain = 1.0;
            if (i < attack && attack > 0)
                gain = (double)i / attack;
            else if (i >= sustainEnd && release > 0)
                gain = (double)(length - 1 - i) / release;
            samples[i] *= (float)Math.Clamp(gain, 0.0, 1.0);
        }
    }

    static void Normalize(float[] samples)
    {
        double sum = 0.0;
        for (int i = 0; i < samples.Length; i++)
            sum += samples[i] * samples[i];
        double rms = samples.Length > 0 ? Math.Sqrt(sum / samples.Length) : 0.0;
        if (rms < 1e-9)
            return;
        float scale = (float)(1.0 / rms);
        for (int i = 0; i < samples.Length; i++)
            samples[i] *= scale;
    }

    static void Add(float[] destination, int start, float[] source)
    {
        for (int i = 0; i < source.Length && start + i < destination.Length; i++)
            destination[start + i] += source[i];
    }

    /// <summary>Stable per-note seed: the same project renders the same breaths.</summary>
    static int Hash(ulong seed, int index, int frame)
    {
        unchecked
        {
            ulong value = seed ^ (ulong)index * 0x9E3779B97F4A7C15UL ^ (ulong)frame * 0xBF58476D1CE4E5B9UL;
            value ^= value >> 33;
            value *= 0xFF51AFD7ED558CCDUL;
            value ^= value >> 33;
            return (int)(value & 0x7FFFFFFF);
        }
    }

    /// <summary>Small deterministic PRNG: the same seed must give the same breath everywhere.</summary>
    struct XorShift
    {
        ulong mState;

        public XorShift(int seed) => mState = (ulong)(uint)seed + 0x9E3779B97F4A7C15UL;

        public ulong NextUInt64()
        {
            ulong x = mState;
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            mState = x;
            return x;
        }

        public int Next(int exclusiveMaximum)
            => exclusiveMaximum <= 0 ? 0 : (int)(NextUInt64() % (ulong)exclusiveMaximum);

        public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);
    }

    /// <summary>RBJ biquad, used only for the fallback noise band.</summary>
    struct Biquad
    {
        readonly double mB0, mB1, mB2, mA1, mA2;
        double mZ1, mZ2;

        Biquad(double b0, double b1, double b2, double a1, double a2)
        {
            mB0 = b0; mB1 = b1; mB2 = b2; mA1 = a1; mA2 = a2;
            mZ1 = 0.0; mZ2 = 0.0;
        }

        public double Process(double input)
        {
            double output = mB0 * input + mZ1;
            mZ1 = mB1 * input - mA1 * output + mZ2;
            mZ2 = mB2 * input - mA2 * output;
            return output;
        }

        public static Biquad LowPass(double frequency, double rate)
        {
            double w0 = 2.0 * Math.PI * frequency / rate;
            double cos = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2.0 * 0.70710678);
            double a0 = 1.0 + alpha;
            return new Biquad((1.0 - cos) / 2.0 / a0, (1.0 - cos) / a0, (1.0 - cos) / 2.0 / a0,
                -2.0 * cos / a0, (1.0 - alpha) / a0);
        }

        public static Biquad HighPass(double frequency, double rate)
        {
            double w0 = 2.0 * Math.PI * frequency / rate;
            double cos = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2.0 * 0.70710678);
            double a0 = 1.0 + alpha;
            return new Biquad((1.0 + cos) / 2.0 / a0, -(1.0 + cos) / a0, (1.0 + cos) / 2.0 / a0,
                -2.0 * cos / a0, (1.0 - alpha) / a0);
        }

        /// <summary>RBJ high shelf: boosts everything above <paramref name="frequency"/>.</summary>
        public static Biquad HighShelf(double frequency, double rate, double gainDecibels)
        {
            double a = Math.Pow(10.0, gainDecibels / 40.0);
            double w0 = 2.0 * Math.PI * frequency / rate;
            double cos = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2.0 * 0.70710678);
            double root = 2.0 * Math.Sqrt(a) * alpha;
            double a0 = (a + 1.0) - (a - 1.0) * cos + root;
            return new Biquad(
                a * ((a + 1.0) + (a - 1.0) * cos + root) / a0,
                -2.0 * a * ((a - 1.0) + (a + 1.0) * cos) / a0,
                a * ((a + 1.0) + (a - 1.0) * cos - root) / a0,
                2.0 * ((a - 1.0) - (a + 1.0) * cos) / a0,
                ((a + 1.0) - (a - 1.0) * cos - root) / a0);
        }
    }
}
