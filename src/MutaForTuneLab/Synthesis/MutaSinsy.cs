using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text;
using MutaForTuneLab.Native;

namespace MutaForTuneLab.Synthesis;

/// <summary>
/// Sinsy's label builder, which is the only thing that knows the full-context features the
/// Japanese voice was trained on.
///
/// The native side takes a score *file*, because that is how Sinsy reads MusicXML, so the
/// written score goes through a temporary file and the labels come back as one string.
/// </summary>
public static class MutaSinsy
{
    const string Library = "muta_sinsy";

    static MutaSinsy() => NativeLibraries.EnsureResolver();

    /// <summary>Full-context label lines, or null when the builder refused the score.</summary>
    public static string? Labels(IReadOnlyList<ScoreNote> notes, double totalSeconds, string dictionaryDirectory)
    {
        if (notes.Count == 0 || string.IsNullOrWhiteSpace(dictionaryDirectory))
            return null;

        string score = MusicXmlWriter.Write(notes, totalSeconds);
        string path = Path.Combine(Path.GetTempPath(), $"muta-score-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(path, score, new UTF8Encoding(false));

            int needed = muta_sinsy_labels(path, dictionaryDirectory, IntPtr.Zero, 0);
            if (needed <= 0)
                return null;

            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                int written = muta_sinsy_labels(path, dictionaryDirectory, buffer, needed);
                if (written <= 0)
                    return null;
                return Marshal.PtrToStringUTF8(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The span the actual singing occupies, in frames.
    ///
    /// Sinsy insists a phrase begins and ends with a pause and inserts two seconds of one
    /// when the score does not, so the labels describe more time than the notes do. The
    /// first and last non-pause phonemes mark the real content; trimming to them is what
    /// makes the rendered length match the block.
    /// </summary>
    public static (int StartFrame, int EndFrame)? ContentWindow(string labels, double frameSeconds)
    {
        if (string.IsNullOrEmpty(labels) || frameSeconds <= 0.0)
            return null;

        double start = double.MaxValue;
        double end = double.MinValue;
        foreach (var line in labels.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            // "<begin> <end> <phoneme>@..."; a derived end is -1 and belongs to its begin.
            var fields = line.Split(' ');
            if (fields.Length < 3)
                continue;
            string phoneme = fields[2];
            int at = phoneme.IndexOf('@');
            string symbol = at > 0 ? phoneme[..at] : phoneme;
            if (symbol is "pau" or "sil" or "xx")
                continue;
            if (!double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double begin))
                continue;
            start = Math.Min(start, begin * 1.0e-7);
            if (double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double finish)
                && finish >= 0)
                end = Math.Max(end, finish * 1.0e-7);
            else
                end = Math.Max(end, begin * 1.0e-7);
        }

        if (start == double.MaxValue || end <= start)
            return null;
        return ((int)Math.Round(start / frameSeconds), (int)Math.Round(end / frameSeconds));
    }
    /// <summary>Where the dictionaries ship, inside the package.</summary>
    public static string DictionaryDirectory => Path.Combine(NativeLibraries.PackageDirectory, "sinsy", "dic");

    /// <summary>Where the Japanese voice ships, inside the package.</summary>
    public static string VoiceFile => Path.Combine(NativeLibraries.PackageDirectory, "sinsy", "voice",
        "nitech_jp_song070_f001.htsvoice");

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int muta_sinsy_labels(string xmlPath, string dictionaryDirectory, IntPtr buffer, int bufferSize);
}
