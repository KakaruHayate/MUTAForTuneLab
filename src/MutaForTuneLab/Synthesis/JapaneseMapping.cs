using System;
using System.Collections.Generic;

namespace MutaForTuneLab.Synthesis;

/// <summary>
/// Japanese phones rewritten onto the phones the MUTA bank records.
///
/// The table describes the *sound*, not what Mandarin happens to allow: a bank records
/// phone pairs, and they cover more than standard pinyin syllables (yanxi's e group has
/// e after k, g, h, t, d, n, m, l -- syllables Mandarin does not have). So the mapping keeps
/// the Japanese consonant and the plain vowel, and lets the selector hard-join when a pair
/// was never recorded. Pronunciation first, mechanism second.
///
/// Two consequences of that rule:
///   * plain e and o stay e and o. Mapping them to "ie"/"uo" added a y/w glide that Japanese
///     does not have -- the source of "ye" and "wo";
///   * a palatalised mora keeps its own consonant (ky -> k) and only borrows the palatal
///     glide (a -> ia), instead of swapping the consonant for Mandarin's q/x.
/// </summary>
public static class JapanesePhones
{
    /// <summary>Japanese onset to the bank's nearest onset phone.</summary>
    static readonly Dictionary<string, string?> sOnset = new(StringComparer.Ordinal)
    {
        [""] = null,
        ["k"] = "k", ["g"] = "g", ["s"] = "s", ["z"] = "z",
        ["t"] = "t", ["d"] = "d", ["n"] = "n", ["h"] = "h",
        ["b"] = "b", ["p"] = "p", ["m"] = "m", ["r"] = "l",
        ["y"] = "y", ["w"] = "w", ["f"] = "f", ["j"] = "j",
        // Affricates and the palatal fricative have real Mandarin counterparts.
        ["ts"] = "c", ["ch"] = "q", ["sh"] = "x",
        // Palatalised onsets keep their own consonant; only the vowel takes the glide.
        ["ky"] = "k", ["gy"] = "g", ["ny"] = "n", ["hy"] = "x",
        ["by"] = "b", ["py"] = "p", ["my"] = "m", ["ry"] = "l",
        ["ty"] = "t", ["dy"] = "d",
    };

    /// <summary>Onsets whose vowel carries a y-glide in Mandarin.</summary>
    static readonly HashSet<string> sPalatal = new(StringComparer.Ordinal)
    {
        "ky", "gy", "ny", "hy", "by", "py", "my", "ry", "ty", "dy", "sh", "ch", "j",
    };

    /// <summary>A palatalised mora borrows the Mandarin final that starts with the glide.</summary>
    static string PalatalFinal(string vowel) => vowel switch
    {
        "a" => "ia",
        "u" => "iu",
        "o" => "iao",
        "e" => "ie",
        _ => "i",
    };

    /// <summary>
    /// The table can be edited without a rebuild: ja-zh-phone-map.tsv ships beside the plugin.
    /// Lines are "jp_phone &lt;TAB&gt; bank_onset &lt;TAB&gt; bank_final"; an empty onset means
    /// a bare vowel. Missing file or missing key falls back to the rules below.
    /// </summary>
    static readonly Dictionary<string, (string? Onset, string Final)>? sTable = LoadTable();

    static Dictionary<string, (string? Onset, string Final)>? LoadTable()
    {
        try
        {
            string path = System.IO.Path.Combine(
                MutaForTuneLab.Native.NativeLibraries.PackageDirectory, "ja-zh-phone-map.tsv");
            if (!System.IO.File.Exists(path))
                return null;

            var table = new Dictionary<string, (string?, string)>(StringComparer.Ordinal);
            foreach (string raw in System.IO.File.ReadAllLines(path))
            {
                if (raw.Length == 0 || raw[0] == '#')
                    continue;
                var fields = raw.Split('\t');
                if (fields.Length < 3 || fields[0].Length == 0)
                    continue;
                table[fields[0].Trim()] =
                    (fields[1].Length == 0 ? null : fields[1].Trim(), fields[2].Trim());
            }
            return table.Count > 0 ? table : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Rewrites one mora. The caller passes the onset and the vowel it got from the kana table;
    /// the pair is looked up whole because palatalisation changes the vowel, not just the onset.
    /// </summary>
    public static bool TryMap(string? onset, string vowel, out string? bankOnset, out string bankFinal)
    {
        string key = (onset ?? string.Empty) + vowel;

        if (sTable is not null && sTable.TryGetValue(key, out var mapped))
        {
            bankOnset = mapped.Onset;
            bankFinal = mapped.Final;
            return true;
        }

        key = onset ?? string.Empty;

        // ん is a syllabic nasal: the bank records it as 恩 (en after the zero initial "ge").
        if (key == "N" || (key.Length == 0 && vowel == "N"))
        {
            bankOnset = null;
            bankFinal = "en";
            return true;
        }

        if (sOnset.TryGetValue(key, out bankOnset))
        {
            bankFinal = sPalatal.Contains(key) ? PalatalFinal(vowel) : vowel;
            return true;
        }

        // Unknown onset: pass it through and let the selector score the nearest unit.
        bankOnset = onset;
        bankFinal = vowel;
        return false;
    }
}
