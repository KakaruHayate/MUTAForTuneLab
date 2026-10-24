"""Vertical check of the bank driver: the Python reference assembles a span from a
synthetic item table, and the C# port must produce the same buffers.

The item table is synthetic (random parameters, fixed seed) so nothing from a MUTA bank
ends up in the repository; what is compared is the assembly arithmetic and the slot
walk, not the fidelity of any particular voice.

Run:
    python tests/reference/bank_drive_fixtures.py --tools <MUTA tools>
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np

MGC_ORDER = 50
BAP_ORDER = 26
TOTAL = 160

PLOSIVES = {"b", "d", "g", "p", "t", "k"}
FRICATIVES = {"c", "ch", "f", "h", "j", "q", "s", "sh", "x", "z", "zh"}
NASALS = {"m", "n", "l", "r", "w", "y", "v"}
LONG_FINALS = {"ang", "eng", "iang", "uang", "iong", "ong"}

# (start, frames, midi, phone)  -- a rest, a two-note phrase, a long vowel
NOTES = [
    (0, 40, 0, "sil", True),
    (40, 60, 60, "chen", False),
    (100, 60, 64, "a", False),
    (160 - 40, 40, 0, "sil", True),
]


def target_frames(phone: str) -> int:
    if phone in PLOSIVES:
        return 10
    if phone in FRICATIVES:
        return 18
    if phone in NASALS:
        return 16
    if phone in LONG_FINALS:
        return 62
    return 54


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tools", type=Path, required=True)
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args()

    sys.path.insert(0, str(args.tools))
    from mcore_context import right_class, choose_first_item, choose_positive_item  # noqa: E402
    from mcore_note_assembly import (  # noqa: E402
        NoteAssemblyState,
        build_note_block,
        fill_rest_mgc,
        plan_note_assembly,
        write_note_block,
    )
    from muta_inf import InfItem  # noqa: E402
    from mcore_label_builder import split_syllable  # noqa: E402

    here = Path(__file__).resolve().parent
    out = args.out or (here.parent / "fixtures" / "bank")
    out.mkdir(parents=True, exist_ok=True)

    rng = np.random.default_rng(4242)

    # A small item table: two contexts per phone, with distinct geometry so the
    # selection has something to choose between.
    phones = {"ch", "en", "a", "ga", "sil"}
    table: dict[str, list[dict]] = {}
    for phone in phones:
        entries = []
        for variant in range(2):
            frames = 6 + 4 * variant
            prefix = 1 + variant
            core = max(1, frames - prefix - 2)
            suffix = frames - prefix - core
            values = [0] * 11
            values[0] = 2 + variant           # left class
            values[1] = 1 + variant           # right class
            values[2] = frames
            values[3] = prefix
            values[4] = core
            values[5] = suffix
            values[6] = 40 + variant          # pitch code
            values[7] = 40
            values[8] = 40
            values[9] = 0
            values[10] = 0
            mgc = rng.normal(-5.0, 0.4, size=(frames, MGC_ORDER))
            bap = rng.normal(-20.0, 3.0, size=(frames, BAP_ORDER))
            entries.append({
                "item": InfItem(current=phone, left="sil", right="sil", values=tuple(values)),
                "mgc": mgc,
                "bap": bap,
            })
        table[phone] = entries

    def streams(item):
        for entry in table[item.current]:
            if entry["item"] is item:
                return entry["mgc"], entry["bap"]
        raise KeyError(item.current)

    groups = {phone: [e["item"] for e in entries] for phone, entries in table.items()}

    mgc = np.zeros((TOTAL, MGC_ORDER))
    bap = np.zeros((TOTAL, BAP_ORDER))
    silence = np.ones(TOTAL, dtype=bool)

    state = NoteAssemblyState()
    previous_phone = "sil"
    trace = []

    for index, (start, frames, midi, phone, rest) in enumerate(NOTES):
        if rest:
            fill_rest_mgc(mgc, start, frames)
            previous_phone = "sil"
            state = NoteAssemblyState(previous_was_rest=True)
            continue

        split = split_syllable(phone)
        initial, final = (split if split else (None, phone))

        consonant_frames = min(target_frames(initial), frames) if initial else 0
        vowel_frames = max(10, frames - consonant_frames)

        next_cons = 0
        next_cons_phone = None
        if index + 1 < len(NOTES):
            n_start, n_frames, _, n_phone, n_rest = NOTES[index + 1]
            if not n_rest:
                n_split = split_syllable(n_phone)
                if n_split:
                    next_cons = min(target_frames(n_split[0]), n_frames)
                    next_cons_phone = n_split[0]
        right_context = next_cons_phone or "sil"

        cursor = start
        previous_duration = 0
        pitch = 440.0 * 2.0 ** ((midi - 69) / 12.0)

        if initial:
            item = choose_first_item(groups[initial], previous_phone, final, pitch)
            plan = plan_note_assembly(
                slot_class=0, duration=consonant_frames, previous_duration=previous_duration,
                next_duration=vowel_frames, next_slot_class=1,
                source_suffix=item.suffix_frames, state=state,
                left_context_nonzero=previous_phone != "sil")
            im, ib = streams(item)
            block = build_note_block(im, ib, source_prefix=item.prefix_frames,
                                     source_core=item.core_frames, source_suffix=item.suffix_frames,
                                     target_core=plan.target_core,
                                     left_extension=plan.left_extension,
                                     right_extension=plan.right_extension)
            write_note_block(mgc, bap, block, start_frame=cursor, plan=plan)
            silence[cursor:cursor + consonant_frames] = False
            trace.append({"slot": "consonant", "phone": initial, "start": cursor,
                          "left_ext": block.left_extension, "core": block.core_frames,
                          "right_ext": block.right_extension})
            cursor += consonant_frames
            previous_duration = consonant_frames
            state = plan.next_state
            previous_phone = initial

        vowel_class = max(1, right_class(final))
        vowel_plan = plan_note_assembly(
            slot_class=vowel_class, duration=vowel_frames, previous_duration=previous_duration,
            next_duration=next_cons, next_slot_class=0 if next_cons else -1,
            source_suffix=0, state=state)
        vowel_item = choose_positive_item(groups[final], previous_phone, right_context,
                                          vowel_plan.target_core, pitch)
        if vowel_item is None:
            raise SystemExit(f"no vowel item for {final!r}")
        vm, vb = streams(vowel_item)
        vblock = build_note_block(vm, vb, source_prefix=vowel_item.prefix_frames,
                                  source_core=vowel_item.core_frames,
                                  source_suffix=vowel_item.suffix_frames,
                                  target_core=vowel_plan.target_core,
                                  left_extension=vowel_plan.left_extension,
                                  right_extension=vowel_plan.right_extension)
        write_note_block(mgc, bap, vblock, start_frame=cursor, plan=vowel_plan)
        silence[cursor:start + frames] = False
        trace.append({"slot": "vowel", "phone": final, "start": cursor,
                      "left_ext": vblock.left_extension, "core": vblock.core_frames,
                      "right_ext": vblock.right_extension})
        state = vowel_plan.next_state
        previous_phone = final

    # Flatten the item table for the C# side.
    items = []
    for phone, entries in table.items():
        for entry in entries:
            items.append({
                "current": phone,
                "left": entry["item"].left,
                "right": entry["item"].right,
                "values": list(entry["item"].values),
                "mgc": entry["mgc"].tolist(),
                "bap": entry["bap"].tolist(),
            })

    payload = {
        "total": TOTAL, "mgc_order": MGC_ORDER, "bap_order": BAP_ORDER,
        "notes": [{"start": s, "frames": f, "midi": m, "phone": p, "rest": r}
                  for s, f, m, p, r in NOTES],
        "items": items,
        "trace": trace,
        "expected_mgc": mgc.tolist(),
        "expected_bap": bap.tolist(),
        "expected_silence": [bool(v) for v in silence],
    }
    (out / "bank_drive.json").write_text(json.dumps(payload) + "\n", encoding="utf-8")
    print(f"notes {len(NOTES)}  items {len(items)}  slots {len(trace)}")
    print(f"wrote {out / 'bank_drive.json'}")
    return 0


def _right_class(phone: str) -> int:
    if phone in {"sil", "sp"}:
        return 0
    if phone.startswith("a"):
        return 1
    if phone.startswith(("ou", "ong")):
        return 8
    if phone.startswith("o"):
        return 2
    if phone.startswith("ei"):
        return 5
    if phone.startswith("e"):
        return 3
    if phone.startswith("if"):
        return 9
    if phone.startswith("ib"):
        return 10
    if phone.startswith("i"):
        return 4
    if phone.startswith("u"):
        return 6
    if phone.startswith("v"):
        return 7
    return -1


if __name__ == "__main__":
    raise SystemExit(main())
