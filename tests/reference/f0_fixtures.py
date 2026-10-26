"""Generate the F0 fixtures used to verify the C# port.

Each case is a carrier F0 curve (what the acoustic model produced) plus the note list,
and the curve the reference implementation assembles from them. The carrier is worked
out per case so the fixtures stay small and readable instead of shipping real songs.

The reference runs with the production configuration the renderers use:
exact_p4, start_ramp and p5 on; the ramp/smooth extras off.

Run:
    python tests/reference/f0_fixtures.py --tools <dir holding muta_f0_mech.py>
"""

from __future__ import annotations

import argparse
import json
import sys
import zlib
from pathlib import Path

import numpy as np

# name -> (total frames, notes as (start, frames, midi), rest spans as (start, frames))
CASES = {
    "single": (300, [(150, 40, 72)], [(0, 150), (190, 110)]),
    "long": (500, [(50, 400, 60)], [(0, 50), (450, 50)]),
    "jump": (420, [(60, 60, 60), (160, 60, 84), (280, 60, 67)],
             [(0, 60), (120, 40), (220, 60), (340, 80)]),
    "rest": (360, [(40, 60, 69), (200, 60, 69)], [(0, 40), (100, 100), (260, 100)]),
    "short": (240, [(40, 10, 72), (60, 10, 74), (80, 10, 76), (100, 12, 79)],
              [(0, 40), (112, 40), (152, 88)]),
}


def make_carrier(total: int, notes, rng) -> np.ndarray:
    """A carrier with the habits of a real one: detune, an onset scoop, slow drift."""
    carrier = np.zeros(total, dtype=np.float64)
    for start, frames, midi in notes:
        stop = min(start + frames, total)
        width = stop - start
        if width <= 0:
            continue
        target = 440.0 * 2.0 ** ((midi - 69.0) / 12.0)
        x = np.arange(width) / max(1, width)
        contour = target * (0.97 + 0.03 * x) * (1.0 + 0.01 * np.sin(2.0 * np.pi * 2.0 * x))
        scoop = max(1, width // 10)
        contour[:scoop] *= 0.90
        contour *= 1.0 + 0.0005 * rng.standard_normal(width)
        carrier[start:stop] = contour
    return carrier


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tools", type=Path, required=True)
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args()

    sys.path.insert(0, str(args.tools))
    from muta_f0_mech import build_f0  # noqa: E402

    here = Path(__file__).resolve().parent
    out = args.out or (here.parent / "fixtures" / "f0")
    out.mkdir(parents=True, exist_ok=True)

    for name, (total, notes, rests) in CASES.items():
        # Stable across runs and machines: Python's str hash is randomised per process.
        rng = np.random.default_rng(zlib.crc32(name.encode("utf-8")))

        note_entries = [{"start_frame": s, "frames": f, "midi": m} for s, f, m in notes]
        note_entries += [{"start_frame": s, "frames": f, "midi": 0, "rest": True} for s, f in rests]
        note_entries.sort(key=lambda n: n["start_frame"])

        carrier = make_carrier(total, notes, rng)
        expected = build_f0(carrier, note_entries, total, exact_p4=True, start_ramp=True, p5=True)

        payload = {"name": name, "total": total, "notes": note_entries}
        (out / f"{name}.json").write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
        carrier.astype("<f8").tofile(out / f"{name}.carrier.f64")
        expected.astype("<f8").tofile(out / f"{name}.f0.f64")

        voiced = expected > 0.0
        print(f"{name:8s} {total:5d} frames  {len(note_entries)} entries  "
              f"voiced {int(voiced.sum()):4d}  "
              f"f0 {expected[voiced].min():7.2f}..{expected[voiced].max():7.2f} Hz")

    print(f"wrote fixtures to {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
