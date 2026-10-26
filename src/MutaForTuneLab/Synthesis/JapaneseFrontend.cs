using System;
using System.Collections.Generic;
using System.Text;
using MutaForTuneLab.Assets;

namespace MutaForTuneLab.Synthesis;

/// <summary>
/// Japanese lyrics: one mora per note.
///
/// Sinsy's front end is a dictionary lookup on text, and that dictionary is keyed by kana,
/// so kana is the one form that must survive: it is the label text and, at the same time,
/// the key we read the phones from. The host may hand us either kana or the romaji it
/// romanised the lyric into, so romaji is converted once -- there is no path back from phones.
///
/// Three morae are special:
///   * ん has no vowel. It is the same unit Chinese sings for 恩: "en" after the zero-initial
///     onset "ge", which is why the bank lookup is given "ge" as its left context.
///   * っ is a closure. It has no bank unit and should not become a stray vowel; it is left
///     to score against "cl", finds nothing, and stays silent.
///   * ー holds the previous vowel instead of falling back to "a".
/// </summary>
public sealed class JapaneseFrontend : ILyricFrontend
{
    readonly KanaTable mKana;
    readonly string mFallback;
    string mLastVowel = "a";
    /// <summary>True when the previous mora was っ: this one begins with its closure.</summary>
    bool mPreviousClosure;

    public JapaneseFrontend(KanaTable kana, string fallback)
    {
        mKana = kana;
        mFallback = fallback;
    }

    public SungSyllable Analyze(string lyric)
    {
        string cleaned = Clean(lyric);
        if (cleaned.Length == 0)
            return Fallback(lyric);

        if (cleaned is "\u30FC" or "\uFF70")   // ー / ｰ
            return new SungSyllable(cleaned, new[] { mLastVowel }, null, mLastVowel);

        string? kana = KanaTable.IsKana(cleaned) ? cleaned : mKana.KanaFor(cleaned);
        if (kana is null || mKana.PhonemesFor(kana) is not { } tablePhones)
            return Fallback(lyric);

        // "k y a" -> "kya": one mora is one onset + one vowel, which is also the shape
        // Sinsy's labels take ("ky" + "a"), so the two never disagree on slot count.
        string joined = tablePhones.Replace(" ", string.Empty);
        int cut = 0;
        while (cut < joined.Length && "aiueo".IndexOf(joined[cut]) < 0)
            cut++;

        if (cut == joined.Length)
        {
            // っ is a closure: Sinsy gives its note the previous vowel and attaches the "cl"
            // to the *next* mora (measured: い っ か ん -> i i cl k a N). The bank has no
            // closure unit, so this note stays silent and the next one lengthens its onset.
            if (joined == "cl")
            {
                mPreviousClosure = true;
                return new SungSyllable(kana, new[] { mLastVowel }, null, "cl");
            }

            // Any other vowel-less phone: its own name is the key.
            if (JapanesePhones.TryMap(null, joined, out var bareOnset, out var bareFinal))
                return new SungSyllable(kana, new[] { joined }, bareOnset, bareFinal,
                    BankLeftFor(bareFinal));
            return new SungSyllable(kana, new[] { joined }, null, joined);
        }

        string? onset = cut > 0 ? joined[..cut] : null;
        string vowel = joined[cut..];
        mLastVowel = vowel[^1].ToString();

        // A mora that follows っ sings its own closure first: "cl" + onset + nucleus.
        bool closure = mPreviousClosure;
        mPreviousClosure = false;
        var phones = new List<string>(3);
        if (closure)
            phones.Add("cl");
        if (onset is not null)
            phones.Add(onset);
        phones.Add(vowel);
        var phoneArray = phones.ToArray();
        if (JapanesePhones.TryMap(onset, vowel, out var bankOnset, out var bankFinal))
            return new SungSyllable(kana, phoneArray, bankOnset, bankFinal, BankLeft(bankFinal, hasOnset: onset is not null));

        // No table entry: pass the pair through as written so the selector still scores
        // the nearest recorded unit.
        return new SungSyllable(kana, phoneArray, onset, vowel, BankLeft(vowel, hasOnset: onset is not null));
    }

    /// <summary>
    /// A bank final that is a bare vowel with no onset from the model is sung the way Chinese
    /// sings a zero-initial syllable: after "ge". ん is exactly that case -- the bank records
    /// it as 恩 (en after ge) -- and without the left context the selector refuses a bare
    /// vowel that follows another vowel.
    /// </summary>
    static string? BankLeft(string bankFinal, bool hasOnset)
        => !hasOnset && bankFinal.Length > 0 && "aiueo".IndexOf(bankFinal[0]) >= 0 ? "ge" : null;

    static string? BankLeftFor(string bankFinal) => BankLeft(bankFinal, hasOnset: false);

    SungSyllable Fallback(string lyric)
    {
        MutaDiagnostics.Log($"[MUTA] lyric [{lyric}] is not singable by this voice, singing [{mFallback}]");
        if (JapanesePhones.TryMap(null, mFallback, out _, out var final))
            return new SungSyllable(mFallback, new[] { mFallback }, null, final, BankLeftFor(final));
        return new SungSyllable(mFallback, new[] { mFallback }, null, mFallback);
    }

    /// <summary>Letters and kana only: spaces, punctuation, digits cannot be sung.</summary>
    static string Clean(string lyric)
    {
        if (string.IsNullOrEmpty(lyric))
            return string.Empty;

        var kept = new StringBuilder(lyric.Length);
        foreach (char c in lyric)
            if (char.IsLetter(c))
                kept.Append(c);
        return kept.ToString().ToLowerInvariant();
    }
}
