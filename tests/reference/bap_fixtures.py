"""Fixtures for the 26-band -> 1025-bin aperiodicity expansion."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tools", type=Path, required=True)
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args()

    sys.path.insert(0, str(args.tools))
    from bap_expand import bap_decibels_to_linear, bap_decibels_to_weight, expand_bap  # noqa: E402

    here = Path(__file__).resolve().parent
    out = args.out or (here.parent / "fixtures" / "vocoder")
    out.mkdir(parents=True, exist_ok=True)

    rng = np.random.default_rng(1017)
    bap = np.concatenate([
        rng.uniform(-60.0, 0.0, size=(6, 26)),
        np.zeros((1, 26)),          # fully periodic
        np.full((1, 26), -60.0),    # fully aperiodic
    ])

    expanded = expand_bap(bap)
    payload = {
        "bap": bap.tolist(),
        "expanded": expanded.tolist(),
        "weight": bap_decibels_to_weight(bap).tolist(),
        "linear": bap_decibels_to_linear(bap).tolist(),
    }
    (out / "bap.json").write_text(json.dumps(payload) + "\n", encoding="utf-8")
    print(f"frames {bap.shape[0]} bands {bap.shape[1]} bins {expanded.shape[1]}")
    print(f"wrote {out / 'bap.json'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
