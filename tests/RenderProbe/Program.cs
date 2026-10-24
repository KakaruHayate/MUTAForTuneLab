using MutaForTuneLab.Assets;
using MutaForTuneLab.Native;
using MutaForTuneLab.Synthesis;

// Compares the two timbre paths at every stage: the parameters each produces, and the
// audio each becomes under both vocoder backends.
//
// usage: RenderProbe [voice dir]
MutaDiagnostics.Sink = message => Console.WriteLine("  [log] " + message);

string directory = args.Length > 0
    ? args[0]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                   "Muta", "voice", "song", "yanxi");

string id = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));
var voice = new MutaVoice(id, directory,
    Path.Combine(directory, id + ".voice"),
    Path.Combine(directory, id + ".inf"),
    Path.Combine(directory, id + ".bnk"),
    id, new FileInfo(Path.Combine(directory, id + ".bnk")).Length);

string[] lyrics = { "chen", "a", "ni", "hao" };
int[] pitches = { 60, 62, 64, 65 };
const int noteFrames = 200;
const int total = 800;
var notes = new List<RenderNote>(lyrics.Length);
for (int i = 0; i < lyrics.Length; i++)
    notes.Add(new RenderNote(i * noteFrames, noteFrames, pitches[i], lyrics[i], false, lyrics[i]));

var expression = new VocoderExpression();

// ---- model path parameters -------------------------------------------------
var container = HtsVoice.Load(voice.VoiceFile);
double[] modelF0;
double[][] modelMgc;
double[][] modelBap;
{
    using var engine = MutaHts.Load(container.EnginePath);
    var score = new NoteScore
    {
        Phones = notes.Select(n => n.Phone).ToArray(),
        Starts = notes.Select(n => n.StartFrame).ToArray(),
        Durations = notes.Select(n => n.Frames).ToArray(),
        Pitches = notes.Select(n => n.Midi).ToArray(),
        Tempo = 120,
    };
    int frames = engine.Synthesize(LabelBuilder.Build(score));
    modelMgc = ReadMatrix(engine, 0, frames);
    modelBap = ReadMatrix(engine, 2, frames);
    int lf0Width = engine.VectorLength(1);
    var buffer = new double[frames * lf0Width];
    engine.GetStream(1, buffer);
    var lf0 = new double[frames];
    for (int f = 0; f < frames; f++)
        lf0[f] = buffer[f * lf0Width];
    modelF0 = PitchFromLf0(lf0);
}

// ---- bank path parameters ---------------------------------------------------
var assets = BankAssets.Load(voice);
var assembled = BankRenderer.Assemble(assets, notes, total);
var bankF0 = new double[total];
for (int i = 0; i < notes.Count; i++)
{
    double hz = 440.0 * Math.Pow(2.0, (notes[i].Midi - 69) / 12.0);
    for (int f = notes[i].StartFrame; f < Math.Min(total, notes[i].StartFrame + notes[i].Frames); f++)
        bankF0[f] = hz;
}

var modelSilence = new bool[modelF0.Length];
for (int f = 0; f < modelF0.Length; f++)
    modelSilence[f] = modelF0[f] <= 0.0;

Tilt("model - bank (dB)", modelMgc, assembled.Mgc);
Aperiodicity("model", modelBap);
Aperiodicity("bank", assembled.Bap);
Console.WriteLine("parameter bands (share of the spectrum that feeds the vocoder):");
BandReport("model", modelMgc, 0.55);
foreach (var alpha in new[] { 0.30, 0.35, 0.40, 0.42, 0.45, 0.50, 0.55, 0.60, 0.65 })
    BandReport($"bank@{alpha:F2}", assembled.Mgc, alpha);

