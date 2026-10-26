"""Reference digest of a MUTA .inf, for comparing the C# parser against the Python one.

The Python parser is the reference implementation used while porting, so it is not
vendored here: pass its directory with --tools.

Usage:
    python tests/reference/inf_digest.py --tools <path to MUTA/tools> <file.inf>

Prints the same three lines the C# probe prints; tools/verify-inf.ps1 diffs them.
"""

from __future__ import annotations

import argparse
import hashlib
import sys
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("inf", type=Path)
    parser.add_argument("--tools", type=Path, required=True,
                        help="directory holding muta_inf.py")
    args = parser.parse_args()

    sys.path.insert(0, str(args.tools))
    from muta_inf import parse_inf  # noqa: E402

    info = parse_inf(args.inf)

    lines: list[str] = []
    lines.append(
        f"v{info.version} n{info.item_count} mgc{info.mgc_order} bap{info.bap_order} "
        f"final{info.final_count} groups{info.group_count}"
    )
    for group in info.groups:
        lines.append(f"g {group.name} {group.declared_count} {group.item_count}")
    for group in info.groups:
        for item in group.items:
            values = " ".join(str(v) for v in item.values)
            lines.append(f"i {item.current} {item.left} {item.right} {values}")
    for count, start, size in info.final:
        lines.append(f"f {count} {start} {size}")

    text = "\n".join(lines) + "\n"
    digest = hashlib.sha256(text.encode("utf-8")).hexdigest().upper()

    print(f"version={info.version} items={info.item_count} mgc={info.mgc_order} "
          f"bap={info.bap_order} final={info.final_count} groups={info.group_count}")
    print(f"parsed_items={sum(g.item_count for g in info.groups)}")
    print(f"sha256={digest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
