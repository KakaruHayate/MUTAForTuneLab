namespace MutaForTuneLab.Synthesis;

/// <summary>
/// Maps a Sinsy phone pair onto phones the MUTA bank can be asked for.
///
/// Japanese sings through the Chinese bank: Sinsy supplies the labels, durations and F0, and
/// the bank supplies the timbre. Its phone names differ, so each mora is rewritten before the
/// assembler sees it. The rules are the ones the prototype validated -- see
/// docs/muta-ja-zh-phone-map-zh.md, which also records why ordinary e becomes ei and why a
/// palatalised onset pairs with a different vowel rather than the same one.
/// </summary>
public static class JapanesePhones
{
    /// <summary>Onset rewrite: Japanese consonant to the bank's nearest.</summary>
    static readonly Dictionary<string, string> sOnsets = new(StringComparer.Ordinal)
    {
        ["ky"] = "k", ["gy"] = "g", ["sh"] = "x", ["ch"] = "q", ["j"] = "j",
        ["ny"] = "n", ["hy"] = "h", ["by"] = "b", ["py"] = "p", ["my"] = "m",
        ["ry"] = "r", ["ty"] = "t", ["dy"] = "d", ["ts"] = "c",
        ["b"] = "b", ["d"] = "d", ["f"] = "f", ["g"] = "g", ["h"] = "h",
        ["k"] = "k", ["m"] = "m", ["n"] = "n", ["p"] = "p", ["r"] = "r",
        ["s"] = "s", ["t"] = "t", ["w"] = "w", ["y"] = "y", ["z"] = "z",
    };

    /// <summary>Vowel rewrite when the onset is not palatalised.</summary>
    static readonly Dictionary<string, string> sPlainVowels = new(StringComparer.Ordinal)
    {
        ["a"] = "a", ["i"] = "i", ["u"] = "u", ["e"] = "ei", ["o"] = "o", ["v"] = "v",
    };

    /// <summary>Vowel rewrite after a palatalised onset: the bank's nearest vowel colour.</summary>
    static readonly Dictionary<string, string> sPalatalizedVowels = new(StringComparer.Ordinal)
    {
        ["a"] = "ia", ["i"] = "i", ["u"] = "v", ["e"] = "ie", ["o"] = "iu",
    };

    static readonly HashSet<string> sPalatalized = new(StringComparer.Ordinal)
    {
        "ky", "gy", "sh", "ch", "j", "ny", "hy", "by", "py", "my", "ry", "ty", "dy",
    };

    /// <summary>
    /// Rewrites one mora's phones. Returns false when the pair cannot be sung from the bank,
    /// which the caller answers by skipping the note rather than inventing a sound.
    /// </summary>
    public static bool TryMap(string? onset, string vowel, out string? bankOnset, out string bankFinal)
    {
        bankOnset = null;
        bankFinal = "a";

        string vowelKey = vowel.ToLowerInvariant();
        if (vowelKey.Length == 0 || vowelKey == "n" || vowelKey == "m")
        {
            // A syllabic nasal: the bank's own nasal coda is the closest thing it has.
            bankFinal = "n";
            return true;
        }

        string? mappedVowel;
        if (onset is { Length: > 0 } rawOnset)
        {
            string onsetKey = rawOnset.ToLowerInvariant();
            if (!sOnsets.TryGetValue(onsetKey, out string? mappedOnset))
                return false;
            bankOnset = mappedOnset;
            mappedVowel = sPalatalized.Contains(onsetKey)
                ? sPalatalizedVowels.GetValueOrDefault(vowelKey)
                : sPlainVowels.GetValueOrDefault(vowelKey);
        }
        else
        {
            mappedVowel = sPlainVowels.GetValueOrDefault(vowelKey);
        }

        if (mappedVowel is null)
            return false;
        bankFinal = mappedVowel;
        return true;
    }
}
