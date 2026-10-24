"""Turn an OpenUtau `.ustx` project into a script the TuneLab CLI can sing.

The engine cannot read `.ustx`; TuneLab has its own project format. This bridges the two so a
real community project can be pushed through the plugin without re-authoring it by hand.

Usage:

    python tools/ustx-to-script.py song.ustx > song.js
    TuneLab.Cli.exe --headless sandbox run --code (Get-Content song.js -Raw)

Positions stay musical: USTX ticks are rescaled to the host's 480 ppq instead of being turned
into seconds, so a project authored at another tempo keeps its rhythm and simply plays at the
tempo of the host project. Notes with no lyric, or with a rest lyric, are skipped.

The payload is deliberately quote-free. The script reaches the CLI as a command-line argument,
and PowerShell mangles double quotes inside such an argument, so notes travel as
`pos,dur,pitch,hex` separated by `~` and are rebuilt with String.fromCharCode.
"""

from __future__ import annotations

import sys
from pathlib import Path

import yaml

HOST_PPQ = 480


def note_rows(track: dict, ppq: int) -> list[tuple[int, int, int, str]]:
    scale = HOST_PPQ / float(ppq or HOST_PPQ)
    rows: list[tuple[int, int, int, str]] = []
    for note in track.get("notes") or []:
        lyric = str(note.get("lyric", "") or "").strip()
        if not lyric or lyric.lower() in {"r", "-"}:
            continue
        duration = float(note.get("duration", 0))
        if duration <= 0:
            continue
        # One code point per note: the engine takes a syllable, not a phrase.
        rows.append(
            (
                int(round(float(note.get("position", 0)) * scale)),
                int(round(duration * scale)),
                int(note.get("tone", 60)),
                "%04X" % ord(lyric[0]),
            )
        )
    rows.sort(key=lambda row: row[0])
    return rows


def build_script(path: Path, voice: str) -> str:
    ustx = yaml.safe_load(path.read_text(encoding="utf-8-sig"))
    if not isinstance(ustx, dict):
        raise SystemExit(f"{path}: not a USTX project")

    ppq = int(ustx.get("ppq", 480) or 480)
    tracks = [
        track
        for track in (ustx.get("tracks") or [])
        if isinstance(track, dict) and (track.get("notes") or [])
    ]
    if not tracks:
        raise SystemExit(f"{path}: no track with notes")

    lines = [
        f"// generated from {path.name} (ppq {ppq}, {len(tracks)} vocal track(s))",
        "const voices = sandbox.voices();",
        f"const VOICE = voices.find(v => v.id === '{voice}') || voices[1] || voices[0];",
        "const p = tl.currentProject();",
        "let sung = 0;",
    ]

    for index, track in enumerate(tracks):
        rows = note_rows(track, ppq)
        if not rows:
            continue
        span = max(row[0] + row[1] for row in rows)
        name = str(track.get("trackName", f"track{index}")).replace("'", " ")
        spec = "~".join(f"{p},{d},{tone},{code}" for p, d, tone, code in rows)
        lines += [
            f"// {name}: {len(rows)} notes, {span} ticks",
            f"const track{index} = p.addTrack();",
            f"const part{index} = track{index}.addPart({{ pos: 0, endOffset: {span + 1920} }});",
            f"part{index}.setSoundSource({{ kind: 'voice', type: VOICE.type, id: VOICE.id }});",
            f"const spec{index} = '{spec}';",
            f"for (const row of spec{index}.split('~')) {{",
            "  const f = row.split(',');",
            f"  part{index}.addNote({{ pos: +f[0], dur: +f[1], pitch: +f[2],"
            " lyric: String.fromCharCode(parseInt(f[3], 16)) });",
            "}",
            f"print('{name}: ' + JSON.stringify(sandbox.synthesize(part{index}, "
            "{ timeoutMs: 1800000, maxDispatches: 900 })));",
            "sung += 1;",
        ]

    lines.append("print('tracks sung: ' + sung);")
    return "\n".join(lines) + "\n"


def main() -> None:
    args = list(sys.argv[1:])
    voice = "yanxi"
    if "--voice" in args:
        at = args.index("--voice")
        voice = args[at + 1]
        del args[at:at + 2]
    if not args:
        raise SystemExit("usage: ustx-to-script.py <project.ustx> [--voice yanxi|sinsy-jp]")
    sys.stdout.write(build_script(Path(args[0]), voice))


if __name__ == "__main__":
    main()
