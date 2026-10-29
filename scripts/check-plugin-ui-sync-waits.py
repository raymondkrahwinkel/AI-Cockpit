#!/usr/bin/env python3
"""Reject sync-over-async in a plugin's UI part (AC-1891).

A UI control that blocks on `.GetAwaiter().GetResult()` holds the UI thread for the whole backend call, and
deadlocks as soon as that call needs the UI thread back. The plugin channel never answers inline, so every
such call waits on another thread. Await it instead.

    scripts/check-plugin-ui-sync-waits.py
"""

from __future__ import annotations

import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
FORBIDDEN = re.compile(r"\.\s*GetAwaiter\s*\(\s*\)\s*\.\s*GetResult\s*\(")


def ui_sources(root: Path) -> list[Path]:
    # A plugin's UI part is its Ui/ (or UI/) folder; the folder name's case differs between plugins.
    def is_ui(parts: tuple[str, ...]) -> bool:
        return len(parts) > 2 and not parts[0].endswith(".Tests") and parts[1].lower() == "ui"

    return sorted(source for source in (root / "plugins-dev").rglob("*.cs") if is_ui(source.relative_to(root / "plugins-dev").parts))


def violations_in(source: Path, text: str, root: Path) -> list[str]:
    found = []
    for line_number, line in enumerate(text.splitlines(), 1):
        code = line.lstrip()
        if code.startswith("//"):
            continue
        if FORBIDDEN.search(code):
            found.append(f"{source.relative_to(root)}:{line_number}: {code.strip()}")
    return found


def main(root: Path = ROOT) -> int:
    violations: list[str] = []
    for source in ui_sources(root):
        violations.extend(violations_in(source, source.read_text(encoding="utf-8"), root))

    if violations:
        print("A plugin's UI part awaits its channel; it never blocks on it with GetAwaiter().GetResult():", file=sys.stderr)
        print("\n".join(violations), file=sys.stderr)
        return 1

    print("Plugin UI sync-wait guard passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
