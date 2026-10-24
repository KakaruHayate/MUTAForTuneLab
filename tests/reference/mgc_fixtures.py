"""Fixtures for the MGC -> power spectrum path.

The expected values come from the reference implementation, which uses a dense cosine
matrix for the last step; the port uses an FFT-backed DCT-I there, so this comparison
also guards the claim that the two agree.

Per-frame alphas are chosen on the 0.005 grid the port quantises to, so both
implementations group the frames the same way.

Run:
    python tests/reference/mgc_fixtures.py --tools <dir holding mgc_spectrum.py> [--mgc <npz>]
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np

BINS = 1025
FRAMES = 6


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tools", type=Path, required=True)
    parser.add_argument("--mgc", type=Path, default=None, help="npz holding a 'mgc' matrix")
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args()

    sys.path.insert(0, str(args.tools))
    from mgc_spectrum import magnitude_spectrum  # noqa: E402

    here = Path(__file__).resolve().parent
    out = args.out or (here.parent / "fixtures" / "vocoder")
    out.mkdir(parents=True, exist_ok=True)

    if args.mgc and args.mgc.exists():
        data = np.load(args.mgc)
        mgc = np.ascontiguousarray(data["mgc"][:FRAMES], dtype=np.float64)
        source = str(args.mgc.name)
    else:
        rng = np.random.default_rng(2026)
        mgc = rng.normal(0.0, 0.1, size=(FRAMES, 50))
        mgc[:, 0] = rng.normal(-5.0, 0.5, size=FRAMES)
        source = "synthetic"

    alpha_scalar = 0.55
    scalar = magnitude_spectrum(mgc, alpha_scalar, 0.0, BINS) ** 2

    # On the 0.005 grid: 0.550 .. 0.600
    per_frame = np.linspace(0.55, 0.60, FRAMES)
    per_frame = np.round(per_frame / 0.005) * 0.005
    framed = magnitude_spectrum(mgc, per_frame, 0.0, BINS) ** 2

    mgc.astype("<f8").tofile(out / "mgc.f64")
    scalar.astype("<f8").tofile(out / "power_scalar.f64")
    framed.astype("<f8").tofile(out / "power_perframe.f64")
    (out / "mgc.json").write_text(json.dumps({
        "bins": BINS,
        "frames": int(mgc.shape[0]),
        "order": int(mgc.shape[1]),
        "source": source,
        "alpha_scalar": alpha_scalar,
        "alpha_per_frame": [float(v) for v in per_frame],
    }, indent=2) + "\n", encoding="utf-8")

    print(f"mgc {mgc.shape} from {source}; alphas {per_frame[0]:.3f}..{per_frame[-1]:.3f}")
    print(f"wrote fixtures to {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
