using System.Security.Cryptography;
using System.Text;
using MutaForTuneLab.Assets;
using MutaForTuneLab.Native;

// Synthesises a label fixture through the managed path and prints the same digest the
// Python reference prints, so a divergence anywhere in the chain (voice decoding,
// header parsing, marshalling, stream layout) shows up as one different hash.
//
// usage: HtsProbe <voice.htsvoice|voice.voice> <labels.txt>
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: HtsProbe <voice> <labels.txt>");
    return 2;
}

try
{
    var voice = HtsVoice.Load(args[0]);
    Console.WriteLine($"voice   {Path.GetFileName(voice.SourcePath)} -> {voice.EnginePath}");
    Console.WriteLine($"header  {voice.SampleRate} Hz, frame period {voice.FramePeriod} " +
                      $"({voice.FramePeriodMs:0.###} ms), {voice.States} states, {voice.Streams} streams");
    Console.WriteLine($"vectors [{string.Join(", ", voice.VectorLengths)}]  " +
                      $"types [{string.Join(", ", voice.StreamTypes)}]");

    var labels = File.ReadAllLines(args[1])
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .ToArray();

    using var engine = MutaHts.Load(voice.EnginePath);
    int frames = engine.Synthesize(labels);

    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    for (int stream = 0; stream < engine.Streams; stream++)
    {
        int width = engine.VectorLength(stream);
        var buffer = new double[frames * width];
        engine.GetStream(stream, buffer);
        hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer.AsSpan()));
    }

    var durations = engine.StateDurations();
    hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(durations.AsSpan()));

    Console.WriteLine($"labels {labels.Length}  frames {frames}  states {durations.Length}");
    Console.WriteLine($"sha256={Convert.ToHexString(hash.GetHashAndReset())}");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}
