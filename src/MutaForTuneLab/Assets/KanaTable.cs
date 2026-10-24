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
    readonly int mLongest;

    public int Count => mMora.Count;

    KanaTable(Dictionary<string, string> mora, int longest)
    {
        mMora = mora;
        mLongest = longest;
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
            int tab = line.IndexOf('\t');
            if (tab <= 0)
                continue;

            string kana = line[..tab].Trim();
            string phones = line[(tab + 1)..].Trim();
            if (kana.Length == 0 || phones.Length == 0)
                continue;

            mora[kana] = phones;
            longest = Math.Max(longest, kana.Length);
        }
        return mora.Count > 0 ? new KanaTable(mora, longest) : null;
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
