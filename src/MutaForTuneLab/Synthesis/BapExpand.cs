using System;

namespace MutaForTuneLab.Synthesis;

/// <summary>
/// Expands the acoustic model's 26-band aperiodicity into a per-bin spectrum.
///
/// WORLD works on a 1025-bin spectrum; the model predicts 26 bands. The band widths
/// are not uniform and the last two are deliberately lopsided (300 then 125), so the
/// table is copied rather than computed. The conversion from decibels follows the
/// engine's own curve, which is not a plain 10^(dB/20): the 1.6 scale and the -0.015
/// offset put a fully periodic frame at 0.001 instead of 0.
/// </summary>
public static class BapExpand
{
    /// <summary>Bins per band; sums to 1025.</summary>
    public static readonly int[] BandRunLengths =
    [
        2, 3, 4, 4, 4, 5, 5, 6, 6, 7, 8, 9, 10, 12, 14, 16,
        19, 24, 29, 37, 49, 68, 99, 160, 300, 125,
    ];

    public const int BandCount = 26;
    public const int BinCount = 1025;

    public const double WeightScale = 1.6;
    public const double WeightOffset = -0.015;
    public const double WeightMinimum = 0.001;
    public const double WeightMaximum = 1.0;

    /// <summary>Repeats each band's value across its bins.</summary>
    public static double[][] Expand(double[][] bap)
    {
        int frames = bap.Length;
        if (frames > 0 && bap[0].Length != BandCount)
            throw new ArgumentException($"BAP must hold {BandCount} bands, got {bap[0].Length}");

        var expanded = new double[frames][];
        for (int f = 0; f < frames; f++)
        {
            var row = new double[BinCount];
            int bin = 0;
            for (int band = 0; band < BandCount; band++)
            {
                int run = BandRunLengths[band];
                double value = bap[f][band];
                for (int k = 0; k < run; k++)
                    row[bin++] = value;
            }
            expanded[f] = row;
        }
        return expanded;
    }

    /// <summary>Decibels to linear aperiodicity, without the engine's curve.</summary>
    public static double ToLinear(double decibels) => Math.Pow(10.0, decibels / 20.0);

    /// <summary>Decibels to the weight WORLD's synthesis expects.</summary>
    public static double ToWeight(double decibels)
    {
        double value = WeightScale * Math.Pow(10.0, decibels / 20.0) + WeightOffset;
        return Math.Clamp(value, WeightMinimum, WeightMaximum);
    }

    /// <summary>One row, expanded and converted in a single pass.</summary>
    public static void ExpandRowToWeights(ReadOnlySpan<double> band, Span<double> destination)
    {
        if (band.Length != BandCount)
            throw new ArgumentException($"BAP row must hold {BandCount} bands");
        if (destination.Length != BinCount)
            throw new ArgumentException($"destination must hold {BinCount} bins");

        int bin = 0;
        for (int b = 0; b < BandCount; b++)
        {
            double weight = ToWeight(band[b]);
            int run = BandRunLengths[b];
            for (int k = 0; k < run; k++)
                destination[bin++] = weight;
        }
    }
}