Console.WriteLine();
Console.WriteLine("audio bands, both parameter sets x both backends:");
foreach (var (name, mgc, bap, f0, silence) in new[]
{
    ("model", modelMgc, modelBap, modelF0, modelSilence),
    ("bank", assembled.Mgc, assembled.Bap, bankF0, assembled.Silence),
})
{
    var sp = VocoderPipeline.Power(mgc, Fill(mgc.Length, 0.55), silence);
    var ap = VocoderPipeline.Aperiodicity(bap);
    foreach (var backend in new[] { VocoderBackend.World, VocoderBackend.ContinuousNoise })
    {
        var settings = new VocoderSettings
        {
            FftSize = 2048, FramePeriodMs = 5.0, SampleRate = 48000, Backend = backend,
        };
        var samples = WorldlineVocoder.Synthesize(sp, ap, f0, settings, expression);
        AudioReport($"{name}/{backend}", samples.Select(v => (float)v).ToArray());
    }
}

// Does the aperiodicity actually reach the output, and does the backend matter?
Console.WriteLine();
Console.WriteLine("aperiodicity response, both backends (bank parameters):");
{
    var sp = VocoderPipeline.Power(assembled.Mgc, Fill(total, 0.55), assembled.Silence);
    foreach (var lift in new[] { 0.0, 20.0, 40.0 })
    {
        var bap = assembled.Bap.Select(f => f.Select(v => v + lift).ToArray()).ToArray();
        var ap = VocoderPipeline.Aperiodicity(bap);
        foreach (var backend in new[] { VocoderBackend.World, VocoderBackend.ContinuousNoise })
        {
            var settings = new VocoderSettings
            {
                FftSize = 2048, FramePeriodMs = 5.0, SampleRate = 48000, Backend = backend,
            };
            var samples = WorldlineVocoder.Synthesize(sp, ap, bankF0, settings, expression);
            AudioReport($"bap+{lift:F0} {backend}", samples.Select(v => (float)v).ToArray());
        }
    }
}
// The four vocoder-side expression curves, one at a time.
Console.WriteLine();
Console.WriteLine("expression curves:");
{
    foreach (var (exprName, gender, breath) in new[]
    {
        ("neutral", 0.5, 0.5),
        ("gender 0", 0.0, 0.5),
        ("gender 1", 1.0, 0.5),
        ("breath 0", 0.5, 0.0),
        ("breath 1", 0.5, 1.0),
    })
    {
        var result = MutaRenderer.Render(new RenderRequest
        {
            Voice = voice, Notes = notes, TotalFrames = total,
            Expression = new VocoderExpression
            {
                Gender = Fill(total, gender),
                Breathiness = Fill(total, breath),
            },
        }, CancellationToken.None);
        AudioReport(exprName, result.Audio);
    }
}
// Each control at an extreme, against the untouched baseline. A control that changes
// nothing is a control that is not wired.
Console.WriteLine();
Console.WriteLine("controls, one curve at a time:");
{
    var baseline = MutaRenderer.Render(new RenderRequest
    {
        Voice = voice, Notes = notes, TotalFrames = total,
    }, CancellationToken.None);
    double baseRms = Rms(baseline.Audio);

    var curves = new (string Name, double[]? Energy, double[]? Timbre, double[]? Husky)[]
    {
        ("energy +12", Fill(total, 12.0), null, null),
        ("energy -12", Fill(total, -12.0), null, null),
        ("timbre +4", null, Fill(total, 4.0), null),
        ("timbre -4", null, Fill(total, -4.0), null),
        ("husky +20", null, null, Fill(total, 20.0)),
        ("husky +40", null, null, Fill(total, 40.0)),
    };
    foreach (var (curveName, energy, timbre, husky) in curves)
    {
        var result = MutaRenderer.Render(new RenderRequest
        {
            Voice = voice, Notes = notes, TotalFrames = total,
            Energy = energy, Timbre = timbre, Husky = husky,
        }, CancellationToken.None);
        double rms = Rms(result.Audio);
        AudioReport(curveName, result.Audio);
        Console.WriteLine($"      {20 * Math.Log10(rms / baseRms):+0.0;-0.0} dB vs baseline");
    }
}

