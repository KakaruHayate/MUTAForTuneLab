using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MutaForTuneLab.Assets;

/// <summary>
/// An HTS acoustic model in memory: the text header MUTA writes plus the path the
/// engine should load.
///
/// MUTA stores the model as <c>.voice</c>, which is the same HTS container with every
/// byte passed through a reversible mapping. The engine cannot read that, so a decoded
/// copy is written to a cache directory once per source file and reused afterwards.
/// A file that is already plain (a <c>.htsvoice</c>) is used where it lies.
/// </summary>
public sealed class HtsVoice
{
    /// <summary>The encoded form of "[DATA]\n": the marker that ends the header.</summary>
    static ReadOnlySpan<byte> EncodedDataMarker => "KTQDQM\n"u8;

    public required string SourcePath { get; init; }
    /// <summary>Path to hand to the engine: the source itself, or a decoded cache copy.</summary>
    public required string EnginePath { get; init; }
    public required string Header { get; init; }
    public required int SampleRate { get; init; }
    public required int FramePeriod { get; init; }
    public required int States { get; init; }
    public required int Streams { get; init; }
    public required IReadOnlyList<int> VectorLengths { get; init; }
    public required IReadOnlyList<string> StreamTypes { get; init; }

    /// <summary>Frame period in milliseconds (48 kHz / 240 samples = 5 ms).</summary>
    public double FramePeriodMs => FramePeriod * 1000.0 / SampleRate;

    public static HtsVoice Load(string path, string? cacheDirectory = null)
    {
        var data = File.ReadAllBytes(path);
        var (header, headerSize, encoded) = ReadHeader(data, path);

        string enginePath = path;
        if (encoded)
        {
            enginePath = WriteDecodedCache(path, data, headerSize, cacheDirectory);
        }

        return Parse(path, enginePath, header);
    }

    /// <summary>
    /// Split the container: decode up to and including the [DATA] marker (cheap — the
    /// header is about 1 KB), and report whether the file needed decoding at all.
    /// </summary>
    static (string Header, int HeaderSize, bool Encoded) ReadHeader(byte[] data, string path)
    {
        // Plain .htsvoice: the text header is already readable.
        if (data.Length > 7 && data[0] == (byte)'[')
        {
            int plainEnd = Find(data, "[DATA]\n"u8);
            if (plainEnd >= 0)
            {
                int end = plainEnd + 7;
                return (Encoding.ASCII.GetString(data, 0, end), end, false);
            }
        }

        int marker = Find(data, EncodedDataMarker);
        if (marker < 0)
            throw new InvalidDataException($"{path}: no [DATA] marker (not an HTS voice container)");

        int headerEnd = marker + 7;
        var decoded = InfFile.DecodeAll(data.AsSpan(0, headerEnd).ToArray());
        if (decoded.Length < 7 || Encoding.ASCII.GetString(decoded, decoded.Length - 7, 7) != "[DATA]\n")
            throw new InvalidDataException($"{path}: header does not decode to [DATA]");

        return (Encoding.ASCII.GetString(decoded), headerEnd, true);
    }

    static string WriteDecodedCache(string source, byte[] data, int headerSize, string? cacheDirectory)
    {
        cacheDirectory ??= DefaultCacheDirectory;

        var stamp = new FileInfo(source);
        string key = $"{Path.GetFileNameWithoutExtension(source)}-" +
                     $"{stamp.Length:X}-{stamp.LastWriteTimeUtc.Ticks:X}";
        string target = Path.Combine(cacheDirectory, key + ".htsvoice");
        if (File.Exists(target) && new FileInfo(target).Length == data.Length)
            return target;

        Directory.CreateDirectory(cacheDirectory);

        // Write beside the target and move into place, so two sessions racing on the
        // same voice cannot leave a half-written file behind.
        string temporary = target + "." + Environment.ProcessId.ToString("X") + ".tmp";
        using (var stream = File.Create(temporary))
        {
            // Both halves are stored obfuscated; the decoded file is what HTS reads.
            var headerBytes = InfFile.DecodeAll(data.AsSpan(0, headerSize).ToArray());
            stream.Write(headerBytes, 0, headerBytes.Length);
            var body = InfFile.DecodeAll(data.AsSpan(headerSize).ToArray());
            stream.Write(body, 0, body.Length);
        }
        File.Move(temporary, target, overwrite: true);
        return target;
    }

    public static string DefaultCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MutaForTuneLab", "voice-cache");

    static HtsVoice Parse(string sourcePath, string enginePath, string header)
    {
        int sampleRate = 0, framePeriod = 0, states = 0, streams = 0;
        var vectorLengths = new List<int>();
        var streamTypes = new List<string>();
        bool inStream = false;

        foreach (var raw in header.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0)
                continue;
            if (line.StartsWith('['))
            {
                inStream = line.Equals("[STREAM]", StringComparison.Ordinal);
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();

            if (!inStream)
            {
                switch (key)
                {
                    case "SAMPLING_FREQUENCY":
                        sampleRate = ParseInt(value);
                        break;
                    case "FRAME_PERIOD":
                        framePeriod = ParseInt(value);
                        break;
                    case "NUM_STATES":
                        states = ParseInt(value);
                        break;
                    case "NUM_STREAMS":
                        streams = ParseInt(value);
                        break;
                    case "STREAM_TYPE":
                        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                            streamTypes.Add(part.Trim());
                        break;
                }
            }
            else if (key.StartsWith("VECTOR_LENGTH[", StringComparison.Ordinal))
            {
                vectorLengths.Add(ParseInt(value));
            }
        }

        if (sampleRate <= 0 || framePeriod <= 0 || states <= 0 || streams <= 0)
            throw new InvalidDataException($"{sourcePath}: incomplete voice header");

        return new HtsVoice
        {
            SourcePath = sourcePath,
            EnginePath = enginePath,
            Header = header,
            SampleRate = sampleRate,
            FramePeriod = framePeriod,
            States = states,
            Streams = streams,
            VectorLengths = vectorLengths,
            StreamTypes = streamTypes,
        };
    }

    static int ParseInt(string text)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;

    static int Find(byte[] haystack, ReadOnlySpan<byte> needle)
        => haystack.AsSpan().IndexOf(needle);

    /// <summary>Short stable id for logs and cache keys.</summary>
    public string ShortId => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(SourcePath))).Substring(0, 8);
}
