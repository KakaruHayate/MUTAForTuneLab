using System;

namespace MutaForTuneLab.Synthesis;

/// <summary>
/// A note whose phonemes the user pinned: the editor shows these, so the engine must sing
/// them instead of its own G2P guess. Carried as plain values so the synthesis layer stays
/// free of the host SDK (the session reads the snapshot and fills this in).
///
/// Durations are seconds, weights are the host's stretch weights, and the offset is the
/// junction between the two lists (seconds from the note head).
/// </summary>
public sealed record PinnedPhones(
    string[] Leading,
    double[] LeadingDurations,
    double[] LeadingWeights,
    string[] Body,
    double[] BodyDurations,
    double[] BodyWeights,
    double BodyOffset)
{
    public bool IsEmpty => Leading.Length == 0 && Body.Length == 0;

    /// <summary>The onset the bank should select for, if the leading list has one.</summary>
    public string? Onset => Leading.Length > 0 ? Leading[^1] : null;

    /// <summary>The nucleus the bank should select for.</summary>
    public string? Nucleus => Body.Length > 0 ? Body[0] : null;
}
