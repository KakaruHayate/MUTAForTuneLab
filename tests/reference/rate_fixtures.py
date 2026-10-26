"""Fixtures for the 48 kHz -> 44.1 kHz feature conversion (the R2 grid).

Mirrors the offline prototype: time axis first, then the frequency axis evaluated at the
destination's physical bin frequencies, then the silence sentinel.

Run:
    python tests/reference/rate_fixtures.py --tools <MUTA tools> --mgc <npz>
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np

SRC_RATE, SRC_FRAME_MS, SRC_FFT = 48000, 5.0, 2048
DST_RATE, DST_HOP, DST_FFT = 44100, 512, 2048
BINS = DST_FFT // 2 + 1
FRAMES = 24
DST_FRAME_MS = DST_HOP * 1000.0 / DST_RATE


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tools", type=Path, required=True)
    parser.add_argument("--mgc", type=Path, required=True)
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args()

    sys.path.insert(0, str(args.tools))
    from bap_expand import bap_decibels_to_weight, expand_bap  # noqa: E402
    from mgc_spectrum import magnitude_spectrum  # noqa: E402

    here = Path(__file__).resolve().parent
    out = args.out or (here.parent / "fixtures" / "vocoder")
    out.mkdir(parents=True, exist_ok=True)

    data = np.load(args.mgc)
    start = max(0, int(np.argmax(data["f0"] > 0.0)) - 6)   # include a few rests
    sl = slice(start, start + FRAMES)
    mgc = np.ascontiguousarray(data["mgc"][sl], dtype=np.float64)
    bap = np.ascontiguousarray(data["bap"][sl], dtype=np.float64)
    f0 = np.ascontiguousarray(data["f0"][sl], dtype=np.float64)

    silence = (np.max(np.abs(mgc), axis=1) < 1e-6) | (mgc[:, 0] <= -8.5)
    power = magnitude_spectrum(mgc, 0.55, 0.0, BINS) ** 2
    power[silence] = 1e-12
    ap = bap_decibels_to_weight(expand_bap(bap))

    t_src = np.arange(FRAMES) * SRC_FRAME_MS
    total_ms = FRAMES * SRC_FRAME_MS
    n_dst = int(round(total_ms / DST_FRAME_MS))
    t_dst = np.arange(n_dst) * DST_FRAME_MS

    def interp_time(values: np.ndarray) -> np.ndarray:
        result = np.empty((n_dst, values.shape[1]), dtype=np.float64)
        for c in range(values.shape[1]):
            result[:, c] = np.interp(t_dst, t_src, values[:, c])
        return result

    voiced = f0 > 0.0
    logf = np.log(f0[voiced])
    f0d = np.exp(np.interp(t_dst, t_src[voiced], logf))
    f0d = np.where(np.interp(t_dst, t_src, voiced.astype(np.float64)) > 0.5, f0d, 0.0)

    logsp = np.log(np.maximum(power, 1e-20))
    logsp_d = interp_time(logsp)
    ap_d = interp_time(ap)

    def remap(values: np.ndarray) -> np.ndarray:
        src_hz = np.arange(BINS) * (SRC_RATE / SRC_FFT)
        dst_hz = np.arange(BINS) * (DST_RATE / DST_FFT)
        pos = np.clip(dst_hz / (SRC_RATE / SRC_FFT), 0.0, BINS - 1)
        lo = np.floor(pos).astype(int)
        hi = np.minimum(lo + 1, BINS - 1)
        frac = (pos - lo)[None, :]
        return values[:, lo] * (1.0 - frac) + values[:, hi] * frac

    sp = np.exp(remap(logsp_d))
    ap_out = np.clip(remap(ap_d), 0.001, 1.0)
    silent_d = interp_time(silence.astype(np.float64)[:, None])[:, 0] > 0.5
    sp[silent_d] = 1e-12

    mgc.astype("<f8").tofile(out / "rate_mgc.f64")
    bap.astype("<f8").tofile(out / "rate_bap.f64")
    f0.astype("<f8").tofile(out / "rate_f0.f64")
    sp.astype("<f8").tofile(out / "rate_sp.f64")
    ap_out.astype("<f8").tofile(out / "rate_ap.f64")
    f0d.astype("<f8").tofile(out / "rate_f0d.f64")
    (out / "rate.json").write_text(json.dumps({
        "frames": FRAMES, "bins": BINS,
        "source_rate": SRC_RATE, "source_frame_ms": SRC_FRAME_MS, "source_fft": SRC_FFT,
        "target_rate": DST_RATE, "target_hop": DST_HOP, "target_fft": DST_FFT,
        "target_frame_ms": DST_FRAME_MS, "target_frames": n_dst,
        "silent_frames": int(silence.sum()),
    }, indent=2) + "\n", encoding="utf-8")

    print(f"{FRAMES} -> {n_dst} frames at {DST_FRAME_MS:.3f} ms, {int(silence.sum())} silent")
    print(f"wrote fixtures to {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
