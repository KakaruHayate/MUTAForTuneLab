using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MutaForTuneLab.Assets;

/// <summary>
/// Kana to phonemes, using the table that ships with Sinsy.
///
/// The table is one line per mora: the kana, then the phonemes it is pronounced as. Some
/// entries are two kana long (きゃ), so a lyric is matched longest-first — otherwise a
/// contraction would be read as two separate morae.
///
/// The table has three encodings; the UTF-8 one is used, and the other two are ignored
/// rather than guessed at, because reading a Shift-JIS table as UTF-8 produces plausible
/// but wrong kana.
/// </summary>
public sealed class KanaTable
{
    readonly Dictionary<string, string> mMora = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> mRomaji = new(StringComparer.OrdinalIgnoreCase);
    readonly int mLongest;
    readonly int mRomajiLongest;

    public int Count => mMora.Count;

    /// <summary>
    /// The kana that produces this onset + vowel pair, or null. Used to turn a pinned
    /// phoneme pair back into a lyric, so the Sinsy labels can be built from the pin
    /// instead of the text the user typed.
    ///
    /// The table stores a mora's phones space separated ("k y a"), while the engine names
    /// the onset as one symbol ("ky"), so the comparison joins everything but the vowel.
    /// </summary>
    public string? KanaForPhones(string? onset, string vowel)
        => KanaForPhones(
            string.IsNullOrEmpty(onset) ? Array.Empty<string>() : new[] { onset }, vowel);

    /// <summary>
    /// Same lookup for a pinned note whose leading list is several phones ("k", "y"), which
    /// is how a youon arrives: the table stores it as one mora with the phones "k y a".
    /// </summary>
    public string? KanaForPhones(IReadOnlyList<string> leading, string vowel)
    {
        string onset = string.Concat(leading);
        foreach (var pair in mMora)
        {
            // The table has lines whose first field is empty; they must not win the lookup.
            if (pair.Key.Length == 0)
                continue;
            var parts = pair.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !string.Equals(parts[^1], vowel, StringComparison.Ordinal))
                continue;
            string head = parts.Length > 1
                ? string.Join(string.Empty, parts, 0, parts.Length - 1)
                : string.Empty;
            if (string.Equals(head, onset, StringComparison.Ordinal))
                return pair.Key;
        }
        return null;
    }

    KanaTable(Dictionary<string, string> mora, Dictionary<string, string> romaji, int longest, int romajiLongest)
    {
        mMora = mora;
        mRomaji = romaji;
        mLongest = longest;
        mRomajiLongest = romajiLongest;
    }

    /// <summary>Loads the UTF-8 table from a directory holding Sinsy's dictionaries.</summary>
    public static KanaTable? TryLoad(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        // Prefer the UTF-8 table; fall back to whatever single table exists.
        string path = Path.Combine(directory, "japanese.utf_8.table");
        if (!File.Exists(path))
        {
            var candidates = Directory.GetFiles(directory, "japanese.*.table");
            if (candidates.Length == 0)
                return null;
            path = candidates[0];
        }

        var mora = new Dictionary<string, string>(StringComparer.Ordinal);
        int longest = 1;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Length == 0)
                continue;

            // "きゃ k y a": the kana, then one or more phonemes, all separated by spaces.
            var fields = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2)
                continue;

            string kana = fields[0];
            string phones = string.Join(' ', fields[1..]);
            if (kana.Length == 0 || phones.Length == 0)
                continue;

            mora[kana] = phones;
            longest = Math.Max(longest, kana.Length);
        }
        // Invert: the phonemes a mora is pronounced as, joined, are its romaji.
        var romaji = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int romajiLongest = 1;
        foreach (var pair in mora)
        {
            string key = pair.Value.Replace(" ", string.Empty);
            if (key.Length == 0 || romaji.ContainsKey(key))
                continue;
            romaji[key] = pair.Key;
            romajiLongest = Math.Max(romajiLongest, key.Length);
        }

        return mora.Count > 0 ? new KanaTable(mora, romaji, longest, romajiLongest) : null;
    }

    /// <summary>
    /// Romaji back to kana.
    ///
    /// The host hands us romanised lyrics, and Sinsy only reads kana. The table already
    /// holds the other direction: a mora's phonemes concatenated are exactly its romaji
    /// (きょ = ky + o = "kyo"), so the mapping is read out of the table instead of being
    /// written again.
    /// </summary>
    public string? KanaFor(string romaji)
    {
        if (string.IsNullOrEmpty(romaji))
            return null;
        for (int length = Math.Min(mRomajiLongest, romaji.Length); length >= 1; length--)
        {
            if (mRomaji.TryGetValue(romaji[..length], out var kana))
                return kana;
        }
        return null;
    }

    /// <summary>True when the text contains kana (so the Japanese table applies).</summary>
    public static bool IsKana(string text)
    {
        foreach (var character in text)
        {
            if (character >= '\u3041' && character <= '\u309F')   // hiragana
                return true;
            if (character >= '\u30A1' && character <= '\u30FF')   // katakana
                return true;
        }
        return false;
    }

    /// <summary>
    /// The phonemes for the first mora of <paramref name="lyric"/>. A note sings one mora,
    /// so a longer lyric is answered by its first one rather than by nothing.
    /// </summary>
    public string? PhonemesFor(string lyric)
    {
        if (string.IsNullOrEmpty(lyric))
            return null;

        for (int length = Math.Min(mLongest, lyric.Length); length >= 1; length--)
        {
            string candidate = lyric[..length];
            if (mMora.TryGetValue(candidate, out var phones))
                return phones;
        }

        // Katakana and hiragana cover the same morae; try the other script before giving up.
        var converted = ConvertScript(lyric);
        if (!string.Equals(converted, lyric, StringComparison.Ordinal))
        {
            for (int length = Math.Min(mLongest, converted.Length); length >= 1; length--)
            {
                if (mMora.TryGetValue(converted[..length], out var phones))
                    return phones;
            }
        }
        return null;
    }

    /// <summary>Katakana to hiragana (the table's script), leaving other characters alone.</summary>
    static string ConvertScript(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character >= '\u30A1' && character <= '\u30F6')
                builder.Append((char)(character - 0x60));
            else
                builder.Append(character);
        }
        return builder.ToString();
    }
}
