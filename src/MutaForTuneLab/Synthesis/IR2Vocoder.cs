using System;

namespace MutaForTuneLab.Synthesis;

/// <summary>
/// The optional R2 backend, seen from the pipeline.
///
/// Kept separate from the implementation on purpose: the lite package ships without ONNX
/// Runtime, so <c>R2Vocoder</c> is not even compiled there, while the pipeline still needs
/// a type for its (always null) parameter.
/// </summary>
public interface IR2Vocoder : IDisposable
{
    int SampleRate { get; }

    double[] Synthesize(
        double[][] sp, double[][] ap, double[] f0, bool[] silence,
        double modelFrameMs, int modelRate);
}
