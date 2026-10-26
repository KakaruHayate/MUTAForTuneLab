"""Ground truth for the WORLDLINE R2 backend.

Runs the same two-model chain the plugin runs (our mel model, then the user's vocoder),
on the same feature slice, and stores the waveform. The point is to check the plumbing --
tensor shapes, padding, trimming, easing -- not to re-derive the model.

Requires the vocoder package the user installed; nothing of it is copied into the repo.

Run:
    python tests/reference/r2_fixtures.py --tools <MUTA tools> --mel <mel.onnx> \
        --vocoder <package dir>
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np

PADDING = 4
MULTIPLE = 16
SRC_RATE, SRC_FRAME_MS, SRC_FFT = 48000, 5.0, 2048


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tools", type=Path, required=True)
    parser.add_argument("--mel", type=Path, required=True)
    parser.add_argument("--vocoder", type=Path, required=True)
    parser.add_argument("--fixtures", type=Path, default=None)
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args()

    sys.path.insert(0, str(args.tools))
    import onnxruntime as ort  # noqa: E402
    from bap_expand import bap_decibels_to_weight, expand_bap  # noqa: E402
    from mgc_spectrum import magnitude_spectrum  # noqa: E402

    here = Path(__file__).resolve().parent
    fixtures = args.fixtures or (here.parent / "fixtures" / "vocoder")
    out = args.out or fixtures

    frames = 40
    bins = 1025
    mgc = np.fromfile(fixtures / "wl_mgc.f64", dtype="<f8").reshape(frames, 50)
    bap = np.fromfile(fixtures / "wl_bap.f64", dtype="<f8").reshape(frames, 26)
    f0 = np.fromfile(fixtures / "wl_f0.f64", dtype="<f8")

    silent = (np.max(np.abs(mgc), axis=1) < 1e-6) | (mgc[:, 0] <= -8.5)
    power = magnitude_spectrum(mgc, 0.55, 0.0, bins) ** 2
    power[silent] = 1e-12
    ap = bap_decibels_to_weight(expand_bap(bap))

    with open(args.vocoder / "vocoder.yaml", encoding="utf-8") as handle:
        config = dict(
            line.split(":", 1) for line in handle.read().splitlines() if ":" in line
        )
    config = {k.strip(): v.strip().strip('"') for k, v in config.items()}
    sample_rate = int(config["sample_rate"])
    hop = int(config["hop_size"])
    fft = int(config.get("fft_size", 2048))
    mel_bins = int(config.get("num_mel_bins", 128))
    target_frame_ms = hop * 1000.0 / sample_rate

    # --- onto the vocoder grid (same steps as the plugin) ---
    t_src = np.arange(frames) * SRC_FRAME_MS
    n_dst = int(round(frames * SRC_FRAME_MS / target_frame_ms))
    t_dst = np.arange(n_dst) * target_frame_ms
    voiced = f0 > 0
    f0d = np.exp(np.interp(t_dst, t_src[voiced], np.log(f0[voiced])))
    f0d = np.where(np.interp(t_dst, t_src, voiced.astype(float)) > 0.5, f0d, 0.0)

    def interp_time(values):
        out_values = np.empty((n_dst, values.shape[1]))
        for c in range(values.shape[1]):
            out_values[:, c] = np.interp(t_dst, t_src, values[:, c])
        return out_values

    def remap(values):
        dst_hz = np.arange(bins) * (sample_rate / fft)
        pos = np.clip(dst_hz / (SRC_RATE / SRC_FFT), 0.0, bins - 1)
        lo = np.floor(pos).astype(int)
        hi = np.minimum(lo + 1, bins - 1)
        frac = (pos - lo)[None, :]
        return values[:, lo] * (1.0 - frac) + values[:, hi] * frac

    sp = np.exp(remap(interp_time(np.log(np.maximum(power, 1e-20)))))
    apd = np.clip(remap(interp_time(ap)), 0.001, 1.0)
    muted = interp_time(silent.astype(float)[:, None])[:, 0] > 0.5
    sp[muted] = 1e-12

    # --- pad and run ---
    total = n_dst + 2 * PADDING
    padded = int(np.ceil(total / MULTIPLE)) * MULTIPLE
    f0_pad = np.zeros((1, padded), dtype=np.float32)
    sp_pad = np.zeros((1, padded, bins), dtype=np.float32)
    ap_pad = np.ones((1, padded, bins), dtype=np.float32)
    body = slice(PADDING, PADDING + n_dst)
    f0_pad[0, body] = f0d.astype(np.float32)
    sp_pad[0, body] = sp.astype(np.float32)
    ap_pad[0, body] = apd.astype(np.float32)

    mel_session = ort.InferenceSession(str(args.mel), providers=["CPUExecutionProvider"])
    mel_inputs = [i.name for i in mel_session.get_inputs()]
    mel = mel_session.run(None, {"f0": f0_pad, "sp_env": sp_pad, "ap": ap_pad})[0]

    voc_session = ort.InferenceSession(
        str(args.vocoder / config["model"]), providers=["CPUExecutionProvider"]
    )
    voc_inputs = [i.name for i in voc_session.get_inputs()]
    mel_in = voc_inputs[0]
    wav = voc_session.run(None, {mel_in: mel.astype(np.float32), "f0": f0_pad})[0]

    samples = int(n_dst * hop)
    offset = PADDING * hop
    audio = np.zeros(samples, dtype=np.float64)
    take = min(samples, wav.shape[1] - offset)
    audio[:take] = wav[0, offset:offset + take]
    ease = min(hop, samples // 2)
    for i in range(ease):
        audio[i] *= i / ease
        audio[samples - 1 - i] *= i / ease

    audio.astype("<f4").tofile(out / "r2_expected.f32")
    (out / "r2.json").write_text(json.dumps({
        "frames": frames, "target_frames": n_dst, "mel_bins": mel_bins,
        "sample_rate": sample_rate, "hop_size": hop, "fft_size": fft,
        "target_frame_ms": target_frame_ms, "samples": samples,
        "padded_frames": padded,
        "mel_inputs": mel_inputs, "vocoder_inputs": voc_inputs,
        "mel_shape": list(mel.shape),
    }, indent=2) + "\n", encoding="utf-8")

    print(f"{frames} -> {n_dst} frames, padded {padded}, {samples} samples")
    print(f"mel inputs {mel_inputs}; vocoder inputs {voc_inputs}")
    print(f"rms {np.sqrt((audio ** 2).mean()):.5f}  peak {np.abs(audio).max():.5f}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