// End to end through the renderer, which is where the level and spectral matching live.
Console.WriteLine();
Console.WriteLine("through the renderer:");
foreach (var useBank in new[] { false, true })
{
    var request = new RenderRequest
    {
        Voice = voice, Notes = notes, TotalFrames = total, UseBank = useBank,
    };
    var result = MutaRenderer.Render(request, CancellationToken.None);
    AudioReport(useBank ? "bank" : "model", result.Audio);
}
// Aperiodicity: if the two layers disagree here as well, the vocoder mixes them
// differently and no amount of spectral matching will close the gap.
static void Aperiodicity(string label, double[][] bap)
{
    var sum = new double[bap[0].Length];
    int frames = 0;
    foreach (var frame in bap)
    {
        for (int b = 0; b < frame.Length; b++)
            sum[b] += frame[b];
        frames++;
    }
    var text = string.Join(" ", sum.Select((v, i) => $"{i}:{v / Math.Max(1, frames):F1}"));
    Console.WriteLine($"  ap {label,-6} {text}");
}
// How much can the aperiodicity move the result at all? Push it to both extremes.
Console.WriteLine();
Console.WriteLine("aperiodicity lever (bank parameters):");
{
    var sp = VocoderPipeline.Power(assembled.Mgc, Fill(total, 0.55), assembled.Silence);
    foreach (var apDb in new[] { -80.0, 0.0, -20.0, -6.0 })
    {
        var bapCopy = assembled.Bap.Select(frame => frame.Select(v => apDb).ToArray()).ToArray();
        var ap = VocoderPipeline.Aperiodicity(bapCopy);
        var settings = new VocoderSettings
        {
            FftSize = 2048, FramePeriodMs = 5.0, SampleRate = 48000, Backend = VocoderBackend.ContinuousNoise,
        };
        var samples = WorldlineVocoder.Synthesize(sp, ap, bankF0, settings, expression);
        AudioReport($"ap={apDb:F0}dB", samples.Select(v => (float)v).ToArray());
    }
}
static double Rms(float[] audio)
{
    if (audio.Length == 0)
        return 0.0;
    double sum = 0.0;
    foreach (var sample in audio)
        sum += (double)sample * sample;
    return Math.Sqrt(sum / audio.Length);
}

static double[] Fill(int length, double value)
{
    var array = new double[Math.Max(1, length)];
    Array.Fill(array, value);
    return array;
}

static double[] PitchFromLf0(double[] lf0)
{
    var pitch = new double[lf0.Length];
    for (int f = 0; f < lf0.Length; f++)
        pitch[f] = lf0[f] <= -1e9 ? 0.0 : Math.Exp(lf0[f]);
    return pitch;
}

static double[][] ReadMatrix(MutaHts engine, int stream, int frames)
{
    int width = engine.VectorLength(stream);
    var buffer = new double[frames * width];
    engine.GetStream(stream, buffer);
    var matrix = new double[frames][];
    for (int f = 0; f < frames; f++)
    {
        matrix[f] = new double[width];
        Array.Copy(buffer, f * width, matrix[f], 0, width);
    }
    return matrix;
}

static void BandReport(string label, double[][] mgc, double alpha)
{
    const int bins = 1025;
    var sums = new double[6];
    int frames = 0;
    foreach (var frame in mgc)
    {
        if (frame[0] <= -8.5)
            continue;
        var spectrum = MgcSpectrum.Power(new[] { frame }, new[] { alpha }, bins)[0];
        for (int bin = 1; bin < bins; bin++)
        {
            double hz = bin * 48000.0 / 2048.0;
            int band = Band(hz);
            if (band >= 0)
                sums[band] += spectrum[bin];
        }
        frames++;
    }
    Console.WriteLine($"  {label,-6} frames {frames,4}  {Share(sums, sums.Sum())}");
}

