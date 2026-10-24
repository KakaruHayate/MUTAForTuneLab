using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
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

    /// <summary>Where the dictionaries ship, inside the package.</summary>
    public static string DictionaryDirectory => Path.Combine(NativeLibraries.PackageDirectory, "sinsy", "dic");

    /// <summary>Where the Japanese voice ships, inside the package.</summary>
    public static string VoiceFile => Path.Combine(NativeLibraries.PackageDirectory, "sinsy", "voice",
        "nitech_jp_song070_f001.htsvoice");

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int muta_sinsy_labels(string xmlPath, string dictionaryDirectory, IntPtr buffer, int bufferSize);
}
