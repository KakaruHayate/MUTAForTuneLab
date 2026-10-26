using System.Collections.Generic;

namespace MutaForTuneLab.Synthesis;

/// <summary>
/// One lyric, resolved into everything the rest of the chain needs -- once.
///
/// The three consumers read different fields and never re-derive each other's:
///   * the label front end sings <see cref="LabelText"/> (a pinyin syllable, or kana for Sinsy);
///   * the alignment check expects <see cref="ModelPhones"/>, in the order the labels emit them;
///   * the bank selects <see cref="BankInitial"/> + <see cref="BankFinal"/>, with
///     <see cref="BankLeft"/> replacing the previous phone when the model has no onset.
/// </summary>
public sealed record SungSyllable(
    string LabelText,
    IReadOnlyList<string> ModelPhones,
    string? BankInitial,
    string BankFinal,
    string? BankLeft = null)
{
    /// <summary>A note with no phones: silence.</summary>
    public bool IsRest => ModelPhones.Count == 0;

    /// <summary>The onset the labels will sing, or null for a bare vowel/nasal.</summary>
    public string? ModelInitial => ModelPhones.Count > 1 ? ModelPhones[0] : null;

    /// <summary>The nucleus the labels will sing.</summary>
    public string ModelFinal => ModelPhones.Count > 0 ? ModelPhones[^1] : "sil";

    public static SungSyllable Rest { get; } = new("sil", [], null, "sil");
}

/// <summary>
/// Turns one lyric into a syllable. The host hands us either the written text or a
/// pronunciation it filled in itself (Han to pinyin, kana to romaji), so an implementation
/// must accept both forms and be idempotent: text already in its target form passes through.
/// </summary>
public interface ILyricFrontend
{
    SungSyllable Analyze(string lyric);
}
