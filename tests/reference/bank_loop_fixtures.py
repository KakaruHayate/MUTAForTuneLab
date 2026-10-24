"""End-to-end fixtures for the bank assembly: block building and buffer writing.

The arithmetic pieces are covered by bank_fixtures.py; this one runs the two compound
operations on a synthetic bank so a disagreement in how they compose shows up too.

Run:
    python tests/reference/bank_loop_fixtures.py --tools <dir holding the reference modules>
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np

MGC_WIDTH = 4
BAP_WIDTH = 3


def context(rng, frames: int):
    mgc = rng.normal(0.0, 0.5, size=(frames, MGC_WIDTH))
    bap = rng.normal(-20.0, 3.0, size=(frames, BAP_WIDTH))
    return mgc, bap


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tools", type=Path, required=True)
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args()

    sys.path.insert(0, str(args.tools))
    from mcore_note_assembly import (  # noqa: E402
        build_note_block,
        fill_rest_mgc,
        write_note_block,
        NoteAssemblyPlan,
        NoteAssemblyState,
    )
    from mcore_note_assembly import plan_note_assembly  # noqa: E402

    here = Path(__file__).resolve().parent
    out = args.out or (here.parent / "fixtures" / "bank")
    out.mkdir(parents=True, exist_ok=True)
    rng = np.random.default_rng(77)

    payload: dict[str, list] = {"blocks": [], "loops": []}

    # --- block building: plain, with states, collapsed core, extensions ---
    block_cases = [
        ("plain", dict(source_prefix=3, source_core=7, source_suffix=2, target_core=9,
                       left_extension=2, right_extension=1, states=None)),
        ("with-states", dict(source_prefix=3, source_core=12, source_suffix=2, target_core=18,
                             left_extension=3, right_extension=2,
                             states=np.array([2, 3, 1, 1, 1], dtype=np.int64))),
        ("collapsed", dict(source_prefix=3, source_core=12, source_suffix=2, target_core=5,
                           left_extension=1, right_extension=0,
                           states=np.array([2, 3, 1, 1, 1], dtype=np.int64))),
        ("no-extensions", dict(source_prefix=0, source_core=6, source_suffix=0, target_core=10,
                               left_extension=0, right_extension=0,
                               states=np.array([1, 1, 1, 1, 1], dtype=np.int64))),
    ]
    for name, kwargs in block_cases:
        frames = kwargs["source_prefix"] + kwargs["source_core"] + kwargs["source_suffix"]
        mgc, bap = context(rng, frames)
        block = build_note_block(mgc, bap, **kwargs)
        payload["blocks"].append({
            "name": name,
            "mgc": mgc.tolist(),
            "bap": bap.tolist(),
            "source_prefix": kwargs["source_prefix"],
            "source_core": kwargs["source_core"],
            "source_suffix": kwargs["source_suffix"],
            "target_core": kwargs["target_core"],
            "left_extension": kwargs["left_extension"],
            "right_extension": kwargs["right_extension"],
            "states": None if kwargs["states"] is None else [int(v) for v in kwargs["states"]],
            "expected_mgc": block.mgc.tolist(),
            "expected_bap": block.bap.tolist(),
        })

    # --- a short assembly loop: consonant (left ext) -> vowel (overlap) -> rest -> consonant ---
    total = 48
    base_mgc, base_bap = context(rng, total)
    mgc_global = base_mgc.copy()
    bap_global = base_bap.copy()

    steps = []
    state = NoteAssemblyState()
    specs = [
        # (slot class, duration, previous duration, next duration, next slot class, source suffix, start)
        (0, 4, 20, 10, 6, 3, 12),
        (6, 8, 4, 10, 0, 0, 16),
        (-1, 6, 0, 0, -1, 0, 26),
        (0, 3, 20, 8, 6, 2, 32),
        (6, 9, 3, 8, -1, 0, 35),
    ]
    for slot_class, duration, prev_dur, next_dur, next_class, suffix, start in specs:
        plan = plan_note_assembly(
            slot_class=slot_class, duration=duration, previous_duration=prev_dur,
            next_duration=next_dur, next_slot_class=next_class, source_suffix=suffix,
            state=state,
        )
        state = plan.next_state

        if plan.branch == "rest":
            fill_rest_mgc(mgc_global, start, duration)
            steps.append({
                "branch": "rest", "start": start, "previous_overlap": plan.previous_overlap,
                "mgc": [], "bap": [], "left_extension": 0,
                "rest_duration": duration,
            })
            continue

        source_frames = 1 + plan.target_core + max(1, plan.right_extension) + 2
        source_prefix = 1
        source_core = plan.target_core
        source_suffix = source_frames - source_prefix - source_core
        block_mgc, block_bap = context(rng, source_frames)
        block = build_note_block(
            block_mgc, block_bap,
            source_prefix=source_prefix, source_core=source_core, source_suffix=source_suffix,
            target_core=plan.target_core,
            left_extension=plan.left_extension, right_extension=plan.right_extension,
            states=np.array([2, 2, 2, 1, 1], dtype=np.int64),
        )
        write_note_block(mgc_global, bap_global, block, start_frame=start, plan=plan)
        steps.append({
            "branch": plan.branch, "start": start, "previous_overlap": plan.previous_overlap,
            "mgc": block.mgc.tolist(), "bap": block.bap.tolist(),
            "left_extension": block.left_extension,
            "rest_duration": 0,
        })

    payload["loops"].append({
        "name": "mixed",
        "initial_mgc": base_mgc.tolist(),
        "initial_bap": base_bap.tolist(),
        "steps": steps,
        "expected_mgc": mgc_global.tolist(),
        "expected_bap": bap_global.tolist(),
    })

    (out / "bank_loop.json").write_text(json.dumps(payload) + "\n", encoding="utf-8")
    print(f"blocks {len(payload['blocks'])}  loops {len(payload['loops'])}  "
          f"steps {len(payload['loops'][0]['steps'])}")
    print(f"wrote {out / 'bank_loop.json'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
