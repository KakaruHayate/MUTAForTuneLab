"""Fixtures for the bank assembly arithmetic.

These cover the pieces where a plausible rewrite silently disagrees: the frame
resampler (float32 position accumulator), the three-part split (float32 products),
the fade weights (narrowed to float), the state ratios, and the branch planning.

Run:
    python tests/reference/bank_fixtures.py --tools <dir holding the reference modules>
"""

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
    from mcore_note_assembly import (  # noqa: E402
        NoteAssemblyState,
        fade_in_weights,
        fade_out_weights,
        plan_note_assembly,
        state_boundary_ratios,
        three_part_split,
    )
    from render_bank_phrase import resample_frames  # noqa: E402

    here = Path(__file__).resolve().parent
    out = args.out or (here.parent / "fixtures" / "bank")
    out.mkdir(parents=True, exist_ok=True)

    rng = np.random.default_rng(20261024)
    payload: dict[str, list] = {"resample": [], "split": [], "fades": [], "ratios": [], "plans": []}

    # --- resample: compression, expansion, exact, single frame ---
    for source_frames, target in [(13, 7), (7, 13), (10, 10), (1, 5), (31, 30), (30, 31), (2, 9), (40, 3)]:
        values = rng.normal(0.0, 1.0, size=(source_frames, 3))
        expected = resample_frames(values, target)
        payload["resample"].append({
            "source": values.tolist(),
            "target": target,
            "expected": np.asarray(expected).tolist(),
        })

    # --- three-part split across the interesting ratios ---
    for total, head, tail in [(40, 0.2, 0.8), (17, 0.2, 0.8), (5, 0.2, 0.8), (33, 0.35, 0.9), (100, 0.19, 0.81)]:
        h, m, t = three_part_split(total, head, tail)
        payload["split"].append({"total": total, "head": head, "tail": tail, "expected": [h, m, t]})

    # --- fade weights, narrowed to float ---
    for length in (1, 2, 5, 20, 33):
        payload["fades"].append({
            "length": length,
            "out": fade_out_weights(length).tolist(),
            "in": fade_in_weights(length).tolist(),
        })

    # --- state boundary ratios with the clamps ---
    for states in ([1, 1, 1, 1, 1], [2, 3, 1, 1, 1], [10, 1, 1, 1, 1], [1, 1, 1, 1, 10], [3, 3, 3, 3, 3]):
        head, tail = state_boundary_ratios(np.asarray(states, dtype=np.int64))
        payload["ratios"].append({"states": states, "head": head, "tail": tail})

    # --- planning: rest, consonant with and without left context, vowel ---
    plan_cases = [
        ("rest", dict(slot_class=-1, duration=20, previous_duration=0, next_duration=0,
                      next_slot_class=-1, source_suffix=0, state=NoteAssemblyState())),
        ("consonant-new", dict(slot_class=0, duration=6, previous_duration=0, next_duration=30,
                               next_slot_class=6, source_suffix=12, state=NoteAssemblyState())),
        ("consonant-after-rest", dict(slot_class=0, duration=6, previous_duration=40, next_duration=30,
                                      next_slot_class=6, source_suffix=12,
                                      state=NoteAssemblyState(previous_was_rest=True))),
        ("consonant-after-note", dict(slot_class=0, duration=6, previous_duration=40, next_duration=30,
                                      next_slot_class=6, source_suffix=3,
                                      state=NoteAssemblyState(previous_was_rest=False))),
        ("vowel", dict(slot_class=6, duration=24, previous_duration=6, next_duration=40,
                       next_slot_class=0, source_suffix=0,
                       state=NoteAssemblyState(has_previous_consonant=True,
                                               previous_consonant_right_extension=5))),
        ("vowel-last", dict(slot_class=6, duration=24, previous_duration=6, next_duration=40,
                            next_slot_class=-1, source_suffix=0,
                            state=NoteAssemblyState(has_previous_consonant=True,
                                                    previous_consonant_right_extension=5))),
    ]
    for name, kwargs in plan_cases:
        plan = plan_note_assembly(**kwargs)
        payload["plans"].append({
            "name": name,
            "slot_class": kwargs["slot_class"],
            "duration": kwargs["duration"],
            "previous_duration": kwargs["previous_duration"],
            "next_duration": kwargs["next_duration"],
            "next_slot_class": kwargs["next_slot_class"],
            "source_suffix": kwargs["source_suffix"],
            "state": {
                "previous_was_rest": kwargs["state"].previous_was_rest,
                "pending_vowel_overlap": kwargs["state"].pending_vowel_overlap,
                "previous_consonant_right_extension": kwargs["state"].previous_consonant_right_extension,
                "has_previous_consonant": kwargs["state"].has_previous_consonant,
            },
            "expected": {
                "branch": plan.branch,
                "target_core": plan.target_core,
                "left_extension": plan.left_extension,
                "right_extension": plan.right_extension,
                "head_overlap": plan.head_overlap,
                "previous_overlap": plan.previous_overlap,
                "pending_vowel_overlap": plan.pending_vowel_overlap,
            },
        })

    (out / "bank.json").write_text(json.dumps(payload) + "\n", encoding="utf-8")
    print(f"resample {len(payload['resample'])}  split {len(payload['split'])}  "
          f"fades {len(payload['fades'])}  ratios {len(payload['ratios'])}  "
          f"plans {len(payload['plans'])}")
    print(f"wrote {out / 'bank.json'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
