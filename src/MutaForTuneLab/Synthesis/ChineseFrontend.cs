using System;
using System.Collections.Generic;
using System.Text;
using MutaForTuneLab.Assets;

namespace MutaForTuneLab.Synthesis;

/// <summary>
/// Chinese lyrics: one pinyin syllable per note.
///
/// The host usually hands us the syllable already (its editor can fill the pronunciation
/// with pinyin), but it may also hand us Han. Both are accepted; Han goes through the
/// install's dictionary. A syllable is only sung if the bank actually records its onset and
/// final -- the native engine does not validate, and a phone it cannot place takes the
/// process down.
/// </summary>
public sealed class ChineseFrontend : ILyricFrontend
{
    readonly PinyinDictionary? mDictionary;
    readonly HashSet<string>? mPhones;
    readonly string mFallback;

    public ChineseFrontend(PinyinDictionary? dictionary, HashSet<string>? phones, string fallback)
    {
        mDictionary = dictionary;
        mPhones = phones;
        mFallback = fallback;
    }

    public SungSyllable Analyze(string lyric)
    {
        string cleaned = Clean(lyric);
        if (cleaned.Length == 0)
            return Fallback(lyric);

        // The host romanises 嗯 as "n", which is not a pinyin syllable: MUTA's own dictionary
        // reads 嗯 and 恩 as "en", the zero-initial nasal the bank records as ge + en.
        if (cleaned is "n" or "ng")
            cleaned = "en";

        if (mDictionary?.SyllableFor(cleaned) is { } fromDictionary && IsSingable(fromDictionary))
            return FromSyllable(fromDictionary);

        if (IsSingable(cleaned))
            return FromSyllable(cleaned);

        // A bare consonant has no final; give it the neutral vowel rather than failing.
        string withVowel = cleaned + "a";
        if (IsSingable(withVowel))
        {
            MutaDiagnostics.Log($"[MUTA] lyric [{lyric}] has no vowel, singing [{withVowel}]");
            return FromSyllable(withVowel);
        }

        return Fallback(lyric);
    }

    static SungSyllable FromSyllable(string syllable)
    {
        if (LabelBuilder.IsBreak(syllable))
            return SungSyllable.Rest;

        var split = LabelBuilder.SplitSyllable(syllable);
        if (split is null)
            return SungSyllable.Rest;

        var (initial, final) = split.Value;
        var phones = initial.Length > 0 ? new[] { initial, final } : new[] { final };
        // Chinese sings the bank's own names, so the model pair is the bank pair.
        return new SungSyllable(syllable, phones,
            initial.Length > 0 ? initial : null, final);
    }

    SungSyllable Fallback(string lyric)
    {
        MutaDiagnostics.Log($"[MUTA] lyric [{lyric}] is not singable by this voice, singing [{mFallback}]");
        return FromSyllable(mFallback);
    }

    /// <summary>Both parts of the syllable have to exist in the bank.</summary>
    bool IsSingable(string syllable)
    {
        if (syllable.Length == 0)
            return false;
        if (mPhones is null)
            return LabelBuilder.SplitSyllable(syllable) is { Final.Length: > 0 };

        if (mPhones.Contains(syllable))
            return true;
        if (LabelBuilder.SplitSyllable(syllable) is not { } parts)
            return false;
        bool onset = parts.Initial.Length == 0 || mPhones.Contains(parts.Initial);
        return onset && mPhones.Contains(parts.Final);
    }

    /// <summary>Letters only: spaces, punctuation and digits cannot be sung.</summary>
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
