using System;

namespace MutaForTuneLab.Synthesis;

/// <summary>
/// Keeps the loaded R2 vocoder around.
///
/// Loading it builds two ONNX sessions over a 56 MB model, so a per-note load would be
/// unusable; a per-render one is already wasteful. The cache is keyed by the directory the
/// user pointed at, so changing the setting picks up the new package instead of silently
/// reusing the old one.
/// </summary>
public static class R2Registry
{
    static readonly object Gate = new();
    static R2Vocoder? sVocoder;
    static string? sDirectory;

    /// <summary>The cached vocoder for this directory, or null when the package is missing.</summary>
    public static R2Vocoder? Get(string? directory, string melModelPath)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        lock (Gate)
        {
            if (sVocoder is not null && string.Equals(sDirectory, directory, StringComparison.OrdinalIgnoreCase))
                return sVocoder;

            sVocoder?.Dispose();
            sVocoder = R2Vocoder.TryLoad(directory, melModelPath);
            sDirectory = sVocoder is null ? null : directory;
            return sVocoder;
        }
    }

    /// <summary>Drops the cached sessions, e.g. when the setting changes.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            sVocoder?.Dispose();
            sVocoder = null;
            sDirectory = null;
        }
    }
}
