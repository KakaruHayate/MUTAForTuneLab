using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MutaForTuneLab.Assets;

/// <summary>
/// Lyrics to pinyin, using the tables that ship with MUTA.
///
/// The tables are read from the user's installation, not copied into the package: the same
/// rule the voices follow. Two files matter — a per-character table keyed by code point,
/// and a word table that settles characters whose reading depends on their neighbour.
///
/// Readings arrive with a tone digit (`TIAN3`); the acoustic model wants the plain
/// syllable, so the digit is dropped and the rest lowercased. Polyphony is resolved by
/// longest match over the word table, which is the whole reason that table exists.
/// </summary>
public sealed class PinyinDictionary
{
    readonly Dictionary<int, string[]> mCharacters = new();
    readonly Dictionary<string, string[]> mWords = new(StringComparer.Ordinal);

    public int CharacterCount => mCharacters.Count;
    public int WordCount => mWords.Count;

    /// <summary>
    /// Loads from a MUTA installation. Returns null when the tables are not there, so
    /// callers can fall back to treating lyrics as already-phonetic.
    /// </summary>
    public static PinyinDictionary? TryLoadFromInstall(string? installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
            return null;
        string pin = Path.Combine(installDirectory, "pin.dict");
        string duo = Path.Combine(installDirectory, "duo.dict");
        if (!File.Exists(pin))
            return null;

        var dictionary = new PinyinDictionary();
        dictionary.LoadCharacters(pin);
        if (File.Exists(duo))
            dictionary.LoadWords(duo);
        return dictionary.CharacterCount > 0 ? dictionary : null;
    }

    /// <summary>Walks up from a voice folder to the installation root and tries there.</summary>
    public static PinyinDictionary? TryLoadForVoice(string voiceDirectory)
    {
        var directory = new DirectoryInfo(voiceDirectory);
        for (int depth = 0; depth < 4 && directory is not null; depth++)
        {
            var found = TryLoadFromInstall(directory.FullName);
            if (found is not null)
                return found;
            directory = directory.Parent;
        }
        return null;
    }

    void LoadCharacters(string path)
    {
        // Lines are "<hex code point>\t<reading> <reading> ..."; the file is ASCII.
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            int tab = line.IndexOf('\t');
            if (tab <= 0)
                continue;
            if (!int.TryParse(line.AsSpan(0, tab), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                continue;

            var readings = Split(line[(tab + 1)..]);
            if (readings.Length > 0)
                mCharacters[code] = readings;
        }
    }

    void LoadWords(string path)
    {
        // Lines are "<word>\t<reading> <reading> ..."; this one is UTF-8 with a BOM.
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Length == 0)
                continue;
            int tab = line.IndexOf('\t');
            if (tab <= 0)
                continue;

            string word = line[..tab].Trim();
            if (word.Length == 0)
                continue;
            var readings = Split(line[(tab + 1)..]);
            if (readings.Length == word.Length)
                mWords[word] = readings;
        }
    }

    static string[] Split(string text)
    {
        var parts = text.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var readings = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var syllable = Plain(part);
            if (syllable.Length > 0)
                readings.Add(syllable);
        }
        return readings.ToArray();
    }

    /// <summary>Drops the tone digit and lowercases: <c>TIAN3</c> becomes <c>tian</c>.</summary>
    static string Plain(string reading)
    {
        int end = reading.Length;
        while (end > 0 && char.IsDigit(reading[end - 1]))
            end--;
        return reading[..end].ToLowerInvariant();
    }

    /// <summary>
    /// The first syllable of a lyric. A note sings one syllable, so a longer lyric is
    /// answered by its first one rather than by nothing.
    /// </summary>
    public string? SyllableFor(string lyric)
    {
        if (string.IsNullOrWhiteSpace(lyric))
            return null;

        // Longest word first: that is what resolves a polyphonic character.
        for (int length = Math.Min(4, lyric.Length); length >= 1; length--)
        {
            string candidate = lyric[..length];
            if (mWords.TryGetValue(candidate, out var readings) && readings.Length > 0)
                return readings[0];
        }

        foreach (var character in lyric)
        {
            if (mCharacters.TryGetValue(character, out var readings) && readings.Length > 0)
                return readings[0];
        }
        return null;
    }
}