static void AudioReport(string label, float[] audio)
{
    double peak = 0, energy = 0;
    foreach (var sample in audio)
    {
        peak = Math.Max(peak, Math.Abs(sample));
        energy += (double)sample * sample;
    }
    double rms = audio.Length > 0 ? Math.Sqrt(energy / audio.Length) : 0;

    // Average over the whole take rather than one window: a single window lands wherever
    // the loudest transient happens to be and says nothing about the timbre.
    const int fftSize = 4096;
    const int hop = fftSize / 2;
    var sums = new double[6];
    var window = new double[fftSize];
    int used = 0;
    for (int start = 0; start + fftSize <= audio.Length; start += hop)
    {
        double energyHere = 0;
        for (int i = 0; i < fftSize; i++)
            energyHere += (double)audio[start + i] * audio[start + i];
        if (Math.Sqrt(energyHere / fftSize) < rms * 0.25)
            continue;

        for (int i = 0; i < fftSize; i++)
            window[i] = audio[start + i] * (0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (fftSize - 1)));
        Fft.Real(window);
        for (int bin = 1; bin < fftSize / 2; bin++)
        {
            int band = Band(bin * 48000.0 / fftSize);
            if (band >= 0)
                sums[band] += window[bin] * window[bin];
        }
        used++;
    }
    Console.WriteLine($"  {label,-22} peak {peak:F3} rms {rms:F4} windows {used,3}  {Share(sums, sums.Sum())}");
}

// The tilt between the two parameter sets, in dB per octave band. This is what a
// match would have to cancel.
static void Tilt(string label, double[][] a, double[][] b)
{
    const int bins = 1025;
    var avgA = Average(a, bins);
    var avgB = Average(b, bins);
    // Normalise each to its own total: this is about shape, not level.
    double totalA = avgA.Sum(), totalB = avgB.Sum();
    for (int bin = 0; bin < bins; bin++)
    {
        avgA[bin] = totalA > 0 ? avgA[bin] / totalA * bins : 0.0;
        avgB[bin] = totalB > 0 ? avgB[bin] / totalB * bins : 0.0;
    }
    var text = new System.Text.StringBuilder();
    double[] edges = { 250, 500, 1000, 2000, 4000, 8000, 16000 };
    for (int band = 0; band < edges.Length - 1; band++)
    {
        double sumA = 0, sumB = 0;
        for (int bin = 1; bin < bins; bin++)
        {
            double hz = bin * 48000.0 / 2048.0;
            if (hz < edges[band] || hz >= edges[band + 1])
                continue;
            sumA += avgA[bin];
            sumB += avgB[bin];
        }
        double ratio = sumB > 0 ? sumA / sumB : 1.0;
        text.Append($"  {edges[band]}-{edges[band + 1]}:{20 * Math.Log10(ratio):+0.0;-0.0}dB");
    }
    Console.WriteLine($"  {label,-22}{text}");
}

static double[] Average(double[][] mgc, int bins)
{
    var total = new double[bins];
    int frames = 0;
    foreach (var frame in mgc)
    {
        if (frame[0] <= -8.5)
            continue;
        var spectrum = MgcSpectrum.Power(new[] { frame }, new[] { 0.55 }, bins)[0];
        for (int bin = 0; bin < bins; bin++)
            total[bin] += spectrum[bin];
        frames++;
    }
    if (frames > 0)
        for (int bin = 0; bin < bins; bin++)
            total[bin] /= frames;
    return total;
}
static int Band(double hz)
    => hz < 250 ? -1 : hz < 500 ? 0 : hz < 1000 ? 1 : hz < 2000 ? 2 : hz < 4000 ? 3 : hz < 8000 ? 4 : 5;

static string Share(double[] sums, double total)
    => string.Join("  ", new[] { "250-500", "500-1k", "1k-2k", "2k-4k", "4k-8k", "8k-16k" }
        .Select((name, i) => $"{name}:{(total > 0 ? sums[i] / total * 100 : 0):F1}%"));
