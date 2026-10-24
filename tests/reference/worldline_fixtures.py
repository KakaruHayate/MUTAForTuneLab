"""Fixtures for the vocoder path: parameters in, samples out.

The C# side recomputes the spectra from the same MGC/BAP before calling the vocoder, so
what this pins down is the vocoder call itself plus the output gate.

Both backends are covered; the continuous-noise one is seeded, so it is deterministic.

Run:
    python tests/reference/worldline_fixtures.py --tools <MUTA tools> --dll <worldline.dll>
"""

from __future__ import annotations

import argparse
import ctypes
import json
import sys
from pathlib import Path

import numpy as np

FS = 48000
FFT = 2048
BINS = FFT // 2 + 1
FRAME_MS = 5.0
HOP = 240
FRAMES = 40
RAMP_FRAMES = 2


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tools", type=Path, required=True)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--mgc", type=Path, default=None)
    parser.add_argument("--out", type=Path, default=None)
    parser.add_argument("--seed", type=int, default=1)
    args = parser.parse_args()

    sys.path.insert(0, str(args.tools))
    from bap_expand import bap_decibels_to_weight, expand_bap  # noqa: E402
    from mgc_spectrum import magnitude_spectrum  # noqa: E402

    here = Path(__file__).resolve().parent
    out = args.out or (here.parent / "fixtures" / "vocoder")
    out.mkdir(parents=True, exist_ok=True)

    if args.mgc and args.mgc.exists():
        data = np.load(args.mgc)
        start = int(np.argmax(data["f0"] > 0.0))
        start = max(0, start - 12)   # a long enough rest to prove the gate
        sl = slice(start, start + FRAMES)
        mgc = np.ascontiguousarray(data["mgc"][sl], dtype=np.float64)
        bap = np.ascontiguousarray(data["bap"][sl], dtype=np.float64)
        f0 = np.ascontiguousarray(data["f0"][sl], dtype=np.float64)
        source = args.mgc.name
    else:
        raise SystemExit("pass --mgc <npz> so the fixture is a real phrase slice")

    # Same silence rule the engine uses: an explicit rest writes c0 = -9.
    silence = (np.max(np.abs(mgc), axis=1) < 1e-6) | (mgc[:, 0] <= -8.5)

    sp = magnitude_spectrum(mgc, 0.55, 0.0, BINS) ** 2
    sp[silence] = 1e-12
    ap = bap_decibels_to_weight(expand_bap(bap))

    lib = ctypes.CDLL(str(args.dll))
    dp = ctypes.POINTER(ctypes.c_double)
    lib.WorldSynthesisSampleCount.argtypes = [ctypes.c_int, ctypes.c_double, ctypes.c_int]
    lib.WorldSynthesisSampleCount.restype = ctypes.c_int
    lib.WorldSynthesis.argtypes = [dp, ctypes.c_int, dp, ctypes.c_bool, ctypes.c_int,
                                   dp, ctypes.c_bool, ctypes.c_int, ctypes.c_double,
                                   ctypes.c_int, dp, dp, dp, dp, dp]
    lib.WorldSynthesis.restype = ctypes.c_int
    lib.WorldSynthesisContinuousNoise.argtypes = [dp, ctypes.c_int, dp, dp, dp, dp,
                                                  ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                                  ctypes.c_uint64, dp, dp, dp, dp, dp]
    lib.WorldSynthesisContinuousNoise.restype = ctypes.c_int

    def ptr(array):
        return array.ctypes.data_as(dp)

    samples = lib.WorldSynthesisSampleCount(FRAMES, FRAME_MS, FS)
    ones = np.ones(FRAMES)
    half = np.full(FRAMES, 0.5)

    world = np.zeros(samples)
    sp_copy = sp.copy()
    written = lib.WorldSynthesis(ptr(f0.copy()), FRAMES, ptr(sp_copy), False, BINS,
                                 ptr(ap.copy()), False, FFT, FRAME_MS, FS, ptr(world),
                                 ptr(half.copy()), ptr(half.copy()), ptr(half.copy()), ptr(ones.copy()))
    assert written == samples, (written, samples)

    harmonic = np.maximum(sp * (1.0 - ap), 1e-12)
    stretch = np.ones(FRAMES)
    continuous = np.zeros(samples)
    written = lib.WorldSynthesisContinuousNoise(ptr(f0.copy()), FRAMES, ptr(sp.copy()),
                                                ptr(harmonic), ptr(ap.copy()), ptr(stretch),
                                                FFT, HOP, FS, args.seed, ptr(continuous),
                                                ptr(half.copy()), ptr(half.copy()),
                                                ptr(half.copy()), ptr(ones.copy()))
    assert written == samples, (written, samples)

    # The gate envelope: 0 across rests, ramped, then dipped at voicing transitions.
    frame_samples = HOP
    gate = np.repeat((~silence).astype(np.float64), frame_samples)[:samples]
    if gate.size < samples:
        gate = np.pad(gate, (0, samples - gate.size), constant_values=1.0)
    span = int(round(RAMP_FRAMES * FS * FRAME_MS / 1000.0))
    window = np.hanning(2 * span + 1)
    window /= window.sum()
    envelope = np.clip(np.convolve(gate, window, mode="same"), 0.0, 1.0)

    voiced = f0 > 0.0
    crossings = np.nonzero(voiced[1:] != voiced[:-1])[0] + 1
    notch = 1.0 - np.hanning(2 * span + 1)
    for frame in crossings:
        centre = int(frame) * frame_samples
        lo = max(0, centre - span)
        hi = min(envelope.size, centre + span + 1)
        length = hi - lo
        if length > 1:
            envelope[lo:hi] *= notch[np.linspace(0, notch.size - 1, length).astype(int)]

    mgc.astype("<f8").tofile(out / "wl_mgc.f64")
    bap.astype("<f8").tofile(out / "wl_bap.f64")
    f0.astype("<f8").tofile(out / "wl_f0.f64")
    world.astype("<f8").tofile(out / "wl_world_pcm.f64")
    continuous.astype("<f8").tofile(out / "wl_continuous_pcm.f64")
    envelope.astype("<f8").tofile(out / "wl_envelope.f64")
    (out / "wl.json").write_text(json.dumps({
        "frames": FRAMES, "frames_samples": frame_samples, "fft_size": FFT, "bins": BINS,
        "frame_period_ms": FRAME_MS, "sample_rate": FS, "samples": samples,
        "seed": args.seed, "source": source,
        "silent_frames": int(silence.sum()),
    }, indent=2) + "\n", encoding="utf-8")

    print(f"frames {FRAMES} ({int(silence.sum())} silent)  samples {samples}")
    print(f"world rms {np.sqrt((world ** 2).mean()):.5f}  continuous rms "
          f"{np.sqrt((continuous ** 2).mean()):.5f}")
    print(f"wrote fixtures to {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
