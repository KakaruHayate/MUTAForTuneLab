"""Generate the label fixtures used to verify the C# port.

Each case is a note-level score (pinyin syllables + frame timings + pitches) plus the
labels the reference implementation produces for it. Both files are written into
tests/fixtures/labels/, so the C# side can be checked without Python.

Run:
    python tests/reference/label_fixtures.py --tools <dir holding mcore_label_builder.py>
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

# (name, tempo, notes) with notes as (phone, start frame, duration frames, pitch)
CASES = [
    ("single", 120, [
        ("sil", 0, 150, 0),
        ("ni", 150, 40, 72),
        ("sil", 190, 100, 0),
    ]),
    ("phrase", 96, [
        ("sil", 0, 120, 0),
        ("wo", 120, 40, 69),
        ("ai", 160, 50, 67),
        ("ni", 210, 60, 72),
        ("sil", 270, 150, 0),
    ]),
    ("jump", 120, [
        ("sil", 0, 100, 0),
        ("ma", 100, 60, 60),
        ("sil", 160, 40, 0),
        ("mi", 200, 60, 84),
        ("sil", 260, 100, 0),
    ]),
    ("longrest", 120, [
        ("sil", 0, 300, 0),
        ("le", 300, 50, 67),
        ("sil", 350, 400, 0),
    ]),
    ("dash", 120, [
        ("sil", 0, 80, 0),
        ("a", 80, 120, 69),
        ("-", 200, 60, 69),
        ("o", 260, 80, 67),
        ("sil", 340, 100, 0),
    ]),
    ("affricate", 120, [
        ("sil", 0, 90, 0),
        ("zhi", 90, 35, 64),
        ("chi", 125, 35, 66),
        ("shi", 160, 35, 68),
        ("ri", 195, 35, 65),
        ("sil", 230, 120, 0),
    ]),
    # No leading break: the first slot is a consonant, so every "index < 1" arm fires.
    ("nolead", 120, [
        ("ni", 0, 60, 72),
        ("sil", 60, 100, 0),
    ]),
    # Two voiced notes touching: a vowel is immediately followed by the next consonant.
    ("legato", 120, [
        ("sil", 0, 80, 0),
        ("ni", 80, 60, 72),
        ("hao", 140, 70, 67),
        ("sil", 210, 100, 0),
    ]),
    # A rest of exactly 200 frames must NOT split; 201 must.
    ("restedge", 120, [
        ("sil", 0, 200, 0),
        ("a", 200, 50, 69),
        ("sil", 250, 201, 0),
        ("o", 451, 50, 67),
        ("sil", 501, 200, 0),
    ]),
]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tools", type=Path, required=True)
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args()

    sys.path.insert(0, str(args.tools))
    from mcore_label_builder import NoteScore, build_labels  # noqa: E402

    here = Path(__file__).resolve().parent
    out = args.out or (here.parent / "fixtures" / "labels")
    out.mkdir(parents=True, exist_ok=True)

    for name, tempo, notes in CASES:
        score = NoteScore(
            phones=[n[0] for n in notes],
            starts=[n[1] for n in notes],
            durations=[n[2] for n in notes],
            pitches=[n[3] for n in notes],
            tempo=tempo,
        )
        labels = build_labels(score)

        payload = {
            "name": name,
            "tempo": tempo,
            "notes": [
                {"phone": phone, "start": start, "frames": frames, "pitch": pitch}
                for phone, start, frames, pitch in notes
            ],
        }
        (out / f"{name}.json").write_text(
            json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        (out / f"{name}.labels").write_text("\n".join(labels) + "\n", encoding="utf-8")
        print(f"{name:10s} {len(notes)} notes -> {len(labels)} labels")

    print(f"wrote fixtures to {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
